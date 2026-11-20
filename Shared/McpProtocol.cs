using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace I1rr.UnityMcp.Shared
{
    /// <summary>Informational transport details, never authenticated client identity.</summary>
    public sealed class McpClientTransportInfo
    {
        public string UserAgent { get; set; }
        public string RemoteAddress { get; set; }
    }

    /// <summary>Stateless MCP JSON-RPC dispatch. Every request supplies its own metadata.</summary>
    public static partial class McpProtocol
    {
        public const string ServerVersion = "1.2.1";
        public const int ErrorParseError = -32700;
        public const int ErrorInvalidRequest = -32600;
        public const int ErrorMethodNotFound = -32601;
        public const int ErrorInvalidParams = -32602;
        public const int ErrorInternalError = -32603;

        public const string MethodToolsList = "tools/list";
        public const string MethodToolsCall = "tools/call";
        public const string MethodResourcesList = "resources/list";
        public const string MethodResourcesRead = "resources/read";

        public static Func<JObject, McpRequestContext, JToken> OnToolsList { get; set; }
        public static Func<JObject, McpRequestContext, JToken> OnToolsCall { get; set; }
        public static Func<JObject, McpRequestContext, JToken> OnResourcesList { get; set; }
        public static Func<JObject, McpRequestContext, JToken> OnResourcesRead { get; set; }

        /// <summary>Parse one JSON-RPC object. Wire batches are not supported.</summary>
        public static (List<JObject> requests, JObject errorResponse) ParseRequest(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return (null, BuildErrorResponse(null, ErrorParseError, "Empty request body"));
            try
            {
                if (JToken.Parse(json) is JObject request)
                    return (new List<JObject> { request }, null);
                return (null, BuildErrorResponse(null, ErrorInvalidRequest, "Expected one JSON-RPC object"));
            }
            catch (JsonReaderException)
            {
                return (null, BuildErrorResponse(null, ErrorParseError, "Invalid JSON"));
            }
        }

        public static JObject ValidateJsonRpc(JObject request)
        {
            // (a) jsonrpc field must exist and equal "2.0"
            var jsonrpc = request["jsonrpc"];
            if (jsonrpc == null || jsonrpc.Type != JTokenType.String || jsonrpc.Value<string>() != "2.0")
            {
                var id = request["id"];
                return BuildErrorResponse(id, ErrorInvalidRequest,
                    "Missing or invalid 'jsonrpc' field. Must be \"2.0\"");
            }

            // (b) method field must exist and be a string
            var method = request["method"];
            if (method == null || method.Type != JTokenType.String)
            {
                var id = request["id"];
                return BuildErrorResponse(id, ErrorInvalidRequest,
                    "Missing or invalid 'method' field. Must be a string");
            }

            // (c) params if present must be object or array
            var paramsToken = request["params"];
            if (paramsToken != null && paramsToken.Type != JTokenType.Object && paramsToken.Type != JTokenType.Array)
            {
                var id = request["id"];
                return BuildErrorResponse(id, ErrorInvalidRequest,
                    "'params' must be an object or array if present");
            }

            // (d) id if present must be string, number, or null
            var idToken = request["id"];
            if (idToken != null &&
                idToken.Type != JTokenType.String &&
                idToken.Type != JTokenType.Integer &&
                idToken.Type != JTokenType.Float &&
                idToken.Type != JTokenType.Null)
            {
                return BuildErrorResponse(null, ErrorInvalidRequest,
                    "'id' must be a string, number, or null if present");
            }

            return null;
        }

        /// <summary>
        /// Returns true if the request is a notification (has no 'id' field).
        /// </summary>
        public static bool IsNotification(JObject request)
        {
            return request["id"] == null;
        }

        /// <summary>
        /// Builds a JSON-RPC 2.0 success response.
        /// </summary>
        public static JObject BuildResponse(JToken id, JToken result)
        {
            var response = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone() ?? JValue.CreateNull(),
                ["result"] = result ?? JValue.CreateNull()
            };
            return response;
        }

        /// <summary>
        /// Builds a JSON-RPC 2.0 error response.
        /// </summary>
        public static JObject BuildErrorResponse(JToken id, int code, string message, JToken data = null)
        {
            var error = new JObject
            {
                ["code"] = code,
                ["message"] = message
            };

            if (data != null)
            {
                error["data"] = data;
            }

            var response = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone() ?? JValue.CreateNull(),
                ["error"] = error
            };
            return response;
        }

        public static JObject BuildToolResult(string text, bool isError = false)
        {
            var result = new JObject
            {
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = text
                    }
                }
            };

            if (isError)
            {
                result["isError"] = true;
            }

            return result;
        }

        /// <summary>
        /// Wraps a raw tool response in MCP content format if not already wrapped.
        /// Tools may return: {"content": [...]} (already wrapped), {"error": {...}} (registry error),
        /// or arbitrary JObject (raw tool data that needs wrapping).
        /// </summary>
        public static JObject WrapToolResult(JObject toolResponse)
        {
            if (toolResponse == null)
                return BuildToolResult("null");

            // Already has content array — pass through
            if (toolResponse["content"] is JArray)
            {
                if (IsToolFailure(toolResponse)) toolResponse["isError"] = true;
                return toolResponse;
            }

            // Registry-level error — convert to MCP error content
            if (toolResponse["error"] is JObject errorObj)
            {
                var msg = errorObj["message"]?.Value<string>() ?? "Unknown error";
                return BuildToolResult(msg, isError: true);
            }

            // Raw tool data — serialize to text content
            return BuildToolResult(toolResponse.ToString(Newtonsoft.Json.Formatting.None), IsToolFailure(toolResponse));
        }

        internal static bool IsToolFailure(JObject result) => result["error"] is JObject ||
            result["error_code"]?.Type == JTokenType.String || (bool?)result["isError"] == true ||
            result["success"]?.Type == JTokenType.Boolean && !(bool)result["success"];

        /// <summary>Shared ordinary/deferred admission before invoking any tool.</summary>
        public static JObject ValidateRequestForDispatch(JObject request, McpRequestContext context)
        {
            var error = ValidateJsonRpc(request);
            if (error != null) return error;
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (context.Method != (string)request["method"] || !JToken.DeepEquals(context.RequestId, request["id"]))
                return BuildErrorResponse(request["id"], ErrorInvalidRequest, "Request context does not match this request");
            return ValidateModernEnvelope(request, context);
        }

        public static JObject ProcessRequestWithContext(JObject request, McpRequestContext context)
        {
            return ProcessModernRequest(request, context);
        }

        private static JObject HandleToolsList(JObject request, McpRequestContext context)
        {
            var id = request["id"];

            if (OnToolsList != null)
            {
                try
                {
                    var result = OnToolsList(request, context);
                    return BuildResponse(id, result);
                }
                catch (Exception ex)
                {
                    return BuildErrorResponse(id, ErrorInternalError, ex.Message);
                }
            }

            // No handler registered — return empty tools list
            return BuildResponse(id, new JObject { ["tools"] = new JArray() });
        }

        private static JObject HandleToolsCall(JObject request, McpRequestContext context)
        {
            var id = request["id"];
            var paramsObj = request["params"] as JObject;

            if (paramsObj == null || paramsObj["name"] == null)
            {
                return BuildErrorResponse(id, ErrorInvalidParams, "Missing 'name' in params");
            }

            if (OnToolsCall != null)
            {
                try
                {
                    var result = OnToolsCall(request, context);
                    // Wrap in MCP content format if not already wrapped
                    var wrapped = WrapToolResult(result as JObject);
                    return BuildResponse(id, wrapped);
                }
                catch (Exception ex)
                {
                    return BuildResponse(id, BuildToolResult(ex.Message, isError: true));
                }
            }

            var toolName = paramsObj["name"].Value<string>();
            return BuildResponse(id, BuildToolResult($"Unknown tool: {toolName}", isError: true));
        }

        private static JObject HandleResourcesList(JObject request, McpRequestContext context)
        {
            var id = request["id"];

            if (OnResourcesList != null)
            {
                try
                {
                    var result = OnResourcesList(request, context);
                    return BuildResponse(id, result);
                }
                catch (Exception ex)
                {
                    return BuildErrorResponse(id, ErrorInternalError, ex.Message);
                }
            }

            return BuildResponse(id, new JObject { ["resources"] = new JArray() });
        }

        private static JObject HandleResourcesRead(JObject request, McpRequestContext context)
        {
            var id = request["id"];
            var paramsObj = request["params"] as JObject;

            if (paramsObj == null || paramsObj["uri"] == null)
            {
                return BuildErrorResponse(id, ErrorInvalidParams, "Missing 'uri' in params");
            }

            if (OnResourcesRead != null)
            {
                try
                {
                    var result = OnResourcesRead(request, context);
                    return BuildResponse(id, result);
                }
                catch (Exception ex)
                {
                    return BuildErrorResponse(id, ErrorInternalError, ex.Message);
                }
            }

            var uri = paramsObj["uri"].Value<string>();
            return BuildErrorResponse(id, ErrorInvalidParams, $"Resource not found: {uri}");
        }

    }
}
