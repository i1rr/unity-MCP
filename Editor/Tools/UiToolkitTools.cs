using System;
using System.Collections.Generic;
using System.Globalization;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace I1rr.UnityMcp.Editor
{
    // Editor Play Mode only. These are semantic game-UI operations, not OS input.
    [InitializeOnLoad, McpToolCategory("UI")]
    public static class UiToolkitTools
    {
        internal const int HandleLimit = 512;
        internal const double HandleLifetime = 120;
        private const int ScanLimit = 4096, AncestorLimit = 64;
        private static readonly Dictionary<string, Target> Targets = new Dictionary<string, Target>();
        private static double nextPrune;

        private sealed class Target
        {
            internal WeakReference<UIDocument> Document;
            internal WeakReference<VisualElement> Root, Element;
            internal WeakReference<IPanel> Panel;
            internal EventCallback<DetachFromPanelEvent> Detached;
            internal string Name;
            internal double Created;
        }

        static UiToolkitTools()
        {
            AssemblyReloadEvents.beforeAssemblyReload += ClearHandles;
            EditorApplication.playModeStateChanged += _ => ClearHandles();
            EditorApplication.quitting += ClearHandles;
            EditorApplication.update += () => {
                if (EditorApplication.timeSinceStartup < nextPrune) return;
                nextPrune = EditorApplication.timeSinceStartup + 5;
                Prune(EditorApplication.timeSinceStartup);
            };
        }

        internal static int HandleCount => Targets.Count;
        internal static void ClearHandles()
        {
            foreach (var id in new List<string>(Targets.Keys)) Revoke(id);
        }
        internal static void Prune(double now)
        {
            foreach (var pair in new List<KeyValuePair<string, Target>>(Targets))
                if (now - pair.Value.Created >= HandleLifetime || !pair.Value.Element.TryGetTarget(out _)) Revoke(pair.Key);
        }
        private static void Revoke(string id)
        {
            if (!Targets.TryGetValue(id, out var target)) return;
            Targets.Remove(id);
            if (target.Element.TryGetTarget(out var element)) element.UnregisterCallback(target.Detached);
        }
        private static string Remember(UIDocument document, VisualElement element)
        {
            Prune(EditorApplication.timeSinceStartup);
            if (Targets.Count >= HandleLimit)
            {
                string oldest = null; double time = double.MaxValue;
                foreach (var pair in Targets) if (pair.Value.Created < time) { oldest = pair.Key; time = pair.Value.Created; }
                if (oldest != null) Revoke(oldest);
            }
            string id = Guid.NewGuid().ToString("N");
            var target = new Target { Document = new WeakReference<UIDocument>(document),
                Root = new WeakReference<VisualElement>(document.rootVisualElement),
                Element = new WeakReference<VisualElement>(element), Panel = new WeakReference<IPanel>(element.panel),
                Created = EditorApplication.timeSinceStartup, Name = element.name };
            target.Detached = _ => Revoke(id);
            Targets.Add(id, target);
            element.RegisterCallback(target.Detached);
            return id;
        }

        private static JObject Error(string code, string message) => McpError.Create(code, message);
        private static JObject Ready()
        {
            if (!EditorApplication.isPlaying) return Error(McpErrorCodes.NotInPlayMode, "UI Toolkit inspection/actions require Editor Play Mode.");
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode != EditorApplication.isPlaying)
                return Error(McpErrorCodes.InvalidState, "Wait for a settled Editor Play Mode state.");
            return null;
        }
        private static bool StringArg(JObject args, string name, int max, out string value)
        {
            value = null;
            if (args[name] == null) return true;
            if (args[name].Type != JTokenType.String) return false;
            value = (string)args[name];
            return value.Length > 0 && value.Length <= max;
        }
        private static bool IntArg(JObject args, string name, int fallback, int min, int max, out int value)
        {
            value = fallback;
            return args[name] == null || (args[name].Type == JTokenType.Integer &&
                int.TryParse(args[name].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= min && value <= max);
        }
        private static bool Only(JObject args, params string[] allowed)
        {
            if (args == null) return false;
            foreach (var p in args.Properties()) if (Array.IndexOf(allowed, p.Name) < 0) return false;
            return true;
        }
        private static bool LiveDocument(UIDocument d) => d != null && d.gameObject.scene.IsValid() && d.gameObject.scene.isLoaded && !EditorUtility.IsPersistent(d);
        private static string Id(UIDocument d) => d.GetMcpId().ToString(CultureInfo.InvariantCulture);
        private static string Clip(string text, int max) => text == null ? "" : text.Substring(0, Math.Min(text.Length, max));

        [McpTool("inspect_ui_document", "Inspect live Play Mode UI Toolkit UIDocuments. Omit document to list IDs; select an ID or unique exact object name. Optional element_name must uniquely match. Bounded tree uses base framework state only; text-input contents are redacted. Returns short-lived single-use action handles.", RequiresPlayMode = true,
            InputSchemaJson = @"{""type"":""object"",""additionalProperties"":false,""properties"":{
                ""document"":{""type"":""string"",""maxLength"":128},""element_name"":{""type"":""string"",""maxLength"":128},
                ""max_depth"":{""type"":""integer"",""minimum"":0,""maximum"":16,""default"":6},
                ""max_nodes"":{""type"":""integer"",""minimum"":1,""maximum"":256,""default"":80}}}")]
        public static JObject Inspect(JObject args)
        {
            var ready = Ready(); if (ready != null) return ready;
            if (!Only(args, "document", "element_name", "max_depth", "max_nodes") ||
                !StringArg(args, "document", 128, out var selector) || !StringArg(args, "element_name", 128, out var name) ||
                !IntArg(args, "max_depth", 6, 0, 16, out var depth) || !IntArg(args, "max_nodes", 80, 1, 256, out var count) || (selector == null && name != null))
                return Error(McpErrorCodes.InvalidParameter, "Use bounded document/element strings, depth 0..16 and nodes 1..256; element_name requires document.");
            var documents = new List<UIDocument>();
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!LiveDocument(d)) continue;
                documents.Add(d);
                if (documents.Count > 128) return Error(McpErrorCodes.MaxLimitReached, "More than 128 live UIDocuments; narrow the running scene.");
            }
            if (selector == null)
            {
                var list = new JArray();
                foreach (var d in documents) list.Add(new JObject { ["id"] = Id(d), ["name"] = Clip(d.gameObject.name, 128),
                    ["scene"] = Clip(d.gameObject.scene.name, 128), ["active"] = d.isActiveAndEnabled,
                    ["attached"] = d.rootVisualElement?.panel != null });
                return new JObject { ["documents"] = list, ["count"] = list.Count, ["truncated"] = false };
            }
            // Prefer identity over names; numeric object names never shadow an ID.
            var selected = documents.FindAll(d => Id(d) == selector);
            if (selected.Count == 0) selected = documents.FindAll(d => d.gameObject.name == selector);
            if (selected.Count != 1) return Error(selected.Count == 0 ? McpErrorCodes.NotFound : McpErrorCodes.Ambiguous, "Document must match one live ID or exact object name.");
            var document = selected[0]; var root = document.rootVisualElement;
            if (root == null || root.panel == null || root.panel.contextType != ContextType.Player)
                return Error(McpErrorCodes.InvalidState, "Document has no attached runtime panel.");
            VisualElement start = root;
            if (name != null)
            {
                int matches = 0; bool cut = false;
                Walk(root, AncestorLimit, ScanLimit, (e, _, __) => { if (e.name == name) { start = e; matches++; } }, ref cut);
                if (cut) return Error(McpErrorCodes.MaxLimitReached, "Query could not establish uniqueness within 4096 nodes/64 levels; narrow the document.");
                if (matches != 1) return Error(matches == 0 ? McpErrorCodes.NotFound : McpErrorCodes.Ambiguous, "element_name must uniquely match within the document (text-input internals are opaque).");
            }
            var nodes = new JArray(); bool truncated = false; int textBudget = 4096;
            Walk(start, depth, count, (e, level, parent) => {
                bool sensitive = Sensitive(e); var type = e.GetType();
                var node = new JObject { ["index"] = nodes.Count, ["parent"] = parent, ["depth"] = level,
                    ["name"] = Clip(e.name, 128), ["type"] = Clip(type.FullName, 128), ["enabled"] = e.enabledInHierarchy,
                    ["visible"] = Unavailable(e, false) == null, ["focused"] = ReferenceEquals(e.panel?.focusController.focusedElement, e),
                    ["bounds"] = new JArray(Safe(e.worldBound.x), Safe(e.worldBound.y), Safe(e.worldBound.width), Safe(e.worldBound.height)),
                    ["redacted"] = sensitive };
                if (typeof(TextField).IsAssignableFrom(type)) node["children_opaque"] = true;
                if (!sensitive && (type == typeof(Label) || type == typeof(Button)))
                {
                    string text = ((TextElement)e).text ?? ""; int take = Math.Min(160, textBudget);
                    node["text"] = Clip(text, take); node["text_truncated"] = text.Length > take;
                    if (text.Length > take) truncated = true;
                    textBudget -= Math.Min(text.Length, take);
                }
                if (!sensitive) DescribeValue(e, node);
                var actions = Actions(e, sensitive); node["actions"] = actions;
                if (actions.Count > 0) node["handle"] = Remember(document, e);
                nodes.Add(node);
            }, ref truncated);
            return new JObject { ["document_id"] = Id(document), ["nodes"] = nodes, ["truncated"] = truncated,
                ["handle_lifetime_seconds"] = HandleLifetime, ["handle_capacity"] = HandleLimit,
                ["scope"] = "semantic_ui_only; visibility does not establish absence of overlapping UI or OS occlusion" };
        }

        // Iterative DFS keeps both traversal and stack memory bounded, including wide trees.
        private struct Frame { internal VisualElement Element; internal int Depth, Parent, Index, Child; }
        private static void Walk(VisualElement root, int depth, int count, Action<VisualElement, int, int> visit, ref bool cut)
        {
            var stack = new Stack<Frame>(); int visited = 0;
            stack.Push(new Frame { Element = root, Parent = -1, Child = -1 });
            while (stack.Count > 0)
            {
                var f = stack.Pop();
                if (f.Child == -1)
                {
                    if (visited >= count) { cut = true; return; }
                    f.Index = visited++; visit(f.Element, f.Depth, f.Parent); f.Child = 0;
                    if (typeof(TextField).IsAssignableFrom(f.Element.GetType())) continue;
                    if (f.Depth >= depth) { if (f.Element.hierarchy.childCount > 0) cut = true; continue; }
                }
                if (f.Child >= f.Element.hierarchy.childCount) continue;
                var child = f.Element.hierarchy[f.Child++]; stack.Push(f);
                stack.Push(new Frame { Element = child, Depth = f.Depth + 1, Parent = f.Index, Child = -1 });
            }
        }
        private static float Safe(float v) => float.IsNaN(v) || float.IsInfinity(v) ? 0 : v;
        private static bool Sensitive(VisualElement e)
        {
            for (int i = 0; e != null && i <= AncestorLimit; i++, e = e.parent)
            {
                // Never expose text-input contents or their internal text elements.
                if (typeof(TextField).IsAssignableFrom(e.GetType())) return true;
                string name = e.name ?? "";
                foreach (var word in new[] { "password", "secret", "token", "credential" })
                    if (name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return e != null; // Unknown/deep ancestry fails closed.
        }
        private static string Unavailable(VisualElement e, bool checkEnabled = true)
        {
            if (e.panel == null || (checkEnabled && !e.enabledInHierarchy)) return "detached_or_disabled";
            var rect = e.worldBound;
            if (!(rect.width > 0 && rect.height > 0) || float.IsInfinity(rect.x) || float.IsNaN(rect.x) || float.IsInfinity(rect.y) || float.IsNaN(rect.y)) return "no_layout";
            int depth = 0;
            for (var p = e; p != null; p = p.parent)
            {
                if (++depth > AncestorLimit) return "ancestry_limit";
                var s = p.resolvedStyle;
                if (s.display == DisplayStyle.None || s.visibility == Visibility.Hidden || !(s.opacity > 0)) return "hidden";
                // Resolved overflow is not public in the supported UI Toolkit API.
                // Conservatively require overlap with every ancestor, even when
                // its authored overflow is Visible; never reflect into worldClip.
                if (p != e)
                {
                    var b = p.worldBound;
                    rect = Rect.MinMaxRect(Mathf.Max(rect.xMin, b.xMin), Mathf.Max(rect.yMin, b.yMin), Mathf.Min(rect.xMax, b.xMax), Mathf.Min(rect.yMax, b.yMax));
                    if (!(rect.width > 0 && rect.height > 0)) return "clipped";
                }
            }
            if (!rect.Overlaps(e.panel.visualTree.worldBound)) return "outside_panel";
            return null;
        }
        private static bool Supported(VisualElement e)
        {
            var t = e.GetType();
            return t == typeof(Button) || t == typeof(Toggle) || t == typeof(Slider) || t == typeof(SliderInt) ||
                t == typeof(DropdownField) || t == typeof(IntegerField) || t == typeof(FloatField) || t == typeof(Scroller);
        }
        private static JArray Actions(VisualElement e, bool sensitive)
        {
            var a = new JArray(); if (sensitive || !Supported(e)) return a;
            if (e.focusable) a.Add("focus");
            if (e.GetType() == typeof(Button)) a.Add("submit");
            else if (Writable(e)) a.Add("set_value");
            return a;
        }
        private static bool Writable(VisualElement e) =>
            !(e.GetType() == typeof(IntegerField) && ((IntegerField)e).isReadOnly) &&
            !(e.GetType() == typeof(FloatField) && ((FloatField)e).isReadOnly);
        private static void DescribeValue(VisualElement e, JObject n)
        {
            var t = e.GetType();
            if (t == typeof(Toggle)) { n["value"] = ((Toggle)e).value; n["value_type"] = "boolean"; }
            if (t == typeof(Slider)) { var s = (Slider)e; n["value"] = Safe(s.value); n["minimum"] = Safe(s.lowValue); n["maximum"] = Safe(s.highValue); n["value_type"] = "number"; }
            if (t == typeof(SliderInt)) { var s = (SliderInt)e; n["value"] = s.value; n["minimum"] = s.lowValue; n["maximum"] = s.highValue; n["value_type"] = "integer"; }
            if (t == typeof(IntegerField)) { n["value"] = ((IntegerField)e).value; n["value_type"] = "integer"; }
            if (t == typeof(FloatField)) { n["value"] = Safe(((FloatField)e).value); n["value_type"] = "number"; }
            if (t == typeof(Scroller)) { var s = (Scroller)e; n["value"] = Safe(s.value); n["minimum"] = Safe(s.lowValue); n["maximum"] = Safe(s.highValue); n["value_type"] = "number"; n["axis"] = s.direction.ToString(); }
            if (t == typeof(DropdownField))
            {
                var d = (DropdownField)e; n["value"] = d.index; n["value_type"] = "choice_index"; n["choice_count"] = d.choices.Count;
                // Choice formatting callbacks are user code. Do not call them or enumerate labels.
            }
        }

        [McpTool("act_on_ui_element", "Perform one semantic Play Mode UI Toolkit action on a fresh inspection handle: focus, Button submit (NavigationSubmitEvent), or typed set_value. Handles are single-use and expire/detach. Exact built-in controls only; no text/password editing. Dispatch can invoke game callbacks and persistent side effects. Synchronous scope only; inspect state after errors/timeouts and never automatically retry an unknown outcome.", RequiresPlayMode = true,
            InputSchemaJson = @"{""type"":""object"",""additionalProperties"":false,""required"":[""handle"",""action""],""properties"":{
                ""handle"":{""type"":""string"",""maxLength"":32},""action"":{""type"":""string"",""enum"":[""focus"",""submit"",""set_value""]},
                ""value"":{""type"":[""boolean"",""integer"",""number""]}}}")]
        public static JObject Act(JObject args)
        {
            var ready = Ready(); if (ready != null) return ready;
            if (!Only(args, "handle", "action", "value") || !StringArg(args, "handle", 32, out var id) || id == null ||
                !StringArg(args, "action", 16, out var action) || (action != "focus" && action != "submit" && action != "set_value") ||
                ((action == "set_value") != (args["value"] != null)))
                return Error(McpErrorCodes.InvalidParameter, "Provide a handle and focus/submit, or set_value with a typed value.");
            Prune(EditorApplication.timeSinceStartup);
            if (!Targets.TryGetValue(id, out var t) || !t.Document.TryGetTarget(out var d) || !LiveDocument(d) || !d.isActiveAndEnabled ||
                !t.Root.TryGetTarget(out var root) || !ReferenceEquals(root, d.rootVisualElement) ||
                !t.Element.TryGetTarget(out var e) || !t.Panel.TryGetTarget(out var panel) || !ReferenceEquals(e.panel, panel) ||
                !ReferenceEquals(root.panel, panel) || !root.Contains(e) || e.name != t.Name)
            { Revoke(id); return Error(McpErrorCodes.InvalidState, "Stale or detached target; inspect again."); }
            string unavailable = Unavailable(e);
            if (unavailable != null) return Error(McpErrorCodes.InvalidState, "Target is unavailable: " + unavailable);
            if (!Supported(e) || Sensitive(e) || (action == "submit" && e.GetType() != typeof(Button)) ||
                (action == "focus" && !e.focusable) || (action == "set_value" && (e.GetType() == typeof(Button) || !Writable(e))))
                return Error(McpErrorCodes.InvalidParameter, "Action is unsupported for this exact control/type/state.");
            Action dispatch;
            if (action == "focus") dispatch = e.Focus;
            else if (action == "submit") dispatch = () => {
                // Without an assigned target, newer Unity dispatchers route
                // navigation to focus even when SendEvent is called on a button.
                using (var evt = NavigationSubmitEvent.GetPooled()) { evt.target = e; e.SendEvent(evt); }
            };
            else
            {
                dispatch = ValueAction(e, args["value"]);
                if (dispatch == null) return Error(McpErrorCodes.InvalidParameter, "Value type/range is invalid. Use boolean Toggle, integer fields/choice indices, or finite numbers within slider/scroller bounds; numeric magnitude <= 1000000.");
            }
            // Consume before calling user callbacks: a replay can never invoke this handle twice.
            Revoke(id); int errors = 0; string exceptionType = null;
            void Observe(string _, string __, LogType type) { if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) errors = Math.Min(10000, errors + 1); }
            Application.logMessageReceived += Observe;
            try { dispatch(); }
            catch (Exception ex) { errors++; exceptionType = ex.GetType().FullName; }
            finally { Application.logMessageReceived -= Observe; }
            JObject result = errors == 0 ? new JObject { ["status"] = "dispatched" } : Error(McpErrorCodes.InternalError, "UI dispatch reported an error; side effects may have occurred. Inspect state before further actions.");
            if (errors > 0) result["status"] = "failed";
            result["action"] = action; result["completion_scope"] = "synchronous_ui_dispatch";
            result["callback_side_effects_possible"] = true; result["automatic_retry_safe"] = false;
            result["handle_consumed"] = true; result["handler_error_count"] = errors;
            if (exceptionType != null) result["exception_type"] = Clip(exceptionType, 128);
            // Do not return log/exception messages: callbacks may print passwords or other secrets.
            return result;
        }
        private static Action ValueAction(VisualElement e, JToken v)
        {
            var t = e.GetType();
            if (t == typeof(Toggle)) return v.Type == JTokenType.Boolean ? (Action)(() => ((Toggle)e).value = (bool)v) : null;
            bool integer = v.Type == JTokenType.Integer;
            if (!integer && v.Type != JTokenType.Float) return null;
            if (!double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || double.IsNaN(n) || double.IsInfinity(n) || Math.Abs(n) > 1000000) return null;
            if (t == typeof(Slider)) { var s = (Slider)e; return n >= Math.Min(s.lowValue, s.highValue) && n <= Math.Max(s.lowValue, s.highValue) ? (Action)(() => s.value = (float)n) : null; }
            if (t == typeof(FloatField)) return () => ((FloatField)e).value = (float)n;
            if (t == typeof(Scroller)) { var s = (Scroller)e; return n >= Math.Min(s.lowValue, s.highValue) && n <= Math.Max(s.lowValue, s.highValue) ? (Action)(() => s.value = (float)n) : null; }
            if (!integer) return null;
            int i = (int)n;
            if (t == typeof(SliderInt)) { var s = (SliderInt)e; return i >= Math.Min(s.lowValue, s.highValue) && i <= Math.Max(s.lowValue, s.highValue) ? (Action)(() => s.value = i) : null; }
            if (t == typeof(IntegerField)) return () => ((IntegerField)e).value = i;
            if (t == typeof(DropdownField)) { var d = (DropdownField)e; return d.choices.Count <= 512 && i >= 0 && i < d.choices.Count ? (Action)(() => d.index = i) : null; }
            return null;
        }
    }
}
