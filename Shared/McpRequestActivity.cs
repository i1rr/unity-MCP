using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace I1rr.UnityMcp.Shared
{
    /// <summary>Bounded observation only. Eviction never affects authorization or request admission.</summary>
    public static class McpRequestActivity
    {
        public const int Capacity = 128;
        private sealed class Entry
        {
            internal JObject Data;
            internal Action Cancel;
            internal bool Finished;
        }
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>();
        private static readonly LinkedList<string> Order = new LinkedList<string>();

        private static string Label(string value) => string.IsNullOrEmpty(value) ? "" :
            new string(value.Where(c => !char.IsControl(c)).Take(100).ToArray());

        public static void Begin(McpRequestContext context, string tool = null)
        {
            if (context?.IsModern != true) return;
            lock (Gate)
            {
                if (Entries.ContainsKey(context.OperationId)) return;
                if (Entries.Count >= Capacity)
                {
                    var expired = Order.FirstOrDefault(id => Entries[id].Finished);
                    if (expired == null) return; // Full observation must never reject actual work.
                    Entries.Remove(expired); Order.Remove(expired);
                }
                var info = context.ClientInfo;
                Entries[context.OperationId] = new Entry { Data = new JObject
                {
                    ["operation_id"] = context.OperationId,
                    ["method"] = Label(context.Method), ["tool"] = Label(tool),
                    ["client_label"] = Label(info["name"]?.Type == JTokenType.String ? (string)info["name"] : null),
                    ["client_version"] = Label(info["version"]?.Type == JTokenType.String ? (string)info["version"] : null),
                    ["protocol"] = context.ProtocolVersion, ["status"] = "running",
                    ["started_at_utc"] = DateTime.UtcNow.ToString("o"), ["cancellable"] = false
                } };
                Order.AddLast(context.OperationId);
            }
        }

        public static void AttachCancellation(McpRequestContext context, Action cancel)
        {
            lock (Gate)
                if (Entries.TryGetValue(context.OperationId, out var entry) && !entry.Finished)
                { entry.Cancel = cancel; entry.Data["cancellable"] = cancel != null; }
        }

        public static bool Cancel(string operationId)
        {
            Action cancel;
            lock (Gate)
            {
                if (operationId == null || !Entries.TryGetValue(operationId, out var entry) || entry.Finished || entry.Cancel == null)
                    return false;
                cancel = entry.Cancel; entry.Cancel = null;
                entry.Data["status"] = "cancellation_requested"; entry.Data["cancellable"] = false;
            }
            cancel();
            return true;
        }

        public static void Finish(McpRequestContext context, string status)
        {
            if (context == null) return;
            lock (Gate)
                if (Entries.TryGetValue(context.OperationId, out var entry) && !entry.Finished)
                {
                    entry.Finished = true; entry.Cancel = null;
                    entry.Data["status"] = status; entry.Data["cancellable"] = false;
                    entry.Data["finished_at_utc"] = DateTime.UtcNow.ToString("o");
                }
        }

        public static JArray Snapshot()
        {
            lock (Gate) return new JArray(Order.Reverse().Select(id => Entries[id].Data.DeepClone()));
        }

        internal static void Clear() { lock (Gate) { Entries.Clear(); Order.Clear(); } }
    }
}
