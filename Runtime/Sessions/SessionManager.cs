using System;
using System.Collections.Generic;
using I1rr.UnityMcp.Shared;
using I1rr.UnityMcp.Shared.Sessions;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Scripting;

namespace I1rr.UnityMcp.Runtime.Sessions
{
    /// <summary>
    /// Runtime-side session manager that maintains active recording sessions.
    /// Ticked each frame from the runtime server's Update() callback.
    /// </summary>
    [Preserve]
    public sealed class SessionManager
    {
        public const int MaxConcurrentSessions = 4;
        public const float DefaultTimeoutSeconds = 60f;

        private static SessionManager _instance;
        public static SessionManager Instance => _instance;

        private readonly Dictionary<string, RecordingSession> _sessions = new Dictionary<string, RecordingSession>();

        public static void Initialize()
        {
            if (_instance != null) return;
            _instance = new SessionManager();
        }

        public static void Shutdown()
        {
            if (_instance == null) return;
            if (_instance._sessions.Count > 0)
            {
                Debug.Log($"[MCP Runtime] Disposing {_instance._sessions.Count} active recording session(s).");
            }
            _instance._sessions.Clear();
            _instance = null;
        }

        /// <summary>
        /// Called each frame from the server Update loop.
        /// </summary>
        public void Update()
        {
            if (_sessions.Count == 0) return;

            var provider = ProfilerRecorderService.Provider;
            if (provider == null) return;

            var expired = (List<string>)null;
            foreach (var kvp in _sessions)
            {
                if (!kvp.Value.Tick(provider))
                {
                    expired ??= new List<string>();
                    expired.Add(kvp.Key);
                }
            }

            if (expired != null)
            {
                foreach (var id in expired)
                {
                    _sessions[id].Stop();
                }
            }
        }

        public JObject StartSession(string[] counters, int intervalFrames, float durationSeconds, int maxSamples, bool intervalClamped)
        {
            if (_sessions.Count >= MaxConcurrentSessions)
            {
                var err = McpError.Create(McpErrorCodes.MaxLimitReached, "Maximum concurrent sessions reached.",
                    "Stop an existing session with stop_recording before starting a new one.");
                err["active_sessions"] = _sessions.Count;
                return err;
            }

            var id = Guid.NewGuid().ToString("N");
            var session = new RecordingSession(id, counters, intervalFrames, durationSeconds, maxSamples, intervalClamped);
            _sessions[id] = session;

            var result = new JObject
            {
                ["session_id"] = id,
                ["started_at"] = session.StartedAtUtc.ToString("O")
            };
            if (intervalClamped)
                result["clamped"] = true;

            return result;
        }

        public JObject StopSession(string sessionId)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                return McpError.Create(McpErrorCodes.NotFound, $"Session not found: {sessionId}");
            }

            session.Stop();
            var results = session.GetResults();
            _sessions.Remove(sessionId);
            return results;
        }

        public JObject GetStatus(string sessionId)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                var err = McpError.Create(McpErrorCodes.NotFound, $"Session not found: {sessionId}");
                err["active"] = false;
                return err;
            }

            var provider = ProfilerRecorderService.Provider;
            double elapsed = (DateTime.UtcNow - session.StartedAtUtc).TotalSeconds;

            return new JObject
            {
                ["active"] = !session.IsStopped,
                ["elapsed_seconds"] = Math.Round(elapsed, 3),
                ["samples_collected"] = session.SamplesCollected,
                ["preview"] = session.GetPreview(provider)
            };
        }

        public int ActiveCount => _sessions.Count;
    }
}
