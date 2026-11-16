using System;
using System.IO;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace I1rr.UnityMcp.Editor.Tests
{
    public sealed class GameViewSequenceTests
    {
        [TestCase(1280, 720, 60, "high", 60825600u)]
        [TestCase(1280, 720, 60, "standard", 19353600u)]
        [TestCase(64, 64, 1, "high", 8000000u)]
        [TestCase(3840, 2160, 60, "high", 160000000u)]
        public void NativeEncodingHasAnExplicitBoundedTarget(int width, int height, int fps, string quality, uint target)
        {
            var attributes = GameViewSequence.VideoAttributes(width, height, fps, quality);
            Assert.That(attributes.targetBitRate, Is.EqualTo(target));
            Assert.That(attributes.width, Is.EqualTo(width)); Assert.That(attributes.height, Is.EqualTo(height));
            Assert.That(attributes.frameRate.numerator, Is.EqualTo(fps));
            Assert.That(attributes.frameRate.denominator, Is.EqualTo(1));
            Assert.That(attributes.bitRateMode, Is.EqualTo(VideoBitrateMode.High));
            Assert.That(attributes.includeAlpha, Is.False);
        }
        [TestCase("'low'")]
        [TestCase("'High'")]
        [TestCase("''")]
        [TestCase("4")]
        [TestCase("true")]
        [TestCase("null")]
        [TestCase("{}")]
        [TestCase("[]")]
        public void InvalidQualityIsRejectedBeforeStateChecks(string value)
        {
            var args = JObject.Parse("{ 'quality':" + value + " }");
            Assert.That((string)GameViewSequence.Capture(args)["error_code"], Is.EqualTo(McpErrorCodes.InvalidParameter));
        }

        [TestCase("frames", "26")]
        [TestCase("fps", "26")]
        [TestCase("pre_frames", "1")]
        [TestCase("clock", "'unscaled'")]
        [TestCase("marker", "'changed'")]
        public void ReplayStillRejectsOtherRequestChanges(string field, string value)
        {
            string id = Guid.NewGuid().ToString("N");
            string directory = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "i1rr-unity-mcp-sequences", id);
            Directory.CreateDirectory(directory);
            var saved = new JObject { ["capture_id"] = id, ["status"] = "completed", ["frames"] = 25, ["fps"] = 25,
                ["pre_frames"] = 0, ["clock"] = "game", ["marker"] = "original", ["quality"] = "high" };
            try
            {
                File.WriteAllText(Path.Combine(directory, "status.json"), saved.ToString());
                var args = new JObject { ["capture_id"] = id, ["marker"] = "original" };
                args[field] = JToken.Parse(value.Replace('\'', '"'));
                Assert.That((string)GameViewSequence.Capture(args)["error_code"], Is.EqualTo(McpErrorCodes.InvalidParameter));
            }
            finally { Directory.Delete(directory, true); }
        }

        [Test] public void ReplayCannotChangeQualityOrRelabelLegacyEncoding()
        {
            string id = Guid.NewGuid().ToString("N");
            string directory = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "i1rr-unity-mcp-sequences", id);
            Directory.CreateDirectory(directory);
            var saved = new JObject { ["capture_id"] = id, ["status"] = "completed", ["frames"] = 25, ["fps"] = 25,
                ["pre_frames"] = 0, ["clock"] = "game", ["marker"] = "", ["quality"] = "high", ["clip_bytes"] = 1234 };
            var args = new JObject { ["capture_id"] = id };
            try
            {
                File.WriteAllText(Path.Combine(directory, "status.json"), saved.ToString());
                Assert.That(JToken.DeepEquals(saved, GameViewSequence.Capture(args)), Is.True);
                args["quality"] = "standard";
                Assert.That((string)GameViewSequence.Capture(args)["error_code"], Is.EqualTo(McpErrorCodes.InvalidParameter));
                args["quality"] = "high";
                Assert.That(JToken.DeepEquals(saved, GameViewSequence.Capture(args)), Is.True);
                saved.Remove("quality");
                File.WriteAllText(Path.Combine(directory, "status.json"), saved.ToString());
                Assert.That((string)GameViewSequence.Capture(args)["error_code"], Is.EqualTo(McpErrorCodes.InvalidParameter));
                Assert.That(GameViewSequence.Status(new JObject { ["capture_id"] = id })["quality"], Is.Null,
                    "Legacy results must remain readable without pretending they used the new quality.");
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
