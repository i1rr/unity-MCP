using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;

namespace I1rr.UnityMcp.Editor
{
    [McpToolCategory("Utility")]
    public static class TypeReflectionTools
    {
        private const int DefaultPageSize = 100;
        private const int MaxPageSize = 500;

        #region get_all_types

        [McpTool("get_all_types",
            "List types from loaded assemblies. Supports filtering by assembly name, base type, and name filter. Paginated (1-indexed).",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""assembly"": { ""type"": ""string"", ""description"": ""Filter by assembly name (e.g. 'Assembly-CSharp'). If omitted, searches all assemblies."" },
                    ""base_type"": { ""type"": ""string"", ""description"": ""Filter types that derive from this base type (full or simple name)."" },
                    ""filter"": { ""type"": ""string"", ""description"": ""Filter type names by case-insensitive Contains match."" },
                    ""page_size"": { ""type"": ""integer"", ""description"": ""Results per page (default 100, max 500)."", ""default"": 100 },
                    ""page"": { ""type"": ""integer"", ""description"": ""Page number (1-indexed, default 1)."", ""default"": 1 }
                }
            }")]
        public static JObject GetAllTypes(JObject parameters)
        {
            var assemblyFilter = parameters["assembly"]?.Value<string>();
            var baseTypeFilter = parameters["base_type"]?.Value<string>();
            var nameFilter = parameters["filter"]?.Value<string>();
            var pageSize = parameters["page_size"]?.Value<int>() ?? DefaultPageSize;
            var page = parameters["page"]?.Value<int>() ?? 1;

            pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
            if (page < 1) page = 1;

            var assemblies = I1rr.UnityMcp.Shared.UnityCompatibility.GetLoadedAssemblies();

            if (!string.IsNullOrEmpty(assemblyFilter))
            {
                assemblies = assemblies
                    .Where(a => a.GetName().Name.Equals(assemblyFilter, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (assemblies.Count == 0)
                {
                    var err = McpError.Create(McpErrorCodes.NotFound, $"Assembly '{assemblyFilter}' not found.");
                    err["available_assemblies"] = new JArray(
                            I1rr.UnityMcp.Shared.UnityCompatibility.GetLoadedAssemblies()
                                .Select(a => a.GetName().Name)
                                .Where(n => n.StartsWith("Assembly-CSharp") || n.StartsWith("Unity") || n.StartsWith("MCP"))
                                .OrderBy(n => n)
                                .Take(50)
                                .ToArray());
                    return err;
                }
            }

            Type baseType = null;
            if (!string.IsNullOrEmpty(baseTypeFilter))
            {
                baseType = FindTypeByName(baseTypeFilter);
                if (baseType == null)
                {
                    return McpError.Create(McpErrorCodes.NotFound, $"Base type '{baseTypeFilter}' not found in loaded assemblies.");
                }
            }

            var types = new List<(Type type, string assemblyName)>();

            foreach (var asm in assemblies)
            {
                string asmName = asm.GetName().Name;
                Type[] exportedTypes;
                try
                {
                    exportedTypes = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    exportedTypes = ex.Types.Where(t => t != null).ToArray();
                }

                foreach (var t in exportedTypes)
                {
                    if (!t.IsPublic && !t.IsNestedPublic)
                        continue;

                    if (baseType != null && !baseType.IsAssignableFrom(t))
                        continue;

                    if (!string.IsNullOrEmpty(nameFilter) &&
                        t.Name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0 &&
                        (t.FullName == null || t.FullName.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0))
                        continue;

                    types.Add((t, asmName));
                }
            }

            types.Sort((a, b) => string.Compare(a.type.FullName ?? a.type.Name, b.type.FullName ?? b.type.Name, StringComparison.Ordinal));

            int totalCount = types.Count;
            int pageCount = (totalCount + pageSize - 1) / pageSize;
            if (page > pageCount && pageCount > 0) page = pageCount;

            var pageItems = types.Skip((page - 1) * pageSize).Take(pageSize);

            var typeEntries = new JArray();
            foreach (var (t, asmName) in pageItems)
            {
                typeEntries.Add(new JObject
                {
                    ["name"] = t.Name,
                    ["fullName"] = t.FullName ?? t.Name,
                    ["assembly"] = asmName,
                    ["isAbstract"] = t.IsAbstract,
                    ["baseType"] = t.BaseType?.FullName
                });
            }

            return new JObject
            {
                ["types"] = typeEntries,
                ["total_count"] = totalCount,
                ["page"] = page,
                ["page_count"] = pageCount
            };
        }

        #endregion

        #region get_type_info

        [McpTool("get_type_info",
            "Get detailed type information: fields, properties, methods, base class, interfaces, and attributes. Shows public and serializable members.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""type_name"": { ""type"": ""string"", ""description"": ""Full or simple type name to inspect (e.g. 'UnityEngine.Transform' or 'MonoBehaviour')."" }
                },
                ""required"": [""type_name""]
            }")]
        public static JObject GetTypeInfo(JObject parameters)
        {
            var typeName = parameters["type_name"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(typeName))
                return Error("Parameter 'type_name' is required.");

            var type = FindTypeByName(typeName);
            if (type == null)
                return Error($"Type '{typeName}' not found in loaded assemblies.");

            var result = new JObject
            {
                ["name"] = type.Name,
                ["fullName"] = type.FullName,
                ["assembly"] = type.Assembly.GetName().Name,
                ["isAbstract"] = type.IsAbstract,
                ["isInterface"] = type.IsInterface,
                ["isEnum"] = type.IsEnum,
                ["isSealed"] = type.IsSealed,
                ["baseType"] = type.BaseType?.FullName
            };

            // Interfaces
            var interfaces = type.GetInterfaces();
            result["interfaces"] = new JArray(interfaces.Select(i => i.FullName ?? i.Name).ToArray());

            // Attributes
            var attrs = type.GetCustomAttributes(false);
            result["attributes"] = new JArray(attrs.Select(a => a.GetType().Name).ToArray());

            // Fields (public + serializable)
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Concat(type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(f => f.GetCustomAttribute<UnityEngine.SerializeField>() != null));

            var fieldArray = new JArray();
            foreach (var f in fields)
            {
                var fieldObj = new JObject
                {
                    ["name"] = f.Name,
                    ["type"] = FormatTypeName(f.FieldType),
                    ["isPublic"] = f.IsPublic,
                    ["isStatic"] = f.IsStatic
                };

                var fieldAttrs = f.GetCustomAttributes(false);
                if (fieldAttrs.Length > 0)
                    fieldObj["attributes"] = new JArray(fieldAttrs.Select(a => a.GetType().Name).ToArray());

                fieldArray.Add(fieldObj);
            }
            result["fields"] = fieldArray;

            // Properties (public, declared)
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            var propArray = new JArray();
            foreach (var p in properties)
            {
                propArray.Add(new JObject
                {
                    ["name"] = p.Name,
                    ["type"] = FormatTypeName(p.PropertyType),
                    ["canRead"] = p.CanRead,
                    ["canWrite"] = p.CanWrite,
                    ["isStatic"] = (p.GetMethod ?? p.SetMethod)?.IsStatic ?? false
                });
            }
            result["properties"] = propArray;

            // Methods (public, declared, no property accessors or object methods)
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName);

            var methodArray = new JArray();
            foreach (var m in methods)
            {
                var methodObj = new JObject
                {
                    ["name"] = m.Name,
                    ["returnType"] = FormatTypeName(m.ReturnType),
                    ["isStatic"] = m.IsStatic,
                    ["isVirtual"] = m.IsVirtual
                };

                var prms = m.GetParameters();
                if (prms.Length > 0)
                {
                    methodObj["parameters"] = new JArray(prms.Select(p => new JObject
                    {
                        ["name"] = p.Name,
                        ["type"] = FormatTypeName(p.ParameterType),
                        ["isOptional"] = p.IsOptional
                    }).ToArray());
                }

                var methodAttrs = m.GetCustomAttributes(false);
                if (methodAttrs.Length > 0)
                    methodObj["attributes"] = new JArray(methodAttrs.Select(a => a.GetType().Name).ToArray());

                methodArray.Add(methodObj);
            }
            result["methods"] = methodArray;

            // Enum values
            if (type.IsEnum)
            {
                var names = Enum.GetNames(type);
                var values = Enum.GetValues(type);
                var enumArray = new JArray();
                for (int i = 0; i < names.Length; i++)
                {
                    enumArray.Add(new JObject
                    {
                        ["name"] = names[i],
                        ["value"] = Convert.ToInt64(values.GetValue(i))
                    });
                }
                result["enumValues"] = enumArray;
            }

            return result;
        }

        #endregion

        #region Helpers

        private static Type FindTypeByName(string typeName)
        {
            // Try exact full name first
            foreach (var asm in I1rr.UnityMcp.Shared.UnityCompatibility.GetLoadedAssemblies())
            {
                try
                {
                    var type = asm.GetType(typeName, false, true);
                    if (type != null) return type;
                }
                catch { }
            }

            // Try simple name match
            foreach (var asm in I1rr.UnityMcp.Shared.UnityCompatibility.GetLoadedAssemblies())
            {
                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray();
                }

                foreach (var t in types)
                {
                    if (t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase))
                        return t;
                }
            }

            return null;
        }

        private static string FormatTypeName(Type type)
        {
            if (type == null) return "void";
            if (type == typeof(void)) return "void";
            if (type == typeof(int)) return "int";
            if (type == typeof(float)) return "float";
            if (type == typeof(double)) return "double";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(string)) return "string";
            if (type == typeof(object)) return "object";

            if (type.IsGenericType)
            {
                var genericDef = type.GetGenericTypeDefinition();
                var baseName = genericDef.Name;
                int backtickIndex = baseName.IndexOf('`');
                if (backtickIndex > 0) baseName = baseName.Substring(0, backtickIndex);

                var genericArgs = type.GetGenericArguments();
                return $"{baseName}<{string.Join(", ", genericArgs.Select(FormatTypeName))}>";
            }

            if (type.IsArray)
                return $"{FormatTypeName(type.GetElementType())}[]";

            return type.Name;
        }

        private static JObject Error(string message)
        {
            return McpError.Create(McpErrorCodes.InternalError, message);
        }

        #endregion
    }
}
