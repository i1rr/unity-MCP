#if UNITY_6000_6_OR_NEWER
using ObjectId = System.UInt64;
#else
using ObjectId = System.Int32;
#endif
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace I1rr.UnityMcp.Editor
{
    [McpToolCategory("Component")]
    public static class ComponentReadTools
    {
        private const int DefaultMaxArrayElements = 100;
        private const int DefaultMaxDepth = 10;

        #region get_components

        [McpTool("get_components",
            "List all components on a GameObject. Optionally includes serialized property values.",
            AutoRoute = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""instanceId"": { ""type"": ""integer"", ""description"": ""Instance ID of the target GameObject"" },
                    ""include_properties"": { ""type"": ""boolean"", ""description"": ""Include serialized property values for each component (default false)"", ""default"": false },
                    ""max_array_elements"": { ""type"": ""integer"", ""description"": ""Max array elements to include before truncation (default 100)"", ""default"": 100 },
                    ""max_depth"": { ""type"": ""integer"", ""description"": ""Max property nesting depth (default 10)"", ""default"": 10 }
                },
                ""required"": [""instanceId""]
            }")]
        public static JObject GetComponents(JObject parameters)
        {
            var instanceId = ToolHelper.RequireParam<ObjectId>(parameters, "instanceId", out var error);
            if (error != null) return error;
            var includeProperties = ToolHelper.OptionalParam(parameters, "include_properties", false);
            var maxArrayElements = ToolHelper.OptionalParam(parameters, "max_array_elements", DefaultMaxArrayElements);
            var maxDepth = ToolHelper.OptionalParam(parameters, "max_depth", DefaultMaxDepth);

            var go = ObjectIdentity.Resolve(instanceId) as GameObject;
            if (go == null)
                return ToolHelper.ErrorResult("not_found", $"GameObject not found with instanceId: {instanceId}");

            var components = go.GetComponents<Component>();
            var items = new JArray();

            for (int i = 0; i < components.Length; i++)
            {
                var comp = components[i];
                if (comp == null)
                {
                    items.Add(new JObject
                    {
                        ["index"] = i,
                        ["type"] = "(Missing Script)",
                        ["enabled"] = false
                    });
                    continue;
                }

                var entry = new JObject
                {
                    ["index"] = i,
                    ["type"] = comp.GetType().FullName,
                    ["enabled"] = GetComponentEnabled(comp)
                };

                if (includeProperties)
                {
                    var so = new SerializedObject(comp);
                    entry["properties"] = ReadAllProperties(so, maxArrayElements, maxDepth);
                    so.Dispose();
                }

                items.Add(entry);
            }

            return new JObject
            {
                ["instanceId"] = instanceId,
                ["name"] = go.name,
                ["component_count"] = components.Length,
                ["components"] = items
            };
        }

        #endregion

        #region get_component_properties

        [McpTool("get_component_properties",
            "Deep read of all SerializedProperties on a specific component as JSON.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""instanceId"": { ""type"": ""integer"", ""description"": ""Instance ID of the target GameObject"" },
                    ""component_type"": { ""type"": ""string"", ""description"": ""Component type name (e.g. 'Rigidbody', 'MyNamespace.MyScript')"" },
                    ""component_index"": { ""type"": ""integer"", ""description"": ""Zero-based index of the component (takes precedence over component_type)"" },
                    ""max_array_elements"": { ""type"": ""integer"", ""description"": ""Max array elements before truncation (default 100)"", ""default"": 100 },
                    ""max_depth"": { ""type"": ""integer"", ""description"": ""Max nesting depth (default 10)"", ""default"": 10 }
                },
                ""required"": [""instanceId""]
            }")]
        public static JObject GetComponentProperties(JObject parameters)
        {
            var instanceId = parameters["instanceId"]?.ToObject<ObjectId>() ?? 0;
            var componentType = parameters["component_type"]?.Value<string>();
            var componentIndex = parameters["component_index"]?.Value<int?>();
            var maxArrayElements = parameters["max_array_elements"]?.Value<int>() ?? DefaultMaxArrayElements;
            var maxDepth = parameters["max_depth"]?.Value<int>() ?? DefaultMaxDepth;

            var go = ObjectIdentity.Resolve(instanceId) as GameObject;
            if (go == null)
                return Error($"GameObject not found with instanceId: {instanceId}");

            var comp = ResolveComponent(go, componentType, componentIndex, out string error);
            if (comp == null)
                return Error(error);

            var so = new SerializedObject(comp);
            var properties = ReadAllProperties(so, maxArrayElements, maxDepth);
            so.Dispose();

            return new JObject
            {
                ["instanceId"] = instanceId,
                ["name"] = go.name,
                ["component_type"] = comp.GetType().FullName,
                ["component_index"] = Array.IndexOf(go.GetComponents<Component>(), comp),
                ["properties"] = properties
            };
        }

        #endregion

        #region SerializedProperty Reader

        internal static JObject ReadAllProperties(SerializedObject so, int maxArrayElements, int maxDepth)
        {
            var result = new JObject();
            var iterator = so.GetIterator();
            bool enterChildren = true;

            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = true;

                // Skip the script reference (m_Script) — it's always the first property on MonoBehaviours
                if (iterator.propertyPath == "m_Script")
                {
                    enterChildren = false;
                    continue;
                }

                // Only handle top-level properties; nested ones are handled by recursion
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
                    return ReadLayerMask(prop);

                case SerializedPropertyType.Enum:
                    return ReadEnum(prop);

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
                    return new JObject
                    {
                        ["x"] = r.x,
                        ["y"] = r.y,
                        ["width"] = r.width,
                        ["height"] = r.height
                    };

                case SerializedPropertyType.ArraySize:
                    return new JValue(prop.intValue);

                case SerializedPropertyType.Character:
                    return new JValue(((char)prop.intValue).ToString());

                case SerializedPropertyType.AnimationCurve:
                    return ReadAnimationCurve(prop);

                case SerializedPropertyType.Bounds:
                    var b = prop.boundsValue;
                    return new JObject
                    {
                        ["center"] = new JArray(b.center.x, b.center.y, b.center.z),
                        ["size"] = new JArray(b.size.x, b.size.y, b.size.z)
                    };

                case SerializedPropertyType.Gradient:
                    return ReadGradient(prop);

                case SerializedPropertyType.Quaternion:
                    var q = prop.quaternionValue;
                    return new JArray(q.x, q.y, q.z, q.w);

                case SerializedPropertyType.ExposedReference:
                    return ReadExposedReference(prop);

                case SerializedPropertyType.Vector2Int:
                    var v2i = prop.vector2IntValue;
                    return new JArray(v2i.x, v2i.y);

                case SerializedPropertyType.Vector3Int:
                    var v3i = prop.vector3IntValue;
                    return new JArray(v3i.x, v3i.y, v3i.z);

                case SerializedPropertyType.RectInt:
                    var ri = prop.rectIntValue;
                    return new JObject
                    {
                        ["x"] = ri.x,
                        ["y"] = ri.y,
                        ["width"] = ri.width,
                        ["height"] = ri.height
                    };

                case SerializedPropertyType.BoundsInt:
                    var bi = prop.boundsIntValue;
                    return new JObject
                    {
                        ["center"] = new JArray(bi.position.x, bi.position.y, bi.position.z),
                        ["size"] = new JArray(bi.size.x, bi.size.y, bi.size.z)
                    };

                case SerializedPropertyType.Hash128:
                    return new JValue(prop.hash128Value.ToString());

                case SerializedPropertyType.ManagedReference:
                    return ReadManagedReference(prop, maxArrayElements, maxDepth, currentDepth);

                case SerializedPropertyType.Generic:
                    if (prop.isArray)
                        return ReadArray(prop, maxArrayElements, maxDepth, currentDepth);
                    return ReadGenericObject(prop, maxArrayElements, maxDepth, currentDepth);

                case SerializedPropertyType.FixedBufferSize:
                    return new JValue(prop.fixedBufferSize);

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
                var guid = AssetDatabase.AssetPathToGUID(path);
                if (!string.IsNullOrEmpty(guid))
                    result["guid"] = guid;
            }

            return result;
        }

        private static JToken ReadLayerMask(SerializedProperty prop)
        {
            int mask = prop.intValue;
            var names = new JArray();
            for (int i = 0; i < 32; i++)
            {
                if ((mask & (1 << i)) != 0)
                {
                    var layerName = LayerMask.LayerToName(i);
                    if (!string.IsNullOrEmpty(layerName))
                        names.Add(layerName);
                }
            }

            return new JObject
            {
                ["value"] = mask,
                ["names"] = names
            };
        }

        private static JToken ReadEnum(SerializedProperty prop)
        {
            var enumNames = prop.enumNames;
            var idx = prop.enumValueIndex;
            return new JObject
            {
                ["value"] = idx,
                ["name"] = (idx >= 0 && idx < enumNames.Length) ? enumNames[idx] : idx.ToString()
            };
        }

        private static JToken ReadAnimationCurve(SerializedProperty prop)
        {
            var curve = prop.animationCurveValue;
            var keys = new JArray();
            foreach (var key in curve.keys)
            {
                keys.Add(new JObject
                {
                    ["time"] = key.time,
                    ["value"] = key.value,
                    ["inTangent"] = key.inTangent,
                    ["outTangent"] = key.outTangent,
                    ["inWeight"] = key.inWeight,
                    ["outWeight"] = key.outWeight,
                    ["weightedMode"] = (int)key.weightedMode
                });
            }
            return new JObject { ["keys"] = keys };
        }

        private static JToken ReadGradient(SerializedProperty prop)
        {
            // Gradient is not directly accessible from SerializedProperty; use reflection
            var gradientValue = GetGradientValue(prop);
            if (gradientValue == null)
                return new JObject { ["__error"] = "Could not read gradient" };

            var colorKeys = new JArray();
            foreach (var ck in gradientValue.colorKeys)
            {
                colorKeys.Add(new JObject
                {
                    ["color"] = new JArray(ck.color.r, ck.color.g, ck.color.b, ck.color.a),
                    ["time"] = ck.time
                });
            }

            var alphaKeys = new JArray();
            foreach (var ak in gradientValue.alphaKeys)
            {
                alphaKeys.Add(new JObject
                {
                    ["alpha"] = ak.alpha,
                    ["time"] = ak.time
                });
            }

            return new JObject
            {
                ["colorKeys"] = colorKeys,
                ["alphaKeys"] = alphaKeys
            };
        }

        private static Gradient GetGradientValue(SerializedProperty prop)
        {
            // Unity doesn't expose gradientValue publicly on older versions; use reflection
            var pi = typeof(SerializedProperty).GetProperty(
                "gradientValue",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            if (pi != null)
                return pi.GetValue(prop) as Gradient;

            return null;
        }

        private static JToken ReadExposedReference(SerializedProperty prop)
        {
            var nameProp = prop.FindPropertyRelative("exposedName");
            var defaultProp = prop.FindPropertyRelative("defaultValue");

            return new JObject
            {
                ["name"] = nameProp?.stringValue,
                ["defaultValue"] = defaultProp != null ? ReadObjectReference(defaultProp) : JValue.CreateNull()
            };
        }

        private static JToken ReadManagedReference(SerializedProperty prop, int maxArrayElements, int maxDepth, int currentDepth)
        {
            var typeName = prop.managedReferenceFullTypename;
            if (string.IsNullOrEmpty(typeName))
                return JValue.CreateNull();

            var obj = ReadGenericObject(prop, maxArrayElements, maxDepth, currentDepth);
            if (obj is JObject jObj)
            {
                jObj["__type"] = typeName;
            }
            return obj;
        }

        private static JArray ReadArray(SerializedProperty prop, int maxArrayElements, int maxDepth, int currentDepth)
        {
            var array = new JArray();
            int count = prop.arraySize;
            int limit = Math.Min(count, maxArrayElements);

            for (int i = 0; i < limit; i++)
            {
                var element = prop.GetArrayElementAtIndex(i);
                array.Add(ReadProperty(element, maxArrayElements, maxDepth, currentDepth + 1));
            }

            if (count > maxArrayElements)
            {
                array.Add(new JObject
                {
                    ["__truncated"] = true,
                    ["__total_count"] = count
                });
            }

            return array;
        }

        private static JObject ReadGenericObject(SerializedProperty prop, int maxArrayElements, int maxDepth, int currentDepth)
        {
            var obj = new JObject();
            var end = prop.GetEndProperty();
            var child = prop.Copy();

            if (!child.NextVisible(true))
                return obj;

            do
            {
                if (SerializedProperty.EqualContents(child, end))
                    break;

                var fieldName = GetRelativeFieldName(child.propertyPath, prop.propertyPath);
                if (fieldName == null || fieldName.Contains('.'))
                    continue;

                obj[fieldName] = ReadProperty(child, maxArrayElements, maxDepth, currentDepth + 1);
            }
            while (child.NextVisible(false));

            return obj;
        }

        private static string GetRelativeFieldName(string childPath, string parentPath)
        {
            if (!childPath.StartsWith(parentPath))
                return null;

            var relative = childPath.Substring(parentPath.Length);
            if (relative.StartsWith("."))
                relative = relative.Substring(1);

            return relative;
        }

        #endregion

        #region Helpers

        internal static Component ResolveComponent(GameObject go, string componentType, int? componentIndex, out string error)
        {
            error = null;
            var components = go.GetComponents<Component>();

            // component_index takes precedence
            if (componentIndex.HasValue)
            {
                var idx = componentIndex.Value;
                if (idx < 0 || idx >= components.Length)
                {
                    error = $"Component index {idx} out of range (0-{components.Length - 1})";
                    return null;
                }
                var comp = components[idx];
                if (comp == null)
                {
                    error = $"Component at index {idx} is missing (null)";
                    return null;
                }
                return comp;
            }

            if (string.IsNullOrEmpty(componentType))
            {
                error = "Either component_type or component_index must be specified";
                return null;
            }

            // Find by type name (first match)
            for (int i = 0; i < components.Length; i++)
            {
                var comp = components[i];
                if (comp == null) continue;

                var type = comp.GetType();
                if (string.Equals(type.Name, componentType, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type.FullName, componentType, StringComparison.OrdinalIgnoreCase))
                {
                    return comp;
                }
            }

            error = $"Component not found with type: {componentType}";
            return null;
        }

        private static bool GetComponentEnabled(Component comp)
        {
            if (comp is Behaviour behaviour)
                return behaviour.enabled;
            if (comp is Renderer renderer)
                return renderer.enabled;
            if (comp is Collider collider)
                return collider.enabled;
            return true;
        }

        private static JObject Error(string message)
        {
            return McpError.Create(McpErrorCodes.InternalError, message);
        }

        #endregion
    }
}
