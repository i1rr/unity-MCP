using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace I1rr.UnityMcp.Shared
{
    /// <summary>
    /// Log level for structured MCP server logging.
    /// </summary>
    public enum McpLogLevel
    {
        Error = 0,
        Warning = 1,
        Info = 2,
        Debug = 3
    }

    /// <summary>
    /// Structured logger that writes JSON-line log entries to a ring file and optionally
    /// forwards to Unity's Debug.Log. Thread-safe.
    /// </summary>
    public static class StructuredLogger
    {
        /// <summary>
        /// Minimum log level. Messages below this level are discarded.
        /// Default: Info.
        /// </summary>
        public static McpLogLevel MinLevel { get; set; } = McpLogLevel.Info;

        /// <summary>
        /// When true, log entries are also forwarded to Unity's Debug.Log/LogWarning/LogError.
        /// Default: true.
        /// </summary>
        public static bool ForwardToUnityConsole { get; set; } = true;

        /// <summary>
        /// Maximum log file size in bytes. When exceeded, the oldest half is truncated.
        /// Default: 10 MB.
        /// </summary>
        public static long MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024;

        private static string _logFilePath;
        private static readonly object _writeLock = new object();
        private static readonly object _readLock = new object();
        private static bool _initialized;

        /// <summary>
        /// Initializes the logger with a file path. Call once at server startup.
        /// </summary>
        public static void Initialize(string logFilePath)
        {
            lock (_writeLock)
            {
                _logFilePath = logFilePath;
                _initialized = !string.IsNullOrEmpty(_logFilePath);
            }
        }

        /// <summary>
        /// Shuts down the logger, flushing any pending writes.
        /// </summary>
        public static void Shutdown()
        {
            lock (_writeLock)
            {
                _initialized = false;
                _logFilePath = null;
            }
        }

        /// <summary>
        /// Logs a structured entry for a tool invocation.
        /// </summary>
        public static void LogToolInvocation(string requestId, string toolName, double durationMs,
            bool success, string errorCode = null, string errorMessage = null, int? resultBytes = null, string status = null)
        {
            if (success && MinLevel < McpLogLevel.Info) return;

            var entry = new JObject
            {
                ["timestamp"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["level"] = "INFO",
                ["request_id"] = requestId,
                ["tool_name"] = toolName,
                ["duration_ms"] = Math.Round(durationMs, 2),
                ["status"] = status ?? (success ? "success" : "error")
            };
            if (resultBytes.HasValue) entry["result_bytes"] = resultBytes.Value;

            if (!success)
            {
                if (errorCode != null) entry["error_code"] = errorCode;
                if (errorMessage != null) entry["error_message"] = errorMessage;
                entry["level"] = "ERROR";
            }

            WriteEntry(entry);
        }

        /// <summary>
        /// Logs a message at the specified level.
        /// </summary>
        public static void Log(McpLogLevel level, string message, string requestId = null,
            string component = null)
        {
            if (level > MinLevel) return;

            var entry = new JObject
            {
                ["timestamp"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["level"] = level.ToString().ToUpperInvariant(),
                ["message"] = message
            };

            if (requestId != null) entry["request_id"] = requestId;
            if (component != null) entry["component"] = component;

            WriteEntry(entry);

            if (ForwardToUnityConsole)
            {
                var logMsg = $"[MCP] {message}";
                switch (level)
                {
                    case McpLogLevel.Error:
                        Debug.LogError(logMsg);
                        break;
                    case McpLogLevel.Warning:
                        Debug.LogWarning(logMsg);
                        break;
                    default:
                        Debug.Log(logMsg);
                        break;
                }
            }
        }

        /// <summary>
        /// Reads the most recent log entries (up to <paramref name="maxLines"/>).
        /// Optionally filters by level and/or tool name.
        /// </summary>
        public static List<JObject> ReadRecentEntries(int maxLines = 100,
            McpLogLevel? minLevel = null, string toolNameFilter = null)
        {
            var results = new List<JObject>();

            lock (_readLock)
            {
                if (!_initialized || _logFilePath == null || !File.Exists(_logFilePath))
                    return results;

                try
                {
                    string[] lines;
                    using (var fs = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(fs, Encoding.UTF8))
                    {
                        lines = reader.ReadToEnd().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    }

                    // Read backwards to get most recent first
                    int count = 0;
                    for (int i = lines.Length - 1; i >= 0 && count < maxLines; i--)
                    {
                        var line = lines[i].Trim();
                        if (string.IsNullOrEmpty(line)) continue;

                        JObject entry;
                        try { entry = JObject.Parse(line); }
                        catch { continue; }

                        // Apply level filter
                        if (minLevel.HasValue)
                        {
                            var entryLevel = ParseLevel(entry["level"]?.Value<string>());
                            if (entryLevel > minLevel.Value) continue;
                        }

                        // Apply tool name filter
                        if (toolNameFilter != null)
                        {
                            var entryTool = entry["tool_name"]?.Value<string>();
                            if (entryTool == null || !entryTool.Contains(toolNameFilter))
                                continue;
                        }

                        results.Add(entry);
                        count++;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[MCP] Failed to read log file: {ex.Message}");
                }
            }

            return results;
        }

        private static void WriteEntry(JObject entry)
        {
            if (!_initialized) return;

            var line = entry.ToString(Newtonsoft.Json.Formatting.None) + "\n";

            lock (_writeLock)
            {
                if (!_initialized || _logFilePath == null) return;

                try
                {
                    // Check size and rotate if needed
                    if (File.Exists(_logFilePath))
                    {
                        var fi = new FileInfo(_logFilePath);
                        if (fi.Length > MaxFileSizeBytes)
                            RotateLog();
                    }

                    File.AppendAllText(_logFilePath, line, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    // Don't recurse — just log to Unity
                    Debug.LogWarning($"[MCP] Failed to write structured log: {ex.Message}");
                }
            }
        }

        private static void RotateLog()
        {
            try
            {
                var lines = File.ReadAllLines(_logFilePath, Encoding.UTF8);
                // Keep the second half
                int keepFrom = lines.Length / 2;
                var kept = new StringBuilder();
                for (int i = keepFrom; i < lines.Length; i++)
                {
                    kept.AppendLine(lines[i]);
                }
                File.WriteAllText(_logFilePath, kept.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MCP] Log rotation failed: {ex.Message}");
                // Last resort: truncate
                try { File.WriteAllText(_logFilePath, "", Encoding.UTF8); }
                catch { /* give up */ }
            }
        }

        private static McpLogLevel ParseLevel(string level)
        {
            if (string.IsNullOrEmpty(level)) return McpLogLevel.Info;
            switch (level.ToUpperInvariant())
            {
                case "ERROR": return McpLogLevel.Error;
                case "WARNING": return McpLogLevel.Warning;
                case "INFO": return McpLogLevel.Info;
                case "DEBUG": return McpLogLevel.Debug;
                default: return McpLogLevel.Info;
            }
        }
    }
}
