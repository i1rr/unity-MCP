using System;
using System.IO;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace I1rr.UnityMcp.Editor
{
    [McpToolCategory("Asset")]
    public static class AssetOperationTools
    {
        #region create_asset

        [McpTool("create_asset",
            "Create a new ScriptableObject asset at the given path.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""path"": { ""type"": ""string"", ""description"": ""Target asset path (e.g. 'Assets/Data/MyConfig.asset')"" },
                    ""type"": { ""type"": ""string"", ""description"": ""ScriptableObject type name (fully qualified or simple)"" },
                    ""properties"": { ""type"": ""object"", ""description"": ""Optional property values to set via SerializedObject after creation"" }
                },
                ""required"": [""path"", ""type""]
            }")]
        public static JObject CreateAsset(JObject parameters)
        {
            var assetPath = parameters["path"]?.Value<string>();
            var typeName = parameters["type"]?.Value<string>();
            var properties = parameters["properties"] as JObject;

            if (string.IsNullOrEmpty(assetPath))
                return Error("path is required.");
            if (string.IsNullOrEmpty(typeName))
                return Error("type is required.");

            // Reject paths not rooted under Assets/ — prevents Packages/ namespace bypass
            if (!assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !assetPath.StartsWith("Assets\\", StringComparison.OrdinalIgnoreCase))
                return Error("Asset path must start with 'Assets/'.");

            // Validate path is within Assets (traversal/symlink checks)
            var validation = McpPathValidator.ValidateAndResolvePath(assetPath, "Assets");
            if (!validation.IsValid)
                return Error($"Invalid path: {validation.Error}");

            if (!assetPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                return Error("Asset path must end with '.asset'");

            // Check if asset already exists
            if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath, AssetPathToGUIDOptions.OnlyExistingAssets)))
                return Error($"Asset already exists at: {assetPath}");

            // Resolve type
            var soType = ResolveScriptableObjectType(typeName);
            if (soType == null)
                return Error($"ScriptableObject type not found: {typeName}");

            // Ensure directory exists
            var directory = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(directory) && !AssetDatabase.IsValidFolder(directory))
                CreateFoldersRecursive(directory);

            // Create the ScriptableObject
            var instance = ScriptableObject.CreateInstance(soType);
            if (instance == null)
                return Error($"Failed to create instance of type: {typeName}");

            AssetDatabase.CreateAsset(instance, assetPath);

            // Apply properties if specified
            if (properties != null && properties.Count > 0)
            {
                var so = new SerializedObject(instance);
                var errors = new JArray();

                foreach (var kvp in properties)
                {
                    var prop = so.FindProperty(kvp.Key);
                    if (prop == null)
                    {
                        errors.Add($"Property not found: {kvp.Key}");
                        continue;
                    }

                    var setErr = SetSerializedPropertyValue(prop, kvp.Value);
                    if (setErr != null)
                        errors.Add($"{kvp.Key}: {setErr}");
                }

                so.ApplyModifiedPropertiesWithoutUndo();
                so.Dispose();

                if (errors.Count > 0)
                {
                    AssetDatabase.SaveAssets();
                    return new JObject
                    {
                        ["success"] = true,
                        ["path"] = assetPath,
                        ["guid"] = AssetDatabase.AssetPathToGUID(assetPath),
                        ["type"] = soType.FullName,
                        ["property_errors"] = errors
                    };
                }
            }

            AssetDatabase.SaveAssets();

            return new JObject
            {
                ["success"] = true,
                ["path"] = assetPath,
                ["guid"] = AssetDatabase.AssetPathToGUID(assetPath),
                ["type"] = soType.FullName
            };
        }

        #endregion

        #region delete_asset

        [McpTool("delete_asset",
            "Delete an asset from the project. Requires confirm: true as a safety check.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""path"": { ""type"": ""string"", ""description"": ""Asset path to delete (e.g. 'Assets/OldStuff/Unused.mat')"" },
                    ""confirm"": { ""type"": ""boolean"", ""description"": ""Must be true to proceed with deletion"" }
                },
                ""required"": [""path"", ""confirm""]
            }")]
        public static JObject DeleteAsset(JObject parameters)
        {
            var assetPath = parameters["path"]?.Value<string>();
            var confirm = parameters["confirm"]?.Value<bool>() ?? false;

            if (string.IsNullOrEmpty(assetPath))
                return Error("path is required.");
            if (!confirm)
                return Error("confirm must be true to delete an asset.");

            // Reject paths not rooted under Assets/
            if (!assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !assetPath.StartsWith("Assets\\", StringComparison.OrdinalIgnoreCase))
                return Error("Asset path must start with 'Assets/'.");

            // Validate path (traversal/symlink checks)
            var validation = McpPathValidator.ValidateAndResolvePath(assetPath, "Assets");
            if (!validation.IsValid)
                return Error($"Invalid path: {validation.Error}");

            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath, AssetPathToGUIDOptions.OnlyExistingAssets)))
                return Error($"Asset not found: {assetPath}");

            var success = AssetDatabase.DeleteAsset(assetPath);
            if (!success)
                return Error($"Failed to delete asset: {assetPath}");

            return new JObject
            {
                ["success"] = true,
                ["deleted_path"] = assetPath
            };
        }

        #endregion

        #region move_asset

        [McpTool("move_asset",
            "Move or rename an asset within the project.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""from"": { ""type"": ""string"", ""description"": ""Current asset path"" },
                    ""to"": { ""type"": ""string"", ""description"": ""Destination asset path"" }
                },
                ""required"": [""from"", ""to""]
            }")]
        public static JObject MoveAsset(JObject parameters)
        {
            var from = parameters["from"]?.Value<string>();
            var to = parameters["to"]?.Value<string>();

            if (string.IsNullOrEmpty(from))
                return Error("from is required.");
            if (string.IsNullOrEmpty(to))
                return Error("to is required.");

            // Reject paths not rooted under Assets/
            if (!from.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !from.StartsWith("Assets\\", StringComparison.OrdinalIgnoreCase))
                return Error("Source path must start with 'Assets/'.");
            if (!to.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !to.StartsWith("Assets\\", StringComparison.OrdinalIgnoreCase))
                return Error("Destination path must start with 'Assets/'.");

            // Validate both paths (traversal/symlink checks)
            var fromValidation = McpPathValidator.ValidateAndResolvePath(from, "Assets");
            if (!fromValidation.IsValid)
                return Error($"Invalid source path: {fromValidation.Error}");

            var toValidation = McpPathValidator.ValidateAndResolvePath(to, "Assets");
            if (!toValidation.IsValid)
                return Error($"Invalid destination path: {toValidation.Error}");

            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(from, AssetPathToGUIDOptions.OnlyExistingAssets)))
                return Error($"Source asset not found: {from}");

            // Ensure destination directory exists
            var destDir = Path.GetDirectoryName(to)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(destDir) && !AssetDatabase.IsValidFolder(destDir))
                CreateFoldersRecursive(destDir);

            var errorMsg = AssetDatabase.MoveAsset(from, to);
            if (!string.IsNullOrEmpty(errorMsg))
                return Error($"Move failed: {errorMsg}");

            return new JObject
            {
                ["success"] = true,
                ["from"] = from,
                ["to"] = to,
                ["guid"] = AssetDatabase.AssetPathToGUID(to)
            };
        }

        #endregion

        #region reimport_asset

        [McpTool("reimport_asset",
            "Synchronously reimport an asset and return its dependency hash. Script compilation/domain reload may still be pending.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""path"": { ""type"": ""string"", ""description"": ""Asset path to reimport"" }
                },
                ""required"": [""path""]
            }")]
        public static JObject ReimportAsset(JObject parameters)
        {
            var assetPath = parameters["path"]?.Value<string>();

            if (string.IsNullOrEmpty(assetPath))
                return Error("path is required.");

            // Reject paths not rooted under Assets/
            if (!assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !assetPath.StartsWith("Assets\\", StringComparison.OrdinalIgnoreCase))
                return Error("Asset path must start with 'Assets/'.");

            var validation = McpPathValidator.ValidateAndResolvePath(assetPath, "Assets");
            if (!validation.IsValid)
                return Error($"Invalid path: {validation.Error}");

            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath, AssetPathToGUIDOptions.OnlyExistingAssets)))
                return Error($"Asset not found: {assetPath}");

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

            return new JObject
            {
                ["success"] = true,
                ["path"] = assetPath,
                ["message"] = "Asset import completed; script compilation/domain reload may still be pending.",
                ["force_synchronous_import"] = true,
                ["compilation_complete"] = false,
                ["dependency_hash"] = AssetDatabase.GetAssetDependencyHash(assetPath).ToString()
            };
        }

        #endregion

        #region Helpers

        private static Type ResolveScriptableObjectType(string typeName)
        {
            // Try direct lookup across all assemblies
            foreach (var assembly in I1rr.UnityMcp.Shared.UnityCompatibility.GetLoadedAssemblies())
            {
                try
                {
                    var type = assembly.GetType(typeName, false, true);
                    if (type != null && typeof(ScriptableObject).IsAssignableFrom(type))
                        return type;
                }
                catch
                {
                    // Skip assemblies that fail
                }
            }

            // Try simple name match
            foreach (var assembly in I1rr.UnityMcp.Shared.UnityCompatibility.GetLoadedAssemblies())
            {
                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (type.Name == typeName && typeof(ScriptableObject).IsAssignableFrom(type))
                            return type;
                    }
                }
                catch
                {
                    // Skip assemblies that fail enumeration
                }
            }

            return null;
        }

        private static void CreateFoldersRecursive(string folderPath)
        {
            var parts = folderPath.Split('/');
            var current = parts[0]; // "Assets"
            for (int i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        #endregion

        #region SerializedProperty Writer

        private static string SetSerializedPropertyValue(SerializedProperty prop, JToken value)
        {
            try
            {
                switch (prop.propertyType)
                {
                    case SerializedPropertyType.Integer:
                        if (prop.type == "long")
                            prop.longValue = value.Value<long>();
                        else
                            prop.intValue = value.Value<int>();
                        return null;

                    case SerializedPropertyType.Boolean:
                        prop.boolValue = value.Value<bool>();
                        return null;

                    case SerializedPropertyType.Float:
                        if (prop.type == "double")
                            prop.doubleValue = value.Value<double>();
                        else
                            prop.floatValue = value.Value<float>();
                        return null;

                    case SerializedPropertyType.String:
                        prop.stringValue = value.Value<string>();
                        return null;

                    case SerializedPropertyType.Color:
                        if (value is JArray colorArr && colorArr.Count >= 3)
                        {
                            prop.colorValue = new Color(
                                colorArr[0].Value<float>(),
                                colorArr[1].Value<float>(),
                                colorArr[2].Value<float>(),
                                colorArr.Count >= 4 ? colorArr[3].Value<float>() : 1f
                            );
                            return null;
                        }
                        return "Color requires an array of [r, g, b] or [r, g, b, a]";

                    case SerializedPropertyType.Enum:
                        if (value.Type == JTokenType.Integer)
                        {
                            prop.enumValueIndex = value.Value<int>();
                            return null;
                        }
                        if (value.Type == JTokenType.String)
                        {
                            var enumName = value.Value<string>();
                            var names = prop.enumNames;
                            var nameIdx = Array.IndexOf(names, enumName);
                            if (nameIdx < 0)
                                return $"Enum value not found: {enumName}. Valid: {string.Join(", ", names)}";
                            prop.enumValueIndex = nameIdx;
                            return null;
                        }
                        return "Enum value must be int or string";

                    case SerializedPropertyType.Vector2:
                        if (value is JArray v2Arr && v2Arr.Count >= 2)
                        {
                            prop.vector2Value = new Vector2(v2Arr[0].Value<float>(), v2Arr[1].Value<float>());
                            return null;
                        }
                        return "Vector2 requires [x, y]";

                    case SerializedPropertyType.Vector3:
                        if (value is JArray v3Arr && v3Arr.Count >= 3)
                        {
                            prop.vector3Value = new Vector3(v3Arr[0].Value<float>(), v3Arr[1].Value<float>(), v3Arr[2].Value<float>());
                            return null;
                        }
                        return "Vector3 requires [x, y, z]";

                    case SerializedPropertyType.Vector4:
                        if (value is JArray v4Arr && v4Arr.Count >= 4)
                        {
                            prop.vector4Value = new Vector4(v4Arr[0].Value<float>(), v4Arr[1].Value<float>(), v4Arr[2].Value<float>(), v4Arr[3].Value<float>());
                            return null;
                        }
                        return "Vector4 requires [x, y, z, w]";

                    case SerializedPropertyType.ObjectReference:
                        if (value == null || value.Type == JTokenType.Null)
                        {
                            prop.objectReferenceValue = null;
                            return null;
                        }
                        if (value.Type == JTokenType.String)
                        {
                            var assetPath = value.Value<string>();
                            var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                            if (obj == null)
                                return $"Asset not found at path: {assetPath}";
                            prop.objectReferenceValue = obj;
                            return null;
                        }
                        if (value is JObject refObj)
                        {
                            var guid = refObj["guid"]?.Value<string>();
                            var refPath = refObj["path"]?.Value<string>();
                            if (!string.IsNullOrEmpty(guid))
                                refPath = AssetDatabase.GUIDToAssetPath(guid);
                            if (!string.IsNullOrEmpty(refPath))
                            {
                                var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(refPath);
                                if (obj == null)
                                    return $"Asset not found: {refPath}";
                                prop.objectReferenceValue = obj;
                                return null;
                            }
                        }
                        return "ObjectReference: provide asset path string or {guid/path} object";

                    default:
                        return $"Setting property type '{prop.propertyType}' is not supported.";
                }
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        #endregion

        private static JObject Error(string message) =>
            McpError.Create(McpErrorCodes.InternalError, message);
    }
}
