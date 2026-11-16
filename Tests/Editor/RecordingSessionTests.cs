using System.Collections.Generic;
using I1rr.UnityMcp.Shared;
using I1rr.UnityMcp.Shared.Sessions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace I1rr.UnityMcp.Editor.Tests
{
    /// <summary>
    /// Unit tests for <see cref="RecordingSession"/>: sample accumulation, interval logic,
    /// aggregation math (min/max/avg/p50/p95/p99), and max samples cap.
    /// </summary>
    public class RecordingSessionTests
    {
        /// <summary>Fake provider that returns configurable values per counter.</summary>
        private class FakeProvider : IProfilerRecorderProvider
        {
            private readonly Dictionary<string, long> _values = new Dictionary<string, long>();

            public void SetValue(string counter, long value) => _values[counter] = value;

            public long GetLastValue(string counterName) =>
                _values.TryGetValue(counterName, out var v) ? v : 0;

            public List<long> GetSamples(string counterName, int count) =>
                new List<long> { GetLastValue(counterName) };

            public bool IsCounterValid(string counterName) => _values.ContainsKey(counterName);

            public bool RequestOnDemandCounter(string category, string counterName) => _values.ContainsKey(counterName);
        }

        [Test]
        public void NewSession_HasZeroSamples()
        {
            var session = new RecordingSession("test", new[] { "A" }, 1, 0, 100, false);
            Assert.That(session.SamplesCollected, Is.EqualTo(0));
            Assert.That(session.IsStopped, Is.False);
        }

        [Test]
        public void Tick_SamplesAtInterval()
        {
            var provider = new FakeProvider();
            provider.SetValue("A", 42);

            var session = new RecordingSession("test", new[] { "A" },
                intervalFrames: 3, durationSeconds: 0, maxSamples: 100, intervalClamped: false);

            // Tick 2 frames - no sample yet (interval=3)
            session.Tick(provider);
            session.Tick(provider);
            Assert.That(session.SamplesCollected, Is.EqualTo(0));

            // Third tick triggers a sample
            session.Tick(provider);
            Assert.That(session.SamplesCollected, Is.EqualTo(1));
        }

        [Test]
        public void Tick_AccumulatesSamples_EveryInterval()
        {
            var provider = new FakeProvider();
            provider.SetValue("A", 10);

            var session = new RecordingSession("test", new[] { "A" },
                intervalFrames: 1, durationSeconds: 0, maxSamples: 100, intervalClamped: false);

            for (int i = 0; i < 5; i++)
                session.Tick(provider);

            Assert.That(session.SamplesCollected, Is.EqualTo(5));
            Assert.That(session.FramesElapsed, Is.EqualTo(5));
        }

        [Test]
        public void MaxSamples_StopsSession()
        {
            var provider = new FakeProvider();
            provider.SetValue("A", 1);

            int maxSamples = 3;
            var session = new RecordingSession("test", new[] { "A" },
                intervalFrames: 1, durationSeconds: 0, maxSamples: maxSamples, intervalClamped: false);

            bool alive = true;
            for (int i = 0; i < 10 && alive; i++)
                alive = session.Tick(provider);

            Assert.That(session.IsStopped, Is.True);
            Assert.That(session.SamplesCollected, Is.EqualTo(maxSamples));
        }

        [Test]
        public void Stop_MarksSessionStopped()
        {
            var session = new RecordingSession("test", new[] { "A" }, 1, 0, 100, false);
            session.Stop();
            Assert.That(session.IsStopped, Is.True);
        }

        [Test]
        public void Tick_AfterStop_ReturnsFalse()
        {
            var provider = new FakeProvider();
            provider.SetValue("A", 1);

            var session = new RecordingSession("test", new[] { "A" }, 1, 0, 100, false);
            session.Stop();

            bool result = session.Tick(provider);
            Assert.That(result, Is.False);
            Assert.That(session.SamplesCollected, Is.EqualTo(0));
        }

        [Test]
        public void GetResults_EmptySession_ReturnsZeroAggregates()
        {
            var session = new RecordingSession("test", new[] { "CounterA" }, 1, 0, 100, false);
            var results = session.GetResults();

            Assert.That(results["samples_collected"].Value<int>(), Is.EqualTo(0));

            var counters = (JArray)results["counters"];
            Assert.That(counters.Count, Is.EqualTo(1));

            var counter = (JObject)counters[0];
            Assert.That(counter["name"].Value<string>(), Is.EqualTo("CounterA"));
            Assert.That(counter["min"].Value<long>(), Is.EqualTo(0));
            Assert.That(counter["max"].Value<long>(), Is.EqualTo(0));
            Assert.That(counter["avg"].Value<double>(), Is.EqualTo(0));
        }

        [Test]
        public void GetResults_WithSamples_CorrectMinMaxAvg()
        {
            var provider = new FakeProvider();
            var session = new RecordingSession("test", new[] { "A" },
                intervalFrames: 1, durationSeconds: 0, maxSamples: 100, intervalClamped: false);

            // Record values: 10, 20, 30, 40, 50
            long[] values = { 10, 20, 30, 40, 50 };
            for (int i = 0; i < values.Length; i++)
            {
                provider.SetValue("A", values[i]);
                session.Tick(provider);
            }

            var results = session.GetResults();
            Assert.That(results["samples_collected"].Value<int>(), Is.EqualTo(5));

            var counters = (JArray)results["counters"];
            var counter = (JObject)counters[0];

            Assert.That(counter["min"].Value<long>(), Is.EqualTo(10));
            Assert.That(counter["max"].Value<long>(), Is.EqualTo(50));
            Assert.That(counter["avg"].Value<double>(), Is.EqualTo(30.0).Within(0.01));
        }

        [Test]
        public void GetResults_Percentiles_Correct()
        {
            var provider = new FakeProvider();
            var session = new RecordingSession("test", new[] { "A" },
                intervalFrames: 1, durationSeconds: 0, maxSamples: 200, intervalClamped: false);

            // Record 100 values: 1, 2, 3, ..., 100
            for (int i = 1; i <= 100; i++)
            {
                provider.SetValue("A", i);
                session.Tick(provider);
            }

            var results = session.GetResults();
            var counters = (JArray)results["counters"];
            var counter = (JObject)counters[0];

            long p50 = counter["p50"].Value<long>();
            long p95 = counter["p95"].Value<long>();
            long p99 = counter["p99"].Value<long>();

            // p50 of 1..100 sorted ~ index 49.5 → ~50
            Assert.That(p50, Is.InRange(49, 51));
            // p95 ~ index 94.05 → ~95
            Assert.That(p95, Is.InRange(94, 96));
            // p99 ~ index 98.01 → ~99
            Assert.That(p99, Is.InRange(98, 100));
        }

        [Test]
        public void GetResults_MultipleCounters_AllTracked()
        {
            var provider = new FakeProvider();
            provider.SetValue("A", 100);
            provider.SetValue("B", 200);

            var session = new RecordingSession("test", new[] { "A", "B" },
                intervalFrames: 1, durationSeconds: 0, maxSamples: 100, intervalClamped: false);

            session.Tick(provider);
            session.Tick(provider);

            var results = session.GetResults();
            var counters = (JArray)results["counters"];
            Assert.That(counters.Count, Is.EqualTo(2));

            // Both counters should have 2 samples each
            foreach (JObject counter in counters)
            {
                var samples = (JArray)counter["samples"];
                Assert.That(samples.Count, Is.EqualTo(2));
            }
        }

        [Test]
        public void GetResults_SamplesArray_ContainsRecordedValues()
        {
            var provider = new FakeProvider();
            var session = new RecordingSession("test", new[] { "X" },
                intervalFrames: 1, durationSeconds: 0, maxSamples: 100, intervalClamped: false);

            provider.SetValue("X", 5);
            session.Tick(provider);
            provider.SetValue("X", 15);
            session.Tick(provider);
            provider.SetValue("X", 25);
            session.Tick(provider);

            var results = session.GetResults();
            var samples = (JArray)((JObject)((JArray)results["counters"])[0])["samples"];

            Assert.That(samples.Count, Is.EqualTo(3));
            Assert.That(samples[0].Value<long>(), Is.EqualTo(5));
            Assert.That(samples[1].Value<long>(), Is.EqualTo(15));
            Assert.That(samples[2].Value<long>(), Is.EqualTo(25));
        }

        [Test]
        public void SessionProperties_AreSetCorrectly()
        {
            var session = new RecordingSession("my-session", new[] { "A", "B" },
                intervalFrames: 5, durationSeconds: 10f, maxSamples: 500, intervalClamped: true);

            Assert.That(session.Id, Is.EqualTo("my-session"));
            Assert.That(session.Counters.Length, Is.EqualTo(2));
            Assert.That(session.IntervalFrames, Is.EqualTo(5));
            Assert.That(session.DurationSeconds, Is.EqualTo(10f));
            Assert.That(session.MaxSamples, Is.EqualTo(500));
            Assert.That(session.IntervalClamped, Is.True);
        }

        [Test]
        public void GetPreview_ReturnsCurrentValues()
        {
            var provider = new FakeProvider();
            provider.SetValue("A", 42);
            provider.SetValue("B", 99);

            var session = new RecordingSession("test", new[] { "A", "B" }, 1, 0, 100, false);
            var preview = session.GetPreview(provider);

            Assert.That(preview.Count, Is.EqualTo(2));
            Assert.That(preview[0]["name"].Value<string>(), Is.EqualTo("A"));
            Assert.That(preview[0]["last_value"].Value<long>(), Is.EqualTo(42));
            Assert.That(preview[1]["name"].Value<string>(), Is.EqualTo("B"));
            Assert.That(preview[1]["last_value"].Value<long>(), Is.EqualTo(99));
        }
    }
}
