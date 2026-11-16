using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace I1rr.UnityMcp.Editor
{
    [McpToolCategory("Editor")]
    public static class ToolCatalog
    {
        private static string ProfilePath => Path.Combine(Path.GetDirectoryName(Application.dataPath),
            "ProjectSettings", "i1rr-unity-mcp-profile.txt");

        public static bool Focused
        {
            get => IsFocused(ProfilePath);
            set => File.WriteAllText(ProfilePath, value ? "Focused\n" : "Full\n");
        }

        internal static bool IsFocused(string path) => !File.Exists(path) || File.ReadAllText(path).Trim() != "Full";

        private static readonly HashSet<string> Everyday = new(StringComparer.Ordinal)
        {
            "discover_tools", "get_editor_state", "refresh_assets", "get_refresh_status",
            "get_compilation_errors", "get_console_logs", "list_tests", "run_tests", "get_test_run",
            "get_hierarchy_summary", "find_gameobjects", "get_components", "get_component_properties",
            "get_asset_object_index", "reimport_asset", "search_assets", "get_loaded_scenes",
            "enter_play_mode", "exit_play_mode", "take_screenshot", "wait_for_frames", "execute_menu_item",
            "capture_game_view_sequence", "get_game_view_sequence"
        };

        internal static JArray Advertise(JObject parameters)
        {
            var all = McpToolRegistry.ListTools((string)parameters?["category"]);
            if ((string)parameters?["profile"] == "full" || !Focused) return all;
            return new JArray(all.Children<JObject>().Where(t => Everyday.Contains((string)t["name"])));
        }

        [McpTool("discover_tools", "Search the full Editor tool catalog by intent or category. Supply an exact name for its argument schema. Specialist tools remain callable by name.",
            InputSchemaJson = @"{""type"":""object"",""properties"":{
                ""query"":{""type"":""string"",""description"":""Substring of the name or description""},
                ""category"":{""type"":""string""},""name"":{""type"":""string"",""description"":""Exact tool name for its full schema""}}}")]
        public static JObject Discover(JObject args)
        {
            var tools = McpToolRegistry.ListTools((string)args["category"]).Children<JObject>();
            string name = (string)args["name"];
            if (!string.IsNullOrEmpty(name))
            {
                var match = tools.FirstOrDefault(t => (string)t["name"] == name);
                return match ?? McpError.Create(McpErrorCodes.NotFound, "Unknown tool: " + name);
            }
            string query = (string)args["query"] ?? "";
            var names = tools.Where(t => ((string)t["name"]).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                ((string)t["description"] ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(t => (string)t["name"]).OrderBy(n => n, StringComparer.Ordinal);
            return new JObject { ["names"] = new JArray(names) };
        }
    }
}
