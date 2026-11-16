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
using UnityEngine.SceneManagement;
using UnityEngine.Scripting;

namespace I1rr.UnityMcp.Runtime
{
    [Preserve]
    [McpToolCategory("Scene")]
    public static class RuntimeSceneTools
    {
        private const int MaxPageSize = 200;
        private const int DefaultPageSize = 50;
        private const int DefaultMaxDepth = 5;

        private static JObject Error(string message) => McpError.Create(McpErrorCodes.InternalError, message);

        private static string GetGameObjectPath(Transform t)
        {
            var parts = new List<string>();
            while (t != null)
            {
                parts.Add(t.name);
                t = t.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static GameObject FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            var parts = path.Split('/');
            GameObject root = null;

            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (var go in scene.GetRootGameObjects())
                {
                    if (go.name == parts[0])
                    {
                        root = go;
                        break;
                    }
                }
                if (root != null) break;
            }

            if (root == null) return null;
            if (parts.Length == 1) return root;

            var current = root.transform;
            for (int i = 1; i < parts.Length; i++)
            {
                var child = current.Find(parts[i]);
                if (child == null) return null;
                current = child;
            }

            return current.gameObject;
        }

        #region runtime_get_hierarchy

        [McpTool("runtime_get_hierarchy",
            "Paged DFS scene hierarchy traversal at runtime. Returns transform data per node.",
            AutoRoute = true,
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""root_path"": { ""type"": ""string"", ""description"": ""Path to root GO. If omitted, traverses all scene roots."" },
                    ""max_depth"": { ""type"": ""integer"", ""description"": ""Max depth to traverse (default 5)"", ""default"": 5 },
                    ""page_size"": { ""type"": ""integer"", ""description"": ""Max items per page (1-200, default 50)"", ""default"": 50 },
                    ""cursor"": { ""type"": ""integer"", ""description"": ""InstanceId of last emitted GO from previous page. Omit for first page."" }
                }
            }")]
        [Preserve]
        public static JObject GetHierarchy(JObject parameters)
        {
            var rootPath = parameters["root_path"]?.Value<string>();
            int maxDepth = Math.Clamp(parameters["max_depth"]?.Value<int>() ?? DefaultMaxDepth, 0, 50);
            int pageSize = Math.Clamp(parameters["page_size"]?.Value<int>() ?? DefaultPageSize, 1, MaxPageSize);
            ObjectId cursorId = parameters["cursor"]?.ToObject<ObjectId>() ?? 0;

            List<GameObject> roots;

            if (!string.IsNullOrEmpty(rootPath))
            {
                var rootGo = FindByPath(rootPath);
                if (rootGo == null) return Error($"GameObject not found at path: {rootPath}");
                roots = new List<GameObject> { rootGo };
            }
            else
            {
                roots = new List<GameObject>();
                for (int s = 0; s < SceneManager.sceneCount; s++)
                {
                    var scene = SceneManager.GetSceneAt(s);
                    if (!scene.isLoaded) continue;
                    roots.AddRange(scene.GetRootGameObjects());
                }
            }

            // DFS traversal
            var allNodes = new List<(GameObject go, int depth, string sceneName, int buildIndex)>();
            var stack = new Stack<(GameObject go, int depth, string sceneName, int buildIndex)>();

            for (int i = roots.Count - 1; i >= 0; i--)
            {
                var go = roots[i];
                var scene = go.scene;
                stack.Push((go, 0, scene.name, scene.buildIndex));
            }

            while (stack.Count > 0)
            {
                var (go, depth, sceneName, buildIndex) = stack.Pop();
                if (go == null) continue;

                allNodes.Add((go, depth, sceneName, buildIndex));

                if (depth >= maxDepth) continue;

                var t = go.transform;
                for (int i = t.childCount - 1; i >= 0; i--)
                {
                    var child = t.GetChild(i);
                    stack.Push((child.gameObject, depth + 1, sceneName, buildIndex));
                }
            }

            // Find start based on cursor
            int startIndex = 0;
            if (cursorId != 0)
            {
                for (int i = 0; i < allNodes.Count; i++)
                {
                    if (allNodes[i].go.GetMcpId() == cursorId)
                    {
                        startIndex = i + 1;
                        break;
                    }
                }
            }

            // Emit page
            var items = new JArray();
            ObjectId lastEmittedId = 0;

            for (int i = startIndex; i < allNodes.Count && items.Count < pageSize; i++)
            {
                var (go, depth, sceneName, buildIndex) = allNodes[i];
                lastEmittedId = go.GetMcpId();

                items.Add(new JObject
                {
                    ["instanceId"] = lastEmittedId,
                    ["name"] = go.name,
                    ["path"] = GetGameObjectPath(go.transform),
                    ["depth"] = depth,
                    ["active"] = go.activeInHierarchy,
                    ["childCount"] = go.transform.childCount,
                    ["scene_name"] = sceneName,
                    ["build_index"] = buildIndex
                });
            }

            bool hasMore = startIndex + items.Count < allNodes.Count;

            return new JObject
            {
                ["total_count"] = allNodes.Count,
                ["page_size"] = pageSize,
                ["items"] = items,
                ["next_cursor"] = hasMore ? lastEmittedId : (ObjectId?)null
            };
        }

