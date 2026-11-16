using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace I1rr.UnityMcp.Shared
{
    /// <summary>Shared screenshot implementation. Game View captures wait for Unity's final framebuffer.</summary>
    public static class ScreenshotCapture
    {
        private const int MaxDimension = 8192;
        private const long MaxPixels = 16 * 1024 * 1024;
        private static DateTime _lastCleanup = DateTime.MinValue;

        private const string CommonProperties = @"
            ""capture_source"": { ""type"": ""string"", ""enum"": [""game_view"", ""camera""], ""description"": ""Defaults to game_view (the final rendered frame including UI Toolkit and overlay canvases; requires unpaused Play Mode). camera renders only a camera and omits screen overlays. A camera_name without capture_source selects camera for compatibility."" },
            ""camera_name"": { ""type"": ""string"", ""description"": ""Exact camera name, only for camera capture."" },
            ""width"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 8192, ""description"": ""Output width. Game View is resized after capture: this does not change the game's layout or viewport. Omit for native size."" },
            ""height"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 8192, ""description"": ""Output height. If only one dimension is given for Game View, preserve aspect ratio."" },
            ""max_dimension"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 1024, ""default"": 512, ""description"": ""Maximum longest edge for inline images (not the saved file)."" }";

        public const string FileSchema = @"{ ""type"": ""object"", ""properties"": {" + CommonProperties + @",
            ""format"": { ""type"": ""string"", ""enum"": [""png"", ""jpg""], ""default"": ""png"" },
            ""supersize"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 4, ""default"": 1, ""description"": ""Render resolution multiplier, subject to the 16-megapixel limit."" },
            ""include_image"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Also return a native MCP image block so the agent can see the capture. Preview is resized to fit the response limit."" }
        }}";
        public const string Base64Schema = @"{ ""type"": ""object"", ""properties"": {" + CommonProperties + "}}";

        private static string DirectoryPath => Path.Combine(Application.temporaryCachePath, "mcp_screenshots");

        public static object Capture(JObject parameters, bool base64 = false)
        {
            parameters = parameters ?? new JObject();
            try
            {
                string cameraName = parameters.Value<string>("camera_name");
                string source = parameters.Value<string>("capture_source")
                    ?? (string.IsNullOrEmpty(cameraName) ? "game_view" : "camera");
                if (source != "game_view" && source != "camera")
                    return Error(McpErrorCodes.InvalidParameter, "capture_source must be game_view or camera.");
                if (source == "game_view" && !string.IsNullOrEmpty(cameraName))
                    return Error(McpErrorCodes.InvalidParameter, "camera_name cannot be combined with game_view.");

                int width = parameters.Value<int?>("width") ?? 0;
                int height = parameters.Value<int?>("height") ?? 0;
                int supersize = base64 ? 1 : parameters.Value<int?>("supersize") ?? 1;
                int maxDim = parameters.Value<int?>("max_dimension") ?? 512;
                string format = base64 ? "png" : parameters.Value<string>("format") ?? "png";
                bool includeImage = !base64 && (parameters.Value<bool?>("include_image") ?? false);
                if ((parameters["width"] != null && width <= 0) || (parameters["height"] != null && height <= 0) ||
                    width > MaxDimension || height > MaxDimension ||
                    supersize < 1 || supersize > 4 || maxDim < 1 || maxDim > 1024 ||
                    (format != "png" && format != "jpg"))
                    return Error(McpErrorCodes.InvalidParameter, "Invalid screenshot dimensions, format, supersize, or max_dimension.");

                if (source == "game_view" && !Application.isPlaying)
                    return Error(McpErrorCodes.InvalidState, "Game View capture requires Play Mode. Enter Play Mode, or explicitly use capture_source=camera for a camera-only render.");
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
                if (source == "game_view" && !HasVisiblePlayerWindow())
                    return Error(McpErrorCodes.InvalidState, "Show or restore the Windows player window before capturing its screen. Use capture_source=camera for an explicit camera-only render.");
#endif
#if UNITY_EDITOR
                if (source == "game_view" && UnityEditor.EditorApplication.isPaused)
                    return Error(McpErrorCodes.InvalidState, "Game View capture requires an unpaused rendered frame. Resume Play Mode and retry.");
#endif
                if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                    return Error(McpErrorCodes.InvalidState, "Screenshots require a graphics device; -nographics is not supported.");

                Camera camera = null;
                int nativeWidth = Screen.width;
                int nativeHeight = Screen.height;
                if (source == "camera")
                {
                    camera = ResolveCamera(cameraName);
                    if (camera == null)
                        return Error(McpErrorCodes.NotFound, "No matching camera found. Provide camera_name explicitly.");
                    nativeWidth = width > 0 ? width : camera.pixelWidth;
                    nativeHeight = height > 0 ? height : camera.pixelHeight;
                }

                ValidateSize((long)nativeWidth * supersize, (long)nativeHeight * supersize);
                Directory.CreateDirectory(DirectoryPath);
                CleanupOldFiles();
                if (source == "camera")
                {
                    Texture2D texture = null;
                    try
                    {
                        texture = RenderCamera(camera, nativeWidth * supersize, nativeHeight * supersize);
                        return Finish(texture, source, camera.name, format, base64, includeImage, maxDim);
                    }
                    finally { DestroyTexture(texture); }
                }

                // A suspended Game View may not render before the deadline. Own
                // the end-of-frame request so cleanup can stop it before it runs.
                var owner = new GameObject("__I1rrMcpScreenshotCapture") { hideFlags = HideFlags.HideAndDontSave };
                UnityEngine.Object.DontDestroyOnLoad(owner);
                var capture = owner.AddComponent<GameViewFrameCapture>();
                capture.StartCoroutine(capture.Capture(supersize));
                var requestedAt = DateTime.UtcNow;
                bool cleaned = false;
                return new DeferredResult
                {
                    TimeoutSeconds = 15,
                    IsComplete = () => cleaned || !Application.isPlaying || capture == null || capture.Complete ||
                        (DateTime.UtcNow - requestedAt).TotalSeconds >= 10,
                    GetResult = () =>
                    {
                        if (capture != null && !string.IsNullOrEmpty(capture.Error))
                            return Error(McpErrorCodes.InternalError, capture.Error);
                        var texture = capture != null ? capture.Frame : null;
                        if (texture == null)
                            return Error(McpErrorCodes.Timeout,
                                "The Game View did not render a completed frame before the capture ended. Keep the Game View visible and Play Mode unpaused, then retry.");
                        capture.Frame = null;
                        try
                        {
                            int outputWidth = width > 0 ? width * supersize : texture.width;
                            int outputHeight = height > 0 ? height * supersize : texture.height;
                            if (width > 0 && height == 0)
                                outputHeight = Math.Max(1, (int)((long)texture.height * outputWidth / texture.width));
                            if (height > 0 && width == 0)
                                outputWidth = Math.Max(1, (int)((long)texture.width * outputHeight / texture.height));
                            ValidateSize(outputWidth, outputHeight);
                            if (outputWidth != texture.width || outputHeight != texture.height)
                            {
                                var resized = Resize(texture, outputWidth, outputHeight);
                                DestroyTexture(texture);
                                texture = resized;
                            }
                            return Finish(texture, source, null, format, base64, includeImage, maxDim);
                        }
                        catch (Exception ex) { return Error(McpErrorCodes.InternalError, ex.Message); }
                        finally { DestroyTexture(texture); }
                    },
                    Cleanup = () =>
                    {
                        if (cleaned) return;
                        cleaned = true;
                        if (capture != null) { capture.StopAllCoroutines(); capture.ReleaseFrame(); }
                        if (owner != null)
                        {
                            if (Application.isPlaying) UnityEngine.Object.Destroy(owner);
                            else UnityEngine.Object.DestroyImmediate(owner);
                        }
                    }
                };
            }
            catch (ArgumentException ex) { return Error(McpErrorCodes.InvalidParameter, ex.Message); }
            catch (OverflowException ex) { return Error(McpErrorCodes.InvalidParameter, ex.Message); }
            catch (Exception ex) { return Error(McpErrorCodes.InternalError, ex.Message); }
        }

        private static Camera ResolveCamera(string name)
        {
            if (!string.IsNullOrEmpty(name)) return Camera.allCameras.FirstOrDefault(c => c.name == name);
            return Camera.main ?? Camera.allCameras.FirstOrDefault();
        }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        // Unity's capture APIs can produce a valid all-black PNG when Windows
        // has no presentable game window, even with an active camera and frames.
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);

        private static bool HasVisiblePlayerWindow()
        {
            uint ownProcess = GetCurrentProcessId();
            IntPtr window = IntPtr.Zero;
            while ((window = FindWindowEx(IntPtr.Zero, window, "UnityWndClass", null)) != IntPtr.Zero)
            {
                GetWindowThreadProcessId(window, out uint process);
                if (process == ownProcess && IsWindowVisible(window) && !IsIconic(window)) return true;
            }
            return false;
        }
