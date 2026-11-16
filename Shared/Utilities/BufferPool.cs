using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Text;

namespace I1rr.UnityMcp.Shared
{
    /// <summary>
    /// Provides pooled byte[] buffers via ArrayPool and pooled StringBuilder instances
    /// to reduce GC pressure on hot I/O paths.
    /// </summary>
    public static class BufferPool
    {
        private const int MaxStringBuilderCapacity = 16384;
        private const int StringBuilderPoolSize = 8;

        private static readonly ConcurrentQueue<StringBuilder> _stringBuilderPool = new ConcurrentQueue<StringBuilder>();

        /// <summary>
        /// Rents a byte[] buffer of at least the specified size from the shared ArrayPool.
        /// The returned buffer may be larger than requested.
        /// Caller MUST return it via <see cref="ReturnBuffer"/>.
        /// </summary>
        public static byte[] RentBuffer(int minimumSize)
        {
            return ArrayPool<byte>.Shared.Rent(minimumSize);
        }

        /// <summary>
        /// Returns a rented buffer to the shared ArrayPool.
        /// </summary>
        public static void ReturnBuffer(byte[] buffer)
        {
            if (buffer != null)
                ArrayPool<byte>.Shared.Return(buffer);
        }

        /// <summary>
        /// Rents a StringBuilder from the pool, or creates a new one if the pool is empty.
        /// Caller MUST return it via <see cref="ReturnStringBuilder"/>.
        /// </summary>
        public static StringBuilder RentStringBuilder(int initialCapacity = 256)
        {
            if (_stringBuilderPool.TryDequeue(out var sb))
            {
                sb.Clear();
                if (sb.Capacity < initialCapacity)
                    sb.Capacity = initialCapacity;
                return sb;
            }
            return new StringBuilder(initialCapacity);
        }

        /// <summary>
        /// Returns a StringBuilder to the pool. Builders exceeding MaxStringBuilderCapacity
        /// are discarded to prevent holding large buffers indefinitely.
        /// </summary>
        public static void ReturnStringBuilder(StringBuilder sb)
        {
            if (sb == null) return;

            // Don't pool oversized builders
            if (sb.Capacity > MaxStringBuilderCapacity)
                return;

            // Don't exceed pool size
            if (_stringBuilderPool.Count >= StringBuilderPoolSize)
                return;

            sb.Clear();
            _stringBuilderPool.Enqueue(sb);
        }
    }
}
