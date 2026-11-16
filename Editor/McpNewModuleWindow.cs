using System;
using UnityEditor;
using UnityEngine;

namespace I1rr.UnityMcp.Editor
{
    internal sealed class McpNewModuleWindow : EditorWindow
    {
        private string _id = "Project", _description = "Project-specific MCP tools.", _error;
        internal static void Open()
        {
            var window = GetWindow<McpNewModuleWindow>(true, "New MCP module", true);
            window.minSize = new Vector2(360, 170); window.ShowUtility();
        }
        private void OnGUI()
        {
            _id = EditorGUILayout.TextField("Module ID", _id);
            _description = EditorGUILayout.TextField("Description", _description);
            EditorGUILayout.HelpBox("Create Editor source in this project. Your AI assistant can add tools for your game or framework, then export the module for reuse.", MessageType.Info);
            if (!string.IsNullOrEmpty(_error)) EditorGUILayout.HelpBox(_error, MessageType.Error);
            if (GUILayout.Button("Create module"))
            {
                string id = _id, description = _description;
                EditorApplication.delayCall += () =>
                {
                    if (!this) return;
                    try { McpModuleManager.Create(id, description); Close(); }
                    catch (Exception ex) { _error = ex.Message; Repaint(); }
                };
            }
        }
    }
}
