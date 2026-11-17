using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace I1rr.UnityMcp.Editor.Tests
{
    public class McpModernProtocolTests
    {
        private Func<JObject, string, JToken> _call, _list, _read;

        [SetUp] public void SetUp()
        {
            _call = McpProtocol.OnToolsCall;
            _list = McpProtocol.OnToolsList;
            _read = McpProtocol.OnResourcesRead;
            McpProtocol.Reset();
        }

        [TearDown] public void TearDown()
        {
            McpProtocol.OnToolsCall = _call;
            McpProtocol.OnToolsList = _list;
            McpProtocol.OnResourcesRead = _read;
            McpProtocol.Reset();
        }

        internal static JObject Request(string method = "server/discover") => new JObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method,
            ["params"] = new JObject
            {
                ["_meta"] = new JObject
                {
                    [McpProtocol.VersionMetaKey] = McpProtocol.ProtocolVersionModern,
                    [McpProtocol.CapabilitiesMetaKey] = new JObject()
                }
            }
        };

        internal static Dictionary<string, string> Headers(JObject request) => new Dictionary<string, string>
        {
            ["mcp-protocol-version"] = (string)request["params"]["_meta"][McpProtocol.VersionMetaKey],
            ["mcp-method"] = (string)request["method"]
        };

        private static byte[] Bytes(JToken request) => Encoding.UTF8.GetBytes(request.ToString(Formatting.None));

        private static JObject PrepareAndDispatch(JObject request, Dictionary<string, string> headers = null)
        {
            Assert.That(McpModernHttp.TryPrepare(headers ?? Headers(request), Bytes(request), null,
                out var parsed, out var context, out var error, out var status), Is.True, error?.ToString());
            Assert.That(status, Is.EqualTo(200));
            Assert.That(context.LegacySessionId, Is.Null);
            var response = McpProtocol.ProcessRequestWithContext(parsed, context, out var newSession);
            Assert.That(newSession, Is.Null);
            return response;
        }

        [Test] public void FirstCallAndFullLegacyTableDoNotAllocateModernSessions()
        {
            var first = PrepareAndDispatch(Request());
            Assert.That((string)first["result"]["resultType"], Is.EqualTo("complete"));
            Assert.That(McpProtocol.SessionCount, Is.Zero);
            var limit = McpProtocol.MaxRetainedSessions;
            try
            {
                McpProtocol.MaxRetainedSessions = 1;
                McpProtocol.RestoreSession("protected", McpProtocol.ProtocolVersionNext);
                var request = Request();
                var headers = Headers(request);
                headers["MCP-Session-Id"] = "stale-unrelated-session";
                var response = PrepareAndDispatch(request, headers);
                Assert.That(response["error"], Is.Null);
                Assert.That(McpProtocol.SessionCount, Is.EqualTo(1));
                Assert.That(McpProtocol.GetSession("protected").RequestCount, Is.Zero);
            }
            finally { McpProtocol.MaxRetainedSessions = limit; }
        }

        [Test] public void CatalogAndReadResultsAreConservativeAndDeterministic()
        {
            McpProtocol.OnToolsList = (_, sid) =>
            {
                Assert.That(sid, Is.Null);
                return new JObject { ["tools"] = new JArray(new JObject { ["name"] = "z" }, new JObject { ["name"] = "a" }) };
            };
            var response = PrepareAndDispatch(Request("tools/list"));
            Assert.That(response["result"]["tools"].Select(t => (string)t["name"]), Is.EqualTo(new[] { "a", "z" }));
            Assert.That((int)response["result"]["ttlMs"], Is.Zero);
            Assert.That((string)response["result"]["cacheScope"], Is.EqualTo("private"));
            Assert.That(response["result"]["_meta"][McpProtocol.ServerInfoMetaKey], Is.Not.Null);
        }

        [TestCase("猫")]
        [TestCase(" padded ")]
        [TestCase("=?base64?literal?=")]
        public void EncodedResourceNamesAreComparedAfterDecoding(string uri)
        {
            var request = Request("resources/read");
            request["params"]["uri"] = uri;
            var headers = Headers(request);
            headers["mcp-name"] = "=?base64?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(uri)) + "?=";
            McpProtocol.OnResourcesRead = (req, _) => new JObject { ["contents"] = new JArray(new JObject { ["uri"] = req["params"]["uri"], ["text"] = "ok" }) };
            Assert.That(PrepareAndDispatch(request, headers)["error"], Is.Null);
            headers["mcp-name"] = "different";
            AssertRejected(request, headers, McpProtocol.ErrorHeaderMismatch);
        }

        private static void AssertRejected(JObject request, Dictionary<string, string> headers, int code)
        {
            Assert.That(McpModernHttp.TryPrepare(headers, Bytes(request), null, out _, out var context, out var error, out var status), Is.False);
            Assert.That(context, Is.Null);
            Assert.That(status, Is.EqualTo(400));
            Assert.That((int)error["error"]["code"], Is.EqualTo(code));
        }

        [Test] public void MismatchesAndMalformedMetadataNeverInvokeTools()
        {
            int calls = 0;
            McpProtocol.OnToolsCall = (_, __) => { calls++; return new JObject(); };
            var request = Request("tools/call");
            request["params"]["name"] = "probe";
            var headers = Headers(request);
            headers["Mcp-Name"] = "probe";
            headers["mcp-method"] = "resources/read";
            AssertRejected(request, headers, McpProtocol.ErrorHeaderMismatch);
            headers["mcp-method"] = "tools/call";
            ((JObject)request["params"]["_meta"]).Remove(McpProtocol.CapabilitiesMetaKey);
            AssertRejected(request, headers, McpProtocol.ErrorInvalidParams);
            Assert.That(calls, Is.Zero);
        }

        [TestCase("null")]
        [TestCase("1.5")]
        [TestCase("true")]
        public void InvalidModernIdsAreRejected(string json)
        {
            var request = Request();
            request["id"] = JToken.Parse(json);
            AssertRejected(request, Headers(request), McpProtocol.ErrorInvalidRequest);
        }

        [Test] public void UnknownVersionReportsSupportedVersionsWithoutCreatingSession()
        {
            var request = Request();
            request["params"]["_meta"][McpProtocol.VersionMetaKey] = "2099-01-01";
            AssertRejected(request, Headers(request), McpProtocol.ErrorUnsupportedProtocolVersion);
            Assert.That(McpProtocol.SessionCount, Is.Zero);
        }

        [Test] public void BatchesAndInvalidUtf8AreRejectedAndMalformedLegacyRoutingDoesNotThrow()
        {
            var request = Request();
            Assert.That(McpModernHttp.TryPrepare(Headers(request), Bytes(new JArray(request)), null, out _, out _, out var error, out _), Is.False);
            Assert.That((int)error["error"]["code"], Is.EqualTo(McpProtocol.ErrorInvalidRequest));
            Assert.That(McpModernHttp.TryPrepare(Headers(request), new byte[] { 0xff }, null, out _, out _, out error, out _), Is.False);
            Assert.That((int)error["error"]["code"], Is.EqualTo(McpProtocol.ErrorParseError));
            Assert.DoesNotThrow(() => McpModernHttp.IsModernRequest(new Dictionary<string, string>(), Bytes(new JObject { ["method"] = new JObject() })));
        }

        [Test] public void ToolFailureStaysAnMcpResultAndUnknownMethodUses404()
        {
            var request = Request("tools/call");
            request["params"]["name"] = "unknown";
            var headers = Headers(request);
            headers["Mcp-Name"] = "unknown";
            McpProtocol.OnToolsCall = (_, __) => throw new InvalidOperationException("probe failure");
            var response = PrepareAndDispatch(request, headers);
            Assert.That((bool)response["result"]["isError"], Is.True);
            Assert.That((string)response["result"]["resultType"], Is.EqualTo("complete"));
            Assert.That(McpProtocol.ModernHttpStatus(response), Is.EqualTo(200));
            response = PrepareAndDispatch(Request("initialize"));
            Assert.That(McpProtocol.ModernHttpStatus(response), Is.EqualTo(404));
            Assert.That(McpProtocol.SessionCount, Is.Zero);
        }

        [Test] public void CustomHeadersFollowStaticNestedPathsAndSafePrimitiveValues()
        {
            var schema = JObject.Parse(@"{'type':'object','properties':{'nested':{'type':'object','properties':{
                'label':{'type':'string','x-mcp-header':'Label'},
                'count':{'type':'integer','x-mcp-header':'Count'},
                'enabled':{'type':'boolean','x-mcp-header':'Enabled'}}}}}");
            var args = JObject.Parse(@"{'nested':{'label':'猫','count':42,'enabled':true}}");
            var headers = new Dictionary<string, string>
            {
                ["Mcp-Param-Label"] = "=?base64?54yr?=", ["Mcp-Param-Count"] = "42.0", ["Mcp-Param-Enabled"] = "true"
            };
            Assert.That(McpModernHttp.ValidateToolHeaders(schema, args, headers), Is.True);
            headers["Mcp-Param-Count"] = "43";
            Assert.That(McpModernHttp.ValidateToolHeaders(schema, args, headers), Is.False);
            headers["Mcp-Param-Count"] = "9007199254740992";
            args["nested"]["count"] = 9007199254740992L;
            Assert.That(McpModernHttp.ValidateToolHeaders(schema, args, headers), Is.False);
            Assert.That(McpModernHttp.ValidateToolHeaders(schema, new JObject(), new Dictionary<string, string>()), Is.True);
        }

        [Test] public void PreflightOnlyAddsValidMcpParameterHeaderNames()
        {
            var value = McpModernHttp.CorsAllowedHeaders(new Dictionary<string, string>
                { ["Access-Control-Request-Headers"] = "Mcp-Param-Region, mcP-param-region, X-Other, Mcp-Param-Bad\r\nInjection" });
            Assert.That(value, Does.Contain("Mcp-Param-Region"));
            Assert.That(value, Does.Not.Contain("X-Other"));
            Assert.That(value, Does.Not.Contain("Injection"));
        }

        [TestCase(@"{'items':{'x-mcp-header':'Wrong','type':'string'}}")]
        [TestCase(@"{'properties':{'a':{'type':'string','x-mcp-header':'Same'},'b':{'type':'string','x-mcp-header':'same'}}}")]
        [TestCase(@"{'properties':{'a':{'type':['string','null'],'x-mcp-header':'A'}}}")]
        [TestCase(@"{'properties':{'a':{'type':'number','x-mcp-header':'A'}}}")]
        [TestCase(@"{'properties':{'a':{'type':'string','x-mcp-header':'bad name'}}}")]
        public void InvalidHeaderDefinitionsAreRejected(string json)
        {
            Assert.That(McpModernHttp.IsValidToolHeaderSchema(JObject.Parse(json)), Is.False);
        }
    }
}
