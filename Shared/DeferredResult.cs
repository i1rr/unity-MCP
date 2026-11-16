using System;
using Newtonsoft.Json.Linq;

namespace I1rr.UnityMcp.Shared
{
    /// <summary>
    /// Returned by deferred MCP tools to hold an HTTP/TCP connection open until a condition is met.
    /// The server checks <see cref="IsComplete"/> each frame; when true, it calls <see cref="GetResult"/>
    /// and sends the response. If <see cref="TimeoutSeconds"/> elapses first, a timeout error is sent.
    /// </summary>
    public sealed class DeferredResult
    {
        public static DeferredResult WaitForFrames(int count, float timeoutSeconds,
            Func<int> readFrame = null, Func<float> readTime = null)
        {
            readFrame ??= () => UnityEngine.Time.frameCount;
            readTime ??= () => UnityEngine.Time.realtimeSinceStartup;
            count = Math.Max(1, Math.Min(600, count));
            int startFrame = readFrame();
            float startTime = readTime();
            return new DeferredResult
            {
                TimeoutSeconds = timeoutSeconds,
                IsComplete = () => readFrame() - startFrame >= count,
                GetResult = () => new JObject
                {
                    ["frames_elapsed"] = Math.Max(0, readFrame() - startFrame),
                    ["time_elapsed_seconds"] = Math.Round(readTime() - startTime, 3)
                }
            };
        }
        /// <summary>
        /// Predicate checked each frame on the main thread. Return true when the result is ready.
        /// </summary>
        public Func<bool> IsComplete { get; set; }

        /// <summary>
        /// Produces the final tool response. Called once when <see cref="IsComplete"/> returns true.
        /// Must return the same format as a normal MCP tool (JObject with content array, or raw data).
        /// </summary>
        public Func<JObject> GetResult { get; set; }

        /// <summary>
        /// Maximum seconds to wait before sending a timeout error. Clamped to [1, 60].
        /// Default: 30 seconds.
        /// </summary>
        public float TimeoutSeconds { get; set; } = 30f;

        /// <summary>
        /// Optional cleanup action invoked when the deferred response is removed from the pending list,
        /// regardless of reason (completion, timeout, client disconnect, or error).
        /// </summary>
        public Action Cleanup { get; set; }

        /// <summary>
        /// UTC time when this deferred result was created.
        /// Set automatically by the server when the result is registered.
        /// </summary>
        public DateTime CreatedAtUtc { get; set; }

        /// <summary>
        /// The MCP session ID under which this deferred result was created.
        /// Explicitly captured at creation time — must not use thread-local storage
        /// because completion callbacks execute during later frame updates.
        /// </summary>
        public string SessionId { get; set; }

        /// <summary>
        /// Clamps <see cref="TimeoutSeconds"/> to the allowed range [1, 60].
        /// </summary>
        public float ClampedTimeout => Math.Max(1f, Math.Min(60f, TimeoutSeconds));

        /// <summary>
        /// Safely invokes the <see cref="Cleanup"/> action, swallowing exceptions.
        /// </summary>
        public void InvokeCleanup()
        {
            try { Cleanup?.Invoke(); }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[MCP] Deferred cleanup threw: {ex}");
            }
        }
    }
}
