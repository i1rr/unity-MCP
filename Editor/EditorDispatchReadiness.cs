using System;
using System.Diagnostics;
using System.Threading;

namespace I1rr.UnityMcp.Editor
{
    /// <summary>
    /// Bounds pre-dispatch waiting independently of EditorApplication.update. Native menus
    /// can suspend that callback while the HTTP listener remains healthy. Only requests
    /// exclusively removed from the inbound queue can be rejected as not started.
    /// </summary>
    internal static class EditorDispatchReadiness
    {
        internal const int DispatchWaitMilliseconds = 2000;
        private static readonly object QueueGate = new object();
        private static long _lastUpdate;
        private static Thread _watchdog;
        private static ManualResetEvent _stop;

        internal static double UpdateAgeMilliseconds => AgeMilliseconds(Interlocked.Read(ref _lastUpdate));
        internal static bool IsResponsive => UpdateAgeMilliseconds < DispatchWaitMilliseconds;

        internal static double AgeMilliseconds(long timestamp) =>
            timestamp == 0 ? double.PositiveInfinity :
            (Stopwatch.GetTimestamp() - timestamp) * 1000.0 / Stopwatch.Frequency;

        internal static void RecordUpdate() => Interlocked.Exchange(ref _lastUpdate, Stopwatch.GetTimestamp());

        internal static void Start()
        {
            RecordUpdate();
            _stop = new ManualResetEvent(false);
            _watchdog = new Thread(Watch) { IsBackground = true, Name = "MCP-DispatchReadiness" };
            _watchdog.Start();
        }

        internal static void Stop()
        {
            _stop?.Set();
            if (_watchdog != null && !_watchdog.Join(1000))
                throw new InvalidOperationException("MCP dispatch watchdog did not stop; refusing to replace its queues.");
            _watchdog = null;
            _stop?.Dispose();
            _stop = null;
        }

        private static void Watch()
        {
            while (!_stop.WaitOne(100)) ExpireQueuedRequests();
        }

        internal static bool TryTake(out McpEditorServer.McpRequest request)
        {
            lock (QueueGate)
            {
                if (!McpEditorServer._inboundQueue.TryDequeue(out request)) return false;
                Interlocked.Decrement(ref McpEditorServer._inboundCount);
                return true;
            }
        }

        internal static bool HasExpired(McpEditorServer.McpRequest request) =>
            AgeMilliseconds(request.EnqueuedAt) >= DispatchWaitMilliseconds;

        internal static void ExpireQueuedRequests()
        {
            while (true)
            {
                McpEditorServer.McpRequest request;
                lock (QueueGate)
                {
                    if (!McpEditorServer._inboundQueue.TryPeek(out request) || !HasExpired(request)) return;
                    McpEditorServer._inboundQueue.TryDequeue(out request);
                    Interlocked.Decrement(ref McpEditorServer._inboundCount);
                }
                EditorRequestPipeline.RejectUndispatchedRequest(request);
            }
        }
    }
}
