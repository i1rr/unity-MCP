using System;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace I1rr.UnityMcp.Shared
{
    public static partial class McpProtocol
    {
        public const string ProtocolVersionModern = "2026-07-28";
        public const string VersionMetaKey = "io.modelcontextprotocol/protocolVersion";
        public const string CapabilitiesMetaKey = "io.modelcontextprotocol/clientCapabilities";
        public const string ClientInfoMetaKey = "io.modelcontextprotocol/clientInfo";
        public const string ServerInfoMetaKey = "io.modelcontextprotocol/serverInfo";
        public const int ErrorHeaderMismatch = -32020;
        public const int ErrorMissingClientCapability = -32021;
        public const int ErrorUnsupportedProtocolVersion = -32022;

        public static JArray SupportedVersions => new JArray(ProtocolVersionModern);

        public static McpRequestContext CreateModernRequestContext(JObject request,
            McpClientTransportInfo transportInfo = null, string authenticatedPrincipal = null,
            CancellationToken cancellation = default)
        {
            var meta = (request?["params"] as JObject)?["_meta"] as JObject;
            return new McpRequestContext((string)request?["method"], request?["id"], ProtocolVersionModern,
                meta?[CapabilitiesMetaKey] as JObject, meta?[ClientInfoMetaKey] as JObject,
                transportInfo, authenticatedPrincipal, cancellation);
        }

        internal static JObject ValidateModernEnvelope(JObject request, McpRequestContext context)
        {
            var id = request["id"];
            if (request.Property("id") != null && id.Type != JTokenType.String && id.Type != JTokenType.Integer)
                return BuildErrorResponse(null, ErrorInvalidRequest, "Modern request IDs must be strings or integers");
            var meta = (request["params"] as JObject)?["_meta"] as JObject;
            if (context.ProtocolVersion != ProtocolVersionModern || meta == null || meta[VersionMetaKey]?.Type != JTokenType.String ||
                (string)meta[VersionMetaKey] != ProtocolVersionModern || !(meta[CapabilitiesMetaKey] is JObject))
                return BuildErrorResponse(id, ErrorInvalidParams, "Each modern request requires protocol version and client capabilities metadata");
            var clientInfo = meta[ClientInfoMetaKey];
            if (clientInfo != null && (!(clientInfo is JObject info) ||
                info["name"]?.Type != JTokenType.String || info["version"]?.Type != JTokenType.String))
                return BuildErrorResponse(id, ErrorInvalidParams, "Client info must contain string name and version");
            if (!JToken.DeepEquals(context.ClientCapabilities, meta[CapabilitiesMetaKey]) ||
                !JToken.DeepEquals(context.ClientInfo, clientInfo ?? new JObject()))
                return BuildErrorResponse(id, ErrorInvalidRequest, "Request metadata does not match its captured context");
            return null;
        }

        private static JObject ProcessModernRequest(JObject request, McpRequestContext context)
        {
            var admission = ValidateRequestForDispatch(request, context);
            if (admission != null) return admission;
            var name = (request["params"] as JObject)?["name"];
            McpRequestActivity.Begin(context, name?.Type == JTokenType.String ? (string)name : null);
            try
            {
                var response = DispatchModernRequest(request, context);
                McpRequestActivity.Finish(context, response?["error"] != null || (bool?)response?["result"]?["isError"] == true ? "failed" : "completed");
                return response;
            }
            catch { McpRequestActivity.Finish(context, "failed_outcome_unknown"); throw; }
        }

        private static JObject DispatchModernRequest(JObject request, McpRequestContext context)
        {
            if (context.IsNotification) return null;
            JObject response;
            switch (context.Method)
            {
                case "server/discover":
                    response = BuildResponse(context.RequestId, new JObject
                    {
                        ["supportedVersions"] = SupportedVersions,
                        ["capabilities"] = new JObject
                        {
                            ["tools"] = new JObject { ["listChanged"] = false },
                            ["resources"] = new JObject { ["listChanged"] = false }
                        },
                        ["instructions"] = "Use tools/list and discover_tools for the available catalog. Pass explicit application handles between calls."
                    });
                    break;
                case MethodToolsList: response = HandleToolsList(request, context); break;
                case MethodToolsCall: response = HandleToolsCall(request, context); break;
                case MethodResourcesList: response = HandleResourcesList(request, context); break;
                case MethodResourcesRead: response = HandleResourcesRead(request, context); break;
                default: return BuildErrorResponse(context.RequestId, ErrorMethodNotFound, "Method not found: " + context.Method);
            }
            return AdaptResponseForContext(response, context);
        }

        /// <summary>Applies the negotiated envelope without mutating a tool's result.</summary>
        public static JObject AdaptResponseForContext(JObject response, McpRequestContext context)
        {
            if (response == null || context == null || response["error"] != null) return response;
            if (!(response["result"] is JObject))
                return BuildErrorResponse(context.RequestId, ErrorInternalError, "MCP results must be JSON objects");
            var copy = (JObject)response.DeepClone();
            var result = (JObject)copy["result"];
            result["resultType"] = "complete";
            var meta = result["_meta"] as JObject ?? new JObject();
            meta[ServerInfoMetaKey] = new JObject { ["name"] = "i1rr-unity-mcp", ["version"] = ServerVersion };
            result["_meta"] = meta;
            if (context.Method == "server/discover" || context.Method == MethodToolsList ||
                context.Method == MethodResourcesList || context.Method == MethodResourcesRead)
            {
                result["ttlMs"] = 0;
                result["cacheScope"] = "private";
            }
            if (result["tools"] is JArray tools)
                result["tools"] = new JArray(tools.Where(t => McpModernHttp.IsValidToolHeaderSchema(t["inputSchema"] as JObject))
                    .OrderBy(t => (string)t["name"], StringComparer.Ordinal));
            if (result["resources"] is JArray resources)
                result["resources"] = new JArray(resources.OrderBy(t => (string)t["uri"], StringComparer.Ordinal));
            return copy;
        }

        public static int ModernHttpStatus(JObject response)
        {
            var code = (int?)response?["error"]?["code"];
            if (code == null) return 200;
            if (code == ErrorMethodNotFound) return 404;
            if (code == ErrorInternalError) return 500;
            return 400;
        }
    }
}
