using I1rr.UnityMcp.Shared;
using NUnit.Framework;

namespace I1rr.UnityMcp.Editor.Tests
{
    /// <summary>
    /// Unit tests for <see cref="FrameTimeRingBuffer"/>: wrap-around, spike detection, empty buffer, capacity boundary.
    /// </summary>
    public class FrameTimeRingBufferTests
    {
        [Test]
        public void EmptyBuffer_ReturnsZeroCount()
        {
            var buffer = new FrameTimeRingBuffer();
            Assert.That(buffer.Count, Is.EqualTo(0));
        }

        [Test]
        public void EmptyBuffer_ReadReturnsZero()
        {
            var buffer = new FrameTimeRingBuffer();
            var dt = new float[10];
            var fc = new int[10];
            var rt = new float[10];

            int read = buffer.Read(10, dt, fc, rt);
            Assert.That(read, Is.EqualTo(0));
        }

        [Test]
        public void EmptyBuffer_ComputeStats_ReturnsDefault()
        {
            var buffer = new FrameTimeRingBuffer();
            var stats = buffer.ComputeStats(60);

            Assert.That(stats.FrameCount, Is.EqualTo(0));
            Assert.That(stats.AvgDeltaTime, Is.EqualTo(0f));
            Assert.That(stats.Fps, Is.EqualTo(0f));
        }

        [Test]
        public void EmptyBuffer_DetectSpikes_ReturnsZero()
        {
            var buffer = new FrameTimeRingBuffer();
            var dt = new float[10];
            var fc = new int[10];
            var rt = new float[10];

            int spikes = buffer.DetectSpikes(dt, fc, rt, 10);
            Assert.That(spikes, Is.EqualTo(0));
        }

        [Test]
        public void Record_SingleFrame_IncrementsCount()
        {
            var buffer = new FrameTimeRingBuffer();
            buffer.Record(0.016f, 1, 1.0f);

            Assert.That(buffer.Count, Is.EqualTo(1));
        }

        [Test]
        public void Record_MultipleFrames_ReadsInOrder()
        {
            var buffer = new FrameTimeRingBuffer();
            for (int i = 0; i < 5; i++)
                buffer.Record(0.01f * (i + 1), i + 1, i * 0.5f);

            Assert.That(buffer.Count, Is.EqualTo(5));

            var dt = new float[5];
            var fc = new int[5];
            var rt = new float[5];
            int read = buffer.Read(5, dt, fc, rt);

            Assert.That(read, Is.EqualTo(5));
            Assert.That(dt[0], Is.EqualTo(0.01f).Within(0.0001f));
            Assert.That(dt[4], Is.EqualTo(0.05f).Within(0.0001f));
            Assert.That(fc[0], Is.EqualTo(1));
            Assert.That(fc[4], Is.EqualTo(5));
        }

        [Test]
        public void Read_LessThanAvailable_ReturnsLatest()
        {
            var buffer = new FrameTimeRingBuffer();
            for (int i = 0; i < 10; i++)
                buffer.Record(0.016f, i + 1, i * 0.1f);

            var dt = new float[3];
            var fc = new int[3];
            var rt = new float[3];
            int read = buffer.Read(3, dt, fc, rt);

            Assert.That(read, Is.EqualTo(3));
            // Should return the last 3 frames (frameCount 8, 9, 10)
            Assert.That(fc[0], Is.EqualTo(8));
            Assert.That(fc[1], Is.EqualTo(9));
            Assert.That(fc[2], Is.EqualTo(10));
        }

        [Test]
        public void ExactCapacity_CountClamped()
        {
            var buffer = new FrameTimeRingBuffer();
            for (int i = 0; i < FrameTimeRingBuffer.Capacity; i++)
                buffer.Record(0.016f, i, i * 0.016f);

            Assert.That(buffer.Count, Is.EqualTo(FrameTimeRingBuffer.Capacity));
        }

        [Test]
        public void WrapAround_CountStaysAtCapacity()
        {
            var buffer = new FrameTimeRingBuffer();
            int total = FrameTimeRingBuffer.Capacity + 100;
            for (int i = 0; i < total; i++)
                buffer.Record(0.016f, i, i * 0.016f);

            Assert.That(buffer.Count, Is.EqualTo(FrameTimeRingBuffer.Capacity));
        }

        [Test]
        public void WrapAround_OldDataOverwritten_ReadsLatest()
        {
            var buffer = new FrameTimeRingBuffer();
            int total = FrameTimeRingBuffer.Capacity + 50;
            for (int i = 0; i < total; i++)
                buffer.Record(0.016f, i, i * 0.016f);

            var dt = new float[5];
            var fc = new int[5];
            var rt = new float[5];
            int read = buffer.Read(5, dt, fc, rt);

            Assert.That(read, Is.EqualTo(5));
            // The most recent 5 frames should have frameCount = total-5 .. total-1
            for (int i = 0; i < 5; i++)
                Assert.That(fc[i], Is.EqualTo(total - 5 + i));
        }

