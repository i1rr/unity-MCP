using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Media;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace I1rr.UnityMcp.Editor
{
    [InitializeOnLoad, McpToolCategory("Editor")]
    public static class GameViewSequence
    {
        private const string ActiveKey = "i1rr-unity-mcp.GameViewSequence";
        private static readonly string Root = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "i1rr-unity-mcp-sequences");
        private static JObject job;
        private static SequenceHost host;
        private static MediaEncoder encoder;
        private static readonly Queue<Frame> before = new();
        private static readonly JArray timing = new();
        private static int postCount;
        private static double firstTime, nextSample;
        private static bool triggered;
        private static double markerTime;
        private static double SampleTime => (string)job["clock"] == "unscaled" ? Time.unscaledTimeAsDouble : Time.timeAsDouble;

        private sealed class Frame
        {
            public Texture2D Image;
            public int Number;
            public double Time;
            public double UnscaledTime;
        }

        static GameViewSequence()
        {
            var id = SessionState.GetString(ActiveKey, "");
            if (id.Length != 0) job = Read(id);
            Application.logMessageReceived += OnLog;
            EditorApplication.update += Update;
            EditorApplication.playModeStateChanged += state => {
                if (job != null && state == PlayModeStateChange.ExitingPlayMode) Finish("failed", "Play Mode ended before the sequence completed.");
            };
            AssemblyReloadEvents.beforeAssemblyReload += () => {
                if (job != null && (triggered || encoder != null)) Finish("failed", "Capture interrupted by script reload.");
                else Release();
            };
        }

        private static string Folder => Path.Combine(Root, (string)job["capture_id"]);
        private static JObject Read(string id)
        {
            string path = Path.Combine(Root, id, "status.json");
            return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null;
        }
        private static void Save() => File.WriteAllText(Path.Combine(Folder, "status.json"), job.ToString());

        [McpTool("capture_game_view_sequence", "Record a bounded composited Game View MP4. Arm in Edit Mode before run_tests with an exact Debug.Log marker, or capture immediately in unpaused Play Mode. Poll get_game_view_sequence for the clip, one preview and frame timing.",
            InputSchemaJson = @"{""type"":""object"",""properties"":{
                ""marker"":{""type"":""string""},""frames"":{""type"":""integer"",""minimum"":2,""maximum"":120},
                ""fps"":{""type"":""integer"",""minimum"":1,""maximum"":60},""pre_frames"":{""type"":""integer"",""minimum"":0,""maximum"":8},
                ""clock"":{""type"":""string"",""enum"":[""game"",""unscaled""],""description"":""Game time by default, unscaled for UI motion independent of simulation speed.""},
                ""quality"":{""type"":""string"",""enum"":[""standard"",""high""],""default"":""high"",""description"":""High preserves fine UI detail at a larger file size; standard uses a lower target bitrate. Both are lossy native H.264.""},
                ""capture_id"":{""type"":""string""}}}")]
        public static JObject Capture(JObject args)
        {
            var qualityToken = args["quality"];
            if (qualityToken != null && (qualityToken.Type != JTokenType.String ||
                ((string)qualityToken != "standard" && (string)qualityToken != "high")))
                return McpError.Create(McpErrorCodes.InvalidParameter, "quality must be standard or high.");
            string quality = (string)qualityToken ?? "high";
            int frames = (int?)args["frames"] ?? 25, fps = (int?)args["fps"] ?? 25, pre = (int?)args["pre_frames"] ?? 0;
            string marker = (string)args["marker"] ?? "";
            string clock = (string)args["clock"] ?? "game";
            if (clock != "game" && clock != "unscaled") return McpError.Create(McpErrorCodes.InvalidParameter, "clock must be game or unscaled.");
            if (frames < 2 || frames > 120 || fps < 1 || fps > 60 || pre < 0 || pre > 8 ||
                marker.Length > 160 || (pre > 0 && marker.Length == 0))
                return McpError.Create(McpErrorCodes.InvalidParameter, "Use 2–120 post-marker frames, 1–60 fps and up to 8 pre-marker frames. A pre-roll requires a marker.");
            if (!Guid.TryParse((string)args["capture_id"] ?? Guid.NewGuid().ToString(), out var guid))
                return McpError.Create(McpErrorCodes.InvalidParameter, "capture_id must be a UUID.");
            string id = guid.ToString("N");
            var previous = Read(id);
            if (previous != null)
            {
                if ((int)previous["frames"] != frames || (int)previous["fps"] != fps || (int)previous["pre_frames"] != pre ||
                    (string)previous["marker"] != marker || ((string)previous["clock"] ?? "unscaled") != clock ||
                    (string)previous["quality"] != quality)
                    return McpError.Create(McpErrorCodes.InvalidParameter, "capture_id identifies a different request.");
                return Status(new JObject { ["capture_id"] = id });
            }
            if (job != null) return McpError.Create(McpErrorCodes.InvalidState, "Another sequence is active: " + job["capture_id"]);
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || EditorApplication.isPaused || (!Application.isPlaying && marker.Length == 0))
                return McpError.Create(McpErrorCodes.InvalidState, "Use a graphical Editor, an unpaused Game View, and a marker when arming outside Play Mode.");
            Directory.CreateDirectory(Path.Combine(Root, id));
            job = new JObject { ["capture_id"] = id, ["status"] = "armed", ["marker"] = marker, ["frames"] = frames,
                ["fps"] = fps, ["pre_frames"] = pre, ["clock"] = clock, ["quality"] = quality,
                ["deadline_ms"] = DateTimeOffset.UtcNow.AddSeconds(120 + frames / (double)fps).ToUnixTimeMilliseconds() };
            SessionState.SetString(ActiveKey, id);
            Save();
            if (marker.Length == 0) Trigger();
            return (JObject)job.DeepClone();
        }

        [McpTool("get_game_view_sequence", "Check an armed or recording Game View sequence. Returns local MP4, preview JPEG and frame timing JSON paths on completion.",
            InputSchemaJson = @"{""type"":""object"",""properties"":{""capture_id"":{""type"":""string""}},""required"":[""capture_id""]}")]
        public static JObject Status(JObject args)
        {
            if (!Guid.TryParse((string)args["capture_id"], out var guid)) return McpError.Create(McpErrorCodes.InvalidParameter, "capture_id must be a UUID.");
            var saved = Read(guid.ToString("N"));
            if (saved == null) return McpError.Create(McpErrorCodes.NotFound, "Unknown sequence.");
            if ((string)saved["capture_id"] != (string)job?["capture_id"] && ((string)saved["status"] == "armed" || (string)saved["status"] == "recording"))
            {
                saved["status"] = "failed";
                saved["message"] = "The originating Editor session ended.";
            }
            return saved;
        }

        private static void OnLog(string message, string trace, LogType type)
        {
            if (job != null && !triggered && Application.isPlaying && type == LogType.Log && message == (string)job["marker"]) Trigger();
        }

        private static void Trigger()
        {
            triggered = true;
            markerTime = SampleTime;
            nextSample = 0;
            job["status"] = "recording";
            Save();
        }

        private static void Update()
        {
            if (job == null) return;
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > (long)job["deadline_ms"] || EditorApplication.isPaused)
            {
                Finish("failed", "Timed out or paused. Keep the Game View rendering and emit the exact marker during Play Mode.");
                return;
            }
            if (Application.isPlaying && host == null && (triggered || (int)job["pre_frames"] > 0))
            {
                var go = new GameObject("i1rr-unity-mcp Game View sequence") { hideFlags = HideFlags.HideAndDontSave };
                Object.DontDestroyOnLoad(go);
                host = go.AddComponent<SequenceHost>();
                host.StartCoroutine(RenderFrames());
            }
        }

        private static IEnumerator RenderFrames()
        {
            var end = new WaitForEndOfFrame();
            while (job != null)
            {
                yield return end;
                if (job == null) yield break;
                try { Sample(); }
                catch (Exception ex) { Finish("failed", ex.Message); }
            }
        }

        private static void Sample()
        {
            double now = SampleTime;
            if (now + 0.0001 < nextSample) return;
            double period = 1.0 / (int)job["fps"];
            nextSample = nextSample > 0 && now < nextSample + period ? nextSample + period : now + period;
            if (Screen.width < 2 || Screen.height < 2 || (long)Screen.width * Screen.height > 8294400)
                throw new InvalidOperationException("Use a Game View of at most 8.3 megapixels.");
            var frame = new Frame { Image = ScreenCapture.CaptureScreenshotAsTexture(), Time = now,
                UnscaledTime = Time.unscaledTimeAsDouble, Number = Time.frameCount };
            if (frame.Image == null) throw new InvalidOperationException("No composited Game View frame available.");
            // H.264 needs even dimensions. Preserve the full composited image
            // when the designer is working with a freely resized Game View.
            if ((frame.Image.width & 1) != 0 || (frame.Image.height & 1) != 0)
            {
                var native = frame.Image;
                try { frame.Image = ScreenshotCapture.Resize(native, native.width & ~1, native.height & ~1); }
                finally { Object.Destroy(native); }
            }
            if (!triggered)
            {
                before.Enqueue(frame);
                while (before.Count > (int)job["pre_frames"]) Object.Destroy(before.Dequeue().Image);
                return;
            }
            try
            {
                while (before.Count > 0)
                {
                    var old = before.Dequeue();
                    try { Encode(old); } finally { Object.Destroy(old.Image); }
                }
                Encode(frame);
                if (postCount++ == 0)
                {
                    string preview = Path.Combine(Folder, "preview.jpg");
                    File.WriteAllBytes(preview, frame.Image.EncodeToJPG(88));
                    job["preview_path"] = preview;
                }
                if (postCount == (int)job["frames"]) Finish("completed");
            }
            finally { Object.Destroy(frame.Image); }
        }

        private static void Encode(Frame frame)
        {
            if (encoder == null)
            {
                firstTime = frame.Time;
                int w = frame.Image.width, h = frame.Image.height;
                job["width"] = w; job["height"] = h;
                var attributes = VideoAttributes(w, h, (int)job["fps"], (string)job["quality"]);
                job["target_bit_rate"] = attributes.targetBitRate;
                job["video_profile"] = "H264High";
                job["gop_size"] = 60; job["b_frames"] = 0;
                encoder = new MediaEncoder(Path.Combine(Folder, "sequence.mp4"), attributes);
            }
            if (frame.Image.width != (int)job["width"] || frame.Image.height != (int)job["height"])
                throw new InvalidOperationException("Game View dimensions changed during capture.");
            double elapsed = frame.Time - firstTime;
            var time = new MediaTime((long)Math.Round(elapsed * 1000000), 1000000, 1);
            if (!encoder.AddFrame(frame.Image, time)) throw new InvalidOperationException("The video encoder rejected a Game View frame.");
            timing.Add(new JObject { ["unity_frame"] = frame.Number, ["seconds"] = elapsed,
                ["from_marker"] = frame.Time - markerTime, ["unscaled_time"] = frame.UnscaledTime });
        }

        internal static VideoTrackEncoderAttributes VideoAttributes(int width, int height, int fps, string quality)
        {
            // Native Low/High presets both lost tiny HUD detail in a 720p/60 clip.
            // Scale an explicit target with pixel throughput, bounded to 8–160 Mb/s.
            // This is a target, not a file-size cap or a lossless guarantee.
            long pixelRate = (long)width * height * fps;
            uint target = (uint)Math.Max(8000000L, Math.Min(160000000L,
                (pixelRate * (quality == "high" ? 110 : 35) + 99) / 100));
            return new VideoTrackEncoderAttributes(new H264EncoderAttributes {
                profile = VideoEncodingProfile.H264High, gopSize = 60, numConsecutiveBFrames = 0
            }) {
                width = (uint)width, height = (uint)height, frameRate = new MediaRational(fps),
                includeAlpha = false, bitRateMode = VideoBitrateMode.High, targetBitRate = target
            };
        }

        private static void Finish(string status, string message = null)
        {
            try
            {
                if (encoder != null) { encoder.Dispose(); encoder = null; }
                if (status == "completed")
                {
                    string clip = Path.Combine(Folder, "sequence.mp4");
                    if (!File.Exists(clip) || new FileInfo(clip).Length == 0) throw new IOException("Encoder produced no clip.");
                    string times = Path.Combine(Folder, "timing.json");
                    File.WriteAllText(times, timing.ToString());
                    job["clip_path"] = clip; job["timing_path"] = times;
                    job["clip_bytes"] = new FileInfo(clip).Length;
                    job["frame_count"] = timing.Count; job["marker_index"] = timing.Count - postCount;
                    job["sample_span_seconds"] = (double)timing[timing.Count - 1]["seconds"];
                }
            }
            catch (Exception ex) { status = "failed"; message = ex.Message; }
            job["status"] = status;
            job["post_frames_written"] = postCount;
            if (message != null) job["message"] = message;
            try { Save(); }
            finally { SessionState.EraseString(ActiveKey); job = null; Release(); }
        }

        private static void Release()
        {
            encoder = null;
            if (host != null) Object.Destroy(host.gameObject);
            host = null;
            while (before.Count > 0) Object.Destroy(before.Dequeue().Image);
            timing.Clear(); nextSample = 0; postCount = 0; triggered = false;
        }
    }

    internal sealed class SequenceHost : MonoBehaviour { }
}
