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
    [McpToolCategory("Utility")]
    public static class FileTools
    {
        private const int MaxFileReadBytes = 1_048_576; // 1 MB
        private const int MaxDirectoryEntries = 1000;

        #region read_file

        [McpTool("read_file",
            "Read file contents by project-relative path. Supports optional line range (1-based). Truncates files larger than 1 MB.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""path"": { ""type"": ""string"", ""description"": ""Project-relative file path (e.g. 'Assets/Scripts/MyScript.cs')"" },
                    ""start_line"": { ""type"": ""integer"", ""description"": ""Start line (1-based, inclusive). Omit to read from beginning."" },
                    ""end_line"": { ""type"": ""integer"", ""description"": ""End line (1-based, inclusive). Omit to read to end."" }
                },
                ""required"": [""path""]
            }")]
        public static JObject ReadFile(JObject parameters)
        {
            var inputPath = parameters["path"]?.Value<string>();
            var startLine = parameters["start_line"]?.Value<int?>();
            var endLine = parameters["end_line"]?.Value<int?>();

            var validation = McpPathValidator.ValidatePath(inputPath);
            if (!validation.IsValid)
                return Error(validation.Error);

            var resolvedPath = validation.ResolvedPath;

            if (!File.Exists(resolvedPath))
                return Error($"File not found: {inputPath}");

            var fileInfo = new FileInfo(resolvedPath);
            bool truncated = fileInfo.Length > MaxFileReadBytes;

            string content;
            try
            {
                if (truncated)
                {
                    using var reader = new StreamReader(resolvedPath);
                    var buffer = new char[MaxFileReadBytes];
                    int charsRead = reader.Read(buffer, 0, buffer.Length);
                    content = new string(buffer, 0, charsRead);
                }
                else
                {
                    content = File.ReadAllText(resolvedPath);
                }
            }
            catch (UnauthorizedAccessException)
            {
                return Error("Permission denied reading file.");
            }
            catch (IOException ex)
            {
                return Error($"IO error: {ex.Message}");
            }

            var lines = content.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            int totalLines = lines.Length;

            int start = startLine.HasValue ? Math.Max(1, startLine.Value) : 1;
            int end = endLine.HasValue ? Math.Min(totalLines, endLine.Value) : totalLines;

            if (start > totalLines)
            {
                return new JObject
                {
                    ["path"] = inputPath,
                    ["content"] = "",
                    ["line_count"] = 0,
                    ["start_line"] = start,
                    ["end_line"] = start - 1,
                    ["total_lines"] = totalLines
                };
            }

            if (end < start)
                end = start;

            var selectedLines = lines.Skip(start - 1).Take(end - start + 1);
            var resultContent = string.Join("\n", selectedLines);

            var result = new JObject
            {
                ["path"] = inputPath,
                ["content"] = resultContent,
                ["line_count"] = end - start + 1,
                ["start_line"] = start,
                ["end_line"] = end,
                ["total_lines"] = totalLines
            };

            if (truncated)
                result["__truncated"] = true;

            return result;
        }

        #endregion

        #region write_file

        [McpTool("write_file",
            "Write content to a file (project-relative path, sandboxed). Auto-creates parent directories. Auto-imports if inside Assets/.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""path"": { ""type"": ""string"", ""description"": ""Project-relative file path (e.g. 'Assets/Scripts/NewScript.cs')"" },
                    ""content"": { ""type"": ""string"", ""description"": ""File content to write"" }
                },
                ""required"": [""path"", ""content""]
            }")]
        public static JObject WriteFile(JObject parameters)
        {
            var inputPath = parameters["path"]?.Value<string>();
            var content = parameters["content"]?.Value<string>() ?? "";

            var validation = McpPathValidator.ValidatePath(inputPath);
            if (!validation.IsValid)
                return Error(validation.Error);

            var resolvedPath = validation.ResolvedPath;

            // Auto-create parent directories
            var directory = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                try
                {
                    Directory.CreateDirectory(directory);
                }
                catch (Exception ex)
                {
                    return Error($"Failed to create directory: {ex.Message}");
                }
            }

            int bytesWritten;
            try
            {
                File.WriteAllText(resolvedPath, content);
                bytesWritten = new FileInfo(resolvedPath).Length > int.MaxValue
                    ? int.MaxValue
                    : (int)new FileInfo(resolvedPath).Length;
            }
            catch (UnauthorizedAccessException)
            {
                return McpError.Create(McpErrorCodes.ReadOnly, "File is read-only. Use p4_checkout first.");
            }
            catch (IOException ex)
            {
                return Error($"IO error: {ex.Message}");
            }

            // Auto-import if inside Assets/
            bool imported = false;
            if (inputPath != null && IsAssetsPath(inputPath))
            {
                var assetPath = NormalizeToForwardSlash(inputPath);
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                imported = true;
            }

            return new JObject
            {
                ["path"] = inputPath,
                ["bytes_written"] = bytesWritten,
                ["imported"] = imported
            };
        }

        #endregion

        #region list_directory

        [McpTool("list_directory",
            "List folder contents by project-relative path. Supports recursive listing and glob filtering. Capped at 1000 entries.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""path"": { ""type"": ""string"", ""description"": ""Project-relative directory path (e.g. 'Assets/Scripts')"" },
                    ""recursive"": { ""type"": ""boolean"", ""description"": ""List subdirectories recursively (default false)"", ""default"": false },
                    ""filter"": { ""type"": ""string"", ""description"": ""Glob pattern filter (e.g. '*.cs')"" }
                },
                ""required"": [""path""]
            }")]
        public static JObject ListDirectory(JObject parameters)
        {
            var inputPath = parameters["path"]?.Value<string>();
            var recursive = parameters["recursive"]?.Value<bool>() ?? false;
            var filter = parameters["filter"]?.Value<string>();

            var validation = McpPathValidator.ValidatePath(inputPath);
            if (!validation.IsValid)
                return Error(validation.Error);

            var resolvedPath = validation.ResolvedPath;

            if (!Directory.Exists(resolvedPath))
                return Error($"Directory not found: {inputPath}");

            var clock = System.Diagnostics.Stopwatch.StartNew();
            bool Expired() => clock.ElapsedMilliseconds >= 5000;
            var searchPattern = string.IsNullOrEmpty(filter) ? "*" : filter;
            if (searchPattern.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || searchPattern == "..") return Error("Invalid filename pattern.");

            var entries = new List<JObject>();
            int totalCount = 0;

            try
            {
                foreach (var entry in FileTree.Walk(resolvedPath, recursive, Expired, searchPattern))
                {
                    if (entry is DirectoryInfo)
                    {
                        var dir = entry.FullName;
                        totalCount++;
                        if (entries.Count < MaxDirectoryEntries)
                        {
                            entries.Add(new JObject
                            {
                                ["name"] = Path.GetFileName(dir),
                                ["path"] = GetProjectRelativePath(dir),
                                ["type"] = "directory",
                                ["size"] = 0
                            });
                        }
                        continue;
                    }
                    var file = entry.FullName;
                    totalCount++;
                    if (entries.Count < MaxDirectoryEntries)
                    {
                        long size = 0;
                        try { size = new FileInfo(file).Length; } catch { }

                        entries.Add(new JObject
                        {
                            ["name"] = Path.GetFileName(file),
                            ["path"] = GetProjectRelativePath(file),
                            ["type"] = "file",
                            ["size"] = size
                        });
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                return Error("Permission denied accessing directory.");
            }
            catch (IOException ex)
            {
                return Error($"IO error: {ex.Message}");
            }

            var result = new JObject
            {
                ["path"] = inputPath,
                ["entries"] = new JArray(entries),
                ["count"] = entries.Count
            };

            if (Expired()) result["__truncated"] = true;
            else if (totalCount > MaxDirectoryEntries)
            {
                result["__truncated"] = true;
                result["__total_count"] = totalCount;
            }

            return result;
        }

        #endregion

        #region file_exists

        [McpTool("file_exists",
            "Check if a file or directory exists at the given project-relative path.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""path"": { ""type"": ""string"", ""description"": ""Project-relative path to check"" }
                },
                ""required"": [""path""]
            }")]
        public static JObject FileExists(JObject parameters)
        {
            var inputPath = parameters["path"]?.Value<string>();

            var validation = McpPathValidator.ValidatePath(inputPath);
            if (!validation.IsValid)
                return Error(validation.Error);

            var resolvedPath = validation.ResolvedPath;
            bool exists = File.Exists(resolvedPath) || Directory.Exists(resolvedPath);

            return new JObject
            {
                ["exists"] = exists,
                ["path"] = inputPath
            };
        }

        #endregion

        #region get_file_info

        [McpTool("get_file_info",
            "Get metadata for a file: size, last modified, created time, read-only status, extension.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""path"": { ""type"": ""string"", ""description"": ""Project-relative file path"" }
                },
                ""required"": [""path""]
            }")]
        public static JObject GetFileInfo(JObject parameters)
        {
            var inputPath = parameters["path"]?.Value<string>();

            var validation = McpPathValidator.ValidatePath(inputPath);
            if (!validation.IsValid)
                return Error(validation.Error);

            var resolvedPath = validation.ResolvedPath;

            if (!File.Exists(resolvedPath))
                return Error($"File not found: {inputPath}");

            FileInfo info;
            try
            {
                info = new FileInfo(resolvedPath);
            }
            catch (Exception ex)
            {
                return Error($"Cannot read file info: {ex.Message}");
            }

            return new JObject
            {
                ["path"] = inputPath,
                ["size"] = info.Length,
                ["last_modified"] = info.LastWriteTimeUtc.ToString("o"),
                ["created"] = info.CreationTimeUtc.ToString("o"),
                ["is_readonly"] = info.IsReadOnly,
                ["extension"] = info.Extension
            };
        }

        #endregion

        #region Helpers

        private static JObject Error(string message) =>
            McpError.Create(McpErrorCodes.InternalError, message);

        private static bool IsAssetsPath(string path)
        {
            var normalized = path.Replace('\\', '/').TrimStart('/');
            return normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                   || normalized.Equals("Assets", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeToForwardSlash(string path) =>
            path.Replace('\\', '/');

        private static string GetProjectRelativePath(string absolutePath)
        {
            var root = McpPathValidator.ProjectRoot;
            if (root == null)
                return absolutePath;

            if (absolutePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return absolutePath.Substring(root.Length).Replace('\\', '/');

            return absolutePath.Replace('\\', '/');
        }

        #endregion
    }
}
