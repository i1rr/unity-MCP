using System;
using I1rr.UnityMcp.Runtime.Sessions;
using I1rr.UnityMcp.Shared;
using I1rr.UnityMcp.Shared.Sessions;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Scripting;

namespace I1rr.UnityMcp.Runtime
{
    [Preserve]
    [McpToolCategory("Session")]
    public static class RuntimeSessionTools
    {
        private static JObject Error(string message) => McpError.Create(McpErrorCodes.InternalError, message);

        #region runtime_start_recording

        [McpTool("runtime_start_recording",
            "Start a profiler recording session that samples specified counters at a configurable interval.",
            AutoRoute = true,
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""counters"": {
                        ""type"": ""array"",
                        ""items"": { ""type"": ""string"" },
                        ""description"": ""List of profiler counter names to record (e.g. 'Main Thread', 'Draw Calls Count')""
                    },
                    ""interval_frames"": {
                        ""type"": ""integer"",
                        ""description"": ""Sample every N frames (1-300, default 1). Takes precedence over interval_ms.""
                    },
                    ""interval_ms"": {
                        ""type"": ""integer"",
                        ""description"": ""Sample every N milliseconds (16-5000). Ignored if interval_frames is set.""
                    },
                    ""duration_seconds"": {
                        ""type"": ""number"",
                        ""description"": ""Auto-stop after this many seconds (0 = manual stop, default 0)""
                    },
                    ""max_samples"": {
                        ""type"": ""integer"",
                        ""description"": ""Maximum samples per counter (default 3600)""
                    }
                },
                ""required"": [""counters""]
            }")]
        [Preserve]
        public static JObject StartRecording(JObject parameters)
        {
            var manager = SessionManager.Instance;
            if (manager == null) return Error("SessionManager not initialized.");

            var countersToken = parameters["counters"] as JArray;
            if (countersToken == null || countersToken.Count == 0)
                return Error("'counters' array is required and must not be empty.");

            var counters = new string[countersToken.Count];
            for (int i = 0; i < countersToken.Count; i++)
                counters[i] = countersToken[i]?.Value<string>() ?? "";

            bool intervalClamped = false;
            int intervalFrames;

            var intervalFramesToken = parameters["interval_frames"];
            var intervalMsToken = parameters["interval_ms"];

            if (intervalFramesToken != null)
            {
                intervalFrames = intervalFramesToken.Value<int>();
                if (intervalFrames <= 0)
                    return Error("interval_frames must be >= 1.");
                if (intervalFrames < RecordingSession.MinIntervalFrames || intervalFrames > RecordingSession.MaxIntervalFrames)
                {
                    intervalFrames = Math.Clamp(intervalFrames, RecordingSession.MinIntervalFrames, RecordingSession.MaxIntervalFrames);
                    intervalClamped = true;
                }
            }
            else if (intervalMsToken != null)
            {
                int intervalMs = intervalMsToken.Value<int>();
                if (intervalMs <= 0)
                    return Error("interval_ms must be >= 16.");
                if (intervalMs < RecordingSession.MinIntervalMs || intervalMs > RecordingSession.MaxIntervalMs)
                {
                    intervalMs = Math.Clamp(intervalMs, RecordingSession.MinIntervalMs, RecordingSession.MaxIntervalMs);
                    intervalClamped = true;
                }
                intervalFrames = Math.Max(1, (int)Math.Round(intervalMs / 16.667));
            }
            else
            {
                intervalFrames = 1;
            }

            float durationSeconds = parameters["duration_seconds"]?.Value<float>() ?? 0f;
            int maxSamples = parameters["max_samples"]?.Value<int>() ?? RecordingSession.MaxSamplesPerCounter;
            maxSamples = Math.Clamp(maxSamples, 1, RecordingSession.MaxSamplesPerCounter);

            return manager.StartSession(counters, intervalFrames, durationSeconds, maxSamples, intervalClamped);
        }

        #endregion

        #region runtime_stop_recording

