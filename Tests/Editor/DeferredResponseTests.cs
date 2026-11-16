using System;
using System.Collections;
using System.Reflection;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace I1rr.UnityMcp.Editor.Tests
{
    /// <summary>
    /// Tests for the DeferredResult pattern used by wait_for_frames and similar deferred tools.
    /// Verifies completion detection, timeout behavior, and cleanup invocation.
    /// </summary>
    public class DeferredResponseTests
    {
        [Test]
        public void DeferredResult_DefaultTimeout_Is30Seconds()
        {
            var deferred = new DeferredResult();
            Assert.That(deferred.TimeoutSeconds, Is.EqualTo(30f));
        }

        [Test]
        public void DeferredResult_ClampedTimeout_EnforcesRange()
        {
            var prop = typeof(DeferredResult).GetProperty("ClampedTimeout",
                BindingFlags.Public | BindingFlags.Instance);
            Assert.That(prop, Is.Not.Null, "ClampedTimeout property should exist.");

            var deferred = new DeferredResult { TimeoutSeconds = 0f };
            Assert.That((float)prop.GetValue(deferred), Is.EqualTo(1f), "Should clamp minimum to 1.");

            deferred.TimeoutSeconds = 120f;
            Assert.That((float)prop.GetValue(deferred), Is.EqualTo(60f), "Should clamp maximum to 60.");

            deferred.TimeoutSeconds = 15f;
            Assert.That((float)prop.GetValue(deferred), Is.EqualTo(15f), "Should pass through valid values.");
        }

        [Test]
        public void DeferredResult_IsComplete_CalledAndReturnsTrue()
        {
            int callCount = 0;
            var deferred = new DeferredResult
            {
                IsComplete = () =>
                {
                    callCount++;
                    return callCount >= 3;
                },
                GetResult = () => new JObject { ["done"] = true }
            };

            Assert.That(deferred.IsComplete(), Is.False);
            Assert.That(deferred.IsComplete(), Is.False);
            Assert.That(deferred.IsComplete(), Is.True);
            Assert.That(callCount, Is.EqualTo(3));
        }

        [Test]
        public void DeferredResult_GetResult_ReturnsExpectedPayload()
        {
            var deferred = new DeferredResult
            {
                IsComplete = () => true,
                GetResult = () => new JObject
                {
                    ["frames_elapsed"] = 10,
                    ["time_elapsed_seconds"] = 0.167
                }
            };

            var result = deferred.GetResult();
            Assert.That(result["frames_elapsed"].Value<int>(), Is.EqualTo(10));
            Assert.That(result["time_elapsed_seconds"].Value<double>(), Is.EqualTo(0.167).Within(0.001));
        }

        static void InvokeCleanupViaReflection(DeferredResult deferred)
        {
            var method = typeof(DeferredResult).GetMethod("InvokeCleanup",
                BindingFlags.Public | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "InvokeCleanup method should exist.");
            method.Invoke(deferred, null);
        }

        [Test]
        public void DeferredResult_Cleanup_IsInvoked()
        {
            bool cleanupCalled = false;
            var deferred = new DeferredResult
            {
                Cleanup = () => cleanupCalled = true
            };

            InvokeCleanupViaReflection(deferred);
            Assert.That(cleanupCalled, Is.True);
        }

        [Test]
        public void DeferredResult_Cleanup_SwallowsExceptions()
        {
            var deferred = new DeferredResult
            {
                Cleanup = () => throw new InvalidOperationException("Test exception")
            };

            Assert.DoesNotThrow(() => InvokeCleanupViaReflection(deferred),
                "Cleanup should swallow exceptions.");
        }

        [Test]
        public void DeferredResult_NullCleanup_DoesNotThrow()
        {
            var deferred = new DeferredResult();
            Assert.DoesNotThrow(() => InvokeCleanupViaReflection(deferred));
        }

        [Test]
        public void WaitForFrames_ReturnsDeferred_WithCorrectFrameCount()
        {
            int frame = 20;
            var result = DeferredResult.WaitForFrames(5, 30, () => frame, () => 0);

            Assert.That(result, Is.InstanceOf<DeferredResult>(),
                "WaitForFrames should return a DeferredResult.");

            var deferred = (DeferredResult)result;
            Assert.That(deferred.IsComplete, Is.Not.Null);
            Assert.That(deferred.GetResult, Is.Not.Null);

            for (int i = 0; i < 10; i++) Assert.That(deferred.IsComplete(), Is.False, "Polling must not advance frames.");
            for (int i = 0; i < 4; i++)
            {
                frame++;
                Assert.That(deferred.IsComplete(), Is.False, $"Should not complete after {i + 1} frames.");
            }

            frame++;
            Assert.That(deferred.IsComplete(), Is.True,
                "Should complete after 5 frames.");

            var payload = deferred.GetResult();
            Assert.That(payload["frames_elapsed"].Value<int>(), Is.EqualTo(5));
        }

        [Test]
        public void WaitForFrames_ClampsMaxCount_To600()
        {
            int frame = 0;
            var result = DeferredResult.WaitForFrames(9999, 30, () => frame, () => 0);

            Assert.That(result, Is.InstanceOf<DeferredResult>());
            var deferred = (DeferredResult)result;

            // It should clamp to 600 frames — verify by stepping 600 times
            frame = 599;
            Assert.That(deferred.IsComplete(), Is.False);
            frame = 600;

            Assert.That(deferred.IsComplete(), Is.True,
                "Should complete at 600 frames (clamped from 9999).");
        }

        [Test]
        public void WaitForFrames_RejectsZeroCount()
        {
            var parameters = new JObject { ["count"] = 0 };
            var result = SessionTools.WaitForFrames(parameters);

            // Should return an error JObject, not a DeferredResult
            Assert.That(result, Is.InstanceOf<JObject>());
            var error = (JObject)result;
            Assert.That(error["error"]?.Value<string>(), Does.Contain("count"),
                "Should return an error about count.");
        }

        [Test]
        public void WaitForFrames_TimeoutSeconds_IsClamped()
        {
            var parameters = new JObject
            {
                ["count"] = 10,
                ["max_wait_seconds"] = 120
            };
            var result = SessionTools.WaitForFrames(parameters);

            Assert.That(result, Is.InstanceOf<DeferredResult>());
            var deferred = (DeferredResult)result;
            Assert.That(deferred.TimeoutSeconds, Is.LessThanOrEqualTo(60f),
                "Timeout should be clamped to max 60 seconds.");
        }
    }
}
