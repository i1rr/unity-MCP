using System;
using System.Collections.Generic;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Scripting;

namespace I1rr.UnityMcp.Runtime
{
    [Preserve]
    [McpToolCategory("Editor")]
    public static class RuntimeConsoleTools
    {
        private const int MaxBufferSize = 2000;
        private const int DefaultCount = 50;

        private static readonly List<LogEntry> _logBuffer = new List<LogEntry>(MaxBufferSize);
        private static readonly object _lock = new object();
        private static bool _registered;
        private static long _totalReceived;
        private static string _oldestTimestamp;

        private struct LogEntry
        {
            public string Message;
            public string StackTrace;
            public LogType Type;
            public string Timestamp;
        }

        /// <summary>
        /// Register the log callback at the earliest possible point — before any scene loads.
        /// This ensures we capture logs from scene loading, static constructors, and early init.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        [Preserve]
        private static void RegisterEarly()
        {
            EnsureRegistered();
        }

        [Preserve]
        public static void EnsureRegistered()
        {
            if (_registered) return;
            _registered = true;
            Application.logMessageReceivedThreaded += OnLogReceived;
        }

        /// <summary>
        /// Returns diagnostic info about the log hook for inclusion in ping responses.
        /// </summary>
        public static JObject GetLogStatus()
        {
            lock (_lock)
            {
                return new JObject
                {
                    ["hook_active"] = _registered,
                    ["buffer_count"] = _logBuffer.Count,
                    ["buffer_capacity"] = MaxBufferSize,
                    ["total_received"] = _totalReceived,
                    ["oldest_timestamp"] = _oldestTimestamp
                };
            }
        }

        private static void OnLogReceived(string message, string stackTrace, LogType type)
        {
            var timestamp = DateTime.UtcNow.ToString("HH:mm:ss.fff");
            var entry = new LogEntry
            {
                Message = message,
                StackTrace = stackTrace,
                Type = type,
                Timestamp = timestamp
            };

            lock (_lock)
            {
                _totalReceived++;
                _logBuffer.Add(entry);
                if (_logBuffer.Count > MaxBufferSize)
                    _logBuffer.RemoveRange(0, _logBuffer.Count - MaxBufferSize);
                if (_logBuffer.Count > 0)
                    _oldestTimestamp = _logBuffer[0].Timestamp;
            }
        }

        #region runtime_get_logs

        [McpTool("runtime_get_logs",
            "Get captured runtime log messages.",
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""count"": { ""type"": ""integer"", ""description"": ""Number of recent logs to return (default 50, max 500)"", ""default"": 50 },
                    ""type_filter"": { ""type"": ""string"", ""description"": ""Filter by type: 'error', 'warning', 'log', 'exception', 'assert'"" }
                }
            }")]
        [Preserve]
        public static JObject GetLogs(JObject parameters)
        {
            EnsureRegistered();

            int count = Math.Clamp(parameters["count"]?.Value<int>() ?? DefaultCount, 1, 500);
            var typeFilter = parameters["type_filter"]?.Value<string>();

            LogType? filterType = null;
            if (!string.IsNullOrEmpty(typeFilter))
            {
                filterType = typeFilter.ToLowerInvariant() switch
                {
                    "error" => LogType.Error,
                    "warning" => LogType.Warning,
                    "log" => LogType.Log,
                    "exception" => LogType.Exception,
                    "assert" => LogType.Assert,
                    _ => null
                };
            }

            var results = new JArray();
            lock (_lock)
            {
                int startIdx = Math.Max(0, _logBuffer.Count - count * 2); // over-fetch to handle filtering
                for (int i = _logBuffer.Count - 1; i >= startIdx && results.Count < count; i--)
                {
                    var entry = _logBuffer[i];
                    if (filterType.HasValue && entry.Type != filterType.Value) continue;

                    results.Add(new JObject
                    {
                        ["message"] = TruncateMessage(entry.Message, 500),
                        ["type"] = entry.Type.ToString(),
                        ["timestamp"] = entry.Timestamp,
                        ["stackTrace"] = !string.IsNullOrEmpty(entry.StackTrace) ? TruncateMessage(entry.StackTrace, 300) : null
                    });
                }
            }

            long totalReceived;
            string oldestTimestamp;
            lock (_lock)
            {
                totalReceived = _totalReceived;
                oldestTimestamp = _oldestTimestamp;
            }

            return new JObject
            {
                ["success"] = true,
                ["count"] = results.Count,
                ["buffer_size"] = _logBuffer.Count,
                ["buffer_capacity"] = MaxBufferSize,
                ["total_received"] = totalReceived,
                ["oldest_timestamp"] = oldestTimestamp,
                ["logs"] = results
            };
        }

        #endregion

        #region runtime_clear_logs

        [McpTool("runtime_clear_logs",
            "Clear the runtime log buffer.",
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {}
            }")]
        [Preserve]
        public static JObject ClearLogs(JObject parameters)
        {
            int cleared;
            lock (_lock)
            {
                cleared = _logBuffer.Count;
                _logBuffer.Clear();
                _oldestTimestamp = null;
            }

            return new JObject
            {
                ["success"] = true,
                ["cleared_count"] = cleared
            };
        }

        #endregion

        #region ping

        [McpTool("ping",
            "Health check — returns server status, Unity version, runtime log diagnostics, and API version.")]
        [Preserve]
        public static JObject Ping(JObject parameters)
        {
            return new JObject
            {
                ["status"] = "ok",
                ["server"] = "i1rr-unity-mcp",
                ["version"] = McpProtocol.ServerVersion,
                ["api_version"] = 2,
                ["unity_version"] = Application.unityVersion,
                ["platform"] = Application.platform.ToString(),
                ["is_playing"] = Application.isPlaying,
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["runtime_log_status"] = GetLogStatus()
            };
        }

        #endregion

        private static string TruncateMessage(string msg, int maxLength)
        {
            if (string.IsNullOrEmpty(msg)) return msg;
            return msg.Length <= maxLength ? msg : msg.Substring(0, maxLength) + "...";
        }
    }
}
