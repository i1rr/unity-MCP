using System;
using I1rr.UnityMcp.Shared;
using UnityEngine;
using UnityEngine.Scripting;

namespace I1rr.UnityMcp.Runtime
{
    /// <summary>
    /// Auto-start mechanism for the MCP runtime server.
    /// Creates a persistent GameObject hosting the server and dispatcher.
    /// </summary>
    [Preserve]
    public static class McpRuntimeBootstrap
    {
        private const string GameObjectName = "[MCP Runtime Server]";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        [Preserve]
        private static void Initialize()
        {
            // RuntimeConsoleTools.RegisterEarly() already ran at BeforeSceneLoad,
            // but ensure it's registered in case execution order varies.
            RuntimeConsoleTools.EnsureRegistered();

            // The Editor owns the project root, MCP sessions, routing and discovery
            // throughout Play Mode. Only a development player starts a TCP server.
            if (Application.isEditor)
            {
                CreateDispatcher();
                return;
            }

            // Set the path validator's project root to the persistent data path.
            // This is the only reliable writable directory across all platforms
            // (Application.dataPath returns the APK path on Android, making its
            // parent unusable as a filesystem root).
            try
            {
                McpPathValidator.ProjectRoot = Application.persistentDataPath;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MCP Runtime] Failed to set McpPathValidator.ProjectRoot: {ex.Message}. " +
                                 "Path validation will reject all paths until ProjectRoot is configured.");
            }

            var go = CreateDispatcher();
            if (go != null) go.AddComponent<McpRuntimeServer>();

            // ProfilerRecorderManager is now self-initializing via its own
            // [RuntimeInitializeOnLoadMethod] — no manual setup needed here.

            Debug.Log("[MCP Runtime] Bootstrap complete.");
        }

        private static GameObject CreateDispatcher()
        {
            if (RuntimeMainThreadDispatcher.Instance != null) return null;
            var go = new GameObject(GameObjectName);
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.DontSave;
            go.AddComponent<RuntimeMainThreadDispatcher>();
            return go;
        }
    }
}
