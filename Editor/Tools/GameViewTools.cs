using System;
using System.Linq;
using System.Reflection;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace I1rr.UnityMcp.Editor
{
    // Unity exposes no public API for Game View size presets. Keep the adapter
    // here, fail explicitly on unsupported Editors, and never resize an OS window.
    [InitializeOnLoad, McpToolCategory("Editor")]
    public static class GameViewTools
    {
        private const string SessionKey = "i1rr-unity-mcp.GameViewOverride";
        private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly Assembly EditorAssembly = typeof(UnityEditor.Editor).Assembly;

        static GameViewTools()
        {
            // quitting runs after Unity saves the window layout. Restoring there
            // removes the preset but persists its now-invalid selection index.
            EditorApplication.wantsToQuit += RestoreBeforeQuit;
        }

        internal static bool RestoreBeforeQuit()
        {
            if (SessionState.GetString(SessionKey, "").Length == 0) return true;
            var result = Configure(new JObject { ["restore"] = true });
            if (result["error_code"] != null) Debug.LogWarning("MCP Game View cleanup: " + result);
            return true;
        }

        private sealed class Adapter
        {
            internal readonly Type ViewType = EditorAssembly.GetType("UnityEditor.GameView", true);
            private readonly Type sizeType = EditorAssembly.GetType("UnityEditor.GameViewSize", true);
            internal readonly object Group;
            internal readonly string GroupName;
            internal readonly PropertyInfo Selection;

            internal Adapter(string groupName = null)
            {
                var sizesType = EditorAssembly.GetType("UnityEditor.GameViewSizes", true);
                var sizes = sizesType.GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Static | BindingFlags.FlattenHierarchy).GetValue(null);
                var groupType = EditorAssembly.GetType("UnityEditor.GameViewSizeGroupType", true);
                object group = groupName == null ? sizesType.GetProperty("currentGroupType", Instance).GetValue(sizes) : Enum.Parse(groupType, groupName);
                GroupName = group.ToString();
                Group = sizesType.GetMethod("GetGroup", Instance).Invoke(sizes, new[] { group });
                Selection = ViewType.GetProperty("selectedSizeIndex", Instance);
                if (Selection == null || !Selection.CanWrite) throw new NotSupportedException("Game View selection API is unavailable.");
            }

            internal int Count => (int)Call("GetTotalCount");
            internal int Builtins => (int)Call("GetBuiltinCount");
            internal object Call(string method, params object[] args) => Group.GetType().GetMethod(method, Instance).Invoke(Group, args);
            internal JObject Size(int index)
            {
                var size = Call("GetGameViewSize", index);
                return new JObject { ["index"] = index, ["label"] = (string)sizeType.GetProperty("baseText", Instance).GetValue(size),
                    ["width"] = (int)sizeType.GetProperty("width", Instance).GetValue(size),
                    ["height"] = (int)sizeType.GetProperty("height", Instance).GetValue(size),
                    ["size_type"] = sizeType.GetProperty("sizeType", Instance).GetValue(size).ToString() };
            }
            internal int Find(JObject descriptor)
            {
                for (int i = 0; i < Count; i++)
                {
                    var size = Size(i);
                    if ((string)size["label"] == (string)descriptor["label"] && (int)size["width"] == (int)descriptor["width"] &&
                        (int)size["height"] == (int)descriptor["height"] && (string)size["size_type"] == (string)descriptor["size_type"]) return i;
                }
                return -1;
            }
            internal int FindOwned(string label)
            {
                for (int i = Builtins; i < Count; i++) if ((string)Size(i)["label"] == label) return i;
                return -1;
            }
            internal void Add(int width, int height, string label)
            {
                var kind = Enum.Parse(EditorAssembly.GetType("UnityEditor.GameViewSizeType", true), "FixedResolution");
                var size = Activator.CreateInstance(sizeType, Instance, null, new object[] { kind, width, height, label }, null);
                Call("AddCustomSize", size);
            }
            internal EditorWindow View(bool create)
            {
                var views = Resources.FindObjectsOfTypeAll(ViewType).Cast<EditorWindow>().ToArray();
                if (views.Length > 1) throw new NotSupportedException("Multiple Game Views are open. Keep one open to identify the target unambiguously.");
                return views.FirstOrDefault() ?? (create ? EditorWindow.GetWindow(ViewType) : null);
            }
        }

        private static JObject Saved()
        {
            string value = SessionState.GetString(SessionKey, "");
            return value.Length == 0 ? null : JObject.Parse(value);
        }

        [McpTool("get_game_view_state", "Inspect the existing Game View's selected render resolution, focus and temporary MCP override without opening or focusing a window. Screenshot width/height only resize the image; use configure_game_view to change actual layout.",
            InputSchemaJson = @"{""type"":""object"",""properties"":{}}")]
        public static JObject GetState(JObject args)
        {
            try { return State(new Adapter()); }
            catch (Exception e) { return Unsupported(e); }
        }

        private static JObject State(Adapter adapter)
        {
            var view = adapter.View(false);
            var result = new JObject { ["open"] = view != null, ["size_group"] = adapter.GroupName,
                ["preset_count"] = adapter.Count, ["custom_preset_count"] = adapter.Count - adapter.Builtins,
                ["override_active"] = Saved() != null, ["is_playing"] = EditorApplication.isPlaying,
                ["is_paused"] = EditorApplication.isPaused };
            if (view == null) return result;
            result["focused"] = EditorWindow.focusedWindow == view;
            result["selected_size"] = adapter.Size((int)adapter.Selection.GetValue(view));
            // Screen dimensions are the last rendered frame, not an acknowledgement
            // that a newly requested layout has settled.
            result["last_rendered_width"] = Screen.width;
            result["last_rendered_height"] = Screen.height;
            result["completion_scope"] = "selection_only";
            return result;
        }

        [McpTool("configure_game_view", "Open/focus a single Game View and optionally set its actual fixed render resolution (64-4096 each). Repeated calls replace one temporary preset. Call restore=true afterward to restore the prior selection and remove that preset; survives domain reload and attempts cleanup on ordinary Editor quit. Returns a request acknowledgement: allow frames and verify a composited screenshot. Cannot dismiss native dialogs or activate an occluded/minimized OS window.",
            InputSchemaJson = @"{""type"":""object"",""properties"":{
                ""width"":{""type"":""integer"",""minimum"":64,""maximum"":4096},
                ""height"":{""type"":""integer"",""minimum"":64,""maximum"":4096},
                ""focus"":{""type"":""boolean"",""default"":true},""restore"":{""type"":""boolean"",""default"":false}}}")]
        public static JObject Configure(JObject args)
        {
            bool hasWidth = args["width"] != null, hasHeight = args["height"] != null;
            if ((args["restore"] != null && args["restore"].Type != JTokenType.Boolean) ||
                (args["focus"] != null && args["focus"].Type != JTokenType.Boolean)) return Invalid("focus and restore must be booleans.");
            bool restore = (bool?)args["restore"] ?? false;
            if (restore && (hasWidth || hasHeight || args["focus"] != null)) return Invalid("restore cannot be combined with width, height or focus.");
            if (hasWidth != hasHeight) return Invalid("Supply both width and height.");
            int width = 0, height = 0;
            if (hasWidth && (args["width"].Type != JTokenType.Integer || args["height"].Type != JTokenType.Integer ||
                !int.TryParse(args["width"].ToString(), out width) || !int.TryParse(args["height"].ToString(), out height) ||
                width < 64 || width > 4096 || height < 64 || height > 4096)) return Invalid("Use integer width and height from 64 to 4096.");
            try
            {
                var saved = Saved();
                if (restore && saved == null) return new JObject { ["status"] = "not_overridden" };
                var adapter = new Adapter();
                if (saved != null && (string)saved["group"] != adapter.GroupName)
                    return McpError.Create(McpErrorCodes.InvalidState, "Return to Game View size group " + saved["group"] + " before changing or restoring the override.");
                var view = adapter.View(!restore);
                if (restore)
                {
                    int original = adapter.Find((JObject)saved["original"]);
                    if (original < 0) return McpError.Create(McpErrorCodes.InvalidState, "The original Game View preset was removed. Restore it before retrying cleanup.");
                    if (view != null) adapter.Selection.SetValue(view, original);
                    int owned = adapter.FindOwned((string)saved["label"]);
                    if (owned >= 0) adapter.Call("RemoveCustomSize", owned);
                    SessionState.EraseString(SessionKey);
                    view?.Repaint();
                    var restored = State(adapter);
                    restored["status"] = "restored";
                    return restored;
                }
                if (hasWidth)
                {
                    if (saved == null)
                    {
                        // Unity truncates preset labels at 40 characters. Keep
                        // ownership intact or replacement/cleanup cannot find it.
                        saved = new JObject { ["group"] = adapter.GroupName, ["label"] = "MCP " + Guid.NewGuid().ToString("N"),
                            ["original"] = adapter.Size((int)adapter.Selection.GetValue(view)) };
                        // Save before mutation, so an interrupted response remains recoverable.
                        SessionState.SetString(SessionKey, saved.ToString());
                    }
                    int original = adapter.Find((JObject)saved["original"]);
                    if (original < 0) return McpError.Create(McpErrorCodes.InvalidState, "The original Game View preset was removed. Restore it before retrying.");
                    adapter.Selection.SetValue(view, original);
                    int owned = adapter.FindOwned((string)saved["label"]);
                    if (owned >= 0) adapter.Call("RemoveCustomSize", owned);
                    adapter.Add(width, height, (string)saved["label"]);
                    adapter.Selection.SetValue(view, adapter.Count - 1);
                }
                if ((bool?)args["focus"] ?? true) { view.Show(); view.Focus(); }
                view.Repaint();
                var result = State(adapter);
                result["status"] = "requested";
                return result;
            }
            catch (Exception e) { return Unsupported(e); }
        }

        private static JObject Invalid(string message) => McpError.Create(McpErrorCodes.InvalidParameter, message);
        private static JObject Unsupported(Exception e) => McpError.Create(McpErrorCodes.InvalidState,
            "Game View controls are unavailable in this Editor: " + (e.InnerException ?? e).Message +
            ". No OS-window or dialog control is provided. If an override is active, inspect state before retrying.");
    }
}
