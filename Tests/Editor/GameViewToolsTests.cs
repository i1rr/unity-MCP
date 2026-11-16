using System;
using System.Reflection;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace I1rr.UnityMcp.Editor.Tests
{
    public sealed class GameViewToolsTests
    {
        private object group;
        private int initialCount;
        private JObject original;
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        [SetUp] public void Before()
        {
            Assert.That((string)GameViewTools.Configure(new JObject { ["restore"] = true })["error_code"], Is.Null);
            var assembly = typeof(UnityEditor.Editor).Assembly;
            EditorWindow.GetWindow(assembly.GetType("UnityEditor.GameView"));
            var type = assembly.GetType("UnityEditor.GameViewSizes");
            var sizes = type.GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy).GetValue(null);
            group = type.GetMethod("GetGroup").Invoke(sizes, new[] { type.GetProperty("currentGroupType", Flags).GetValue(sizes) });
            initialCount = Count;
            original = (JObject)GameViewTools.GetState(new JObject())["selected_size"];
            Assert.That(original, Is.Not.Null);
        }
        private int Count => (int)group.GetType().GetMethod("GetTotalCount").Invoke(group, null);
        private static JObject Set(int width, int height) => GameViewTools.Configure(new JObject { ["width"] = width, ["height"] = height, ["focus"] = false });
        private static void Success(JObject result) => Assert.That((string)result["error_code"], Is.Null, result.ToString());

        [TearDown] public void After()
        {
            Success(GameViewTools.Configure(new JObject { ["restore"] = true }));
            Assert.That(Count, Is.EqualTo(initialCount), "Temporary presets leaked.");
            Assert.That(JToken.DeepEquals(original, GameViewTools.GetState(new JObject())["selected_size"]), Is.True, "Original selection changed.");
        }

        [Test] public void ResolutionChangesSelectionAndRepeatedCallsKeepOnlyOneOwnedPreset()
        {
            foreach (var dimensions in new[] { new Vector2Int(1280, 720), new Vector2Int(800, 900), new Vector2Int(1024, 768), new Vector2Int(1024, 768) })
            {
                var result = Set(dimensions.x, dimensions.y);
                Success(result);
                Assert.That((string)result["status"], Is.EqualTo("requested"));
                Assert.That((bool)result["override_active"], Is.True);
                Assert.That((string)result["selected_size"]["size_type"], Is.EqualTo("FixedResolution"));
                Assert.That((int)result["selected_size"]["width"], Is.EqualTo(dimensions.x));
                Assert.That((int)result["selected_size"]["height"], Is.EqualTo(dimensions.y));
                Assert.That(Count, Is.EqualTo(initialCount + 1));
            }
            var restored = GameViewTools.Configure(new JObject { ["restore"] = true });
            Success(restored);
            Assert.That((string)restored["status"], Is.EqualTo("restored"));
            Assert.That((bool)restored["override_active"], Is.False);
            Assert.That(Count, Is.EqualTo(initialCount));
            Assert.That((string)GameViewTools.Configure(new JObject { ["restore"] = true })["status"], Is.EqualTo("not_overridden"));
        }

        [TestCase("{'width':800}")]
        [TestCase("{'height':600}")]
        [TestCase("{'width':63,'height':600}")]
        [TestCase("{'width':800,'height':4097}")]
        [TestCase("{'width':2147483648,'height':600}")]
        [TestCase("{'width':800.5,'height':600}")]
        [TestCase("{'width':'800','height':600}")]
        [TestCase("{'width':800,'height':null}")]
        [TestCase("{'restore':true,'width':800,'height':600}")]
        [TestCase("{'restore':true,'focus':false}")]
        [TestCase("{'restore':'yes'}")]
        [TestCase("{'focus':0}")]
        public void InvalidRequestsDoNotChangeSelectionOrCreatePresets(string json)
        {
            var result = GameViewTools.Configure(JObject.Parse(json));
            Assert.That((string)result["error_code"], Is.EqualTo(McpErrorCodes.InvalidParameter));
            Assert.That((bool)GameViewTools.GetState(new JObject())["override_active"], Is.False);
            Assert.That(Count, Is.EqualTo(initialCount));
        }

        [Test] public void FocusOnlyDoesNotCreateAResolutionOverride()
        {
            Success(GameViewTools.Configure(new JObject()));
            var state = GameViewTools.GetState(new JObject());
            Assert.That((bool)state["focused"], Is.True);
            Assert.That((bool)state["override_active"], Is.False);
            Assert.That(Count, Is.EqualTo(initialCount));
        }

        [Test] public void ReadOnlyStateDoesNotChangeFocusOrSelection()
        {
            var focus = EditorWindow.focusedWindow;
            var state = GameViewTools.GetState(new JObject());
            Success(state);
            Assert.That(EditorWindow.focusedWindow, Is.SameAs(focus));
            Assert.That((int)state["preset_count"], Is.EqualTo(initialCount));
            Assert.That(JToken.DeepEquals(original, state["selected_size"]), Is.True);
        }

        [Test] public void QuitCleanupRestoresSelectionWithoutPreventingQuit()
        {
            Success(Set(1280, 720));
            Assert.That(GameViewTools.RestoreBeforeQuit(), Is.True);
            Assert.That(Count, Is.EqualTo(initialCount));
            Assert.That((bool)GameViewTools.GetState(new JObject())["override_active"], Is.False);
            Assert.That(JToken.DeepEquals(original, GameViewTools.GetState(new JObject())["selected_size"]), Is.True);
        }

        [Test] public void MultipleViewsAreRejectedBeforeChangingSharedPresets()
        {
            var extra = (EditorWindow)ScriptableObject.CreateInstance(typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.GameView"));
            try
            {
                Assert.That((string)Set(1280, 720)["error_code"], Is.EqualTo(McpErrorCodes.InvalidState));
                Assert.That(Count, Is.EqualTo(initialCount));
                Assert.That((string)GameViewTools.GetState(new JObject())["error_code"], Is.EqualTo(McpErrorCodes.InvalidState));
            }
            finally { UnityEngine.Object.DestroyImmediate(extra); }
        }

        [Test] public void RestorePreservesUnrelatedCustomPresets()
        {
            Success(Set(1280, 720));
            var assembly = typeof(UnityEditor.Editor).Assembly;
            var size = Activator.CreateInstance(assembly.GetType("UnityEditor.GameViewSize"), Flags, null,
                new object[] { Enum.Parse(assembly.GetType("UnityEditor.GameViewSizeType"), "FixedResolution"), 700, 500, "Unrelated test preset" }, null);
            group.GetType().GetMethod("AddCustomSize").Invoke(group, new[] { size });
            try
            {
                Success(GameViewTools.Configure(new JObject { ["restore"] = true }));
                Assert.That(Count, Is.EqualTo(initialCount + 1));
                Assert.That(group.GetType().GetMethod("GetGameViewSize").Invoke(group, new object[] { Count - 1 }), Is.SameAs(size));
            }
            finally { group.GetType().GetMethod("RemoveCustomSize").Invoke(group, new object[] { Count - 1 }); }
        }
    }
}
