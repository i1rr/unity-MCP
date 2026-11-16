using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;

namespace I1rr.UnityMcp.Editor
{
    [McpToolCategory("Utility")]
    public static class CodeSearchTools
    {
        private const int DefaultMaxResults = 200;
        private const int DefaultTotalTimeoutMs = 30000;
        private const int PerFileRegexTimeoutSeconds = 5;

        private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".shader", ".hlsl", ".cginc", ".json", ".xml", ".txt", ".md",
            ".asmdef", ".yml", ".yaml", ".uxml", ".uss", ".prefab", ".unity", ".asset"
        };

        private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tga", ".psd", ".tif", ".tiff",
            ".fbx", ".obj", ".blend", ".dae", ".3ds", ".max",
            ".wav", ".mp3", ".ogg", ".aiff", ".flac",
            ".mesh", ".dll", ".so", ".dylib", ".exe", ".pdb", ".mdb",
            ".zip", ".rar", ".7z", ".tar", ".gz",
            ".ttf", ".otf", ".woff",
            ".mp4", ".avi", ".mov", ".webm",
            ".exr", ".hdr", ".cubemap"
        };

        #region search_in_files

        [McpTool("search_in_files",
            "Search (grep) across project source files. Supports plain text or regex. Scopes: Assets/ + Packages/ by default. Skips binary files. Per-file regex timeout: 5s. Total timeout configurable.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""query"": { ""type"": ""string"", ""description"": ""Search query (plain text or regex pattern)."" },
                    ""path_pattern"": { ""type"": ""string"", ""description"": ""Optional glob-like filter for file paths (e.g. '*.cs', 'Assets/Scripts/**')."" },
                    ""regex"": { ""type"": ""boolean"", ""description"": ""Treat query as regex pattern (default false)."", ""default"": false },
                    ""max_results"": { ""type"": ""integer"", ""description"": ""Maximum matches to return (default 200)."", ""default"": 200 },
                    ""total_timeout_ms"": { ""type"": ""integer"", ""description"": ""Total timeout in milliseconds for entire scan (default 30000)."", ""default"": 30000 }
                },
                ""required"": [""query""]
            }")]
        public static JObject SearchInFiles(JObject parameters)
        {
            var query = parameters["query"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(query))
                return Error("Parameter 'query' is required.");

            var pathPattern = parameters["path_pattern"]?.Value<string>();
            var useRegex = parameters["regex"]?.Value<bool>() ?? false;
            var maxResults = parameters["max_results"]?.Value<int>() ?? DefaultMaxResults;
            var totalTimeoutMs = parameters["total_timeout_ms"]?.Value<int>() ?? DefaultTotalTimeoutMs;

            maxResults = Math.Clamp(maxResults, 1, 1000);
            totalTimeoutMs = Math.Clamp(totalTimeoutMs, 1000, 120000);

            Regex regex = null;
            if (useRegex)
            {
                try
                {
                    regex = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.Multiline,
                        TimeSpan.FromSeconds(PerFileRegexTimeoutSeconds));
                }
                catch (ArgumentException ex)
                {
                    return Error($"Invalid regex pattern: {ex.Message}");
                }
            }

            var projectRoot = McpPathValidator.ProjectRoot;
            var searchDirs = new List<string>();

            var assetsDir = Path.Combine(projectRoot, "Assets");
            var packagesDir = Path.Combine(projectRoot, "Packages");

            if (Directory.Exists(assetsDir)) searchDirs.Add(assetsDir);
            if (Directory.Exists(packagesDir)) searchDirs.Add(packagesDir);

            if (searchDirs.Count == 0)
                return Error("No Assets/ or Packages/ directory found.");

            Regex pathFilterRegex = null;
            if (!string.IsNullOrEmpty(pathPattern))
            {
                var regexPattern = GlobToRegex(pathPattern);
                try
                {
                    pathFilterRegex = new Regex(regexPattern, RegexOptions.IgnoreCase,
                        TimeSpan.FromSeconds(1));
                }
                catch
                {
                    return Error($"Invalid path pattern: {pathPattern}");
                }
            }

            var results = new JArray();
            int filesScanned = 0;
            bool timedOut = false;
            var totalSw = Stopwatch.StartNew();

            bool Expired() => totalSw.ElapsedMilliseconds >= totalTimeoutMs;
            foreach (var dir in searchDirs)
            foreach (var entry in FileTree.Walk(dir, true, Expired))
            {
                if (results.Count >= maxResults) break;
                if (entry is not FileInfo file || McpPathValidator.ContainsSymlink(file.FullName)) continue;
                var filePath = file.FullName;
                var ext = file.Extension;
                if (!TextExtensions.Contains(ext))
                    continue;

                if (BinaryExtensions.Contains(ext))
                    continue;

                var relativePath = GetProjectRelativePath(filePath);

                if (pathFilterRegex != null)
                {
                    try
                    {
                        if (!pathFilterRegex.IsMatch(relativePath))
                            continue;
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        continue;
                    }
                }

                filesScanned++;

                try
                {
                    int lineNumber = 0;
                    foreach (var line in File.ReadLines(filePath))
                    {
                        lineNumber++;
                        if (results.Count >= maxResults || Expired()) break;

                        bool matched;
                        if (useRegex && regex != null)
                        {
                            try
                            {
                                matched = regex.IsMatch(line);
                            }
                            catch (RegexMatchTimeoutException)
                            {
                                // Per-file regex timeout — skip rest of file
                                break;
                            }
                        }
                        else
                        {
                            matched = line.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                        }

                        if (matched)
                        {
                            results.Add(new JObject
                            {
                                ["file"] = relativePath,
                                ["line"] = lineNumber,
                                ["content"] = line.TrimEnd()
                            });
                        }
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            timedOut = Expired();
            totalSw.Stop();

            var response = new JObject
            {
                ["results"] = results,
                ["match_count"] = results.Count,
                ["files_scanned"] = filesScanned,
                ["elapsed_ms"] = totalSw.ElapsedMilliseconds
            };

            if (timedOut)
            {
                response["timed_out"] = true;
            }

            if (results.Count >= maxResults)
                response["max_results_reached"] = true;

            return response;
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

        private static string GlobToRegex(string glob)
        {
            var sb = new System.Text.StringBuilder("^");
            foreach (char c in glob)
            {
                switch (c)
                {
                    case '*':
                        sb.Append(".*");
                        break;
                    case '?':
                        sb.Append('.');
                        break;
                    case '.':
                        sb.Append("\\.");
                        break;
                    case '\\':
                    case '/':
                        sb.Append("[/\\\\]");
                        break;
                    default:
                        if ("+()^${}[]|".IndexOf(c) >= 0)
                            sb.Append('\\');
                        sb.Append(c);
                        break;
                }
            }
            sb.Append('$');
            return sb.ToString();
        }

        private static JObject Error(string message)
        {
            return McpError.Create(McpErrorCodes.InternalError, message);
        }

        #endregion
    }
}
