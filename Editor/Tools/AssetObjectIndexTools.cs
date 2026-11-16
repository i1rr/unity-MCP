using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace I1rr.UnityMcp.Editor
{
    [McpToolCategory("Asset")]
    public static class AssetObjectIndexTools
    {
        [McpTool("get_asset_object_index",
            "Map an asset's serialized local file IDs to hierarchy paths and concrete Unity component types. " +
            "Designed for annotating Unity YAML and visual diffs. localFileId is returned as a decimal string " +
            "because Unity's 64-bit IDs exceed JavaScript's safe integer range.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""assetPath"": {
                        ""type"": ""string"",
                        ""description"": ""Unity project-relative asset path (for example 'Assets/Prefabs/MyPrefab.prefab')""
                    },
                    ""name"": { ""type"": ""string"", ""description"": ""Case-insensitive substring of the hierarchy path or subasset name."" },
                    ""component_type"": { ""type"": ""string"", ""description"": ""Exact Unity type, case-insensitive (e.g. AnimationClip)."" }
                },
                ""required"": [""assetPath""]
            }")]
        public static JObject GetAssetObjectIndex(JObject parameters)
        {
            var assetPath = ToolHelper.RequireParam<string>(parameters, "assetPath", out var error);
            if (error != null)
                return error;

            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return ToolHelper.ErrorResult(McpErrorCodes.MissingParameter,
                    "assetPath is required.",
                    "Provide a project-relative path such as 'Assets/Prefabs/MyPrefab.prefab'.");
            }

            assetPath = assetPath.Trim().Replace('\\', '/');
            if (!IsProjectRelativeAssetPath(assetPath))
            {
                return ToolHelper.ErrorResult(McpErrorCodes.InvalidParameter,
                    $"assetPath must start with 'Assets/' or 'Packages/': {assetPath}",
                    "Provide a Unity project-relative asset path.");
            }

            var assetGuid = AssetDatabase.AssetPathToGUID(assetPath, AssetPathToGUIDOptions.OnlyExistingAssets);
            if (string.IsNullOrEmpty(assetGuid))
                return ToolHelper.ErrorResult(McpErrorCodes.NotFound, $"Asset not found: {assetPath}");

            var entries = new Dictionary<long, AssetObjectEntry>();

            if (assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                var sceneError = IndexScene(assetPath, assetGuid, entries);
                if (sceneError != null)
                    return sceneError;
            }
            else
            {
                var mainAsset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                if (mainAsset == null)
                    return ToolHelper.ErrorResult(McpErrorCodes.InternalError, $"Failed to load asset: {assetPath}");

                if (mainAsset is GameObject root)
                    IndexGameObjectHierarchy(root, assetGuid, entries);

                foreach (var assetObject in AssetDatabase.LoadAllAssetsAtPath(assetPath))
                {
                    if (assetObject == null)
                        continue;

                    if (assetObject is GameObject gameObject)
                    {
                        IndexGameObjectHierarchy(gameObject, assetGuid, entries);
                    }
                    else if (assetObject is Component component)
                    {
                        AddObject(component, GetHierarchyPath(component.transform), component.GetType().Name,
                            assetGuid, entries);
                    }
                    else
                    {
                        var displayPath = string.IsNullOrEmpty(assetObject.name)
                            ? Path.GetFileNameWithoutExtension(assetPath)
                            : assetObject.name;
                        AddObject(assetObject, displayPath, assetObject.GetType().Name, assetGuid, entries);
                    }
                }
            }

            string name = (string)parameters["name"] ?? "";
            string type = (string)parameters["component_type"] ?? "";
            var objects = new JArray(entries.Values
                .Where(entry => entry.HierarchyPath.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (type.Length == 0 || string.Equals(type, entry.ComponentType, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(entry => entry.LocalFileId)
                .Select(entry => new JObject
                {
                    // JSON numbers cannot safely represent all Unity local file IDs in browser tooling.
                    ["localFileId"] = entry.LocalFileId.ToString(CultureInfo.InvariantCulture),
                    ["hierarchyPath"] = entry.HierarchyPath,
                    ["componentType"] = entry.ComponentType
                }));

            return new JObject
            {
                ["assetPath"] = assetPath,
                ["assetGuid"] = assetGuid,
                ["totalObjectCount"] = entries.Count,
                ["objectCount"] = objects.Count,
                ["objects"] = objects
            };
        }

        private static JObject IndexScene(string assetPath, string assetGuid,
            Dictionary<long, AssetObjectEntry> entries)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return ToolHelper.ErrorResult(McpErrorCodes.InvalidParameter,
                    "Scene asset indexing is only available outside Play Mode.",
                    "Exit Play Mode before indexing a .unity asset.");
            }

            var scene = SceneManager.GetSceneByPath(assetPath);
            var openedByTool = false;
            try
            {
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    scene = OpenSceneForIndexing(assetPath);
                    openedByTool = true;
                }

                if (!scene.IsValid() || !scene.isLoaded)
                    return ToolHelper.ErrorResult(McpErrorCodes.InternalError, $"Failed to load scene asset: {assetPath}");

                foreach (var root in scene.GetRootGameObjects())
                    IndexGameObjectHierarchy(root, assetGuid, entries);

                return null;
            }
            catch (Exception ex)
            {
                return ToolHelper.ErrorResult(McpErrorCodes.InternalError,
                    $"Failed to index scene asset '{assetPath}': {ex.Message}");
            }
            finally
            {
                if (openedByTool && scene.IsValid())
                    EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static Scene OpenSceneForIndexing(string assetPath)
        {
#if UNITY_6000_6_OR_NEWER
            return EditorSceneManager.OpenPreviewScene(assetPath);
#else
            // 2022.3 exposes the same isolated preview operation internally.
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var open = typeof(EditorSceneManager).GetMethod("OpenPreviewScene", flags, null, new[] { typeof(string) }, null);
            if (open == null) throw new NotSupportedException("This Editor cannot open an isolated preview of the scene.");
            return (Scene)open.Invoke(null, new object[] { assetPath });
#endif
        }

        private static void IndexGameObjectHierarchy(GameObject root, string assetGuid,
            Dictionary<long, AssetObjectEntry> entries)
        {
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                var hierarchyPath = GetHierarchyPath(transform);
                AddObject(transform.gameObject, hierarchyPath, nameof(GameObject), assetGuid, entries);

                foreach (var component in transform.GetComponents<Component>())
                {
                    // Missing scripts are represented as null components and expose no Unity Object to query.
                    if (component != null)
                        AddObject(component, hierarchyPath, component.GetType().Name, assetGuid, entries);
                }
            }
        }

        private static void AddObject(UnityEngine.Object assetObject, string hierarchyPath, string componentType,
            string expectedAssetGuid, Dictionary<long, AssetObjectEntry> entries)
        {
            if (!TryGetAssetIdentity(assetObject, out var objectGuid, out var localFileId))
                return;

            // Nested prefab content can resolve to its source prefab. Only include IDs owned by the requested file.
            if (!string.Equals(objectGuid, expectedAssetGuid, StringComparison.OrdinalIgnoreCase))
                return;

            if (!entries.ContainsKey(localFileId))
                entries.Add(localFileId, new AssetObjectEntry(localFileId, hierarchyPath, componentType));
        }

        private static bool TryGetAssetIdentity(UnityEngine.Object assetObject, out string assetGuid,
            out long localFileId)
        {
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(assetObject, out assetGuid, out localFileId) &&
                localFileId != 0 && !string.IsNullOrEmpty(assetGuid))
                return true;

            // Scene objects do not resolve through AssetDatabase in Unity 2022, but GlobalObjectId
            // exposes the scene GUID and the same YAML local file ID while the scene is loaded.
            var globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(assetObject);
            assetGuid = globalObjectId.assetGUID.ToString();
            localFileId = unchecked((long)globalObjectId.targetObjectId);
            return localFileId != 0 &&
                   !string.IsNullOrEmpty(assetGuid) &&
                   !string.Equals(assetGuid, "00000000000000000000000000000000", StringComparison.Ordinal);
        }

        private static string GetHierarchyPath(Transform transform)
        {
            var names = new Stack<string>();
            for (var current = transform; current != null; current = current.parent)
                names.Push(current.name);
            return string.Join("/", names);
        }

        private static bool IsProjectRelativeAssetPath(string assetPath)
        {
            return assetPath.StartsWith("Assets/", StringComparison.Ordinal) ||
                   assetPath.StartsWith("Packages/", StringComparison.Ordinal);
        }

        private sealed class AssetObjectEntry
        {
            public AssetObjectEntry(long localFileId, string hierarchyPath, string componentType)
            {
                LocalFileId = localFileId;
                HierarchyPath = hierarchyPath;
                ComponentType = componentType;
            }

            public long LocalFileId { get; }
            public string HierarchyPath { get; }
            public string ComponentType { get; }
        }
    }
}
