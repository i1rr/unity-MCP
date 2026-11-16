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

namespace I1rr.UnityMcp.Editor
{
    [McpToolCategory("Scene")]
    public static class PrefabTools
    {
        #region get_prefab_info

        [McpTool("get_prefab_info",
            "Returns prefab status, asset type, base prefab path, and variant chain for a given asset path or scene instance.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""path"": { ""type"": ""string"", ""description"": ""Asset path to the prefab (e.g. 'Assets/Prefabs/Player.prefab'), or omit and use instanceId for a scene instance."" },
                    ""instanceId"": { ""type"": ""integer"", ""description"": ""Instance ID of a prefab instance in the scene. Takes precedence over path."" }
                }
            }")]
        public static JObject GetPrefabInfo(JObject parameters)
        {
            var path = parameters["path"]?.Value<string>();
            var instanceId = parameters["instanceId"]?.ToObject<ObjectId?>();

            GameObject target;

            if (instanceId.HasValue)
            {
                target = ObjectIdentity.Resolve(instanceId.Value) as GameObject;
                if (target == null)
                    return Error($"GameObject not found with instanceId: {instanceId.Value}");
            }
            else if (!string.IsNullOrEmpty(path))
            {
                target = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (target == null)
                    return Error($"Prefab not found at path: {path}");
            }
            else
            {
                return Error("Either 'path' or 'instanceId' must be provided.");
            }

            var assetType = PrefabUtility.GetPrefabAssetType(target);
            var instanceStatus = PrefabUtility.GetPrefabInstanceStatus(target);
            var assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(target);

            var result = new JObject
            {
                ["name"] = target.name,
                ["asset_type"] = assetType.ToString(),
                ["instance_status"] = instanceStatus.ToString(),
                ["asset_path"] = string.IsNullOrEmpty(assetPath) ? null : assetPath
            };

            if (instanceId.HasValue)
            {
                result["instanceId"] = instanceId.Value;
                var correspondingAsset = PrefabUtility.GetCorrespondingObjectFromSource(target);
                if (correspondingAsset != null)
                    result["source_prefab"] = AssetDatabase.GetAssetPath(correspondingAsset);
            }

            // Build variant chain
            var variantChain = new JArray();
            var current = string.IsNullOrEmpty(assetPath) ? path : assetPath;

            if (!string.IsNullOrEmpty(current))
            {
                var visited = new HashSet<string>();
                while (!string.IsNullOrEmpty(current) && visited.Add(current))
                {
                    var asset = AssetDatabase.LoadAssetAtPath<GameObject>(current);
                    if (asset == null) break;

                    var isVariant = PrefabUtility.GetPrefabAssetType(asset) == PrefabAssetType.Variant;
                    variantChain.Add(new JObject
                    {
                        ["path"] = current,
                        ["is_variant"] = isVariant
                    });

                    if (!isVariant) break;

                    var baseAsset = PrefabUtility.GetCorrespondingObjectFromSource(asset);
                    current = baseAsset != null ? AssetDatabase.GetAssetPath(baseAsset) : null;
                }
            }

            if (variantChain.Count > 0)
                result["variant_chain"] = variantChain;

            return result;
        }

        #endregion

        #region get_prefab_overrides

