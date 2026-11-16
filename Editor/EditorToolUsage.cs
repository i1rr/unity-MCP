using System.IO;
using I1rr.UnityMcp.Shared;
using UnityEditor;
using UnityEngine;

namespace I1rr.UnityMcp.Editor
{
    [InitializeOnLoad]
    internal static class EditorToolUsage
    {
        internal static readonly ToolUsageStore Store = new ToolUsageStore(
            Path.Combine(Path.GetDirectoryName(Application.dataPath), "UserSettings", "i1rr-unity-mcp-usage.json"));
        private static double _lastFlush;

        static EditorToolUsage()
        {
            McpToolRegistry.ToolInvoked = Store.Record;
            AssemblyReloadEvents.beforeAssemblyReload += Store.Flush;
            EditorApplication.quitting += Store.Flush;
            EditorApplication.update += () =>
            {
                if (EditorApplication.timeSinceStartup - _lastFlush < 5) return;
                _lastFlush = EditorApplication.timeSinceStartup;
                Store.Flush();
            };
        }
    }
}
