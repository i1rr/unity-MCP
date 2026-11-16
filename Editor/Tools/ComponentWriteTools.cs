#if UNITY_6000_6_OR_NEWER
using ObjectId = System.UInt64;
#else
using ObjectId = System.Int32;
#endif
using System;
using System.Reflection;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace I1rr.UnityMcp.Editor
{
    [McpToolCategory("Component")]
    public static class ComponentWriteTools
    {
        #region set_component_property

        [McpTool("set_component_property",
            "Write a serialized property value on a component. Supports standard types, enums (name or int), ObjectReference (GUID/path/instanceId), LayerMask (array of names), vectors, colors, etc.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""instanceId"": { ""type"": ""integer"", ""description"": ""Instance ID of the target GameObject"" },
                    ""component_type"": { ""type"": ""string"", ""description"": ""Component type name"" },
                    ""component_index"": { ""type"": ""integer"", ""description"": ""Zero-based component index (takes precedence over component_type)"" },
                    ""property_path"": { ""type"": ""string"", ""description"": ""SerializedProperty path (e.g. 'm_Mass', 'myField.nested')"" },
                    ""value"": { ""description"": ""Value to set (type depends on property type)"" }
                },
                ""required"": [""instanceId"", ""property_path"", ""value""]
            }")]
        public static JObject SetComponentProperty(JObject parameters)
        {
            var instanceId = parameters["instanceId"]?.ToObject<ObjectId>() ?? 0;
            var componentType = parameters["component_type"]?.Value<string>();
            var componentIndex = parameters["component_index"]?.Value<int?>();
            var propertyPath = parameters["property_path"]?.Value<string>();
            var value = parameters["value"];

            if (string.IsNullOrEmpty(propertyPath))
                return Error("property_path is required");
            if (value == null)
                return Error("value is required");

            var go = ObjectIdentity.Resolve(instanceId) as GameObject;
            if (go == null)
                return Error($"GameObject not found with instanceId: {instanceId}");

            var comp = ComponentReadTools.ResolveComponent(go, componentType, componentIndex, out string error);
            if (comp == null)
                return Error(error);

            var so = new SerializedObject(comp);
            var prop = so.FindProperty(propertyPath);
            if (prop == null)
            {
                so.Dispose();
                return Error($"Property not found: {propertyPath}");
            }

            Undo.RecordObject(comp, $"MCP Set {propertyPath}");

            var setError = SetPropertyValue(prop, value);
            if (setError != null)
            {
                so.Dispose();
                return Error(setError);
            }

            so.ApplyModifiedProperties();
            so.Dispose();

            return new JObject
            {
                ["success"] = true,
                ["instanceId"] = instanceId,
                ["component_type"] = comp.GetType().FullName,
                ["property_path"] = propertyPath
            };
        }

        #endregion

        #region add_component

        [McpTool("add_component",
            "Add a component to a GameObject by type name (with Undo support).",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""instanceId"": { ""type"": ""integer"", ""description"": ""Instance ID of the target GameObject"" },
                    ""type_name"": { ""type"": ""string"", ""description"": ""Full or short type name of the component to add (e.g. 'Rigidbody', 'UnityEngine.UI.Image')"" }
                },
                ""required"": [""instanceId"", ""type_name""]
            }")]
        public static JObject AddComponent(JObject parameters)
        {
            var instanceId = parameters["instanceId"]?.ToObject<ObjectId>() ?? 0;
            var typeName = parameters["type_name"]?.Value<string>();

            if (string.IsNullOrEmpty(typeName))
                return Error("type_name is required");

            var go = ObjectIdentity.Resolve(instanceId) as GameObject;
            if (go == null)
                return Error($"GameObject not found with instanceId: {instanceId}");

            var type = ResolveComponentType(typeName);
            if (type == null)
                return Error($"Component type not found: {typeName}");

            var comp = Undo.AddComponent(go, type);
            if (comp == null)
                return Error($"Failed to add component of type: {typeName}");

            return new JObject
            {
                ["success"] = true,
                ["instanceId"] = instanceId,
                ["component_type"] = comp.GetType().FullName,
                ["component_index"] = Array.IndexOf(go.GetComponents<Component>(), comp)
            };
        }

        #endregion

        #region remove_component

        [McpTool("remove_component",
            "Remove a component from a GameObject (with Undo support). Use component_index or component_type to identify which.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""instanceId"": { ""type"": ""integer"", ""description"": ""Instance ID of the target GameObject"" },
                    ""component_type"": { ""type"": ""string"", ""description"": ""Component type name to remove"" },
                    ""component_index"": { ""type"": ""integer"", ""description"": ""Zero-based component index (takes precedence over component_type)"" }
                },
                ""required"": [""instanceId""]
            }")]
        public static JObject RemoveComponent(JObject parameters)
        {
            var instanceId = parameters["instanceId"]?.ToObject<ObjectId>() ?? 0;
            var componentType = parameters["component_type"]?.Value<string>();
            var componentIndex = parameters["component_index"]?.Value<int?>();

            var go = ObjectIdentity.Resolve(instanceId) as GameObject;
            if (go == null)
                return Error($"GameObject not found with instanceId: {instanceId}");

            var comp = ComponentReadTools.ResolveComponent(go, componentType, componentIndex, out string error);
            if (comp == null)
                return Error(error);

            if (comp is Transform)
                return Error("Cannot remove Transform component");

            var removedType = comp.GetType().FullName;
            Undo.DestroyObjectImmediate(comp);

            return new JObject
            {
                ["success"] = true,
                ["instanceId"] = instanceId,
                ["removed_type"] = removedType
            };
        }

        #endregion

        #region Property Writer

        private static string SetPropertyValue(SerializedProperty prop, JToken value)
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
                    return SetColor(prop, value);

                case SerializedPropertyType.ObjectReference:
                    return SetObjectReference(prop, value);

                case SerializedPropertyType.LayerMask:
                    return SetLayerMask(prop, value);

                case SerializedPropertyType.Enum:
                    return SetEnum(prop, value);

                case SerializedPropertyType.Vector2:
                    return SetVector2(prop, value);

                case SerializedPropertyType.Vector3:
                    return SetVector3(prop, value);

                case SerializedPropertyType.Vector4:
                    return SetVector4(prop, value);

                case SerializedPropertyType.Rect:
                    return SetRect(prop, value);

                case SerializedPropertyType.Bounds:
                    return SetBounds(prop, value);

                case SerializedPropertyType.Quaternion:
                    return SetQuaternion(prop, value);

                case SerializedPropertyType.Vector2Int:
                    return SetVector2Int(prop, value);

                case SerializedPropertyType.Vector3Int:
                    return SetVector3Int(prop, value);

                case SerializedPropertyType.RectInt:
                    return SetRectInt(prop, value);

                case SerializedPropertyType.BoundsInt:
                    return SetBoundsInt(prop, value);

                case SerializedPropertyType.Character:
                    var str = value.Value<string>();
                    prop.intValue = string.IsNullOrEmpty(str) ? 0 : str[0];
                    return null;

                case SerializedPropertyType.AnimationCurve:
                    return SetAnimationCurve(prop, value);

                case SerializedPropertyType.ArraySize:
                    prop.intValue = value.Value<int>();
                    return null;

                case SerializedPropertyType.Hash128:
                    prop.hash128Value = Hash128.Parse(value.Value<string>());
                    return null;

                case SerializedPropertyType.Generic:
                    if (prop.isArray)
                        return SetArray(prop, value);
                    return SetGenericObject(prop, value);

                default:
                    return $"Unsupported property type for writing: {prop.propertyType}";
            }
        }

        private static string SetColor(SerializedProperty prop, JToken value)
        {
            if (value is JArray arr && arr.Count >= 3)
            {
                prop.colorValue = new Color(
                    arr[0].Value<float>(),
                    arr[1].Value<float>(),
                    arr[2].Value<float>(),
                    arr.Count >= 4 ? arr[3].Value<float>() : 1f);
                return null;
            }
            return "Color must be [r, g, b] or [r, g, b, a]";
        }

        private static string SetObjectReference(SerializedProperty prop, JToken value)
        {
            if (value == null || value.Type == JTokenType.Null)
            {
                prop.objectReferenceValue = null;
                return null;
            }

            if (value.Type == JTokenType.String)
            {
                var str = value.Value<string>();

                // Try as GUID
                var path = AssetDatabase.GUIDToAssetPath(str);
                if (!string.IsNullOrEmpty(path))
                {
                    prop.objectReferenceValue = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                    return prop.objectReferenceValue != null ? null : $"Asset found at GUID path but failed to load: {path}";
                }

                // Try as asset path
                var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(str);
                if (obj != null)
                {
                    prop.objectReferenceValue = obj;
                    return null;
                }

                return $"Could not resolve object reference: {str}";
            }

            if (value is JObject jObj)
            {
                // Try instanceId
                if (jObj["instanceId"] != null)
                {
                    var id = jObj["instanceId"].ToObject<ObjectId>();
                    var obj = ObjectIdentity.Resolve(id);
                    if (obj != null)
                    {
                        prop.objectReferenceValue = obj;
                        return null;
                    }
                    return $"Object not found with instanceId: {id}";
                }

                // Try guid
                if (jObj["guid"] != null)
                {
                    var guid = jObj["guid"].Value<string>();
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.IsNullOrEmpty(path))
                    {
                        prop.objectReferenceValue = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                        return prop.objectReferenceValue != null ? null : $"Asset not loadable at: {path}";
                    }
                    return $"No asset found for GUID: {guid}";
                }

                // Try path
                if (jObj["path"] != null)
                {
                    var assetPath = jObj["path"].Value<string>();
                    var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                    if (obj != null)
                    {
                        prop.objectReferenceValue = obj;
                        return null;
                    }
                    return $"Asset not found at path: {assetPath}";
                }
            }

            return "ObjectReference value must be a GUID string, asset path string, or object with instanceId/guid/path";
        }

        private static string SetLayerMask(SerializedProperty prop, JToken value)
        {
            if (value.Type == JTokenType.Integer)
            {
                prop.intValue = value.Value<int>();
                return null;
            }

            if (value is JArray arr)
            {
                int mask = 0;
                foreach (var item in arr)
                {
                    var layerName = item.Value<string>();
                    var layer = LayerMask.NameToLayer(layerName);
                    if (layer < 0)
                        return $"Unknown layer name: {layerName}";
                    mask |= 1 << layer;
                }
                prop.intValue = mask;
                return null;
            }

            return "LayerMask must be an integer or array of layer name strings";
        }

        private static string SetEnum(SerializedProperty prop, JToken value)
        {
            if (value.Type == JTokenType.Integer)
            {
                prop.enumValueIndex = value.Value<int>();
                return null;
            }

            if (value.Type == JTokenType.String)
            {
                var name = value.Value<string>();
                var names = prop.enumNames;
                for (int i = 0; i < names.Length; i++)
                {
                    if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase))
                    {
                        prop.enumValueIndex = i;
                        return null;
                    }
                }
                return $"Enum value not found: '{name}'. Valid values: {string.Join(", ", names)}";
            }

            return "Enum value must be a string name or integer index";
        }

        private static string SetVector2(SerializedProperty prop, JToken value)
        {
            if (value is JArray arr && arr.Count >= 2)
            {
                prop.vector2Value = new Vector2(arr[0].Value<float>(), arr[1].Value<float>());
                return null;
            }
            return "Vector2 must be [x, y]";
        }

        private static string SetVector3(SerializedProperty prop, JToken value)
        {
            if (value is JArray arr && arr.Count >= 3)
            {
                prop.vector3Value = new Vector3(arr[0].Value<float>(), arr[1].Value<float>(), arr[2].Value<float>());
                return null;
            }
            return "Vector3 must be [x, y, z]";
        }

        private static string SetVector4(SerializedProperty prop, JToken value)
        {
            if (value is JArray arr && arr.Count >= 4)
            {
                prop.vector4Value = new Vector4(arr[0].Value<float>(), arr[1].Value<float>(), arr[2].Value<float>(), arr[3].Value<float>());
                return null;
            }
            return "Vector4 must be [x, y, z, w]";
        }

        private static string SetRect(SerializedProperty prop, JToken value)
        {
            if (value is JObject obj)
            {
                prop.rectValue = new Rect(
                    obj["x"]?.Value<float>() ?? 0,
                    obj["y"]?.Value<float>() ?? 0,
                    obj["width"]?.Value<float>() ?? 0,
                    obj["height"]?.Value<float>() ?? 0);
                return null;
            }
            return "Rect must be { x, y, width, height }";
        }

        private static string SetBounds(SerializedProperty prop, JToken value)
        {
            if (value is JObject obj)
            {
                var center = obj["center"] as JArray;
                var size = obj["size"] as JArray;
                if (center != null && center.Count >= 3 && size != null && size.Count >= 3)
                {
                    prop.boundsValue = new Bounds(
                        new Vector3(center[0].Value<float>(), center[1].Value<float>(), center[2].Value<float>()),
                        new Vector3(size[0].Value<float>(), size[1].Value<float>(), size[2].Value<float>()));
                    return null;
                }
            }
            return "Bounds must be { center: [x,y,z], size: [x,y,z] }";
        }

        private static string SetQuaternion(SerializedProperty prop, JToken value)
        {
            if (value is JArray arr && arr.Count >= 4)
            {
                prop.quaternionValue = new Quaternion(
                    arr[0].Value<float>(), arr[1].Value<float>(),
                    arr[2].Value<float>(), arr[3].Value<float>());
                return null;
            }
            return "Quaternion must be [x, y, z, w]";
        }

        private static string SetVector2Int(SerializedProperty prop, JToken value)
        {
            if (value is JArray arr && arr.Count >= 2)
            {
                prop.vector2IntValue = new Vector2Int(arr[0].Value<int>(), arr[1].Value<int>());
                return null;
            }
            return "Vector2Int must be [x, y]";
        }

        private static string SetVector3Int(SerializedProperty prop, JToken value)
        {
            if (value is JArray arr && arr.Count >= 3)
            {
                prop.vector3IntValue = new Vector3Int(arr[0].Value<int>(), arr[1].Value<int>(), arr[2].Value<int>());
                return null;
            }
            return "Vector3Int must be [x, y, z]";
        }

        private static string SetRectInt(SerializedProperty prop, JToken value)
        {
            if (value is JObject obj)
            {
                prop.rectIntValue = new RectInt(
                    obj["x"]?.Value<int>() ?? 0,
                    obj["y"]?.Value<int>() ?? 0,
                    obj["width"]?.Value<int>() ?? 0,
                    obj["height"]?.Value<int>() ?? 0);
                return null;
            }
            return "RectInt must be { x, y, width, height }";
        }

        private static string SetBoundsInt(SerializedProperty prop, JToken value)
        {
            if (value is JObject obj)
            {
                var center = obj["center"] as JArray;
                var size = obj["size"] as JArray;
                if (center != null && center.Count >= 3 && size != null && size.Count >= 3)
                {
                    prop.boundsIntValue = new BoundsInt(
                        new Vector3Int(center[0].Value<int>(), center[1].Value<int>(), center[2].Value<int>()),
                        new Vector3Int(size[0].Value<int>(), size[1].Value<int>(), size[2].Value<int>()));
                    return null;
                }
            }
            return "BoundsInt must be { center: [x,y,z], size: [x,y,z] }";
        }

        private static string SetAnimationCurve(SerializedProperty prop, JToken value)
        {
            if (value is JObject obj && obj["keys"] is JArray keysArr)
            {
                var keys = new Keyframe[keysArr.Count];
                for (int i = 0; i < keysArr.Count; i++)
                {
                    var k = keysArr[i] as JObject;
                    if (k == null)
                        return $"AnimationCurve key at index {i} must be an object";

                    keys[i] = new Keyframe
                    {
                        time = k["time"]?.Value<float>() ?? 0,
                        value = k["value"]?.Value<float>() ?? 0,
                        inTangent = k["inTangent"]?.Value<float>() ?? 0,
                        outTangent = k["outTangent"]?.Value<float>() ?? 0,
                        inWeight = k["inWeight"]?.Value<float>() ?? 0,
                        outWeight = k["outWeight"]?.Value<float>() ?? 0,
                        weightedMode = (WeightedMode)(k["weightedMode"]?.Value<int>() ?? 0)
                    };
                }
                prop.animationCurveValue = new AnimationCurve(keys);
                return null;
            }
            return "AnimationCurve must be { keys: [{time, value, inTangent, outTangent, ...}] }";
        }

        private static string SetArray(SerializedProperty prop, JToken value)
        {
            if (value is not JArray arr)
                return "Array property requires a JSON array value";

            prop.arraySize = arr.Count;
            for (int i = 0; i < arr.Count; i++)
            {
                var element = prop.GetArrayElementAtIndex(i);
                var err = SetPropertyValue(element, arr[i]);
                if (err != null)
                    return $"Error setting array element [{i}]: {err}";
            }
            return null;
        }

        private static string SetGenericObject(SerializedProperty prop, JToken value)
        {
            if (value is not JObject obj)
                return "Generic object property requires a JSON object value";

            foreach (var kvp in obj)
            {
                var child = prop.FindPropertyRelative(kvp.Key);
                if (child == null)
                    return $"Child property not found: {kvp.Key}";

                var err = SetPropertyValue(child, kvp.Value);
                if (err != null)
                    return $"Error setting '{kvp.Key}': {err}";
            }
            return null;
        }

        #endregion

        #region Helpers

        private static Type ResolveComponentType(string typeName)
        {
            // Try exact match in all loaded assemblies
            foreach (var assembly in I1rr.UnityMcp.Shared.UnityCompatibility.GetLoadedAssemblies())
            {
                var type = assembly.GetType(typeName, false, true);
                if (type != null && typeof(Component).IsAssignableFrom(type))
                    return type;
            }

            // Try common Unity namespaces
            string[] prefixes = { "UnityEngine.", "UnityEngine.UI.", "UnityEngine.EventSystems.", "TMPro." };
            foreach (var prefix in prefixes)
            {
                foreach (var assembly in I1rr.UnityMcp.Shared.UnityCompatibility.GetLoadedAssemblies())
                {
                    var type = assembly.GetType(prefix + typeName, false, true);
                    if (type != null && typeof(Component).IsAssignableFrom(type))
                        return type;
                }
            }

            // Fallback: search all types by short name
            foreach (var assembly in I1rr.UnityMcp.Shared.UnityCompatibility.GetLoadedAssemblies())
            {
                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (typeof(Component).IsAssignableFrom(type) &&
                            string.Equals(type.Name, typeName, StringComparison.OrdinalIgnoreCase))
                        {
                            return type;
                        }
                    }
                }
                catch (ReflectionTypeLoadException)
                {
                    // Skip assemblies that fail to load types
                }
            }

            return null;
        }

        private static JObject Error(string message)
        {
            return McpError.Create(McpErrorCodes.InternalError, message);
        }

        #endregion
    }
}