        [McpTool("get_prefab_overrides",
            "Returns all overrides on a prefab instance: property modifications, added/removed components, added/removed GameObjects. Supports filtering by null references or audio-related overrides.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""instanceId"": { ""type"": ""integer"", ""description"": ""Instance ID of the prefab instance in the scene"" },
                    ""gameobject"": { ""type"": ""string"", ""description"": ""Hierarchy path to the prefab instance root (alternative to instanceId)"" },
                    ""filter"": { ""type"": ""string"", ""enum"": [""all"", ""null_references"", ""audio""], ""description"": ""Filter which overrides to report. Default: all"" }
                }
            }")]
        public static JObject GetPrefabOverrides(JObject parameters)
        {
            var instanceId = parameters["instanceId"]?.ToObject<ObjectId?>() ?? null;
            var goPath = parameters["gameobject"]?.Value<string>();
            var filter = parameters["filter"]?.Value<string>() ?? "all";

            GameObject go = null;

            if (instanceId.HasValue && instanceId.Value != 0)
            {
                go = ObjectIdentity.Resolve(instanceId.Value) as GameObject;
            }
            else if (!string.IsNullOrEmpty(goPath))
            {
                go = GameObject.Find(goPath);
                if (go == null)
                {
                    // Try searching through scene roots
                    var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                    if (activeScene.IsValid())
                    {
                        foreach (var root in activeScene.GetRootGameObjects())
                        {
                            if (root.name == goPath) { go = root; break; }
                            var t = root.transform.Find(goPath.Contains("/") ? goPath.Substring(goPath.IndexOf('/') + 1) : goPath);
                            if (t != null && GetHierarchyPath(t) == goPath) { go = t.gameObject; break; }
                        }
                    }
                }
            }

            if (go == null)
                return Error(instanceId.HasValue
                    ? $"GameObject not found with instanceId: {instanceId}"
                    : $"GameObject not found at path: {goPath}");

            if (PrefabUtility.GetPrefabInstanceStatus(go) == PrefabInstanceStatus.NotAPrefab)
                return Error("The specified GameObject is not a prefab instance.");

            var outermost = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            if (outermost == null)
                outermost = go;

            // Property modifications
            var modifications = PrefabUtility.GetPropertyModifications(outermost);
            var modsArray = new JArray();
            var suspiciousNulls = new JArray();
            if (modifications != null)
            {
                foreach (var mod in modifications)
                {
                    if (mod.target == null) continue;

                    bool isNullRef = mod.objectReference == null && mod.value == ""
                        && mod.propertyPath.Contains("Reference");
                    bool isAudioPath = mod.target is AudioSource || mod.target is AudioListener
                        || mod.propertyPath.IndexOf("audio", StringComparison.OrdinalIgnoreCase) >= 0
                        || mod.target.GetType().Name.StartsWith("Ak", StringComparison.Ordinal);

                    // Apply filter
                    if (filter == "null_references" && !isNullRef) continue;
                    if (filter == "audio" && !isAudioPath) continue;

                    var entry = new JObject
                    {
                        ["target"] = mod.target.name,
                        ["target_type"] = mod.target.GetType().Name,
                        ["property_path"] = mod.propertyPath,
                        ["value"] = mod.value,
                        ["object_reference"] = mod.objectReference != null
                            ? mod.objectReference.name
                            : null
                    };

                    if (isNullRef)
                        entry["suspicious_null"] = true;

                    modsArray.Add(entry);

                    if (isNullRef)
                        suspiciousNulls.Add(entry.DeepClone());
                }
            }

            // Added components
            var addedComponents = PrefabUtility.GetAddedComponents(outermost);
            var addedCompsArray = new JArray();
            foreach (var added in addedComponents)
            {
                addedCompsArray.Add(new JObject
                {
                    ["component_type"] = added.instanceComponent.GetType().FullName,
                    ["gameobject"] = added.instanceComponent.gameObject.name,
                    ["instanceId"] = added.instanceComponent.GetMcpId()
                });
            }

            // Removed components
            var removedComponents = PrefabUtility.GetRemovedComponents(outermost);
            var removedCompsArray = new JArray();
            foreach (var removed in removedComponents)
            {
                removedCompsArray.Add(new JObject
                {
                    ["component_type"] = removed.assetComponent?.GetType().FullName,
                    ["gameobject"] = removed.containingInstanceGameObject?.name
                });
            }

            // Added GameObjects
            var addedGameObjects = PrefabUtility.GetAddedGameObjects(outermost);
            var addedGosArray = new JArray();
            foreach (var added in addedGameObjects)
            {
                addedGosArray.Add(new JObject
                {
                    ["name"] = added.instanceGameObject.name,
                    ["instanceId"] = added.instanceGameObject.GetMcpId(),
                    ["parent"] = added.instanceGameObject.transform.parent?.name
                });
            }

            // Removed GameObjects
            var removedGameObjects = PrefabUtility.GetRemovedGameObjects(outermost);
            var removedGosArray = new JArray();
            foreach (var removed in removedGameObjects)
            {
                removedGosArray.Add(new JObject
                {
                    ["name"] = removed.assetGameObject?.name,
                    ["parent"] = removed.parentOfRemovedGameObjectInInstance?.name
                });
            }

            return new JObject
            {
                ["instanceId"] = outermost.GetMcpId(),
                ["name"] = outermost.name,
                ["filter"] = filter,
                ["property_modifications"] = modsArray,
                ["suspicious_null_overrides"] = suspiciousNulls,
                ["added_components"] = addedCompsArray,
                ["removed_components"] = removedCompsArray,
                ["added_gameobjects"] = addedGosArray,
                ["removed_gameobjects"] = removedGosArray
            };
        }

