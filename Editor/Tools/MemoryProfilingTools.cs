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

namespace I1rr.UnityMcp.Editor
{
    [McpToolCategory("Profiling")]
    public static class MemoryProfilingTools
    {
        private static HashSet<ObjectId> _baselineInstanceIds;
        private static string _baselineTimestamp;
        private static long _baselineMonoUsed;
        private static long _baselineTotalAllocated;
        private static long _baselineTotalReserved;

        #region get_memory_summary

        [McpTool("get_memory_summary",
            "Get total managed/native/GfxDriver memory, mono heap size. Play mode only.",
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {}
            }")]
        public static JObject GetMemorySummary(JObject parameters)
        {
            long totalAllocated = Profiler.GetTotalAllocatedMemoryLong();
            long totalReserved = Profiler.GetTotalReservedMemoryLong();
            long monoHeap = Profiler.GetMonoHeapSizeLong();
            long monoUsed = Profiler.GetMonoUsedSizeLong();
            long gfxDriver = Profiler.GetAllocatedMemoryForGraphicsDriver();
            long tempAllocator = Profiler.GetTempAllocatorSize();

            return new JObject
            {
                ["success"] = true,
                ["total_allocated_mb"] = Math.Round(totalAllocated / (1024.0 * 1024.0), 2),
                ["total_reserved_mb"] = Math.Round(totalReserved / (1024.0 * 1024.0), 2),
                ["mono_heap_mb"] = Math.Round(monoHeap / (1024.0 * 1024.0), 2),
                ["mono_used_mb"] = Math.Round(monoUsed / (1024.0 * 1024.0), 2),
                ["gfx_driver_mb"] = Math.Round(gfxDriver / (1024.0 * 1024.0), 2),
                ["temp_allocator_mb"] = Math.Round(tempAllocator / (1024.0 * 1024.0), 2),
                ["total_allocated_bytes"] = totalAllocated,
                ["total_reserved_bytes"] = totalReserved,
                ["mono_heap_bytes"] = monoHeap,
                ["mono_used_bytes"] = monoUsed,
                ["gfx_driver_bytes"] = gfxDriver
            };
        }

        #endregion

        #region get_loaded_textures

        [McpTool("get_loaded_textures",
            "Get loaded Texture2D list with dimensions and memory. Play mode only.",
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""sort_by"": { ""type"": ""string"", ""enum"": [""memory"", ""name"", ""width"", ""height""], ""description"": ""Sort field (default: memory)"", ""default"": ""memory"" },
                    ""min_size"": { ""type"": ""integer"", ""description"": ""Minimum texture memory in bytes to include (default 0)"", ""default"": 0 },
                    ""max_results"": { ""type"": ""integer"", ""description"": ""Maximum results to return (default 100, max 500)"", ""default"": 100 }
                }
            }")]
        public static JObject GetLoadedTextures(JObject parameters)
        {
            var sortBy = parameters["sort_by"]?.Value<string>() ?? "memory";
            long minSize = parameters["min_size"]?.Value<long>() ?? 0;
            int maxResults = Math.Clamp(parameters["max_results"]?.Value<int>() ?? 100, 1, 500);

            var textures = Resources.FindObjectsOfTypeAll<Texture2D>();
            var entries = new List<(string name, int width, int height, long memory, TextureFormat format, int mipmapCount)>();

            foreach (var tex in textures)
            {
                if (tex == null) continue;
                long mem = Profiler.GetRuntimeMemorySizeLong(tex);
                if (mem < minSize) continue;

                entries.Add((tex.name, tex.width, tex.height, mem, tex.format, tex.mipmapCount));
            }

            entries = sortBy switch
            {
                "name" => entries.OrderBy(e => e.name).ToList(),
                "width" => entries.OrderByDescending(e => e.width).ToList(),
                "height" => entries.OrderByDescending(e => e.height).ToList(),
                _ => entries.OrderByDescending(e => e.memory).ToList()
            };

            long totalMemory = entries.Sum(e => e.memory);
            int totalCount = entries.Count;
            entries = entries.Take(maxResults).ToList();

            var results = new JArray();
            foreach (var e in entries)
            {
                results.Add(new JObject
                {
                    ["name"] = e.name,
                    ["width"] = e.width,
                    ["height"] = e.height,
                    ["memory_bytes"] = e.memory,
                    ["memory_mb"] = Math.Round(e.memory / (1024.0 * 1024.0), 3),
                    ["format"] = e.format.ToString(),
                    ["mipmap_count"] = e.mipmapCount
                });
            }

            return new JObject
            {
                ["success"] = true,
                ["total_count"] = totalCount,
                ["returned_count"] = results.Count,
                ["total_memory_mb"] = Math.Round(totalMemory / (1024.0 * 1024.0), 2),
                ["textures"] = results
            };
        }

        #endregion

        #region get_loaded_meshes

        [McpTool("get_loaded_meshes",
            "Get loaded meshes with vertex count and memory. Play mode only.",
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""sort_by"": { ""type"": ""string"", ""enum"": [""memory"", ""name"", ""vertices""], ""description"": ""Sort field (default: memory)"", ""default"": ""memory"" },
                    ""max_results"": { ""type"": ""integer"", ""description"": ""Maximum results to return (default 100, max 500)"", ""default"": 100 }
                }
            }")]
        public static JObject GetLoadedMeshes(JObject parameters)
        {
            var sortBy = parameters["sort_by"]?.Value<string>() ?? "memory";
            int maxResults = Math.Clamp(parameters["max_results"]?.Value<int>() ?? 100, 1, 500);

            var meshes = Resources.FindObjectsOfTypeAll<Mesh>();
            var entries = new List<(string name, int vertexCount, int subMeshCount, long memory, int triangles)>();

            foreach (var mesh in meshes)
            {
                if (mesh == null) continue;
                long mem = Profiler.GetRuntimeMemorySizeLong(mesh);
                int tris = 0;
                for (int i = 0; i < mesh.subMeshCount; i++)
                    tris += (int)mesh.GetIndexCount(i) / 3;

                entries.Add((mesh.name, mesh.vertexCount, mesh.subMeshCount, mem, tris));
            }

            entries = sortBy switch
            {
                "name" => entries.OrderBy(e => e.name).ToList(),
                "vertices" => entries.OrderByDescending(e => e.vertexCount).ToList(),
                _ => entries.OrderByDescending(e => e.memory).ToList()
            };

            long totalMemory = entries.Sum(e => e.memory);
            int totalCount = entries.Count;
            entries = entries.Take(maxResults).ToList();

            var results = new JArray();
            foreach (var e in entries)
            {
                results.Add(new JObject
                {
                    ["name"] = e.name,
                    ["vertex_count"] = e.vertexCount,
                    ["triangle_count"] = e.triangles,
                    ["sub_mesh_count"] = e.subMeshCount,
                    ["memory_bytes"] = e.memory,
                    ["memory_mb"] = Math.Round(e.memory / (1024.0 * 1024.0), 3)
                });
            }

            return new JObject
            {
                ["success"] = true,
                ["total_count"] = totalCount,
                ["returned_count"] = results.Count,
                ["total_memory_mb"] = Math.Round(totalMemory / (1024.0 * 1024.0), 2),
                ["meshes"] = results
            };
        }

        #endregion

        #region get_loaded_materials

        [McpTool("get_loaded_materials",
            "Get materials currently in memory with shader references. Play mode only.",
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""max_results"": { ""type"": ""integer"", ""description"": ""Maximum results to return (default 100, max 500)"", ""default"": 100 }
                }
            }")]
        public static JObject GetLoadedMaterials(JObject parameters)
        {
            int maxResults = Math.Clamp(parameters["max_results"]?.Value<int>() ?? 100, 1, 500);

            var materials = Resources.FindObjectsOfTypeAll<Material>();
            var entries = new List<(string name, string shader, long memory, int renderQueue, string[] keywords)>();

            foreach (var mat in materials)
            {
                if (mat == null) continue;
                long mem = Profiler.GetRuntimeMemorySizeLong(mat);
                var shaderName = mat.shader != null ? mat.shader.name : "<null>";
                var keywords = mat.shaderKeywords ?? Array.Empty<string>();

                entries.Add((mat.name, shaderName, mem, mat.renderQueue, keywords));
            }

            entries = entries.OrderByDescending(e => e.memory).ToList();

            int totalCount = entries.Count;
            entries = entries.Take(maxResults).ToList();

            var shaderGroups = entries.GroupBy(e => e.shader)
                .Select(g => new JObject
                {
                    ["shader"] = g.Key,
                    ["count"] = g.Count()
                })
                .OrderByDescending(j => j["count"].Value<int>());

            var results = new JArray();
            foreach (var e in entries)
            {
                results.Add(new JObject
                {
                    ["name"] = e.name,
                    ["shader"] = e.shader,
                    ["memory_bytes"] = e.memory,
                    ["render_queue"] = e.renderQueue,
                    ["keywords"] = new JArray(e.keywords)
                });
            }

            return new JObject
            {
                ["success"] = true,
                ["total_count"] = totalCount,
                ["returned_count"] = results.Count,
                ["shader_usage"] = new JArray(shaderGroups),
                ["materials"] = results
            };
        }

        #endregion

        #region get_active_render_textures

        [McpTool("get_active_render_textures",
            "Get RenderTextures currently in memory with dimensions and format. Play mode only.",
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {}
            }")]
        public static JObject GetActiveRenderTextures(JObject parameters)
        {
            var renderTextures = Resources.FindObjectsOfTypeAll<RenderTexture>();
            var results = new JArray();
            long totalMemory = 0;

            foreach (var rt in renderTextures)
            {
                if (rt == null) continue;
                long mem = Profiler.GetRuntimeMemorySizeLong(rt);
                totalMemory += mem;

                results.Add(new JObject
                {
                    ["name"] = rt.name,
                    ["width"] = rt.width,
                    ["height"] = rt.height,
                    ["depth"] = rt.depth,
                    ["format"] = rt.format.ToString(),
                    ["color_format"] = rt.graphicsFormat.ToString(),
                    ["anti_aliasing"] = rt.antiAliasing,
                    ["memory_bytes"] = mem,
                    ["memory_mb"] = Math.Round(mem / (1024.0 * 1024.0), 3),
                    ["is_created"] = rt.IsCreated()
                });
            }

            return new JObject
            {
                ["success"] = true,
                ["count"] = results.Count,
                ["total_memory_mb"] = Math.Round(totalMemory / (1024.0 * 1024.0), 2),
                ["render_textures"] = results
            };
        }

        #endregion

        #region find_leaked_objects

        [McpTool("find_leaked_objects",
            "Find UnityEngine.Objects surviving scene unload — potential leaks. Play mode only.",
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""type"": { ""type"": ""string"", ""description"": ""Object type to check (e.g. 'Texture2D', 'Material', 'Mesh', 'GameObject'). Default: all."" },
                    ""ignore_editor_types"": { ""type"": ""boolean"", ""description"": ""Exclude editor-only objects and hidden objects (default: true)"", ""default"": true },
                    ""max_results"": { ""type"": ""integer"", ""description"": ""Maximum results to return (default 200, max 1000)"", ""default"": 200 }
                }
            }")]
        public static JObject FindLeakedObjects(JObject parameters)
        {
            var typeName = parameters["type"]?.Value<string>();
            bool ignoreEditorTypes = parameters["ignore_editor_types"]?.Value<bool>() ?? true;
            int maxResults = Math.Clamp(parameters["max_results"]?.Value<int>() ?? 200, 1, 1000);

            UnityEngine.Object[] allObjects;

            if (!string.IsNullOrEmpty(typeName))
            {
                var type = ProfilingHelpers.FindUnityType(typeName);
                if (type == null)
                    return Error($"Type '{typeName}' not found. Use fully qualified name or common Unity types.");

                allObjects = Resources.FindObjectsOfTypeAll(type);
            }
            else
            {
                allObjects = Resources.FindObjectsOfTypeAll<UnityEngine.Object>();
            }

            var leaked = new List<(string name, string type, ObjectId instanceId, long memory, HideFlags hideFlags)>();

            foreach (var obj in allObjects)
            {
                if (obj == null) continue;

                if (ignoreEditorTypes)
                {
                    if ((obj.hideFlags & HideFlags.HideAndDontSave) != 0) continue;
                    if ((obj.hideFlags & HideFlags.DontUnloadUnusedAsset) != 0) continue;
                }

                // Check if object has no scene reference (orphaned runtime object)
                if (obj is GameObject go)
                {
                    if (go.scene.IsValid() && go.scene.isLoaded) continue;
                    if (string.IsNullOrEmpty(go.scene.name)) // DontDestroyOnLoad or leaked
                    {
                        long mem = Profiler.GetRuntimeMemorySizeLong(obj);
                        leaked.Add((obj.name, obj.GetType().Name, obj.GetMcpId(), mem, obj.hideFlags));
                    }
                }
                else if (obj is Component comp)
                {
                    if (comp.gameObject.scene.IsValid() && comp.gameObject.scene.isLoaded) continue;
                }
            }

            leaked = leaked.OrderByDescending(e => e.memory).Take(maxResults).ToList();

            var results = new JArray();
            foreach (var e in leaked)
            {
                results.Add(new JObject
                {
                    ["name"] = e.name,
                    ["type"] = e.type,
                    ["instance_id"] = e.instanceId,
                    ["memory_bytes"] = e.memory,
                    ["hide_flags"] = e.hideFlags.ToString()
                });
            }

            return new JObject
            {
                ["success"] = true,
                ["count"] = results.Count,
                ["note"] = "Objects without a valid loaded scene. May include DontDestroyOnLoad objects.",
                ["leaked_objects"] = results
            };
        }

        #endregion

        #region get_object_count_by_type

        [McpTool("get_object_count_by_type",
            "Get count of all UnityEngine.Object types currently in memory. Play mode only.",
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {}
            }")]
        public static JObject GetObjectCountByType(JObject parameters)
        {
            var allObjects = Resources.FindObjectsOfTypeAll<UnityEngine.Object>();
            var typeCounts = new Dictionary<string, int>();
            long totalMemory = 0;

            foreach (var obj in allObjects)
            {
                if (obj == null) continue;
                var typeName = obj.GetType().Name;
                typeCounts.TryGetValue(typeName, out int count);
                typeCounts[typeName] = count + 1;
                totalMemory += Profiler.GetRuntimeMemorySizeLong(obj);
            }

            var sorted = typeCounts.OrderByDescending(kv => kv.Value).ToList();

            var results = new JArray();
            foreach (var kv in sorted)
            {
                results.Add(new JObject
                {
                    ["type"] = kv.Key,
                    ["count"] = kv.Value
                });
            }

            return new JObject
            {
                ["success"] = true,
                ["total_objects"] = allObjects.Length,
                ["total_memory_mb"] = Math.Round(totalMemory / (1024.0 * 1024.0), 2),
                ["unique_types"] = typeCounts.Count,
                ["types"] = results
            };
        }

        #endregion

        #region take_memory_baseline

        [McpTool("take_memory_baseline",
            "Snapshot current object set as baseline for leak detection. Play mode only.",
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""include_gc"": { ""type"": ""boolean"", ""description"": ""Run GC.Collect before taking the baseline (default: false). Set to true for a clean baseline, false to preserve transient objects."", ""default"": false }
                }
            }")]
        public static JObject TakeMemoryBaseline(JObject parameters)
        {
            bool includeGc = parameters["include_gc"]?.Value<bool>() ?? false;

            if (includeGc)
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
                GC.WaitForPendingFinalizers();
            }

            var allObjects = Resources.FindObjectsOfTypeAll<UnityEngine.Object>();
            _baselineInstanceIds = new HashSet<ObjectId>(allObjects.Length);

            foreach (var obj in allObjects)
            {
                if (obj == null) continue;
                // Filter out editor-only assets and hidden objects to reduce noise
                if ((obj.hideFlags & HideFlags.HideAndDontSave) != 0) continue;
                _baselineInstanceIds.Add(obj.GetMcpId());
            }

            _baselineTimestamp = DateTimeOffset.UtcNow.ToString("O");
            _baselineMonoUsed = Profiler.GetMonoUsedSizeLong();
            _baselineTotalAllocated = Profiler.GetTotalAllocatedMemoryLong();
            _baselineTotalReserved = Profiler.GetTotalReservedMemoryLong();

            return new JObject
            {
                ["success"] = true,
                ["baseline_object_count"] = _baselineInstanceIds.Count,
                ["timestamp"] = _baselineTimestamp,
                ["include_gc"] = includeGc,
                ["mono_used_bytes"] = _baselineMonoUsed,
                ["total_allocated_bytes"] = _baselineTotalAllocated,
                ["total_reserved_bytes"] = _baselineTotalReserved
            };
        }

        #endregion

        #region compare_memory_baseline

        [McpTool("compare_memory_baseline",
            "Diff current objects vs baseline taken with take_memory_baseline. Play mode only.",
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""include_gc"": { ""type"": ""boolean"", ""description"": ""Run GC.Collect before comparing (default: false). Set to true to exclude short-lived objects, false to include them in the diff."", ""default"": false },
                    ""max_results"": { ""type"": ""integer"", ""description"": ""Maximum new objects to return (default 200, max 1000)"", ""default"": 200 }
                }
            }")]
        public static JObject CompareMemoryBaseline(JObject parameters)
        {
            if (_baselineInstanceIds == null)
                return Error("No baseline taken. Call take_memory_baseline first.");

            int maxResults = Math.Clamp(parameters["max_results"]?.Value<int>() ?? 200, 1, 1000);
            bool includeGc = parameters["include_gc"]?.Value<bool>() ?? false;

            if (includeGc)
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
                GC.WaitForPendingFinalizers();
            }

            // Capture current memory stats for delta tracking
            long currentMonoUsed = Profiler.GetMonoUsedSizeLong();
            long currentTotalAllocated = Profiler.GetTotalAllocatedMemoryLong();
            long currentTotalReserved = Profiler.GetTotalReservedMemoryLong();

            var allObjects = Resources.FindObjectsOfTypeAll<UnityEngine.Object>();
            var newObjects = new List<(string name, string type, ObjectId instanceId, long memory)>();
            int destroyedCount = 0;

            var currentIds = new HashSet<ObjectId>(allObjects.Length);
            foreach (var obj in allObjects)
            {
                if (obj == null) continue;
                // Filter out editor-only assets and hidden objects (same filter as baseline)
                if ((obj.hideFlags & HideFlags.HideAndDontSave) != 0) continue;

                ObjectId id = obj.GetMcpId();
                currentIds.Add(id);

                if (!_baselineInstanceIds.Contains(id))
                {
                    long mem = Profiler.GetRuntimeMemorySizeLong(obj);
                    newObjects.Add((obj.name, obj.GetType().Name, id, mem));
                }
            }

            // Count objects that were in baseline but no longer exist
            foreach (ObjectId baseId in _baselineInstanceIds)
            {
                if (!currentIds.Contains(baseId))
                    destroyedCount++;
            }

            newObjects = newObjects.OrderByDescending(e => e.memory).ToList();

            var typeSummary = newObjects.GroupBy(e => e.type)
                .Select(g => new JObject
                {
                    ["type"] = g.Key,
                    ["count"] = g.Count(),
                    ["total_memory_bytes"] = g.Sum(x => x.memory),
                    ["total_memory_mb"] = Math.Round(g.Sum(x => x.memory) / (1024.0 * 1024.0), 3)
                })
                .OrderByDescending(j => j["total_memory_bytes"].Value<long>());

            var details = new JArray();
            foreach (var e in newObjects.Take(maxResults))
            {
                details.Add(new JObject
                {
                    ["name"] = e.name,
                    ["type"] = e.type,
                    ["instance_id"] = e.instanceId,
                    ["memory_bytes"] = e.memory
                });
            }

            return new JObject
            {
                ["success"] = true,
                ["include_gc"] = includeGc,
                ["baseline_timestamp"] = _baselineTimestamp,
                ["baseline_count"] = _baselineInstanceIds.Count,
                ["current_count"] = currentIds.Count,
                ["new_objects_count"] = newObjects.Count,
                ["destroyed_count"] = destroyedCount,
                ["new_objects_total_memory_bytes"] = newObjects.Sum(e => e.memory),
                ["memory_delta"] = new JObject
                {
                    ["mono_used_delta_bytes"] = currentMonoUsed - _baselineMonoUsed,
                    ["total_allocated_delta_bytes"] = currentTotalAllocated - _baselineTotalAllocated,
                    ["total_reserved_delta_bytes"] = currentTotalReserved - _baselineTotalReserved,
                    ["mono_used_delta_mb"] = Math.Round((currentMonoUsed - _baselineMonoUsed) / (1024.0 * 1024.0), 3),
                    ["total_allocated_delta_mb"] = Math.Round((currentTotalAllocated - _baselineTotalAllocated) / (1024.0 * 1024.0), 3),
                    ["total_reserved_delta_mb"] = Math.Round((currentTotalReserved - _baselineTotalReserved) / (1024.0 * 1024.0), 3)
                },
                ["type_summary"] = new JArray(typeSummary),
                ["new_objects"] = details
            };
        }

        #endregion

        private static JObject Error(string message)
        {
            var result = McpError.Create(McpErrorCodes.InternalError, message);
            result["success"] = false;
            return result;
        }
    }
}
