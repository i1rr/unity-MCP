using System;
using System.Threading;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace I1rr.UnityMcp.Editor.Tests
{
    public class McpRequestContextTests
    {
        private Func<JObject, string, JToken> _previousCall;

        [SetUp] public void SetUp()
        {
            _previousCall = McpProtocol.OnToolsCall;
            McpProtocol.Reset();
        }

        [TearDown] public void TearDown()
        {
            McpProtocol.OnToolsCall = _previousCall;
            McpProtocol.Reset();
        }

        private static JObject Call(string id = "probe") => new JObject
        {
            ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "tools/call",
            ["params"] = new JObject { ["name"] = "probe", ["arguments"] = new JObject() }
        };

        private static string Initialize(string version, JObject capabilities = null)
        {
            var init = new JObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize",
                ["params"] = new JObject
                {
                    ["protocolVersion"] = version, ["capabilities"] = capabilities ?? new JObject(),
                    ["clientInfo"] = new JObject { ["name"] = "untrusted-label", ["version"] = "1" }
                }
            };
            McpProtocol.ProcessRequest(init, null, out var session,
                new McpClientTransportInfo { UserAgent = "original-agent", RemoteAddress = "127.0.0.1" });
            McpProtocol.ProcessRequest(new JObject
                { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, session, out _);
            return session;
        }

        [TestCase("2025-03-26")]
        [TestCase("2025-11-25")]
        public void LegacyWireEnvelopeAndCallbackSessionRemainCompatible(string version)
        {
            var session = Initialize(version);
            int calls = 0;
            McpProtocol.OnToolsCall = (_, actualSession) =>
            {
                Assert.That(actualSession, Is.EqualTo(session));
                calls++;
                return new JObject { ["ok"] = true };
            };
            var response = McpProtocol.ProcessRequest(Call(), session, out var newSession);
            var wire = JObject.Parse(response.ToString(Formatting.None));
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(newSession, Is.Null);
            Assert.That((string)wire["jsonrpc"], Is.EqualTo("2.0"));
            Assert.That((string)wire["id"], Is.EqualTo("probe"));
            Assert.That((string)wire["result"]["content"][0]["text"], Is.EqualTo("{\"ok\":true}"));
            Assert.That(wire["result"]["resultType"], Is.Null, "Legacy envelopes must not change.");
            Assert.That(wire["result"]["_meta"], Is.Null);
        }

        [Test]
        public void CapturedMetadataCannotBeRewrittenBySessionsOrReturnedJson()
        {
            var caps = new JObject { ["roots"] = new JObject { ["listChanged"] = true } };
            var session = Initialize(McpProtocol.ProtocolVersionNext, caps);
            var request = Call();
            var context = McpProtocol.CreateLegacyRequestContext(request, session);
            var deferred = new DeferredResult { SessionId = session, RequestContext = context };
            caps["roots"]["listChanged"] = false;
            context.ClientCapabilities["roots"]["listChanged"] = false;
            context.ClientInfo["name"] = "changed";
            ((JValue)context.RequestId).Value = "changed";
            context.TransportInfo.UserAgent = "changed";
            McpProtocol.Reset();
            McpProtocol.RestoreSession("other", McpProtocol.ProtocolVersionBaseline);
            Assert.That(deferred.RequestContext.ProtocolVersion, Is.EqualTo("2025-11-25"));
            Assert.That(deferred.RequestContext.LegacySessionId, Is.EqualTo(session));
            Assert.That((bool)context.ClientCapabilities["roots"]["listChanged"], Is.True);
            Assert.That((string)context.ClientInfo["name"], Is.EqualTo("untrusted-label"));
            Assert.That((string)context.RequestId, Is.EqualTo("probe"));
            Assert.That(context.TransportInfo.UserAgent, Is.EqualTo("original-agent"));
            Assert.That(context.AuthenticatedPrincipal, Is.Null, "A client name is not authority.");
        }

        [Test]
        public void NegotiatedCapabilitiesSurviveLegacyReloadSerialization()
        {
            var session = Initialize(McpProtocol.ProtocolVersionNext,
                new JObject { ["sampling"] = new JObject() });
            var saved = JObject.Parse(McpProtocol.SerializeSession(McpProtocol.GetSession(session)).ToString());
            McpProtocol.Reset();
            McpProtocol.RestoreSession(saved);
            var context = McpProtocol.CreateLegacyRequestContext(Call(), session);
            Assert.That(context.ClientCapabilities["sampling"], Is.TypeOf<JObject>());
            Assert.That(context.IsInitialized, Is.True);
            Assert.That(context.ProtocolVersion, Is.EqualTo("2025-11-25"));
        }

        [TestCase("2025-03-26")]
        [TestCase("2025-11-25")]
        public void SharedAdmissionRejectsUninitializedCallsBeforeToolSideEffects(string version)
        {
            McpProtocol.RestoreSession("pending", version, false);
            int calls = 0;
            McpProtocol.OnToolsCall = (_, __) => { calls++; return new JObject(); };
            var request = Call();
            var context = McpProtocol.CreateLegacyRequestContext(request, "pending");
            Assert.That((int)McpProtocol.ValidateRequestForDispatch(request, context)["error"]["code"],
                Is.EqualTo(McpProtocol.ErrorServerNotInitialized));
            var response = McpProtocol.ProcessRequestWithContext(request, context, out _);
            Assert.That((string)response["error"]["message"], Is.EqualTo("Server not initialized"));
            Assert.That(calls, Is.Zero);
        }

        [Test]
        public void ContextCannotBeReusedForAnotherRequestAndCancellationRemainsLive()
        {
            var session = Initialize(McpProtocol.ProtocolVersionBaseline);
            using var cancellation = new CancellationTokenSource();
            var request = Call();
            var context = McpProtocol.CreateLegacyRequestContext(request, session,
                authenticatedPrincipal: "host-established", cancellation: cancellation.Token);
            int calls = 0;
            McpProtocol.OnToolsCall = (_, __) => { calls++; return new JObject(); };
            var other = Call("other");
            var response = McpProtocol.ProcessRequestWithContext(other, context, out _);
            Assert.That((int)response["error"]["code"], Is.EqualTo(McpProtocol.ErrorInvalidRequest));
            Assert.That(calls, Is.Zero);
            cancellation.Cancel();
            Assert.That(context.Cancellation.IsCancellationRequested, Is.True);
            Assert.That(context.AuthenticatedPrincipal, Is.EqualTo("host-established"));
        }
    }
}
