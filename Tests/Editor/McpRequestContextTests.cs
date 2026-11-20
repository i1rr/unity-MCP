using System;
using System.Threading;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace I1rr.UnityMcp.Editor.Tests
{
    public class McpRequestContextTests
    {
        private Func<JObject, McpRequestContext, JToken> _previousCall;
        [SetUp] public void Before() => _previousCall = McpProtocol.OnToolsCall;
        [TearDown] public void After() => McpProtocol.OnToolsCall = _previousCall;

        private static JObject Call() {
            var request = McpModernProtocolTests.Request("tools/call");
            request["params"]["name"] = "probe";
            request["params"]["_meta"][McpProtocol.ClientInfoMetaKey] = new JObject { ["name"] = "client", ["version"] = "1" };
            return request;
        }

        [Test] public void CallbackReceivesCapturedPerRequestContext()
        {
            var request = Call();
            var context = McpProtocol.CreateModernRequestContext(request);
            McpProtocol.OnToolsCall = (_, actual) => {
                Assert.That(actual, Is.SameAs(context));
                return new JObject { ["ok"] = true };
            };
            var response = McpProtocol.ProcessRequestWithContext(request, context);
            Assert.That((string)response["result"]["resultType"], Is.EqualTo("complete"));
            Assert.That((string)response["result"]["content"][0]["text"], Is.EqualTo("{\"ok\":true}"));
        }

        [Test] public void DeferredMetadataCannotBeRewrittenByOtherRequestsOrReturnedJson()
        {
            var request = Call();
            request["params"]["_meta"][McpProtocol.CapabilitiesMetaKey] = new JObject { ["roots"] = true };
            var context = McpProtocol.CreateModernRequestContext(request,
                new McpClientTransportInfo { UserAgent = "original-agent", RemoteAddress = "127.0.0.1" });
            var deferred = new DeferredResult { RequestContext = context };
            context.ClientCapabilities["roots"] = false;
            context.ClientInfo["name"] = "changed";
            ((JValue)context.RequestId).Value = "changed";
            context.TransportInfo.UserAgent = "changed";
            request["params"]["_meta"][McpProtocol.ClientInfoMetaKey]["name"] = "other";
            McpProtocol.CreateModernRequestContext(request);
            Assert.That((bool)deferred.RequestContext.ClientCapabilities["roots"], Is.True);
            Assert.That((string)context.ClientInfo["name"], Is.EqualTo("client"));
            Assert.That((int)context.RequestId, Is.EqualTo(1));
            Assert.That(context.TransportInfo.UserAgent, Is.EqualTo("original-agent"));
            Assert.That(context.AuthenticatedPrincipal, Is.Null);
        }

        [Test] public void MissingMetadataIsRejectedBeforeToolSideEffects()
        {
            var request = Call();
            ((JObject)request["params"]).Remove("_meta");
            int calls = 0;
            McpProtocol.OnToolsCall = (_, __) => { calls++; return new JObject(); };
            var response = McpProtocol.ProcessRequestWithContext(request, McpProtocol.CreateModernRequestContext(request));
            Assert.That((int)response["error"]["code"], Is.EqualTo(McpProtocol.ErrorInvalidParams));
            Assert.That(calls, Is.Zero);
        }

        [Test] public void ContextCannotBeReusedAndCancellationRemainsLive()
        {
            var request = Call();
            using var cancellation = new CancellationTokenSource();
            var context = McpProtocol.CreateModernRequestContext(request,
                authenticatedPrincipal: "host-established", cancellation: cancellation.Token);
            request["id"] = "other";
            int calls = 0;
            McpProtocol.OnToolsCall = (_, __) => { calls++; return new JObject(); };
            var response = McpProtocol.ProcessRequestWithContext(request, context);
            Assert.That((int)response["error"]["code"], Is.EqualTo(McpProtocol.ErrorInvalidRequest));
            Assert.That(calls, Is.Zero);
            var replaced = context.WithCancellation(cancellation.Token);
            cancellation.Cancel();
            Assert.That(replaced.OperationId, Is.EqualTo(context.OperationId));
            Assert.That(replaced.Cancellation.IsCancellationRequested, Is.True);
            Assert.That(replaced.AuthenticatedPrincipal, Is.EqualTo("host-established"));
        }
    }
}
