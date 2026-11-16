using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace I1rr.UnityMcp.Shared
{
    /// <summary>
    /// Opt-in per-tool execution profiler that tracks timing histograms and GC allocation.
    /// Thread-safe. Exposed via the diagnostics_tool_performance tool.
    /// </summary>
    public static class ToolExecutionProfiler
    {
        /// <summary>
        /// When true, per-tool timing and allocation data is collected.
        /// </summary>
        public static bool Enabled { get; set; }

        /// <summary>
        /// Threshold in milliseconds above which a tool execution is considered slow.
        /// Default 100ms.
        /// </summary>
        public static double SlowToolThresholdMs { get; set; } = 100.0;

        private sealed class ToolMetrics
        {
            public readonly List<double> ExecutionTimesMs = new List<double>();
            public readonly List<long> AllocationsBytes = new List<long>();
            public int TotalInvocations;
            public int SlowInvocations;
        }

        private static readonly Dictionary<string, ToolMetrics> _metrics =
            new Dictionary<string, ToolMetrics>(StringComparer.Ordinal);
        private static readonly object _lock = new object();

        private const int MaxSamplesPerTool = 1000;

        /// <summary>
        /// Records a tool execution measurement.
        /// Called from McpToolRegistry after each tool invocation when profiling is enabled.
        /// </summary>
        public static void RecordExecution(string toolName, double elapsedMs, long allocatedBytes)
        {
            if (!Enabled) return;

            lock (_lock)
            {
                if (!_metrics.TryGetValue(toolName, out var metrics))
                {
                    metrics = new ToolMetrics();
                    _metrics[toolName] = metrics;
                }

                metrics.TotalInvocations++;

                if (elapsedMs >= SlowToolThresholdMs)
                    metrics.SlowInvocations++;

                // Ring-buffer behavior: drop oldest when at capacity
                if (metrics.ExecutionTimesMs.Count >= MaxSamplesPerTool)
                {
                    metrics.ExecutionTimesMs.RemoveAt(0);
                    metrics.AllocationsBytes.RemoveAt(0);
                }

                metrics.ExecutionTimesMs.Add(elapsedMs);
                metrics.AllocationsBytes.Add(allocatedBytes);
            }
        }

        /// <summary>
        /// Gets performance data for all profiled tools as a JObject.
        /// </summary>
        public static JObject GetPerformanceReport(string filterTool = null)
        {
            lock (_lock)
            {
                var result = new JObject();
                var toolsArray = new JArray();

                var entries = filterTool != null
                    ? _metrics.Where(kv => string.Equals(kv.Key, filterTool, StringComparison.OrdinalIgnoreCase))
                    : _metrics.AsEnumerable();

                foreach (var kv in entries.OrderByDescending(x => x.Value.TotalInvocations))
                {
                    var metrics = kv.Value;
                    var times = metrics.ExecutionTimesMs;
                    var allocs = metrics.AllocationsBytes;

                    var toolReport = new JObject
                    {
                        ["tool_name"] = kv.Key,
                        ["total_invocations"] = metrics.TotalInvocations,
                        ["slow_invocations"] = metrics.SlowInvocations,
                        ["samples"] = times.Count,
                        ["timing_ms"] = ComputeHistogram(times),
                        ["allocations_bytes"] = ComputeAllocationStats(allocs)
                    };

                    toolsArray.Add(toolReport);
                }

                result["enabled"] = Enabled;
                result["slow_threshold_ms"] = SlowToolThresholdMs;
                result["profiled_tools"] = toolsArray.Count;
                result["tools"] = toolsArray;
                return result;
            }
        }

        /// <summary>
        /// Clears all collected profiling data.
        /// </summary>
        public static void Reset()
        {
            lock (_lock)
            {
                _metrics.Clear();
            }
        }

        private static JObject ComputeHistogram(List<double> values)
        {
            if (values.Count == 0)
                return new JObject { ["avg"] = 0, ["p50"] = 0, ["p95"] = 0, ["p99"] = 0, ["min"] = 0, ["max"] = 0 };

            var sorted = values.OrderBy(x => x).ToList();
            double avg = sorted.Average();
            double p50 = Percentile(sorted, 0.50);
            double p95 = Percentile(sorted, 0.95);
            double p99 = Percentile(sorted, 0.99);

            return new JObject
            {
                ["avg"] = Math.Round(avg, 2),
                ["p50"] = Math.Round(p50, 2),
                ["p95"] = Math.Round(p95, 2),
                ["p99"] = Math.Round(p99, 2),
                ["min"] = Math.Round(sorted[0], 2),
                ["max"] = Math.Round(sorted[sorted.Count - 1], 2)
            };
        }

        private static JObject ComputeAllocationStats(List<long> values)
        {
            if (values.Count == 0)
                return new JObject { ["avg_bytes"] = 0, ["total_bytes"] = 0, ["p95_bytes"] = 0 };

            var sorted = values.OrderBy(x => x).ToList();
            double avg = sorted.Average();
            long total = sorted.Sum();
            double p95 = Percentile(sorted.Select(x => (double)x).ToList(), 0.95);

            return new JObject
            {
                ["avg_bytes"] = (long)Math.Round(avg),
                ["total_bytes"] = total,
                ["p95_bytes"] = (long)Math.Round(p95)
            };
        }

        private static double Percentile(List<double> sorted, double percentile)
        {
            if (sorted.Count == 0) return 0;
            if (sorted.Count == 1) return sorted[0];

            double index = percentile * (sorted.Count - 1);
            int lower = (int)Math.Floor(index);
            int upper = (int)Math.Ceiling(index);

            if (lower == upper)
                return sorted[lower];

            double frac = index - lower;
            return sorted[lower] * (1.0 - frac) + sorted[upper] * frac;
        }
    }
}
