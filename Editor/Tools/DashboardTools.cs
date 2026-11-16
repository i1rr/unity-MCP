using System.Linq;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace I1rr.UnityMcp.Editor
{
    [McpToolCategory("Editor")]
    public static class DashboardTools
    {
        [McpTool("get_dashboard_state", "Read the open MCP Dashboard's cached state and refresh counters. Does not open a window or expose credentials.")]
        public static JObject Inspect(JObject args)
        {
            var window = Resources.FindObjectsOfTypeAll<McpDashboardWindow>().FirstOrDefault();
            return window ? window.InspectSnapshot() : new JObject { ["open"] = false };
        }

        [McpTool("show_dashboard", "Open the Unity MCP Dashboard and optionally select a tab. Uses Unity APIs, not desktop automation.",
            InputSchemaJson = @"{""type"":""object"",""properties"":{""tab"":{""type"":""string"",""enum"":[""Overview"",""Tools"",""Settings"",""Activity""]}}}")]
        public static JObject Show(JObject args)
        {
            string tab = (string)args?["tab"];
            if (tab != null && !new[] { "Overview", "Tools", "Settings", "Activity" }.Contains(tab))
                return McpError.Create(McpErrorCodes.InvalidParameter, "Unknown Dashboard tab.");
            McpDashboardWindow.ShowWindow();
            var window = EditorWindow.GetWindow<McpDashboardWindow>();
            if (tab != null) window.SelectTab(tab);
            return window.InspectSnapshot();
        }
    }
}
