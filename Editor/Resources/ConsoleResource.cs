using Newtonsoft.Json.Linq;
using UnityEditor;

namespace I1rr.UnityMcp.Editor
{
    /// <summary>
    /// MCP resource: editor://console
    /// Provides a quick snapshot of the last 100 console entries by delegating to ConsoleTools' buffer.
    /// </summary>
    public static class ConsoleResource
    {
        [InitializeOnLoadMethod]
        private static void Register()
        {
            McpResourceRegistry.Register(
                "editor://console",
                "Console Logs",
                "Last 100 console log entries (snapshot). Use the get_console_logs tool for filtered/paginated access.",
                GetConsoleLogs);
        }

        private static JObject GetConsoleLogs()
        {
            var parameters = new JObject
            {
                ["count"] = 100
            };

            return ConsoleTools.GetConsoleLogs(parameters);
        }
    }
}