        #endregion

        #region apply_prefab_override

        [McpTool("apply_prefab_override",
            "Apply a specific property override from a prefab instance to its base prefab asset.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""instanceId"": { ""type"": ""integer"", ""description"": ""Instance ID of the prefab instance GameObject"" },
                    ""property_path"": { ""type"": ""string"", ""description"": ""SerializedProperty path to apply (e.g. 'm_LocalPosition.x'). If omitted, applies all overrides."" }
                },
                ""required"": [""instanceId""]
            }")]
        public static JObject ApplyPrefabOverride(JObject parameters)
        {
            var instanceId = parameters["instanceId"]?.ToObject<ObjectId>() ?? 0;
            var propertyPath = parameters["property_path"]?.Value<string>();

            var go = ObjectIdentity.Resolve(instanceId) as GameObject;
            if (go == null)
                return Error($"GameObject not found with instanceId: {instanceId}");

            if (PrefabUtility.GetPrefabInstanceStatus(go) == PrefabInstanceStatus.NotAPrefab)
                return Error("The specified GameObject is not a prefab instance.");

            var assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
            if (string.IsNullOrEmpty(assetPath))
                return Error("Could not determine prefab asset path.");

            if (string.IsNullOrEmpty(propertyPath))
            {
                // Apply all overrides
                PrefabUtility.ApplyPrefabInstance(go, InteractionMode.AutomatedAction);
                return new JObject
                {
                    ["success"] = true,
                    ["applied"] = "all",
                    ["asset_path"] = assetPath
                };
            }

            // Apply a specific property override
            var outermost = PrefabUtility.GetOutermostPrefabInstanceRoot(go) ?? go;
            var modifications = PrefabUtility.GetPropertyModifications(outermost);
            if (modifications == null)
                return Error("No property modifications found on this instance.");

            var matching = modifications.Where(m => m.propertyPath == propertyPath).ToArray();
            if (matching.Length == 0)
                return Error($"No override found for property path: {propertyPath}");

            // Find the component/object with the override and apply via SerializedObject
            foreach (var mod in matching)
            {
                if (mod.target == null) continue;

                var instanceComponent = FindInstanceObjectForModification(outermost, mod);
                if (instanceComponent == null) continue;

                var so = new SerializedObject(instanceComponent);
                var prop = so.FindProperty(propertyPath);
                if (prop != null)
                {
                    PrefabUtility.ApplyPropertyOverride(prop, assetPath, InteractionMode.AutomatedAction);
                }
                so.Dispose();
            }

            return new JObject
            {
                ["success"] = true,
                ["applied"] = propertyPath,
                ["asset_path"] = assetPath
            };
        }

        #endregion

        #region revert_prefab_override

