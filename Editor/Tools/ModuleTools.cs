using System;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;

namespace I1rr.UnityMcp.Editor
{
    [McpToolCategory("Modules")]
    public static class ModuleTools
    {
        [McpTool("manage_module", "Create project-local Editor MCP modules, enable/disable tools, or inspect/import/export source archives. Use get_module_status first. Inspect source before importing; import requires its sha256 and starts with tools disabled.",
            InputSchemaJson = @"{""type"":""object"",""properties"":{
                ""action"":{""type"":""string"",""enum"":[""create"",""set_enabled"",""inspect_import"",""import"",""export"",""refresh""]},
                ""id"":{""type"":""string"",""description"":""Module ID; for creation use an identifier such as GameTools.""},
                ""description"":{""type"":""string""},""enabled"":{""type"":""boolean""},
                ""path"":{""type"":""string"",""description"":""Archive path inside this Unity project; export may omit it for a private default path.""},
                ""sha256"":{""type"":""string"",""description"":""Hash returned by inspect_import, required for import.""}},""required"":[""action""]}")]
        public static JObject Manage(JObject args)
        {
            try
            {
                McpModuleManager.EnsureInstalled();
                string id = (string)args?["id"];
                switch ((string)args?["action"])
                {
                    case "create": return McpModuleManager.Create(id, (string)args["description"]);
                    case "set_enabled":
                        if (args["enabled"]?.Type != JTokenType.Boolean) throw new ArgumentException("enabled must be a boolean.");
                        McpModuleManager.SetEnabled(id, (bool)args["enabled"]); return McpModuleManager.Snapshot();
                    case "inspect_import": return McpProjectModules.InspectArchive(McpModuleManager.Root, (string)args["path"]);
                    case "import": return McpModuleManager.Import((string)args["path"], (string)args["sha256"]);
                    case "export": return McpProjectModules.Export(McpModuleManager.Root, id, (string)args["path"]);
                    case "refresh": McpToolRegistry.RecheckModuleAvailability(); return McpModuleManager.Snapshot();
                    default: throw new ArgumentException("Unknown module action.");
                }
            }
            catch (Exception ex) { return McpError.Create(McpErrorCodes.InvalidParameter, ex.Message); }
        }
    }
}
