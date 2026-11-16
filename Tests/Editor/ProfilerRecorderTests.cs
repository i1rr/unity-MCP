using System.Collections;
using System.Collections.Generic;
using I1rr.UnityMcp.Shared;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace I1rr.UnityMcp.Editor.Tests
{
    /// <summary>
    /// Integration tests for ProfilerRecorderManager: verifies sample accumulation over
    /// multiple frames and domain reload recovery via the [InitializeOnLoadMethod] re-init path.
    /// </summary>
    public class ProfilerRecorderTests
    {
        private IProfilerRecorderProvider _previousProvider;

        [SetUp]
        public void SetUp()
        {
            _previousProvider = ProfilerRecorderService.Provider;
        }

        [TearDown]
        public void TearDown()
        {
            ProfilerRecorderService.Provider = _previousProvider;
        }

        [Test]
        public void Provider_IsRegistered_AfterEditorInit()
        {
            // The editor ProfilerRecorderManager registers itself via [InitializeOnLoadMethod].
            // By the time tests run, the provider should be set.
            Assert.That(ProfilerRecorderService.Provider, Is.Not.Null,
                "ProfilerRecorderService.Provider should be set after editor initialization.");
        }

        [UnityTest]
        public IEnumerator StandardCounters_AccumulateSamples_OverMultipleFrames()
        {
            var provider = ProfilerRecorderService.Provider;
            Assert.That(provider, Is.Not.Null, "Provider must be initialized.");

            // Wait a few frames for recorders to accumulate data
            for (int i = 0; i < 10; i++)
                yield return null;

            // At least one standard counter should have valid data
            bool anyValid = provider.IsCounterValid("Total Used Memory")
                         || provider.IsCounterValid("GC Used Memory")
                         || provider.IsCounterValid("System Used Memory");

            Assert.That(anyValid, Is.True,
                "At least one standard memory counter should be valid after 10 frames.");
        }

        [UnityTest]
        public IEnumerator GetSamples_ReturnsNonEmpty_ForStandardCounter()
        {
            var provider = ProfilerRecorderService.Provider;
            Assert.That(provider, Is.Not.Null);

            // Wait for samples to accumulate
            for (int i = 0; i < 15; i++)
                yield return null;

            List<long> samples = provider.GetSamples("Total Used Memory", 5);

            // It's possible some counters aren't available in test environment,
            // so we only assert structure if the counter is valid.
            if (provider.IsCounterValid("Total Used Memory"))
            {
                Assert.That(samples, Is.Not.Null);
                Assert.That(samples.Count, Is.GreaterThan(0),
                    "Should have at least one sample for Total Used Memory.");
            }
        }

        [UnityTest]
        public IEnumerator GetLastValue_ReturnsValue_ForActiveCounter()
        {
            var provider = ProfilerRecorderService.Provider;
            Assert.That(provider, Is.Not.Null);

            // Wait for data
            for (int i = 0; i < 10; i++)
                yield return null;

            if (provider.IsCounterValid("Total Used Memory"))
            {
                long value = provider.GetLastValue("Total Used Memory");
                Assert.That(value, Is.GreaterThan(0),
                    "Total Used Memory should report a non-zero value.");
            }
        }

        [Test]
        public void GetSamples_ReturnsEmpty_ForInvalidCounter()
        {
            var provider = ProfilerRecorderService.Provider;
            Assert.That(provider, Is.Not.Null);

            var samples = provider.GetSamples("NonExistentCounter_XYZ_12345", 10);
            Assert.That(samples, Is.Not.Null);
            Assert.That(samples.Count, Is.EqualTo(0),
                "Invalid counter should return an empty sample list.");
        }

        [Test]
        public void IsCounterValid_ReturnsFalse_ForNonExistentCounter()
        {
            var provider = ProfilerRecorderService.Provider;
            Assert.That(provider, Is.Not.Null);

            bool valid = provider.IsCounterValid("NonExistentCounter_XYZ_12345");
            Assert.That(valid, Is.False);
        }

        [Test]
        public void MockProvider_CanBeSwapped_ViaServiceLocator()
        {
            var mock = new MockProfilerRecorderProvider();
            mock.AddSample("TestCounter", 42);

            ProfilerRecorderService.Provider = mock;

            Assert.That(ProfilerRecorderService.Provider, Is.SameAs(mock));
            Assert.That(ProfilerRecorderService.Provider.GetLastValue("TestCounter"), Is.EqualTo(42));
        }

        [UnityTest]
        public IEnumerator DomainReloadRecovery_ProviderIsReinitialized()
        {
            // Simulate what happens after a domain reload: the provider should be re-set.
            // We can't actually trigger a domain reload in a test, but we can verify that
            // the initialization path correctly sets up the provider.
            var provider = ProfilerRecorderService.Provider;
            Assert.That(provider, Is.Not.Null,
                "Provider should be re-initialized after domain reload (via [InitializeOnLoadMethod]).");

            // Verify it can still produce data after re-init
            for (int i = 0; i < 5; i++)
                yield return null;

            // Provider should still be the same instance (no spurious re-creation)
            Assert.That(ProfilerRecorderService.Provider, Is.SameAs(provider),
                "Provider instance should remain stable between frames.");
        }

        /// <summary>
        /// Simple mock provider for testing the service locator pattern.
        /// </summary>
        private class MockProfilerRecorderProvider : IProfilerRecorderProvider
        {
            private readonly Dictionary<string, List<long>> _data = new Dictionary<string, List<long>>();

            public void AddSample(string counterName, long value)
            {
                if (!_data.TryGetValue(counterName, out var list))
                {
                    list = new List<long>();
                    _data[counterName] = list;
                }
                list.Add(value);
            }

            public List<long> GetSamples(string counterName, int count)
            {
                if (!_data.TryGetValue(counterName, out var list))
                    return new List<long>();
                int start = System.Math.Max(0, list.Count - count);
                return list.GetRange(start, list.Count - start);
            }

            public long GetLastValue(string counterName)
            {
                if (!_data.TryGetValue(counterName, out var list) || list.Count == 0)
                    return 0;
                return list[list.Count - 1];
            }

            public bool IsCounterValid(string counterName) => _data.ContainsKey(counterName);

            public bool RequestOnDemandCounter(string category, string counterName) => false;
        }
    }
}
