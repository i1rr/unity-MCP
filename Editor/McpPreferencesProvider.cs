using UnityEditor;
using UnityEngine;

namespace I1rr.UnityMcp.Editor
{
    /// <summary>
    /// Minimal Project Settings stub that redirects users to the dockable MCP Dashboard.
    /// Retains the <see cref="EnableCodeExecution"/> property for backward compatibility.
    /// </summary>
    public sealed class McpPreferencesProvider : SettingsProvider
    {
        private const string SettingsPath = "Project/i1rr-unity-mcp";
        internal static string CodeExecutionKey(string projectAssets) => "i1rr-unity-mcp.EnableCodeExecution:" + projectAssets;

        // Editor transport always binds loopback. Runtime/LAN authentication is separate.
        public static bool RequireAuthentication
        {
            get => EditorPrefs.GetBool("MCP_RequireAuthentication:" + Application.dataPath, false);
            set
            {
                EditorPrefs.SetBool("MCP_RequireAuthentication:" + Application.dataPath, value);
                McpEditorServer._requireAuthentication = value;
                if (McpEditorServer.IsRunning)
                    ServerLifecycleManager.WriteMcpJsonIfNeeded(McpEditorServer.Port);
            }
        }

        /// <summary>
        /// Whether the execute_code tool is enabled.
        /// Defaults to OFF for security — arbitrary C# execution requires explicit opt-in.
        /// Persisted per-project via EditorPrefs.
        /// </summary>
        public static bool EnableCodeExecution
        {
            get => EditorPrefs.GetBool(CodeExecutionKey(Application.dataPath), false);
            set => EditorPrefs.SetBool(CodeExecutionKey(Application.dataPath), value);
        }

        private McpPreferencesProvider()
            : base(SettingsPath, SettingsScope.Project)
        {
            label = "i1rr-unity-mcp";
            keywords = new System.Collections.Generic.HashSet<string>
            {
                "mcp", "i1rr-unity-mcp", "server", "token", "port"
            };
        }

        [SettingsProvider]
        public static SettingsProvider CreateProvider()
        {
            return new McpPreferencesProvider();
        }

        public override void OnGUI(string searchContext)
        {
            EditorGUILayout.Space(12);

            EditorGUILayout.HelpBox(
                "i1rr-unity-mcp settings have moved to the dockable Dashboard window.\n" +
                "Open it via  Tools → i1rr-unity-mcp Dashboard.",
                MessageType.Info);

            EditorGUILayout.Space(8);

            if (GUILayout.Button("Open MCP Dashboard", GUILayout.Height(30)))
            {
                McpDashboardWindow.ShowWindow();
            }
        }
    }
}
