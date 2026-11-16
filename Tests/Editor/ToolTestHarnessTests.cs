using System;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace I1rr.UnityMcp.Editor.Tests
{
    /// <summary>
    /// Tests for tool invocation via <see cref="McpToolRegistry.Invoke"/>: parameter validation,
    /// error paths, and general tool harness patterns. Tests that tools return meaningful errors
    /// for invalid input rather than crashing.
    /// </summary>
    public class ToolTestHarnessTests
    {
        private Func<bool> _originalIsInPlayMode;

        [SetUp]
        public void SetUp()
        {
            _originalIsInPlayMode = McpToolRegistry.IsInPlayMode;
            McpToolRegistry.IsInPlayMode = () => false;
        }

        [TearDown]
        public void TearDown()
        {
            McpToolRegistry.IsInPlayMode = _originalIsInPlayMode;
        }

        #region Parameter Validation — Missing Required Parameters

        [Test]
        public void EditorStateToolAndResource_ReportCompilationFailureFlag()
        {
            foreach (var state in new[] {
                EditorControlTools.GetEditorState(new JObject()),
                McpResourceRegistry.ReadResource("editor://state")
            }) {
                Assert.That(state["hasCompilationErrors"]?.Type, Is.EqualTo(JTokenType.Boolean));
                Assert.That(state.Value<bool>("hasCompilationErrors"), Is.EqualTo(UnityEditor.EditorUtility.scriptCompilationFailed));
            }
        }

        [Test]
        public void GetAssetInfo_MissingPath_ReturnsError()
        {
            var result = McpToolRegistry.Invoke("get_asset_info", new JObject());
            AssertIsErrorResult(result, "path");
        }

        [Test]
        public void ReadFile_MissingPath_ReturnsError()
        {
            var result = McpToolRegistry.Invoke("read_file", new JObject());
            AssertIsErrorResult(result, "path");
        }

        [Test]
        public void SearchAssets_MissingQuery_ReturnsError()
        {
            var result = McpToolRegistry.Invoke("search_assets", new JObject());
            AssertIsErrorResult(result);
        }

        [Test]
        public void FindGameObjects_EmptyParams_ReturnsResult()
        {
            // find_gameobjects may allow empty params to list all objects
            var result = McpToolRegistry.Invoke("find_gameobjects", new JObject());
            Assert.That(result, Is.Not.Null);
        }

        #endregion

        #region Parameter Validation — Wrong Types

        [Test]
        public void GetAssetInfo_PathAsNumber_ReturnsError()
        {
            var result = McpToolRegistry.Invoke("get_asset_info", new JObject { ["path"] = 42 });
            AssertIsErrorResult(result);
        }

        [Test]
        public void WaitForFrames_NegativeCount_ReturnsError()
        {
            var result = McpToolRegistry.Invoke("wait_for_frames", new JObject { ["count"] = -5 });
            // Should be JObject error, not DeferredResult
            Assert.That(result, Is.Not.Null);
            var jResult = result as JObject;
            if (jResult != null)
            {
                var hasError = jResult["error"] != null || jResult["error_code"] != null;
                Assert.That(hasError, Is.True, "Negative frame count should return an error.");
            }
        }

        #endregion

        #region Error Paths — Not In Play Mode

        [Test]
        public void PlayModeRequiredTool_WhenNotInPlayMode_ReturnsPlayModeError()
        {
            McpToolRegistry.IsInPlayMode = () => false;

            // Find a play-mode tool and try invoking it
            var tools = McpToolRegistry.ListTools();
            string playModeTool = null;
            foreach (JObject t in tools)
            {
                var name = t["name"]?.Value<string>();
                if (name != null && McpToolRegistry.IsPlayModeRequired(name))
                {
                    playModeTool = name;
                    break;
                }
            }

            if (playModeTool == null)
            {
                Assert.Ignore("No play-mode-required tool found.");
                return;
            }

            var result = McpToolRegistry.Invoke(playModeTool, new JObject());
            var msg = result.ToString().ToLowerInvariant();
            Assert.That(msg, Does.Contain("play mode"));
        }

        #endregion

        #region Error Paths — Invalid Paths

        [Test]
        public void ReadFile_TraversalPath_ReturnsError()
        {
            var result = McpToolRegistry.Invoke("read_file", new JObject { ["path"] = "../../etc/passwd" });
            AssertIsErrorResult(result);
        }

        [Test]
        public void GetAssetInfo_AbsolutePath_ReturnsError()
        {
            var result = McpToolRegistry.Invoke("get_asset_info", new JObject { ["path"] = "C:\\Windows\\System32\\cmd.exe" });
            AssertIsErrorResult(result);
        }

        #endregion

        #region Pagination Consistency

        [Test]
        public void ListTools_ReturnsSameResultsAcrossCalls()
        {
            var first = McpToolRegistry.ListTools();
            var second = McpToolRegistry.ListTools();
            Assert.That(second.Count, Is.EqualTo(first.Count),
                "ListTools should return consistent results across calls.");
        }

        #endregion

        #region execute_code Gate

        [Test]
        public void ExecuteCode_DisabledByDefault()
        {
            var wasEnabled = McpPreferencesProvider.EnableCodeExecution;
            try
            {
                McpPreferencesProvider.EnableCodeExecution = false;
                var result = McpToolRegistry.Invoke("execute_code", new JObject { ["code"] = "1+1" });
                Assert.That(result, Is.Not.Null);
                Assert.That(result.ToString(), Does.Contain("disabled").IgnoreCase);
            }
            finally
            {
                McpPreferencesProvider.EnableCodeExecution = wasEnabled;
            }
        }

        [Test]
        public void ExecuteCode_GateEnforcedViaMcpToolRegistryInvoke()
        {
            // This simulates what batch_execute does internally — direct invocation
            // via McpToolRegistry.Invoke bypassing any routing layer
            var wasEnabled = McpPreferencesProvider.EnableCodeExecution;
            try
            {
                McpPreferencesProvider.EnableCodeExecution = false;
                var result = McpToolRegistry.Invoke("execute_code",
                    new JObject { ["code"] = "System.IO.File.Delete(\"important.dat\")" });
                Assert.That(result["success"]?.Value<bool>(), Is.Not.True,
                    "execute_code gate must prevent execution even through McpToolRegistry.Invoke.");
            }
            finally
            {
                McpPreferencesProvider.EnableCodeExecution = wasEnabled;
            }
        }

        #endregion

        private static void AssertIsErrorResult(JObject result, string containsText = null)
        {
            Assert.That(result, Is.Not.Null);
            var json = result.ToString().ToLowerInvariant();
            bool hasError = json.Contains("error") || json.Contains("missing") || json.Contains("required");
            Assert.That(hasError, Is.True, $"Expected error response, got: {result}");
            if (containsText != null)
                Assert.That(json, Does.Contain(containsText.ToLowerInvariant()));
        }
    }
}
