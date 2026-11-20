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
    /// and exercises independent per-request metadata and tool dispatch.
    /// </summary>
    public class McpIntegrationTests
    {
        private Func<bool> _originalPlayMode;
        private Func<JObject, McpRequestContext, JToken> _originalOnToolsList;
        private Func<JObject, McpRequestContext, JToken> _originalOnToolsCall;

        [SetUp]
        public void SetUp()
        {
            _originalPlayMode = McpToolRegistry.IsInPlayMode;
            _originalOnToolsList = McpProtocol.OnToolsList;
            _originalOnToolsCall = McpProtocol.OnToolsCall;

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
            McpProtocol.OnToolsList = _originalOnToolsList;
            McpProtocol.OnToolsCall = _originalOnToolsCall;
        }

        #region Full Request Lifecycle

        [Test]
        public void DiscoveryWorksWithoutInitialization()
        {
            var response = Dispatch(BuildRequest("server/discover", null, 1));
            Assert.That(response["error"], Is.Null);
            Assert.That((string)response["result"]["resultType"], Is.EqualTo("complete"));
        }

        [Test]
        public void FirstRequest_ListTools()
        {

            var request = BuildRequest("tools/list", new JObject(), 2);
            var response = Dispatch(request);

            Assert.That(response["result"], Is.Not.Null);
            Assert.That(response["result"]["tools"], Is.Not.Null);
            Assert.That(response["result"]["tools"].Type, Is.EqualTo(JTokenType.Array));
            Assert.That(((JArray)response["result"]["tools"]).Count, Is.GreaterThan(0));
        }

        [Test]
        public void FirstRequest_CallTool()
        {

            var request = BuildRequest("tools/call", new JObject
            {
                ["name"] = "ping",
                ["arguments"] = new JObject()
            }, 3);
            var response = Dispatch(request);

            Assert.That(response["result"], Is.Not.Null);
            Assert.That(response["result"]["content"], Is.Not.Null);
        }

        #endregion


        #region Concurrent Requests

        [Test]
        public void ConcurrentRequests_MultipleToolCalls_AllSucceed()
        {

            // Simulate multiple simultaneous tool calls (in reality sequential on main thread)
            var results = new List<JObject>();
            for (int i = 0; i < 5; i++)
            {
                var request = BuildRequest("server/discover", null, i + 10);
                var response = Dispatch(request);
                results.Add(response);
            }

            foreach (var r in results)
            {
                Assert.That(r["error"], Is.Null);
                Assert.That(r["result"], Is.Not.Null);
            }
        }

        [Test]
        public void InterleavedClientsUseOnlyTheirOwnRequestMetadata()
        {
            var first = BuildRequest("tools/list", null, 1);
            var second = BuildRequest("tools/list", null, 1);
            first["params"]["_meta"][McpProtocol.ClientInfoMetaKey] = new JObject { ["name"] = "first", ["version"] = "1" };
            second["params"]["_meta"][McpProtocol.ClientInfoMetaKey] = new JObject { ["name"] = "second", ["version"] = "1" };
            var seen = new List<string>();
            McpProtocol.OnToolsList = (_, context) => { seen.Add((string)context.ClientInfo["name"]); return new JObject { ["tools"] = new JArray() }; };
            Assert.That(Dispatch(first)["error"], Is.Null);
            Assert.That(Dispatch(second)["error"], Is.Null);
            Assert.That(Dispatch(first)["error"], Is.Null);
            Assert.That(seen, Is.EqualTo(new[] { "first", "second", "first" }));
        }

        #endregion

        #region Deferred Response Lifecycle

        [Test]
        public void DeferredTool_ReturnsNonNullResult()
        {
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
            var request = BuildRequest("nonexistent/method", null, 10);
            var response = Dispatch(request);

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
        public void Error_MissingMetadataIsRejected()
        {
            // Each request must supply metadata, even when a previous request succeeded.
            var request = BuildRequest("tools/list", new JObject(), 1);
            ((JObject)request["params"]).Remove("_meta");
            var response = Dispatch(request);

            Assert.That(response["error"], Is.Not.Null);
            Assert.That(response["error"]["code"].Value<int>(), Is.EqualTo(McpProtocol.ErrorInvalidParams));
        }

        [Test]
        public void ToolsCall_StructuredFailureSetsMcpErrorAndKeepsDetails()
        {
            var request = BuildRequest("tools/call", new JObject {
                ["name"] = "get_asset_object_index", ["arguments"] = new JObject { ["assetPath"] = "outside.asset" }
            }, 8);
            var response = Dispatch(request);
            Assert.That((bool?)response["result"]["isError"], Is.True);
            var details = JObject.Parse((string)response["result"]["content"][0]["text"]);
            Assert.That((string)details["error_code"], Is.EqualTo(McpErrorCodes.InvalidParameter));
        }

        [Test]
        public void Error_ToolsCall_MissingName_ReturnsInvalidParams()
        {
            var request = BuildRequest("tools/call", new JObject { ["arguments"] = new JObject() }, 5);
            var response = Dispatch(request);

            Assert.That(response["error"], Is.Not.Null);
            Assert.That(response["error"]["code"].Value<int>(), Is.EqualTo(McpProtocol.ErrorInvalidParams));
        }

        #endregion

        #region HTTP Parsing Integration (via TcpHttpHandler)

        [Test]
        public void HttpParsing_ValidMcpPost_ParsesCorrectly()
        {
            var body = BuildRequest("server/discover", null, 1).ToString(Newtonsoft.Json.Formatting.None);
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

            var response = Dispatch(requests[0]);
            Assert.That(response["result"]["supportedVersions"], Is.Not.Null);
        }

        #endregion

        #region Helpers

        private static JObject Dispatch(JObject request) =>
            McpProtocol.ProcessRequestWithContext(request, McpProtocol.CreateModernRequestContext(request));

        private static JObject BuildRequest(string method, JObject parameters, int id)
        {
            var request = McpModernProtocolTests.Request(method);
            request["id"] = id;
            if (parameters != null)
                foreach (var property in parameters.Properties()) request["params"][property.Name] = property.Value;
            return request;
        }

        #endregion
    }
}