        [McpTool("runtime_stop_recording",
            "Stop a profiler recording session and return aggregated results (min/max/avg/p50/p95/p99 and time series).",
            AutoRoute = true,
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""session_id"": { ""type"": ""string"", ""description"": ""The session ID returned by runtime_start_recording"" }
                },
                ""required"": [""session_id""]
            }")]
        [Preserve]
        public static JObject StopRecording(JObject parameters)
        {
            var manager = SessionManager.Instance;
            if (manager == null) return Error("SessionManager not initialized.");

            var sessionId = parameters["session_id"]?.Value<string>();
            if (string.IsNullOrEmpty(sessionId))
                return Error("'session_id' is required.");

            return manager.StopSession(sessionId);
        }

        #endregion

        #region runtime_get_recording_status

        [McpTool("runtime_get_recording_status",
            "Get the status of an active profiler recording session without stopping it.",
            AutoRoute = true,
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""session_id"": { ""type"": ""string"", ""description"": ""The session ID returned by runtime_start_recording"" }
                },
                ""required"": [""session_id""]
            }")]
        [Preserve]
        public static JObject GetRecordingStatus(JObject parameters)
        {
            var manager = SessionManager.Instance;
            if (manager == null) return Error("SessionManager not initialized.");

            var sessionId = parameters["session_id"]?.Value<string>();
            if (string.IsNullOrEmpty(sessionId))
                return Error("'session_id' is required.");

            return manager.GetStatus(sessionId);
        }

        #endregion

        #region runtime_wait_for_frames

        [McpTool("runtime_wait_for_frames",
            "Wait for a specified number of frames to pass before returning. Defers the MCP response until the frame count is reached.",
            AutoRoute = true,
            RequiresPlayMode = true,
            DeferredResponse = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""count"": { ""type"": ""integer"", ""description"": ""Number of frames to wait (1-600)"" },
                    ""max_wait_seconds"": { ""type"": ""number"", ""description"": ""Maximum seconds to wait (default 30, max 60)"" }
                },
                ""required"": [""count""]
            }")]
        [Preserve]
        public static object WaitForFrames(JObject parameters)
        {
            int count = parameters["count"]?.Value<int>() ?? 0;
            if (count <= 0) return Error("'count' must be >= 1.");
            count = Math.Min(count, 600);

            float maxWait = parameters["max_wait_seconds"]?.Value<float>() ?? 30f;
            maxWait = Math.Clamp(maxWait, 1f, 60f);

            return DeferredResult.WaitForFrames(count, maxWait);
        }

        #endregion

        #region runtime_wait_for_seconds

        [McpTool("runtime_wait_for_seconds",
            "Wait for a specified number of seconds before returning. Defers the MCP response until the time elapses.",
            AutoRoute = true,
            RequiresPlayMode = true,
            DeferredResponse = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""seconds"": { ""type"": ""number"", ""description"": ""Number of seconds to wait (0.016-60)"" },
                    ""max_wait_seconds"": { ""type"": ""number"", ""description"": ""Maximum seconds to wait (default: seconds + 5, max 60)"" }
                },
                ""required"": [""seconds""]
            }")]
        [Preserve]
        public static object WaitForSeconds(JObject parameters)
        {
            float seconds = parameters["seconds"]?.Value<float>() ?? 0;
            if (seconds <= 0) return Error("'seconds' must be > 0.");
            seconds = Math.Clamp(seconds, 0.016f, 60f);

            float maxWait = parameters["max_wait_seconds"]?.Value<float>() ?? (seconds + 5f);
            maxWait = Math.Clamp(maxWait, seconds, 60f);

            float startTime = Time.realtimeSinceStartup;
            int framesElapsed = 0;

            return new DeferredResult
            {
                TimeoutSeconds = maxWait,
                IsComplete = () =>
                {
                    framesElapsed++;
                    return (Time.realtimeSinceStartup - startTime) >= seconds;
                },
                GetResult = () =>
                {
                    float actual = Time.realtimeSinceStartup - startTime;
                    return new JObject
                    {
                        ["actual_seconds"] = Math.Round(actual, 3),
                        ["frames_elapsed"] = framesElapsed
                    };
                }
            };
        }

        #endregion
    }
}
