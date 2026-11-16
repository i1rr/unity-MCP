using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace I1rr.UnityMcp.Shared
{
    /// <summary>Project-local invocation counts, independent of sessions and profiler resets.</summary>
    public sealed class ToolUsageStore
    {
        private readonly string _path;
        private readonly object _gate = new object();
        private Dictionary<string, long> _counts = new Dictionary<string, long>(StringComparer.Ordinal);
        private bool _dirty;
        private long _revision;
        public long Revision => Interlocked.Read(ref _revision);
        public string StartedAtUtc { get; private set; } = DateTime.UtcNow.ToString("O");
        public string PersistenceError { get; private set; }

        public ToolUsageStore(string path)
        {
            _path = path;
            if (!File.Exists(path)) return;
            try { Load(path); }
            catch (Exception ex)
            {
                PersistenceError = "Usage history could not be read: " + ex.Message;
                try { Load(path + ".bak"); }
                catch { /* Keep the unreadable file; never silently replace history. */ }
            }
        }

        private void Load(string path)
        {
            var json = Newtonsoft.Json.JsonConvert.DeserializeObject<JObject>(File.ReadAllText(path),
                new Newtonsoft.Json.JsonSerializerSettings { DateParseHandling = Newtonsoft.Json.DateParseHandling.None });
            if ((int?)json["version"] != 1 || !(json["counts"] is JObject counts))
                throw new FormatException("Unsupported usage history format.");
            var loaded = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var item in counts.Properties())
            {
                if (item.Value.Type != JTokenType.Integer || (long)item.Value < 0)
                    throw new FormatException("Invalid tool count.");
                loaded[item.Name] = (long)item.Value;
            }
            StartedAtUtc = (string)json["startedAtUtc"] ?? StartedAtUtc;
            _counts = loaded;
        }

        public void Record(string toolName)
        {
            if (string.IsNullOrEmpty(toolName)) return;
            lock (_gate)
            {
                _counts.TryGetValue(toolName, out var count);
                _counts[toolName] = count == long.MaxValue ? count : count + 1;
                _dirty = true;
                Interlocked.Increment(ref _revision);
            }
        }

        public Dictionary<string, long> Snapshot()
        {
            lock (_gate) return new Dictionary<string, long>(_counts, StringComparer.Ordinal);
        }

        public void Flush()
        {
            lock (_gate)
            {
                if (!_dirty) return;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                    // Retain corrupt input for diagnosis before replacing it with recovered counts.
                    if (PersistenceError != null && File.Exists(_path))
                        File.Copy(_path, _path + ".unreadable", true);
                    var json = new JObject { ["version"] = 1, ["startedAtUtc"] = StartedAtUtc, ["counts"] = JObject.FromObject(_counts) };
                    var temporary = _path + ".tmp";
                    File.WriteAllText(temporary, json.ToString());
                    if (File.Exists(_path)) File.Replace(temporary, _path, _path + ".bak");
                    else File.Move(temporary, _path);
                    _dirty = false;
                    PersistenceError = null;
                }
                catch (Exception ex) { PersistenceError = "Usage counts are not saved: " + ex.Message; }
            }
        }
    }
}
