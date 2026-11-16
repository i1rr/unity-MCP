using System;
using System.Collections.Generic;
using System.Threading;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Scripting;

namespace I1rr.UnityMcp.Runtime
{
    [Preserve]
    [McpToolCategory("Profiling")]
    public static class RuntimeProfilingTools
    {
        private static JObject Error(string message) => McpError.Create(McpErrorCodes.InternalError, message);

        // Object-scan fallback cache for texture/mesh memory (1-second TTL)
        private static volatile float _cachedTexMemMb;
        private static volatile float _cachedMeshMemMb;
        private static long _lastScanTicks;

        private static void EnsureObjectScanCache(out float texMb, out float meshMb)
        {
            long now = (long)(uint)Environment.TickCount;
            if (now - Interlocked.Read(ref _lastScanTicks) < 1000)
            {
                texMb = _cachedTexMemMb;
                meshMb = _cachedMeshMemMb;
                return;
            }

            float t = 0f;
            foreach (var tex in Resources.FindObjectsOfTypeAll<Texture>())
                t += Profiler.GetRuntimeMemorySizeLong(tex) / (1024f * 1024f);

            float m = 0f;
            foreach (var mesh in Resources.FindObjectsOfTypeAll<Mesh>())
                m += Profiler.GetRuntimeMemorySizeLong(mesh) / (1024f * 1024f);

            _cachedTexMemMb = t;
            _cachedMeshMemMb = m;
            Interlocked.Exchange(ref _lastScanTicks, now);
            texMb = t;
            meshMb = m;
        }

        #region runtime_get_frame_stats

        [McpTool("runtime_get_frame_stats",
            "Get FPS, frame time, draw calls, triangles, and batches sampled over N frames.",
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""sample_count"": { ""type"": ""integer"", ""description"": ""Number of frames to sample (default 60, max 300)"", ""default"": 60 }
                }
            }")]
        [Preserve]
        public static JObject GetFrameStats(JObject parameters)
        {
            var provider = ProfilerRecorderService.Provider;
            if (provider == null) return Error("ProfilerRecorderManager not initialized.");

            int sampleCount = Math.Clamp(parameters["sample_count"]?.Value<int>() ?? 60, 1, 300);

            var mainThreadSamples = provider.GetSamples("Main Thread", sampleCount);

            int count = mainThreadSamples.Count;
            double totalNs = 0;
            double maxNs = 0;
            double minNs = double.MaxValue;

            for (int i = 0; i < count; i++)
            {
                double val = mainThreadSamples[i];
                totalNs += val;
                if (val > maxNs) maxNs = val;
                if (val < minNs) minNs = val;
            }

            double avgMs = count > 0 ? (totalNs / count) / 1_000_000.0 : 0;
            double maxMs = maxNs / 1_000_000.0;
            double minMs = count > 0 ? minNs / 1_000_000.0 : 0;
            double fps = avgMs > 0 ? 1000.0 / avgMs : 0;

            long lastDrawCalls = provider.GetLastValue("Draw Calls Count");
            long lastTriangles = provider.GetLastValue("Triangles Count");
            long lastBatches = provider.GetLastValue("Batches Count");

            return new JObject
            {
                ["success"] = true,
                ["samples_available"] = count,
                ["fps"] = Math.Round(fps, 1),
                ["frame_time_avg_ms"] = Math.Round(avgMs, 3),
                ["frame_time_max_ms"] = Math.Round(maxMs, 3),
                ["frame_time_min_ms"] = Math.Round(minMs, 3),
                ["draw_calls"] = lastDrawCalls,
                ["triangles"] = lastTriangles,
                ["batches"] = lastBatches
            };
        }

        #endregion

        #region runtime_get_memory

