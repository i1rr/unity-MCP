#if UNITY_6000_6_OR_NEWER
using ObjectId = System.UInt64;
#else
using ObjectId = System.Int32;
#endif
using System;
using System.Collections.Generic;
using System.Linq;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Scripting;

namespace I1rr.UnityMcp.Runtime
{
    [Preserve]
    [McpToolCategory("Profiling")]
    public static class RuntimeAssetTools
    {
        private const int DefaultPageSize = 50;
        private const int MaxPageSize = 200;

        private static JObject Error(string message) => McpError.Create(McpErrorCodes.InternalError, message);

        #region runtime_list_loaded_assets

        [McpTool("runtime_list_loaded_assets",
            "List loaded assets by type with memory usage. Supports textures, meshes, materials, shaders, audio clips. " +
            "Returns name, instance ID, and memory size for each asset. Useful for profiling memory at runtime.",
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""asset_type"": { ""type"": ""string"", ""description"": ""Asset type to list: texture, mesh, material, shader, audioclip"", ""enum"": [""texture"", ""mesh"", ""material"", ""shader"", ""audioclip""] },
                    ""sort_by"": { ""type"": ""string"", ""description"": ""Sort order: memory (descending size) or name (alphabetical). Default: memory"", ""enum"": [""memory"", ""name""], ""default"": ""memory"" },
                    ""page"": { ""type"": ""integer"", ""description"": ""Page number (1-based). Default: 1"", ""default"": 1 },
                    ""page_size"": { ""type"": ""integer"", ""description"": ""Items per page (max 200). Default: 50"", ""default"": 50 },
                    ""name_filter"": { ""type"": ""string"", ""description"": ""Optional substring filter on asset name (case-insensitive)"" }
                },
                ""required"": [""asset_type""]
            }")]
        [Preserve]
        public static JObject ListLoadedAssets(JObject parameters)
        {
            var assetType = parameters["asset_type"]?.Value<string>();
            if (string.IsNullOrEmpty(assetType))
                return Error("'asset_type' is required.");

            var sortBy = parameters["sort_by"]?.Value<string>() ?? "memory";
            int page = Math.Max(1, parameters["page"]?.Value<int>() ?? 1);
            int pageSize = Math.Clamp(parameters["page_size"]?.Value<int>() ?? DefaultPageSize, 1, MaxPageSize);
            var nameFilter = parameters["name_filter"]?.Value<string>();

            Type unityType;
            switch (assetType.ToLowerInvariant())
            {
                case "texture": unityType = typeof(Texture); break;
                case "mesh": unityType = typeof(Mesh); break;
                case "material": unityType = typeof(Material); break;
                case "shader": unityType = typeof(Shader); break;
                case "audioclip": unityType = typeof(AudioClip); break;
                default:
                    return Error($"Unsupported asset_type: '{assetType}'. Use: texture, mesh, material, shader, audioclip");
            }

            var objects = Resources.FindObjectsOfTypeAll(unityType);
            var entries = new List<AssetEntry>(objects.Length);

            long totalMemory = 0;

            for (int i = 0; i < objects.Length; i++)
            {
                var obj = objects[i];
                if (obj == null) continue;

                // Skip editor-only objects in case this somehow runs in editor
                if (obj.hideFlags.HasFlag(HideFlags.HideAndDontSave)) continue;

                var objName = obj.name;
                if (!string.IsNullOrEmpty(nameFilter) &&
                    objName.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                long memSize = Profiler.GetRuntimeMemorySizeLong(obj);
                totalMemory += memSize;

                entries.Add(new AssetEntry
                {
                    Name = objName,
                    InstanceId = obj.GetMcpId(),
                    MemoryBytes = memSize
                });
            }

            // Sort
            if (sortBy == "name")
                entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            else
                entries.Sort((a, b) => b.MemoryBytes.CompareTo(a.MemoryBytes));

            // Paginate
            int totalCount = entries.Count;
            int totalPages = Math.Max(1, (totalCount + pageSize - 1) / pageSize);
            page = Math.Min(page, totalPages);
            int skip = (page - 1) * pageSize;
            var pageItems = entries.Skip(skip).Take(pageSize);

            var items = new JArray();
            foreach (var entry in pageItems)
            {
                items.Add(new JObject
                {
                    ["name"] = entry.Name,
                    ["instance_id"] = entry.InstanceId,
                    ["memory_bytes"] = entry.MemoryBytes,
                    ["memory_mb"] = Math.Round(entry.MemoryBytes / (1024.0 * 1024.0), 3)
                });
            }

            return new JObject
            {
                ["success"] = true,
                ["asset_type"] = assetType,
                ["total_count"] = totalCount,
                ["total_memory_mb"] = Math.Round(totalMemory / (1024.0 * 1024.0), 2),
                ["page"] = page,
                ["page_size"] = pageSize,
                ["total_pages"] = totalPages,
                ["items"] = items
            };
        }

        private struct AssetEntry
        {
            public string Name;
            public ObjectId InstanceId;
            public long MemoryBytes;
        }

        #endregion
    }
}
