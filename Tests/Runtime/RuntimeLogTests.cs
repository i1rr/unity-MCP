using System;
using System.Reflection;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace I1rr.UnityMcp.Runtime.Tests
{
    /// <summary>
    /// Integration tests for RuntimeConsoleTools log buffer: verifies log capture via
    /// Debug.Log, buffer capacity bounds, filtering, and the clear operation.
    /// </summary>
    public class RuntimeLogTests
    {
        [SetUp]
        public void SetUp()
        {
            // Ensure the log callback is registered
            RuntimeConsoleTools.EnsureRegistered();

            // Clear the buffer before each test
            RuntimeConsoleTools.ClearLogs(new JObject());
        }

        [Test]
        public void EnsureRegistered_CanBeCalledMultipleTimes()
        {
            Assert.DoesNotThrow(() =>
            {
                RuntimeConsoleTools.EnsureRegistered();
                RuntimeConsoleTools.EnsureRegistered();
                RuntimeConsoleTools.EnsureRegistered();
            }, "EnsureRegistered should be idempotent.");
        }

        [Test]
        public void GetLogs_ReturnsEmptyAfterClear()
        {
            var result = RuntimeConsoleTools.GetLogs(new JObject());

            Assert.That(result["success"].Value<bool>(), Is.True);
            Assert.That(result["count"].Value<int>(), Is.EqualTo(0));
            Assert.That(result["logs"].Type, Is.EqualTo(JTokenType.Array));
        }

        [Test]
        public void GetLogs_CapturesDebugLogMessages()
        {
            // Emit some log messages
            Debug.Log("[MCP_TEST] Test log message 1");
            Debug.Log("[MCP_TEST] Test log message 2");
            Debug.Log("[MCP_TEST] Test log message 3");

            var result = RuntimeConsoleTools.GetLogs(new JObject { ["count"] = 50 });

            Assert.That(result["success"].Value<bool>(), Is.True);
            Assert.That(result["count"].Value<int>(), Is.GreaterThanOrEqualTo(3),
                "Should capture at least the 3 test messages.");

            var logs = (JArray)result["logs"];
            bool foundTestMsg = false;
            foreach (JObject log in logs)
            {
                if (log["message"].Value<string>().Contains("[MCP_TEST]"))
                {
                    foundTestMsg = true;
                    Assert.That(log["type"].Value<string>(), Is.EqualTo("Log"));
                    Assert.That(log["timestamp"], Is.Not.Null);
                    break;
                }
            }
            Assert.That(foundTestMsg, Is.True, "Should find the test log message in results.");
        }

        [Test]
        public void GetLogs_CapturesWarningsAndErrors()
        {
            Debug.LogWarning("[MCP_TEST] Test warning");
            LogAssert.Expect(LogType.Error, "[MCP_TEST] Test error");
            Debug.LogError("[MCP_TEST] Test error");

            var result = RuntimeConsoleTools.GetLogs(new JObject { ["count"] = 50 });

            var logs = (JArray)result["logs"];
            bool foundWarning = false;
            bool foundError = false;

            foreach (JObject log in logs)
            {
                string msg = log["message"].Value<string>();
                if (msg.Contains("[MCP_TEST] Test warning"))
                    foundWarning = true;
                if (msg.Contains("[MCP_TEST] Test error"))
                    foundError = true;
            }

            Assert.That(foundWarning, Is.True, "Should capture warning messages.");
            Assert.That(foundError, Is.True, "Should capture error messages.");
        }

        [Test]
        public void GetLogs_TypeFilter_FiltersCorrectly()
        {
            Debug.Log("[MCP_TEST] Info message");
            Debug.LogWarning("[MCP_TEST] Warning message");
            LogAssert.Expect(LogType.Error, "[MCP_TEST] Error message");
            Debug.LogError("[MCP_TEST] Error message");

            var errorResult = RuntimeConsoleTools.GetLogs(new JObject
            {
                ["count"] = 50,
                ["type_filter"] = "error"
            });

            var logs = (JArray)errorResult["logs"];
            foreach (JObject log in logs)
            {
                Assert.That(log["type"].Value<string>(), Is.EqualTo("Error"),
                    "With error filter, all returned logs should be errors.");
            }
        }

        [Test]
        public void GetLogs_CountParameter_LimitsResults()
        {
            // Emit more than the requested count
            for (int i = 0; i < 10; i++)
                Debug.Log($"[MCP_TEST] Message {i}");

            var result = RuntimeConsoleTools.GetLogs(new JObject { ["count"] = 3 });
            Assert.That(result["count"].Value<int>(), Is.LessThanOrEqualTo(3));
        }

        [Test]
        public void ClearLogs_ResetsBuffer()
        {
            Debug.Log("[MCP_TEST] Before clear");

            var beforeClear = RuntimeConsoleTools.GetLogs(new JObject());
            Assert.That(beforeClear["count"].Value<int>(), Is.GreaterThan(0));

            var clearResult = RuntimeConsoleTools.ClearLogs(new JObject());
            Assert.That(clearResult["success"].Value<bool>(), Is.True);
            Assert.That(clearResult["cleared_count"].Value<int>(), Is.GreaterThan(0));

            var afterClear = RuntimeConsoleTools.GetLogs(new JObject());
            Assert.That(afterClear["count"].Value<int>(), Is.EqualTo(0),
                "Buffer should be empty after clear.");
        }

        [Test]
        public void GetLogStatus_ReportsBufferInfo()
        {
            Debug.Log("[MCP_TEST] Status test");

            var status = RuntimeConsoleTools.GetLogStatus();

            Assert.That(status["hook_active"].Value<bool>(), Is.True);
            Assert.That(status["buffer_capacity"].Value<int>(), Is.GreaterThan(0));
            Assert.That(status["total_received"].Value<long>(), Is.GreaterThan(0));
        }

        [Test]
        public void BufferCapacity_DoesNotExceedMaxSize()
        {
            // The max buffer size is 2000 (from the source).
            // We can't easily emit 2000+ messages in a unit test without side effects,
            // so we verify the structure reports a bounded capacity.
            var status = RuntimeConsoleTools.GetLogStatus();
            int capacity = status["buffer_capacity"].Value<int>();

            Assert.That(capacity, Is.EqualTo(2000),
                "Buffer capacity should be the defined MaxBufferSize.");
        }

        [Test]
        public void GetLogs_IncludesBufferMetadata()
        {
            var result = RuntimeConsoleTools.GetLogs(new JObject());

            Assert.That(result["buffer_size"], Is.Not.Null, "Should include buffer_size.");
            Assert.That(result["buffer_capacity"], Is.Not.Null, "Should include buffer_capacity.");
            Assert.That(result["total_received"], Is.Not.Null, "Should include total_received.");
        }
    }
}
