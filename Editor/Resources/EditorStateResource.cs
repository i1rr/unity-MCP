using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace I1rr.UnityMcp.Editor
{
    /// <summary>
    /// MCP resource: editor://state
    /// Provides a snapshot of the Unity Editor's current state.
    /// </summary>
    public static class EditorStateResource
    {
        [InitializeOnLoadMethod]
        private static void Register()
        {
            McpResourceRegistry.Register(
                "editor://state",
                "Editor State",
                "Current Unity Editor state: play mode, compilation status, active scene, platform, and version.",
                GetEditorState);
        }

        private static JObject GetEditorState()
        {
            var activeScene = SceneManager.GetActiveScene();

            return new JObject
            {
                ["isPlaying"] = EditorApplication.isPlaying,
                ["isPaused"] = EditorApplication.isPaused,
                ["isCompiling"] = EditorApplication.isCompiling,
                ["hasCompilationErrors"] = EditorUtility.scriptCompilationFailed,
                ["activeScene"] = new JObject
                {
                    ["name"] = activeScene.name,
                    ["path"] = activeScene.path,
                    ["isDirty"] = activeScene.isDirty,
                    ["isLoaded"] = activeScene.isLoaded
                },
                ["platform"] = EditorUserBuildSettings.activeBuildTarget.ToString(),
                ["unityVersion"] = Application.unityVersion
            };
        }
    }
}
