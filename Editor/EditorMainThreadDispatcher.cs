using System;
using I1rr.UnityMcp.Shared;
using UnityEngine;

namespace I1rr.UnityMcp.Editor
{
    /// <summary>
    /// Editor-specific main thread dispatcher. Singleton that drains inbound MCP requests
    /// on EditorApplication.update and records per-frame timing data into a ring buffer.
    /// </summary>
    public sealed class EditorMainThreadDispatcher : MainThreadDispatcher
    {
        private static EditorMainThreadDispatcher _instance;

        private readonly FrameTimeRingBuffer _frameTimeBuffer = new FrameTimeRingBuffer();

        public static EditorMainThreadDispatcher Instance
        {
            get
            {
                if (_instance == null)
                    _instance = new EditorMainThreadDispatcher();
                return _instance;
            }
        }

        /// <summary>
        /// Persistent ring buffer recording per-frame timing data.
        /// </summary>
        public FrameTimeRingBuffer FrameTimeBuffer => _frameTimeBuffer;

        private EditorMainThreadDispatcher() { }

        /// <summary>
        /// Called from McpEditorServer's EditorApplication.update callback.
        /// Drains up to maxPerTick requests and invokes the handler for each.
        /// Also records frame timing data into the ring buffer.
        /// </summary>
        public int DrainQueue(Action<McpRequest> handler, int maxPerTick = 20)
        {
            _frameTimeBuffer.Record(Time.unscaledDeltaTime, Time.frameCount, Time.realtimeSinceStartup);
            return Drain(maxPerTick, handler);
        }
    }
}
