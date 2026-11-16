using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace I1rr.UnityMcp.Runtime.Tests
{
    public class GameViewScreenshotTests
    {
        [UnityTest]
        public IEnumerator GameViewCapture_ResizePreservesFramebufferColors()
        {
            var owner = new GameObject("ScreenshotColorRegressionCamera");
            var camera = owner.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.2f, 0.4f, 0.6f);
            camera.cullingMask = 0;
            camera.depth = 1000;
            DeferredResult pending = null;
            string path = null;
            Texture2D screen = null;
            var decoded = new Texture2D(2, 2);
            try
            {
                yield return null;
                yield return new WaitForEndOfFrame();
                screen = ScreenCapture.CaptureScreenshotAsTexture();
                var expected = screen.GetPixel(screen.width / 2, screen.height / 2);
                pending = ScreenshotCapture.Capture(new JObject { ["width"] = 64, ["height"] = 64 }) as DeferredResult;
                Assert.That(pending, Is.Not.Null);
                float deadline = Time.realtimeSinceStartup + 12;
                while (!pending.IsComplete() && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.That(pending.IsComplete(), Is.True);
                var result = pending.GetResult();
                Assert.That(result.Value<bool>("success"), Is.True, result.ToString());
                path = result.Value<string>("path");
                Assert.That(decoded.LoadImage(File.ReadAllBytes(path)), Is.True);
                var actual = decoded.GetPixel(decoded.width / 2, decoded.height / 2);
                Assert.That(actual.r, Is.EqualTo(expected.r).Within(0.02f), "Red changed during resize.");
                Assert.That(actual.g, Is.EqualTo(expected.g).Within(0.02f), "Green changed during resize.");
                Assert.That(actual.b, Is.EqualTo(expected.b).Within(0.02f), "Blue changed during resize.");
            }
            finally
            {
                pending?.InvokeCleanup();
                if (path != null) File.Delete(path);
                if (screen != null) Object.Destroy(screen);
                Object.Destroy(decoded);
                Object.Destroy(owner);
            }
        }

        [UnityTest]
        public IEnumerator CancelledCapture_DoesNotLeaveALatePendingPng()
        {
            string directory = Path.Combine(Application.temporaryCachePath, "mcp_screenshots");
            var before = new HashSet<string>(Directory.Exists(directory)
                ? Directory.GetFiles(directory, "pending_*.png") : new string[0]);
            var cameraObject = new GameObject("CancelledScreenshotCamera");
            cameraObject.AddComponent<Camera>();
            DeferredResult pending = null;
            try
            {
                yield return null;
                pending = ScreenshotCapture.Capture(new JObject { ["width"] = 64, ["height"] = 64 }) as DeferredResult;
                Assert.That(pending, Is.Not.Null);
                pending.InvokeCleanup();
                // Unity can finish a queued native PNG after the requester has left.
                float deadline = Time.realtimeSinceStartup + 2;
                while (Time.realtimeSinceStartup < deadline) yield return null;
                var lateFiles = Directory.GetFiles(directory, "pending_*.png").Where(path => !before.Contains(path)).ToArray();
                Assert.That(lateFiles, Is.Empty, "Cancelled capture wrote an orphaned PNG after cleanup.");
            }
            finally
            {
                pending?.InvokeCleanup();
                Object.Destroy(cameraObject);
                if (Directory.Exists(directory)) foreach (var path in Directory.GetFiles(directory, "pending_*.png"))
                    if (!before.Contains(path)) File.Delete(path);
            }
        }

        [UnityTest]
        public IEnumerator GameViewCapture_IncludesUiToolkitOverlayThatCameraCaptureOmits()
        {
            var cameraObject = new GameObject("ScreenshotRegressionCamera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.cullingMask = 0;
            camera.depth = 1000;
            var panel = ScriptableObject.CreateInstance<PanelSettings>();
            // Every fixture style below is inline. A test-only Resources theme
            // would also be packed into ordinary games that install this package.
            var theme = ScriptableObject.CreateInstance<ThemeStyleSheet>();
            panel.themeStyleSheet = theme;
            panel.sortingOrder = 32760;
            var uiObject = new GameObject("ScreenshotRegressionOverlay");
            uiObject.SetActive(false);
            var document = uiObject.AddComponent<UIDocument>();
            document.panelSettings = panel;
            uiObject.SetActive(true);
            var root = document.rootVisualElement;
            root.style.position = Position.Absolute;
            root.style.left = root.style.right = root.style.top = root.style.bottom = 0;
            root.style.backgroundColor = Color.magenta;
            bool painted = false;
            root.generateVisualContent += _ => painted = true;
            DeferredResult pending = null;
            string gamePath = null;
            string cameraPath = null;
            var texture = new Texture2D(2, 2);
            try
            {
                float paintDeadline = Time.realtimeSinceStartup + 5;
                while ((!painted || root.worldBound.width < 1) && Time.realtimeSinceStartup < paintDeadline)
                    yield return null;
                Assert.That(painted && root.worldBound.width > 0, Is.True, "The UI fixture never painted.");
                yield return null;
                var cameraResult = (JObject)ScreenshotCapture.Capture(new JObject
                {
                    ["capture_source"] = "camera", ["camera_name"] = camera.name,
                    ["width"] = 64, ["height"] = 64
                });
                Assert.That(cameraResult.Value<bool>("success"), Is.True, cameraResult.ToString());
                cameraPath = cameraResult.Value<string>("path");
                Assert.That(texture.LoadImage(File.ReadAllBytes(cameraPath)), Is.True);
                var cameraPixel = texture.GetPixel(texture.width / 2, texture.height / 2);
                Assert.That(cameraPixel.r + cameraPixel.g + cameraPixel.b, Is.LessThan(0.15f));

                // Witness the actual composed frame. Paint callbacks can precede presentation.
                bool presented = false;
                var presentDeadline = Time.realtimeSinceStartup + 5;
                do
                {
                    yield return new WaitForEndOfFrame();
                    var screen = ScreenCapture.CaptureScreenshotAsTexture();
                    try
                    {
                        var center = screen.GetPixel(screen.width / 2, screen.height / 2);
                        presented = center.r > 0.8f && center.g < 0.2f && center.b > 0.8f;
                    }
                    finally { Object.Destroy(screen); }
                } while (!presented && Time.realtimeSinceStartup < presentDeadline);
                Assert.That(presented, Is.True, "The Game View itself did not present the test overlay.");
                pending = ScreenshotCapture.Capture(new JObject { ["width"] = 64, ["height"] = 64 }) as DeferredResult;
                Assert.That(pending, Is.Not.Null, "Game View capture must wait for the final rendered frame.");
                float deadline = Time.realtimeSinceStartup + 12;
                while (!pending.IsComplete() && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.That(pending.IsComplete(), Is.True, "Screenshot did not complete.");
                var result = pending.GetResult();
                Assert.That(result.Value<bool>("success"), Is.True, result.ToString());
                Assert.That(result.Value<bool>("includes_screen_overlays"), Is.True);
                gamePath = result.Value<string>("path");
                Assert.That(texture.LoadImage(File.ReadAllBytes(gamePath)), Is.True);
                var pixel = texture.GetPixel(texture.width / 2, texture.height / 2);
                Assert.That(pixel.r, Is.GreaterThan(0.8f), "UI Toolkit red channel missing.");
                Assert.That(pixel.g, Is.LessThan(0.2f));
                Assert.That(pixel.b, Is.GreaterThan(0.8f), "UI Toolkit blue channel missing.");
            }
            finally
            {
                pending?.InvokeCleanup();
                if (gamePath != null) File.Delete(gamePath);
                if (cameraPath != null) File.Delete(cameraPath);
                Object.Destroy(texture);
                Object.Destroy(uiObject);
                Object.Destroy(panel);
                Object.Destroy(theme);
                Object.Destroy(cameraObject);
            }
        }
    }
}
