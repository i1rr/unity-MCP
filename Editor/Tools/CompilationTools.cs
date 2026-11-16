using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;

namespace I1rr.UnityMcp.Editor
{
    [McpToolCategory("Editor")]
    public static class CompilationTools
    {
        private static readonly Regex CompilerDiagnostic = new Regex(
            @"\b(error|warning)\s+[A-Za-z][A-Za-z0-9]*\d+\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        #region get_compilation_errors

        [McpTool("get_compilation_errors",
            "Snapshot compiler and analyzer diagnostics from the latest 500 console entries, falling back to cached compilation events. Coverage metadata describes this bounded snapshot; request and verify a fresh compile for clean-compiler proof.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {}
            }")]
        public static JObject GetCompilationErrors(JObject parameters)
        {
            return BuildCompilationDiagnostics();
        }

        [McpTool("refresh_and_get_compilation_errors",
            "Request an asset refresh and return a diagnostics snapshot. This does not wait for compilation or domain reload; poll editor state and verify loaded code.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""request_id"": { ""type"": ""string"", ""description"": ""Optional refresh UUID for safe retry and status lookup."" },
                    ""forceSynchronousImport"": {
                        ""type"": ""boolean"",
                        ""description"": ""If true, use ForceSynchronousImport to reduce race conditions in automation loops. Default true.""
                    },
                    ""settleMs"": {
                        ""type"": ""integer"",
                        ""minimum"": 0,
                        ""maximum"": 5000,
                        ""description"": ""Deprecated and ignored: sleeping on the Unity main thread cannot settle compilation. Always returns immediately after refresh.""
                    }
                }
            }")]
        public static JObject RefreshAndGetCompilationErrors(JObject parameters)
        {
            var forceSynchronousImport = parameters["forceSynchronousImport"]?.Value<bool?>() ?? true;

            var options = forceSynchronousImport
                ? ImportAssetOptions.ForceSynchronousImport
                : ImportAssetOptions.Default;

            var request = RefreshRequests.Request(parameters, options);
            if (request["error"] != null || request["error_code"] != null) return request;

            // Compilation/reload needs subsequent Editor ticks. Never sleep here.
            var diagnostics = BuildCompilationDiagnostics();
            diagnostics.Merge(request);
            diagnostics["force_synchronous_import"] = forceSynchronousImport;
            diagnostics["settle_ms"] = 0;
            return diagnostics;
        }

        private static JObject BuildCompilationDiagnostics()
        {
            var messages = new List<JObject>();
            int consoleCount = 0, consoleScanned = 0;
            bool consoleAvailable = false;
            string source = "console";

            // Primary source: EditorUtility-based compilation messages via LogEntries reflection
            try
            {
                var logEntriesType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.LogEntries");
                if (logEntriesType != null)
                {
                    var getCount = logEntriesType.GetMethod("GetCount", BindingFlags.Static | BindingFlags.Public);
                    var startGetting = logEntriesType.GetMethod("StartGettingEntries", BindingFlags.Static | BindingFlags.Public);
                    var endGetting = logEntriesType.GetMethod("EndGettingEntries", BindingFlags.Static | BindingFlags.Public);
                    var getEntry = logEntriesType.GetMethod("GetEntryInternal", BindingFlags.Static | BindingFlags.Public);

                    if (getCount != null && startGetting != null && endGetting != null)
                    {
                        int count = (int)getCount.Invoke(null, null);
                        consoleCount = count;
                        startGetting.Invoke(null, null);
                        try
                        {
                            var logEntryType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.LogEntry");
                            // Recent diagnostics must not disappear behind older gameplay logs.
                            for (int i = Math.Max(0, count - 500); i < count; i++)
                            {
                                if (logEntryType != null && getEntry != null)
                                {
                                    var entry = Activator.CreateInstance(logEntryType);
                                    getEntry.Invoke(null, new object[] { i, entry });
                                    consoleScanned++;
                                    consoleAvailable = true;

                                    var mode = (int)(logEntryType.GetField("mode", BindingFlags.Public | BindingFlags.Instance)?.GetValue(entry) ?? 0);
                                    var message = (string)(logEntryType.GetField("message", BindingFlags.Public | BindingFlags.Instance)?.GetValue(entry) ?? "");
                                    var file = (string)(logEntryType.GetField("file", BindingFlags.Public | BindingFlags.Instance)?.GetValue(entry) ?? "");
                                    var line = (int)(logEntryType.GetField("line", BindingFlags.Public | BindingFlags.Instance)?.GetValue(entry) ?? 0);

                                    // Analyzer IDs are not restricted to Roslyn's CS prefix (for example UAC0005).
                                    var diagnostic = CompilerDiagnostic.Match(message);
                                    bool isCompilerError = diagnostic.Success && string.Equals(diagnostic.Groups[1].Value, "error", StringComparison.OrdinalIgnoreCase);
                                    bool isCompilerWarning = diagnostic.Success && !isCompilerError;

                                    if (isCompilerError || isCompilerWarning)
                                    {
                                        messages.Add(new JObject
                                        {
                                            ["type"] = isCompilerError ? "error" : "warning",
                                            ["message"] = message.Trim(),
                                            ["file"] = file,
                                            ["line"] = line
                                        });
                                    }
                                }
                            }
                            if (count == 0 && logEntryType != null && getEntry != null) consoleAvailable = true;
                        }
                        finally
                        {
                            endGetting.Invoke(null, null);
                        }
                    }
                }
            }
            catch
            {
                // Fallback: reflection failed, silently return empty
            }

            // If reflection approach yielded nothing, try the CompilationPipeline events cache
            if (messages.Count == 0)
            {
                source = "compilation_event_cache";
                // Check if there are cached compilation messages from our listener
                var cached = CompilationMessageCache.GetMessages();
                foreach (var msg in cached)
                {
                    messages.Add(msg);
                }
            }

            int errorCount = messages.Count(m => m["type"]?.Value<string>() == "error");
            int warningCount = messages.Count(m => m["type"]?.Value<string>() == "warning");

            return new JObject
            {
                ["errors"] = errorCount,
                ["warnings"] = warningCount,
                ["messages"] = new JArray(messages),
                ["is_compiling"] = EditorApplication.isCompiling,
                ["is_updating"] = EditorApplication.isUpdating,
                ["snapshot_only"] = true,
                ["source"] = source,
                ["console_snapshot_available"] = consoleAvailable,
                ["console_entries_available"] = consoleCount,
                ["console_entries_scanned"] = consoleScanned,
                ["console_scan_truncated"] = consoleCount > consoleScanned,
                ["editor_state"] = EditorControlTools.GetEditorState(new JObject())
            };
        }

        #endregion

        #region get_asmdef_info

        [McpTool("get_asmdef_info",
            "Get Assembly Definition (.asmdef) information: references, platforms, defines, and settings.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""path"": { ""type"": ""string"", ""description"": ""Project-relative path to the .asmdef file."" },
                    ""name"": { ""type"": ""string"", ""description"": ""Assembly definition name (will search for it if path not provided)."" }
                }
            }")]
        public static JObject GetAsmdefInfo(JObject parameters)
        {
            var inputPath = parameters["path"]?.Value<string>();
            var name = parameters["name"]?.Value<string>();

            if (string.IsNullOrWhiteSpace(inputPath) && string.IsNullOrWhiteSpace(name))
                return Error("Either 'path' or 'name' must be provided.");

            string resolvedPath = null;

            if (!string.IsNullOrWhiteSpace(inputPath))
            {
                var validation = McpPathValidator.ValidatePath(inputPath);
                if (!validation.IsValid)
                    return Error(validation.Error);
                resolvedPath = validation.ResolvedPath;
            }
            else
            {
                // Search for asmdef by name
                var guids = AssetDatabase.FindAssets($"t:asmdef {name}");
                foreach (var guid in guids)
                {
                    var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    var asmdefContent = File.ReadAllText(assetPath);
                    try
                    {
                        var parsed = JObject.Parse(asmdefContent);
                        if (string.Equals(parsed["name"]?.Value<string>(), name, StringComparison.OrdinalIgnoreCase))
                        {
                            resolvedPath = Path.GetFullPath(assetPath);
                            break;
                        }
                    }
                    catch
                    {
                        // Skip malformed files
                    }
                }

                if (resolvedPath == null)
                    return Error($"Assembly definition '{name}' not found.");
            }

            if (!File.Exists(resolvedPath))
                return Error($"File not found: {inputPath ?? name}");

            string content;
            try
            {
                content = File.ReadAllText(resolvedPath);
            }
            catch (Exception ex)
            {
                return Error($"Failed to read file: {ex.Message}");
            }

            JObject asmdef;
            try
            {
                asmdef = JObject.Parse(content);
            }
            catch (Exception ex)
            {
                return Error($"Failed to parse asmdef JSON: {ex.Message}");
            }

            var result = new JObject
            {
                ["name"] = asmdef["name"]?.Value<string>(),
                ["path"] = GetProjectRelativePath(resolvedPath),
                ["rootNamespace"] = asmdef["rootNamespace"]?.Value<string>() ?? "",
                ["references"] = asmdef["references"] ?? new JArray(),
                ["includePlatforms"] = asmdef["includePlatforms"] ?? new JArray(),
                ["excludePlatforms"] = asmdef["excludePlatforms"] ?? new JArray(),
                ["defineConstraints"] = asmdef["defineConstraints"] ?? new JArray(),
                ["versionDefines"] = asmdef["versionDefines"] ?? new JArray(),
                ["allowUnsafeCode"] = asmdef["allowUnsafeCode"]?.Value<bool>() ?? false,
                ["autoReferenced"] = asmdef["autoReferenced"]?.Value<bool>() ?? true,
                ["overrideReferences"] = asmdef["overrideReferences"]?.Value<bool>() ?? false,
                ["precompiledReferences"] = asmdef["precompiledReferences"] ?? new JArray(),
                ["noEngineReferences"] = asmdef["noEngineReferences"]?.Value<bool>() ?? false
            };

            return result;
        }

        #endregion

        #region Helpers

        private static string GetProjectRelativePath(string absolutePath)
        {
            var root = McpPathValidator.ProjectRoot;
            if (absolutePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return absolutePath.Substring(root.Length).Replace('\\', '/');
            return absolutePath.Replace('\\', '/');
        }

        private static JObject Error(string message)
        {
            return McpError.Create(McpErrorCodes.InternalError, message);
        }

        #endregion

        #region CompilationMessageCache

        /// <summary>
        /// Caches compilation messages from CompilationPipeline events.
        /// </summary>
        private static class CompilationMessageCache
        {
            private static readonly object CacheLock = new();
            private static readonly List<JObject> Messages = new();

            [InitializeOnLoadMethod]
            private static void Initialize()
            {
                CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompilationFinished;
                CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
                CompilationPipeline.compilationStarted -= OnCompilationStarted;
                CompilationPipeline.compilationStarted += OnCompilationStarted;
            }

            private static void OnCompilationStarted(object context)
            {
                lock (CacheLock)
                {
                    Messages.Clear();
                }
            }

            private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
            {
                lock (CacheLock)
                {
                    foreach (var msg in messages)
                    {
                        if (msg.type == CompilerMessageType.Error || msg.type == CompilerMessageType.Warning)
                        {
                            Messages.Add(new JObject
                            {
                                ["type"] = msg.type == CompilerMessageType.Error ? "error" : "warning",
                                ["message"] = msg.message,
                                ["file"] = msg.file,
                                ["line"] = msg.line,
                                ["column"] = msg.column,
                                ["assembly"] = Path.GetFileNameWithoutExtension(assemblyPath)
                            });
                        }
                    }
                }
            }

            public static List<JObject> GetMessages()
            {
                lock (CacheLock)
                {
                    return new List<JObject>(Messages);
                }
            }
        }

        #endregion
    }
}