        [McpTool("revert_prefab_override",
            "Revert a specific property override on a prefab instance back to the base prefab value.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""instanceId"": { ""type"": ""integer"", ""description"": ""Instance ID of the prefab instance GameObject"" },
                    ""property_path"": { ""type"": ""string"", ""description"": ""SerializedProperty path to revert (e.g. 'm_LocalPosition.x'). If omitted, reverts all overrides."" }
                },
                ""required"": [""instanceId""]
            }")]
        public static JObject RevertPrefabOverride(JObject parameters)
        {
            var instanceId = parameters["instanceId"]?.ToObject<ObjectId>() ?? 0;
            var propertyPath = parameters["property_path"]?.Value<string>();

            var go = ObjectIdentity.Resolve(instanceId) as GameObject;
            if (go == null)
                return Error($"GameObject not found with instanceId: {instanceId}");

            if (PrefabUtility.GetPrefabInstanceStatus(go) == PrefabInstanceStatus.NotAPrefab)
                return Error("The specified GameObject is not a prefab instance.");

            if (string.IsNullOrEmpty(propertyPath))
            {
                // Revert all overrides
                PrefabUtility.RevertPrefabInstance(go, InteractionMode.AutomatedAction);
                return new JObject
                {
                    ["success"] = true,
                    ["reverted"] = "all"
                };
            }

            // Revert a specific property override
            var outermost = PrefabUtility.GetOutermostPrefabInstanceRoot(go) ?? go;
            var modifications = PrefabUtility.GetPropertyModifications(outermost);
            if (modifications == null)
                return Error("No property modifications found on this instance.");

            var matching = modifications.Where(m => m.propertyPath == propertyPath).ToArray();
            if (matching.Length == 0)
                return Error($"No override found for property path: {propertyPath}");

            foreach (var mod in matching)
            {
                if (mod.target == null) continue;

                var instanceComponent = FindInstanceObjectForModification(outermost, mod);
                if (instanceComponent == null) continue;

                var so = new SerializedObject(instanceComponent);
                var prop = so.FindProperty(propertyPath);
                if (prop != null)
                {
                    PrefabUtility.RevertPropertyOverride(prop, InteractionMode.AutomatedAction);
                }
                so.Dispose();
            }

            return new JObject
            {
                ["success"] = true,
                ["reverted"] = propertyPath
            };
        }

        #endregion

        #region open_prefab

        [McpTool("open_prefab",
            "Enter prefab editing mode (prefab stage) for the specified prefab asset. Warning: get_hierarchy will return the prefab contents while in prefab stage.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""path"": { ""type"": ""string"", ""description"": ""Asset path to the prefab (e.g. 'Assets/Prefabs/Player.prefab')"" }
                },
                ""required"": [""path""]
            }")]
        public static JObject OpenPrefab(JObject parameters)
        {
            var path = parameters["path"]?.Value<string>();
            if (string.IsNullOrEmpty(path))
                return Error("'path' is required.");

            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null)
                return Error($"Prefab not found at path: {path}");

            var assetType = PrefabUtility.GetPrefabAssetType(asset);
            if (assetType == PrefabAssetType.NotAPrefab)
                return Error($"Asset at '{path}' is not a prefab.");

            var stage = PrefabStageUtility.OpenPrefab(path);
            if (stage == null)
                return Error($"Failed to open prefab stage for: {path}");

            return new JObject
            {
                ["success"] = true,
                ["prefab_path"] = path,
                ["root_name"] = stage.prefabContentsRoot.name,
                ["root_instanceId"] = stage.prefabContentsRoot.GetMcpId(),
                ["warning"] = "get_hierarchy now returns prefab contents. Use close_prefab to return to the scene."
            };
        }

        #endregion

        #region close_prefab

        [McpTool("close_prefab",
            "Exit prefab editing mode and return to the main stage. Optionally saves changes before exiting.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""save"": { ""type"": ""boolean"", ""description"": ""Whether to save changes before exiting (default true)"", ""default"": true }
                }
            }")]
        public static JObject ClosePrefab(JObject parameters)
        {
            var save = parameters["save"]?.Value<bool>() ?? true;

            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null)
                return Error("Not currently in prefab editing mode.");

