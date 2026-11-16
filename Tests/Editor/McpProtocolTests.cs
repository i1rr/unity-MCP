using System;
using System.Collections.Generic;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace I1rr.UnityMcp.Editor.Tests
{
    /// <summary>
    /// Tests for <see cref="McpProtocol"/>: request validation, session management,
    /// error code responses, protocol negotiation, batch processing, and safe capacity handling.
    /// </summary>
    public class McpProtocolTests
    {
        private int _originalMaxClients;
        private int _originalPruneThresholdMinutes;
        private int _originalAnonymousReclaimGraceSeconds;

        [SetUp]
        public void SetUp()
        {
            _originalMaxClients = McpProtocol.MaxRetainedSessions;
            _originalPruneThresholdMinutes = McpProtocol.SessionPruneIdleThresholdMinutes;
            _originalAnonymousReclaimGraceSeconds = McpProtocol.AnonymousSessionReclaimGraceSeconds;
            McpProtocol.Reset();
            McpProtocol.MaxRetainedSessions = 64;
            McpProtocol.SessionPruneIdleThresholdMinutes = 60;
            McpProtocol.AnonymousSessionReclaimGraceSeconds = 15;
        }

        [TearDown]
        public void TearDown()
        {
            McpProtocol.MaxRetainedSessions = _originalMaxClients;
            McpProtocol.SessionPruneIdleThresholdMinutes = _originalPruneThresholdMinutes;
            McpProtocol.AnonymousSessionReclaimGraceSeconds = _originalAnonymousReclaimGraceSeconds;
            McpProtocol.Reset();
        }

        #region ParseRequest

        [Test]
        public void ParseRequest_NullInput_ReturnsParseError()
        {
            var (requests, error) = McpProtocol.ParseRequest(null);
            Assert.That(error, Is.Not.Null);
            Assert.That(error["error"]["code"].Value<int>(), Is.EqualTo(McpProtocol.ErrorParseError));
        }

        [Test]
        public void ParseRequest_EmptyString_ReturnsParseError()
        {
            var (requests, error) = McpProtocol.ParseRequest("");
            Assert.That(error, Is.Not.Null);
            Assert.That(error["error"]["code"].Value<int>(), Is.EqualTo(McpProtocol.ErrorParseError));
        }

        [Test]
        public void ParseRequest_WhitespaceOnly_ReturnsParseError()
        {
            var (requests, error) = McpProtocol.ParseRequest("   ");
            Assert.That(error, Is.Not.Null);
            Assert.That(error["error"]["code"].Value<int>(), Is.EqualTo(McpProtocol.ErrorParseError));
        }

        [Test]
        public void ParseRequest_InvalidJson_ReturnsParseError()
        {
            var (requests, error) = McpProtocol.ParseRequest("{not valid json}}}");
            Assert.That(error, Is.Not.Null);
            Assert.That(error["error"]["code"].Value<int>(), Is.EqualTo(McpProtocol.ErrorParseError));
        }

        [Test]
        public void ParseRequest_ValidSingleObject_ReturnsList()
        {
            var json = @"{""jsonrpc"":""2.0"",""method"":""ping"",""id"":1}";
            var (requests, error) = McpProtocol.ParseRequest(json);
            Assert.That(error, Is.Null);
            Assert.That(requests, Is.Not.Null);
            Assert.That(requests.Count, Is.EqualTo(1));
            Assert.That(requests[0]["method"].Value<string>(), Is.EqualTo("ping"));
        }

        [Test]
        public void ParseRequest_EmptyBatchArray_ReturnsInvalidRequest()
        {
            var (requests, error) = McpProtocol.ParseRequest("[]");
            Assert.That(error, Is.Not.Null);
            Assert.That(error["error"]["code"].Value<int>(), Is.EqualTo(McpProtocol.ErrorInvalidRequest));
        }

        [Test]
        public void ParseRequest_BatchWithNonObject_ReturnsInvalidRequest()
        {
            var (requests, error) = McpProtocol.ParseRequest(@"[1, 2, 3]");
            Assert.That(error, Is.Not.Null);
            Assert.That(error["error"]["code"].Value<int>(), Is.EqualTo(McpProtocol.ErrorInvalidRequest));
        }

        [Test]
        public void ParseRequest_ValidBatch_ReturnsList()
        {
            var json = @"[{""jsonrpc"":""2.0"",""method"":""ping"",""id"":1},{""jsonrpc"":""2.0"",""method"":""ping"",""id"":2}]";
            var (requests, error) = McpProtocol.ParseRequest(json);
            Assert.That(error, Is.Null);
            Assert.That(requests, Is.Not.Null);
            Assert.That(requests.Count, Is.EqualTo(2));
        }

        [Test]
        public void ParseRequest_PrimitiveJson_ReturnsInvalidRequest()
        {
            var (requests, error) = McpProtocol.ParseRequest("42");
            Assert.That(error, Is.Not.Null);
            Assert.That(error["error"]["code"].Value<int>(), Is.EqualTo(McpProtocol.ErrorInvalidRequest));
        }

        [Test]
        public void ParseRequest_BatchRejectedUnderNextProtocol_WhenSessionUsesNext()
        {
            // Create a session with next protocol version
            McpProtocol.RestoreSession("test-session", McpProtocol.ProtocolVersionNext, true);

            var json = @"[{""jsonrpc"":""2.0"",""method"":""ping"",""id"":1}]";
            var (requests, error) = McpProtocol.ParseRequest(json, "test-session");
            Assert.That(error, Is.Not.Null);
            Assert.That(error["error"]["message"].Value<string>(), Does.Contain("Batch requests are not supported"));
        }

        #endregion

        #region ValidateJsonRpc

        [Test]
        public void ValidateJsonRpc_MissingJsonrpc_ReturnsError()
        {
            var request = JObject.Parse(@"{""method"":""ping"",""id"":1}");
            var error = McpProtocol.ValidateJsonRpc(request);
            Assert.That(error, Is.Not.Null);
            Assert.That(error["error"]["code"].Value<int>(), Is.EqualTo(McpProtocol.ErrorInvalidRequest));
        }

        [Test]
        public void ValidateJsonRpc_WrongJsonrpcVersion_ReturnsError()
        {
            var request = JObject.Parse(@"{""jsonrpc"":""1.0"",""method"":""ping"",""id"":1}");
            var error = McpProtocol.ValidateJsonRpc(request);
            Assert.That(error, Is.Not.Null);
        }

        [Test]
        public void ValidateJsonRpc_MissingMethod_ReturnsError()
        {
            var request = JObject.Parse(@"{""jsonrpc"":""2.0"",""id"":1}");
            var error = McpProtocol.ValidateJsonRpc(request);
            Assert.That(error, Is.Not.Null);
        }

        [Test]
        public void ValidateJsonRpc_NumericMethod_ReturnsError()
        {
            var request = JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":123,""id"":1}");
            var error = McpProtocol.ValidateJsonRpc(request);
            Assert.That(error, Is.Not.Null);
        }

        [Test]
        public void ValidateJsonRpc_ParamsAsArray_IsValid()
        {
            var request = JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""ping"",""id"":1,""params"":[1,2,3]}");
            var error = McpProtocol.ValidateJsonRpc(request);
            Assert.That(error, Is.Null);
        }

        [Test]
        public void ValidateJsonRpc_ParamsAsString_ReturnsError()
        {
            var request = JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""ping"",""id"":1,""params"":""bad""}");
            var error = McpProtocol.ValidateJsonRpc(request);
            Assert.That(error, Is.Not.Null);
        }

        [Test]
        public void ValidateJsonRpc_IdAsString_IsValid()
        {
            var request = JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""ping"",""id"":""abc""}");
            var error = McpProtocol.ValidateJsonRpc(request);
            Assert.That(error, Is.Null);
        }

        [Test]
        public void ValidateJsonRpc_IdAsBoolean_ReturnsError()
        {
            var request = JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""ping"",""id"":true}");
            var error = McpProtocol.ValidateJsonRpc(request);
            Assert.That(error, Is.Not.Null);
        }

        [Test]
        public void ValidateJsonRpc_ValidRequest_ReturnsNull()
        {
            var request = JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""ping"",""id"":1}");
            var error = McpProtocol.ValidateJsonRpc(request);
            Assert.That(error, Is.Null);
        }

        #endregion

        #region IsNotification

        [Test]
        public void IsNotification_NoId_ReturnsTrue()
        {
            var request = JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""notifications/initialized""}");
            Assert.That(McpProtocol.IsNotification(request), Is.True);
        }

        [Test]
        public void IsNotification_WithId_ReturnsFalse()
        {
            var request = JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""ping"",""id"":1}");
            Assert.That(McpProtocol.IsNotification(request), Is.False);
        }

        #endregion

        #region Session Management

        [Test]
        public void SessionCount_InitiallyZero()
        {
            Assert.That(McpProtocol.SessionCount, Is.EqualTo(0));
        }

        [Test]
        public void SessionId_NullWhenNoSessions()
        {
            Assert.That(McpProtocol.SessionId, Is.Null);
        }

        [Test]
        public void IsInitialized_FalseWhenNoSessions()
        {
            Assert.That(McpProtocol.IsInitialized, Is.False);
        }

        [Test]
        public void RestoreSession_CreatesActiveSession()
        {
            McpProtocol.RestoreSession("sess-1", McpProtocol.ProtocolVersionBaseline, true);
            Assert.That(McpProtocol.SessionCount, Is.EqualTo(1));
            Assert.That(McpProtocol.IsInitialized, Is.True);
            Assert.That(McpProtocol.SessionId, Is.EqualTo("sess-1"));
        }

        [Test]
        public void RestoreSession_UninitializedSession_NotReportedAsInitialized()
        {
            McpProtocol.RestoreSession("sess-1", McpProtocol.ProtocolVersionBaseline, false);
            Assert.That(McpProtocol.IsInitialized, Is.False);
            Assert.That(McpProtocol.IsSessionInitialized("sess-1"), Is.False);
        }

        [Test]
        public void GetSession_ReturnsNullForNonexistent()
        {
            Assert.That(McpProtocol.GetSession("nonexistent"), Is.Null);
        }

        [Test]
        public void GetSession_ReturnsNullForNullId()
        {
            Assert.That(McpProtocol.GetSession(null), Is.Null);
        }

        [Test]
        public void GetSession_ReturnsExistingSession()
        {
            McpProtocol.RestoreSession("sess-1", McpProtocol.ProtocolVersionBaseline);
            var session = McpProtocol.GetSession("sess-1");
            Assert.That(session, Is.Not.Null);
            Assert.That(session.Id, Is.EqualTo("sess-1"));
        }

        [Test]
        public void GetSessionIds_ReturnsAllSessionIds()
        {
            McpProtocol.RestoreSession("a", McpProtocol.ProtocolVersionBaseline);
            McpProtocol.RestoreSession("b", McpProtocol.ProtocolVersionBaseline);
            var ids = McpProtocol.GetSessionIds();
            Assert.That(ids.Length, Is.EqualTo(2));
            Assert.That(ids, Contains.Item("a"));
            Assert.That(ids, Contains.Item("b"));
        }

        [Test]
        public void IsSessionLimitReached_FalseWhenUnderLimit()
        {
            McpProtocol.MaxRetainedSessions = 4;
            McpProtocol.RestoreSession("a", McpProtocol.ProtocolVersionBaseline);
            Assert.That(McpProtocol.IsSessionLimitReached, Is.False);
        }

        [Test]
        public void IsSessionLimitReached_TrueWhenAtLimit()
        {
            McpProtocol.MaxRetainedSessions = 2;
            McpProtocol.RestoreSession("a", McpProtocol.ProtocolVersionBaseline);
            McpProtocol.RestoreSession("b", McpProtocol.ProtocolVersionBaseline);
            Assert.That(McpProtocol.IsSessionLimitReached, Is.True);
        }

        [Test]
        public void TerminateSession_RemovesSession()
        {
            McpProtocol.RestoreSession("sess-1", McpProtocol.ProtocolVersionBaseline);
            Assert.That(McpProtocol.SessionCount, Is.EqualTo(1));

            McpProtocol.TerminateSession("sess-1");
            Assert.That(McpProtocol.SessionCount, Is.EqualTo(0));
            Assert.That(McpProtocol.GetSession("sess-1"), Is.Null);
        }

        [Test]
        public void TerminateSession_NullId_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => McpProtocol.TerminateSession(null));
        }

        [Test]
        public void Reset_ClearsAllSessions()
        {
            McpProtocol.RestoreSession("a", McpProtocol.ProtocolVersionBaseline);
            McpProtocol.RestoreSession("b", McpProtocol.ProtocolVersionBaseline);
            McpProtocol.Reset();
            Assert.That(McpProtocol.SessionCount, Is.EqualTo(0));
        }

        #endregion

        #region ValidateSessionHeader

        [Test]
        public void ValidateSessionHeader_NullHeader_ReturnsOk()
        {
            var error = McpProtocol.ValidateSessionHeader(null, out int status, out string matched);
            Assert.That(error, Is.Null);
            Assert.That(status, Is.EqualTo(200));
            Assert.That(matched, Is.Null);
        }

        [Test]
        public void ValidateSessionHeader_ValidSessionId_ReturnsOk()
        {
            McpProtocol.RestoreSession("sess-1", McpProtocol.ProtocolVersionBaseline);
            var error = McpProtocol.ValidateSessionHeader("sess-1", out int status, out string matched);
            Assert.That(error, Is.Null);
            Assert.That(status, Is.EqualTo(200));
            Assert.That(matched, Is.EqualTo("sess-1"));
        }

        [Test]
        public void ValidateSessionHeader_InvalidSessionId_Returns404()
        {
            var error = McpProtocol.ValidateSessionHeader("nonexistent", out int status, out string matched);
            Assert.That(error, Is.Not.Null);
            Assert.That(status, Is.EqualTo(404));
            Assert.That(matched, Is.Null);
        }

        #endregion

        #region ValidateProtocolVersionHeader

        [Test]
        public void ValidateProtocolVersionHeader_Null_ReturnsNull()
        {
            Assert.That(McpProtocol.ValidateProtocolVersionHeader(null), Is.Null);
        }

        [Test]
        public void ValidateProtocolVersionHeader_Baseline_ReturnsNull()
        {
            Assert.That(McpProtocol.ValidateProtocolVersionHeader(McpProtocol.ProtocolVersionBaseline), Is.Null);
        }

        [Test]
        public void ValidateProtocolVersionHeader_Next_ReturnsNull()
        {
            Assert.That(McpProtocol.ValidateProtocolVersionHeader(McpProtocol.ProtocolVersionNext), Is.Null);
        }

        [Test]
        public void ValidateProtocolVersionHeader_Unsupported_ReturnsError()
        {
            var error = McpProtocol.ValidateProtocolVersionHeader("9999-01-01");
            Assert.That(error, Is.Not.Null);
            Assert.That(error, Does.Contain("Unsupported"));
        }

        #endregion

        #region ProcessRequest

        [Test]
        public void ProcessRequest_Initialize_CreatesNewSession()
        {
            var request = JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""initialize"",""id"":1,""params"":{""protocolVersion"":""2025-03-26""}}");
            var response = McpProtocol.ProcessRequest(request, null, out string newSessionId);

            Assert.That(newSessionId, Is.Not.Null);
            Assert.That(response["result"], Is.Not.Null);
            Assert.That(response["result"]["protocolVersion"].Value<string>(), Is.EqualTo(McpProtocol.ProtocolVersionBaseline));
            Assert.That(McpProtocol.SessionCount, Is.EqualTo(1));
        }

        [Test]
        public void ProcessRequest_Initialize_CapturesClientIdentityAndTransportDetails()
        {
            var request = JObject.Parse(@"{
                ""jsonrpc"":""2.0"",""method"":""initialize"",""id"":1,
                ""params"":{
                    ""protocolVersion"":""2025-03-26"",
                    ""clientInfo"":{""name"":""qa-runner"",""title"":""QA Runner"",""version"":""2.4.1""},
                    ""_meta"":{""i1rr-unity-mcp/client"":{
                        ""instanceId"":""worker-a"",""instanceName"":""QA Worker"",
                        ""kind"":""automation"",""runId"":""run-42""
                    }}
                }}");

            McpProtocol.ProcessRequest(request, null, out var sessionId, new McpClientTransportInfo
            {
                UserAgent = "QA Runner/2.4.1",
                RemoteAddress = "127.0.0.1"
            });

            var session = McpProtocol.GetSession(sessionId);
            Assert.That(session.ClientName, Is.EqualTo("qa-runner"));
            Assert.That(session.DisplayName, Is.EqualTo("QA Runner"));
            Assert.That(session.ClientKind, Is.EqualTo("automation"));
            Assert.That(session.ClientInstanceId, Is.EqualTo("worker-a"));
            Assert.That(session.ClientInstanceName, Is.EqualTo("QA Worker"));
            Assert.That(session.RunId, Is.EqualTo("run-42"));
            Assert.That(session.UserAgent, Is.EqualTo("QA Runner/2.4.1"));
            Assert.That(session.RemoteAddress, Is.EqualTo("127.0.0.1"));
            Assert.That(session.RequestCount, Is.EqualTo(1));
            Assert.That(session.LastMethod, Is.EqualTo(McpProtocol.MethodInitialize));
        }

        [Test]
        public void ProcessRequest_Initialize_NumbersConcurrentInstancesOfSameApp()
        {
            var request = JObject.Parse(@"{
                ""jsonrpc"":""2.0"",""method"":""initialize"",""id"":1,
                ""params"":{""clientInfo"":{""name"":""codex-mcp-client"",""title"":""OpenAI Codex"",""version"":""1.0""}}}");

            McpProtocol.ProcessRequest(request, null, out var firstId);
            McpProtocol.ProcessRequest(request, null, out var secondId);

            Assert.That(McpProtocol.GetSession(firstId).DisplayName, Is.EqualTo("Codex"));
            Assert.That(McpProtocol.GetSession(firstId).AppInstanceNumber, Is.EqualTo(1));
            Assert.That(McpProtocol.GetSession(secondId).AppInstanceNumber, Is.EqualTo(2));
            Assert.That(firstId, Is.Not.EqualTo(secondId));
        }

        [Test]
        public void ProcessRequest_Initialize_StableInstanceReconnectReplacesOnlyItsPreviousSession()
        {
            McpProtocol.MaxRetainedSessions = 1;
            var request = JObject.Parse(@"{
                ""jsonrpc"":""2.0"",""method"":""initialize"",""id"":1,
                ""params"":{
                    ""clientInfo"":{""name"":""qa-runner"",""title"":""QA Runner"",""version"":""1.0""},
                    ""_meta"":{""i1rr-unity-mcp/client"":{""instanceId"":""worker-a"",""kind"":""automation""}}
                }}");

            McpProtocol.ProcessRequest(request, null, out var firstId);
            var firstNumber = McpProtocol.GetSession(firstId).AppInstanceNumber;
            McpProtocol.ProcessRequest(request, null, out var replacementId);

            Assert.That(replacementId, Is.Not.EqualTo(firstId));
            Assert.That(McpProtocol.GetSession(firstId), Is.Null);
            Assert.That(McpProtocol.GetSession(replacementId).AppInstanceNumber, Is.EqualTo(firstNumber));
            Assert.That(McpProtocol.GetSession(replacementId).RequestCount, Is.EqualTo(2));
            Assert.That(McpProtocol.SessionCount, Is.EqualTo(1));
        }

        [Test]
        public void SerializeAndRestoreSession_PreservesClientObservabilityState()
        {
            var request = JObject.Parse(@"{
                ""jsonrpc"":""2.0"",""method"":""initialize"",""id"":1,
                ""params"":{""clientInfo"":{""name"":""claude-code"",""version"":""3.0""}}}");
            McpProtocol.ProcessRequest(request, null, out var sessionId);
            var ping = JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""ping"",""id"":2}");
            McpProtocol.ProcessRequest(ping, sessionId, out _);
            var state = McpProtocol.SerializeSession(McpProtocol.GetSession(sessionId));

            McpProtocol.Reset();
            McpProtocol.RestoreSession(state);

            var restored = McpProtocol.GetSession(sessionId);
            Assert.That(restored.DisplayName, Is.EqualTo("Claude"));
            Assert.That(restored.ClientVersion, Is.EqualTo("3.0"));
            Assert.That(restored.RequestCount, Is.EqualTo(2));
            Assert.That(restored.LastMethod, Is.EqualTo(McpProtocol.MethodPing));
        }

        [Test]
        public void ProcessRequest_Initialize_AtCapacity_DoesNotEvictHealthySession()
        {
            McpProtocol.MaxRetainedSessions = 1;
            McpProtocol.RestoreSession("existing", McpProtocol.ProtocolVersionBaseline, true);

            var request = JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""initialize"",""id"":1,""params"":{}}");
            var response = McpProtocol.ProcessRequest(request, null, out string newSessionId);

            // Capacity pressure must not destabilize a recently active client.
            Assert.That(response["error"], Is.Not.Null);
            Assert.That(response["error"]["code"].Value<int>(), Is.EqualTo(McpProtocol.ErrorSessionCapacity));
            Assert.That(newSessionId, Is.Null);
            Assert.That(McpProtocol.SessionCount, Is.EqualTo(1));
            Assert.That(McpProtocol.GetSession("existing"), Is.Not.Null,
                "A healthy session must not be evicted to admit another client.");
        }

        [Test]
        public void ProcessRequest_Initialize_AtCapacity_PrunesOnlyStaleSession()
        {
            McpProtocol.MaxRetainedSessions = 2;
            McpProtocol.SessionPruneIdleThresholdMinutes = 5;
            McpProtocol.RestoreSession("old", McpProtocol.ProtocolVersionBaseline, true);
            McpProtocol.RestoreSession("recent", McpProtocol.ProtocolVersionBaseline, true);
            McpProtocol.GetSession("old").LastActivityUtc = DateTime.UtcNow.AddMinutes(-10);
            McpProtocol.GetSession("recent").LastActivityUtc = DateTime.UtcNow;

            var request = JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""initialize"",""id"":1,""params"":{}}");
            McpProtocol.ProcessRequest(request, null, out string newSessionId);

            Assert.That(McpProtocol.GetSession("old"), Is.Null, "Stale session should be pruned under pressure.");
            Assert.That(McpProtocol.GetSession("recent"), Is.Not.Null, "Recently-active session must be kept.");
            Assert.That(McpProtocol.GetSession(newSessionId), Is.Not.Null);
            Assert.That(McpProtocol.SessionCount, Is.EqualTo(2));
        }

        [Test]
        public void ProcessRequest_Initialize_AtCapacity_ReclaimsOldestAnonymousSameClientSession()
        {
            McpProtocol.MaxRetainedSessions = 2;
            McpProtocol.AnonymousSessionReclaimGraceSeconds = 10;
            var clientRequest = JObject.Parse(@"{
                ""jsonrpc"":""2.0"",""method"":""initialize"",""id"":1,
                ""params"":{""clientInfo"":{""name"":""qa-runner"",""title"":""QA Runner"",""version"":""1.0""}}}");
            var claudeRequest = JObject.Parse(@"{
                ""jsonrpc"":""2.0"",""method"":""initialize"",""id"":2,
                ""params"":{""clientInfo"":{""name"":""claude-code"",""title"":""Claude"",""version"":""1.0""}}}");
            var clientTransport = new McpClientTransportInfo
            {
                UserAgent = "QA Runner/1.0",
                RemoteAddress = "127.0.0.1"
            };

            McpProtocol.ProcessRequest(clientRequest, null, out var abandonedId, clientTransport);
            McpProtocol.ProcessRequest(claudeRequest, null, out var claudeId, new McpClientTransportInfo
            {
                UserAgent = "Claude/1.0",
                RemoteAddress = "127.0.0.1"
            });
            var abandoned = McpProtocol.GetSession(abandonedId);
            var originalOrdinal = abandoned.AppInstanceNumber;
            abandoned.LastActivityUtc = DateTime.UtcNow.AddSeconds(-20);

            var response = McpProtocol.ProcessRequest(clientRequest, null, out var replacementId,
                clientTransport);

            Assert.That(response["error"], Is.Null);
            Assert.That(McpProtocol.GetSession(abandonedId), Is.Null);
            Assert.That(McpProtocol.GetSession(claudeId), Is.Not.Null);
            Assert.That(McpProtocol.GetSession(replacementId).AppInstanceNumber, Is.EqualTo(originalOrdinal));
            Assert.That(McpProtocol.GetSession(replacementId).RequestCount, Is.EqualTo(2));
            Assert.That(McpProtocol.SessionCount, Is.EqualTo(2));
        }

        [Test]
        public void ProcessRequest_Initialize_AtCapacity_DoesNotReclaimSameClientInsideGracePeriod()
        {
            McpProtocol.MaxRetainedSessions = 1;
            McpProtocol.AnonymousSessionReclaimGraceSeconds = 10;
            var request = JObject.Parse(@"{
                ""jsonrpc"":""2.0"",""method"":""initialize"",""id"":1,
                ""params"":{""clientInfo"":{""name"":""qa-runner"",""version"":""1.0""}}}");
            var transport = new McpClientTransportInfo { UserAgent = "QA Runner/1.0" };
            McpProtocol.ProcessRequest(request, null, out var existingId, transport);
            McpProtocol.GetSession(existingId).LastActivityUtc = DateTime.UtcNow.AddSeconds(-5);

            var response = McpProtocol.ProcessRequest(request, null, out var rejectedId, transport);

            Assert.That(response["error"]["code"].Value<int>(), Is.EqualTo(McpProtocol.ErrorSessionCapacity));
            Assert.That(rejectedId, Is.Null);
            Assert.That(McpProtocol.GetSession(existingId), Is.Not.Null);
        }

        [Test]
        public void ProcessRequest_Initialize_AtCapacity_DoesNotReclaimDifferentClientFingerprint()
        {
            McpProtocol.MaxRetainedSessions = 1;
            McpProtocol.AnonymousSessionReclaimGraceSeconds = 10;
            var request = JObject.Parse(@"{
                ""jsonrpc"":""2.0"",""method"":""initialize"",""id"":1,
                ""params"":{""clientInfo"":{""name"":""qa-runner"",""version"":""1.0""}}}");
            McpProtocol.ProcessRequest(request, null, out var existingId,
                new McpClientTransportInfo { UserAgent = "QA Runner/desktop" });
            McpProtocol.GetSession(existingId).LastActivityUtc = DateTime.UtcNow.AddSeconds(-20);

            var response = McpProtocol.ProcessRequest(request, null, out var rejectedId,
                new McpClientTransportInfo { UserAgent = "QA Runner/cloud-worker" });

            Assert.That(response["error"]["code"].Value<int>(), Is.EqualTo(McpProtocol.ErrorSessionCapacity));
            Assert.That(rejectedId, Is.Null);
            Assert.That(McpProtocol.GetSession(existingId), Is.Not.Null);
        }

        [Test]
        public void ProcessRequest_Initialize_AtCapacity_NeverFamilyReclaimsStableInstanceSession()
        {
            McpProtocol.MaxRetainedSessions = 1;
            McpProtocol.AnonymousSessionReclaimGraceSeconds = 0;
            var stableRequest = JObject.Parse(@"{
                ""jsonrpc"":""2.0"",""method"":""initialize"",""id"":1,
                ""params"":{
                    ""clientInfo"":{""name"":""qa-runner"",""version"":""1.0""},
                    ""_meta"":{""i1rr-unity-mcp/client"":{""instanceId"":""worker-a""}}
                }}");
            var anonymousRequest = JObject.Parse(@"{
                ""jsonrpc"":""2.0"",""method"":""initialize"",""id"":2,
                ""params"":{""clientInfo"":{""name"":""qa-runner"",""version"":""1.0""}}}");
            var transport = new McpClientTransportInfo { UserAgent = "QA Runner/1.0" };
            McpProtocol.ProcessRequest(stableRequest, null, out var stableId, transport);

            var response = McpProtocol.ProcessRequest(anonymousRequest, null, out var rejectedId, transport);

            Assert.That(response["error"]["code"].Value<int>(), Is.EqualTo(McpProtocol.ErrorSessionCapacity));
            Assert.That(rejectedId, Is.Null);
            Assert.That(McpProtocol.GetSession(stableId), Is.Not.Null);
        }

        [Test]
        public void ProcessRequest_Ping_BeforeInit_ReturnsServerNotInitialized()
        {
            var request = JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""ping"",""id"":1}");
            var response = McpProtocol.ProcessRequest(request, null, out _);

            Assert.That(response["error"], Is.Not.Null);
            Assert.That(response["error"]["code"].Value<int>(), Is.EqualTo(McpProtocol.ErrorServerNotInitialized));
        }

        [Test]
        public void ProcessRequest_Ping_AfterInit_ReturnsPong()
        {
            McpProtocol.RestoreSession("sess-1", McpProtocol.ProtocolVersionBaseline, true);
            var request = JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""ping"",""id"":1}");
            var response = McpProtocol.ProcessRequest(request, "sess-1", out _);

            Assert.That(response["result"], Is.Not.Null);
            Assert.That(response["error"], Is.Null);
        }

        [Test]
        public void ProcessRequest_UnknownMethod_ReturnsMethodNotFound()
        {
            McpProtocol.RestoreSession("sess-1", McpProtocol.ProtocolVersionBaseline, true);
            var request = JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""unknown/method"",""id"":1}");
            var response = McpProtocol.ProcessRequest(request, "sess-1", out _);

            Assert.That(response["error"], Is.Not.Null);
            Assert.That(response["error"]["code"].Value<int>(), Is.EqualTo(McpProtocol.ErrorMethodNotFound));
        }

        [Test]
        public void ProcessRequest_Notification_ReturnsNull()
        {
            McpProtocol.RestoreSession("sess-1", McpProtocol.ProtocolVersionBaseline, true);
            var request = JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""notifications/initialized""}");
            var response = McpProtocol.ProcessRequest(request, "sess-1", out _);

            Assert.That(response, Is.Null);
        }

        [Test]
        public void ProcessRequest_UpdatesLastActivity()
        {
            McpProtocol.RestoreSession("sess-1", McpProtocol.ProtocolVersionBaseline, true);
            var session = McpProtocol.GetSession("sess-1");
            var before = session.LastActivityUtc;

            System.Threading.Thread.Sleep(10);

            var request = JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""ping"",""id"":1}");
            McpProtocol.ProcessRequest(request, "sess-1", out _);

            Assert.That(session.LastActivityUtc, Is.GreaterThan(before));
        }

        #endregion

        #region ProcessBatch

        [Test]
        public void ProcessBatch_ExecutesAllRequests()
        {
            McpProtocol.RestoreSession("sess-1", McpProtocol.ProtocolVersionBaseline, true);
            var requests = new List<JObject>
            {
                JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""ping"",""id"":1}"),
                JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""ping"",""id"":2}")
            };

            var (responses, newSessionId) = McpProtocol.ProcessBatch(requests, "sess-1");
            Assert.That(responses.Count, Is.EqualTo(2));
        }

        [Test]
        public void ProcessBatch_NotificationsExcludedFromResponses()
        {
            McpProtocol.RestoreSession("sess-1", McpProtocol.ProtocolVersionBaseline, true);
            var requests = new List<JObject>
            {
                JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""ping"",""id"":1}"),
                JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""notifications/initialized""}")
            };

            var (responses, _) = McpProtocol.ProcessBatch(requests, "sess-1");
            Assert.That(responses.Count, Is.EqualTo(1));
        }

        #endregion

        #region Protocol Negotiation

        [Test]
        public void Initialize_WithNextVersion_SetsSessionToNext()
        {
            var request = JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""initialize"",""id"":1,""params"":{""protocolVersion"":""2025-11-25""}}");
            McpProtocol.ProcessRequest(request, null, out string newSessionId);

            Assert.That(newSessionId, Is.Not.Null);
            var session = McpProtocol.GetSession(newSessionId);
            Assert.That(session.ProtocolVersion, Is.EqualTo(McpProtocol.ProtocolVersionNext));
        }

        [Test]
        public void Initialize_WithBaselineVersion_SetsSessionToBaseline()
        {
            var request = JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""initialize"",""id"":1,""params"":{""protocolVersion"":""2025-03-26""}}");
            McpProtocol.ProcessRequest(request, null, out string newSessionId);

            var session = McpProtocol.GetSession(newSessionId);
            Assert.That(session.ProtocolVersion, Is.EqualTo(McpProtocol.ProtocolVersionBaseline));
        }

        [Test]
        public void Initialize_WithUnknownVersion_DefaultsToBaseline()
        {
            var request = JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""initialize"",""id"":1,""params"":{""protocolVersion"":""9999-01-01""}}");
            McpProtocol.ProcessRequest(request, null, out string newSessionId);

            var session = McpProtocol.GetSession(newSessionId);
            Assert.That(session.ProtocolVersion, Is.EqualTo(McpProtocol.ProtocolVersionBaseline));
        }

        #endregion

        #region BuildResponse / BuildErrorResponse

        [Test]
        public void BuildResponse_IncludesJsonRpc20()
        {
            var response = McpProtocol.BuildResponse(new JValue(1), new JObject { ["ok"] = true });
            Assert.That(response["jsonrpc"].Value<string>(), Is.EqualTo("2.0"));
            Assert.That(response["id"].Value<int>(), Is.EqualTo(1));
            Assert.That(response["result"]["ok"].Value<bool>(), Is.True);
        }

        [Test]
        public void BuildResponse_NullId_SetsIdToNull()
        {
            var response = McpProtocol.BuildResponse(null, new JObject());
            Assert.That(response["id"].Type, Is.EqualTo(JTokenType.Null));
        }

        [Test]
        public void BuildErrorResponse_IncludesCodeAndMessage()
        {
            var error = McpProtocol.BuildErrorResponse(new JValue(5), -32600, "Invalid request");
            Assert.That(error["error"]["code"].Value<int>(), Is.EqualTo(-32600));
            Assert.That(error["error"]["message"].Value<string>(), Is.EqualTo("Invalid request"));
        }

        [Test]
        public void BuildErrorResponse_WithData_IncludesData()
        {
            var data = new JObject { ["detail"] = "extra info" };
            var error = McpProtocol.BuildErrorResponse(new JValue(1), -32600, "err", data);
            Assert.That(error["error"]["data"]["detail"].Value<string>(), Is.EqualTo("extra info"));
        }

        [Test]
        public void BuildToolResult_WrapsTextInContentArray()
        {
            var result = McpProtocol.BuildToolResult("hello");
            Assert.That(result["content"], Is.Not.Null);
            Assert.That(result["content"].Type, Is.EqualTo(JTokenType.Array));
            Assert.That(result["content"][0]["type"].Value<string>(), Is.EqualTo("text"));
            Assert.That(result["content"][0]["text"].Value<string>(), Is.EqualTo("hello"));
        }

        [Test]
        public void BuildToolResult_ErrorFlag_SetsIsError()
        {
            var result = McpProtocol.BuildToolResult("fail", isError: true);
            Assert.That(result["isError"].Value<bool>(), Is.True);
        }

        #endregion
    }
}
