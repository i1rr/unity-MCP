using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace I1rr.UnityMcp.Editor.Tests
{
    public class McpSseStreamTests
    {
        private static McpRequestContext Context() => McpProtocol.CreateModernRequestContext(McpModernProtocolTests.Request());
        private static void WaitStopped(McpStreamPool pool) =>
            Assert.That(SpinWait.SpinUntil(() => pool.Count == 0, 2000), Is.True, "Writer must release its owned slot");

        [Test] public void AckPrecedesFinalResponseAndOnlySupportedFiltersAreGranted()
        {
            var pool = new McpStreamPool();
            using var output = new MemoryStream();
            int closed = 0;
            var stream = pool.TryOpen(output, () => McpSseStream.WriteHttpHeaders(output, null),
                () => Interlocked.Increment(ref closed), () => true, Context());
            try
            {
                var request = McpModernProtocolTests.Request(McpSubscriptions.Method);
                request["params"]["notifications"] = new JObject { ["toolsListChanged"] = true, ["resourcesListChanged"] = true };
                Assert.That(McpSubscriptions.Validate(request), Is.Null);
                var ack = McpSubscriptions.Acknowledgment(stream.Context);
                Assert.That(((JObject)ack["params"]["notifications"]).Count, Is.Zero);
                Assert.That((int)ack["params"]["_meta"][McpSubscriptions.SubscriptionIdMetaKey], Is.EqualTo(1));
                Assert.That(stream.Send(ack), Is.True);
                stream.Complete(McpProtocol.BuildResponse(1, new JObject()));
                stream.Complete(McpProtocol.BuildResponse(1, new JObject()));
                WaitStopped(pool);
                string wire = Encoding.UTF8.GetString(output.ToArray());
                Assert.That(wire, Does.Contain("Content-Type: text/event-stream\r\n"));
                Assert.That(wire, Does.Not.Contain("Content-Length:"));
                Assert.That(wire, Does.Not.Contain("MCP-Session-Id:"));
                Assert.That(wire.IndexOf("subscriptions/acknowledged"), Is.LessThan(wire.IndexOf("resultType")));
                Assert.That(wire.Split(new[] { "event: message" }, StringSplitOptions.None).Length - 1, Is.EqualTo(2));
                Assert.That(closed, Is.EqualTo(1));
            }
            finally { pool.Shutdown("test cleanup"); }
        }

        [Test] public void DisconnectCancelsOnlyItsRequestAndStopsFurtherMessages()
        {
            var pool = new McpStreamPool();
            using var first = new MemoryStream();
            using var second = new MemoryStream();
            int alive = 1, closed = 0;
            var a = pool.TryOpen(first, () => { }, () => Interlocked.Increment(ref closed), () => Volatile.Read(ref alive) != 0, Context());
            var b = pool.TryOpen(second, () => { }, () => { }, () => true, Context());
            try
            {
                Volatile.Write(ref alive, 0);
                pool.Tick();
                Assert.That(a.Cancellation.IsCancellationRequested, Is.True);
                Assert.That(a.Send(new JObject()), Is.False);
                Assert.That(b.Cancellation.IsCancellationRequested, Is.False);
                b.Complete(McpProtocol.BuildResponse(1, new JObject()));
                WaitStopped(pool);
                Assert.That(closed, Is.EqualTo(1));
            }
            finally { pool.Shutdown("test cleanup"); }
        }

        private sealed class BlockedOutput : MemoryStream
        {
            internal readonly ManualResetEventSlim Entered = new ManualResetEventSlim();
            internal readonly ManualResetEventSlim Release = new ManualResetEventSlim();
            public override void Write(byte[] buffer, int offset, int count)
            {
                Entered.Set();
                if (!Release.Wait(3000)) throw new IOException("Blocked writer was not aborted");
                throw new IOException("Transport closed");
            }
        }

        [Test] public void BackpressureAbortsOneBoundedQueueWithoutBlockingAnotherWriter()
        {
            var pool = new McpStreamPool();
            using var blocked = new BlockedOutput();
            using var ordinary = new MemoryStream();
            var slow = pool.TryOpen(blocked, () => { }, () => blocked.Release.Set(), () => true, Context());
            try
            {
                Assert.That(slow.Send(new JObject()), Is.True);
                Assert.That(blocked.Entered.Wait(1000), Is.True);
                var fast = pool.TryOpen(ordinary, () => { }, () => { }, () => true, Context());
                fast.Complete(McpProtocol.BuildResponse(1, new JObject()));
                Assert.That(SpinWait.SpinUntil(() => fast.IsStopped, 1000), Is.True);
                for (int i = 0; i < 5; i++) slow.Send(new JObject());
                Assert.That(slow.Cancellation.IsCancellationRequested, Is.True);
                WaitStopped(pool);
            }
            finally { pool.Shutdown("test cleanup"); }
        }

        [Test] public void CapacityAndShutdownReleaseSlotsAndReportUnknownOutcome()
        {
            var pool = new McpStreamPool();
            var outputs = Enumerable.Range(0, McpStreamPool.MaxStreams).Select(_ => new MemoryStream()).ToArray();
            try
            {
                var streams = outputs.Select(o => pool.TryOpen(o, () => { }, () => { }, () => true, Context())).ToArray();
                Assert.That(streams.All(s => s != null), Is.True);
                Assert.That(pool.TryOpen(new MemoryStream(), () => { }, () => { }, () => true, Context()), Is.Null);
                pool.Shutdown("test shutdown");
                WaitStopped(pool);
                Assert.That(streams.All(s => s.Cancellation.IsCancellationRequested), Is.True);
                Assert.That(outputs.All(o => Encoding.UTF8.GetString(o.ToArray()).Contains("started_outcome_unknown")), Is.True);
            }
            finally { pool.Shutdown("test cleanup"); foreach (var output in outputs) output.Dispose(); }
        }

        [Test] public void CleanupIsOnceEvenWhenDisconnectAndShutdownCompete()
        {
            int calls = 0;
            var pending = new DeferredResult { Cleanup = () => Interlocked.Increment(ref calls) };
            System.Threading.Tasks.Parallel.For(0, 20, _ => pending.InvokeCleanup());
            Assert.That(calls, Is.EqualTo(1));
        }

        [TestCase("null")]
        [TestCase("{'toolsListChanged':'yes'}")]
        [TestCase("{'resourceSubscriptions':[1]}")]
        public void InvalidSubscriptionFiltersAreRejected(string json)
        {
            var request = McpModernProtocolTests.Request(McpSubscriptions.Method);
            request["params"]["notifications"] = JToken.Parse(json);
            Assert.That(McpSubscriptions.Validate(request)?["error"], Is.Not.Null);
        }
    }
}