        #endregion

        #region runtime_find_gameobjects

        [McpTool("runtime_find_gameobjects",
            "Find GameObjects matching criteria at runtime.",
            AutoRoute = true,
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""name"": { ""type"": ""string"", ""description"": ""Partial name match (case-insensitive)"" },
                    ""tag"": { ""type"": ""string"", ""description"": ""Exact tag match"" },
                    ""layer"": { ""type"": ""string"", ""description"": ""Layer name"" },
                    ""component_type"": { ""type"": ""string"", ""description"": ""Component type name (e.g. 'Camera', 'Rigidbody')"" },
                    ""inactive"": { ""type"": ""boolean"", ""description"": ""Include inactive objects (default true)."" }
                }
            }")]
        [Preserve]
        public static JObject FindGameObjects(JObject parameters)
        {
            var nameFilter = parameters["name"]?.Value<string>();
            var tagFilter = parameters["tag"]?.Value<string>();
            var layerFilter = parameters["layer"]?.Value<string>();
            var componentFilter = parameters["component_type"]?.Value<string>();

            int layer = string.IsNullOrEmpty(layerFilter) ? -1 : LayerMask.NameToLayer(layerFilter);
            if (!string.IsNullOrEmpty(layerFilter) && layer < 0) return Error($"Layer not found: {layerFilter}");
            Type type = string.IsNullOrEmpty(componentFilter) ? null : FindType(componentFilter);
            if (!string.IsNullOrEmpty(componentFilter) && type == null) return Error($"Component type not found: {componentFilter}");

            var includeInactive = (bool?)parameters["inactive"] ?? true;
            var transforms = UnityCompatibility.FindObjectsByType<Transform>(
                includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude);
            var results = new JArray();
            bool capped = false;
            if (!string.IsNullOrEmpty(tagFilter))
            {
                try { GameObject.FindWithTag(tagFilter); }
                catch (UnityException) { return Error($"Invalid tag: {tagFilter}"); }
            }
            foreach (var t in transforms)
            {
                var go = t.gameObject;
                if (!string.IsNullOrEmpty(nameFilter) && go.name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (layer >= 0 && go.layer != layer) continue;
                if (type != null && go.GetComponent(type) == null) continue;
                if (!string.IsNullOrEmpty(tagFilter) && !go.CompareTag(tagFilter)) continue;
                if (results.Count == MaxPageSize) { capped = true; break; }

                results.Add(new JObject
                {
                    ["instanceId"] = go.GetMcpId(), ["name"] = go.name, ["path"] = GetGameObjectPath(t),
                    ["active"] = go.activeInHierarchy, ["activeInHierarchy"] = go.activeInHierarchy,
                    ["layer"] = LayerMask.LayerToName(go.layer), ["tag"] = go.tag
                });
            }
            return new JObject { ["count"] = results.Count, ["capped"] = capped, ["results"] = results };
        }

        private static Type FindType(string typeName)
        {
            foreach (var asm in I1rr.UnityMcp.Shared.UnityCompatibility.GetLoadedAssemblies())
            {
                try
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if ((t.Name == typeName || t.FullName == typeName) && typeof(Component).IsAssignableFrom(t))
                            return t;
                    }
                }
                catch (System.Reflection.ReflectionTypeLoadException) { }
            }
            return null;
        }

        #endregion

        #region runtime_get_transform

        [McpTool("runtime_get_transform",
            "Get world and local position, rotation, and scale of a GameObject by path.",
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""path"": { ""type"": ""string"", ""description"": ""Hierarchy path to the GameObject (e.g. 'Canvas/Panel/Button')"" }
                },
                ""required"": [""path""]
            }")]
        [Preserve]
        public static JObject GetTransform(JObject parameters)
        {
            var path = parameters["path"]?.Value<string>();
            if (string.IsNullOrEmpty(path)) return Error("'path' is required.");

            var go = FindByPath(path);
            if (go == null) return Error($"GameObject not found at path: {path}");

            var t = go.transform;
            return new JObject
            {
                ["instanceId"] = go.GetMcpId(),
                ["name"] = go.name,
                ["path"] = path,
                ["world"] = new JObject
                {
                    ["position"] = new JObject { ["x"] = t.position.x, ["y"] = t.position.y, ["z"] = t.position.z },
                    ["rotation"] = new JObject { ["x"] = t.eulerAngles.x, ["y"] = t.eulerAngles.y, ["z"] = t.eulerAngles.z },
                    ["scale"] = new JObject { ["x"] = t.lossyScale.x, ["y"] = t.lossyScale.y, ["z"] = t.lossyScale.z }
                },
                ["local"] = new JObject
                {
                    ["position"] = new JObject { ["x"] = t.localPosition.x, ["y"] = t.localPosition.y, ["z"] = t.localPosition.z },
                    ["rotation"] = new JObject { ["x"] = t.localEulerAngles.x, ["y"] = t.localEulerAngles.y, ["z"] = t.localEulerAngles.z },
                    ["scale"] = new JObject { ["x"] = t.localScale.x, ["y"] = t.localScale.y, ["z"] = t.localScale.z }
                }
            };
        }

        #endregion
    }
}
