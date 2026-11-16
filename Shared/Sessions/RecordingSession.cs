using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace I1rr.UnityMcp.Shared.Sessions
{
    /// <summary>
    /// Records profiler counter samples at a configurable interval into bounded ring buffers.
    /// This is a stateless data structure — lifecycle is managed by the platform-specific SessionManager.
    /// </summary>
    public sealed class RecordingSession
    {
        public const int MaxSamplesPerCounter = 3600;
        public const int MinIntervalFrames = 1;
        public const int MaxIntervalFrames = 300;
        public const int MinIntervalMs = 16;
        public const int MaxIntervalMs = 5000;

        public string Id { get; }
        public DateTime StartedAtUtc { get; }
        public string[] Counters { get; }
        public int IntervalFrames { get; }
        public float DurationSeconds { get; }
        public int MaxSamples { get; }
        public bool IntervalClamped { get; }

        private readonly Dictionary<string, RingBuffer> _buffers;
        private int _framesSinceLastSample;
        private bool _stopped;

        public bool IsStopped => _stopped;
        public int SamplesCollected { get; private set; }
        public int FramesElapsed { get; private set; }

        public RecordingSession(string id, string[] counters, int intervalFrames, float durationSeconds, int maxSamples, bool intervalClamped)
        {
            Id = id;
            StartedAtUtc = DateTime.UtcNow;
            Counters = counters;
            IntervalFrames = intervalFrames;
            DurationSeconds = durationSeconds;
            MaxSamples = maxSamples;
            IntervalClamped = intervalClamped;

            _buffers = new Dictionary<string, RingBuffer>(counters.Length);
            foreach (var counter in counters)
            {
                _buffers[counter] = new RingBuffer(maxSamples);
            }
        }

        /// <summary>
        /// Called each frame. Returns true if the session should continue, false if it has expired.
        /// </summary>
        public bool Tick(IProfilerRecorderProvider provider)
        {
            if (_stopped) return false;

            FramesElapsed++;
            _framesSinceLastSample++;

            // Check duration timeout
            if (DurationSeconds > 0 && (DateTime.UtcNow - StartedAtUtc).TotalSeconds >= DurationSeconds)
            {
                _stopped = true;
                return false;
            }

            // Sample at the configured interval
            if (_framesSinceLastSample >= IntervalFrames)
            {
                _framesSinceLastSample = 0;

                foreach (var counter in Counters)
                {
                    long value = provider.GetLastValue(counter);
                    _buffers[counter].Add(value);
                }
                SamplesCollected++;

                // Check max samples
                if (SamplesCollected >= MaxSamples)
                {
                    _stopped = true;
                    return false;
                }
            }

            return true;
        }

        public void Stop()
        {
            _stopped = true;
        }

        /// <summary>
        /// Returns a preview of the last value for each counter.
        /// </summary>
        public JArray GetPreview(IProfilerRecorderProvider provider)
        {
            var preview = new JArray();
            foreach (var counter in Counters)
            {
                preview.Add(new JObject
                {
                    ["name"] = counter,
                    ["last_value"] = provider.GetLastValue(counter)
                });
            }
            return preview;
        }

        /// <summary>
        /// Returns aggregated results for all counters.
        /// </summary>
        public JObject GetResults()
        {
            double elapsed = (DateTime.UtcNow - StartedAtUtc).TotalSeconds;

            var countersArray = new JArray();
            foreach (var counter in Counters)
            {
                var buffer = _buffers[counter];
                var samples = buffer.ToArray();
                countersArray.Add(BuildCounterResult(counter, samples));
            }

            return new JObject
            {
                ["duration_seconds"] = Math.Round(elapsed, 3),
                ["samples_collected"] = SamplesCollected,
                ["counters"] = countersArray
            };
        }

        private static JObject BuildCounterResult(string name, long[] samples)
        {
            int count = samples.Length;
            if (count == 0)
            {
                return new JObject
                {
                    ["name"] = name,
                    ["min"] = 0,
                    ["max"] = 0,
                    ["avg"] = 0,
                    ["p50"] = 0,
                    ["p95"] = 0,
                    ["p99"] = 0,
                    ["samples"] = new JArray()
                };
            }

            long min = long.MaxValue, max = long.MinValue;
            double sum = 0;
            for (int i = 0; i < count; i++)
            {
                if (samples[i] < min) min = samples[i];
                if (samples[i] > max) max = samples[i];
                sum += samples[i];
            }

            // Sort a copy for percentile calculation
            var sorted = new long[count];
            Array.Copy(samples, sorted, count);
            Array.Sort(sorted);

            double avg = sum / count;
            long p50 = Percentile(sorted, 0.50);
            long p95 = Percentile(sorted, 0.95);
            long p99 = Percentile(sorted, 0.99);

            var samplesArray = new JArray();
            for (int i = 0; i < count; i++)
                samplesArray.Add(samples[i]);

            return new JObject
            {
                ["name"] = name,
                ["min"] = min,
                ["max"] = max,
                ["avg"] = Math.Round(avg, 2),
                ["p50"] = p50,
                ["p95"] = p95,
                ["p99"] = p99,
                ["samples"] = samplesArray
            };
        }

        private static long Percentile(long[] sorted, double p)
        {
            if (sorted.Length == 0) return 0;
            double index = (sorted.Length - 1) * p;
            int lower = (int)Math.Floor(index);
            if (lower >= sorted.Length - 1) return sorted[sorted.Length - 1];
            double frac = index - lower;
            return (long)(sorted[lower] + frac * (sorted[lower + 1] - sorted[lower]));
        }

        /// <summary>
        /// Simple bounded ring buffer for long values.
        /// </summary>
        private sealed class RingBuffer
        {
            private readonly long[] _data;
            private int _head;
            private int _count;

            public RingBuffer(int capacity)
            {
                _data = new long[capacity];
            }

            public void Add(long value)
            {
                _data[_head] = value;
                _head = (_head + 1) % _data.Length;
                if (_count < _data.Length) _count++;
            }

            public long[] ToArray()
            {
                var result = new long[_count];
                if (_count == 0) return result;

                int start = _count < _data.Length ? 0 : _head;
                for (int i = 0; i < _count; i++)
                {
                    result[i] = _data[(start + i) % _data.Length];
                }
                return result;
            }
        }
    }
}
