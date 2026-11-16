using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace I1rr.UnityMcp.Editor
{
    internal static class McpProjectModules
    {
        internal const string SourceRoot = "Assets/McpModules";
        internal const int MaxFiles = 128, MaxFileBytes = 256 * 1024, MaxTotalBytes = 4 * 1024 * 1024;
        internal static readonly string[] Builtins = { "Addressables" };
        private static readonly Regex IdPattern = new(@"^[A-Za-z][A-Za-z0-9_]{0,47}$");
        private static readonly UTF8Encoding Utf8 = new(false, true);

        internal static string Folder(string root, string id)
        {
            if (!IdPattern.IsMatch(id ?? "")) throw new ArgumentException("Use a module ID starting with a letter, followed by letters, numbers or underscores (up to 48 characters).");
            string path = Path.GetFullPath(Path.Combine(root, SourceRoot, id));
            if (McpPathValidator.ContainsSymlink(path)) throw new IOException("The module source path contains a link.");
            return path;
        }

        internal static JObject Create(string root, string id, string description)
        {
            if ((description?.Length ?? 0) > 1024) throw new ArgumentException("Keep the module description under 1024 characters.");
            string folder = Folder(root, id);
            if (Builtins.Contains(id, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("That ID belongs to a built-in integration.");
            if (Directory.Exists(folder) || File.Exists(folder)) throw new IOException("That module source already exists; it was not overwritten.");
            string assembly = "Project.Mcp." + id + ".Editor";
            if (UnityCompatibility.GetLoadedAssemblies().Any(a => a.GetName().Name == assembly)) throw new IOException("That module assembly is already loaded.");
            var manifest = new JObject { ["format_version"] = 1, ["id"] = id, ["description"] = description ?? "", ["assembly"] = assembly };
            string code = "using I1rr.UnityMcp.Shared;\nusing Newtonsoft.Json.Linq;\nusing UnityEditor;\nusing UnityEngine;\n\n" +
                "namespace Project.Mcp.M_" + id + "\n{\n" +
                "    public sealed class Module : IMcpModule\n    {\n        public string ModuleName => \"" + id + "\";\n" +
                "        public bool IsAvailable => true;\n        public void Initialize() { }\n        public void Shutdown() { }\n    }\n\n" +
                "    [McpToolCategory(\"" + id + "\")]\n    public static class Tools\n    {\n" +
                "        [McpTool(\"" + id.ToLowerInvariant() + "_module_info\", \"Inspect the " + id + " module and current Unity project.\")]\n" +
                "        public static JObject Info(JObject args) => new JObject\n        {\n" +
                "            [\"module\"] = \"" + id + "\",\n            [\"project\"] = System.IO.Path.GetDirectoryName(Application.dataPath),\n" +
                "            [\"unity_version\"] = Application.unityVersion,\n            [\"is_playing\"] = EditorApplication.isPlaying\n        };\n    }\n}\n";
            var asmdef = new JObject
            {
                ["name"] = assembly, ["references"] = new JArray("I1rr.UnityMcp.Shared"),
                ["includePlatforms"] = new JArray("Editor"), ["autoReferenced"] = false,
                ["overrideReferences"] = true, ["precompiledReferences"] = new JArray("Newtonsoft.Json.dll")
            };
            var files = new Dictionary<string, byte[]>
            {
                ["module.json"] = Utf8.GetBytes(manifest + "\n"),
                ["Editor/" + id + ".Editor.asmdef"] = Utf8.GetBytes(asmdef + "\n"),
                ["Editor/" + id + "Module.cs"] = Utf8.GetBytes(code),
                ["README.md"] = Utf8.GetBytes("# " + id + " MCP module\n\nProject-owned Editor tools. Edit the C# source and add named assembly references for\nyour framework or game. Keep tool names prefixed with `" + id.ToLowerInvariant() + "_`.\nUse IMcpModule.IsAvailable for lightweight dependency checks; cache expensive probes.\nSee the installed package's Documentation~/ToolAuthoringGuide.md.\n\nEnable/disable controls MCP exposure, not C# compilation or static initializers.\nExport this module from the Dashboard to reuse its source and metadata elsewhere.\n")
            };
            foreach (string name in files.Keys.ToArray()) files[name + ".meta"] = Meta(false);
            files["Editor.meta"] = Meta(true);
            Install(root, folder, files);
            return new JObject { ["id"] = id, ["path"] = folder, ["status"] = "source_created", ["compilation_complete"] = false };
        }

        internal static JObject Manifest(string folder)
        {
            string path = Path.Combine(folder, "module.json");
            if (new FileInfo(path).Length > 16384 || McpPathValidator.ContainsSymlink(path)) throw new IOException("Module manifest is too large or linked.");
            var manifest = JObject.Parse(File.ReadAllText(path));
            string id = (string)manifest["id"];
            if ((int?)manifest["format_version"] != 1 || !IdPattern.IsMatch(id ?? "") || string.IsNullOrWhiteSpace((string)manifest["assembly"]) || Path.GetFileName(folder) != id)
                throw new IOException("Invalid module manifest.");
            return manifest;
        }

        internal static JObject InspectArchive(string root, string path, bool external = false)
        {
            string archive = ArchivePath(root, path, external);
            var files = ReadArchive(archive, out string hash);
            var manifest = Validate(files);
            var missing = MissingDependencies(files);
            var source = new JObject();
            int remaining = 24000;
            foreach (var file in files.Where(f => f.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            {
                string text = Utf8.GetString(file.Value);
                if (text.Length > remaining) break;
                source[file.Key] = text; remaining -= text.Length;
            }
            return new JObject
            {
                ["id"] = manifest["id"], ["assembly"] = manifest["assembly"], ["description"] = manifest["description"],
                ["sha256"] = hash, ["files"] = new JArray(files.Keys.OrderBy(x => x)),
                ["missing_dependencies"] = new JArray(missing), ["source_preview"] = source,
                ["source_preview_truncated"] = source.Count < files.Count(f => f.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)),
                ["notice"] = "Imports Editor C# source. Disabled tools are not a code sandbox; review source before importing."
            };
        }

        internal static JObject Import(string root, string path, string expectedHash, bool external = false)
        {
            string archive = ArchivePath(root, path, external);
            var files = ReadArchive(archive, out string hash);
            if (string.IsNullOrWhiteSpace(expectedHash) || !string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Archive hash changed or was not supplied. Inspect the archive before importing.");
            var manifest = Validate(files);
            string id = (string)manifest["id"];
            string folder = Folder(root, id);
            if (Builtins.Contains(id, StringComparer.OrdinalIgnoreCase) || Directory.Exists(folder) || File.Exists(folder))
                throw new IOException("That module already exists or has a reserved ID; nothing was overwritten.");
            if (UnityCompatibility.GetLoadedAssemblies().Any(a => a.GetName().Name == (string)manifest["assembly"]))
                throw new IOException("That module assembly is already loaded. Finish its reload before importing.");
            var missing = MissingDependencies(files);
            if (missing.Length > 0) throw new IOException("Install these module dependencies first: " + string.Join(", ", missing));
            var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var meta in files.Where(f => f.Key.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)))
            {
                var match = Regex.Match(Utf8.GetString(meta.Value), @"(?m)^guid:\s*([a-fA-F0-9]{32})\s*$");
                if (!match.Success || !guids.Add(match.Groups[1].Value) || !string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(match.Groups[1].Value)))
                    throw new IOException("Module metadata has an invalid or already installed asset GUID: " + meta.Key);
            }
            SetEnabled(root, id, false);
            Install(root, folder, files);
            return new JObject { ["id"] = id, ["path"] = folder, ["status"] = "source_imported", ["enabled"] = false, ["compilation_complete"] = false };
        }

        internal static JObject Export(string root, string id, string path, bool external = false)
        {
            string folder = Folder(root, id);
            Manifest(folder);
            var files = new Dictionary<string, byte[]>();
            foreach (string file in SourceFiles(folder))
            {
                if (McpPathValidator.ContainsSymlink(file)) throw new IOException("Module exports cannot contain linked files.");
                string name = file.Substring(folder.Length + 1).Replace('\\', '/');
                CheckName(name);
                if (new FileInfo(file).Length > MaxFileBytes) throw new IOException("Module source file is too large: " + name);
                files.Add(name, File.ReadAllBytes(file));
                CheckLimits(files);
            }
            Validate(files);
            path ??= "UserSettings/i1rr-unity-mcp/exports/" + id + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".zip";
            string destination = ArchivePath(root, path, external);
            if (destination.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Export outside the module source directory.");
            if (File.Exists(destination)) throw new IOException("The export file already exists. Choose a new filename.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            using (var output = File.Open(destination, FileMode.CreateNew))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
                foreach (var file in files.OrderBy(f => f.Key))
                    using (var stream = zip.CreateEntry(file.Key).Open()) stream.Write(file.Value, 0, file.Value.Length);
            return new JObject { ["id"] = id, ["path"] = destination, ["sha256"] = Hash(destination), ["file_count"] = files.Count };
        }

        internal static string PolicyPath(string root) => Path.Combine(root, "ProjectSettings", "i1rr-unity-mcp-modules.json");
        internal static JObject ReadPolicy(string root)
        {
            string path = PolicyPath(root);
            if (!File.Exists(path)) return new JObject { ["version"] = 1, ["disabled"] = new JArray() };
            if (new FileInfo(path).Length > 65536 || McpPathValidator.ContainsSymlink(path)) throw new IOException("Module settings are too large or linked.");
            var policy = JObject.Parse(File.ReadAllText(path));
            if ((int?)policy["version"] != 1 || policy["disabled"] is not JArray disabled || disabled.Count > 128 ||
                disabled.Any(t => t.Type != JTokenType.String || ((string)t).Length > 128)) throw new IOException("Invalid module settings; preserve and repair the settings file.");
            return policy;
        }
        internal static void SetEnabled(string root, string id, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 128) throw new ArgumentException("Module name is required.");
            var policy = ReadPolicy(root);
            var disabled = new HashSet<string>(((JArray)policy["disabled"]).Values<string>(), StringComparer.OrdinalIgnoreCase);
            if (enabled) disabled.Remove(id); else disabled.Add(id);
            if (disabled.Count > 128) throw new IOException("Too many disabled module settings.");
            policy["disabled"] = new JArray(disabled.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            string path = PolicyPath(root);
            if (McpPathValidator.ContainsSymlink(path)) throw new IOException("Module settings path contains a link.");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, policy + "\n", Utf8);
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static string ArchivePath(string root, string path, bool external)
        {
            if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("A .zip archive path is required.");
            string resolved = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
            if (McpPathValidator.ContainsSymlink(resolved)) throw new IOException("Module archive path contains a link.");
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!external && !resolved.StartsWith(prefix, Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new IOException("MCP archive paths must stay inside the current project. Dashboard file pickers can select external files.");
            return resolved;
        }
        private static Dictionary<string, byte[]> ReadArchive(string path, out string hash)
        {
            if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new IOException("Module archive exceeds 8 MiB.");
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            using var input = File.OpenRead(path);
            var snapshot = new byte[8 * 1024 * 1024 + 1];
            int size = 0, chunk;
            while (size < snapshot.Length && (chunk = input.Read(snapshot, size, snapshot.Length - size)) > 0) size += chunk;
            if (size > 8 * 1024 * 1024) throw new IOException("Module archive grew beyond 8 MiB.");
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(snapshot, 0, size)).Replace("-", "").ToLowerInvariant();
            using var captured = new MemoryStream(snapshot, 0, size, false);
            using var zip = new ZipArchive(captured, ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxFiles) throw new IOException("Too many archive entries.");
            foreach (var entry in zip.Entries)
            {
                string name = entry.FullName.Replace('\\', '/');
                if (name.EndsWith("/", StringComparison.Ordinal)) { CheckSegments(name.TrimEnd('/')); continue; }
                CheckName(name);
                if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || entry.Length > MaxFileBytes || files.ContainsKey(name))
                    throw new IOException("Invalid, duplicate, linked or oversized module entry: " + name);
                using var stream = entry.Open();
                var buffer = new byte[MaxFileBytes + 1];
                int length = 0, read;
                while (length < buffer.Length && (read = stream.Read(buffer, length, buffer.Length - length)) > 0) length += read;
                if (length > MaxFileBytes) throw new IOException("Oversized module entry.");
                var data = new byte[length]; Array.Copy(buffer, data, length);
                Utf8.GetString(data); // Source archives contain text, not binaries.
                files.Add(name, data); CheckLimits(files);
            }
            return files;
        }
        private static JObject Validate(Dictionary<string, byte[]> files)
        {
            CheckLimits(files);
            if (!files.TryGetValue("module.json", out var data) || data.Length > 16384) throw new IOException("Module manifest is missing or too large.");
            var manifest = JObject.Parse(Utf8.GetString(data).TrimStart('\ufeff'));
            string id = (string)manifest["id"];
            if ((int?)manifest["format_version"] != 1 || !IdPattern.IsMatch(id ?? "") ||
                (string)manifest["assembly"] != "Project.Mcp." + id + ".Editor") throw new IOException("Invalid module manifest or assembly identity.");
            string asmPath = "Editor/" + id + ".Editor.asmdef";
            if (!files.TryGetValue(asmPath, out var asm) || files.Keys.Count(p => p.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase)) != 1)
                throw new IOException("A module must have one Editor assembly definition.");
            var definition = JObject.Parse(Utf8.GetString(asm).TrimStart('\ufeff'));
            if ((string)definition["name"] != (string)manifest["assembly"] || definition["includePlatforms"] is not JArray platforms ||
                platforms.Count != 1 || (string)platforms[0] != "Editor") throw new IOException("Module assembly must be Editor-only and match its manifest.");
            return manifest;
        }
        private static string[] MissingDependencies(Dictionary<string, byte[]> files)
        {
            var asm = JObject.Parse(Utf8.GetString(files.First(f => f.Key.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase)).Value).TrimStart('\ufeff'));
            var loaded = new HashSet<string>(UnityCompatibility.GetLoadedAssemblies().Select(a => a.GetName().Name), StringComparer.Ordinal);
            var missing = new List<string>();
            foreach (string reference in (asm["references"] as JArray ?? new JArray()).Values<string>())
            {
                string name = reference;
                if (reference.StartsWith("GUID:", StringComparison.Ordinal))
                {
                    string path = AssetDatabase.GUIDToAssetPath(reference.Substring(5));
                    name = File.Exists(path) ? (string)JObject.Parse(File.ReadAllText(path))["name"] : reference;
                }
                if (!loaded.Contains(name)) missing.Add(reference);
            }
            foreach (string reference in (asm["precompiledReferences"] as JArray ?? new JArray()).Values<string>())
                if (!loaded.Contains(Path.GetFileNameWithoutExtension(reference))) missing.Add(reference);
            return missing.ToArray();
        }
        private static void CheckName(string name)
        {
            CheckSegments(name);
            string extension = Path.GetExtension(name).ToLowerInvariant();
            if (extension == ".cs" && !name.StartsWith("Editor/", StringComparison.Ordinal)) throw new IOException("Module C# source must stay under Editor/.");
            if (name != "module.json" && extension != ".cs" && extension != ".asmdef" && extension != ".md" && extension != ".meta")
                throw new IOException("Portable modules contain C# source, one assembly definition, metadata and Markdown documentation only: " + name);
        }
        private static void CheckSegments(string name)
        {
            if (name.Length > 180 || name.Contains(":") || name.StartsWith("/", StringComparison.Ordinal) ||
                name.Split('/').Any(p => p.Length == 0 || p.StartsWith(".", StringComparison.Ordinal) || p.EndsWith(".", StringComparison.Ordinal) || p.EndsWith(" ", StringComparison.Ordinal)))
                throw new IOException("Unsafe archive path: " + name);
        }
        private static void CheckLimits(Dictionary<string, byte[]> files)
        { if (files.Count > MaxFiles || files.Values.Sum(b => (long)b.Length) > MaxTotalBytes) throw new IOException("Module exceeds the source archive limits."); }
        private static byte[] Meta(bool folder) => Utf8.GetBytes("fileFormatVersion: 2\nguid: " + Guid.NewGuid().ToString("N") + (folder ? "\nfolderAsset: yes\n" : "\n"));
        private static void Install(string root, string folder, Dictionary<string, byte[]> files)
        {
            if (Directory.Exists(folder) || File.Exists(folder)) throw new IOException("Module source already exists.");
            string staging = Path.Combine(root, "UserSettings", "i1rr-unity-mcp", "staging", Guid.NewGuid().ToString("N"));
            if (McpPathValidator.ContainsSymlink(staging)) throw new IOException("Module staging path contains a link.");
            try
            {
                Directory.CreateDirectory(staging);
                foreach (var file in files)
                {
                    CheckName(file.Key);
                    string destination = Path.Combine(staging, file.Key.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.WriteAllBytes(destination, file.Value);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(folder));
                Directory.Move(staging, folder);
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        }
        private static IEnumerable<string> SourceFiles(string root)
        {
            var pending = new Stack<string>(); pending.Push(root);
            int entries = 0;
            while (pending.Count > 0)
                foreach (string path in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    if (++entries > MaxFiles * 2) throw new IOException("Too many source entries.");
                    if (McpPathValidator.ContainsSymlink(path)) throw new IOException("Module source contains a linked path.");
                    if (Directory.Exists(path)) pending.Push(path); else yield return path;
                }
        }
        private static string Hash(string path)
        {
            if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new IOException("Module archive exceeds 8 MiB.");
            using var sha = SHA256.Create(); using var stream = File.OpenRead(path);
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }
}
