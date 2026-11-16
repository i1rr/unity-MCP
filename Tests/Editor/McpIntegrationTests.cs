using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace I1rr.UnityMcp.Editor.Tests
{
    /// <summary>
    /// Integration tests exercising the full request lifecycle: HTTP request → JSON-RPC → tool execution → response.
    /// Uses <see cref="McpProtocol"/> directly rather than a live HTTP server to avoid port conflicts in CI,
    /// but exercises the full protocol pipeline including session management.
    /// </summary>
    public class McpIntegrationTests
    {
        private int _originalMaxClients;
        private Func<bool> _originalPlayMode;
        private int _originalPruneThresholdMinutes;
        private int _originalAnonymousReclaimGraceSeconds;
        private Func<JObject, string, JToken> _originalOnToolsList;
        private Func<JObject, string, JToken> _originalOnToolsCall;

        [SetUp]
        public void SetUp()
        {
            _originalPlayMode = McpToolRegistry.IsInPlayMode;
            _originalMaxClients = McpProtocol.MaxRetainedSessions;
            _originalPruneThresholdMinutes = McpProtocol.SessionPruneIdleThresholdMinutes;
            _originalAnonymousReclaimGraceSeconds = McpProtocol.AnonymousSessionReclaimGraceSeconds;
            _originalOnToolsList = McpProtocol.OnToolsList;
            _originalOnToolsCall = McpProtocol.OnToolsCall;
            McpProtocol.Reset();
            McpProtocol.MaxRetainedSessions = 4;
            McpProtocol.SessionPruneIdleThresholdMinutes = 60;
            McpProtocol.AnonymousSessionReclaimGraceSeconds = 15;

            // Wire up tool handlers as the real server does
            McpProtocol.OnToolsList = (request, sessionId) =>
            {
                var tools = McpToolRegistry.ListTools();
                return new JObject { ["tools"] = tools };
            };
            McpProtocol.OnToolsCall = (request, sessionId) =>
            {
                var paramsObj = request["params"] as JObject;
                var toolName = paramsObj?["name"]?.Value<string>();
                var arguments = paramsObj?["arguments"] as JObject;
                return McpToolRegistry.Invoke(toolName, arguments ?? new JObject());
            };
        }

        [TearDown]
        public void TearDown()
        {
            McpToolRegistry.IsInPlayMode = _originalPlayMode;
            McpProtocol.MaxRetainedSessions = _originalMaxClients;
            McpProtocol.SessionPruneIdleThresholdMinutes = _originalPruneThresholdMinutes;
            McpProtocol.AnonymousSessionReclaimGraceSeconds = _originalAnonymousReclaimGraceSeconds;
            McpProtocol.OnToolsList = _originalOnToolsList;
            McpProtocol.OnToolsCall = _originalOnToolsCall;
            McpProtocol.Reset();
        }

        #region Full Request Lifecycle

        [Test]
        public void FullLifecycle_Initialize_Then_Ping()
        {
            // Step 1: Initialize
            var initRequest = BuildRequest("initialize", new JObject { ["protocolVersion"] = "2025-03-26" }, 1);
            var initResponse = McpProtocol.ProcessRequest(initRequest, null, out string sessionId);

            Assert.That(sessionId, Is.Not.Null.And.Not.Empty);
            Assert.That(initResponse["result"]["protocolVersion"].Value<string>(), Is.EqualTo("2025-03-26"));

            // Step 2: Send notifications/initialized
            var notifRequest = JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""notifications/initialized""}");
            var notifResponse = McpProtocol.ProcessRequest(notifRequest, sessionId, out _);
            Assert.That(notifResponse, Is.Null, "Notifications should not produce a response.");

            // Step 3: Verify session is initialized
            Assert.That(McpProtocol.IsSessionInitialized(sessionId), Is.True);

            // Step 4: Ping
            var pingRequest = BuildRequest("ping", null, 2);
            var pingResponse = McpProtocol.ProcessRequest(pingRequest, sessionId, out _);

            Assert.That(pingResponse["result"], Is.Not.Null);
            Assert.That(pingResponse["error"], Is.Null);
            Assert.That(pingResponse["id"].Value<int>(), Is.EqualTo(2));
        }

        [Test]
        public void FullLifecycle_Initialize_Then_ListTools()
        {
            var sessionId = InitializeSession();

            var request = BuildRequest("tools/list", new JObject(), 2);
            var response = McpProtocol.ProcessRequest(request, sessionId, out _);

            Assert.That(response["result"], Is.Not.Null);
            Assert.That(response["result"]["tools"], Is.Not.Null);
            Assert.That(response["result"]["tools"].Type, Is.EqualTo(JTokenType.Array));
            Assert.That(((JArray)response["result"]["tools"]).Count, Is.GreaterThan(0));
        }

        [Test]
        public void FullLifecycle_Initialize_Then_CallTool()
        {
            var sessionId = InitializeSession();

            var request = BuildRequest("tools/call", new JObject
            {
                ["name"] = "ping",
                ["arguments"] = new JObject()
            }, 3);
            var response = McpProtocol.ProcessRequest(request, sessionId, out _);

            Assert.That(response["result"], Is.Not.Null);
            Assert.That(response["result"]["content"], Is.Not.Null);
        }

        #endregion

        #region Session Management

        [Test]
        public void Session_HeaderValidation_InvalidSession_Returns404()
        {
            InitializeSession();

            var error = McpProtocol.ValidateSessionHeader("invalid-session-id", out int status, out _);
            Assert.That(status, Is.EqualTo(404));
            Assert.That(error, Is.Not.Null);
        }

        [Test]
        public void Session_MultipleClients_EachGetsUniqueSession()
        {
            var session1 = InitializeSession();
            var session2 = InitializeSession();

            Assert.That(session1, Is.Not.EqualTo(session2));
            Assert.That(McpProtocol.SessionCount, Is.EqualTo(2));
        }

        [Test]
        public void Session_ReInitialize_ReplacesPreviousSession()
        {
            var session1 = InitializeSession();
            Assert.That(McpProtocol.SessionCount, Is.EqualTo(1));

            // Re-initialize with existing session
            var initRequest = BuildRequest("initialize", new JObject { ["protocolVersion"] = "2025-03-26" }, 99);
            McpProtocol.ProcessRequest(initRequest, session1, out string newSession);

            Assert.That(newSession, Is.Not.Null);
            Assert.That(newSession, Is.Not.EqualTo(session1));
            Assert.That(McpProtocol.SessionCount, Is.EqualTo(1), "Re-init should replace, not add.");
        }

        [Test]
        public void Session_MaxRetainedSessions_EnforcedOnInitialize()
        {
            McpProtocol.MaxRetainedSessions = 2;
            InitializeSession();
            InitializeSession();

            // Third should fail
            var initRequest = BuildRequest("initialize", new JObject { ["protocolVersion"] = "2025-03-26" }, 100);
            var response = McpProtocol.ProcessRequest(initRequest, null, out string failedSession);

            Assert.That(failedSession, Is.Null);
            Assert.That(response["error"], Is.Not.Null);
        }

        [Test]
        public void Session_TerminatedSession_CannotBeUsed()
        {
            var sessionId = InitializeSession();
            McpProtocol.TerminateSession(sessionId);

            var request = BuildRequest("ping", null, 5);
            var response = McpProtocol.ProcessRequest(request, sessionId, out _);

            Assert.That(response["error"], Is.Not.Null);
            Assert.That(response["error"]["code"].Value<int>(), Is.EqualTo(McpProtocol.ErrorServerNotInitialized));
        }

        #endregion

        #region Concurrent Requests

        [Test]
        public void ConcurrentRequests_MultipleToolCalls_AllSucceed()
        {
            var sessionId = InitializeSession();

            // Simulate multiple simultaneous tool calls (in reality sequential on main thread)
            var results = new List<JObject>();
            for (int i = 0; i < 5; i++)
            {
                var request = BuildRequest("ping", null, i + 10);
                var response = McpProtocol.ProcessRequest(request, sessionId, out _);
                results.Add(response);
            }

            foreach (var r in results)
            {
                Assert.That(r["error"], Is.Null);
                Assert.That(r["result"], Is.Not.Null);
            }
        }

        [Test]
        public void ConcurrentSessions_EachSessionOperatesIndependently()
        {
            var session1 = InitializeSession();
            var session2 = InitializeSession();

            // Ping on session1
            var req1 = BuildRequest("ping", null, 1);
            var resp1 = McpProtocol.ProcessRequest(req1, session1, out _);
            Assert.That(resp1["error"], Is.Null);

            // Terminate session1
            McpProtocol.TerminateSession(session1);

            // Session2 should still work
            var req2 = BuildRequest("ping", null, 2);
            var resp2 = McpProtocol.ProcessRequest(req2, session2, out _);
            Assert.That(resp2["error"], Is.Null);

            // Session1 should fail
            var req3 = BuildRequest("ping", null, 3);
            var resp3 = McpProtocol.ProcessRequest(req3, session1, out _);
            Assert.That(resp3["error"], Is.Not.Null);
        }

        #endregion

        #region Deferred Response Lifecycle

        [Test]
        public void DeferredTool_ReturnsNonNullResult()
        {
            var sessionId = InitializeSession();
            McpToolRegistry.IsInPlayMode = () => true;

            // wait_for_frames is a deferred tool — invoke via InvokeRaw
            var result = McpToolRegistry.InvokeRaw("wait_for_frames", new JObject { ["count"] = 1 });
            Assert.That(result, Is.Not.Null);
            // It should be a DeferredResult
            Assert.That(result, Is.InstanceOf<DeferredResult>());

            var deferred = (DeferredResult)result;
            Assert.That(deferred.IsComplete, Is.Not.Null);
            Assert.That(deferred.GetResult, Is.Not.Null);
            Assert.That(deferred.TimeoutSeconds, Is.GreaterThan(0));
        }

        [Test]
        public void DeferredTool_DoesNotCompleteByPollingWithoutFrames()
        {
            McpToolRegistry.IsInPlayMode = () => true;
            var result = McpToolRegistry.InvokeRaw("wait_for_frames", new JObject { ["count"] = 2 });
            Assert.That(result, Is.InstanceOf<DeferredResult>());

            var deferred = (DeferredResult)result;
            Assert.That(deferred.IsComplete(), Is.False);
            Assert.That(deferred.IsComplete(), Is.False);

            var payload = deferred.GetResult();
            Assert.That(payload, Is.Not.Null);
            Assert.That(payload["frames_elapsed"]?.Value<int>(), Is.EqualTo(0));
        }

        #endregion

        #region Error Responses

        [Test]
        public void Error_InvalidMethod_ReturnsMethodNotFound()
        {
            var sessionId = InitializeSession();
            var request = BuildRequest("nonexistent/method", null, 10);
            var response = McpProtocol.ProcessRequest(request, sessionId, out _);

            Assert.That(response["error"]["code"].Value<int>(), Is.EqualTo(McpProtocol.ErrorMethodNotFound));
        }

        [Test]
        public void Error_BadJson_ReturnedByParseRequest()
        {
            var (_, error) = McpProtocol.ParseRequest("{invalid json...");
            Assert.That(error, Is.Not.Null);
            Assert.That(error["error"]["code"].Value<int>(), Is.EqualTo(McpProtocol.ErrorParseError));
        }

        [Test]
        public void Error_Unauthorized_PreInitGate()
        {
            // Without initializing, all non-init requests should fail
            var request = BuildRequest("tools/list", new JObject(), 1);
            var response = McpProtocol.ProcessRequest(request, null, out _);

            Assert.That(response["error"], Is.Not.Null);
            Assert.That(response["error"]["code"].Value<int>(), Is.EqualTo(McpProtocol.ErrorServerNotInitialized));
        }

        [Test]
        public void ToolsCall_StructuredFailureSetsMcpErrorAndKeepsDetails()
        {
            var sessionId = InitializeSession();
            var request = BuildRequest("tools/call", new JObject {
                ["name"] = "get_asset_object_index", ["arguments"] = new JObject { ["assetPath"] = "outside.asset" }
            }, 8);
            var response = McpProtocol.ProcessRequest(request, sessionId, out _);
            Assert.That((bool?)response["result"]["isError"], Is.True);
            var details = JObject.Parse((string)response["result"]["content"][0]["text"]);
            Assert.That((string)details["error_code"], Is.EqualTo(McpErrorCodes.InvalidParameter));
        }

        [Test]
        public void Error_ToolsCall_MissingName_ReturnsInvalidParams()
        {
            var sessionId = InitializeSession();
            var request = BuildRequest("tools/call", new JObject { ["arguments"] = new JObject() }, 5);
            var response = McpProtocol.ProcessRequest(request, sessionId, out _);

            Assert.That(response["error"], Is.Not.Null);
            Assert.That(response["error"]["code"].Value<int>(), Is.EqualTo(McpProtocol.ErrorInvalidParams));
        }

        #endregion

        #region HTTP Parsing Integration (via TcpHttpHandler)

        [Test]
        public void HttpParsing_ValidMcpPost_ParsesCorrectly()
        {
            var body = @"{""jsonrpc"":""2.0"",""method"":""initialize"",""id"":1,""params"":{""protocolVersion"":""2025-03-26""}}";
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var raw = $"POST /mcp HTTP/1.1\r\nHost: 127.0.0.1:8080\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nAuthorization: Bearer test-token\r\n\r\n{body}";
            var stream = new MemoryStream(Encoding.ASCII.GetBytes(raw));

            var httpReq = TcpHttpHandler.ParseRequest(stream);

            Assert.That(httpReq.Error, Is.Null);
            Assert.That(httpReq.Method, Is.EqualTo("POST"));
            Assert.That(httpReq.Path, Is.EqualTo("/mcp"));
            Assert.That(httpReq.Headers["Content-Type"], Is.EqualTo("application/json"));
            Assert.That(httpReq.Headers["Authorization"], Does.StartWith("Bearer "));

            // Parse the body as JSON-RPC and process
            var json = Encoding.UTF8.GetString(httpReq.Body);
            var (requests, parseError) = McpProtocol.ParseRequest(json);
            Assert.That(parseError, Is.Null);
            Assert.That(requests.Count, Is.EqualTo(1));

            var response = McpProtocol.ProcessRequest(requests[0], null, out string sessionId);
            Assert.That(sessionId, Is.Not.Null);
            Assert.That(response["result"]["protocolVersion"], Is.Not.Null);
        }

        #endregion

        #region Helpers

        private string InitializeSession()
        {
            var initRequest = BuildRequest("initialize", new JObject { ["protocolVersion"] = "2025-03-26" }, 1);
            McpProtocol.ProcessRequest(initRequest, null, out string sessionId);

            var notif = JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""notifications/initialized""}");
            McpProtocol.ProcessRequest(notif, sessionId, out _);

            return sessionId;
        }

        private static JObject BuildRequest(string method, JObject parameters, int id)
        {
            var req = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method,
                ["id"] = id
            };
            if (parameters != null)
                req["params"] = parameters;
            return req;
        }

        #endregion
    }
}
