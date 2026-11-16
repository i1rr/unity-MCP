using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Scripting;

namespace I1rr.UnityMcp.Runtime
{
    [Preserve]
    [McpToolCategory("Component")]
    public static class RuntimeComponentTools
    {
        private const int DefaultMaxDepth = 5;
        private const int DefaultMaxArrayElements = 50;

        private static JObject Error(string message) => McpError.Create(McpErrorCodes.InternalError, message);

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

        #region runtime_get_components

        [McpTool("runtime_get_components",
            "List all components on a GameObject at runtime via reflection.",
            AutoRoute = true,
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""gameobject_path"": { ""type"": ""string"", ""description"": ""Hierarchy path to the GameObject"" },
                    ""include_fields"": { ""type"": ""boolean"", ""description"": ""Include serializable field summaries (default false)"", ""default"": false },
                    ""max_depth"": { ""type"": ""integer"", ""description"": ""Max field nesting depth (default 5)"", ""default"": 5 }
                },
                ""required"": [""gameobject_path""]
            }")]
        [Preserve]
        public static JObject GetComponents(JObject parameters)
        {
            var path = parameters["gameobject_path"]?.Value<string>();
            if (string.IsNullOrEmpty(path)) return Error("'gameobject_path' is required.");

            var includeFields = parameters["include_fields"]?.Value<bool>() ?? false;
            int maxDepth = parameters["max_depth"]?.Value<int>() ?? DefaultMaxDepth;

            var go = FindByPath(path);
            if (go == null) return Error($"GameObject not found at path: {path}");

            var components = go.GetComponents<Component>();
            var items = new JArray();

            for (int i = 0; i < components.Length; i++)
            {
                var comp = components[i];
                if (comp == null)
                {
                    items.Add(new JObject { ["index"] = i, ["type"] = "(Missing Script)", ["enabled"] = false });
                    continue;
                }

                var entry = new JObject
                {
                    ["index"] = i,
                    ["type"] = comp.GetType().FullName,
                    ["enabled"] = GetEnabled(comp)
                };

                if (includeFields)
                {
                    entry["fields"] = ReflectionReader.ReadObject(comp, maxDepth);
                }

                items.Add(entry);
            }

            return new JObject
            {
                ["gameobject_path"] = path,
                ["name"] = go.name,
                ["component_count"] = components.Length,
                ["components"] = items
            };
        }

        #endregion

        #region runtime_get_component_data

        [McpTool("runtime_get_component_data",
            "Read Unity-serializable fields, or select bounded instance field paths (including private fields and auto-property backing fields). Never evaluates computed properties in selected-path mode.",
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""gameobject_path"": { ""type"": ""string"", ""description"": ""Hierarchy path to the GameObject"" },
                    ""component_type"": { ""type"": ""string"", ""description"": ""Component type name (e.g. 'Rigidbody', 'MyNamespace.MyScript')"" },
                    ""max_depth"": { ""type"": ""integer"", ""description"": ""Max nesting depth in default serialized mode (default 5)"", ""default"": 5 },
                    ""max_array_elements"": { ""type"": ""integer"", ""description"": ""Max array elements in default serialized mode (default 50)"", ""default"": 50 },
                    ""field_paths"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 16, ""items"": { ""type"": ""string"", ""maxLength"": 128 }, ""description"": ""Optional explicit instance fields/auto-properties, e.g. _input._destination or actors[0].Action. Up to six dot segments; arrays or exact List only. Fixed output limits: 256-character strings, 16 elements, two collection levels, 128 nodes. Opaque objects require explicit traversal. No user getters/indexers/ToString."" }
                },
                ""required"": [""gameobject_path"", ""component_type""]
            }")]
        [Preserve]
        public static JObject GetComponentData(JObject parameters)
        {
            var path = parameters["gameobject_path"]?.Value<string>();
            var componentType = parameters["component_type"]?.Value<string>();
            int maxDepth = parameters["max_depth"]?.Value<int>() ?? DefaultMaxDepth;
            int maxArrayElements = parameters["max_array_elements"]?.Value<int>() ?? DefaultMaxArrayElements;

            if (string.IsNullOrEmpty(path)) return Error("'gameobject_path' is required.");
            if (string.IsNullOrEmpty(componentType)) return Error("'component_type' is required.");

            var selected = parameters["field_paths"];
            if (selected != null && !ReflectionReader.ValidateDiagnosticPaths(selected, out string selectionError))
                return McpError.Create(McpErrorCodes.InvalidParameter, selectionError);

            var go = FindByPath(path);
            if (go == null) return Error($"GameObject not found at path: {path}");

            var comp = FindComponent(go, componentType);
            if (comp == null) return Error($"Component '{componentType}' not found on '{go.name}'.");

            var result = selected == null
                ? new JObject { ["data"] = ReflectionReader.ReadObject(comp, maxDepth, maxArrayElements) }
                : ReflectionReader.ReadDiagnosticFields(comp, (JArray)selected);
            result["gameobject_path"] = path;
            result["component_type"] = comp.GetType().FullName;
            return result;
        }

