#if UNITY_6000_6_OR_NEWER
using ObjectId = System.UInt64;
#else
using ObjectId = System.Int32;
#endif
using System;
using System.IO;
using System.Linq;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace I1rr.UnityMcp.Editor
{
    [McpToolCategory("Asset")]
    public static class AssetInfoTools
    {
        private const int DefaultMaxArrayElements = 100;
        private const int DefaultMaxDepth = 10;

        #region get_asset_info

        [McpTool("get_asset_info",
            "Get detailed information about an asset: type, dependencies, file size, and import settings summary.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""path"": { ""type"": ""string"", ""description"": ""Asset path (e.g. 'Assets/Materials/MyMat.mat')"" },
                    ""guid"": { ""type"": ""string"", ""description"": ""Asset GUID (alternative to path)"" }
                }
            }")]
        public static JObject GetAssetInfo(JObject parameters)
        {
            var path = ResolveAssetPath(parameters);
            if (path == null)
                return Error("Either 'path' or 'guid' is required.");
            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)))
                return Error($"Asset not found: {path}");

            var guid = AssetDatabase.AssetPathToGUID(path);
            var mainAsset = AssetDatabase.LoadMainAssetAtPath(path);
            var assetType = AssetDatabase.GetMainAssetTypeAtPath(path);
            var importer = AssetImporter.GetAtPath(path);

            var result = new JObject
            {
                ["path"] = path,
                ["guid"] = guid,
                ["type"] = assetType?.FullName ?? "Unknown",
                ["name"] = Path.GetFileNameWithoutExtension(path),
                ["extension"] = Path.GetExtension(path)
            };

            // File size
            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
            {
                var fileInfo = new FileInfo(fullPath);
                result["file_size_bytes"] = fileInfo.Length;
            }

            // Labels
            if (mainAsset != null)
            {
                var assetLabels = AssetDatabase.GetLabels(mainAsset);
                if (assetLabels.Length > 0)
                    result["labels"] = new JArray(assetLabels);
            }

            // Direct dependencies
            var deps = AssetDatabase.GetDependencies(path, false);
            result["dependency_count"] = deps.Length;

            // Import settings summary
            if (importer != null)
            {
                var importSettings = new JObject
                {
                    ["importer_type"] = importer.GetType().Name
                };

                if (importer is TextureImporter texImporter)
                {
                    importSettings["texture_type"] = texImporter.textureType.ToString();
                    importSettings["max_size"] = texImporter.maxTextureSize;
                    importSettings["compression"] = texImporter.textureCompression.ToString();
                    importSettings["read_write"] = texImporter.isReadable;
                }
                else if (importer is ModelImporter modelImporter)
                {
                    importSettings["scale_factor"] = modelImporter.globalScale;
                    importSettings["import_animation"] = modelImporter.importAnimation;
                    importSettings["mesh_compression"] = modelImporter.meshCompression.ToString();
                }
                else if (importer is AudioImporter audioImporter)
                {
                    importSettings["force_mono"] = audioImporter.forceToMono;
                    importSettings["load_in_background"] = audioImporter.loadInBackground;
                }

                result["import_settings"] = importSettings;
            }

            // Sub-assets
            var subAssets = AssetDatabase.LoadAllAssetRepresentationsAtPath(path);
            if (subAssets.Length > 0)
            {
                result["sub_asset_count"] = subAssets.Length;
                var subItems = new JArray();
                foreach (var sub in subAssets.Take(20))
                {
                    if (sub == null) continue;
                    subItems.Add(new JObject
                    {
                        ["name"] = sub.name,
                        ["type"] = sub.GetType().Name
                    });
                }
                result["sub_assets"] = subItems;
            }

            return result;
        }

        #endregion

        #region get_serialized_asset

        [McpTool("get_serialized_asset",
            "Read any asset's SerializedObject properties as JSON. Useful for inspecting ScriptableObjects, Materials, and other assets.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""path"": { ""type"": ""string"", ""description"": ""Asset path (e.g. 'Assets/Data/Config.asset')"" },
                    ""max_array_elements"": { ""type"": ""integer"", ""description"": ""Max array elements to include before truncation (default 100)"", ""default"": 100 },
                    ""max_depth"": { ""type"": ""integer"", ""description"": ""Max property nesting depth (default 10)"", ""default"": 10 }
                },
                ""required"": [""path""]
            }")]
        public static JObject GetSerializedAsset(JObject parameters)
        {
            var assetPath = parameters["path"]?.Value<string>();
            var maxArrayElements = parameters["max_array_elements"]?.Value<int>() ?? DefaultMaxArrayElements;
            var maxDepth = parameters["max_depth"]?.Value<int>() ?? DefaultMaxDepth;

            if (string.IsNullOrEmpty(assetPath))
                return Error("path is required.");
            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath, AssetPathToGUIDOptions.OnlyExistingAssets)))
                return Error($"Asset not found: {assetPath}");

            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (asset == null)
                return Error($"Failed to load asset: {assetPath}");

            var so = new SerializedObject(asset);
            var properties = ReadAllProperties(so, maxArrayElements, maxDepth);
            so.Dispose();

            return new JObject
            {
                ["path"] = assetPath,
                ["guid"] = AssetDatabase.AssetPathToGUID(assetPath),
                ["type"] = asset.GetType().FullName,
                ["name"] = asset.name,
                ["properties"] = properties
            };
        }

        #endregion

        #region get_project_structure

        [McpTool("get_project_structure",
            "List directory contents with file metadata (size, extension, last modified). Supports recursive listing and filtering.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""path"": { ""type"": ""string"", ""description"": ""Project-relative directory path (e.g. 'Assets/Scripts'). Defaults to project root."" },
                    ""recursive"": { ""type"": ""boolean"", ""description"": ""List contents recursively (default false)."", ""default"": false },
                    ""filter"": { ""type"": ""string"", ""description"": ""Filter entries by name (case-insensitive Contains match) or extension (e.g. '.cs')."" }
                }
            }")]
        public static JObject GetProjectStructure(JObject parameters)
        {
            var inputPath = parameters["path"]?.Value<string>() ?? "";
            var recursive = parameters["recursive"]?.Value<bool>() ?? false;
            var filter = parameters["filter"]?.Value<string>();

            string resolvedPath;
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                resolvedPath = McpPathValidator.ProjectRoot.TrimEnd(Path.DirectorySeparatorChar);
            }
            else
            {
                var validation = McpPathValidator.ValidatePath(inputPath);
                if (!validation.IsValid)
                    return Error(validation.Error);
                resolvedPath = validation.ResolvedPath;
            }

            if (!Directory.Exists(resolvedPath))
                return Error($"Directory not found: {inputPath}");

            var clock = System.Diagnostics.Stopwatch.StartNew();
            bool Expired() => clock.ElapsedMilliseconds >= 5000;
            const int maxEntries = 2000;

            var entries = new JArray();
            int totalCount = 0;
            bool truncated = false;

            try
            {
                foreach (var entry in FileTree.Walk(resolvedPath, recursive, Expired))
                {
                    if (entry is DirectoryInfo)
                    {
                        var dir = entry.FullName;
                        var dirName = Path.GetFileName(dir);
                        if (!string.IsNullOrEmpty(filter) &&
                            dirName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        totalCount++;
                        if (entries.Count >= maxEntries)
                        {
                            truncated = true;
                            continue;
                        }

                        entries.Add(new JObject
                        {
                            ["name"] = dirName,
                            ["path"] = GetProjectRelativePath(dir),
                            ["type"] = "directory"
                        });
                        continue;
                    }
                    var file = entry.FullName;
                    var fileName = Path.GetFileName(file);
                    var ext = Path.GetExtension(file);

                    if (!string.IsNullOrEmpty(filter))
                    {
                        bool matchesName = fileName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
                        bool matchesExt = filter.StartsWith(".") && ext.Equals(filter, StringComparison.OrdinalIgnoreCase);
                        if (!matchesName && !matchesExt)
                            continue;
                    }

                    totalCount++;
                    if (entries.Count >= maxEntries)
                    {
                        truncated = true;
                        continue;
                    }

                    var fileInfo = new FileInfo(file);
                    entries.Add(new JObject
                    {
                        ["name"] = fileName,
                        ["path"] = GetProjectRelativePath(file),
                        ["type"] = "file",
                        ["size"] = fileInfo.Length,
                        ["extension"] = ext,
                        ["lastModified"] = fileInfo.LastWriteTimeUtc.ToString("O")
                    });
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
                ["path"] = string.IsNullOrWhiteSpace(inputPath) ? "." : inputPath,
                ["entries"] = entries,
                ["total_count"] = totalCount
            };

            if (Expired()) result["truncated"] = true;
            else if (truncated)
                result["truncated"] = true;

            return result;
        }

        #endregion

        #region Helpers

        private static string ResolveAssetPath(JObject parameters)
        {
            var path = parameters["path"]?.Value<string>();
            if (!string.IsNullOrEmpty(path))
                return path;

            var guid = parameters["guid"]?.Value<string>();
            if (!string.IsNullOrEmpty(guid))
            {
                var resolved = AssetDatabase.GUIDToAssetPath(guid);
                return string.IsNullOrEmpty(resolved) ? null : resolved;
            }

            return null;
        }

        private static string GetProjectRelativePath(string absolutePath)
        {
            var root = McpPathValidator.ProjectRoot;
            if (absolutePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return absolutePath.Substring(root.Length).Replace('\\', '/');
            return absolutePath.Replace('\\', '/');
        }

        #endregion

        #region SerializedProperty Reader

        private static JObject ReadAllProperties(SerializedObject so, int maxArrayElements, int maxDepth)
        {
            var result = new JObject();
            var iterator = so.GetIterator();
            bool enterChildren = true;

            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = true;

                if (iterator.propertyPath == "m_Script")
                {
                    enterChildren = false;
                    continue;
                }

                if (!iterator.propertyPath.Contains('.'))
                {
                    result[iterator.propertyPath] = ReadProperty(iterator, maxArrayElements, maxDepth, 0);
                    enterChildren = false;
                }
                else
                {
                    enterChildren = false;
                }
            }

            return result;
        }

        private static JToken ReadProperty(SerializedProperty prop, int maxArrayElements, int maxDepth, int currentDepth)
        {
            if (currentDepth >= maxDepth)
                return new JObject { ["__truncated_depth"] = true };

            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return prop.type == "long" ? new JValue(prop.longValue) : new JValue(prop.intValue);

                case SerializedPropertyType.Boolean:
                    return new JValue(prop.boolValue);

                case SerializedPropertyType.Float:
                    return prop.type == "double" ? new JValue(prop.doubleValue) : new JValue(prop.floatValue);

                case SerializedPropertyType.String:
                    return new JValue(prop.stringValue);

                case SerializedPropertyType.Color:
                    var c = prop.colorValue;
                    return new JArray(c.r, c.g, c.b, c.a);

                case SerializedPropertyType.ObjectReference:
                    return ReadObjectReference(prop);

                case SerializedPropertyType.LayerMask:
                    return new JValue(prop.intValue);

                case SerializedPropertyType.Enum:
                    var enumNames = prop.enumNames;
                    var idx = prop.enumValueIndex;
                    return (idx >= 0 && idx < enumNames.Length) ? new JValue(enumNames[idx]) : new JValue(idx);

                case SerializedPropertyType.Vector2:
                    var v2 = prop.vector2Value;
                    return new JArray(v2.x, v2.y);

                case SerializedPropertyType.Vector3:
                    var v3 = prop.vector3Value;
                    return new JArray(v3.x, v3.y, v3.z);

                case SerializedPropertyType.Vector4:
                    var v4 = prop.vector4Value;
                    return new JArray(v4.x, v4.y, v4.z, v4.w);

                case SerializedPropertyType.Rect:
                    var r = prop.rectValue;
                    return new JObject { ["x"] = r.x, ["y"] = r.y, ["width"] = r.width, ["height"] = r.height };

                case SerializedPropertyType.ArraySize:
                    return new JValue(prop.intValue);

                case SerializedPropertyType.Character:
                    return new JValue(((char)prop.intValue).ToString());

                case SerializedPropertyType.AnimationCurve:
                    var curve = prop.animationCurveValue;
                    var keys = new JArray();
                    foreach (var key in curve.keys)
                    {
                        keys.Add(new JObject
                        {
                            ["time"] = key.time,
                            ["value"] = key.value,
                            ["inTangent"] = key.inTangent,
                            ["outTangent"] = key.outTangent
                        });
                    }
                    return keys;

                case SerializedPropertyType.Bounds:
                    var b = prop.boundsValue;
                    return new JObject
                    {
                        ["center"] = new JArray(b.center.x, b.center.y, b.center.z),
                        ["size"] = new JArray(b.size.x, b.size.y, b.size.z)
                    };

                case SerializedPropertyType.Quaternion:
                    var q = prop.quaternionValue;
                    return new JArray(q.x, q.y, q.z, q.w);

                case SerializedPropertyType.Vector2Int:
                    var v2i = prop.vector2IntValue;
                    return new JArray(v2i.x, v2i.y);

                case SerializedPropertyType.Vector3Int:
                    var v3i = prop.vector3IntValue;
                    return new JArray(v3i.x, v3i.y, v3i.z);

                case SerializedPropertyType.RectInt:
                    var ri = prop.rectIntValue;
                    return new JObject { ["x"] = ri.x, ["y"] = ri.y, ["width"] = ri.width, ["height"] = ri.height };

                case SerializedPropertyType.BoundsInt:
                    var bi = prop.boundsIntValue;
                    return new JObject
                    {
                        ["center"] = new JArray(bi.position.x, bi.position.y, bi.position.z),
                        ["size"] = new JArray(bi.size.x, bi.size.y, bi.size.z)
                    };

                case SerializedPropertyType.Hash128:
                    return new JValue(prop.hash128Value.ToString());

                case SerializedPropertyType.Generic:
                    if (prop.isArray)
                        return ReadArray(prop, maxArrayElements, maxDepth, currentDepth);
                    return ReadGenericObject(prop, maxArrayElements, maxDepth, currentDepth);

                default:
                    return new JValue($"(unsupported: {prop.propertyType})");
            }
        }

        private static JToken ReadObjectReference(SerializedProperty prop)
        {
            var obj = prop.objectReferenceValue;
            if (obj == null)
                return JValue.CreateNull();

            var result = new JObject
            {
                ["instanceId"] = obj.GetMcpId(),
                ["name"] = obj.name,
                ["type"] = obj.GetType().Name
            };

            var path = AssetDatabase.GetAssetPath(obj);
            if (!string.IsNullOrEmpty(path))
            {
                result["path"] = path;
                result["guid"] = AssetDatabase.AssetPathToGUID(path);
            }

            return result;
        }

        private static JToken ReadArray(SerializedProperty prop, int maxArrayElements, int maxDepth, int currentDepth)
        {
            int count = prop.arraySize;
            var arr = new JArray();
            int limit = Math.Min(count, maxArrayElements);

            for (int i = 0; i < limit; i++)
            {
                var element = prop.GetArrayElementAtIndex(i);
                arr.Add(ReadProperty(element, maxArrayElements, maxDepth, currentDepth + 1));
            }

            if (count > maxArrayElements)
            {
                arr.Add(new JObject
                {
                    ["__truncated"] = true,
                    ["total_count"] = count,
                    ["shown"] = maxArrayElements
                });
            }

            return arr;
        }

        private static JToken ReadGenericObject(SerializedProperty prop, int maxArrayElements, int maxDepth, int currentDepth)
        {
            var obj = new JObject();
            var child = prop.Copy();
            var end = prop.GetEndProperty();

            if (!child.NextVisible(true))
                return obj;

            do
            {
                if (SerializedProperty.EqualContents(child, end))
                    break;

                var name = child.propertyPath;
                var dotIdx = name.LastIndexOf('.');
                if (dotIdx >= 0)
                    name = name.Substring(dotIdx + 1);

                obj[name] = ReadProperty(child, maxArrayElements, maxDepth, currentDepth + 1);
            }
            while (child.NextVisible(false));

            return obj;
        }

        #endregion

        private static JObject Error(string message) =>
            McpError.Create(McpErrorCodes.InternalError, message);
    }
}