#endif

        private static Texture2D RenderCamera(Camera camera, int width, int height)
        {
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            var target = RenderTexture.GetTemporary(width, height, 24);
            try
            {
                camera.targetTexture = target;
                camera.Render();
                return ReadTexture(target, width, height);
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(target);
            }
        }

        internal static Texture2D Resize(Texture2D input, int width, int height)
        {
            var previousActive = RenderTexture.active;
            RenderTexture intermediate = null;
            try
            {
                // A single bilinear sample skips thin UI strokes at large reductions.
                // Halve in stages so each output texel integrates its source footprint.
                Texture source = input;
                while (true)
                {
                    bool reduce = source.width > width * 2 || source.height > height * 2;
                    int nextWidth = reduce ? Math.Min(source.width, Math.Max(width, (source.width + 1) / 2)) : width;
                    int nextHeight = reduce ? Math.Min(source.height, Math.Max(height, (source.height + 1) / 2)) : height;
                    var next = RenderTexture.GetTemporary(nextWidth, nextHeight, 0);
                    next.filterMode = FilterMode.Bilinear;
                    try { Graphics.Blit(source, next); }
                    catch { RenderTexture.ReleaseTemporary(next); throw; }
                    if (intermediate != null) RenderTexture.ReleaseTemporary(intermediate);
                    intermediate = next;
                    if (nextWidth == width && nextHeight == height)
                        return ReadTexture(next, width, height);
                    source = next;
                }
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (intermediate != null) RenderTexture.ReleaseTemporary(intermediate);
            }
        }

        private static Texture2D ReadTexture(RenderTexture target, int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                return texture;
            }
            catch { DestroyTexture(texture); throw; }
        }

        private static JObject Finish(Texture2D texture, string source, string camera, string format,
            bool base64, bool includeImage, int maxDim)
        {
            var metadata = new JObject
            {
                ["success"] = true, ["capture_source"] = source,
                ["includes_screen_overlays"] = source == "game_view",
                ["width"] = texture.width, ["height"] = texture.height,
                ["format"] = format, ["camera"] = camera,
                ["captured_at_utc"] = DateTime.UtcNow.ToString("O")
            };
            if (!base64)
            {
                byte[] bytes = format == "jpg" ? texture.EncodeToJPG(85) : texture.EncodeToPNG();
                string path = NewPath("screenshot", format);
                File.WriteAllBytes(path, bytes);
                metadata["path"] = path;
                metadata["file_size_bytes"] = bytes.Length;
                metadata["note"] = "File auto-deletes after 1 hour. Game View output resizing does not change UI layout.";
                if (!includeImage) return metadata;
            }

            // Keep the entire encoded response below the transport's existing size limit.
            // Native image blocks let MCP clients display the preview without parsing JSON base64.
            int edge = Math.Min(maxDim, Math.Max(texture.width, texture.height));
            while (true)
            {
                float scale = (float)edge / Math.Max(texture.width, texture.height);
                int width = Math.Max(1, (int)(texture.width * scale));
                int height = Math.Max(1, (int)(texture.height * scale));
                Texture2D preview = null;
                JObject result;
                try
                {
                    preview = Resize(texture, width, height);
                    string encoded = Convert.ToBase64String(preview.EncodeToPNG());
                    if (base64)
                    {
                        result = (JObject)metadata.DeepClone();
                        result["width"] = width;
                        result["height"] = height;
                        result["base64"] = encoded;
                    }
                    else
                    {
                        metadata["preview_width"] = width;
                        metadata["preview_height"] = height;
                        result = new JObject { ["content"] = new JArray(
                            new JObject { ["type"] = "text", ["text"] = metadata.ToString(Formatting.None) },
                            new JObject { ["type"] = "image", ["mimeType"] = "image/png", ["data"] = encoded }) };
                    }
                }
                finally { DestroyTexture(preview); }
                int limit = ResponseSizeLimiter.MaxResponseSizeBytes;
                // Legacy raw JSON is later escaped into a text content block by the protocol.
                int responseSize = result.ToString(Formatting.None).Length;
                if (base64) responseSize += 512;
                if (limit <= 0 || responseSize <= limit) return result;
                if (edge <= 1)
                    return Error(McpErrorCodes.InvalidState, "Response size limit is too small for an inline screenshot. Request a file without include_image.");
                edge = Math.Max(1, edge * 3 / 4);
            }
        }

        private static void ValidateSize(long width, long height)
        {
            if (width < 1 || height < 1 || width > MaxDimension || height > MaxDimension || width * height > MaxPixels)
                throw new ArgumentException("Screenshot exceeds the 8192-pixel edge or 16-megapixel limit, or the viewport has no size.");
        }

        private static string NewPath(string prefix, string extension) =>
            Path.Combine(DirectoryPath, $"{prefix}_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.{extension}");

        private static void CleanupOldFiles()
        {
            var now = DateTime.UtcNow;
            if ((now - _lastCleanup).TotalMinutes < 10) return;
            _lastCleanup = now;
            try
            {
                var files = Directory.GetFiles(DirectoryPath).Select(p => new FileInfo(p))
                    .OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
                for (int i = 0; i < files.Length; i++)
                    if ((now - files[i].LastWriteTimeUtc).TotalHours > 1 ||
                        (i >= 50 && !files[i].Name.StartsWith("pending_", StringComparison.Ordinal)))
                        try { files[i].Delete(); } catch (IOException) { }
            }
            catch (IOException) { /* Best-effort cleanup must not block a new capture. */ }
        }

        private static void DestroyTexture(Texture2D texture)
        {
            if (texture == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(texture);
            else UnityEngine.Object.DestroyImmediate(texture);
        }

        private static JObject Error(string code, string message)
        {
            var error = McpError.Create(code, message);
            error["success"] = false;
            error["content"] = new JArray(new JObject
            {
                ["type"] = "text", ["text"] = error.ToString(Formatting.None)
            });
            error["isError"] = true;
            return error;
        }
    }
}
