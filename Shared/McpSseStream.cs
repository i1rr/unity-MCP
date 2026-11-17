using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace I1rr.UnityMcp.Shared
{
    /// <summary>Bounded stream ownership independent of the ordinary HTTP response writer.</summary>
    public sealed class McpStreamPool
    {
        public const int MaxStreams = 16;
        private readonly ConcurrentDictionary<int, McpSseStream> _streams = new ConcurrentDictionary<int, McpSseStream>();
        private readonly object _gate = new object();
        private int _sequence;
        public int Count => _streams.Count;

        public McpSseStream TryOpen(Stream output, Action writeHeaders, Action close, Func<bool> alive,
            McpRequestContext context, Action abort = null)
        {
            lock (_gate)
            {
                if (_streams.Count >= MaxStreams) return null;
                int key = ++_sequence;
                var stream = new McpSseStream(output, writeHeaders, close, abort ?? close, alive, context,
                    () => _streams.TryRemove(key, out _));
                _streams[key] = stream;
                stream.Start();
                return stream;
            }
        }

        public void Tick()
        {
            foreach (var stream in _streams.Values) stream.CheckLifetime();
        }

        public void Shutdown(string reason)
        {
            var streams = _streams.Values.ToArray();
            foreach (var stream in streams)
            {
                stream.CancelWork();
                stream.Complete(McpProtocol.BuildErrorResponse(stream.Context.RequestId, -32000, reason,
                    new JObject { ["dispatch_state"] = "started_outcome_unknown" }));
            }
            var deadline = Stopwatch.StartNew();
            foreach (var stream in streams) stream.Join(Math.Max(0, 250 - (int)deadline.ElapsedMilliseconds));
            foreach (var stream in streams) stream.Abort();
            foreach (var stream in streams) stream.Join(Math.Max(0, 500 - (int)deadline.ElapsedMilliseconds));
        }
    }

    /// <summary>One request, one bounded queue and writer. Tool cleanup remains on the Unity thread.</summary>
    public sealed class McpSseStream
    {
        public const int MaxFrameBytes = 2 * 1024 * 1024;
        public const int HeartbeatMilliseconds = 1000;
        private readonly BlockingCollection<byte[]> _frames = new BlockingCollection<byte[]>(4);
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private readonly Stream _output;
        private readonly Action _headers, _close, _abort, _finished;
        private readonly Func<bool> _alive;
        private readonly Thread _worker;
        private long _writingSince;
        private int _ending, _closed, _stopped, _aborted;
        public McpRequestContext Context { get; }
        public CancellationToken Cancellation => _cancel.Token;
        public bool IsStopped => Volatile.Read(ref _stopped) != 0;

        internal McpSseStream(Stream output, Action headers, Action close, Action abort, Func<bool> alive,
            McpRequestContext context, Action finished)
        {
            _output = output; _headers = headers; _close = close; _abort = abort; _alive = alive; _finished = finished;
            Context = context.WithCancellation(_cancel.Token);
            _worker = new Thread(WriteLoop) { IsBackground = true, Name = "MCP-request-stream" };
        }

        internal void Start() => _worker.Start();
        internal void Join(int milliseconds) { if (milliseconds > 0 && _worker.IsAlive) _worker.Join(milliseconds); }

        public bool Send(JObject message)
        {
            if (Volatile.Read(ref _ending) != 0 || Volatile.Read(ref _aborted) != 0) return false;
            var bytes = Encoding.UTF8.GetBytes("event: message\ndata: " + message.ToString(Formatting.None) + "\n\n");
            if (bytes.Length > MaxFrameBytes) { Abort(); return false; }
            try { if (!_frames.TryAdd(bytes)) { Abort(); return false; } }
            catch (InvalidOperationException) { return false; }
            return true;
        }

        public void Complete(JObject response)
        {
            if (Interlocked.CompareExchange(ref _ending, 1, 0) != 0 || Volatile.Read(ref _aborted) != 0) return;
            response = McpProtocol.AdaptResponseForContext(response, Context);
            var bytes = Encoding.UTF8.GetBytes("event: message\ndata: " + response.ToString(Formatting.None) + "\n\n");
            if (bytes.Length > MaxFrameBytes)
                bytes = Encoding.UTF8.GetBytes("event: message\ndata: " + McpProtocol.BuildErrorResponse(Context.RequestId,
                    McpProtocol.ErrorInternalError, "Streaming result exceeds the bounded frame limit; narrow the request").ToString(Formatting.None) + "\n\n");
            if (!_frames.TryAdd(bytes)) Abort();
            _frames.CompleteAdding();
        }

        public void Abort()
        {
            Interlocked.Exchange(ref _aborted, 1);
            CancelWork();
            CloseOnce(true);
        }

        public void CancelWork()
        {
            try { _cancel.Cancel(); } catch (AggregateException) { }
        }

        public void CheckLifetime()
        {
            if (IsStopped) return;
            try
            {
                long writing = Interlocked.Read(ref _writingSince);
                if (!_alive() || writing != 0 && Stopwatch.GetTimestamp() - writing > Stopwatch.Frequency * 2)
                    Abort();
            }
            catch { Abort(); }
        }

        private void CloseOnce(bool abort = false)
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
                try { if (abort) _abort(); else _close(); } catch { }
        }

        private void WriteLoop()
        {
            try
            {
                Interlocked.Exchange(ref _writingSince, Stopwatch.GetTimestamp());
                _headers();
                Interlocked.Exchange(ref _writingSince, 0);
                var lastWrite = Stopwatch.StartNew();
                while (Volatile.Read(ref _aborted) == 0)
                {
                    if (_frames.TryTake(out var frame, 100))
                    {
                        Interlocked.Exchange(ref _writingSince, Stopwatch.GetTimestamp());
                        _output.Write(frame, 0, frame.Length);
                        _output.Flush();
                        Interlocked.Exchange(ref _writingSince, 0);
                        lastWrite.Restart();
                    }
                    else if (_frames.IsCompleted) break;
                    else if (lastWrite.ElapsedMilliseconds >= HeartbeatMilliseconds)
                    {
                        Interlocked.Exchange(ref _writingSince, Stopwatch.GetTimestamp());
                        var heartbeat = Encoding.ASCII.GetBytes(": keepalive\n\n");
                        _output.Write(heartbeat, 0, heartbeat.Length);
                        _output.Flush();
                        Interlocked.Exchange(ref _writingSince, 0);
                        lastWrite.Restart();
                    }
                    CheckLifetime();
                }
            }
            catch { Abort(); }
            finally
            {
                CloseOnce();
                Interlocked.Exchange(ref _stopped, 1);
                _finished();
            }
        }

        public static void WriteHttpHeaders(Stream stream, IDictionary<string, string> extraHeaders)
        {
            var text = new StringBuilder("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-store\r\nConnection: close\r\nX-Accel-Buffering: no\r\n");
            if (extraHeaders != null)
                foreach (var header in extraHeaders)
                    if (!new[] { "Content-Type", "Content-Length", "Connection", "Cache-Control", "X-Accel-Buffering", "MCP-Session-Id" }
                        .Contains(header.Key, StringComparer.OrdinalIgnoreCase))
                        text.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            text.Append("\r\n");
            var bytes = Encoding.ASCII.GetBytes(text.ToString());
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
    }
}