        [Test]
        public void WrapAround_ReadAll_ReturnsCapacityEntries()
        {
            var buffer = new FrameTimeRingBuffer();
            int total = FrameTimeRingBuffer.Capacity + 200;
            for (int i = 0; i < total; i++)
                buffer.Record(0.016f, i, i * 0.016f);

            var dt = new float[FrameTimeRingBuffer.Capacity];
            var fc = new int[FrameTimeRingBuffer.Capacity];
            var rt = new float[FrameTimeRingBuffer.Capacity];
            int read = buffer.Read(FrameTimeRingBuffer.Capacity, dt, fc, rt);

            Assert.That(read, Is.EqualTo(FrameTimeRingBuffer.Capacity));
            // Oldest should be total - Capacity, newest should be total - 1
            Assert.That(fc[0], Is.EqualTo(total - FrameTimeRingBuffer.Capacity));
            Assert.That(fc[read - 1], Is.EqualTo(total - 1));
        }

        [Test]
        public void ComputeStats_Correct_MinMaxAvgFps()
        {
            var buffer = new FrameTimeRingBuffer();
            // 10 frames at 0.01, 0.02, 0.03, ..., 0.10
            for (int i = 1; i <= 10; i++)
                buffer.Record(0.01f * i, i, i * 0.1f);

            var stats = buffer.ComputeStats(10);
            Assert.That(stats.FrameCount, Is.EqualTo(10));
            Assert.That(stats.MinDeltaTime, Is.EqualTo(0.01f).Within(0.0001f));
            Assert.That(stats.MaxDeltaTime, Is.EqualTo(0.10f).Within(0.0001f));
            // avg = (0.01+0.02+...+0.10)/10 = 0.55/10 = 0.055
            Assert.That(stats.AvgDeltaTime, Is.EqualTo(0.055f).Within(0.001f));
            Assert.That(stats.Fps, Is.GreaterThan(0f));
            Assert.That(stats.Fps, Is.EqualTo(1f / 0.055f).Within(0.5f));
        }

        [Test]
        public void ComputeStats_RequestMoreThanAvailable_UsesActualCount()
        {
            var buffer = new FrameTimeRingBuffer();
            buffer.Record(0.016f, 1, 0f);
            buffer.Record(0.032f, 2, 0.016f);

            var stats = buffer.ComputeStats(1000);
            Assert.That(stats.FrameCount, Is.EqualTo(2));
        }

        [Test]
        public void DetectSpikes_IdentifiesHighDeltaTimes()
        {
            var buffer = new FrameTimeRingBuffer();
            // 9 normal frames at 0.016, then 1 spike at 0.100
            for (int i = 0; i < 9; i++)
                buffer.Record(0.016f, i, i * 0.016f);
            buffer.Record(0.100f, 9, 9 * 0.016f);

            var dt = new float[10];
            var fc = new int[10];
            var rt = new float[10];
            int spikes = buffer.DetectSpikes(dt, fc, rt, 10);

            // avg ~ (9*0.016 + 0.100)/10 = 0.0244, threshold = 0.0488
            // Only 0.100 exceeds threshold
            Assert.That(spikes, Is.EqualTo(1));
            Assert.That(dt[0], Is.EqualTo(0.100f).Within(0.001f));
            Assert.That(fc[0], Is.EqualTo(9));
        }

        [Test]
        public void DetectSpikes_NoSpikes_ReturnsZero()
        {
            var buffer = new FrameTimeRingBuffer();
            // All frames have the same delta time — no spikes possible
            for (int i = 0; i < 20; i++)
                buffer.Record(0.016f, i, i * 0.016f);

            var dt = new float[20];
            var fc = new int[20];
            var rt = new float[20];
            int spikes = buffer.DetectSpikes(dt, fc, rt, 20);

            Assert.That(spikes, Is.EqualTo(0));
        }

        [Test]
        public void DetectSpikes_MaxSpikes_RespectsLimit()
        {
            var buffer = new FrameTimeRingBuffer();
            // Keep the median at the baseline: five slow samples out of ten is not a spike population.
            for (int i = 0; i < 15; i++)
                buffer.Record(0.016f, i, i * 0.016f);
            for (int i = 15; i < 20; i++)
                buffer.Record(0.200f, i, i * 0.016f);

            var dt = new float[2];
            var fc = new int[2];
            var rt = new float[2];
            int spikes = buffer.DetectSpikes(dt, fc, rt, 2);

            Assert.That(spikes, Is.EqualTo(2));
        }
    }
}
