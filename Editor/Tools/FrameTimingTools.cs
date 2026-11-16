using System;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace I1rr.UnityMcp.Editor
{
    [McpToolCategory("Profiling")]
    public static class FrameTimingTools
    {
        #region get_frame_timing

        [McpTool("get_frame_timing",
            "Get FPS, frame time, CPU main thread time from long-lived ProfilerRecorders over N frames. Optionally includes FrameTimingManager data. Play mode only.",
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""frames"": { ""type"": ""integer"", ""description"": ""Number of frames to sample (default 60, max 300)"", ""default"": 60 }
                }
            }")]
        public static JObject GetFrameTiming(JObject parameters)
        {
            var provider = ProfilerRecorderService.Provider;
            if (provider == null) return Error("ProfilerRecorderManager not initialized.");

            int frames = Math.Clamp(parameters["frames"]?.Value<int>() ?? 60, 1, 300);

            var mainThreadSamples = provider.GetSamples("Main Thread", frames);
            var renderThreadSamples = provider.GetSamples("Render Thread", frames);

            int mainCount = mainThreadSamples.Count;
            double mainThreadAvgNs = 0;
            double mainThreadMaxNs = 0;
            if (mainCount > 0)
            {
                for (int i = 0; i < mainCount; i++)
                {
                    mainThreadAvgNs += mainThreadSamples[i];
                    if (mainThreadSamples[i] > mainThreadMaxNs) mainThreadMaxNs = mainThreadSamples[i];
                }
                mainThreadAvgNs /= mainCount;
            }

            int renderCount = renderThreadSamples.Count;
            double renderThreadAvgNs = 0;
            if (renderCount > 0)
            {
                for (int i = 0; i < renderCount; i++)
                    renderThreadAvgNs += renderThreadSamples[i];
                renderThreadAvgNs /= renderCount;
            }

            double fps = mainThreadAvgNs > 0 ? 1_000_000_000.0 / mainThreadAvgNs : 0;

            var result = new JObject
            {
                ["success"] = true,
                ["samples_collected"] = mainCount,
                ["fps"] = Math.Round(fps, 1),
                ["main_thread_avg_ms"] = Math.Round(mainThreadAvgNs / 1_000_000.0, 3),
                ["main_thread_max_ms"] = Math.Round(mainThreadMaxNs / 1_000_000.0, 3),
                ["render_thread_avg_ms"] = Math.Round(renderThreadAvgNs / 1_000_000.0, 3),
            };

            // Attempt FrameTimingManager as an alternative data source (available on most platforms in 2022.3+).
            try
            {
                FrameTimingManager.CaptureFrameTimings();
                var timings = new FrameTiming[1];
                uint count = FrameTimingManager.GetLatestTimings((uint)timings.Length, timings);
                if (count > 0)
                {
                    result["frame_timing_cpu_ms"] = Math.Round(timings[0].cpuFrameTime, 3);
                    result["frame_timing_gpu_ms"] = Math.Round(timings[0].gpuFrameTime, 3);
                }
            }
            catch { /* FrameTimingManager not available on all platforms */ }

            return result;
        }

        #endregion

        #region get_fps_realtime

        [McpTool("get_fps_realtime",
            "Get FPS and frame-time stats from the persistent Time.unscaledDeltaTime ring buffer. Always works regardless of ProfilerRecorder state. Supports since_frame/since_time filtering to compare FPS before vs after an event. Play mode only.",
            AutoRoute = true,
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""sample_frames"": { ""type"": ""integer"", ""description"": ""Number of recent frames to sample (default 60, max 600). Ignored when since_frame or since_time is provided."", ""default"": 60 },
                    ""since_frame"": { ""type"": ""integer"", ""description"": ""Include only frames where frameCount >= this value. Takes precedence over since_time."" },
                    ""since_time"": { ""type"": ""number"", ""description"": ""Include only frames where realtimeSinceStartup >= this value. Ignored if since_frame is provided."" }
                }
            }")]
        public static JObject GetFpsRealtime(JObject parameters)
        {
            var buffer = EditorMainThreadDispatcher.Instance.FrameTimeBuffer;
            if (buffer.Count == 0)
                return Error("No frame timing data recorded yet.");

            int? sinceFrame = parameters["since_frame"]?.Value<int>();
            float? sinceTime = parameters["since_time"]?.Value<float>();

            JObject result;

            if (sinceFrame.HasValue || sinceTime.HasValue)
            {
                FrameSample[] samples = sinceFrame.HasValue
                    ? buffer.GetSamplesSinceFrame(sinceFrame.Value)
                    : buffer.GetSamplesSinceTime(sinceTime.Value);

                int count = samples.Length;
                if (count == 0)
                {
                    result = new JObject
                    {
                        ["success"] = true,
                        ["fps"] = 0,
                        ["avg_frame_time_ms"] = 0,
                        ["min_frame_time_ms"] = 0,
                        ["max_frame_time_ms"] = 0,
                        ["frame_count"] = 0,
                        ["samples_available"] = 0,
                        ["current_frame"] = Time.frameCount,
                        ["current_time"] = (double)Time.realtimeSinceStartup
                    };
                    return result;
                }

                double sum = 0;
                float min = float.MaxValue;
                float max = 0f;
                for (int i = 0; i < count; i++)
                {
                    float dt = samples[i].DeltaTime;
                    sum += dt;
                    if (dt < min) min = dt;
                    if (dt > max) max = dt;
                }
                float avg = (float)(sum / count);
                float fps = avg > 0f ? 1f / avg : 0f;

                result = new JObject
                {
                    ["success"] = true,
                    ["fps"] = Math.Round(fps, 1),
                    ["avg_frame_time_ms"] = Math.Round(avg * 1000.0, 3),
                    ["min_frame_time_ms"] = Math.Round(min * 1000.0, 3),
                    ["max_frame_time_ms"] = Math.Round(max * 1000.0, 3),
                    ["frame_count"] = count,
                    ["samples_available"] = count,
                    ["current_frame"] = Time.frameCount,
                    ["current_time"] = (double)Time.realtimeSinceStartup
                };
            }
            else
            {
                int sampleFrames = Math.Clamp(parameters["sample_frames"]?.Value<int>() ?? 60, 1, FrameTimeRingBuffer.Capacity);

                var stats = buffer.ComputeStats(sampleFrames);

                result = new JObject
                {
                    ["success"] = true,
                    ["fps"] = Math.Round(stats.Fps, 1),
                    ["avg_frame_time_ms"] = Math.Round(stats.AvgDeltaTime * 1000.0, 3),
                    ["min_frame_time_ms"] = Math.Round(stats.MinDeltaTime * 1000.0, 3),
                    ["max_frame_time_ms"] = Math.Round(stats.MaxDeltaTime * 1000.0, 3),
                    ["frame_count"] = stats.FrameCount,
                    ["current_frame"] = Time.frameCount,
                    ["current_time"] = (double)Time.realtimeSinceStartup
                };

                // Spike detection (only for non-filtered queries)
                var spikeDt = new float[FrameTimeRingBuffer.Capacity];
                var spikeFc = new int[FrameTimeRingBuffer.Capacity];
                var spikeRt = new float[FrameTimeRingBuffer.Capacity];
                int spikeCount = buffer.DetectSpikes(spikeDt, spikeFc, spikeRt, FrameTimeRingBuffer.Capacity);

                if (spikeCount > 0)
                {
                    var spikes = new JArray();
                    for (int i = 0; i < spikeCount; i++)
                    {
                        spikes.Add(new JObject
                        {
                            ["frame"] = spikeFc[i],
                            ["delta_time_ms"] = Math.Round(spikeDt[i] * 1000.0, 3),
                            ["realtime_since_startup"] = Math.Round(spikeRt[i], 3)
                        });
                    }
                    result["spikes"] = spikes;
                    result["spike_count"] = spikeCount;
                }
            }

            return result;
        }

        #endregion

        #region get_per_camera_stats

        [McpTool("get_per_camera_stats",
            "Get configuration of all cameras in the scene — name, depth, culling mask, pixel rect, target texture, clear flags. Play mode only.",
            AutoRoute = true,
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {}
            }")]
        public static JObject GetPerCameraStats(JObject parameters)
        {
            var allCameras = Camera.allCameras;
            if (allCameras.Length == 0)
                return Error("No active cameras found.");

            var cameras = new JArray();
            foreach (var cam in allCameras)
            {
                if (cam == null) continue;
                var rect = cam.pixelRect;
                cameras.Add(new JObject
                {
                    ["name"] = cam.gameObject.name,
                    ["enabled"] = cam.enabled,
                    ["depth"] = cam.depth,
                    ["culling_mask"] = cam.cullingMask,
                    ["pixel_rect"] = $"{rect.x},{rect.y},{rect.width},{rect.height}",
                    ["target_texture"] = cam.targetTexture != null ? cam.targetTexture.name : null,
                    ["clear_flags"] = cam.clearFlags.ToString(),
                    ["field_of_view"] = Math.Round(cam.fieldOfView, 2),
                    ["orthographic"] = cam.orthographic,
                    ["near_clip"] = Math.Round(cam.nearClipPlane, 3),
                    ["far_clip"] = Math.Round(cam.farClipPlane, 1)
                });
            }

            return new JObject
            {
                ["success"] = true,
                ["camera_count"] = cameras.Count,
                ["cameras"] = cameras
            };
        }

        #endregion

        #region get_gpu_timing

        private static bool? _gpuTimingSupported;

        [McpTool("get_gpu_timing",
            "Get GPU and CPU frame timing from FrameTimingManager. Helps identify GPU vs CPU bottlenecks. Play mode only.",
            AutoRoute = true,
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""frames"": { ""type"": ""integer"", ""description"": ""Number of frame timings to request (default 1, max 10)"", ""default"": 1 }
                }
            }")]
        public static JObject GetGpuTiming(JObject parameters)
        {
            // Lazy support check — cache result to avoid repeated probing
            if (_gpuTimingSupported == null)
            {
                try
                {
                    FrameTimingManager.CaptureFrameTimings();
                    var probe = new FrameTiming[1];
                    uint probeCount = FrameTimingManager.GetLatestTimings(1, probe);
                    _gpuTimingSupported = probeCount > 0 && probe[0].gpuFrameTime > 0;
                }
                catch
                {
                    _gpuTimingSupported = false;
                }
            }

            if (_gpuTimingSupported == false)
            {
                return new JObject
                {
                    ["success"] = true,
                    ["supported"] = false,
                    ["suggestion"] = "GPU timing not available on this platform/graphics API. Use get_frame_timing for CPU-side frame times."
                };
            }

            int frameCount = Math.Clamp(parameters["frames"]?.Value<int>() ?? 1, 1, 10);

            FrameTimingManager.CaptureFrameTimings();
            var timings = new FrameTiming[frameCount];
            uint count = FrameTimingManager.GetLatestTimings((uint)frameCount, timings);

            if (count == 0)
            {
                return new JObject
                {
                    ["success"] = true,
                    ["supported"] = false,
                    ["suggestion"] = "GPU timing not available on this platform/graphics API. Use get_frame_timing for CPU-side frame times."
                };
            }

            double gpuTotal = 0, cpuTotal = 0;
            double gpuMax = 0, cpuMax = 0;
            for (int i = 0; i < (int)count; i++)
            {
                gpuTotal += timings[i].gpuFrameTime;
                cpuTotal += timings[i].cpuFrameTime;
                if (timings[i].gpuFrameTime > gpuMax) gpuMax = timings[i].gpuFrameTime;
                if (timings[i].cpuFrameTime > cpuMax) cpuMax = timings[i].cpuFrameTime;
            }

            double gpuAvg = gpuTotal / count;
            double cpuAvg = cpuTotal / count;

            return new JObject
            {
                ["success"] = true,
                ["supported"] = true,
                ["frames_captured"] = (int)count,
                ["gpu_frame_time_ms"] = Math.Round(gpuAvg, 3),
                ["cpu_frame_time_ms"] = Math.Round(cpuAvg, 3),
                ["gpu_frame_time_max_ms"] = Math.Round(gpuMax, 3),
                ["cpu_frame_time_max_ms"] = Math.Round(cpuMax, 3),
                ["is_gpu_bound"] = gpuAvg > cpuAvg
            };
        }

        #endregion

        #region get_rendering_stats

        [McpTool("get_rendering_stats",
            "Get draw calls, batches, triangles, set-pass calls, shadow casters from long-lived recorders. Shadow casters and render-texture stats are only available in the Built-in Render Pipeline; URP/HDRP returns null for those fields. Play mode only.",
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {}
            }")]
        public static JObject GetRenderingStats(JObject parameters)
        {
            var provider = ProfilerRecorderService.Provider;
            if (provider == null) return Error("ProfilerRecorderManager not initialized.");

            // Request on-demand counters for stats not in the standard set.
            // First call primes them; data is available from subsequent calls.
            provider.RequestOnDemandCounter("Render", "Shadow Casters Count");
            provider.RequestOnDemandCounter("Render", "Render Textures Count");
            provider.RequestOnDemandCounter("Render", "Render Textures Bytes");
            provider.RequestOnDemandCounter("Render", "Used Textures Count");
            provider.RequestOnDemandCounter("Render", "Used Textures Bytes");

            // Detect active render pipeline (matches get_rendering_pipeline in BuildTools —
            // currentRenderPipeline honors per-quality-level overrides; defaultRenderPipeline does not).
            var rpa = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            string pipeline = rpa == null ? "BiRP" :
                rpa.GetType().Name.Contains("Universal") ? "URP" :
                rpa.GetType().Name.Contains("HighDefinition") ? "HDRP" : "Custom";

            return new JObject
            {
                ["success"] = true,
                ["rendering_pipeline"] = pipeline,
                ["draw_calls"] = provider.IsCounterValid("Draw Calls Count") ? (JToken)provider.GetLastValue("Draw Calls Count") : JValue.CreateNull(),
                ["batches"] = provider.IsCounterValid("Batches Count") ? (JToken)provider.GetLastValue("Batches Count") : JValue.CreateNull(),
                ["triangles"] = provider.IsCounterValid("Triangles Count") ? (JToken)provider.GetLastValue("Triangles Count") : JValue.CreateNull(),
                ["vertices"] = provider.IsCounterValid("Vertices Count") ? (JToken)provider.GetLastValue("Vertices Count") : JValue.CreateNull(),
                ["set_pass_calls"] = provider.IsCounterValid("SetPass Calls Count") ? (JToken)provider.GetLastValue("SetPass Calls Count") : JValue.CreateNull(),
                ["shadow_casters"] = provider.IsCounterValid("Shadow Casters Count") ? (JToken)provider.GetLastValue("Shadow Casters Count") : JValue.CreateNull(),
                ["render_textures_count"] = provider.IsCounterValid("Render Textures Count") ? (JToken)provider.GetLastValue("Render Textures Count") : JValue.CreateNull(),
                ["render_textures_bytes"] = provider.IsCounterValid("Render Textures Bytes") ? (JToken)provider.GetLastValue("Render Textures Bytes") : JValue.CreateNull(),
                ["used_textures_count"] = provider.IsCounterValid("Used Textures Count") ? (JToken)provider.GetLastValue("Used Textures Count") : JValue.CreateNull(),
                ["used_textures_bytes"] = provider.IsCounterValid("Used Textures Bytes") ? (JToken)provider.GetLastValue("Used Textures Bytes") : JValue.CreateNull()
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
