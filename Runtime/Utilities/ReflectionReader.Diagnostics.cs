using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Scripting;

namespace I1rr.UnityMcp.Runtime
{
    public static partial class ReflectionReader
    {
        private const int DiagnosticMaxPaths = 16;
        private const int DiagnosticMaxSegments = 6;
        private const int DiagnosticMaxString = 256;
        private const int DiagnosticMaxElements = 16;
        private const int DiagnosticMaxNodes = 128;
        private static readonly Regex DiagnosticSegment = new Regex(
            @"^[A-Za-z_][A-Za-z0-9_]*(\[[0-9]{1,8}\])?\z", RegexOptions.CultureInvariant);

        /// <summary>Validate the entire selection before inspecting any values.</summary>
        public static bool ValidateDiagnosticPaths(JToken selection, out string error)
        {
            error = null;
            if (!(selection is JArray paths) || paths.Count < 1 || paths.Count > DiagnosticMaxPaths)
            {
                error = "field_paths must be an array of 1–16 field paths.";
                return false;
            }
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in paths)
            {
                if (item.Type != JTokenType.String || ((string)item).Length > 128)
                {
                    error = "Each field path must be a string of at most 128 characters.";
                    return false;
                }
                string path = (string)item;
                var segments = path.Split('.');
                if (segments.Length > DiagnosticMaxSegments || !unique.Add(path))
                {
                    error = "Field paths must be unique and contain at most six segments.";
                    return false;
                }
                foreach (var segment in segments)
                    if (!DiagnosticSegment.IsMatch(segment))
                    {
                        error = "Use dot-separated ASCII field names with optional nonnegative [index].";
                        return false;
                    }
            }
            return true;
        }

        /// <summary>
        /// Read managed instance fields without invoking user getters, indexers, enumerators
        /// or ToString. Auto-properties resolve only to compiler-generated backing fields.
        /// Opaque objects are traversed only by an explicit path, never recursively expanded.
        /// </summary>
        [Preserve]
        public static JObject ReadDiagnosticFields(object obj, JArray paths)
        {
            if (!ValidateDiagnosticPaths(paths, out string error)) throw new ArgumentException(error, nameof(paths));
            var data = new JObject();
            var errors = new JObject();
            int remaining = DiagnosticMaxNodes;
            foreach (string path in paths)
            {
                if (TryReadDiagnosticField(obj, path, out object value, out string failure))
                    data[path] = ConvertDiagnosticValue(value, 0, ref remaining);
                else
                    errors[path] = failure;
            }
            return new JObject
            {
                ["inspection_scope"] = "selected_instance_fields",
                ["data"] = data,
                ["field_errors"] = errors
            };
        }

        private static bool TryReadDiagnosticField(object obj, string path, out object value, out string error)
        {
            value = obj;
            error = null;
            foreach (string segment in path.Split('.'))
            {
                if (value == null || (value is UnityEngine.Object unityObj && unityObj == null))
                {
                    error = "null_intermediate";
                    return false;
                }
                int bracket = segment.IndexOf('[');
                string name = bracket < 0 ? segment : segment.Substring(0, bracket);
                var field = FindDiagnosticField(value.GetType(), name);
                if (field == null)
                {
                    error = "instance_field_or_auto_property_not_found";
                    return false;
                }
                try { value = field.GetValue(value); }
                catch { error = "field_unavailable"; return false; }
                if (bracket < 0) continue;
                int index = int.Parse(segment.Substring(bracket + 1, segment.Length - bracket - 2),
                    System.Globalization.CultureInfo.InvariantCulture);
                if (!TryDiagnosticList(value, out IList list))
                {
                    error = "index_requires_zero_based_array_or_exact_List";
                    return false;
                }
                if (index >= list.Count)
                {
                    error = "index_out_of_range";
                    return false;
                }
                value = list[index];
            }
            return true;
        }

        private static FieldInfo FindDiagnosticField(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public |
                                       BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var current = type; current != null; current = current.BaseType)
            {
                var field = current.GetField(name, flags);
                if (field != null) return field;
                var backing = current.GetField("<" + name + ">k__BackingField", flags);
                if (backing != null && backing.IsDefined(typeof(CompilerGeneratedAttribute), false)) return backing;
            }
            return null;
        }

        private static bool TryDiagnosticList(object value, out IList list)
        {
            list = null;
            if (value == null) return false;
            var type = value.GetType();
            if (value is Array array)
            {
                if (array.Rank != 1 || array.GetLowerBound(0) != 0) return false;
            }
            else if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(List<>)) return false;
            list = (IList)value;
            return true;
        }

        private static JToken ConvertDiagnosticValue(object value, int depth, ref int remaining)
        {
            if (remaining-- <= 0) return new JObject { ["$truncated"] = "node_limit" };
            if (value == null || (value is UnityEngine.Object unityObj && unityObj == null)) return JValue.CreateNull();
            var type = value.GetType();
            if (value is string text)
                return text.Length <= DiagnosticMaxString ? new JValue(text) :
                    new JObject { ["value"] = text.Substring(0, DiagnosticMaxString), ["$truncated"] = "string_limit", ["length"] = text.Length };
            // Only known scalar/engine leaves enter the legacy converter. It must never
            // receive an arbitrary class, IList implementation or struct with user code.
            if (type.IsEnum || type == typeof(int) || type == typeof(float) || type == typeof(double) ||
                type == typeof(bool) || type == typeof(long) || type == typeof(byte) || type == typeof(short) ||
                type == typeof(uint) || type == typeof(ulong) || type == typeof(Vector2) || type == typeof(Vector3) ||
                type == typeof(Vector4) || type == typeof(Vector2Int) || type == typeof(Vector3Int) ||
                type == typeof(Color) || type == typeof(Color32) || type == typeof(Quaternion) ||
                type == typeof(Bounds) || type == typeof(Rect) || value is UnityEngine.Object)
                return ConvertValue(value, type, 0, 1, 0, null);
            if (type == typeof(sbyte)) return new JValue((sbyte)value);
            if (type == typeof(ushort)) return new JValue((ushort)value);
            if (type == typeof(char)) return new JValue((char)value);
            if (type == typeof(decimal)) return new JValue((decimal)value);
            if (TryDiagnosticList(value, out IList list))
            {
                var items = new JArray();
                int count = list.Count;
                if (depth < 2)
                    for (int i = 0; i < Math.Min(count, DiagnosticMaxElements) && remaining > 0; i++)
                        items.Add(ConvertDiagnosticValue(list[i], depth + 1, ref remaining));
                return new JObject { ["$type"] = type.FullName, ["count"] = count, ["items"] = items,
                    ["truncated"] = items.Count < count };
            }
            return new JObject { ["$type"] = type.FullName, ["opaque"] = true };
        }
    }
}
