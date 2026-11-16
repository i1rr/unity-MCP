using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace I1rr.UnityMcp.Editor.Tests
{
    public class EditorDispatchReadinessTests
    {
        private ConcurrentQueue<McpEditorServer.McpRequest> _inbound;
        private BlockingCollection<McpEditorServer.CompletedRequest> _outbound;
        private int _count, _port;
        private bool _auth;

        [SetUp] public void SetUp()
        {
            EditorDispatchReadiness.Stop();
            _inbound = McpEditorServer._inboundQueue;
            _outbound = McpEditorServer._outboundQueue;
            _count = McpEditorServer._inboundCount;
            _port = McpEditorServer._port;
            _auth = McpEditorServer._requireAuthentication;
            McpEditorServer._inboundQueue = new ConcurrentQueue<McpEditorServer.McpRequest>();
            McpEditorServer._outboundQueue = new BlockingCollection<McpEditorServer.CompletedRequest>();
            McpEditorServer._inboundCount = 0;
            McpEditorServer._port = 8123;
            McpEditorServer._requireAuthentication = false;
        }

        [TearDown] public void TearDown()
        {
            EditorDispatchReadiness.Stop();
            McpEditorServer._outboundQueue.Dispose();
            McpEditorServer._inboundQueue = _inbound;
            McpEditorServer._outboundQueue = _outbound;
            McpEditorServer._inboundCount = _count;
            McpEditorServer._port = _port;
            McpEditorServer._requireAuthentication = _auth;
            if (McpEditorServer.IsRunning) EditorDispatchReadiness.Start();
        }

        private static void Queue(int id, bool expired, string host = "127.0.0.1:8123")
        {
            McpEditorServer._inboundQueue.Enqueue(new McpEditorServer.McpRequest
            {
                ClientId = id,
                EnqueuedAt = Stopwatch.GetTimestamp() - (expired ? 3 * Stopwatch.Frequency : 0),
                HttpRequest = new I1rr.UnityMcp.Shared.TcpHttpHandler.HttpRequest
                {
                    Method = "POST", Path = "/mcp",
                    Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Host"] = host }
                }
            });
            McpEditorServer._inboundCount++;
        }

        [Test] public void ExpiryRemovesRequestAndExplicitlyReportsNotStarted()
        {
            Queue(1, true);
            EditorDispatchReadiness.ExpireQueuedRequests();
            Assert.That(EditorDispatchReadiness.TryTake(out _), Is.False, "Expired work must never dispatch on recovery.");
            Assert.That(McpEditorServer._inboundCount, Is.Zero);
            Assert.That(McpEditorServer._outboundQueue.TryTake(out var reply), Is.True);
            Assert.That(reply.StatusCode, Is.EqualTo(503));
            Assert.That(reply.Headers["Retry-After"], Is.EqualTo("1"));
            var body = JObject.Parse(Encoding.UTF8.GetString(reply.ResponseBytes));
            Assert.That((string)body["error_code"], Is.EqualTo("editor_not_ready"));
            Assert.That((string)body["dispatch_state"], Is.EqualTo("not_started"));
        }

        [Test] public void FreshWorkRemainsAvailableAndTakenWorkCannotExpire()
        {
            Queue(1, true); Queue(2, false);
            Assert.That(EditorDispatchReadiness.TryTake(out var started), Is.True);
            EditorDispatchReadiness.ExpireQueuedRequests();
            Assert.That(McpEditorServer._outboundQueue.Count, Is.Zero, "A taken request has an unknown/running outcome, never not_started.");
            Assert.That(EditorDispatchReadiness.TryTake(out var fresh), Is.True);
            Assert.That(fresh.ClientId, Is.EqualTo(2));
            Assert.That(EditorDispatchReadiness.HasExpired(fresh), Is.False);
        }

        [Test] public void MainThreadAndWatchdogHaveExclusiveOwnership()
        {
            for (int i = 0; i < 200; i++) Queue(i, true);
            var taken = new ConcurrentBag<int>();
            Parallel.Invoke(EditorDispatchReadiness.ExpireQueuedRequests, () =>
            {
                while (EditorDispatchReadiness.TryTake(out var request)) taken.Add(request.ClientId);
            });
            var ids = taken.Concat(McpEditorServer._outboundQueue.Select(reply => reply.ClientId)).ToArray();
            Assert.That(ids.Length, Is.EqualTo(200));
            Assert.That(ids.Distinct().Count(), Is.EqualTo(200));
            Assert.That(McpEditorServer._inboundCount, Is.Zero);
        }

        [TestCase(false, "attacker.invalid:8123", 403)]
        [TestCase(true, "127.0.0.1:8123", 401)]
        public void ExpiryPreservesAdmissionChecks(bool auth, string host, int status)
        {
            McpEditorServer._requireAuthentication = auth;
            Queue(1, true, host);
            EditorDispatchReadiness.ExpireQueuedRequests();
            Assert.That(McpEditorServer._outboundQueue.TryTake(out var reply), Is.True);
            Assert.That(reply.StatusCode, Is.EqualTo(status));
        }

        [Test] public void WatchdogExpiresWorkWithoutAnEditorUpdate()
        {
            Queue(1, true);
            EditorDispatchReadiness.Start();
            Assert.That(McpEditorServer._outboundQueue.TryTake(out var reply, 1000), Is.True);
            Assert.That(reply.StatusCode, Is.EqualTo(503));
            Assert.That(McpEditorServer._inboundQueue.IsEmpty, Is.True);
        }
    }
}
