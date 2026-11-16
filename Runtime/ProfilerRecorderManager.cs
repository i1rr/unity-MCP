using System;
using System.Collections.Generic;
using I1rr.UnityMcp.Shared;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Scripting;

namespace I1rr.UnityMcp.Runtime
{
    /// <summary>
    /// Runtime-side singleton that maintains long-lived <see cref="ProfilerRecorder"/> instances.
    /// Lives on a dedicated <c>DontDestroyOnLoad</c> GameObject created via
    /// <c>[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]</c>.
    /// </summary>
    [Preserve]
    public sealed class ProfilerRecorderManager : MonoBehaviour, IProfilerRecorderProvider
    {
        private const int RingBufferCapacity = 300;
        private const int MaxOnDemandRecorders = 32;
        private const int InvalidationFrameThreshold = 60;
        private const float BlacklistDurationSeconds = 30f;

        private static ProfilerRecorderManager _instance;

        // Standard counters — started in Awake, kept alive until OnDestroy.
        private readonly Dictionary<string, ProfilerRecorder> _recorders = new();

        // On-demand counters with LRU eviction.
        private readonly Dictionary<string, ProfilerRecorder> _onDemandRecorders = new();
        private readonly LinkedList<string> _onDemandLru = new();
        private readonly Dictionary<string, LinkedListNode<string>> _onDemandLruMap = new();
        private readonly Dictionary<string, int> _onDemandStartFrames = new();
        // Shared scratch for the two sequential main-thread maintenance passes.
        private readonly List<string> _maintenanceNames = new(MaxOnDemandRecorders);

        // Blacklist: counter names that proved invalid → Time.unscaledTime when blacklisted.
        private readonly Dictionary<string, float> _blacklist = new();

        private static readonly (string name, ProfilerCategory category)[] StandardCounters =
        {
            ("Main Thread",          ProfilerCategory.Internal),
            ("Render Thread",        ProfilerCategory.Internal),
            ("Draw Calls Count",     ProfilerCategory.Render),
            ("Batches Count",        ProfilerCategory.Render),
            ("Triangles Count",      ProfilerCategory.Render),
            ("Vertices Count",       ProfilerCategory.Render),
            ("SetPass Calls Count",  ProfilerCategory.Render),
            ("Used Texture Memory",  ProfilerCategory.Memory),
            ("Used Mesh Memory",     ProfilerCategory.Memory),
            ("GC Allocated In Frame",ProfilerCategory.Memory),
            ("GC Used Memory",       ProfilerCategory.Memory),
            ("System Used Memory",   ProfilerCategory.Memory),
            ("Total Used Memory",    ProfilerCategory.Memory),
            ("Total Reserved Memory",ProfilerCategory.Memory),
        };

        public static ProfilerRecorderManager Instance => _instance;

        // ───── Lifecycle ─────

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        [Preserve]
        private static void AutoInitialize()
        {
            if (_instance != null) return;

            var go = new GameObject("[MCP ProfilerRecorders]");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.DontSave;
            go.AddComponent<ProfilerRecorderManager>();
        }

        [Preserve]
        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            StartStandardRecorders();
            ProfilerRecorderService.Provider = this;
        }

        [Preserve]
        private void Update()
        {
            CheckOnDemandRecorders();
        }

        [Preserve]
        private void OnDestroy()
        {
            if (_instance != this) return;

            DisposeAll();
            _instance = null;
            if (ReferenceEquals(ProfilerRecorderService.Provider, this))
                ProfilerRecorderService.Provider = null;
        }

        private void StartStandardRecorders()
        {
            foreach (var (name, category) in StandardCounters)
            {
                try
                {
                    var recorder = ProfilerRecorder.StartNew(category, name, RingBufferCapacity);
                    _recorders[name] = recorder;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[MCP Runtime] Failed to start ProfilerRecorder '{name}': {ex.Message}");
                }
            }
        }

        private void DisposeAll()
        {
            foreach (var kv in _recorders)
                kv.Value.Dispose();
            _recorders.Clear();

            foreach (var kv in _onDemandRecorders)
                kv.Value.Dispose();
            _onDemandRecorders.Clear();
            _onDemandLru.Clear();
            _onDemandLruMap.Clear();
            _onDemandStartFrames.Clear();
            _blacklist.Clear();
            _maintenanceNames.Clear();
        }

        // ───── Per-frame maintenance ─────

