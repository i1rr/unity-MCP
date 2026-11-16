using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace I1rr.UnityMcp.Editor
{
    [InitializeOnLoad]
    internal static class McpModuleManager
    {
        internal static string Root => Path.GetDirectoryName(Application.dataPath);
        private static (long, long) _policyStamp = (long.MinValue, 0);
        private static HashSet<string> _disabled = new(StringComparer.OrdinalIgnoreCase);
        private static string _policyError;
        private static bool _availabilityDirty;
        private static double _availabilityAfter;
        private static readonly Dictionary<string, ((long, long) stamp, JObject value)> Manifests = new();

        static McpModuleManager()
        {
            McpToolRegistry.RefreshModulePolicy = RefreshPolicy;
            McpToolRegistry.ModuleEnabled = id => _policyError == null && !_disabled.Contains(id);
            ModuleStatusTools.StatusProvider = Snapshot;
            EditorApplication.projectChanged += () => { _availabilityDirty = true; _availabilityAfter = EditorApplication.timeSinceStartup + 2; };
            EditorApplication.update += () =>
            {
                if (!_availabilityDirty || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.timeSinceStartup < _availabilityAfter) return;
                _availabilityDirty = false;
                McpToolRegistry.RecheckModuleAvailability();
            };
            RefreshPolicy();
        }
        internal static void EnsureInstalled() { }

        private static void RefreshPolicy()
        {
            try
            {
                var file = new FileInfo(McpProjectModules.PolicyPath(Root));
                var stamp = file.Exists ? (file.Length, file.LastWriteTimeUtc.Ticks) : (-1L, 0L);
                if (stamp == _policyStamp) return;
                var policy = McpProjectModules.ReadPolicy(Root);
                _disabled = new HashSet<string>(((JArray)policy["disabled"]).Values<string>(), StringComparer.OrdinalIgnoreCase);
                _policyStamp = stamp; _policyError = null;
                McpToolRegistry.NotifyCatalogChanged();
            }
            catch (Exception ex)
            {
                if (_policyError != ex.Message) McpToolRegistry.NotifyCatalogChanged();
                _policyError = ex.Message; // Fail closed for module tools; core recovery tools remain available.
            }
        }

        internal static void SetEnabled(string id, bool enabled)
        {
            var known = ((JArray)Snapshot()["modules"]).Children<JObject>().Any(m => string.Equals((string)m["name"], id, StringComparison.OrdinalIgnoreCase));
            if (!known) throw new ArgumentException("Unknown module. Inspect get_module_status first.");
            McpProjectModules.SetEnabled(Root, id, enabled);
            _policyStamp = (long.MinValue, 0);
            RefreshPolicy();
        }

        internal static JObject Create(string id, string description)
        {
            if (McpToolRegistry.Modules.Any(m => string.Equals(m.ModuleName, id, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("A loaded module already uses that name.");
            var result = McpProjectModules.Create(Root, id, description);
            QueueRefresh();
            return result;
        }
        internal static JObject Import(string path, string hash, bool external = false)
        {
            var result = McpProjectModules.Import(Root, path, hash, external);
            _policyStamp = (long.MinValue, 0); RefreshPolicy(); QueueRefresh();
            return result;
        }
        private static void QueueRefresh() => EditorApplication.delayCall += () => AssetDatabase.Refresh();

        internal static JObject Snapshot()
        {
            RefreshPolicy();
            var loaded = McpToolRegistry.Modules.GroupBy(m => m.ModuleName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
            var ids = new HashSet<string>(McpProjectModules.Builtins, StringComparer.OrdinalIgnoreCase);
            foreach (string id in loaded.Keys) ids.Add(id);
            var sources = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            var errors = new JArray();
            string directory = Path.Combine(Root, McpProjectModules.SourceRoot);
            if (Directory.Exists(directory) && !McpPathValidator.ContainsSymlink(directory))
            {
                foreach (string folder in Directory.EnumerateDirectories(directory).Take(65))
                {
                    if (sources.Count >= 64) { errors.Add("Showing at most 64 project modules."); break; }
                    try
                    {
                        if (McpPathValidator.ContainsSymlink(folder)) throw new IOException("Linked module folder was not read.");
                        var file = new FileInfo(Path.Combine(folder, "module.json"));
                        var stamp = (file.Length, file.LastWriteTimeUtc.Ticks);
                        if (!Manifests.TryGetValue(folder, out var cached) || cached.stamp != stamp)
                            Manifests[folder] = cached = (stamp, McpProjectModules.Manifest(folder));
                        string id = (string)cached.value["id"];
                        if (sources.ContainsKey(id)) throw new IOException("Duplicate module ID: " + id);
                        ids.Add(id); sources[id] = cached.value;
                    }
                    catch (Exception ex) { errors.Add(Path.GetFileName(folder) + ": " + ex.Message); }
                }
            }
            var modules = new JArray();
            foreach (string id in ids.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                loaded.TryGetValue(id, out var implementations);
                bool available = implementations?.Any(McpToolRegistry.IsModuleAvailable) ?? false;
                bool requested = !_disabled.Contains(id);
                bool enabled = available && requested && _policyError == null;
                bool project = sources.TryGetValue(id, out var manifest);
                string status = _policyError != null ? "settings_error" : !requested ? "disabled" :
                    available ? "enabled" : project && implementations == null ? "awaiting_reload" : implementations == null ? "not_installed" : "unavailable";
                modules.Add(new JObject
                {
                    ["name"] = id, ["status"] = status, ["available"] = available,
                    ["requested_enabled"] = requested, ["enabled"] = enabled, ["project_module"] = project,
                    ["assembly"] = project ? manifest["assembly"] : implementations?.FirstOrDefault()?.GetType().Assembly.GetName().Name,
                    ["description"] = project ? manifest["description"] : id == "Addressables" ? "Addressables tools; requires the Unity package and Addressable settings." :
                        "Discovered MCP integration.",
                    ["tool_count"] = McpToolRegistry.ModuleToolCount(id), ["active_tool_count"] = McpToolRegistry.ModuleToolCount(id, true),
                    ["path"] = project ? Path.Combine(Root, McpProjectModules.SourceRoot, id) : null
                });
            }
            return new JObject { ["module_count"] = modules.Count, ["modules"] = modules, ["policy_error"] = _policyError, ["source_errors"] = errors };
        }

        internal static void ImportDialog()
        {
            string path = EditorUtility.OpenFilePanel("Import MCP module source", Root, "zip");
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var preview = McpProjectModules.InspectArchive(Root, path, true);
                if (((JArray)preview["missing_dependencies"]).Count > 0)
                    throw new IOException("Install dependencies first: " + string.Join(", ", ((JArray)preview["missing_dependencies"]).Values<string>()));
                if (EditorUtility.DisplayDialog("Import MCP module", $"Import {preview["id"]} ({((JArray)preview["files"]).Count} source files)?\n\nUnity compiles imported C# even when its MCP tools are disabled. Only import source you trust. Existing modules are not overwritten.", "Import", "Cancel"))
                    Import(path, (string)preview["sha256"], true);
            }
            catch (Exception ex) { EditorUtility.DisplayDialog("Module import", ex.Message, "OK"); }
        }
        internal static void ExportDialog(string id)
        {
            string path = EditorUtility.SaveFilePanel("Export MCP module source", Root, id + ".zip", "zip");
            if (string.IsNullOrEmpty(path)) return;
            try { McpProjectModules.Export(Root, id, path, true); }
            catch (Exception ex) { EditorUtility.DisplayDialog("Module export", ex.Message, "OK"); }
        }
    }
}
