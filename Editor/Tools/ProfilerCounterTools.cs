using System;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;

namespace I1rr.UnityMcp.Editor
{
    [McpToolCategory("Profiling")]
    public static class ProfilerCounterTools
    {
        #region get_profiler_counters

        [McpTool("get_profiler_counters",
            "Read ProfilerRecorder counters by category and name. If a counter is not pre-registered, it is started on-demand and data will be available on subsequent calls. Play mode only.",
            AutoRoute = true,
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""category"": { ""type"": ""string"", ""description"": ""Profiler category (e.g. 'Render', 'Memory', 'Scripts', 'Physics', 'Animation', 'Audio', 'GC', 'Internal')"" },
                    ""names"": {
                        ""type"": ""array"",
                        ""items"": { ""type"": ""string"" },
                        ""description"": ""Counter names to read within the category""
                    }
                },
                ""required"": [""category"", ""names""]
            }")]
        public static JObject GetProfilerCounters(JObject parameters)
        {
            var provider = ProfilerRecorderService.Provider;
            if (provider == null) return Error("ProfilerRecorderManager not initialized.");

            var categoryName = parameters["category"]?.Value<string>();
            var names = parameters["names"]?.ToObject<string[]>();

            if (string.IsNullOrWhiteSpace(categoryName))
                return Error("Parameter 'category' is required.");
            if (names == null || names.Length == 0)
                return Error("Parameter 'names' is required and must not be empty.");
            if (names.Length > 50)
                return Error("Maximum 50 counter names per call.");

            var results = new JArray();

            foreach (var name in names)
            {
                bool ready = provider.RequestOnDemandCounter(categoryName, name);
                bool valid = provider.IsCounterValid(name);

                var entry = new JObject
                {
                    ["name"] = name,
                    ["valid"] = valid
                };

                if (valid)
                {
                    entry["value"] = provider.GetLastValue(name);
                    entry["count"] = provider.GetSamples(name, 1).Count;
                }

                if (!ready && !valid)
                    entry["note"] = "Counter just started. Data will be available on subsequent calls.";

                results.Add(entry);
            }

            return new JObject
            {
                ["success"] = true,
                ["category"] = categoryName,
                ["counters"] = results
            };
        }

        #endregion

        #region get_gc_pressure

        [McpTool("get_gc_pressure",
            "Get GC allocation bytes per frame over N frames from long-lived recorder. Play mode only.",
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""frames"": { ""type"": ""integer"", ""description"": ""Number of frames to sample (default 60, max 300)"", ""default"": 60 }
                }
            }")]
        public static JObject GetGcPressure(JObject parameters)
        {
            var provider = ProfilerRecorderService.Provider;
            if (provider == null) return Error("ProfilerRecorderManager not initialized.");

            int frames = Math.Clamp(parameters["frames"]?.Value<int>() ?? 60, 1, 300);

            var samples = provider.GetSamples("GC Allocated In Frame", frames);

            int count = samples.Count;
            long totalBytes = 0;
            long maxBytes = 0;
            long minBytes = long.MaxValue;

            var perFrame = count <= 60 ? new JArray() : null;
            for (int i = 0; i < count; i++)
            {
                long value = samples[i];
                totalBytes += value;
                if (value > maxBytes) maxBytes = value;
                if (value < minBytes) minBytes = value;
                perFrame?.Add(value);
            }

            if (count == 0) minBytes = 0;

            double avgBytes = count > 0 ? (double)totalBytes / count : 0;

            return new JObject
            {
                ["success"] = true,
                ["samples_collected"] = count,
                ["total_gc_bytes"] = totalBytes,
                ["avg_bytes_per_frame"] = Math.Round(avgBytes, 0),
                ["max_bytes_per_frame"] = maxBytes,
                ["min_bytes_per_frame"] = minBytes,
                ["per_frame_bytes"] = perFrame
            };
        }

        #endregion

        private static JObject Error(string message)
        {
            var result = McpError.Create(McpErrorCodes.InternalError, message);
            result["success"] = false;
            return result;
        }
    }
}