        private void CheckOnDemandRecorders()
        {
            float now = Time.unscaledTime;
            _maintenanceNames.Clear();

            foreach (var kv in _onDemandStartFrames)
            {
                int elapsed = Time.frameCount - kv.Value;
                if (elapsed < InvalidationFrameThreshold)
                    continue;

                if (_onDemandRecorders.TryGetValue(kv.Key, out var rec))
                {
                    if (rec.Count == 0 || !rec.Valid)
                    {
                        rec.Dispose();
                        _onDemandRecorders.Remove(kv.Key);
                        if (_onDemandLruMap.TryGetValue(kv.Key, out var node))
                        {
                            _onDemandLru.Remove(node);
                            _onDemandLruMap.Remove(kv.Key);
                        }
                        _blacklist[kv.Key] = now;
                    }
                }
                _maintenanceNames.Add(kv.Key);
            }
            foreach (var name in _maintenanceNames)
                _onDemandStartFrames.Remove(name);

            _maintenanceNames.Clear();
            foreach (var kv in _blacklist)
            {
                if (now - kv.Value > BlacklistDurationSeconds)
                    _maintenanceNames.Add(kv.Key);
            }
            foreach (var name in _maintenanceNames)
                _blacklist.Remove(name);
            _maintenanceNames.Clear();
        }

        // ───── IProfilerRecorderProvider ─────

        [Preserve]
        public List<long> GetSamples(string counterName, int count)
        {
            var recorder = FindRecorder(counterName);
            if (recorder == null || !recorder.Value.Valid)
                return new List<long>();

            var rec = recorder.Value;
            int available = rec.Count;
            int toRead = Math.Min(count, available);
            var samples = new List<long>(toRead);

            int startIndex = available - toRead;
            for (int i = startIndex; i < available; i++)
                samples.Add(rec.GetSample(i).Value);

            return samples;
        }

        [Preserve]
        public long GetLastValue(string counterName)
        {
            var recorder = FindRecorder(counterName);
            if (recorder == null || !recorder.Value.Valid)
                return 0;
            return recorder.Value.LastValue;
        }

        [Preserve]
        public bool IsCounterValid(string counterName)
        {
            var recorder = FindRecorder(counterName);
            return recorder != null && recorder.Value.Valid && recorder.Value.Count > 0;
        }

        [Preserve]
        public bool RequestOnDemandCounter(string category, string counterName)
        {
            // Already a standard counter?
            if (_recorders.TryGetValue(counterName, out var std))
                return std.Valid && std.Count > 0;

            // Already an on-demand counter?
            if (_onDemandRecorders.TryGetValue(counterName, out var existing))
            {
                TouchLru(counterName);
                return existing.Valid && existing.Count > 0;
            }

            // Blacklisted?
            if (_blacklist.ContainsKey(counterName))
                return false;

            // Evict LRU entries until under capacity.
            while (_onDemandRecorders.Count >= MaxOnDemandRecorders && _onDemandLru.Count > 0)
            {
                var oldest = _onDemandLru.First.Value;
                _onDemandLru.RemoveFirst();
                _onDemandLruMap.Remove(oldest);
                if (_onDemandRecorders.TryGetValue(oldest, out var old))
                {
                    old.Dispose();
                    _onDemandRecorders.Remove(oldest);
                }
                _onDemandStartFrames.Remove(oldest);
            }

            // Start new recorder.
            try
            {
                var profCategory = ResolveCategory(category);
                var recorder = ProfilerRecorder.StartNew(profCategory, counterName, RingBufferCapacity);
                _onDemandRecorders[counterName] = recorder;
                var lruNode = _onDemandLru.AddLast(counterName);
                _onDemandLruMap[counterName] = lruNode;
                _onDemandStartFrames[counterName] = Time.frameCount;
                return false; // just started — no data yet
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MCP Runtime] Failed to start on-demand ProfilerRecorder '{counterName}': {ex.Message}");
                _blacklist[counterName] = Time.unscaledTime;
                return false;
            }
        }

        // ───── Helpers ─────

        private ProfilerRecorder? FindRecorder(string counterName)
        {
            if (_recorders.TryGetValue(counterName, out var standard))
                return standard;
            if (_onDemandRecorders.TryGetValue(counterName, out var onDemand))
            {
                TouchLru(counterName);
                return onDemand;
            }
            return null;
        }

        private void TouchLru(string counterName)
        {
            if (_onDemandLruMap.TryGetValue(counterName, out var node))
            {
                _onDemandLru.Remove(node);
                _onDemandLru.AddLast(node);
            }
        }

        private static ProfilerCategory ResolveCategory(string name)
        {
            return name?.ToLowerInvariant() switch
            {
                "render"    => ProfilerCategory.Render,
                "scripts"   => ProfilerCategory.Scripts,
                "memory"    => ProfilerCategory.Memory,
                "physics"   => ProfilerCategory.Physics,
                "animation" => ProfilerCategory.Animation,
                "audio"     => ProfilerCategory.Audio,
                "video"     => ProfilerCategory.Video,
                "lighting"  => ProfilerCategory.Lighting,
                "network"   => ProfilerCategory.Network,
                "gui" or "ui" => ProfilerCategory.Gui,
                "vr"        => ProfilerCategory.Vr,
                "internal"  => ProfilerCategory.Internal,
                "input"     => ProfilerCategory.Input,
                "loading"   => ProfilerCategory.Loading,
                "gc"        => ProfilerCategory.Memory,
                _           => ProfilerCategory.Scripts
            };
        }
    }
}
