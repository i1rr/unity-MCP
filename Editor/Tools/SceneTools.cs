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
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace I1rr.UnityMcp.Editor
{
    [McpToolCategory("Scene")]
    public static class SceneTools
    {
        private const int MaxPageSize = 500;
        private const int DefaultPageSize = 50;

        #region get_hierarchy

        [McpTool("get_hierarchy",
            "Paged DFS traversal of the scene hierarchy. Returns flat rows with depth metadata. Cursor = instanceId of last emitted GO.",
            AutoRoute = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""scene"": { ""type"": ""string"", ""description"": ""Scene name or path. If omitted, uses the active scene."" },
                    ""page_size"": { ""type"": ""integer"", ""description"": ""Max items per page (1-500, default 50)"", ""default"": 50 },
                    ""cursor"": { ""type"": ""integer"", ""description"": ""instanceId of the last emitted GO from previous page. Omit for first page."" },
                    ""depth"": { ""type"": ""integer"", ""description"": ""Max depth to traverse (0 = roots only). If omitted, no depth limit."" },
                    ""include_ddol"": { ""type"": ""boolean"", ""description"": ""Include DontDestroyOnLoad objects (default false)"", ""default"": false }
                }
            }")]
        public static JObject GetHierarchy(JObject parameters)
        {
            var sceneName = ToolHelper.OptionalParam<string>(parameters, "scene");
            var pageSize = Math.Clamp(ToolHelper.OptionalParam(parameters, "page_size", DefaultPageSize), 1, MaxPageSize);
            var cursorId = ToolHelper.OptionalParam(parameters, "cursor", (ObjectId)0);
            var maxDepth = parameters["depth"]?.Value<int?>();
            var includeDdol = ToolHelper.OptionalParam(parameters, "include_ddol", false);

            // Determine context
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            JObject context;
            List<GameObject> roots;

            if (prefabStage != null)
            {
                context = new JObject
                {
                    ["context"] = "prefab_stage",
                    ["prefab_path"] = prefabStage.assetPath
                };
                roots = new List<GameObject> { prefabStage.prefabContentsRoot };
            }
            else
            {
                Scene scene;
                if (!string.IsNullOrEmpty(sceneName))
                {
                    scene = FindSceneByNameOrPath(sceneName);
                    if (!scene.IsValid())
                    {
                        return ToolHelper.ErrorResult("not_found", $"Scene not found: {sceneName}");
                    }
                }
                else
                {
                    scene = SceneManager.GetActiveScene();
                }

                context = new JObject
                {
                    ["context"] = "scene",
                    ["scene_name"] = scene.name,
                    ["scene_path"] = scene.path
                };
                roots = new List<GameObject>(scene.GetRootGameObjects());

                if (includeDdol)
                {
                    var ddolRoots = GetDontDestroyOnLoadRoots();
                    if (ddolRoots.Count > 0)
                    {
                        roots.AddRange(ddolRoots);
                    }
                }
            }

            // Lazy DFS traversal — avoids materializing the full node list.
            // Stops as soon as pageSize items are emitted past the cursor.
            var stack = new Stack<(GameObject go, int depth)>();
            for (int i = roots.Count - 1; i >= 0; i--)
                stack.Push((roots[i], 0));

            bool pastCursor = (cursorId == 0);
            var items = new JArray();
            ObjectId lastEmittedId = cursorId;

            while (stack.Count > 0)
            {
                var (go, depth) = stack.Pop();
                if (go == null) continue;

                if (!pastCursor)
                {
                    if (go.GetMcpId() == cursorId)
                        pastCursor = true;

                    // Still push children to continue DFS past cursor
                    if (!maxDepth.HasValue || depth < maxDepth.Value)
                    {
                        var tc = go.transform;
                        for (int ci = tc.childCount - 1; ci >= 0; ci--)
                            stack.Push((tc.GetChild(ci).gameObject, depth + 1));
                    }
                    continue;
                }

                items.Add(new JObject
                {
                    ["instanceId"] = go.GetMcpId(),
                    ["name"] = go.name,
                    ["depth"] = depth,
                    ["active"] = go.activeSelf,
                    ["childCount"] = go.transform.childCount
                });
                lastEmittedId = go.GetMcpId();

                // Push children after emitting (DFS order)
                if (!maxDepth.HasValue || depth < maxDepth.Value)
                {
                    var tc = go.transform;
                    for (int ci = tc.childCount - 1; ci >= 0; ci--)
                        stack.Push((tc.GetChild(ci).gameObject, depth + 1));
                }

                if (items.Count >= pageSize)
                    break;
            }

            // Cursor was specified but never found — restart from beginning
            if (cursorId != 0 && !pastCursor)
            {
                // Re-run without cursor
                var retryParams = (JObject)parameters.DeepClone();
                retryParams.Remove("cursor");
                var retryResult = GetHierarchy(retryParams);
                retryResult["cursor_invalidated"] = true;
                return retryResult;
            }

            bool hasMore = stack.Count > 0;

            var result = new JObject();
            result.Merge(context);

            if (cursorId == 0)
            {
                result["total_count"] = CountHierarchy(roots, maxDepth);
            }

            result["items"] = items;
            result["cursor"] = hasMore ? lastEmittedId : (ObjectId?)null;
            result["has_more"] = hasMore;
            result["page_size"] = pageSize;

            return result;
        }

        #endregion

        #region get_hierarchy_summary

        [McpTool("get_hierarchy_summary",
            "Quick overview of the scene hierarchy. Returns per-scene stats: root count, total GameObjects, max depth, and root object names. Much faster than get_hierarchy for large scenes.",
            AutoRoute = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""scene"": { ""type"": ""string"", ""description"": ""Scene name or path. If omitted, summarizes all loaded scenes."" }
                }
            }")]
        public static JObject GetHierarchySummary(JObject parameters)
        {
            var sceneName = ToolHelper.OptionalParam<string>(parameters, "scene");

            var scenes = new JArray();
            int sceneCount = string.IsNullOrEmpty(sceneName) ? SceneManager.sceneCount : 1;

            for (int s = 0; s < sceneCount; s++)
            {
                Scene scene;
                if (!string.IsNullOrEmpty(sceneName))
                {
                    scene = FindSceneByNameOrPath(sceneName);
                    if (!scene.IsValid())
                        return ToolHelper.ErrorResult("not_found", $"Scene not found: {sceneName}");
                }
                else
                {
                    scene = SceneManager.GetSceneAt(s);
                }

                if (!scene.isLoaded) continue;

                var sceneRoots = scene.GetRootGameObjects();
                int totalCount = 0;
                int deepest = 0;
                int activeCount = 0;

                // Fast counting pass — no JObject allocation per node
                var countStack = new Stack<(Transform t, int depth)>();
                for (int i = sceneRoots.Length - 1; i >= 0; i--)
                    countStack.Push((sceneRoots[i].transform, 0));

                while (countStack.Count > 0)
                {
                    var (t, depth) = countStack.Pop();
                    totalCount++;
                    if (depth > deepest) deepest = depth;
                    if (t.gameObject.activeInHierarchy) activeCount++;

                    for (int i = t.childCount - 1; i >= 0; i--)
                        countStack.Push((t.GetChild(i), depth + 1));
                }

                var rootNames = new JArray();
                int rootLimit = Math.Min(sceneRoots.Length, 30);
                for (int i = 0; i < rootLimit; i++)
                    rootNames.Add(sceneRoots[i].name);

                scenes.Add(new JObject
                {
                    ["name"] = scene.name,
                    ["path"] = scene.path,
                    ["isActive"] = scene == SceneManager.GetActiveScene(),
                    ["rootCount"] = sceneRoots.Length,
                    ["totalGameObjects"] = totalCount,
                    ["activeGameObjects"] = activeCount,
                    ["maxDepth"] = deepest,
                    ["rootNames"] = rootNames
                });
            }

            return new JObject
            {
                ["scenes"] = scenes,
                ["sceneCount"] = scenes.Count
            };
        }

        #endregion

        #region find_gameobjects

        [McpTool("find_gameobjects",
            "Search scene objects by name, tag, layer, or component type. Returns matching GameObjects with basic info.",
            AutoRoute = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""name"": { ""type"": ""string"", ""description"": ""Filter by name (substring match, case-insensitive)"" },
                    ""tag"": { ""type"": ""string"", ""description"": ""Filter by tag (exact match)"" },
                    ""layer"": { ""type"": ""string"", ""description"": ""Filter by layer name (exact match)"" },
                    ""component_type"": { ""type"": ""string"", ""description"": ""Filter by component type name (exact match)"" },
                    ""inactive"": { ""type"": ""boolean"", ""description"": ""Include inactive GameObjects (default true)"", ""default"": true }
                }
            }")]
        public static JObject FindGameObjects(JObject parameters)
        {
            var nameFilter = parameters["name"]?.Value<string>();
            var tagFilter = parameters["tag"]?.Value<string>();
            var layerFilter = parameters["layer"]?.Value<string>();
            var componentFilter = parameters["component_type"]?.Value<string>();
            var includeInactive = parameters["inactive"]?.Value<bool>() ?? true;

            // Use FindObjectsByType to include inactive if requested
            var allTransforms = includeInactive
                ? Resources.FindObjectsOfTypeAll<Transform>()
                : UnityCompatibility.FindObjectsByType<Transform>();

            int? layerIndex = null;
            if (!string.IsNullOrEmpty(layerFilter))
            {
                layerIndex = LayerMask.NameToLayer(layerFilter);
                if (layerIndex == -1)
                {
                    var err = McpError.Create(McpErrorCodes.NotFound, $"Unknown layer: {layerFilter}");
                    err["results"] = new JArray();
                    return err;
                }
            }

            var results = new JArray();
            int maxResults = MaxPageSize;

            foreach (var t in allTransforms)
            {
                if (results.Count >= maxResults) break;

                var go = t.gameObject;

                // Skip non-scene objects (assets, editor hidden objects)
                if (go.scene.name == null || string.IsNullOrEmpty(go.scene.name))
                    continue;

                if (!string.IsNullOrEmpty(nameFilter) &&
                    go.name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (!string.IsNullOrEmpty(tagFilter))
                {
                    try
                    {
                        if (!go.CompareTag(tagFilter))
                            continue;
                    }
                    catch
                    {
                        // CompareTag throws if tag doesn't exist
                        continue;
                    }
                }

                if (layerIndex.HasValue && go.layer != layerIndex.Value)
                    continue;

                if (!string.IsNullOrEmpty(componentFilter))
                {
                    bool hasComponent = false;
                    foreach (var c in go.GetComponents<Component>())
                    {
                        if (c != null && c.GetType().Name == componentFilter)
                        {
                            hasComponent = true;
                            break;
                        }
                    }
                    if (!hasComponent) continue;
                }

                results.Add(new JObject
                {
                    ["instanceId"] = go.GetMcpId(),
                    ["name"] = go.name,
                    ["path"] = GetGameObjectPath(go),
                    ["active"] = go.activeSelf,
                    ["activeInHierarchy"] = go.activeInHierarchy,
                    ["layer"] = LayerMask.LayerToName(go.layer),
                    ["tag"] = go.tag
                });
            }

            return new JObject
            {
                ["count"] = results.Count,
                ["capped"] = results.Count >= maxResults,
                ["results"] = results
            };
        }

        #endregion

        #region get_gameobject

        [McpTool("get_gameobject",
            "Get full detail of a single GameObject: transform, active state, layer, tag, components, and children.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""instanceId"": { ""type"": ""integer"", ""description"": ""Instance ID of the GameObject"" },
                    ""path"": { ""type"": ""string"", ""description"": ""Hierarchy path (e.g. 'Canvas/Panel/Button'). Used if instanceId not provided."" }
                }
            }")]
        public static JObject GetGameObject(JObject parameters)
        {
            var go = ResolveGameObject(parameters);
            if (go == null)
            {
                return McpError.Create(McpErrorCodes.NotFound, "GameObject not found");
            }

            var t = go.transform;

            var components = new JArray();
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null)
                {
                    components.Add(new JObject { ["type"] = "(Missing Script)" });
                    continue;
                }
                components.Add(new JObject
                {
                    ["type"] = c.GetType().Name,
                    ["fullType"] = c.GetType().FullName,
                    ["enabled"] = c is Behaviour b ? b.enabled : true
                });
            }

            var children = new JArray();
            for (int i = 0; i < t.childCount; i++)
            {
                var child = t.GetChild(i).gameObject;
                children.Add(new JObject
                {
                    ["instanceId"] = child.GetMcpId(),
                    ["name"] = child.name,
                    ["active"] = child.activeSelf
                });
            }

            return new JObject
            {
                ["instanceId"] = go.GetMcpId(),
                ["name"] = go.name,
                ["path"] = GetGameObjectPath(go),
                ["active"] = go.activeSelf,
                ["activeInHierarchy"] = go.activeInHierarchy,
                ["isStatic"] = go.isStatic,
                ["layer"] = LayerMask.LayerToName(go.layer),
                ["tag"] = go.tag,
                ["scene"] = go.scene.name,
                ["transform"] = new JObject
                {
                    ["localPosition"] = Vec3ToJObject(t.localPosition),
                    ["localRotation"] = Vec3ToJObject(t.localEulerAngles),
                    ["localScale"] = Vec3ToJObject(t.localScale),
                    ["worldPosition"] = Vec3ToJObject(t.position),
                    ["worldRotation"] = Vec3ToJObject(t.eulerAngles)
                },
                ["components"] = components,
                ["children"] = children,
                ["childCount"] = t.childCount
            };
        }

        #endregion

        #region create_gameobject

        [McpTool("create_gameobject",
            "Create a new GameObject with optional parent and components. Supports Undo.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""name"": { ""type"": ""string"", ""description"": ""Name of the new GameObject"" },
                    ""parent"": { ""type"": ""string"", ""description"": ""Parent path or instanceId (as string). If omitted, creates at root."" },
                    ""components"": {
                        ""type"": ""array"",
                        ""items"": { ""type"": ""string"" },
                        ""description"": ""Component type names to add (e.g. ['MeshRenderer', 'BoxCollider'])""
                    }
                },
                ""required"": [""name""]
            }")]
        public static JObject CreateGameObject(JObject parameters)
        {
            var name = parameters["name"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(name))
            {
                return McpError.Create(McpErrorCodes.MissingParameter, "name is required", "Provide a 'name' for the new GameObject.");
            }

            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, $"Create {name}");

            // Set parent
            var parentParam = parameters["parent"]?.Value<string>();
            if (!string.IsNullOrEmpty(parentParam))
            {
                Transform parentTransform = null;

                if (ObjectId.TryParse(parentParam, out ObjectId parentId))
                {
                    var parentGo = ObjectIdentity.Resolve(parentId) as GameObject;
                    parentTransform = parentGo != null ? parentGo.transform : null;
                }
                else
                {
                    var parentGo = GameObject.Find(parentParam);
                    parentTransform = parentGo != null ? parentGo.transform : null;
                }

                if (parentTransform == null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                    return McpError.Create(McpErrorCodes.NotFound, $"Parent not found: {parentParam}");
                }

                Undo.SetTransformParent(go.transform, parentTransform, $"Set parent of {name}");
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;
            }

            // Add components
            var componentErrors = new JArray();
            var componentsParam = parameters["components"] as JArray;
            if (componentsParam != null)
            {
                foreach (var comp in componentsParam)
                {
                    var typeName = comp.Value<string>();
                    if (string.IsNullOrWhiteSpace(typeName)) continue;

                    var type = FindComponentType(typeName);
                    if (type == null)
                    {
                        componentErrors.Add($"Type not found: {typeName}");
                        continue;
                    }

                    Undo.AddComponent(go, type);
                }
            }

            var result = new JObject
            {
                ["instanceId"] = go.GetMcpId(),
                ["name"] = go.name,
                ["path"] = GetGameObjectPath(go)
            };

            if (componentErrors.Count > 0)
            {
                result["component_errors"] = componentErrors;
            }

            return result;
        }

        #endregion

        #region destroy_gameobject

        [McpTool("destroy_gameobject",
            "Destroy a GameObject. Supports Undo.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""instanceId"": { ""type"": ""integer"", ""description"": ""Instance ID of the GameObject to destroy"" },
                    ""path"": { ""type"": ""string"", ""description"": ""Hierarchy path of the GameObject. Used if instanceId not provided."" }
                }
            }")]
        public static JObject DestroyGameObject(JObject parameters)
        {
            var go = ResolveGameObject(parameters);
            if (go == null)
            {
                return McpError.Create(McpErrorCodes.NotFound, "GameObject not found");
            }

            var name = go.name;
            var instanceId = go.GetMcpId();

            Undo.DestroyObjectImmediate(go);

            return new JObject
            {
                ["status"] = "destroyed",
                ["name"] = name,
                ["instanceId"] = instanceId
            };
        }

        #endregion

        #region set_transform

        [McpTool("set_transform",
            "Set position, rotation, and/or scale of a GameObject's transform. Supports Undo.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""instanceId"": { ""type"": ""integer"", ""description"": ""Instance ID of the target GameObject"" },
                    ""position"": {
                        ""type"": ""object"",
                        ""properties"": { ""x"": {""type"":""number""}, ""y"": {""type"":""number""}, ""z"": {""type"":""number""} },
                        ""description"": ""New position (x, y, z)""
                    },
                    ""rotation"": {
                        ""type"": ""object"",
                        ""properties"": { ""x"": {""type"":""number""}, ""y"": {""type"":""number""}, ""z"": {""type"":""number""} },
                        ""description"": ""New rotation in euler angles (x, y, z)""
                    },
                    ""scale"": {
                        ""type"": ""object"",
                        ""properties"": { ""x"": {""type"":""number""}, ""y"": {""type"":""number""}, ""z"": {""type"":""number""} },
                        ""description"": ""New scale (x, y, z)""
                    },
                    ""space"": { ""type"": ""string"", ""enum"": [""World"", ""Local""], ""description"": ""Coordinate space (default: Local)"", ""default"": ""Local"" }
                },
                ""required"": [""instanceId""]
            }")]
        public static JObject SetTransform(JObject parameters)
        {
            var instanceId = parameters["instanceId"]?.ToObject<ObjectId>() ?? 0;
            if (instanceId == 0)
            {
                return McpError.Create(McpErrorCodes.MissingParameter, "instanceId is required", "Provide the 'instanceId' parameter.");
            }

            var go = ObjectIdentity.Resolve(instanceId) as GameObject;
            if (go == null)
            {
                return McpError.Create(McpErrorCodes.NotFound, $"GameObject not found with instanceId: {instanceId}");
            }

            var t = go.transform;
            var space = parameters["space"]?.Value<string>() ?? "Local";
            bool isWorld = string.Equals(space, "World", StringComparison.OrdinalIgnoreCase);

            Undo.RecordObject(t, $"Set Transform on {go.name}");

            var posParam = parameters["position"] as JObject;
            if (posParam != null)
            {
                var pos = JObjectToVec3(posParam);
                if (isWorld)
                    t.position = pos;
                else
                    t.localPosition = pos;
            }

            var rotParam = parameters["rotation"] as JObject;
            if (rotParam != null)
            {
                var rot = JObjectToVec3(rotParam);
                if (isWorld)
                    t.eulerAngles = rot;
                else
                    t.localEulerAngles = rot;
            }

            var scaleParam = parameters["scale"] as JObject;
            if (scaleParam != null)
            {
                // Scale is always local
                t.localScale = JObjectToVec3(scaleParam);
            }

            EditorUtility.SetDirty(go);

            return new JObject
            {
                ["instanceId"] = instanceId,
                ["name"] = go.name,
                ["transform"] = new JObject
                {
                    ["localPosition"] = Vec3ToJObject(t.localPosition),
                    ["localRotation"] = Vec3ToJObject(t.localEulerAngles),
                    ["localScale"] = Vec3ToJObject(t.localScale),
                    ["worldPosition"] = Vec3ToJObject(t.position),
                    ["worldRotation"] = Vec3ToJObject(t.eulerAngles)
                }
            };
        }

        #endregion

        #region set_active

        [McpTool("set_active",
            "Enable or disable a GameObject. Supports Undo.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""instanceId"": { ""type"": ""integer"", ""description"": ""Instance ID of the target GameObject"" },
                    ""active"": { ""type"": ""boolean"", ""description"": ""Whether to activate (true) or deactivate (false) the GameObject"" }
                },
                ""required"": [""instanceId"", ""active""]
            }")]
        public static JObject SetActive(JObject parameters)
        {
            var instanceId = parameters["instanceId"]?.ToObject<ObjectId>() ?? 0;
            if (instanceId == 0)
            {
                return McpError.Create(McpErrorCodes.MissingParameter, "instanceId is required", "Provide the 'instanceId' parameter.");
            }

            var active = parameters["active"]?.Value<bool>();
            if (!active.HasValue)
            {
                return McpError.Create(McpErrorCodes.MissingParameter, "active (boolean) is required", "Provide the 'active' parameter as true or false.");
            }

            var go = ObjectIdentity.Resolve(instanceId) as GameObject;
            if (go == null)
            {
                return McpError.Create(McpErrorCodes.NotFound, $"GameObject not found with instanceId: {instanceId}");
            }

            Undo.RecordObject(go, $"Set Active {go.name}");
            go.SetActive(active.Value);
            EditorUtility.SetDirty(go);

            return new JObject
            {
                ["instanceId"] = instanceId,
                ["name"] = go.name,
                ["active"] = go.activeSelf,
                ["activeInHierarchy"] = go.activeInHierarchy
            };
        }

        #endregion

        #region get_loaded_scenes

        [McpTool("get_loaded_scenes", "Returns all currently loaded scenes with paths, names, and states.")]
        public static JObject GetLoadedScenes(JObject parameters)
        {
            var scenes = new JArray();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                scenes.Add(new JObject
                {
                    ["name"] = scene.name,
                    ["path"] = scene.path,
                    ["buildIndex"] = scene.buildIndex,
                    ["isLoaded"] = scene.isLoaded,
                    ["isDirty"] = scene.isDirty,
                    ["isActive"] = scene == SceneManager.GetActiveScene(),
                    ["rootCount"] = scene.rootCount
                });
            }

            return new JObject
            {
                ["count"] = scenes.Count,
                ["scenes"] = scenes
            };
        }

        #endregion

        #region load_scene

        [McpTool("load_scene",
            "Load a scene in the editor. WARNING: mode 'Single' will close all other open scenes.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""path"": { ""type"": ""string"", ""description"": ""Scene asset path (e.g. 'Assets/Scenes/Main.unity')"" },
                    ""mode"": { ""type"": ""string"", ""enum"": [""Single"", ""Additive""], ""description"": ""Load mode (default: Single)"", ""default"": ""Single"" }
                },
                ""required"": [""path""]
            }")]
        public static JObject LoadScene(JObject parameters)
        {
            var path = parameters["path"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(path))
            {
                return McpError.Create(McpErrorCodes.MissingParameter, "path is required", "Provide the 'path' to the scene asset.");
            }

            // Validate scene asset exists
            var sceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(path);
            if (sceneAsset == null)
            {
                return McpError.Create(McpErrorCodes.NotFound, $"Scene asset not found at path: {path}");
            }

            var modeStr = parameters["mode"]?.Value<string>() ?? "Single";
            var mode = string.Equals(modeStr, "Additive", StringComparison.OrdinalIgnoreCase)
                ? OpenSceneMode.Additive
                : OpenSceneMode.Single;

            // Check for unsaved changes in Single mode
            if (mode == OpenSceneMode.Single)
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var s = SceneManager.GetSceneAt(i);
                    if (s.isDirty)
                    {
                        bool save = EditorSceneManager.SaveModifiedScenesIfUserWantsTo(
                            new[] { s });
                        if (!save)
                        {
                            return new JObject
                            {
                                ["status"] = "cancelled",
                                ["reason"] = "User cancelled due to unsaved changes"
                            };
                        }
                        break;
                    }
                }
            }

            var scene = EditorSceneManager.OpenScene(path, mode);

            return new JObject
            {
                ["status"] = "loaded",
                ["name"] = scene.name,
                ["path"] = scene.path,
                ["mode"] = modeStr,
                ["rootCount"] = scene.rootCount
            };
        }

        #endregion

        #region save_scene

        [McpTool("save_scene",
            "Save the current scene (or a specified scene) to disk.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""path"": { ""type"": ""string"", ""description"": ""Scene asset path to save. If omitted, saves the active scene."" }
                }
            }")]
        public static JObject SaveScene(JObject parameters)
        {
            var path = parameters["path"]?.Value<string>();
            Scene scene;

            if (!string.IsNullOrEmpty(path))
            {
                scene = FindSceneByNameOrPath(path);
                if (!scene.IsValid())
                {
                    return McpError.Create(McpErrorCodes.NotFound, $"Scene not found or not loaded: {path}");
                }
            }
            else
            {
                scene = SceneManager.GetActiveScene();
            }

            bool success = EditorSceneManager.SaveScene(scene);

            return new JObject
            {
                ["status"] = success ? "saved" : "failed",
                ["name"] = scene.name,
                ["path"] = scene.path
            };
        }

        #endregion

        #region Helpers

        private static GameObject ResolveGameObject(JObject parameters)
        {
            var instanceId = parameters["instanceId"]?.ToObject<ObjectId?>();
            if (instanceId.HasValue && instanceId.Value != 0)
            {
                return ObjectIdentity.Resolve(instanceId.Value) as GameObject;
            }

            var path = parameters["path"]?.Value<string>();
            if (!string.IsNullOrEmpty(path))
            {
                return GameObject.Find(path);
            }

            return null;
        }

        private static Scene FindSceneByNameOrPath(string nameOrPath)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.path == nameOrPath || scene.name == nameOrPath)
                {
                    return scene;
                }
            }
            return default;
        }

        private static string GetGameObjectPath(GameObject go)
        {
            var parts = new List<string>();
            var current = go.transform;
            while (current != null)
            {
                parts.Add(current.name);
                current = current.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static List<GameObject> GetDontDestroyOnLoadRoots()
        {
            var results = new List<GameObject>();

            // DontDestroyOnLoad objects live in a hidden scene. Access via a temporary GO.
            // This only works in Play Mode.
            if (!Application.isPlaying)
                return results;

            var temp = new GameObject("__mcp_ddol_probe__");
            UnityEngine.Object.DontDestroyOnLoad(temp);

            var ddolScene = temp.scene;
            if (ddolScene.IsValid())
            {
                var roots = ddolScene.GetRootGameObjects();
                foreach (var root in roots)
                {
                    if (root != temp)
                    {
                        results.Add(root);
                    }
                }
            }

            UnityEngine.Object.DestroyImmediate(temp);
            return results;
        }

        private static Type FindComponentType(string typeName)
        {
            // Search all loaded assemblies for the type
            foreach (var assembly in I1rr.UnityMcp.Shared.UnityCompatibility.GetLoadedAssemblies())
            {
                // Try exact name first
                var type = assembly.GetType(typeName, false, true);
                if (type != null && typeof(Component).IsAssignableFrom(type))
                    return type;
            }

            // Try searching by simple name
            foreach (var assembly in I1rr.UnityMcp.Shared.UnityCompatibility.GetLoadedAssemblies())
            {
                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (type.Name == typeName && typeof(Component).IsAssignableFrom(type))
                            return type;
                    }
                }
                catch (System.Reflection.ReflectionTypeLoadException)
                {
                    // Skip assemblies that fail to enumerate
                }
            }

            return null;
        }

        private static JObject Vec3ToJObject(Vector3 v)
        {
            return new JObject
            {
                ["x"] = Math.Round(v.x, 4),
                ["y"] = Math.Round(v.y, 4),
                ["z"] = Math.Round(v.z, 4)
            };
        }

        private static Vector3 JObjectToVec3(JObject obj)
        {
            return new Vector3(
                obj["x"]?.Value<float>() ?? 0f,
                obj["y"]?.Value<float>() ?? 0f,
                obj["z"]?.Value<float>() ?? 0f
            );
        }

        private static int CountHierarchy(List<GameObject> roots, int? maxDepth)
        {
            int count = 0;
            var stack = new Stack<(Transform t, int depth)>();
            for (int i = roots.Count - 1; i >= 0; i--)
            {
                if (roots[i] != null)
                    stack.Push((roots[i].transform, 0));
            }
            while (stack.Count > 0)
            {
                var (t, depth) = stack.Pop();
                count++;
                if (!maxDepth.HasValue || depth < maxDepth.Value)
                {
                    for (int ci = t.childCount - 1; ci >= 0; ci--)
                        stack.Push((t.GetChild(ci), depth + 1));
                }
            }
            return count;
        }

        #endregion
    }
}
