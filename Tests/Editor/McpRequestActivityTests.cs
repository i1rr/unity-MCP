using System;
using System.Linq;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace I1rr.UnityMcp.Editor.Tests
{
    public class McpRequestActivityTests
    {
        [SetUp] public void Before() => McpRequestActivity.Clear();
        [TearDown] public void After() => McpRequestActivity.Clear();
        private static McpRequestContext Context() => McpProtocol.CreateModernRequestContext(McpModernProtocolTests.Request());

        [Test] public void HistoryEvictionIsIndependentOfModernAdmission()
        {
            int sessions = McpProtocol.SessionCount;
            for (int i = 0; i < McpRequestActivity.Capacity + 3; i++)
            {
                var request = McpModernProtocolTests.Request();
                var context = McpProtocol.CreateModernRequestContext(request);
                Assert.That(McpProtocol.ProcessRequestWithContext(request, context, out _)["error"], Is.Null);
            }
            Assert.That(McpRequestActivity.Snapshot().Count, Is.EqualTo(McpRequestActivity.Capacity));
            Assert.That(McpProtocol.SessionCount, Is.EqualTo(sessions));
            McpRequestActivity.Clear();
            for (int i = 0; i < McpRequestActivity.Capacity; i++) McpRequestActivity.Begin(Context());
            var extra = McpModernProtocolTests.Request();
            Assert.That(McpProtocol.ProcessRequestWithContext(extra, McpProtocol.CreateModernRequestContext(extra), out _)["error"], Is.Null,
                "Even a full active observation table must not reject a modern request");
        }

        [Test] public void ServerOperationIdsSeparateReusedRpcIdsAndSpoofedLabels()
        {
            var request = McpModernProtocolTests.Request();
            request["params"]["_meta"][McpProtocol.ClientInfoMetaKey] = new JObject { ["name"] = "administrator\nspoof", ["version"] = "1" };
            var a = McpProtocol.CreateModernRequestContext(request);
            var b = McpProtocol.CreateModernRequestContext(request);
            Assert.That(a.OperationId, Is.Not.EqualTo(b.OperationId));
            Assert.That(a.WithCancellation(default).OperationId, Is.EqualTo(a.OperationId));
            Assert.That(a.AuthenticatedPrincipal, Is.Null);
            McpRequestActivity.Begin(a); McpRequestActivity.Begin(b);
            int cancelledA = 0, cancelledB = 0;
            McpRequestActivity.AttachCancellation(a, () => cancelledA++);
            McpRequestActivity.AttachCancellation(b, () => cancelledB++);
            Assert.That(McpRequestActivity.Cancel("administrator\nspoof"), Is.False);
            Assert.That(McpRequestActivity.Cancel(a.OperationId), Is.True);
            Assert.That(McpRequestActivity.Cancel(a.OperationId), Is.False);
            Assert.That(cancelledA, Is.EqualTo(1)); Assert.That(cancelledB, Is.Zero);
            var snapshot = McpRequestActivity.Snapshot();
            Assert.That(snapshot.All(item => !((string)item["client_label"]).Contains("\n")), Is.True);
            snapshot[0]["client_label"] = "changed copy";
            Assert.That(McpRequestActivity.Snapshot().All(item => (string)item["client_label"] != "changed copy"), Is.True);
        }

        [Test] public void FinishedOperationsCannotBeCancelledOrRewrittenByLateCallbacks()
        {
            var context = Context();
            McpRequestActivity.Begin(context);
            McpRequestActivity.AttachCancellation(context, () => Assert.Fail("Finished work must not invoke cancellation"));
            McpRequestActivity.Finish(context, "failed");
            McpRequestActivity.Finish(context, "completed");
            Assert.That(McpRequestActivity.Cancel(context.OperationId), Is.False);
            Assert.That((string)McpRequestActivity.Snapshot()[0]["status"], Is.EqualTo("failed"));
        }
    }
}
