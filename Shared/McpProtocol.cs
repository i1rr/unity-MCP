using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace I1rr.UnityMcp.Shared
{
    /// <summary>
    /// Transport-level client details that are not part of the MCP initialize payload.
    /// Values are informational and must not be treated as authenticated identity.
    /// </summary>
    public sealed class McpClientTransportInfo
    {
        public string UserAgent { get; set; }
        public string RemoteAddress { get; set; }
    }

    /// <summary>
    /// Protocol and observability state for one MCP client session.
    /// </summary>
    public sealed class McpSession
    {
        public string Id { get; }
        public bool IsInitialized { get; internal set; }
        public string ProtocolVersion { get; internal set; }
        internal JObject ClientCapabilities { get; set; } = new JObject();
        public DateTime CreatedAtUtc { get; internal set; }
        public DateTime LastActivityUtc { get; set; }

        public string ClientName { get; internal set; }
        public string ClientTitle { get; internal set; }
        public string ClientVersion { get; internal set; }
        public string DisplayName { get; internal set; }
        public string ClientKind { get; internal set; }
        public int AppInstanceNumber { get; internal set; }
        public string ClientInstanceId { get; internal set; }
        public string ClientInstanceName { get; internal set; }
        public string RunId { get; internal set; }
        public string UserAgent { get; internal set; }
        public string RemoteAddress { get; internal set; }
        public long RequestCount { get; internal set; }
        public long ErrorCount { get; internal set; }
        public int InFlightRequests { get; internal set; }
        public string LastMethod { get; internal set; }
        public string LastTool { get; internal set; }

        public McpSession(string id, string protocolVersion)
        {
            Id = id;
            ProtocolVersion = protocolVersion ?? McpProtocol.ProtocolVersionBaseline;
            CreatedAtUtc = DateTime.UtcNow;
            LastActivityUtc = DateTime.UtcNow;
            DisplayName = "Unknown MCP Client";
            ClientKind = "unknown";
            AppInstanceNumber = 1;
        }
    }

    /// <summary>
    /// Core MCP protocol handler implementing JSON-RPC 2.0 with MCP extensions.
    /// Supports multiple concurrent client sessions keyed by session ID.
    /// Does not handle HTTP transport directly.
    /// </summary>
    public static partial class McpProtocol
    {
        // Protocol versions
        public const string ProtocolVersionBaseline = "2025-03-26";
        public const string ProtocolVersionNext = "2025-11-25";
        public const string ServerVersion = "1.1.1";

        // JSON-RPC error codes
        public const int ErrorParseError = -32700;
        public const int ErrorInvalidRequest = -32600;
        public const int ErrorMethodNotFound = -32601;
        public const int ErrorInvalidParams = -32602;
        public const int ErrorInternalError = -32603;
        public const int ErrorServerNotInitialized = -32002;
        public const int ErrorSessionCapacity = -32003;

        // Supported methods
        public const string MethodInitialize = "initialize";
        public const string MethodPing = "ping";
        public const string MethodToolsList = "tools/list";
        public const string MethodToolsCall = "tools/call";
        public const string MethodResourcesList = "resources/list";
        public const string MethodResourcesRead = "resources/read";
        public const string NotificationInitialized = "notifications/initialized";

        private static readonly HashSet<string> SupportedMethods = new HashSet<string>
        {
            MethodInitialize,
            MethodPing,
            MethodToolsList,
            MethodToolsCall,
            MethodResourcesList,
            MethodResourcesRead,
            NotificationInitialized
        };

        // Multi-session state
        private static readonly Dictionary<string, McpSession> _sessions = new Dictionary<string, McpSession>();

        /// <summary>
        /// Maximum number of retained MCP sessions. Sessions are lightweight metadata,
        /// so the default is intentionally generous. Recently active sessions are never
        /// evicted to admit another client.
        /// </summary>
        public static int MaxRetainedSessions { get; set; } = 64;

        /// <summary>
        /// Idle age required before a session may be pruned under capacity pressure.
        /// Sessions are not reaped below capacity. Default: 60 minutes.
        /// </summary>
        public static int SessionPruneIdleThresholdMinutes { get; set; } = 60;

        /// <summary>
        /// Minimum idle age before an anonymous session from the same client family may
        /// be recycled at capacity. This is a pressure-only safety net for clients that
        /// abandon session IDs; it never reaps sessions below capacity. Default: 15 seconds.
        /// </summary>
        public static int AnonymousSessionReclaimGraceSeconds { get; set; } = 15;

        /// <summary>Backward-compatible alias for the previous setting name.</summary>
        [Obsolete("Use MaxRetainedSessions. This value counts retained sessions, not live connections.")]
        public static int MaxConcurrentClients
        {
            get => MaxRetainedSessions;
            set => MaxRetainedSessions = value;
        }

        private static readonly Dictionary<string, int> _nextInstanceNumberByApp =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private const string ClientMetaKey = "i1rr-unity-mcp/client";
        private const int MaxIdentityValueLength = 128;

        // Dispatch delegates — session-aware (second parameter is sessionId)
        public static Func<JObject, string, JToken> OnToolsList { get; set; }
        public static Func<JObject, string, JToken> OnToolsCall { get; set; }
        public static Func<JObject, string, JToken> OnResourcesList { get; set; }
        public static Func<JObject, string, JToken> OnResourcesRead { get; set; }

        /// <summary>
        /// Returns the session ID of the single active session (backward compat).
        /// Returns null if no sessions exist; returns the first session ID if multiple.
        /// Prefer <see cref="GetSession"/> for multi-client scenarios.
        /// </summary>
        public static string SessionId
        {
            get
            {
                if (_sessions.Count == 0) return null;
                if (_sessions.Count == 1) return _sessions.Values.First().Id;
                // Multiple sessions — return first (callers should use GetSession instead)
                return _sessions.Values.First().Id;
            }
        }

        /// <summary>
        /// Returns true if any session is fully initialized (backward compat).
        /// Prefer <see cref="IsSessionInitialized"/> for multi-client scenarios.
        /// </summary>
        public static bool IsInitialized
        {
            get
            {
                foreach (var s in _sessions.Values)
                    if (s.IsInitialized) return true;
                return false;
            }
        }

        /// <summary>
        /// Returns the protocol version of the single active session (backward compat).
        /// Returns baseline if no sessions or multiple sessions exist.
        /// </summary>
        public static string ProtocolVersion
        {
            get
            {
                if (_sessions.Count == 1) return _sessions.Values.First().ProtocolVersion;
                return ProtocolVersionBaseline;
            }
        }

        /// <summary>Number of active sessions.</summary>
        public static int SessionCount => _sessions.Count;

        /// <summary>
        /// Returns true if the retained-session limit has been reached.
        /// </summary>
        public static bool IsSessionLimitReached => _sessions.Count >= MaxRetainedSessions;

        /// <summary>
        /// Returns the session with the given ID, or null if not found.
        /// </summary>
        public static McpSession GetSession(string sessionId)
        {
            if (sessionId != null && _sessions.TryGetValue(sessionId, out var session))
                return session;
            return null;
        }

        /// <summary>
        /// Returns whether a specific session is fully initialized.
        /// </summary>
        public static bool IsSessionInitialized(string sessionId)
        {
            return sessionId != null && _sessions.TryGetValue(sessionId, out var s) && s.IsInitialized;
        }

        /// <summary>
        /// Returns IDs of all active sessions. Caller should not cache — snapshot only.
        /// </summary>
        public static string[] GetSessionIds()
        {
            var ids = new string[_sessions.Count];
            _sessions.Keys.CopyTo(ids, 0);
            return ids;
        }

        /// <summary>Returns a stable snapshot ordered by first creation time.</summary>
        public static McpSession[] GetSessionsSnapshot()
        {
            return _sessions.Values.OrderBy(s => s.CreatedAtUtc).ToArray();
        }

        /// <summary>
        /// Resets all session state. Call on full shutdown or session termination.
        /// </summary>
        public static void Reset()
        {
            _sessions.Clear();
            _nextInstanceNumberByApp.Clear();
        }

        /// <summary>
        /// Restores a session after domain reload. Allows clients to continue
        /// using the same session without reinitializing.
        /// </summary>
        public static void RestoreSession(string sessionId, string protocolVersion, bool isInitialized = true)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            var session = new McpSession(sessionId, protocolVersion)
            {
                IsInitialized = isInitialized,
                AppInstanceNumber = AllocateInstanceNumber("Unknown MCP Client")
            };
            _sessions[sessionId] = session;
        }

        /// <summary>Serializes all protocol and observability state needed across domain reload.</summary>
        public static JObject SerializeSession(McpSession session)
        {
            if (session == null) return null;

            return new JObject
            {
                ["id"] = session.Id,
                ["protocolVersion"] = session.ProtocolVersion,
                ["initialized"] = session.IsInitialized,
                ["clientCapabilities"] = session.ClientCapabilities.DeepClone(),
                ["createdAtUtc"] = session.CreatedAtUtc.ToString("o"),
                ["lastActivityUtc"] = session.LastActivityUtc.ToString("o"),
                ["clientName"] = session.ClientName,
                ["clientTitle"] = session.ClientTitle,
                ["clientVersion"] = session.ClientVersion,
                ["displayName"] = session.DisplayName,
                ["clientKind"] = session.ClientKind,
                ["appInstanceNumber"] = session.AppInstanceNumber,
                ["clientInstanceId"] = session.ClientInstanceId,
                ["clientInstanceName"] = session.ClientInstanceName,
                ["runId"] = session.RunId,
                ["userAgent"] = session.UserAgent,
                ["remoteAddress"] = session.RemoteAddress,
                ["requestCount"] = session.RequestCount,
                ["errorCount"] = session.ErrorCount,
                ["lastMethod"] = session.LastMethod,
                ["lastTool"] = session.LastTool
            };
        }

        /// <summary>Restores a session previously produced by <see cref="SerializeSession"/>.</summary>
        public static void RestoreSession(JObject state)
        {
            var sessionId = SanitizeIdentityValue(state?["id"]?.Value<string>());
            if (string.IsNullOrEmpty(sessionId)) return;

            var session = new McpSession(sessionId, state["protocolVersion"]?.Value<string>())
            {
                IsInitialized = state["initialized"]?.Value<bool>() ?? false,
                ClientCapabilities = (JObject)(state["clientCapabilities"] as JObject)?.DeepClone() ?? new JObject(),
                ClientName = SanitizeIdentityValue(state["clientName"]?.Value<string>()),
                ClientTitle = SanitizeIdentityValue(state["clientTitle"]?.Value<string>()),
                ClientVersion = SanitizeIdentityValue(state["clientVersion"]?.Value<string>()),
                DisplayName = SanitizeIdentityValue(state["displayName"]?.Value<string>()) ?? "Unknown MCP Client",
                ClientKind = NormalizeClientKind(state["clientKind"]?.Value<string>()),
                AppInstanceNumber = Math.Max(1, state["appInstanceNumber"]?.Value<int>() ?? 1),
                ClientInstanceId = SanitizeIdentityValue(state["clientInstanceId"]?.Value<string>()),
                ClientInstanceName = SanitizeIdentityValue(state["clientInstanceName"]?.Value<string>()),
                RunId = SanitizeIdentityValue(state["runId"]?.Value<string>()),
                UserAgent = SanitizeIdentityValue(state["userAgent"]?.Value<string>()),
                RemoteAddress = SanitizeIdentityValue(state["remoteAddress"]?.Value<string>()),
                RequestCount = Math.Max(0, state["requestCount"]?.Value<long>() ?? 0),
                ErrorCount = Math.Max(0, state["errorCount"]?.Value<long>() ?? 0),
                LastMethod = SanitizeIdentityValue(state["lastMethod"]?.Value<string>()),
                LastTool = SanitizeIdentityValue(state["lastTool"]?.Value<string>())
            };

            session.CreatedAtUtc = ParseUtc(state["createdAtUtc"]?.Value<string>(), DateTime.UtcNow);
            session.LastActivityUtc = ParseUtc(state["lastActivityUtc"]?.Value<string>(), session.CreatedAtUtc);
            _sessions[sessionId] = session;
            ReserveInstanceNumber(session.DisplayName, session.AppInstanceNumber);
        }

        /// <summary>
        /// Validates the MCP-Session-Id header against active sessions.
        /// Returns null if valid, or an error message string if invalid.
        /// Out parameter <paramref name="httpStatus"/> indicates the HTTP status to return.
        /// Out parameter <paramref name="matchedSessionId"/> is set to the matched session ID when valid.
        /// </summary>
        public static string ValidateSessionHeader(string headerValue, out int httpStatus, out string matchedSessionId)
        {
            matchedSessionId = null;

            if (headerValue != null)
            {
                // Header present: must match an active session
                if (!_sessions.ContainsKey(headerValue))
                {
                    httpStatus = 404;
                    return "Invalid or expired session ID";
                }
                matchedSessionId = headerValue;
                httpStatus = 200;
                return null;
            }

            // Header absent — allow through (could be initialize or re-initialize)
            httpStatus = 200;
            return null;
        }

        /// <summary>
        /// Validates the MCP-Protocol-Version header value.
        /// Returns null if valid, or an error message if the version is unsupported.
        /// </summary>
        public static string ValidateProtocolVersionHeader(string headerValue)
        {
            if (string.IsNullOrEmpty(headerValue))
                return null; // Absent means assume baseline

            if (headerValue != ProtocolVersionBaseline && headerValue != ProtocolVersionNext)
                return $"Unsupported protocol version: {headerValue}";

            return null;
        }

        /// <summary>
        /// Parses raw JSON into a single request or a batch of requests.
        /// Returns a tuple: (requests, errorResponse).
        /// If errorResponse is non-null, parsing failed and the error should be returned.
        /// The optional <paramref name="sessionId"/> is used to look up the session's protocol version
        /// for batch request validation. Pass null for pre-init requests.
        /// </summary>
        public static (List<JObject> requests, JObject errorResponse) ParseRequest(string json, string sessionId = null)
            => ParseRequest(json, sessionId, out _);

        public static (List<JObject> requests, JObject errorResponse) ParseRequest(string json, string sessionId, out bool isBatch)
        {
            isBatch = false;
            if (string.IsNullOrWhiteSpace(json))
            {
                return (null, BuildErrorResponse(null, ErrorParseError, "Empty request body"));
            }

            JToken token;
            try
            {
                token = JToken.Parse(json);
            }
            catch (JsonReaderException)
            {
                return (null, BuildErrorResponse(null, ErrorParseError, "Invalid JSON"));
            }

            if (token.Type == JTokenType.Array)
            {
                isBatch = true;
                // Batch request — check protocol version
                var protocolVersion = ProtocolVersionBaseline;
                if (sessionId != null && _sessions.TryGetValue(sessionId, out var session))
                    protocolVersion = session.ProtocolVersion;

                if (protocolVersion == ProtocolVersionNext)
                {
                    return (null, BuildErrorResponse(null, ErrorInvalidRequest,
                        "Batch requests are not supported in protocol version " + ProtocolVersionNext));
                }

                var array = (JArray)token;
                if (array.Count == 0)
                {
                    return (null, BuildErrorResponse(null, ErrorInvalidRequest, "Empty batch array"));
                }

                var requests = new List<JObject>(array.Count);
                foreach (var item in array)
                {
                    if (item.Type != JTokenType.Object)
                    {
                        return (null, BuildErrorResponse(null, ErrorInvalidRequest,
                            "Batch array contains non-object element"));
                    }
                    requests.Add((JObject)item);
                }
                return (requests, null);
            }

            if (token.Type == JTokenType.Object)
            {
                return (new List<JObject> { (JObject)token }, null);
            }

            return (null, BuildErrorResponse(null, ErrorInvalidRequest,
                "Request must be a JSON object or array"));
        }

        /// <summary>
        /// Validates JSON-RPC 2.0 structure of a parsed request object.
        /// Returns an error response JObject if invalid, or null if valid.
        /// </summary>
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

        /// <summary>
        /// Builds the InitializeResult response payload including capabilities and server info.
        /// </summary>
        public static JObject BuildInitializeResult(McpSession session)
        {
            return new JObject
            {
                ["protocolVersion"] = session.ProtocolVersion,
#if UNITY_EDITOR
                ["instructions"] = "The Editor may advertise a focused catalog. Use discover_tools for specialist names and schemas, and list_tests (when available) to find runnable tests.",
#else
                ["instructions"] = "Use tools/list for tools available in this development player.",
#endif
                ["capabilities"] = new JObject
                {
                    ["tools"] = new JObject
                    {
                        ["listChanged"] = false
                    },
                    ["resources"] = new JObject
                    {
                        ["subscribe"] = false,
                        ["listChanged"] = false
                    }
                },
                ["serverInfo"] = new JObject
                {
                    ["name"] = "i1rr-unity-mcp",
                    ["version"] = ServerVersion
                }
            };
        }

        /// <summary>
        /// Builds a tool result content block with a single text entry.
        /// </summary>
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

        /// <summary>
        /// High-level request dispatch. Validates pre-init gate, routes to method handler.
        /// Returns null for notifications that require no response.
        /// <paramref name="sessionId"/> is the validated session ID from the header (null for new clients).
        /// The out parameter <paramref name="newSessionId"/> is set when initialize creates a new session.
        /// </summary>
        public static JObject ProcessRequest(JObject request, string sessionId, out string newSessionId,
            McpClientTransportInfo transportInfo = null)
            => ProcessRequestWithContext(request, CreateLegacyRequestContext(request, sessionId, transportInfo),
                out newSessionId);

        /// <summary>Captures negotiated legacy metadata once, before dispatch or deferred work.</summary>
        public static McpRequestContext CreateLegacyRequestContext(JObject request, string sessionId,
            McpClientTransportInfo transportInfo = null, string authenticatedPrincipal = null,
            System.Threading.CancellationToken cancellation = default)
        {
            var session = GetSession(sessionId);
            var parameters = request?["params"] as JObject;
            var clientInfo = session == null ? parameters?["clientInfo"] as JObject : new JObject
            {
                ["name"] = session.ClientName,
                ["title"] = session.ClientTitle,
                ["version"] = session.ClientVersion
            };
            return new McpRequestContext(
                request?["method"]?.Type == JTokenType.String ? (string)request["method"] : null,
                request?["id"], session?.ProtocolVersion ?? ProtocolVersionBaseline, sessionId,
                session?.IsInitialized ?? false, session?.ClientCapabilities ?? parameters?["capabilities"] as JObject,
                clientInfo, transportInfo ?? (session == null ? null : new McpClientTransportInfo
                { UserAgent = session.UserAgent, RemoteAddress = session.RemoteAddress }),
                authenticatedPrincipal, cancellation);
        }

        /// <summary>Shared ordinary/deferred envelope and admission checks, before any tool invocation.</summary>
        public static JObject ValidateRequestForDispatch(JObject request, McpRequestContext context)
        {
            var error = ValidateJsonRpc(request);
            if (error != null) return error;
            if (context == null) throw new ArgumentNullException(nameof(context));
            var id = request["id"];
            var method = (string)request["method"];
            if (context.Method != method || !JToken.DeepEquals(context.RequestId, id))
                return BuildErrorResponse(id, ErrorInvalidRequest, "Request context does not match this request");
            if (context.IsModern) return ValidateModernEnvelope(request, context);
            if (!context.IsInitialized && method != MethodInitialize && method != NotificationInitialized)
                return BuildErrorResponse(id, ErrorServerNotInitialized, "Server not initialized");
            return null;
        }

        /// <summary>Dispatches using captured metadata; retained legacy callbacks receive the original session ID.</summary>
        public static JObject ProcessRequestWithContext(JObject request, McpRequestContext context,
            out string newSessionId)
        {
            newSessionId = null;

            // Validate JSON-RPC structure
            var validationError = ValidateJsonRpc(request);
            if (validationError != null)
                return validationError;

            if (context == null) throw new ArgumentNullException(nameof(context));
            if (context.IsModern) return ProcessModernRequest(request, context);
            var sessionId = context.LegacySessionId;
            var transportInfo = context.TransportInfo;

            var method = request["method"].Value<string>();
            var id = request["id"];
            bool isNotification = id == null;

            if (method != MethodInitialize)
                RecordRequest(sessionId, method, ExtractToolName(request));

            // Update last activity timestamp for the matched session
            if (sessionId != null && _sessions.TryGetValue(sessionId, out var activeSession))
                activeSession.LastActivityUtc = DateTime.UtcNow;

            // Use the same captured admission state as deferred requests.
            var admissionError = ValidateRequestForDispatch(request, context);
            if (admissionError != null)
            {
                if (isNotification)
                    return null;

                return TrackResponse(sessionId, admissionError);
            }

            // Check for unknown methods
            if (!SupportedMethods.Contains(method))
            {
                if (isNotification)
                    return null;

                return TrackResponse(sessionId,
                    BuildErrorResponse(id, ErrorMethodNotFound, $"Method not found: {method}"));
            }

            // Route to handler
            switch (method)
            {
                case MethodInitialize:
                    return HandleInitialize(request, sessionId, transportInfo, out newSessionId);

                case NotificationInitialized:
                    HandleNotificationInitialized(sessionId);
                    return null; // Notification — no response

                case MethodPing:
                    if (isNotification)
                        return null;
                    return BuildResponse(id, new JObject());

                case MethodToolsList:
                    if (isNotification)
                        return null;
                    return TrackResponse(sessionId, HandleToolsList(request, sessionId));

                case MethodToolsCall:
                    if (isNotification)
                        return null;
                    return TrackResponse(sessionId, HandleToolsCall(request, sessionId));

                case MethodResourcesList:
                    if (isNotification)
                        return null;
                    return TrackResponse(sessionId, HandleResourcesList(request, sessionId));

                case MethodResourcesRead:
                    if (isNotification)
                        return null;
                    return TrackResponse(sessionId, HandleResourcesRead(request, sessionId));

                default:
                    if (isNotification)
                        return null;
                    return TrackResponse(sessionId,
                        BuildErrorResponse(id, ErrorMethodNotFound, $"Method not found: {method}"));
            }
        }

        /// <summary>
        /// Processes a batch of requests. Returns a list of responses (excluding notifications).
        /// </summary>
        public static (List<JObject> responses, string newSessionId) ProcessBatch(List<JObject> requests,
            string sessionId, McpClientTransportInfo transportInfo = null)
        {
            var responses = new List<JObject>();
            string newSessionId = null;

            foreach (var request in requests)
            {
                var response = ProcessRequest(request, sessionId, out var newSid, transportInfo);
                if (newSid != null)
                {
                    newSessionId = newSid;
                    sessionId = newSid; // subsequent requests in batch use the new session
                }

                if (response != null)
                    responses.Add(response);
            }

            return (responses, newSessionId);
        }

        /// <summary>
        /// Reports whether initialization can reuse an existing logical instance, reclaim an
        /// abandoned anonymous session from the same client family, or recycle a stale slot.
        /// This preflight is non-mutating; the matching slot is replaced during initialization.
        /// </summary>
        public static bool TryPrepareInitialize(JObject request, string currentSessionId, out string errorMessage,
            McpClientTransportInfo transportInfo = null)
        {
            errorMessage = null;

            if (currentSessionId != null && _sessions.ContainsKey(currentSessionId))
                return true;

            var descriptor = ParseClientDescriptor(request);
            var reusable = FindSessionByStableInstance(descriptor);
            if (reusable != null)
            {
                if (reusable.InFlightRequests == 0)
                    return true;

                errorMessage = $"Client instance '{descriptor.DisplayName}' is reconnecting while its previous session is busy.";
                return false;
            }

            var capacity = Math.Max(1, MaxRetainedSessions);
            if (_sessions.Count < capacity)
                return true;

            var now = DateTime.UtcNow;
            if (FindAnonymousFamilyCandidate(descriptor, transportInfo, now) != null ||
                FindStaleCandidate(now) != null)
                return true;

            errorMessage = $"Maximum retained MCP sessions ({capacity}) reached. " +
                           "No recyclable session from this client family is available and " +
                           $"no session has been idle for {Math.Max(0, SessionPruneIdleThresholdMinutes)} minutes. " +
                           "Retry later; this is a transient capacity condition.";
            return false;
        }

        private static JObject HandleInitialize(JObject request, string currentSessionId,
            McpClientTransportInfo transportInfo, out string newSessionId)
        {
            var id = request["id"];
            newSessionId = null;

            if (!TryPrepareInitialize(request, currentSessionId, out var capacityError, transportInfo))
                return BuildErrorResponse(id, ErrorSessionCapacity, capacityError);

            var descriptor = ParseClientDescriptor(request);
            McpSession slotToRemove = null;
            McpSession identitySource = null;
            string replacementReason = null;
            if (currentSessionId != null)
                _sessions.TryGetValue(currentSessionId, out slotToRemove);
            if (slotToRemove != null)
            {
                identitySource = slotToRemove;
                replacementReason = "reinitialize";
            }
            else
            {
                slotToRemove = FindSessionByStableInstance(descriptor);
                if (slotToRemove != null)
                {
                    identitySource = slotToRemove;
                    replacementReason = "stable-instance reconnect";
                }
            }

            if (slotToRemove == null && _sessions.Count >= Math.Max(1, MaxRetainedSessions))
            {
                var now = DateTime.UtcNow;
                slotToRemove = FindAnonymousFamilyCandidate(descriptor, transportInfo, now);
                if (slotToRemove != null)
                {
                    identitySource = slotToRemove;
                    replacementReason = "same-client reclaim";
                }
                else
                {
                    slotToRemove = FindStaleCandidate(now);
                    replacementReason = slotToRemove != null ? "stale reclaim" : null;
                }
            }

            if (slotToRemove != null)
                _sessions.Remove(slotToRemove.Id);

            // Determine protocol version from client preference
            var protocolVersion = ProtocolVersionBaseline;
            var clientParams = request["params"] as JObject;
            if (clientParams != null)
            {
                var clientVersion = clientParams["protocolVersion"]?.Value<string>();
                if (clientVersion == ProtocolVersionNext)
                    protocolVersion = ProtocolVersionNext;
            }

            // Create new session
            var session = new McpSession(Guid.NewGuid().ToString("N"), protocolVersion);
            session.ClientCapabilities = (JObject)(clientParams?["capabilities"] as JObject)?.DeepClone() ?? new JObject();
            ApplyClientDescriptor(session, descriptor, identitySource);
            session.UserAgent = SanitizeIdentityValue(transportInfo?.UserAgent) ?? identitySource?.UserAgent;
            session.RemoteAddress = SanitizeIdentityValue(transportInfo?.RemoteAddress) ?? identitySource?.RemoteAddress;
            session.RequestCount = (identitySource?.RequestCount ?? 0) + 1;
            session.ErrorCount = identitySource?.ErrorCount ?? 0;
            session.LastMethod = MethodInitialize;
            if (identitySource != null)
                session.CreatedAtUtc = identitySource.CreatedAtUtc;

            _sessions[session.Id] = session;
            newSessionId = session.Id;

            if (slotToRemove != null)
            {
                UnityEngine.Debug.Log($"[MCP] {replacementReason}: {session.DisplayName} " +
                                      $"#{session.AppInstanceNumber} replaced session {slotToRemove.Id} " +
                                      $"with {session.Id}.");
            }

            // Build result (not yet fully initialized — waiting for notifications/initialized)
            var result = BuildInitializeResult(session);
            return BuildResponse(id, result);
        }

        private static void HandleNotificationInitialized(string sessionId)
        {
            // Transition to ready state for the specific session
            if (sessionId != null && _sessions.TryGetValue(sessionId, out var session))
            {
                session.IsInitialized = true;
            }
        }

        private sealed class McpClientDescriptor
        {
            public bool HasClientInfo;
            public string Name;
            public string Title;
            public string Version;
            public string DisplayName;
            public string Kind;
            public string InstanceId;
            public string InstanceName;
            public string RunId;
        }

        private static McpClientDescriptor ParseClientDescriptor(JObject request)
        {
            var descriptor = new McpClientDescriptor();
            var clientParams = request?["params"] as JObject;
            var clientInfo = clientParams?["clientInfo"] as JObject;
            descriptor.HasClientInfo = clientInfo != null;
            descriptor.Name = SanitizeIdentityValue(clientInfo?["name"]?.Value<string>());
            descriptor.Title = SanitizeIdentityValue(clientInfo?["title"]?.Value<string>());
            descriptor.Version = SanitizeIdentityValue(clientInfo?["version"]?.Value<string>());

            var meta = clientParams?["_meta"]?[ClientMetaKey] as JObject;
            descriptor.InstanceId = SanitizeIdentityValue(meta?["instanceId"]?.Value<string>());
            descriptor.InstanceName = SanitizeIdentityValue(meta?["instanceName"]?.Value<string>());
            descriptor.RunId = SanitizeIdentityValue(meta?["runId"]?.Value<string>());

            var requestedDisplayName = SanitizeIdentityValue(meta?["displayName"]?.Value<string>());
            descriptor.DisplayName = requestedDisplayName ?? GetFriendlyClientName(descriptor.Title, descriptor.Name);
            descriptor.Kind = NormalizeClientKind(meta?["kind"]?.Value<string>());
            if (descriptor.Kind == "unknown")
                descriptor.Kind = InferClientKind(descriptor.DisplayName, descriptor.Name);

            return descriptor;
        }

        private static void ApplyClientDescriptor(McpSession session, McpClientDescriptor descriptor,
            McpSession replacedSession)
        {
            bool usePrevious = !descriptor.HasClientInfo && replacedSession != null;
            session.ClientName = usePrevious ? replacedSession.ClientName : descriptor.Name;
            session.ClientTitle = usePrevious ? replacedSession.ClientTitle : descriptor.Title;
            session.ClientVersion = usePrevious ? replacedSession.ClientVersion : descriptor.Version;
            session.DisplayName = usePrevious ? replacedSession.DisplayName : descriptor.DisplayName;
            session.ClientKind = usePrevious ? replacedSession.ClientKind : descriptor.Kind;
            session.ClientInstanceId = descriptor.InstanceId ?? replacedSession?.ClientInstanceId;
            session.ClientInstanceName = descriptor.InstanceName ?? replacedSession?.ClientInstanceName;
            session.RunId = descriptor.RunId ?? replacedSession?.RunId;
            session.AppInstanceNumber = replacedSession?.AppInstanceNumber ??
                                        AllocateInstanceNumber(session.DisplayName);
        }

        private static McpSession FindSessionByStableInstance(McpClientDescriptor descriptor)
        {
            if (descriptor == null || string.IsNullOrEmpty(descriptor.InstanceId))
                return null;

            return _sessions.Values.FirstOrDefault(session =>
                string.Equals(session.ClientInstanceId, descriptor.InstanceId, StringComparison.Ordinal) &&
                string.Equals(session.ClientName ?? session.DisplayName,
                    descriptor.Name ?? descriptor.DisplayName, StringComparison.OrdinalIgnoreCase));
        }

        private static McpSession FindAnonymousFamilyCandidate(McpClientDescriptor descriptor,
            McpClientTransportInfo transportInfo, DateTime now)
        {
            if (descriptor == null || string.IsNullOrEmpty(descriptor.Name))
                return null;

            var grace = TimeSpan.FromSeconds(Math.Max(0, AnonymousSessionReclaimGraceSeconds));
            return _sessions.Values
                .Where(session => session.InFlightRequests == 0 &&
                                  string.IsNullOrEmpty(session.ClientInstanceId) &&
                                  now - session.LastActivityUtc >= grace &&
                                  IsSameAnonymousClientFamily(session, descriptor, transportInfo))
                .OrderBy(session => session.LastActivityUtc)
                .FirstOrDefault();
        }

        private static bool IsSameAnonymousClientFamily(McpSession session, McpClientDescriptor descriptor,
            McpClientTransportInfo transportInfo)
        {
            if (!string.Equals(session.ClientName, descriptor.Name, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!OptionalIdentityMatches(session.ClientTitle, descriptor.Title) ||
                !OptionalIdentityMatches(session.ClientVersion, descriptor.Version) ||
                !OptionalIdentityMatches(session.UserAgent, SanitizeIdentityValue(transportInfo?.UserAgent)) ||
                !OptionalIdentityMatches(session.RemoteAddress, SanitizeIdentityValue(transportInfo?.RemoteAddress)))
                return false;

            return true;
        }

        private static bool OptionalIdentityMatches(string existing, string incoming)
        {
            return string.IsNullOrEmpty(existing) || string.IsNullOrEmpty(incoming) ||
                   string.Equals(existing, incoming, StringComparison.OrdinalIgnoreCase);
        }

        private static McpSession FindStaleCandidate(DateTime now)
        {
            var threshold = TimeSpan.FromMinutes(Math.Max(0, SessionPruneIdleThresholdMinutes));
            return _sessions.Values
                .Where(session => session.InFlightRequests == 0 && now - session.LastActivityUtc >= threshold)
                .OrderBy(session => session.LastActivityUtc)
                .FirstOrDefault();
        }

        private static int AllocateInstanceNumber(string displayName)
        {
            displayName = displayName ?? "Unknown MCP Client";
            _nextInstanceNumberByApp.TryGetValue(displayName, out var lastNumber);
            var nextNumber = lastNumber + 1;
            _nextInstanceNumberByApp[displayName] = nextNumber;
            return nextNumber;
        }

        private static void ReserveInstanceNumber(string displayName, int instanceNumber)
        {
            displayName = displayName ?? "Unknown MCP Client";
            _nextInstanceNumberByApp.TryGetValue(displayName, out var existing);
            if (instanceNumber > existing)
                _nextInstanceNumberByApp[displayName] = instanceNumber;
        }

        private static string GetFriendlyClientName(string title, string name)
        {
            var candidate = !string.IsNullOrEmpty(title) ? title : name;
            if (string.IsNullOrEmpty(candidate)) return "Unknown MCP Client";

            // Normalize the well-known applications users care about even when the
            // client sends a longer product title (for example, "OpenAI Codex").
            var normalized = candidate.ToLowerInvariant();
            if (normalized.Contains("codex")) return "Codex";
            if (normalized.Contains("claude")) return "Claude";
            if (normalized.Contains("visual studio code") || normalized.Contains("vscode")) return "VS Code";
            if (normalized.Contains("rider")) return "Rider";
            return candidate;
        }

        private static string InferClientKind(string displayName, string name)
        {
            var value = ((displayName ?? "") + " " + (name ?? "")).ToLowerInvariant();
            if (value.Contains("codex") || value.Contains("claude")) return "ai-agent";
            if (value.Contains("code") || value.Contains("vscode") || value.Contains("rider")) return "ide";
            return "unknown";
        }

        private static string NormalizeClientKind(string value)
        {
            value = SanitizeIdentityValue(value)?.ToLowerInvariant();
            switch (value)
            {
                case "ai-agent":
                case "automation":
                case "ide":
                case "test-runner":
                    return value;
                default:
                    return "unknown";
            }
        }

        private static string SanitizeIdentityValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var sanitized = new string(value.Trim()
                .Where(c => !char.IsControl(c))
                .Take(MaxIdentityValueLength)
                .ToArray());
            return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized;
        }

        private static DateTime ParseUtc(string value, DateTime fallback)
        {
            if (DateTime.TryParse(value, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                return parsed.ToUniversalTime();
            return fallback;
        }

        private static string ExtractToolName(JObject request)
        {
            if (request?["method"]?.Value<string>() != MethodToolsCall) return null;
            return SanitizeIdentityValue(request["params"]?["name"]?.Value<string>());
        }

        private static void RecordRequest(string sessionId, string method, string toolName)
        {
            if (sessionId == null || !_sessions.TryGetValue(sessionId, out var session)) return;
            session.LastActivityUtc = DateTime.UtcNow;
            session.RequestCount++;
            session.LastMethod = SanitizeIdentityValue(method);
            if (!string.IsNullOrEmpty(toolName)) session.LastTool = toolName;
        }

        /// <summary>Marks a deferred request as active for dashboard telemetry.</summary>
        public static void BeginDeferredRequest(string sessionId, string method, string toolName)
        {
            RecordRequest(sessionId, method, toolName);
            if (sessionId != null && _sessions.TryGetValue(sessionId, out var session))
                session.InFlightRequests++;
        }

        /// <summary>Completes deferred-request telemetry.</summary>
        public static void CompleteDeferredRequest(string sessionId, bool isError)
        {
            if (sessionId == null || !_sessions.TryGetValue(sessionId, out var session)) return;
            if (session.InFlightRequests > 0) session.InFlightRequests--;
            if (isError) session.ErrorCount++;
            session.LastActivityUtc = DateTime.UtcNow;
        }

        private static bool IsErrorResponse(JObject response)
        {
            return response?["error"] != null || response?["result"]?["isError"]?.Value<bool>() == true;
        }

        private static JObject TrackResponse(string sessionId, JObject response)
        {
            if (IsErrorResponse(response) && sessionId != null &&
                _sessions.TryGetValue(sessionId, out var session))
                session.ErrorCount++;
            return response;
        }

        private static JObject HandleToolsList(JObject request, string sessionId)
        {
            var id = request["id"];

            if (OnToolsList != null)
            {
                try
                {
                    var result = OnToolsList(request, sessionId);
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

        private static JObject HandleToolsCall(JObject request, string sessionId)
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
                    var result = OnToolsCall(request, sessionId);
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

        private static JObject HandleResourcesList(JObject request, string sessionId)
        {
            var id = request["id"];

            if (OnResourcesList != null)
            {
                try
                {
                    var result = OnResourcesList(request, sessionId);
                    return BuildResponse(id, result);
                }
                catch (Exception ex)
                {
                    return BuildErrorResponse(id, ErrorInternalError, ex.Message);
                }
            }

            return BuildResponse(id, new JObject { ["resources"] = new JArray() });
        }

        private static JObject HandleResourcesRead(JObject request, string sessionId)
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
                    var result = OnResourcesRead(request, sessionId);
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

        /// <summary>
        /// Terminates a specific session. After this call, the session ID is invalidated.
        /// </summary>
        public static void TerminateSession(string sessionId)
        {
            if (sessionId != null)
                _sessions.Remove(sessionId);
        }

    }
}
