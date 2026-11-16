using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace I1rr.UnityMcp.Shared
{
    /// <summary>
    /// MCP tool that reports the status of all discovered modules.
    /// </summary>
    [McpToolCategory("Utility")]
    public static class ModuleStatusTools
    {
        public static Func<JObject> StatusProvider { get; set; }
        [McpTool("get_module_status",
            "Inspect MCP module availability, requested enable state, assembly and tool counts. The Editor also includes known integrations whose dependencies are not installed.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {}
            }")]
        public static JObject GetModuleStatus(JObject parameters)
        {
            if (StatusProvider != null) return StatusProvider();
            var modules = McpToolRegistry.Modules;
            var allTools = McpToolRegistry.ListTools();
            var modulesArray = new JArray();

            foreach (var module in modules)
            {
                string status;
                try
                {
                    status = McpToolRegistry.IsModuleAvailable(module) ? "available" : "unavailable";
                }
                catch (Exception ex)
                {
                    status = $"error: {ex.Message}";
                }

                var moduleAssembly = module.GetType().Assembly;
                var assemblyName = moduleAssembly.GetName().Name;
                var assemblyVersion = moduleAssembly.GetName().Version?.ToString() ?? "unknown";

                // Count tools in this module's assembly
                int toolCount = 0;
                if (allTools != null)
                {
                    // Count tools whose category matches or that come from this module
                    foreach (var tool in allTools)
                    {
                        if (tool is JObject toolObj)
                        {
                            var category = toolObj["category"]?.ToString();
                            if (string.Equals(category, module.ModuleName, StringComparison.OrdinalIgnoreCase))
                                toolCount++;
                        }
                    }
                }

                // Get referenced assemblies as dependency info
                var deps = moduleAssembly.GetReferencedAssemblies()
                    .Where(r => !r.Name.StartsWith("System", StringComparison.Ordinal)
                             && !r.Name.StartsWith("mscorlib", StringComparison.Ordinal)
                             && !r.Name.StartsWith("netstandard", StringComparison.Ordinal)
                             && !r.Name.StartsWith("Unity", StringComparison.Ordinal)
                             && !r.Name.StartsWith("Newtonsoft", StringComparison.Ordinal)
                             && !r.Name.StartsWith("I1rr.UnityMcp", StringComparison.Ordinal))
                    .Select(r => r.Name)
                    .ToArray();

                modulesArray.Add(new JObject
                {
                    ["name"] = module.ModuleName,
                    ["assembly"] = assemblyName,
                    ["version"] = assemblyVersion,
                    ["status"] = status,
                    ["tool_count"] = toolCount,
                    ["dependencies"] = new JArray(deps)
                });
            }

            return new JObject
            {
                ["module_count"] = modules.Count,
                ["modules"] = modulesArray
            };
        }
    }
}