        [McpTool("runtime_get_memory",
            "Get total/used/GC heap/texture/mesh memory at runtime.",
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {}
            }")]
        [Preserve]
        public static JObject GetMemory(JObject parameters)
        {
            long totalAllocated = Profiler.GetTotalAllocatedMemoryLong();
            long totalReserved = Profiler.GetTotalReservedMemoryLong();
            long monoHeap = Profiler.GetMonoHeapSizeLong();
            long monoUsed = Profiler.GetMonoUsedSizeLong();
            long gfxDriver = Profiler.GetAllocatedMemoryForGraphicsDriver();

            // Read texture/mesh memory from the long-lived manager recorders
            var provider = ProfilerRecorderService.Provider;
            long texMemory = provider?.GetLastValue("Used Texture Memory") ?? 0;
            long meshMemory = provider?.GetLastValue("Used Mesh Memory") ?? 0;

            string memorySource = "profiler_counter";
            double textureMb, meshMb;

            if (texMemory > 0 || meshMemory > 0)
            {
                textureMb = Math.Round(texMemory / (1024.0 * 1024.0), 2);
                meshMb = Math.Round(meshMemory / (1024.0 * 1024.0), 2);
            }
            else
            {
                EnsureObjectScanCache(out float tMb, out float mMb);
                textureMb = Math.Round(tMb, 2);
                meshMb = Math.Round(mMb, 2);
                memorySource = "object_scan";
            }

            return new JObject
            {
                ["success"] = true,
                ["total_allocated_mb"] = Math.Round(totalAllocated / (1024.0 * 1024.0), 2),
                ["total_reserved_mb"] = Math.Round(totalReserved / (1024.0 * 1024.0), 2),
                ["mono_heap_mb"] = Math.Round(monoHeap / (1024.0 * 1024.0), 2),
                ["mono_used_mb"] = Math.Round(monoUsed / (1024.0 * 1024.0), 2),
                ["gfx_driver_mb"] = Math.Round(gfxDriver / (1024.0 * 1024.0), 2),
                ["texture_memory_mb"] = textureMb,
                ["mesh_memory_mb"] = meshMb,
                ["memory_source"] = memorySource,
                ["total_allocated_bytes"] = totalAllocated,
                ["total_reserved_bytes"] = totalReserved,
                ["mono_heap_bytes"] = monoHeap,
                ["mono_used_bytes"] = monoUsed,
                ["gfx_driver_bytes"] = gfxDriver
            };
        }

        #endregion

        #region runtime_get_profiler_counters

        [McpTool("runtime_get_profiler_counters",
            "Read raw ProfilerRecorder counter values by category and name. If a counter is not pre-registered, it is started on-demand and data will be available on subsequent calls.",
            AutoRoute = true,
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""category"": { ""type"": ""string"", ""description"": ""Profiler category (e.g. 'Render', 'Memory', 'Scripts', 'Physics')"" },
                    ""names"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Counter names to read"" }
                },
                ""required"": [""category"", ""names""]
            }")]
        [Preserve]
        public static JObject GetProfilerCounters(JObject parameters)
        {
            var provider = ProfilerRecorderService.Provider;
            if (provider == null) return Error("ProfilerRecorderManager not initialized.");

            var categoryName = parameters["category"]?.Value<string>();
            var names = parameters["names"]?.ToObject<string[]>();

            if (string.IsNullOrEmpty(categoryName)) return Error("'category' is required.");
            if (names == null || names.Length == 0) return Error("'names' array is required and must not be empty.");

            var results = new JObject();

            foreach (var name in names)
            {
                bool ready = provider.RequestOnDemandCounter(categoryName, name);
                bool valid = provider.IsCounterValid(name);

                var entry = new JObject
                {
                    ["value"] = valid ? provider.GetLastValue(name) : 0,
                    ["valid"] = valid,
                    ["count"] = valid ? provider.GetSamples(name, 1).Count : 0
                };

                if (!ready && !valid)
                    entry["note"] = "Counter just started. Data will be available on subsequent calls.";

                results[name] = entry;
            }

            return new JObject
            {
                ["success"] = true,
                ["category"] = categoryName,
                ["counters"] = results
            };
        }

        #endregion

        #region runtime_gc_collect

        [McpTool("runtime_gc_collect",
            "Force garbage collection and report freed bytes.",
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {}
            }")]
        [Preserve]
        public static JObject GcCollect(JObject parameters)
        {
            long beforeMono = Profiler.GetMonoUsedSizeLong();
            long beforeTotal = GC.GetTotalMemory(false);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long afterMono = Profiler.GetMonoUsedSizeLong();
            long afterTotal = GC.GetTotalMemory(true);

            long freedMono = beforeMono - afterMono;
            long freedTotal = beforeTotal - afterTotal;

            return new JObject
            {
                ["success"] = true,
                ["freed_mono_bytes"] = Math.Max(0, freedMono),
                ["freed_gc_bytes"] = Math.Max(0, freedTotal),
                ["freed_mono_mb"] = Math.Round(Math.Max(0, freedMono) / (1024.0 * 1024.0), 3),
                ["freed_gc_mb"] = Math.Round(Math.Max(0, freedTotal) / (1024.0 * 1024.0), 3),
                ["after_mono_used_mb"] = Math.Round(afterMono / (1024.0 * 1024.0), 2),
                ["after_gc_total_mb"] = Math.Round(afterTotal / (1024.0 * 1024.0), 2)
            };
        }

        #endregion

        #region runtime_get_fps_realtime

        [McpTool("runtime_get_fps_realtime",
            "Get FPS and frame-time stats from the persistent Time.unscaledDeltaTime ring buffer. Always works regardless of ProfilerRecorder state. Supports since_frame/since_time filtering to compare FPS before vs after an event.",
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
        [Preserve]
        public static JObject GetFpsRealtime(JObject parameters)
        {
            var buffer = RuntimeMainThreadDispatcher.FrameTimeBuffer;
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

        #region runtime_get_per_camera_stats

        [McpTool("runtime_get_per_camera_stats",
            "Get configuration of all cameras in the scene — name, depth, culling mask, pixel rect, target texture, clear flags.",
            AutoRoute = true,
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {}
            }")]
        [Preserve]
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

        #region runtime_get_gpu_timing

        private static bool? _gpuTimingSupported;

        [McpTool("runtime_get_gpu_timing",
            "Get GPU and CPU frame timing from FrameTimingManager. Helps identify GPU vs CPU bottlenecks.",
            AutoRoute = true,
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""frames"": { ""type"": ""integer"", ""description"": ""Number of frame timings to request (default 1, max 10)"", ""default"": 1 }
                }
            }")]
        [Preserve]
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
                    ["suggestion"] = "GPU timing not available on this platform/graphics API. Use runtime_get_frame_stats for CPU-side frame times."
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
                    ["suggestion"] = "GPU timing not available on this platform/graphics API. Use runtime_get_frame_stats for CPU-side frame times."
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
    }
}
