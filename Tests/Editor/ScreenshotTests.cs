using System;
using System.Collections.Generic;
using System.IO;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace I1rr.UnityMcp.Editor.Tests
{
    public class ScreenshotTests
    {
        private GameObject _cameraObject;
        private Camera _camera;
        private readonly List<string> _files = new List<string>();

        [SetUp]
        public void SetUp()
        {
            _cameraObject = new GameObject("ScreenshotTest_" + Guid.NewGuid().ToString("N"));
            _camera = _cameraObject.AddComponent<Camera>();
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = Color.black;
            _camera.cullingMask = 0;
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_cameraObject);
            foreach (var path in _files) if (File.Exists(path)) File.Delete(path);
            _files.Clear();
        }

        private JObject Parameters() => new JObject
        {
            ["capture_source"] = "camera", ["camera_name"] = _camera.name,
            ["width"] = 64, ["height"] = 32
        };

        private JObject Capture(JObject parameters)
        {
            var result = (JObject)ScreenshotTools.TakeScreenshot(parameters);
            var metadata = result["content"] is JArray content
                ? JObject.Parse(content[0].Value<string>("text")) : result;
            if (metadata.Value<string>("path") is string path) _files.Add(path);
            return result;
        }

        [Test]
        public void CameraCapture_ProducesDecodableFileAndHonestMetadata()
        {
            var result = Capture(Parameters());
            Assert.That(result.Value<bool>("success"), Is.True, result.ToString());
            Assert.That(result.Value<string>("capture_source"), Is.EqualTo("camera"));
            Assert.That(result.Value<bool>("includes_screen_overlays"), Is.False);
            var texture = new Texture2D(2, 2);
            try
            {
                Assert.That(texture.LoadImage(File.ReadAllBytes(result.Value<string>("path"))), Is.True);
                Assert.That(texture.width, Is.EqualTo(64));
                Assert.That(texture.height, Is.EqualTo(32));
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }

        [Test]
        public void DefaultCapture_RequiresPlayModeInsteadOfReturningCameraWithoutUi()
        {
            var result = (JObject)ScreenshotTools.TakeScreenshot(new JObject());
            Assert.That(result.Value<bool>("success"), Is.False);
            Assert.That(result.Value<string>("error_code"), Is.EqualTo(McpErrorCodes.InvalidState));
        }

        [Test]
        public void LegacyNamedCamera_RemainsSupported()
        {
            var parameters = Parameters();
            parameters.Remove("capture_source");
            Assert.That(Capture(parameters).Value<bool>("success"), Is.True);
        }

        [TestCase("invalid", null)]
        [TestCase("game_view", "Camera")]
        public void InvalidCaptureSource_IsRejected(string source, string camera)
        {
            var result = Capture(new JObject { ["capture_source"] = source, ["camera_name"] = camera });
            Assert.That(result.Value<string>("error_code"), Is.EqualTo(McpErrorCodes.InvalidParameter));
        }

        [Test]
        public void MissingNamedCamera_ReturnsActionableError()
        {
            var parameters = Parameters();
            parameters["camera_name"] = "Missing_" + Guid.NewGuid();
            var result = Capture(parameters);
            Assert.That(result.Value<bool>("success"), Is.False);
            Assert.That(result.Value<string>("error"), Does.Contain("camera"));
        }

        [Test]
        public void Supersize_MultipliesDimensions()
        {
            var parameters = Parameters();
            parameters["supersize"] = 2;
            var result = Capture(parameters);
            Assert.That(result.Value<int>("width"), Is.EqualTo(128));
            Assert.That(result.Value<int>("height"), Is.EqualTo(64));
        }

        [Test]
        public void ExcessivePixelAllocation_IsRejectedBeforeRendering()
        {
            var parameters = Parameters();
            parameters["width"] = 8192;
            parameters["height"] = 8192;
            Assert.That(Capture(parameters).Value<string>("error_code"), Is.EqualTo(McpErrorCodes.InvalidParameter));
        }

        [Test]
        public void CameraCapture_RestoresExistingRenderTargets()
        {
            var cameraTarget = RenderTexture.GetTemporary(16, 16, 24);
            var activeTarget = RenderTexture.GetTemporary(16, 16, 0);
            var originalActive = RenderTexture.active;
            try
            {
                _camera.targetTexture = cameraTarget;
                RenderTexture.active = activeTarget;
                Capture(Parameters());
                Assert.That(_camera.targetTexture, Is.SameAs(cameraTarget));
                Assert.That(RenderTexture.active, Is.SameAs(activeTarget));
            }
            finally
            {
                _camera.targetTexture = null;
                RenderTexture.active = originalActive;
                RenderTexture.ReleaseTemporary(cameraTarget);
                RenderTexture.ReleaseTemporary(activeTarget);
            }
        }

        [Test]
        public void RapidCaptures_DoNotOverwriteEachOther()
        {
            var first = Capture(Parameters()).Value<string>("path");
            var second = Capture(Parameters()).Value<string>("path");
            Assert.That(first, Is.Not.EqualTo(second));
            Assert.That(File.Exists(first) && File.Exists(second), Is.True);
        }

        [Test]
        public void InlinePreview_IsNativeMcpImageAndFitsResponseLimit()
        {
            var parameters = Parameters();
            parameters["include_image"] = true;
            parameters["max_dimension"] = 32;
            var result = Capture(parameters);
            var content = (JArray)result["content"];
            Assert.That(content, Is.Not.Null, result.ToString());
            Assert.That(content[1].Value<string>("type"), Is.EqualTo("image"));
            Assert.That(content[1].Value<string>("mimeType"), Is.EqualTo("image/png"));
            var metadata = JObject.Parse(content[0].Value<string>("text"));
            Assert.That(metadata.Value<int>("preview_width"), Is.EqualTo(32));
            Assert.That(metadata.Value<int>("preview_height"), Is.EqualTo(16));
            Assert.That(result.ToString(Formatting.None).Length, Is.LessThanOrEqualTo(ResponseSizeLimiter.MaxResponseSizeBytes));
        }

        [Test]
        public void PreviewReductionRetainsThinLines()
        {
            var input = new Texture2D(128, 32, TextureFormat.RGB24, false);
            Texture2D preview = null;
            try
            {
                var colors = new Color32[128 * 32];
                for (int i = 0; i < colors.Length; i++)
                    colors[i] = (i % 8 == 0) ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 255);
                input.SetPixels32(colors);
                input.Apply();
                preview = ScreenshotCapture.Resize(input, 16, 4);
                Assert.That(preview.GetPixel(8, 2).r, Is.InRange(0.06f, 0.5f), "Thin light strokes disappeared in the inline preview.");
            }
            finally
            {
                if (preview != null) UnityEngine.Object.DestroyImmediate(preview);
                UnityEngine.Object.DestroyImmediate(input);
            }
        }

        [Test]
        public void ExplicitZeroWidthIsInvalid()
        {
            var parameters = Parameters();
            parameters["width"] = 0;
            Assert.That((string)Capture(parameters)["error_code"], Is.EqualTo(McpErrorCodes.InvalidParameter));
        }

        [Test]
        public void LegacyBase64_StillReturnsDecodableBoundedPng()
        {
            var parameters = Parameters();
            parameters["max_dimension"] = 16;
            var result = (JObject)ScreenshotTools.TakeScreenshotBase64(parameters);
            var texture = new Texture2D(2, 2);
            try
            {
                Assert.That(texture.LoadImage(Convert.FromBase64String(result.Value<string>("base64"))), Is.True);
                Assert.That(texture.width, Is.EqualTo(16));
                Assert.That(texture.height, Is.EqualTo(8));
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }
    }
}
