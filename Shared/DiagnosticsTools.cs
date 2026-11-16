using System;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace I1rr.UnityMcp.Shared
{
    /// <summary>
    /// MCP diagnostic tools shared between Editor and Runtime servers.
    /// Reports server state, tool registry status, profiler counters, and uptime.
    /// </summary>
    [McpToolCategory("Utility")]
    public static class DiagnosticsTools
    {
        private static float _startTime;

        /// <summary>
        /// Must be called once by the hosting server (Editor or Runtime) at startup
        /// to record the server start time for uptime calculation.
        /// </summary>
        public static void RecordStartTime()
        {
            _startTime = Time.realtimeSinceStartup;
        }

        [McpTool("diagnostics",
            "Reports MCP server system state: server type, play mode, profiler recorders, loaded tools, uptime, and scanned assemblies.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {}
            }")]
        public static JObject GetDiagnostics(JObject parameters)
        {
            bool isEditor = Application.isEditor;
            bool playMode = McpToolRegistry.IsInPlayMode?.Invoke() ?? false;
            var profilerActive = ProfilerRecorderService.Provider != null;
            float uptime = Time.realtimeSinceStartup - _startTime;

            var scannedAssemblies = new JArray();
            foreach (var asm in McpToolRegistry.ScannedAssemblies)
                scannedAssemblies.Add(asm);

            return new JObject
            {
                ["server_type"] = isEditor ? "editor" : "runtime",
                ["play_mode"] = playMode,
                ["profiler_provider_active"] = profilerActive,
                ["verbose_logging"] = McpToolRegistry.VerboseLogging,
                ["loaded_tool_count"] = McpToolRegistry.ToolCount,
                ["tool_scan_time_seconds"] = McpToolRegistry.InitTimeSeconds,
                ["uptime_seconds"] = Math.Round(uptime, 1),
                ["scanned_assemblies"] = scannedAssemblies
            };
        }

        [McpTool("diagnostics_tool_performance",
            "Reports per-tool execution performance: timing histograms (avg/p50/p95/p99), GC allocation tracking, and slow-tool warnings. " +
            "Profiling must be enabled first via the 'enable' action. Supports filtering by tool name and resetting collected data.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""action"": {
                        ""type"": ""string"",
                        ""enum"": [""report"", ""enable"", ""disable"", ""reset"", ""configure""],
                        ""description"": ""Action to perform. 'report' returns current metrics (default), 'enable'/'disable' toggles profiling, 'reset' clears data, 'configure' sets threshold.""
                    },
                    ""tool_name"": {
                        ""type"": ""string"",
                        ""description"": ""Optional: filter report to a specific tool name.""
                    },
                    ""slow_threshold_ms"": {
                        ""type"": ""number"",
                        ""description"": ""For 'configure' action: set the slow-tool warning threshold in milliseconds (default 100).""
                    }
                }
            }")]
        public static JObject GetToolPerformance(JObject parameters)
        {
            var action = parameters?["action"]?.Value<string>() ?? "report";

            switch (action)
            {
                case "enable":
                    ToolExecutionProfiler.Enabled = true;
                    return new JObject
                    {
                        ["result"] = "ok",
                        ["profiling_enabled"] = true,
                        ["slow_threshold_ms"] = ToolExecutionProfiler.SlowToolThresholdMs
                    };

                case "disable":
                    ToolExecutionProfiler.Enabled = false;
                    return new JObject
                    {
                        ["result"] = "ok",
                        ["profiling_enabled"] = false
                    };

                case "reset":
                    ToolExecutionProfiler.Reset();
                    return new JObject
                    {
                        ["result"] = "ok",
                        ["message"] = "Profiling data cleared."
                    };

                case "configure":
                    var threshold = parameters?["slow_threshold_ms"]?.Value<double>();
                    if (threshold.HasValue && threshold.Value > 0)
                        ToolExecutionProfiler.SlowToolThresholdMs = threshold.Value;
                    return new JObject
                    {
                        ["result"] = "ok",
                        ["slow_threshold_ms"] = ToolExecutionProfiler.SlowToolThresholdMs
                    };

                case "report":
                default:
                    var filterTool = parameters?["tool_name"]?.Value<string>();
                    return ToolExecutionProfiler.GetPerformanceReport(filterTool);
            }
        }

        [McpTool("get_server_logs",
            "Retrieves recent structured server log entries. Supports filtering by log level and tool name. " +
            "Returns entries in reverse chronological order (most recent first).",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""max_lines"": {
                        ""type"": ""integer"",
                        ""description"": ""Maximum number of log entries to return. Default 50, max 500.""
                    },
                    ""level"": {
                        ""type"": ""string"",
                        ""enum"": [""Error"", ""Warning"", ""Info"", ""Debug""],
                        ""description"": ""Minimum log level filter. Only entries at this level or more severe are returned. Default 'Info'.""
                    },
                    ""tool_name"": {
                        ""type"": ""string"",
                        ""description"": ""Filter entries to those matching this tool name (substring match).""
                    }
                }
            }")]
        public static JObject GetServerLogs(JObject parameters)
        {
            int maxLines = parameters?["max_lines"]?.Value<int>() ?? 50;
            if (maxLines < 1) maxLines = 1;
            if (maxLines > 500) maxLines = 500;

            var levelStr = parameters?["level"]?.Value<string>();
            McpLogLevel? minLevel = null;
            if (!string.IsNullOrEmpty(levelStr))
            {
                switch (levelStr.ToUpperInvariant())
                {
                    case "ERROR": minLevel = McpLogLevel.Error; break;
                    case "WARNING": minLevel = McpLogLevel.Warning; break;
                    case "INFO": minLevel = McpLogLevel.Info; break;
                    case "DEBUG": minLevel = McpLogLevel.Debug; break;
                }
            }

            var toolFilter = parameters?["tool_name"]?.Value<string>();

            var entries = StructuredLogger.ReadRecentEntries(maxLines, minLevel, toolFilter);

            var entriesArray = new JArray();
            foreach (var entry in entries)
                entriesArray.Add(entry);

            return new JObject
            {
                ["entries"] = entriesArray,
                ["count"] = entries.Count,
                ["max_lines"] = maxLines
            };
        }

        [McpTool("get_server_metrics",
            "Returns server performance metrics: request rates, error rates, average response time, " +
            "active connections, and deferred responses pending. Optionally exports metrics to a file.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""export"": {
                        ""type"": ""boolean"",
                        ""description"": ""When true, also exports metrics to the configured file path. Default false.""
                    }
                }
            }")]
        public static JObject GetServerMetrics(JObject parameters)
        {
            var metrics = ServerMetrics.GetMetrics();

            bool export = parameters?["export"]?.Value<bool>() ?? false;
            if (export)
                ServerMetrics.ExportToFile();

            return metrics;
        }
    }
}
