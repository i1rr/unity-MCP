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
using UnityEngine;
using UnityEngine.Scripting;

namespace I1rr.UnityMcp.Runtime
{
    [Preserve]
    [McpToolCategory("Utility")]
    public static class RuntimeDataTools
    {
        private const int DefaultMaxDepth = 5;

        private static JObject Error(string message) => McpError.Create(McpErrorCodes.InternalError, message);

        #region runtime_get_so_value

        [McpTool("runtime_get_so_value",
            "Read a field value from a loaded ScriptableObject at runtime via reflection.",
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""so_type"": { ""type"": ""string"", ""description"": ""ScriptableObject type name (simple or fully qualified)"" },
                    ""field_path"": { ""type"": ""string"", ""description"": ""Dot-separated field path to read"" }
                },
                ""required"": [""so_type"", ""field_path""]
            }")]
        [Preserve]
        public static JObject GetSoValue(JObject parameters)
        {
            var typeName = parameters["so_type"]?.Value<string>();
            var fieldPath = parameters["field_path"]?.Value<string>();

            if (string.IsNullOrEmpty(typeName)) return Error("'so_type' is required.");
            if (string.IsNullOrEmpty(fieldPath)) return Error("'field_path' is required.");

            var type = FindType(typeName);
            if (type == null) return Error($"Type not found: {typeName}");

            if (!typeof(ScriptableObject).IsAssignableFrom(type))
                return Error($"Type '{typeName}' is not a ScriptableObject.");

            var instances = Resources.FindObjectsOfTypeAll(type);
            if (instances.Length == 0) return Error($"No instances of '{typeName}' found in memory.");

            var results = new JArray();
            foreach (var instance in instances)
            {
                if (instance == null) continue;
                var so = instance as ScriptableObject;
                if (so == null) continue;

                // Skip Unity internals
                if (so.hideFlags.HasFlag(HideFlags.HideAndDontSave)) continue;

                var value = ReflectionReader.ReadFieldValue(so, fieldPath, DefaultMaxDepth);
                results.Add(new JObject
                {
                    ["name"] = so.name,
                    ["instanceId"] = so.GetMcpId(),
                    ["value"] = value
                });
            }

            return new JObject
            {
                ["success"] = true,
                ["so_type"] = typeName,
                ["field_path"] = fieldPath,
                ["count"] = results.Count,
                ["results"] = results
            };
        }

        #endregion

        #region runtime_list_sos

        [McpTool("runtime_list_sos",
            "List all loaded ScriptableObjects at runtime, optionally filtered by type.",
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""type_filter"": { ""type"": ""string"", ""description"": ""Type name filter (partial match)"" },
                    ""include_internal"": { ""type"": ""boolean"", ""description"": ""Include Unity internal SOs (default false)"", ""default"": false }
                }
            }")]
        [Preserve]
        public static JObject ListSos(JObject parameters)
        {
            var typeFilter = parameters["type_filter"]?.Value<string>();
            var includeInternal = parameters["include_internal"]?.Value<bool>() ?? false;

            var allSos = Resources.FindObjectsOfTypeAll<ScriptableObject>();
            var results = new JArray();

            foreach (var so in allSos)
            {
                if (so == null) continue;

                // Filter Unity internals
                if (!includeInternal)
                {
                    if (so.hideFlags.HasFlag(HideFlags.HideAndDontSave)) continue;
                    if (so.hideFlags.HasFlag(HideFlags.DontSaveInEditor)) continue;

                    var ns = so.GetType().Namespace;
                    if (!string.IsNullOrEmpty(ns) && (ns.StartsWith("UnityEngine") || ns.StartsWith("UnityEditor")))
                        continue;
                }

                var typeFull = so.GetType().FullName;
                var typeName = so.GetType().Name;

                if (!string.IsNullOrEmpty(typeFilter))
                {
                    if (typeName.IndexOf(typeFilter, StringComparison.OrdinalIgnoreCase) < 0 &&
                        (typeFull == null || typeFull.IndexOf(typeFilter, StringComparison.OrdinalIgnoreCase) < 0))
                        continue;
                }

                results.Add(new JObject
                {
                    ["name"] = so.name,
                    ["type"] = typeFull,
                    ["instanceId"] = so.GetMcpId()
                });

                if (results.Count >= 500) break;
            }

            return new JObject
            {
                ["success"] = true,
                ["count"] = results.Count,
                ["scriptable_objects"] = results
            };
        }

        #endregion

        private static Type FindType(string typeName)
        {
            foreach (var asm in I1rr.UnityMcp.Shared.UnityCompatibility.GetLoadedAssemblies())
            {
                try
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name == typeName || t.FullName == typeName)
                            return t;
                    }
                }
                catch (ReflectionTypeLoadException) { }
            }
            return null;
        }
    }
}
