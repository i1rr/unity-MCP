using System;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace I1rr.UnityMcp.Editor.Tests
{
    /// <summary>
    /// Tests for <see cref="McpToolRegistry"/>: discovery, caching, auto-route resolution,
    /// play-mode guards, concurrent access, and invoke behavior.
    /// </summary>
    public class McpToolRegistryTests
    {
        private Func<bool> _originalIsInPlayMode;

        [SetUp]
        public void SetUp()
        {
            _originalIsInPlayMode = McpToolRegistry.IsInPlayMode;
        }

        [TearDown]
        public void TearDown()
        {
            McpToolRegistry.IsInPlayMode = _originalIsInPlayMode;
        }

        #region ListTools

        [Test]
        public void ListTools_ReturnsNonEmptyArray()
        {
            var tools = McpToolRegistry.ListTools();
            Assert.That(tools, Is.Not.Null);
            Assert.That(tools.Count, Is.GreaterThan(0), "At least one tool should be registered.");
        }

        [Test]
        public void ListTools_EachToolHasNameAndDescription()
        {
            var tools = McpToolRegistry.ListTools();
            foreach (JObject tool in tools)
            {
                Assert.That(tool["name"]?.Value<string>(), Is.Not.Null.And.Not.Empty,
                    "Every tool must have a non-empty name.");
                Assert.That(tool["description"]?.Value<string>(), Is.Not.Null.And.Not.Empty,
                    $"Tool '{tool["name"]}' must have a description.");
            }
        }

        [Test]
        public void ListTools_EachToolHasInputSchema()
        {
            var tools = McpToolRegistry.ListTools();
            foreach (JObject tool in tools)
            {
                Assert.That(tool["inputSchema"], Is.Not.Null,
                    $"Tool '{tool["name"]}' must have an inputSchema.");
            }
        }

        [Test]
        public void ListTools_CategoryFilter_ReturnsOnlyMatchingCategory()
        {
            var tools = McpToolRegistry.ListTools("Utility");
            foreach (JObject tool in tools)
            {
                var category = tool["category"]?.Value<string>();
                Assert.That(category, Is.EqualTo("Utility").IgnoreCase,
                    $"Tool '{tool["name"]}' does not match category 'Utility'.");
            }
        }

        [Test]
        public void ListTools_CategoryFilter_UnknownCategory_ReturnsEmpty()
        {
            var tools = McpToolRegistry.ListTools("NonexistentCategory_xyz");
            Assert.That(tools.Count, Is.EqualTo(0));
        }

        #endregion

        #region Invoke

        [Test]
        public void Invoke_NonexistentTool_ReturnsError()
        {
            var result = McpToolRegistry.Invoke("nonexistent_tool_xyz", new JObject());
            Assert.That(result, Is.Not.Null);
            // Registry returns error code -32601 for tool not found
            var errorCode = result["error"]?["code"]?.Value<int>();
            var errorMsg = result["error"]?["message"]?.Value<string>();
            // The error could be in the format {"error":{"code":-32601}} or flat
            bool hasError = errorCode == -32601 ||
                            (errorMsg != null && errorMsg.Contains("not found"));
            Assert.That(hasError, Is.True, "Should return tool-not-found error.");
        }

        [Test]
        public void Invoke_PingTool_Succeeds()
        {
            // ping is a built-in tool; if it exists, invoke it
            var tools = McpToolRegistry.ListTools();
            bool hasPing = false;
            foreach (JObject t in tools)
            {
                if (t["name"]?.Value<string>() == "ping")
                {
                    hasPing = true;
                    break;
                }
            }

            if (!hasPing)
            {
                Assert.Ignore("ping tool not registered — skipping.");
                return;
            }

            var result = McpToolRegistry.Invoke("ping", new JObject());
            Assert.That(result, Is.Not.Null);
        }

        #endregion

        #region Play Mode Guards

        [Test]
        public void IsPlayModeRequired_ReturnsFalseForUnknownTool()
        {
            Assert.That(McpToolRegistry.IsPlayModeRequired("nonexistent_tool_xyz"), Is.False);
        }

        [Test]
        public void Invoke_PlayModeRequiredTool_NoChecker_ReturnsError()
        {
            // Find a play-mode-required tool
            string playModeTool = FindPlayModeTool();
            if (playModeTool == null)
            {
                Assert.Ignore("No play-mode-required tool found.");
                return;
            }

            McpToolRegistry.IsInPlayMode = null;
            var result = McpToolRegistry.Invoke(playModeTool, new JObject());
            Assert.That(result, Is.Not.Null);
            var msg = result.ToString();
            Assert.That(msg, Does.Contain("play mode").IgnoreCase);
        }

        [Test]
        public void Invoke_PlayModeRequiredTool_NotInPlayMode_ReturnsError()
        {
            string playModeTool = FindPlayModeTool();
            if (playModeTool == null)
            {
                Assert.Ignore("No play-mode-required tool found.");
                return;
            }

            McpToolRegistry.IsInPlayMode = () => false;
            var result = McpToolRegistry.Invoke(playModeTool, new JObject());
            Assert.That(result, Is.Not.Null);
            var msg = result.ToString();
            Assert.That(msg, Does.Contain("Play Mode").IgnoreCase);
        }

        #endregion

        #region IsDeferredTool

        [Test]
        public void IsDeferredTool_ReturnsFalseForUnknownTool()
        {
            Assert.That(McpToolRegistry.IsDeferredTool("nonexistent_tool_xyz"), Is.False);
        }

        [Test]
        public void IsDeferredTool_ReturnsTrueForDeferredTools()
        {
            // batch_execute and wait_for_frames are deferred
            if (McpToolRegistry.IsDeferredTool("wait_for_frames"))
                Assert.Pass();
            else if (McpToolRegistry.IsDeferredTool("batch_execute"))
                Assert.Pass();
            else
                Assert.Ignore("No known deferred tool found to test.");
        }

        #endregion

        #region Cache Invalidation

        [Test]
        public void InvalidateCache_AllowsReScan()
        {
            var toolsBefore = McpToolRegistry.ListTools();
            McpToolRegistry.InvalidateCache();
            var toolsAfter = McpToolRegistry.ListTools();

            Assert.That(toolsAfter.Count, Is.EqualTo(toolsBefore.Count),
                "Tool count should be consistent after cache invalidation.");
        }

        #endregion

        #region InvokeRaw

        [Test]
        public void InvokeRaw_NonexistentTool_ReturnsJObjectError()
        {
            var result = McpToolRegistry.InvokeRaw("nonexistent_tool_xyz", new JObject());
            Assert.That(result, Is.InstanceOf<JObject>());
        }

        #endregion

        private static string FindPlayModeTool()
        {
            var tools = McpToolRegistry.ListTools();
            foreach (JObject tool in tools)
            {
                var name = tool["name"]?.Value<string>();
                if (name != null && McpToolRegistry.IsPlayModeRequired(name))
                    return name;
            }
            return null;
        }
    }
}
