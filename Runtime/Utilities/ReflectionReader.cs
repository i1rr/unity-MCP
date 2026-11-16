#if UNITY_6000_6_OR_NEWER
using ObjectId = System.UInt64;
#else
using ObjectId = System.Int32;
#endif
using I1rr.UnityMcp.Shared;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Scripting;

namespace I1rr.UnityMcp.Runtime
{
    /// <summary>
    /// IL2CPP-safe reflection-based field reader for runtime component/object inspection.
    /// Follows Unity serialization rules: public fields OR [SerializeField] private fields,
    /// excluding [NonSerialized] and [HideInInspector].
    /// </summary>
    [Preserve]
    public static partial class ReflectionReader
    {
        private const int DefaultMaxDepth = 5;
        private const int DefaultMaxArrayElements = 50;

        [Preserve]
        public static JObject ReadObject(object obj, int maxDepth = DefaultMaxDepth, int maxArrayElements = DefaultMaxArrayElements)
        {
            if (obj == null) return null;
            return ReadObjectInternal(obj, obj.GetType(), 0, maxDepth, maxArrayElements, new HashSet<object>(ReferenceEqualityComparer.Instance));
        }

        [Preserve]
        public static JToken ReadFieldValue(object obj, string fieldPath, int maxDepth = DefaultMaxDepth, int maxArrayElements = DefaultMaxArrayElements)
        {
            if (obj == null) return JValue.CreateNull();

            var parts = fieldPath.Split('.');
            object current = obj;
            Type currentType = obj.GetType();

            for (int i = 0; i < parts.Length; i++)
            {
                if (current == null) return JValue.CreateNull();

                var part = parts[i];

                // Handle array indexing: fieldName[0]
                int arrayIndex = -1;
                if (part.EndsWith("]"))
                {
                    int bracketStart = part.IndexOf('[');
                    if (bracketStart >= 0)
                    {
                        var indexStr = part.Substring(bracketStart + 1, part.Length - bracketStart - 2);
                        if (int.TryParse(indexStr, out arrayIndex))
                            part = part.Substring(0, bracketStart);
                    }
                }

                var field = currentType.GetField(part, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field == null)
                {
                    var prop = currentType.GetProperty(part, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (prop == null || !prop.CanRead) return JValue.CreateNull();
                    current = prop.GetValue(current);
                    currentType = prop.PropertyType;
                }
                else
                {
                    current = field.GetValue(current);
                    currentType = field.FieldType;
                }

                if (arrayIndex >= 0 && current is IList list)
                {
                    if (arrayIndex >= list.Count) return JValue.CreateNull();
                    current = list[arrayIndex];
                    currentType = current?.GetType() ?? currentType.GetElementType() ?? typeof(object);
                }
            }

            return ConvertValue(current, currentType, 0, maxDepth, maxArrayElements, new HashSet<object>(ReferenceEqualityComparer.Instance));
        }

        [Preserve]
        public static bool SetFieldValue(object obj, string fieldPath, object value)
        {
            if (obj == null) return false;

            var parts = fieldPath.Split('.');
            object current = obj;
            Type currentType = obj.GetType();

            for (int i = 0; i < parts.Length - 1; i++)
            {
                var part = parts[i];
                int arrayIndex = -1;
                if (part.EndsWith("]"))
                {
                    int bracketStart = part.IndexOf('[');
                    if (bracketStart >= 0)
                    {
                        var indexStr = part.Substring(bracketStart + 1, part.Length - bracketStart - 2);
                        if (int.TryParse(indexStr, out arrayIndex))
                            part = part.Substring(0, bracketStart);
                    }
                }

                var field = currentType.GetField(part, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null)
                {
                    current = field.GetValue(current);
                    currentType = field.FieldType;
                }
                else
                {
                    var prop = currentType.GetProperty(part, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (prop == null || !prop.CanRead) return false;
                    current = prop.GetValue(current);
                    currentType = prop.PropertyType;
                }

                if (arrayIndex >= 0 && current is IList list)
                {
                    if (arrayIndex >= list.Count) return false;
                    current = list[arrayIndex];
                    currentType = current?.GetType() ?? currentType.GetElementType() ?? typeof(object);
                }

                if (current == null) return false;
            }

            var lastPart = parts[^1];
            var targetField = currentType.GetField(lastPart, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (targetField != null)
            {
                targetField.SetValue(current, value);
                return true;
            }

            var targetProp = currentType.GetProperty(lastPart, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (targetProp != null && targetProp.CanWrite)
            {
                targetProp.SetValue(current, value);
                return true;
            }

            return false;
        }

        [Preserve]
        public static object ConvertFromJson(JToken token, Type targetType)
        {
            if (token == null || token.Type == JTokenType.Null) return null;

            if (targetType == typeof(int)) return token.Value<int>();
            if (targetType == typeof(float)) return token.Value<float>();
            if (targetType == typeof(double)) return token.Value<double>();
            if (targetType == typeof(bool)) return token.Value<bool>();
            if (targetType == typeof(string)) return token.Value<string>();
            if (targetType == typeof(long)) return token.Value<long>();

            if (targetType == typeof(Vector2) && token is JObject v2)
                return new Vector2(v2["x"]?.Value<float>() ?? 0, v2["y"]?.Value<float>() ?? 0);

            if (targetType == typeof(Vector3) && token is JObject v3)
                return new Vector3(v3["x"]?.Value<float>() ?? 0, v3["y"]?.Value<float>() ?? 0, v3["z"]?.Value<float>() ?? 0);

            if (targetType == typeof(Vector4) && token is JObject v4)
                return new Vector4(v4["x"]?.Value<float>() ?? 0, v4["y"]?.Value<float>() ?? 0,
                    v4["z"]?.Value<float>() ?? 0, v4["w"]?.Value<float>() ?? 0);

            if (targetType == typeof(Color) && token is JObject col)
                return new Color(col["r"]?.Value<float>() ?? 0, col["g"]?.Value<float>() ?? 0,
                    col["b"]?.Value<float>() ?? 0, col["a"]?.Value<float>() ?? 1);

            if (targetType == typeof(Quaternion) && token is JObject q)
                return new Quaternion(q["x"]?.Value<float>() ?? 0, q["y"]?.Value<float>() ?? 0,
                    q["z"]?.Value<float>() ?? 0, q["w"]?.Value<float>() ?? 0);

            if (targetType.IsEnum)
            {
                var str = token.Value<string>();
                if (str != null && Enum.IsDefined(targetType, str))
                    return Enum.Parse(targetType, str);
                if (token.Type == JTokenType.Integer)
                    return Enum.ToObject(targetType, token.Value<int>());
                return null;
            }

            return null;
        }

        private static JObject ReadObjectInternal(object obj, Type type, int depth, int maxDepth, int maxArrayElements, HashSet<object> visited)
        {
            if (obj == null || depth > maxDepth) return null;

            if (!type.IsValueType && !visited.Add(obj))
                return new JObject { ["$ref"] = "circular" };

            var result = new JObject();
            var fields = GetSerializableFields(type);

            foreach (var field in fields)
            {
                try
                {
                    var value = field.GetValue(obj);
                    result[field.Name] = ConvertValue(value, field.FieldType, depth + 1, maxDepth, maxArrayElements, visited);
                }
                catch
                {
                    result[field.Name] = "<error reading field>";
                }
            }

            return result;
        }

        private static JToken ConvertValue(object value, Type declaredType, int depth, int maxDepth, int maxArrayElements, HashSet<object> visited)
        {
            if (value == null) return JValue.CreateNull();

            // Unity object null check
            if (value is UnityEngine.Object uObj && uObj == null)
                return JValue.CreateNull();

            var type = value.GetType();

            // Primitives
            if (type == typeof(int)) return new JValue((int)value);
            if (type == typeof(float)) return new JValue((float)value);
            if (type == typeof(double)) return new JValue((double)value);
            if (type == typeof(bool)) return new JValue((bool)value);
            if (type == typeof(string)) return new JValue((string)value);
            if (type == typeof(long)) return new JValue((long)value);
            if (type == typeof(byte)) return new JValue((byte)value);
            if (type == typeof(short)) return new JValue((short)value);
            if (type == typeof(uint)) return new JValue((uint)value);
            if (type == typeof(ulong)) return new JValue((ulong)value);

            // Enums
            if (type.IsEnum) return new JValue(value.ToString());

            // Unity struct types
            if (type == typeof(Vector2))
            {
                var v = (Vector2)value;
                return new JObject { ["x"] = v.x, ["y"] = v.y };
            }
            if (type == typeof(Vector3))
            {
                var v = (Vector3)value;
                return new JObject { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z };
            }
            if (type == typeof(Vector4))
            {
                var v = (Vector4)value;
                return new JObject { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z, ["w"] = v.w };
            }
            if (type == typeof(Vector2Int))
            {
                var v = (Vector2Int)value;
                return new JObject { ["x"] = v.x, ["y"] = v.y };
            }
            if (type == typeof(Vector3Int))
            {
                var v = (Vector3Int)value;
                return new JObject { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z };
            }
            if (type == typeof(Color))
            {
                var c = (Color)value;
                return new JObject { ["r"] = c.r, ["g"] = c.g, ["b"] = c.b, ["a"] = c.a };
            }
            if (type == typeof(Color32))
            {
                var c = (Color32)value;
                return new JObject { ["r"] = c.r, ["g"] = c.g, ["b"] = c.b, ["a"] = c.a };
            }
            if (type == typeof(Quaternion))
            {
                var q = (Quaternion)value;
                return new JObject { ["x"] = q.x, ["y"] = q.y, ["z"] = q.z, ["w"] = q.w };
            }
            if (type == typeof(Bounds))
            {
                var b = (Bounds)value;
                return new JObject
                {
                    ["center"] = new JObject { ["x"] = b.center.x, ["y"] = b.center.y, ["z"] = b.center.z },
                    ["size"] = new JObject { ["x"] = b.size.x, ["y"] = b.size.y, ["z"] = b.size.z }
                };
            }
            if (type == typeof(Rect))
            {
                var r = (Rect)value;
                return new JObject { ["x"] = r.x, ["y"] = r.y, ["width"] = r.width, ["height"] = r.height };
            }

            // Unity Object reference
            if (value is UnityEngine.Object unityObj)
            {
                return new JObject
                {
                    ["$type"] = type.Name,
                    ["name"] = unityObj.name,
                    ["instanceId"] = unityObj.GetMcpId()
                };
            }

            // Depth check
            if (depth >= maxDepth) return $"<depth limit: {type.Name}>";

            // Arrays / Lists
            if (value is IList list)
            {
                var arr = new JArray();
                int count = Math.Min(list.Count, maxArrayElements);
                for (int i = 0; i < count; i++)
                {
                    arr.Add(ConvertValue(list[i], list[i]?.GetType() ?? typeof(object), depth + 1, maxDepth, maxArrayElements, visited));
                }
                if (list.Count > maxArrayElements)
                    arr.Add($"... +{list.Count - maxArrayElements} more");
                return arr;
            }

            // Nested object
            if (type.IsClass || (type.IsValueType && !type.IsPrimitive))
            {
                return ReadObjectInternal(value, type, depth, maxDepth, maxArrayElements, visited);
            }

            return value.ToString();
        }

        private static List<FieldInfo> GetSerializableFields(Type type)
        {
            var result = new List<FieldInfo>();
            var current = type;

            while (current != null && current != typeof(object) && current != typeof(UnityEngine.Object))
            {
                var fields = current.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                foreach (var field in fields)
                {
                    if (field.IsDefined(typeof(NonSerializedAttribute), false)) continue;
                    if (field.IsDefined(typeof(HideInInspector), false)) continue;

                    bool isPublic = field.IsPublic;
                    bool hasSerializeField = field.IsDefined(typeof(SerializeField), false);

                    if (isPublic || hasSerializeField)
                    {
                        result.Add(field);
                    }
                }
                current = current.BaseType;
            }

            return result;
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

            bool IEqualityComparer<object>.Equals(object x, object y) => ReferenceEquals(x, y);
            int IEqualityComparer<object>.GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
