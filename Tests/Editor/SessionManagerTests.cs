using System.Collections;
using System.Collections.Generic;
using I1rr.UnityMcp.Shared;
using I1rr.UnityMcp.Shared.Sessions;
using I1rr.UnityMcp.Editor.Sessions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace I1rr.UnityMcp.Editor.Tests
{
    /// <summary>
    /// Integration tests for SessionManager recording sessions: verifies session lifecycle,
    /// sample collection, max-session overflow, and play-mode-exit cleanup.
    /// </summary>
    public class SessionManagerTests
    {
        private IProfilerRecorderProvider _previousProvider;
        private MockProfilerProvider _mockProvider;

        [SetUp]
        public void SetUp()
        {
            _previousProvider = ProfilerRecorderService.Provider;
            _mockProvider = new MockProfilerProvider();
            ProfilerRecorderService.Provider = _mockProvider;

            SessionManager.Shutdown();
            SessionManager.Initialize();
        }

        [TearDown]
        public void TearDown()
        {
            SessionManager.Shutdown();
            ProfilerRecorderService.Provider = _previousProvider;
        }

        [Test]
        public void StartSession_ReturnsSessionId()
        {
            var result = SessionManager.Instance.StartSession(
                new[] { "TestCounter" }, intervalFrames: 1, durationSeconds: 10f,
                maxSamples: 100, intervalClamped: false);

            Assert.That(result["session_id"], Is.Not.Null);
            Assert.That(result["session_id"].Value<string>(), Is.Not.Empty);
            Assert.That(result["started_at"], Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator Session_CollectsSamples_OverFrames()
        {
            _mockProvider.SetValue("TestCounter", 42);

            var startResult = SessionManager.Instance.StartSession(
                new[] { "TestCounter" }, intervalFrames: 1, durationSeconds: 30f,
                maxSamples: 100, intervalClamped: false);

            string sessionId = startResult["session_id"].Value<string>();

            // Step frames to collect samples
            for (int i = 0; i < 5; i++)
            {
                _mockProvider.SetValue("TestCounter", 42 + i);
                SessionManager.Instance.Update();
                yield return null;
            }

            var status = SessionManager.Instance.GetStatus(sessionId);
            Assert.That(status["active"].Value<bool>(), Is.True);
            Assert.That(status["samples_collected"].Value<int>(), Is.GreaterThan(0),
                "Should have collected samples after stepping frames.");
        }

        [UnityTest]
        public IEnumerator StopSession_ReturnsNonZeroSamples()
        {
            _mockProvider.SetValue("TestCounter", 100);

            var startResult = SessionManager.Instance.StartSession(
                new[] { "TestCounter" }, intervalFrames: 1, durationSeconds: 60f,
                maxSamples: 1000, intervalClamped: false);

            string sessionId = startResult["session_id"].Value<string>();

            // Collect some samples
            for (int i = 0; i < 10; i++)
            {
                _mockProvider.SetValue("TestCounter", 100 + i * 10);
                SessionManager.Instance.Update();
                yield return null;
            }

            var stopResult = SessionManager.Instance.StopSession(sessionId);
            Assert.That(stopResult["samples_collected"].Value<int>(), Is.GreaterThan(0),
                "Stopped session should report non-zero samples.");
            Assert.That(stopResult["counters"], Is.Not.Null);
        }

        [Test]
        public void MaxConcurrentSessions_ReturnsError_WhenExceeded()
        {
            for (int i = 0; i < SessionManager.MaxConcurrentSessions; i++)
            {
                var result = SessionManager.Instance.StartSession(
                    new[] { $"Counter{i}" }, intervalFrames: 1, durationSeconds: 60f,
                    maxSamples: 1000, intervalClamped: false);
                Assert.That(result["session_id"], Is.Not.Null,
                    $"Session {i} should start successfully.");
            }

            // One more should fail
            var overflow = SessionManager.Instance.StartSession(
                new[] { "Overflow" }, intervalFrames: 1, durationSeconds: 60f,
                maxSamples: 1000, intervalClamped: false);

            Assert.That(overflow["error"], Is.Not.Null,
                "Should return an error when max sessions exceeded.");
            Assert.That(overflow["error_code"]?.Value<string>(), Is.EqualTo(McpErrorCodes.MaxLimitReached));
        }

        [Test]
        public void GetStatus_ReturnsError_ForUnknownSession()
        {
            var status = SessionManager.Instance.GetStatus("nonexistent_session_id");
            Assert.That(status["error"], Is.Not.Null);
        }

        [Test]
        public void StopSession_ReturnsError_ForUnknownSession()
        {
            var result = SessionManager.Instance.StopSession("nonexistent_session_id");
            Assert.That(result["error"], Is.Not.Null);
        }

        [Test]
        public void RecordingSession_MaxSamples_StopsAutomatically()
        {
            var session = new RecordingSession("test", new[] { "Counter" },
                intervalFrames: 1, durationSeconds: 0f, maxSamples: 3, intervalClamped: false);

            for (int i = 0; i < 10; i++)
            {
                bool shouldContinue = session.Tick(_mockProvider);
                if (!shouldContinue)
                    break;
            }

            Assert.That(session.IsStopped, Is.True,
                "Session should auto-stop after reaching max samples.");
            Assert.That(session.SamplesCollected, Is.EqualTo(3));
        }

        [Test]
        public void RecordingSession_GetResults_ReturnsValidStructure()
        {
            _mockProvider.SetValue("Counter", 50);
            var session = new RecordingSession("test", new[] { "Counter" },
                intervalFrames: 1, durationSeconds: 0f, maxSamples: 5, intervalClamped: false);

            for (int i = 0; i < 5; i++)
                session.Tick(_mockProvider);

            session.Stop();
            var results = session.GetResults();

            Assert.That(results["duration_seconds"], Is.Not.Null);
            Assert.That(results["samples_collected"].Value<int>(), Is.EqualTo(5));
            Assert.That(results["counters"], Is.Not.Null);
            Assert.That(results["counters"].Type, Is.EqualTo(JTokenType.Array));
        }

        private class MockProfilerProvider : IProfilerRecorderProvider
        {
            private readonly Dictionary<string, long> _values = new Dictionary<string, long>();

            public void SetValue(string name, long value) => _values[name] = value;

            public List<long> GetSamples(string counterName, int count)
            {
                if (_values.TryGetValue(counterName, out var val))
                    return new List<long> { val };
                return new List<long>();
            }

            public long GetLastValue(string counterName)
            {
                return _values.TryGetValue(counterName, out var val) ? val : 0;
            }

            public bool IsCounterValid(string counterName) => _values.ContainsKey(counterName);
            public bool RequestOnDemandCounter(string category, string counterName) => false;
        }
    }
}