        #endregion

        #region runtime_set_field

        [McpTool("runtime_set_field",
            "Set a field value on a component via reflection. Supports int, float, double, bool, string, Vector2/3/4, Color, Quaternion, enum.",
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""gameobject_path"": { ""type"": ""string"", ""description"": ""Hierarchy path to the GameObject"" },
                    ""component_type"": { ""type"": ""string"", ""description"": ""Component type name"" },
                    ""field_path"": { ""type"": ""string"", ""description"": ""Dot-separated field path (e.g. 'health' or 'config.maxSpeed')"" },
                    ""value"": { ""description"": ""The value to set (type inferred from field)"" }
                },
                ""required"": [""gameobject_path"", ""component_type"", ""field_path"", ""value""]
            }")]
        [Preserve]
        public static JObject SetField(JObject parameters)
        {
            var path = parameters["gameobject_path"]?.Value<string>();
            var componentType = parameters["component_type"]?.Value<string>();
            var fieldPath = parameters["field_path"]?.Value<string>();
            var valueToken = parameters["value"];

            if (string.IsNullOrEmpty(path)) return Error("'gameobject_path' is required.");
            if (string.IsNullOrEmpty(componentType)) return Error("'component_type' is required.");
            if (string.IsNullOrEmpty(fieldPath)) return Error("'field_path' is required.");
            if (valueToken == null) return Error("'value' is required.");

            var go = FindByPath(path);
            if (go == null) return Error($"GameObject not found at path: {path}");

            var comp = FindComponent(go, componentType);
            if (comp == null) return Error($"Component '{componentType}' not found on '{go.name}'.");

            // Resolve the field type
            Type fieldType = ResolveFieldType(comp, fieldPath);
            if (fieldType == null) return Error($"Field path '{fieldPath}' not found on component '{componentType}'.");

            object convertedValue = ReflectionReader.ConvertFromJson(valueToken, fieldType);
            if (convertedValue == null && valueToken.Type != JTokenType.Null)
                return Error($"Cannot convert value to type '{fieldType.Name}'.");

            bool success = ReflectionReader.SetFieldValue(comp, fieldPath, convertedValue);
            if (!success) return Error($"Failed to set field '{fieldPath}' on component '{componentType}'.");

            Debug.LogWarning($"[MCP Runtime] Field set: {path}/{componentType}.{fieldPath} = {valueToken}");

            return new JObject
            {
                ["success"] = true,
                ["gameobject_path"] = path,
                ["component_type"] = componentType,
                ["field_path"] = fieldPath,
                ["new_value"] = valueToken
            };
        }

        #endregion

        private static Component FindComponent(GameObject go, string typeName)
        {
            var components = go.GetComponents<Component>();
            foreach (var comp in components)
            {
                if (comp == null) continue;
                var type = comp.GetType();
                if (type.Name == typeName || type.FullName == typeName)
                    return comp;
            }
            return null;
        }

        private static Type ResolveFieldType(object obj, string fieldPath)
        {
            var parts = fieldPath.Split('.');
            Type currentType = obj.GetType();

            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                int arrayIndex = -1;
                if (part.EndsWith("]"))
                {
                    int bracketStart = part.IndexOf('[');
                    if (bracketStart >= 0)
                    {
                        part = part.Substring(0, bracketStart);
                        arrayIndex = 0; // just need to know it's an array access
                    }
                }

                var field = currentType.GetField(part, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null)
                {
                    currentType = field.FieldType;
                }
                else
                {
                    var prop = currentType.GetProperty(part, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (prop == null) return null;
                    currentType = prop.PropertyType;
                }

                if (arrayIndex >= 0)
                {
                    if (currentType.IsArray)
                        currentType = currentType.GetElementType();
                    else if (currentType.IsGenericType)
                        currentType = currentType.GetGenericArguments()[0];
                }
            }

            return currentType;
        }

        private static bool GetEnabled(Component comp)
        {
            if (comp is Behaviour b) return b.enabled;
            if (comp is Renderer r) return r.enabled;
            if (comp is Collider c) return c.enabled;
            return true;
        }
    }
}
