using System;
using System.Collections;
using System.Collections.Generic;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace I1rr.UnityMcp.Editor.Tests
{
    public sealed class UiToolkitToolsTests
    {
        private GameObject owner;
        private UIDocument document;
        private PanelSettings settings;
        private ThemeStyleSheet theme;
        private RenderTexture texture;
        private VisualElement root;

        [UnitySetUp] public IEnumerator Before()
        {
            yield return new EnterPlayMode();
            UiToolkitTools.ClearHandles();
            settings = ScriptableObject.CreateInstance<PanelSettings>();
            theme = ScriptableObject.CreateInstance<ThemeStyleSheet>(); settings.themeStyleSheet = theme;
            texture = new RenderTexture(800, 600, 0); settings.targetTexture = texture;
            owner = new GameObject("UiToolkitToolFixture"); owner.SetActive(false);
            document = owner.AddComponent<UIDocument>(); document.panelSettings = settings; owner.SetActive(true);
            root = document.rootVisualElement;
            root.style.width = 800; root.style.height = 600;
            Add(new Button { name = "submit", text = "Go" });
            yield return Settle();
        }
        [UnityTearDown] public IEnumerator After()
        {
            UiToolkitTools.ClearHandles();
            if (owner != null) Object.DestroyImmediate(owner);
            if (settings != null) Object.DestroyImmediate(settings);
            if (theme != null) Object.DestroyImmediate(theme);
            if (texture != null) { texture.Release(); Object.DestroyImmediate(texture); }
            yield return new ExitPlayMode();
            Assert.That(UiToolkitTools.HandleCount, Is.Zero);
        }
        private void Add(VisualElement e) { e.style.width = 140; e.style.height = 28; root.Add(e); }
        private static IEnumerator Settle() { for (int i = 0; i < 5; i++) yield return null; }
        private JObject Node(string name)
        {
            var args = new JObject { ["document"] = document.GetMcpId().ToString(), ["element_name"] = name };
            var result = UiToolkitTools.Inspect(args); Success(result); return (JObject)result["nodes"][0];
        }
        private string Handle(string name) => (string)Node(name)["handle"];
        private static JObject Act(string handle, string action, JToken value = null)
        {
            var args = new JObject { ["handle"] = handle, ["action"] = action };
            if (value != null) args["value"] = value;
            return UiToolkitTools.Act(args);
        }
        private static void Success(JObject r) => Assert.That(r["error_code"], Is.Null, r.ToString());
        private static void Refused(JObject r, string code) => Assert.That((string)r["error_code"], Is.EqualTo(code), r.ToString());

        [UnityTest] public IEnumerator SubmitAndFocusUseActualEventsExactlyOnceAndConsumeHandles()
        {
            var button = root.Q<Button>("submit"); int clicked = 0, submitted = 0, focused = 0;
            button.clicked += () => clicked++;
            button.RegisterCallback<NavigationSubmitEvent>(_ => submitted++);
            button.RegisterCallback<FocusInEvent>(_ => focused++);
            string h = Handle("submit"); Success(Act(h, "focus"));
            Assert.That(root.panel.focusController.focusedElement, Is.SameAs(button)); Assert.That(focused, Is.EqualTo(1));
            Refused(Act(h, "focus"), McpErrorCodes.InvalidState);
            h = Handle("submit"); var result = Act(h, "submit"); Success(result);
            Assert.That(clicked, Is.EqualTo(1)); Assert.That(submitted, Is.EqualTo(1));
            Assert.That((string)result["completion_scope"], Is.EqualTo("synchronous_ui_dispatch"));
            Assert.That((bool)result["automatic_retry_safe"], Is.False);
            Refused(Act(h, "submit"), McpErrorCodes.InvalidState); Assert.That(clicked, Is.EqualTo(1));
            var other = new Button { name = "other" }; Add(other); yield return Settle();
            int otherCalls = 0; other.clicked += () => otherCalls++; other.Focus();
            Success(Act(Handle("submit"), "submit"));
            Assert.That(clicked, Is.EqualTo(2)); Assert.That(otherCalls, Is.Zero, "Submit must not route to a different focused control.");
            yield return null;
        }

        [UnityTest] public IEnumerator FreshStatelessRequestsCannotReplayAConsumedUiHandle()
        {
            var button = root.Q<Button>("submit");
            int calls = 0;
            button.clicked += () => calls++;
            string handle = Handle("submit");
            var previous = McpProtocol.OnToolsCall;
            try
            {
                McpProtocol.OnToolsCall = (request, _) => UiToolkitTools.Act((JObject)request["params"]["arguments"]);
                JObject Dispatch(int requestId)
                {
                    var request = McpModernProtocolTests.Request("tools/call");
                    request["id"] = requestId;
                    request["params"]["name"] = "act_on_ui_element";
                    request["params"]["arguments"] = new JObject { ["handle"] = handle, ["action"] = "submit" };
                    return McpProtocol.ProcessRequestWithContext(request, McpProtocol.CreateModernRequestContext(request));
                }
                var first = Dispatch(1);
                Assert.That(first["result"]["isError"], Is.Null);
                Assert.That((string)first["result"]["resultType"], Is.EqualTo("complete"));
                Assert.That(calls, Is.EqualTo(1));
                Assert.That((bool)Dispatch(2)["result"]["isError"], Is.True);
                Assert.That(calls, Is.EqualTo(1));
            }
            finally { McpProtocol.OnToolsCall = previous; }
            yield return null;
        }

        [UnityTest] public IEnumerator ValuesNotifyOnceAndSameValueDoesNotNotifyAgain()
        {
            var toggle = new Toggle { name = "toggle" }; var slider = new Slider(0, 10) { name = "slider" };
            var integer = new SliderInt(0, 10) { name = "integer" };
            var dropdown = new DropdownField(new List<string> { "one", "two" }, 0) { name = "dropdown" };
            var number = new IntegerField { name = "number" }; var floating = new FloatField { name = "floating" };
            Add(toggle); Add(slider); Add(integer); Add(dropdown); Add(number); Add(floating);
            int[] calls = new int[6];
            toggle.RegisterValueChangedCallback(_ => calls[0]++); slider.RegisterValueChangedCallback(_ => calls[1]++);
            integer.RegisterValueChangedCallback(_ => calls[2]++); dropdown.RegisterValueChangedCallback(_ => calls[3]++);
            number.RegisterValueChangedCallback(_ => calls[4]++); floating.RegisterValueChangedCallback(_ => calls[5]++);
            yield return Settle();
            string[] names = { "toggle", "slider", "integer", "dropdown", "number", "floating" };
            JToken[] values = { new JValue(true), new JValue(2.5), new JValue(3), new JValue(1), new JValue(4), new JValue(5.5) };
            for (int i = 0; i < names.Length; i++)
            {
                Success(Act(Handle(names[i]), "set_value", values[i]));
                Success(Act(Handle(names[i]), "set_value", values[i]));
                Assert.That(calls[i], Is.EqualTo(1), names[i]);
            }
            Assert.That(toggle.value, Is.True); Assert.That(slider.value, Is.EqualTo(2.5f)); Assert.That(integer.value, Is.EqualTo(3));
            Assert.That(dropdown.value, Is.EqualTo("two")); Assert.That(number.value, Is.EqualTo(4)); Assert.That(floating.value, Is.EqualTo(5.5f));
        }

        [UnityTest] public IEnumerator LiveVisibilityEnabledAndClippingAreRecheckedBeforeEveryMutation()
        {
            var b = root.Q<Button>("submit"); int calls = 0; b.clicked += () => calls++;
            var parent = new VisualElement(); root.Add(parent); parent.Add(b); yield return Settle();
            foreach (string state in new[] { "display", "visibility", "opacity", "disabled" })
            {
                string h = Handle("submit");
                if (state == "display") parent.style.display = DisplayStyle.None;
                if (state == "visibility") parent.style.visibility = Visibility.Hidden;
                if (state == "opacity") parent.style.opacity = 0;
                if (state == "disabled") parent.SetEnabled(false);
                yield return Settle(); Refused(Act(h, "submit"), McpErrorCodes.InvalidState);
                Assert.That((bool)Node("submit")["visible"], Is.EqualTo(state == "disabled"), "Disabled must remain distinct from visually hidden.");
                parent.style.display = DisplayStyle.Flex; parent.style.visibility = Visibility.Visible;
                parent.style.opacity = 1; parent.SetEnabled(true); yield return Settle();
            }
            parent.style.height = 20; parent.style.overflow = Overflow.Hidden; b.style.position = Position.Absolute; b.style.top = 50;
            yield return Settle(); Refused(Act(Handle("submit"), "submit"), McpErrorCodes.InvalidState);
            Assert.That(calls, Is.Zero);
        }

        [UnityTest] public IEnumerator DetachedReattachedRenamedReplacedAndExpiredTargetsAreStale()
        {
            var b = root.Q<Button>("submit"); string h = Handle("submit");
            b.RemoveFromHierarchy(); root.Add(b); yield return Settle(); Refused(Act(h, "submit"), McpErrorCodes.InvalidState);
            h = Handle("submit"); b.name = "renamed"; Refused(Act(h, "submit"), McpErrorCodes.InvalidState); b.name = "submit";
            h = Handle("submit"); b.RemoveFromHierarchy(); Add(new Button { name = "submit" }); yield return Settle();
            Refused(Act(h, "submit"), McpErrorCodes.InvalidState);
            h = Handle("submit"); UiToolkitTools.Prune(EditorApplication.timeSinceStartup + UiToolkitTools.HandleLifetime + 1);
            Refused(Act(h, "submit"), McpErrorCodes.InvalidState);
            h = Handle("submit"); owner.SetActive(false); yield return null; Refused(Act(h, "submit"), McpErrorCodes.InvalidState);
        }

        [UnityTest] public IEnumerator AmbiguousNamesNeverResolveToTheFirstMatch()
        {
            Add(new Button { name = "submit" }); yield return Settle();
            Refused(UiToolkitTools.Inspect(new JObject { ["document"] = owner.name, ["element_name"] = "submit" }), McpErrorCodes.Ambiguous);
            var other = new GameObject(owner.name); other.SetActive(false); var doc = other.AddComponent<UIDocument>(); doc.panelSettings = settings; other.SetActive(true);
            try { Refused(UiToolkitTools.Inspect(new JObject { ["document"] = owner.name }), McpErrorCodes.Ambiguous); Success(UiToolkitTools.Inspect(new JObject { ["document"] = document.GetMcpId().ToString() })); }
            finally { Object.DestroyImmediate(other); }
        }

        private sealed class CustomButton : Button { public string DangerousProperty => throw new Exception("must not read arbitrary getters"); }
        [UnityTest] public IEnumerator InspectionRedactsSecretsAndDoesNotInvokeCustomFormattingOrGetters()
        {
            Add(new TextField { name = "password", isPasswordField = true, value = "never-leak-this" });
            var secret = new VisualElement { name = "api-token" }; secret.Add(new Label("never-leak-that")); Add(secret);
            Add(new CustomButton { name = "custom", text = "custom-text-must-not-read" });
            var dropdown = new DropdownField(new List<string> { "one", "two" }, 0) { name = "choice" };
            Add(dropdown); yield return Settle();
            int formatted = 0; dropdown.formatSelectedValueCallback = s => { formatted++; return s; }; formatted = 0;
            var result = UiToolkitTools.Inspect(new JObject { ["document"] = owner.name, ["max_depth"] = 16, ["max_nodes"] = 256 }); Success(result);
            Assert.That(result.ToString(), Does.Not.Contain("never-leak").And.Not.Contain("custom-text-must-not-read"));
            Assert.That(formatted, Is.Zero); Assert.That(Node("custom")["handle"], Is.Null);
            Assert.That(Node("password")["handle"], Is.Null); Assert.That(Node("password")["value"], Is.Null);
        }

        [UnityTest] public IEnumerator BoundsMarkTruncationAndUniquenessFailsClosedWhenScanIsIncomplete()
        {
            var parent = new VisualElement { name = "deep" }; Add(parent);
            for (int i = 0; i < 70; i++) { var child = new VisualElement(); parent.Add(child); parent = child; }
            parent.Add(new Button { name = "deep-target" });
            for (int i = 0; i < 30; i++) Add(new Label(new string('x', 400)));
            yield return Settle();
            var args = new JObject { ["document"] = owner.name, ["max_nodes"] = 3, ["max_depth"] = 16 };
            var r = UiToolkitTools.Inspect(args); Success(r); Assert.That(((JArray)r["nodes"]).Count, Is.EqualTo(3)); Assert.That((bool)r["truncated"], Is.True);
            args["max_nodes"] = 256; args["max_depth"] = 0; r = UiToolkitTools.Inspect(args); Assert.That(((JArray)r["nodes"]).Count, Is.EqualTo(1)); Assert.That((bool)r["truncated"], Is.True);
            Refused(UiToolkitTools.Inspect(new JObject { ["document"] = owner.name, ["element_name"] = "submit" }), McpErrorCodes.MaxLimitReached);
            root.Q("deep").RemoveFromHierarchy(); args["max_depth"] = 16; r = UiToolkitTools.Inspect(args);
            int chars = 0; foreach (var n in (JArray)r["nodes"]) chars += ((string)n["text"] ?? "").Length;
            Assert.That(chars, Is.LessThanOrEqualTo(4096)); Assert.That((bool)r["truncated"], Is.True);
        }

        [UnityTest] public IEnumerator StrictInputAndTypeLimitsRefuseWithoutChangingValues()
        {
            Add(new SliderInt(0, 10) { name = "int" }); Add(new Toggle { name = "toggle" });
            Add(new Slider(0, 10) { name = "float" }); Add(new DropdownField(new List<string> { "one" }, 0) { name = "choice" });
            Add(new IntegerField { name = "readonly", isReadOnly = true });
            yield return Settle();
            foreach (var json in new[] { "{'max_nodes':0}", "{'max_nodes':257}", "{'max_depth':17}", "{'max_depth':'1'}", "{'max_nodes':999999999999999999999999999}", "{'element_name':'submit'}", "{'unknown':1}" })
                Refused(UiToolkitTools.Inspect(JObject.Parse(json)), McpErrorCodes.InvalidParameter);
            foreach (var value in new JToken[] { new JValue(2.5), new JValue("2"), new JValue(true), new JValue(11), new JValue(1000001), JValue.CreateNull() })
                Refused(Act(Handle("int"), "set_value", value), McpErrorCodes.InvalidParameter);
            Refused(Act(Handle("float"), "set_value", new JValue(double.NaN)), McpErrorCodes.InvalidParameter);
            Refused(Act(Handle("float"), "set_value", new JValue(double.PositiveInfinity)), McpErrorCodes.InvalidParameter);
            Refused(Act(Handle("toggle"), "set_value", new JValue(1)), McpErrorCodes.InvalidParameter);
            Refused(Act(Handle("choice"), "set_value", new JValue(1)), McpErrorCodes.InvalidParameter);
            Refused(Act(Handle("int"), "submit"), McpErrorCodes.InvalidParameter);
            Refused(Act(Handle("readonly"), "set_value", new JValue(3)), McpErrorCodes.InvalidParameter);
            Refused(Act(Handle("submit"), "invoke"), McpErrorCodes.InvalidParameter);
            Assert.That(root.Q<SliderInt>("int").value, Is.Zero); Assert.That(root.Q<Toggle>("toggle").value, Is.False);
        }

        [UnityTest] public IEnumerator CallbackErrorsHaveUnknownSideEffectsAndCannotReplayOrLeakMessages()
        {
            var b = root.Q<Button>("submit"); int calls = 0;
            Action callback = () => { calls++; Debug.LogError("secret-ui-callback-message"); };
            b.clicked += callback; string h = Handle("submit"); LogAssert.Expect(LogType.Error, "secret-ui-callback-message");
            var result = Act(h, "submit"); Refused(result, McpErrorCodes.InternalError);
            Assert.That(calls, Is.EqualTo(1)); Assert.That(result.ToString(), Does.Not.Contain("secret-ui-callback-message"));
            Refused(Act(h, "submit"), McpErrorCodes.InvalidState); Assert.That(calls, Is.EqualTo(1)); b.clicked -= callback;
            Action throwing = () => { calls++; throw new InvalidOperationException("throwing-ui-callback"); };
            b.clicked += throwing;
            result = Act(Handle("submit"), "submit"); Refused(result, McpErrorCodes.InternalError); Assert.That(calls, Is.EqualTo(2)); b.clicked -= throwing;
            Assert.That((string)result["exception_type"], Is.EqualTo(typeof(InvalidOperationException).FullName));
            Assert.That(result.ToString(), Does.Not.Contain("throwing-ui-callback"));
            Success(Act(Handle("submit"), "submit"));
            yield return null;
        }

        [UnityTest] public IEnumerator HandleStorageIsBoundedAndClearedAcrossPlayTransitions()
        {
            string first = Handle("submit");
            for (int i = 0; i < UiToolkitTools.HandleLimit + 5; i++) Handle("submit");
            Assert.That(UiToolkitTools.HandleCount, Is.EqualTo(UiToolkitTools.HandleLimit));
            Refused(Act(first, "submit"), McpErrorCodes.InvalidState);
            Object.DestroyImmediate(owner); owner = null;
            Assert.That(UiToolkitTools.HandleCount, Is.Zero, "Detach must unregister/revoke all handles.");
            yield return new ExitPlayMode(); Assert.That(UiToolkitTools.HandleCount, Is.Zero);
            Refused(UiToolkitTools.Inspect(new JObject()), McpErrorCodes.NotInPlayMode);
            Refused(UiToolkitTools.Act(new JObject { ["handle"] = first, ["action"] = "submit" }), McpErrorCodes.NotInPlayMode);
            yield return new EnterPlayMode();
        }

        [UnityTest] public IEnumerator SpecialistDiscoveryDoesNotEnlargeFocusedCatalogOrEnableCodeExecution()
        {
            bool focused = ToolCatalog.Focused, code = McpPreferencesProvider.EnableCodeExecution;
            try
            {
                ToolCatalog.Focused = true;
                var names = ToolCatalog.Discover(new JObject { ["query"] = "UI Toolkit" }).ToString();
                Assert.That(names, Does.Contain("inspect_ui_document").And.Contain("act_on_ui_element"));
                string ordinary = ToolCatalog.Advertise(new JObject()).ToString();
                Assert.That(ordinary, Does.Not.Contain("inspect_ui_document").And.Not.Contain("act_on_ui_element"));
                Assert.That(McpPreferencesProvider.EnableCodeExecution, Is.EqualTo(code));
            }
            finally { ToolCatalog.Focused = focused; }
            yield return null;
        }

        [UnityTest] public IEnumerator FractionalBoundsSurviveJsonRoundTripAndNotifyOnce()
        {
            const float near = .1234567f, far = 853.3334f;
            foreach (bool reversed in new[] { false, true })
            {
                float low = reversed ? far : near, high = reversed ? near : far;
                var slider = new Slider(low, high) { name = "fractional-slider-" + reversed };
                var scroller = new Scroller(low, high, null, SliderDirection.Horizontal) { name = "fractional-scroller-" + reversed };
                Add(slider); Add(scroller); slider.value = low; scroller.value = low;
                yield return Settle();
                int sliderCalls = 0, scrollerCalls = 0;
                slider.RegisterValueChangedCallback(_ => sliderCalls++);
                scroller.valueChanged += _ => scrollerCalls++;
                foreach (var control in new VisualElement[] { slider, scroller })
                {
                    foreach (string endpoint in new[] { "maximum", "minimum" })
                    {
                        var wire = JObject.Parse(Node(control.name).ToString(Newtonsoft.Json.Formatting.None));
                        float expected = endpoint == "maximum" ? high : low;
                        Assert.That((double)wire[endpoint], Is.EqualTo((double)expected), "Inspection must retain the real control bound on the wire.");
                        for (int repeat = 0; repeat < 2; repeat++)
                        {
                            var request = new JObject { ["handle"] = Handle(control.name), ["action"] = "set_value", ["value"] = wire[endpoint] };
                            Success(UiToolkitTools.Act(JObject.Parse(request.ToString(Newtonsoft.Json.Formatting.None))));
                        }
                        Assert.That(control == slider ? slider.value : scroller.value, Is.EqualTo(expected));
                    }
                    Refused(Act(Handle(control.name), "set_value", new JValue((double)far + .001)), McpErrorCodes.InvalidParameter);
                    Refused(Act(Handle(control.name), "set_value", new JValue((double)near - .001)), McpErrorCodes.InvalidParameter);
                }
                Assert.That(sliderCalls, Is.EqualTo(2)); Assert.That(scrollerCalls, Is.EqualTo(2));
            }
        }

        [UnityTest] public IEnumerator PublicScrollerRevealsClippedControlsAndNotifiesOnce()
        {
            var scroll = new ScrollView(ScrollViewMode.Vertical) { name = "scroll" };
            scroll.style.width = 300; scroll.style.height = 150; root.Add(scroll);
            // This fixture deliberately has no shipped theme; give the public
            // scrollbar geometry instead of depending on default USS assets.
            scroll.contentViewport.style.width = 280; scroll.contentViewport.style.height = 150;
            scroll.contentViewport.style.flexShrink = 0;
            scroll.contentViewport.style.overflow = Overflow.Hidden;
            scroll.verticalScroller.style.width = 20; scroll.verticalScroller.style.height = 150;
            scroll.verticalScroller.style.flexShrink = 0;
            scroll.verticalScroller.parent.style.width = 300; scroll.verticalScroller.parent.style.height = 150;
            scroll.verticalScroller.parent.style.flexDirection = FlexDirection.Row;
            scroll.horizontalScroller.style.display = DisplayStyle.None;
            var spacer = new VisualElement(); spacer.style.height = 500; spacer.style.flexShrink = 0; scroll.Add(spacer);
            var b = new Button { name = "below-fold", text = "Back" }; b.style.height = 28; b.style.flexShrink = 0; scroll.Add(b);
            scroll.verticalScroller.name = "vertical-scroller";
            yield return Settle();
            Assert.That((bool)Node("below-fold")["visible"], Is.False);
            Refused(Act(Handle("below-fold"), "submit"), McpErrorCodes.InvalidState);
            int calls = 0; scroll.verticalScroller.valueChanged += _ => calls++;
            float end = scroll.verticalScroller.highValue; Assert.That(end, Is.GreaterThan(0));
            Success(Act(Handle("vertical-scroller"), "set_value", new JValue(end)));
            Success(Act(Handle("vertical-scroller"), "set_value", new JValue(end)));
            Assert.That(calls, Is.EqualTo(1)); Assert.That(scroll.scrollOffset.y, Is.EqualTo(end).Within(0.01f));
            yield return Settle();
            string ancestry = "";
            for (var p = (VisualElement)b; p != null; p = p.parent) ancestry += p.GetType().Name + ":" + p.worldBound + ";";
            Assert.That((bool)Node("below-fold")["visible"], Is.True, ancestry);
            int clicks = 0; b.clicked += () => clicks++; Success(Act(Handle("below-fold"), "submit")); Assert.That(clicks, Is.EqualTo(1));
            Refused(Act(Handle("vertical-scroller"), "set_value", new JValue(end + 1)), McpErrorCodes.InvalidParameter);
        }
    }
}
