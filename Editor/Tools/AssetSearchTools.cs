using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace I1rr.UnityMcp.Editor
{
    [McpToolCategory("Asset")]
    public static class AssetSearchTools
    {

        #region search_assets

        [McpTool("search_assets",
            "Find assets in the project using AssetDatabase search. Supports type, label, and folder filters. Pages are 1-indexed.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""filter"": { ""type"": ""string"", ""description"": ""Search filter string (same as AssetDatabase.FindAssets). Can include t:TypeName or l:Label prefixes."" },
                    ""type"": { ""type"": ""string"", ""description"": ""Asset type filter (appended as t:TypeName to filter)"" },
                    ""labels"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Label filters (each appended as l:Label to filter)"" },
                    ""path_pattern"": { ""type"": ""string"", ""description"": ""Folder path to restrict search (e.g. 'Assets/Prefabs')"" },
                    ""page_size"": { ""type"": ""integer"", ""description"": ""Results per page (1-500, default 50)"", ""default"": 50 },
                    ""page"": { ""type"": ""integer"", ""description"": ""Page number (1-indexed, default 1)"", ""default"": 1 }
                }
            }")]
        public static JObject SearchAssets(JObject parameters)
        {
            var filter = ToolHelper.OptionalParam<string>(parameters, "filter") ?? "";
            var type = ToolHelper.OptionalParam<string>(parameters, "type");
            var labels = parameters["labels"]?.ToObject<string[]>();
            var pathPattern = ToolHelper.OptionalParam<string>(parameters, "path_pattern");
            var pageSize = ToolHelper.OptionalParam(parameters, "page_size", ToolHelper.DefaultPageSize);
            var page = ToolHelper.OptionalParam(parameters, "page", 1);

            // Build composite filter
            var filterParts = new List<string>();
            if (!string.IsNullOrEmpty(filter))
                filterParts.Add(filter);
            if (!string.IsNullOrEmpty(type))
                filterParts.Add($"t:{type}");
            if (labels != null)
            {
                foreach (var label in labels)
                {
                    if (!string.IsNullOrEmpty(label))
                        filterParts.Add($"l:{label}");
                }
            }

            var compositeFilter = string.Join(" ", filterParts);

            // Search folders
            string[] searchFolders = null;
            if (!string.IsNullOrEmpty(pathPattern))
            {
                if (!AssetDatabase.IsValidFolder(pathPattern))
                    return ToolHelper.ErrorResult("invalid_parameter", $"Invalid search folder: {pathPattern}",
                        "Provide a valid folder path such as 'Assets/Prefabs'.");
                searchFolders = new[] { pathPattern };
            }

            string[] guids = searchFolders != null
                ? AssetDatabase.FindAssets(compositeFilter, searchFolders)
                : AssetDatabase.FindAssets(compositeFilter);

            var pagination = ToolHelper.Paginate(guids, page, pageSize, guid =>
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                var assetType = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
                return new JObject
                {
                    ["guid"] = guid,
                    ["path"] = assetPath,
                    ["type"] = assetType?.Name ?? "Unknown",
                    ["name"] = Path.GetFileNameWithoutExtension(assetPath)
                };
            });

            pagination["filter"] = compositeFilter;
            return pagination;
        }

        #endregion

        #region guid_to_path

        [McpTool("guid_to_path",
            "Resolve an asset GUID to its project-relative path.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""guid"": { ""type"": ""string"", ""description"": ""Asset GUID to resolve"" }
                },
                ""required"": [""guid""]
            }")]
        public static JObject GuidToPath(JObject parameters)
        {
            var guid = ToolHelper.RequireParam<string>(parameters, "guid", out var error);
            if (error != null) return error;
            if (string.IsNullOrEmpty(guid))
                return ToolHelper.ErrorResult("missing_parameter", "guid is required.", "Provide a non-empty 'guid' value.");

            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path))
                return ToolHelper.ErrorResult("not_found", $"No asset found for GUID: {guid}");

            return new JObject
            {
                ["guid"] = guid,
                ["path"] = path,
                ["exists"] = File.Exists(Path.GetFullPath(path)) || Directory.Exists(Path.GetFullPath(path))
            };
        }

        #endregion

        #region path_to_guid

        [McpTool("path_to_guid",
            "Resolve an asset path to its GUID.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""path"": { ""type"": ""string"", ""description"": ""Asset path (e.g. 'Assets/Scripts/MyScript.cs')"" }
                },
                ""required"": [""path""]
            }")]
        public static JObject PathToGuid(JObject parameters)
        {
            var path = ToolHelper.RequireParam<string>(parameters, "path", out var error);
            if (error != null) return error;
            if (string.IsNullOrEmpty(path))
                return ToolHelper.ErrorResult("missing_parameter", "path is required.", "Provide a non-empty 'path' value.");

            var guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid))
                return ToolHelper.ErrorResult("not_found", $"No asset found at path: {path}");

            return new JObject
            {
                ["path"] = path,
                ["guid"] = guid
            };
        }

        #endregion

        #region get_asset_dependencies

        [McpTool("get_asset_dependencies",
            "Get all assets that this asset depends on (direct or recursive).",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""path"": { ""type"": ""string"", ""description"": ""Asset path (e.g. 'Assets/Prefabs/Player.prefab')"" },
                    ""recursive"": { ""type"": ""boolean"", ""description"": ""Include indirect (transitive) dependencies (default true)"", ""default"": true }
                },
                ""required"": [""path""]
            }")]
        public static JObject GetAssetDependencies(JObject parameters)
        {
            var path = ToolHelper.RequireParam<string>(parameters, "path", out var error);
            if (error != null) return error;
            var recursive = ToolHelper.OptionalParam(parameters, "recursive", true);

            if (string.IsNullOrEmpty(path))
                return ToolHelper.ErrorResult("missing_parameter", "path is required.");
            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)))
                return ToolHelper.ErrorResult("not_found", $"Asset not found: {path}");

            var deps = AssetDatabase.GetDependencies(path, recursive);

            // Exclude self
            var filtered = deps.Where(d => !string.Equals(d, path, StringComparison.OrdinalIgnoreCase)).ToArray();

            var items = new JArray();
            foreach (var dep in filtered)
            {
                var depType = AssetDatabase.GetMainAssetTypeAtPath(dep);
                items.Add(new JObject
                {
                    ["path"] = dep,
                    ["type"] = depType?.Name ?? "Unknown",
                    ["guid"] = AssetDatabase.AssetPathToGUID(dep)
                });
            }

            return new JObject
            {
                ["path"] = path,
                ["recursive"] = recursive,
                ["count"] = items.Count,
                ["dependencies"] = items
            };
        }

        #endregion

        #region get_asset_references

        [McpTool("get_asset_references",
            "Find all assets that reference the given asset (reverse dependency lookup). Runs async via JobManager — returns a job_id to poll.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""path"": { ""type"": ""string"", ""description"": ""Asset path to find references to"" },
                    ""timeout_ms"": { ""type"": ""integer"", ""description"": ""Max scan time in milliseconds (default 30000)"", ""default"": 30000 }
                },
                ""required"": [""path""]
            }")]
        public static JObject GetAssetReferences(JObject parameters)
        {
            var path = ToolHelper.RequireParam<string>(parameters, "path", out var error);
            if (error != null) return error;
            var timeoutMs = ToolHelper.OptionalParam(parameters, "timeout_ms", 30000);

            if (string.IsNullOrEmpty(path))
                return ToolHelper.ErrorResult("missing_parameter", "path is required.");

            var targetGuid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(targetGuid))
                return ToolHelper.ErrorResult("not_found", $"Asset not found: {path}");

            // Gather the list of scannable files on the main thread
            var allAssets = AssetDatabase.GetAllAssetPaths()
                .Where(p => p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                .Where(p =>
                {
                    var ext = Path.GetExtension(p).ToLowerInvariant();
                    return ext is ".prefab" or ".unity" or ".asset" or ".mat"
                        or ".controller" or ".anim" or ".playable" or ".rendertexture"
                        or ".lighting" or ".shadergraph" or ".shadersubgraph";
                })
                .Select(assetPath => (Path: assetPath, Guid: AssetDatabase.AssetPathToGUID(assetPath)))
                .ToArray();

            var projectRoot = McpPathValidator.ProjectRoot;

            var jobId = JobManager.CreateJob($"Find references to {path}", ctx =>
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken);
                cts.CancelAfter(timeoutMs);
                var token = cts.Token;

                var references = new List<JObject>();
                var errors = new JArray();
                int processed = 0;
                int total = allAssets.Length;

                foreach (var (assetPath, assetGuid) in allAssets)
                {
                    if (token.IsCancellationRequested)
                        break;

                    processed++;
                    if (processed % 100 == 0)
                        ctx.ReportProgress((float)processed / total);

                    // Skip self
                    if (string.Equals(assetPath, path, StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        var fullPath = Path.Combine(projectRoot, assetPath);
                        if (McpPathValidator.ContainsSymlink(fullPath))
                            continue;

                        var content = File.ReadAllText(fullPath);
                        if (content.Contains(targetGuid, StringComparison.Ordinal))
                        {
                            references.Add(new JObject
                            {
                                ["path"] = assetPath,
                                ["guid"] = assetGuid
                            });
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        errors.Add(new JObject { ["path"] = assetPath, ["message"] = ex.Message });
                    }
                }

                var result = new JObject
                {
                    ["target_path"] = path,
                    ["target_guid"] = targetGuid,
                    ["count"] = references.Count,
                    ["scanned"] = processed,
                    ["total_scannable"] = total,
                    ["timed_out"] = token.IsCancellationRequested,
                    ["references"] = new JArray(references.ToArray())
                };

                if (errors.Count == 0) ctx.Complete(result);
                else ctx.CompleteWithErrors(result, errors);
            });

            return new JObject
            {
                ["job_id"] = jobId,
                ["message"] = $"Scanning {allAssets.Length} assets for references to '{path}'. Use get_job_status to poll."
            };
        }

        #endregion

    }
}