            var prefabPath = stage.assetPath;

            if (save)
            {
                // Mark the prefab stage dirty and save
                EditorUtility.SetDirty(stage.prefabContentsRoot);
                PrefabUtility.SaveAsPrefabAsset(stage.prefabContentsRoot, prefabPath);
            }

            StageUtility.GoToMainStage();

            return new JObject
            {
                ["success"] = true,
                ["saved"] = save,
                ["prefab_path"] = prefabPath
            };
        }

        #endregion

        #region unpack_prefab

        [McpTool("unpack_prefab",
            "Unpack a prefab instance in the scene. Breaks the prefab link partially (outermost root) or completely.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""instanceId"": { ""type"": ""integer"", ""description"": ""Instance ID of the prefab instance to unpack"" },
                    ""mode"": { ""type"": ""string"", ""enum"": [""completely"", ""outermost""], ""description"": ""Unpack mode: 'completely' breaks all nested prefab links; 'outermost' only unpacks the root (default 'outermost')"", ""default"": ""outermost"" }
                },
                ""required"": [""instanceId""]
            }")]
        public static JObject UnpackPrefab(JObject parameters)
        {
            var instanceId = parameters["instanceId"]?.ToObject<ObjectId>() ?? 0;
            var modeStr = parameters["mode"]?.Value<string>() ?? "outermost";

            var go = ObjectIdentity.Resolve(instanceId) as GameObject;
            if (go == null)
                return Error($"GameObject not found with instanceId: {instanceId}");

            if (PrefabUtility.GetPrefabInstanceStatus(go) == PrefabInstanceStatus.NotAPrefab)
                return Error("The specified GameObject is not a prefab instance.");

            var unpackMode = modeStr.ToLowerInvariant() switch
            {
                "completely" => PrefabUnpackMode.Completely,
                "outermost" => PrefabUnpackMode.OutermostRoot,
                _ => PrefabUnpackMode.OutermostRoot
            };

            Undo.RegisterFullObjectHierarchyUndo(go, "Unpack Prefab");
            PrefabUtility.UnpackPrefabInstance(go, unpackMode, InteractionMode.AutomatedAction);

            return new JObject
            {
                ["success"] = true,
                ["instanceId"] = instanceId,
                ["name"] = go.name,
                ["mode"] = unpackMode.ToString()
            };
        }

        #endregion

        #region Helpers

        private static UnityEngine.Object FindInstanceObjectForModification(GameObject outermost, PropertyModification mod)
        {
            if (mod.target == null) return null;

            var targetType = mod.target.GetType();

            if (targetType == typeof(GameObject))
            {
                // Find the corresponding instance GameObject
                return FindCorrespondingInstanceObject(outermost, mod.target as GameObject);
            }

            // It's a component — find the corresponding instance component
            var allComponents = outermost.GetComponentsInChildren(targetType, true);
            var sourceObj = PrefabUtility.GetCorrespondingObjectFromSource(outermost);

            foreach (var comp in allComponents)
            {
                var source = PrefabUtility.GetCorrespondingObjectFromSource(comp);
                if (source == mod.target)
                    return comp;
            }

            return null;
        }

        private static GameObject FindCorrespondingInstanceObject(GameObject instanceRoot, GameObject assetObject)
        {
            if (assetObject == null) return null;

            var allTransforms = instanceRoot.GetComponentsInChildren<Transform>(true);
            foreach (var t in allTransforms)
            {
                var source = PrefabUtility.GetCorrespondingObjectFromSource(t.gameObject);
                if (source == assetObject)
                    return t.gameObject;
            }

            return null;
        }

        private static string GetHierarchyPath(Transform t)
        {
            var parts = new System.Collections.Generic.List<string>();
            while (t != null)
            {
                parts.Insert(0, t.name);
                t = t.parent;
            }
            return string.Join("/", parts);
        }

        private static JObject Error(string message) =>
            McpError.Create(McpErrorCodes.InternalError, message);

        #endregion
    }
}
