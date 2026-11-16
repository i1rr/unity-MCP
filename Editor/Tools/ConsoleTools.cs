using System;
using System.Collections.Generic;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace I1rr.UnityMcp.Editor
{
    [McpToolCategory("Editor")]
    public static class ConsoleTools
    {
        private const int MaxBufferSize = 10000;
        private const string McpPrefix = "[MCP]";

        private static readonly object Lock = new();
        private static readonly Queue<LogEntry> Buffer = new();
        private static long _nextId = 1;

        private struct LogEntry
        {
            public long Id;
            public string Message;
            public string StackTrace;
            public LogType Type;
            public long Timestamp;
            public string Source;
        }

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            Application.logMessageReceivedThreaded -= OnLogMessageReceived;
            Application.logMessageReceivedThreaded += OnLogMessageReceived;
        }

        private static void OnLogMessageReceived(string message, string stackTrace, LogType type)
        {
            var source = message != null && message.StartsWith(McpPrefix) ? "mcp" : "game";
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            lock (Lock)
            {
                if (Buffer.Count >= MaxBufferSize)
                {
                    Buffer.Dequeue();
                }

                Buffer.Enqueue(new LogEntry
                {
                    Id = _nextId++,
                    Message = message,
                    StackTrace = stackTrace,
                    Type = type,
                    Timestamp = timestamp,
                    Source = source
                });
            }
        }

        [McpTool("get_console_logs",
            "Get console log entries (Log/Warning/Error). Supports incremental reads via cursor.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""count"": { ""type"": ""integer"", ""description"": ""Max entries to return (default 50, max 500)"", ""default"": 50 },
                    ""types"": {
                        ""type"": ""array"",
                        ""items"": { ""type"": ""string"", ""enum"": [""Log"", ""Warning"", ""Error"", ""Assert"", ""Exception""] },
                        ""description"": ""Filter by log types. If omitted, returns all types.""
                    },
                    ""since_cursor"": { ""type"": ""integer"", ""description"": ""Return only entries with ID greater than this cursor value"" }
                }
            }")]
        public static JObject GetConsoleLogs(JObject parameters)
        {
            var count = Math.Clamp(ToolHelper.OptionalParam(parameters, "count", 50), 1, 500);

            var sinceCursor = ToolHelper.OptionalParam(parameters, "since_cursor", 0L);

            HashSet<LogType> typeFilter = null;
            var typesParam = parameters["types"] as JArray;
            if (typesParam is { Count: > 0 })
            {
                typeFilter = new HashSet<LogType>();
                foreach (var t in typesParam)
                {
                    if (Enum.TryParse<LogType>(t.Value<string>(), true, out var parsed))
                    {
                        typeFilter.Add(parsed);
                    }
                }
            }

            // Copy entries under lock, serialize outside
            List<LogEntry> snapshot;
            long oldestId;
            long newestId;

            lock (Lock)
            {
                if (Buffer.Count == 0)
                {
                    return new JObject
                    {
                        ["entries"] = new JArray(),
                        ["cursor"] = 0,
                        ["total_buffered"] = 0
                    };
                }

                oldestId = Buffer.Peek().Id;
                newestId = _nextId - 1;

                if (sinceCursor > 0 && sinceCursor < oldestId)
                {
                    return new JObject
                    {
                        ["cursor_expired"] = true,
                        ["oldest_available_cursor"] = oldestId,
                        ["newest_cursor"] = newestId,
                        ["total_buffered"] = Buffer.Count
                    };
                }

                snapshot = new List<LogEntry>(Math.Min(count, Buffer.Count));
                foreach (var entry in Buffer)
                {
                    if (entry.Id <= sinceCursor)
                        continue;

                    if (typeFilter != null && !typeFilter.Contains(entry.Type))
                        continue;

                    snapshot.Add(entry);
                    if (snapshot.Count >= count)
                        break;
                }
            }

            // Serialize outside lock
            var entries = new JArray();
            long lastCursor = sinceCursor;

            foreach (var entry in snapshot)
            {
                entries.Add(new JObject
                {
                    ["id"] = entry.Id,
                    ["message"] = entry.Message,
                    ["stackTrace"] = entry.StackTrace,
                    ["type"] = entry.Type.ToString(),
                    ["timestamp"] = entry.Timestamp,
                    ["source"] = entry.Source
                });
                lastCursor = entry.Id;
            }

            return new JObject
            {
                ["entries"] = entries,
                ["cursor"] = lastCursor,
                ["total_buffered"] = Buffer.Count,
                ["has_more"] = newestId > lastCursor
            };
        }

        [McpTool("clear_console", "Clear the Unity console and the internal log buffer")]
        public static JObject ClearConsole(JObject parameters)
        {
            lock (Lock)
            {
                Buffer.Clear();
            }

            // Clear Unity's console via internal log entries
            var assembly = typeof(UnityEditor.Editor).Assembly;
            var logEntries = assembly.GetType("UnityEditor.LogEntries");
            var clearMethod = logEntries?.GetMethod("Clear",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            clearMethod?.Invoke(null, null);

            return new JObject
            {
                ["status"] = "cleared"
            };
        }

        [McpTool("wait_for_compilation",
            "Non-blocking compilation status check. Returns immediately with compiling state and any errors.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""timeout_ms"": { ""type"": ""integer"", ""description"": ""Unused — kept for API compatibility. This call always returns immediately."" }
                }
            }")]
        public static JObject WaitForCompilation(JObject parameters)
        {
            var isCompiling = EditorApplication.isCompiling;
            var errors = new JArray();

            if (!isCompiling)
            {
                // Collect errors from our log buffer (most reliable across Unity versions)
                lock (Lock)
                {
                    foreach (var entry in Buffer)
                    {
                        if (entry.Type is LogType.Error or LogType.Exception)
                        {
                            errors.Add(new JObject
                            {
                                ["message"] = entry.Message,
                                ["stackTrace"] = entry.StackTrace,
                                ["type"] = entry.Type.ToString(),
                                ["timestamp"] = entry.Timestamp
                            });
                        }
                    }
                }

                // Also try to extract compiler errors from log buffer
                try
                {
                    // CompilationPipeline.GetCompilerMessages is not available in all Unity versions
                    // Fall back to checking console log entries for compiler errors
                    lock (Lock)
                    {
                        foreach (var entry in Buffer)
                        {
                            if (entry.Type == LogType.Error && entry.Message.Contains(") error CS"))
                            {
                                errors.Add(new JObject
                                {
                                    ["message"] = entry.Message,
                                    ["file"] = "",
                                    ["line"] = 0,
                                    ["column"] = 0,
                                    ["compiler_error"] = true
                                });
                            }
                        }
                    }
                }
                catch
                {
                    // GetCompilerMessages may not be available in all Unity versions — fall through
                }
            }

            return new JObject
            {
                ["compiling"] = isCompiling,
                ["errors"] = errors,
                ["error_count"] = errors.Count
            };
        }

        /// <summary>
        /// Returns the current newest cursor value (useful for other tools that want to snapshot position).
        /// </summary>
        internal static long GetCurrentCursor()
        {
            lock (Lock)
            {
                return _nextId - 1;
            }
        }
    }
}
