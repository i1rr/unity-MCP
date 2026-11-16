using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace I1rr.UnityMcp.Shared
{
    /// <summary>
    /// Shared-layer inbound request envelope used by the Runtime server's main-thread dispatcher.
    /// </summary>
    public struct McpRequest
    {
        public object Context;
        public byte[] RequestBody;
        public string HttpMethod;
        public string Path;
        public Dictionary<string, string> Headers;
    }

    /// <summary>
    /// Shared-layer outbound response envelope carrying serialized bytes and status code.
    /// </summary>
    public struct CompletedRequest
    {
        public object Context;
        public byte[] ResponseBytes;
        public int StatusCode;
        public Dictionary<string, string> ResponseHeaders;
    }

    /// <summary>
    /// Base class for main-thread request dispatchers. Provides a bounded concurrent queue
    /// with TryEnqueue/Drain semantics. Subclasses (Editor and Runtime) drive draining from
    /// their respective update loops.
    /// </summary>
    public abstract class MainThreadDispatcher
    {
        protected readonly ConcurrentQueue<McpRequest> _inboundQueue = new ConcurrentQueue<McpRequest>();

        protected const int MaxQueueSize = 200;

        private int _pendingCount;

        public int PendingCount => Volatile.Read(ref _pendingCount);

        public bool TryEnqueue(McpRequest request)
        {
            if (Volatile.Read(ref _pendingCount) >= MaxQueueSize)
                return false;

            _inboundQueue.Enqueue(request);
            Interlocked.Increment(ref _pendingCount);
            return true;
        }

        protected int Drain(int max, Action<McpRequest> handler)
        {
            int drained = 0;
            while (drained < max && _inboundQueue.TryDequeue(out McpRequest request))
            {
                Interlocked.Decrement(ref _pendingCount);
                drained++;
                handler(request);
            }
            return drained;
        }
    }
}
