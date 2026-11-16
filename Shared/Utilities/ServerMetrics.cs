using System;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace I1rr.UnityMcp.Shared
{
    /// <summary>
    /// Tracks MCP server metrics: request rates, error rates, response times,
    /// active connections, and deferred responses pending.
    /// Thread-safe. Counters reset on server restart.
    /// </summary>
    public static class ServerMetrics
    {
        private static long _totalRequests;
        private static long _totalErrors;
        private static long _totalResponseTimeMs;
        private static int _activeConnections;
        private static readonly object _lock = new object();
        private static DateTime _startTimeUtc;
        private static string _exportFilePath;

        /// <summary>
        /// Delegate that returns the current number of pending deferred responses.
        /// Set by the hosting server at startup.
        /// </summary>
        public static Func<int> GetDeferredPendingCount { get; set; }

        /// <summary>
        /// Initializes metrics tracking. Call once at server startup.
        /// </summary>
        public static void Initialize(string exportFilePath = null)
        {
            _totalRequests = 0;
            _totalErrors = 0;
            _totalResponseTimeMs = 0;
            _activeConnections = 0;
            _startTimeUtc = DateTime.UtcNow;
            _exportFilePath = exportFilePath;
        }

        /// <summary>
        /// Records a completed request.
        /// </summary>
        public static void RecordRequest(double responseTimeMs, bool isError)
        {
            Interlocked.Increment(ref _totalRequests);
            Interlocked.Add(ref _totalResponseTimeMs, (long)responseTimeMs);

            if (isError)
                Interlocked.Increment(ref _totalErrors);
        }

        /// <summary>
        /// Increments the active connection count.
        /// </summary>
        public static void IncrementConnections()
        {
            Interlocked.Increment(ref _activeConnections);
        }

        /// <summary>
        /// Decrements the active connection count.
        /// </summary>
        public static void DecrementConnections()
        {
            Interlocked.Decrement(ref _activeConnections);
        }

        /// <summary>
        /// Returns the current metrics as a JObject.
        /// </summary>
        public static JObject GetMetrics()
        {
            long requests = Interlocked.Read(ref _totalRequests);
            long errors = Interlocked.Read(ref _totalErrors);
            long totalMs = Interlocked.Read(ref _totalResponseTimeMs);
            int connections = _activeConnections;
            int deferred = GetDeferredPendingCount?.Invoke() ?? 0;

            double uptimeSeconds = (DateTime.UtcNow - _startTimeUtc).TotalSeconds;
            double requestsPerSec = uptimeSeconds > 0 ? requests / uptimeSeconds : 0;
            double errorsPerSec = uptimeSeconds > 0 ? errors / uptimeSeconds : 0;
            double avgResponseMs = requests > 0 ? (double)totalMs / requests : 0;

            return new JObject
            {
                ["total_requests"] = requests,
                ["total_errors"] = errors,
                ["requests_per_sec"] = Math.Round(requestsPerSec, 2),
                ["errors_per_sec"] = Math.Round(errorsPerSec, 2),
                ["avg_response_time_ms"] = Math.Round(avgResponseMs, 2),
                ["active_connections"] = connections,
                ["deferred_pending"] = deferred,
                ["uptime_seconds"] = Math.Round(uptimeSeconds, 1),
                ["started_at_utc"] = _startTimeUtc.ToString("O")
            };
        }

        /// <summary>
        /// Exports current metrics to the configured file path, if set.
        /// </summary>
        public static void ExportToFile()
        {
            if (string.IsNullOrEmpty(_exportFilePath)) return;

            try
            {
                var metrics = GetMetrics();
                File.WriteAllText(_exportFilePath, metrics.ToString(Newtonsoft.Json.Formatting.Indented), Encoding.UTF8);
            }
            catch
            {
                // Best effort — don't fail requests over metrics export
            }
        }
    }
}
