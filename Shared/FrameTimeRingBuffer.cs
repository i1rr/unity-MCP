using System;
using System.Collections.Generic;
using UnityEngine;

namespace I1rr.UnityMcp.Shared
{
    /// <summary>
    /// A single frame sample from the ring buffer.
    /// </summary>
    public struct FrameSample
    {
        public float DeltaTime;
        public int FrameCount;
        public float RealtimeSinceStartup;
    }

    /// <summary>
    /// Zero-allocation ring buffer that records per-frame timing data from <see cref="Time.unscaledDeltaTime"/>.
    /// Pre-allocates all arrays at construction; no GC after init.
    /// Capacity: 600 frames (~10 seconds at 60 fps).
    /// </summary>
    public sealed class FrameTimeRingBuffer
    {
        public const int Capacity = 600;

        private readonly float[] _deltaTimes = new float[Capacity];
        private readonly int[] _frameCounts = new int[Capacity];
        private readonly float[] _realtimeSinceStartup = new float[Capacity];

        private int _head;  // next write index
        private int _count; // total entries written (clamped to Capacity)

        /// <summary>Number of frames currently stored (up to <see cref="Capacity"/>).</summary>
        public int Count => _count;

        /// <summary>
        /// Records one frame of timing data. Call once per frame from a dispatcher's Update loop.
        /// </summary>
        public void Record(float unscaledDeltaTime, int frameCount, float realtimeSinceStartup)
        {
            _deltaTimes[_head] = unscaledDeltaTime;
            _frameCounts[_head] = frameCount;
            _realtimeSinceStartup[_head] = realtimeSinceStartup;

            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
        }

        /// <summary>
        /// Reads up to <paramref name="maxFrames"/> most recent entries into the caller-supplied arrays.
        /// Returns the actual number of entries written (min of <paramref name="maxFrames"/> and <see cref="Count"/>).
        /// Arrays must be at least <paramref name="maxFrames"/> in length.
        /// </summary>
        public int Read(int maxFrames, float[] outDeltaTimes, int[] outFrameCounts, float[] outRealtime)
        {
            int toRead = Math.Min(maxFrames, _count);
            if (toRead == 0) return 0;

            // Oldest of the requested entries is at (_head - toRead) mod Capacity.
            int start = ((_head - toRead) % Capacity + Capacity) % Capacity;

            for (int i = 0; i < toRead; i++)
            {
                int idx = (start + i) % Capacity;
                outDeltaTimes[i] = _deltaTimes[idx];
                outFrameCounts[i] = _frameCounts[idx];
                outRealtime[i] = _realtimeSinceStartup[idx];
            }

            return toRead;
        }

        /// <summary>
        /// Computes aggregate stats over the most recent <paramref name="sampleFrames"/> entries.
        /// </summary>
        public FrameTimeStats ComputeStats(int sampleFrames)
        {
            int toRead = Math.Min(sampleFrames, _count);
            if (toRead == 0)
                return default;

            int start = ((_head - toRead) % Capacity + Capacity) % Capacity;

            double sum = 0;
            float min = float.MaxValue;
            float max = 0f;

            for (int i = 0; i < toRead; i++)
            {
                float dt = _deltaTimes[(start + i) % Capacity];
                sum += dt;
                if (dt < min) min = dt;
                if (dt > max) max = dt;
            }

            float avg = (float)(sum / toRead);

            return new FrameTimeStats
            {
                FrameCount = toRead,
                AvgDeltaTime = avg,
                MinDeltaTime = min,
                MaxDeltaTime = max,
                Fps = avg > 0f ? 1f / avg : 0f
            };
        }

        /// <summary>
        /// Detects spikes: frames where deltaTime &gt; 2× the average over the full buffer.
        /// Returns the number of spikes written into the output arrays (up to <paramref name="maxSpikes"/>).
        /// </summary>
        public int DetectSpikes(float[] outDeltaTimes, int[] outFrameCounts, float[] outRealtime, int maxSpikes)
        {
            if (_count == 0 || maxSpikes <= 0) return 0;

            // Compute average over the full buffer
            int fullCount = _count;
            int fullStart = ((_head - fullCount) % Capacity + Capacity) % Capacity;

            double sum = 0;
            for (int i = 0; i < fullCount; i++)
                sum += _deltaTimes[(fullStart + i) % Capacity];

            float avg = (float)(sum / fullCount);
            float threshold = avg * 2f;

            int spikeCount = 0;
            for (int i = 0; i < fullCount && spikeCount < maxSpikes; i++)
            {
                int idx = (fullStart + i) % Capacity;
                if (_deltaTimes[idx] > threshold)
                {
                    outDeltaTimes[spikeCount] = _deltaTimes[idx];
                    outFrameCounts[spikeCount] = _frameCounts[idx];
                    outRealtime[spikeCount] = _realtimeSinceStartup[idx];
                    spikeCount++;
                }
            }

            return spikeCount;
        }

        /// <summary>
        /// Returns samples where <see cref="FrameSample.FrameCount"/> &gt;= <paramref name="minFrame"/>.
        /// If <paramref name="minFrame"/> is older than the oldest sample, returns all available samples.
        /// If <paramref name="minFrame"/> is in the future, returns an empty array.
        /// </summary>
        public FrameSample[] GetSamplesSinceFrame(int minFrame)
        {
            if (_count == 0) return Array.Empty<FrameSample>();

            int start = ((_head - _count) % Capacity + Capacity) % Capacity;

            var list = new List<FrameSample>();
            for (int i = 0; i < _count; i++)
            {
                int idx = (start + i) % Capacity;
                if (_frameCounts[idx] >= minFrame)
                {
                    list.Add(new FrameSample
                    {
                        DeltaTime = _deltaTimes[idx],
                        FrameCount = _frameCounts[idx],
                        RealtimeSinceStartup = _realtimeSinceStartup[idx]
                    });
                }
            }

            return list.ToArray();
        }

        /// <summary>
        /// Returns samples where <see cref="FrameSample.RealtimeSinceStartup"/> &gt;= <paramref name="minTime"/>.
        /// If the buffer is empty, returns an empty array.
        /// </summary>
        public FrameSample[] GetSamplesSinceTime(float minTime)
        {
            if (_count == 0) return Array.Empty<FrameSample>();

            int start = ((_head - _count) % Capacity + Capacity) % Capacity;

            var list = new List<FrameSample>();
            for (int i = 0; i < _count; i++)
            {
                int idx = (start + i) % Capacity;
                if (_realtimeSinceStartup[idx] >= minTime)
                {
                    list.Add(new FrameSample
                    {
                        DeltaTime = _deltaTimes[idx],
                        FrameCount = _frameCounts[idx],
                        RealtimeSinceStartup = _realtimeSinceStartup[idx]
                    });
                }
            }

            return list.ToArray();
        }
    }

    /// <summary>
    /// Aggregate frame-time statistics computed from the ring buffer.
    /// </summary>
    public struct FrameTimeStats
    {
        public int FrameCount;
        public float AvgDeltaTime;
        public float MinDeltaTime;
        public float MaxDeltaTime;
        public float Fps;
    }
}
