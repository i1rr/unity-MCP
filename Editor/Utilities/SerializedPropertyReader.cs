#if UNITY_6000_6_OR_NEWER
using ObjectId = System.UInt64;
#else
using ObjectId = System.Int32;
#endif
using I1rr.UnityMcp.Shared;
using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace I1rr.UnityMcp.Editor
{
    /// <summary>
    /// Standalone utility for deep SerializedProperty traversal.
    /// Extracts property reading logic into a reusable helper for ComponentTools, AssetTools, etc.
    /// </summary>
    public static class SerializedPropertyReader
    {
        /// <summary>
        /// Reads all visible top-level properties from a SerializedObject as a flat JSON object.
        /// Skips the "m_Script" property (always first on MonoBehaviours).
        /// </summary>
        public static JObject ReadAllProperties(SerializedObject so, int maxDepth = 10, int maxArrayElements = 100)
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
                    result[iterator.propertyPath] = ReadProperty(iterator, maxDepth, maxArrayElements, 0);
                    enterChildren = false;
                }
                else
                {
                    enterChildren = false;
                }
            }

            return result;
        }

        /// <summary>
        /// Reads a single SerializedProperty recursively, producing a JToken representation.
        /// </summary>
        public static JToken ReadProperty(SerializedProperty prop, int maxDepth = 10, int maxArrayElements = 100)
        {
            return ReadProperty(prop, maxDepth, maxArrayElements, 0);
        }

        private static JToken ReadProperty(SerializedProperty prop, int maxDepth, int maxArrayElements, int currentDepth)
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
                    return ReadManagedReference(prop, maxDepth, maxArrayElements, currentDepth);

                case SerializedPropertyType.Generic:
                    if (prop.isArray)
                        return ReadArray(prop, maxDepth, maxArrayElements, currentDepth);
                    return ReadGenericObject(prop, maxDepth, maxArrayElements, currentDepth);

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
            var pi = typeof(SerializedProperty).GetProperty(
                "gradientValue",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            return pi?.GetValue(prop) as Gradient;
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

        private static JToken ReadManagedReference(SerializedProperty prop, int maxDepth, int maxArrayElements, int currentDepth)
        {
            var typeName = prop.managedReferenceFullTypename;
            if (string.IsNullOrEmpty(typeName))
                return JValue.CreateNull();

            var obj = ReadGenericObject(prop, maxDepth, maxArrayElements, currentDepth);
            if (obj is JObject jObj)
            {
                jObj["__type"] = typeName;
            }
            return obj;
        }

        private static JArray ReadArray(SerializedProperty prop, int maxDepth, int maxArrayElements, int currentDepth)
        {
            var array = new JArray();
            int count = prop.arraySize;
            int limit = Math.Min(count, maxArrayElements);

            for (int i = 0; i < limit; i++)
            {
                var element = prop.GetArrayElementAtIndex(i);
                array.Add(ReadProperty(element, maxDepth, maxArrayElements, currentDepth + 1));
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

        private static JObject ReadGenericObject(SerializedProperty prop, int maxDepth, int maxArrayElements, int currentDepth)
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

                obj[fieldName] = ReadProperty(child, maxDepth, maxArrayElements, currentDepth + 1);
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
    }
}
