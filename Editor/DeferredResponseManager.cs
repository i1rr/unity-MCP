using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace I1rr.UnityMcp.Editor
{
    /// <summary>
    /// Manages pending deferred tool responses — tracking, timeout ticking, cleanup,
    /// and per-connection limit enforcement.
    /// </summary>
    internal static class DeferredResponseManager
    {
        // --- Constants ---
        internal const int MaxDeferredTotal = 8;
        internal const int MaxDeferredPerConnection = 2;

        // --- State ---
        internal static readonly List<PendingDeferredResponse> _deferredResponses = new List<PendingDeferredResponse>();

        // --- Data Structures ---
        internal sealed class PendingDeferredResponse
        {
            public int ClientId;
            public HttpListenerContext Context;
            public JToken RequestId;
            public Dictionary<string, string> Headers;
            public DeferredResult Deferred;
            public DateTime Deadline;
            public int ConnectionHash;
            public string SessionId;
            public string ToolName;
            public McpSseStream Stream;
        }

        // --- Deferred Tool Call Handling ---

        internal static void HandleDeferredToolCall(McpEditorServer.McpRequest request, JObject jsonRpcReq,
            string toolName, JObject paramsObj, Dictionary<string, string> headers, string sessionId,
            McpRequestContext requestContext = null)
        {
            var id = jsonRpcReq["id"];

            requestContext = requestContext ?? McpProtocol.CreateLegacyRequestContext(jsonRpcReq, sessionId);
            var validationError = McpProtocol.ValidateRequestForDispatch(jsonRpcReq, requestContext);
            if (validationError != null)
            {
                EditorResponseWriter.EnqueueJsonRpcResponse(request,
                    requestContext.IsModern ? McpProtocol.ModernHttpStatus(validationError) : 200, validationError, headers);
                return;
            }

            if (_deferredResponses.Count >= MaxDeferredTotal)
            {
                var errResp = McpProtocol.BuildErrorResponse(id, -32000, "Too many concurrent deferred requests");
                EditorResponseWriter.EnqueueJsonRpcResponse(request,
                    requestContext.IsModern ? McpProtocol.ModernHttpStatus(errResp) : 200, errResp, headers);
                return;
            }

            int connHash = request.ClientId >= 0 ? request.ClientId : GetFallbackConnectionHash(request.Context);
            int connCount = 0;
            for (int i = 0; i < _deferredResponses.Count; i++)
            {
                if (_deferredResponses[i].ConnectionHash == connHash)
                    connCount++;
            }
            if (connCount >= MaxDeferredPerConnection)
            {
                var errResp = McpProtocol.BuildErrorResponse(id, -32000,
                    "Too many deferred requests on this connection. Wait for existing deferred calls to complete.");
                EditorResponseWriter.EnqueueJsonRpcResponse(request,
                    requestContext.IsModern ? McpProtocol.ModernHttpStatus(errResp) : 200, errResp, headers);
                return;
            }

            if (sessionId != null)
                headers["MCP-Session-Id"] = sessionId;

            McpSseStream stream = null;
            if (requestContext.IsModern)
            {
                McpRequestActivity.Begin(requestContext, toolName);
                stream = EditorStreamingResponses.TryOpen(request, headers, requestContext);
                if (stream == null)
                {
                    McpRequestActivity.Finish(requestContext, "not_started");
                    EditorResponseWriter.EnqueueJsonRpcResponse(request, 503, McpProtocol.BuildErrorResponse(id, -32000,
                        "Stream capacity reached", new JObject { ["dispatch_state"] = "not_started" }), headers);
                    return;
                }
                requestContext = stream.Context;
            }

            McpProtocol.BeginDeferredRequest(sessionId, McpProtocol.MethodToolsCall, toolName);
            var arguments = paramsObj?["arguments"] as JObject;
            object rawResult;
            try
            {
                rawResult = McpToolRegistry.InvokeRaw(toolName, arguments);
            }
            catch
            {
                stream?.Abort();
                McpProtocol.CompleteDeferredRequest(sessionId, true);
                throw;
            }

            if (rawResult is DeferredResult deferred)
            {
                deferred.CreatedAtUtc = DateTime.UtcNow;
                deferred.SessionId = sessionId;
                deferred.RequestContext = requestContext;
                _deferredResponses.Add(new PendingDeferredResponse
                {
                    ClientId = request.ClientId,
                    Context = request.Context,
                    RequestId = id,
                    Headers = headers,
                    Deferred = deferred,
                    Deadline = DateTime.UtcNow.AddSeconds(deferred.ClampedTimeout),
                    ConnectionHash = connHash,
                    SessionId = sessionId, ToolName = toolName, Stream = stream
                });
            }
            else
            {
                var toolResponse = rawResult as JObject ?? new JObject { ["result"] = "ok" };
                var wrapped = McpProtocol.WrapToolResult(toolResponse);
                var jsonRpcResp = McpProtocol.AdaptResponseForContext(McpProtocol.BuildResponse(id, wrapped), requestContext);
                if (stream != null) stream.Complete(jsonRpcResp);
                else EditorResponseWriter.EnqueueJsonRpcResponse(request, 200, jsonRpcResp, headers);
                McpProtocol.CompleteDeferredRequest(sessionId, wrapped["isError"]?.Value<bool>() == true);
            }
        }

        // --- Tick (called each frame from main thread) ---

        internal static void TickDeferredResponses()
        {
            if (_deferredResponses.Count == 0) return;

            var now = DateTime.UtcNow;

            for (int i = _deferredResponses.Count - 1; i >= 0; i--)
            {
                var pending = _deferredResponses[i];

                if (pending.Stream?.Cancellation.IsCancellationRequested == true)
                {
                    pending.Deferred.InvokeCleanup();
                    McpProtocol.CompleteDeferredRequest(pending.SessionId, true);
                    LogCompletion(pending, false, 0, "disconnected_outcome_unknown");
                    _deferredResponses.RemoveAt(i);
                    continue;
                }

                // Check session still valid
                if (pending.SessionId != null && McpProtocol.GetSession(pending.SessionId) == null)
                {
                    pending.Deferred.InvokeCleanup();
                    var errResp = McpProtocol.BuildErrorResponse(pending.RequestId, -32000, "Session terminated");
                    SendDeferredResponse(pending, 200, errResp);
                    _deferredResponses.RemoveAt(i);
                    continue;
                }

                // Check client disconnect
                if (!IsDeferredClientAlive(pending))
                {
                    pending.Stream?.Abort();
                    pending.Deferred.InvokeCleanup();
                    CloseDeferredClient(pending);
                    McpProtocol.CompleteDeferredRequest(pending.SessionId, true);
                    LogCompletion(pending, false, 0, "disconnected");
                    _deferredResponses.RemoveAt(i);
                    continue;
                }

                // Check timeout
                if (now >= pending.Deadline)
                {
                    pending.Stream?.CancelWork();
                    pending.Deferred.InvokeCleanup();
                    var errResp = McpProtocol.BuildErrorResponse(pending.RequestId, -32000, "Deferred request timed out");
                    SendDeferredResponse(pending, 200, errResp);
                    _deferredResponses.RemoveAt(i);
                    continue;
                }

                // Check completion
                try
                {
                    if (pending.Deferred.IsComplete != null && pending.Deferred.IsComplete())
                    {
                        JObject toolResult;
                        try
                        {
                            toolResult = pending.Deferred.GetResult?.Invoke()
                                         ?? new JObject { ["result"] = "ok" };
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"[MCP] Deferred result producer threw: {ex}");
                            toolResult = McpProtocol.BuildToolResult(ex.Message, isError: true);
                        }

                        toolResult = ResponseSizeLimiter.Apply(toolResult);
                        var wrapped = McpProtocol.WrapToolResult(toolResult);
                        var jsonRpcResp = McpProtocol.BuildResponse(pending.RequestId, wrapped);
                        SendDeferredResponse(pending, 200, jsonRpcResp);
                        pending.Deferred.InvokeCleanup();
                        _deferredResponses.RemoveAt(i);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[MCP] Deferred predicate threw: {ex}");
                    pending.Stream?.CancelWork();
                    pending.Deferred.InvokeCleanup();
                    var errResp = McpProtocol.BuildErrorResponse(pending.RequestId, -32000,
                        $"Deferred predicate error: {ex.Message}");
                    SendDeferredResponse(pending, 200, errResp);
                    _deferredResponses.RemoveAt(i);
                }
            }
        }

        // --- Reload Cleanup (called from ServerLifecycleManager.StopManagedLayer) ---

        /// <summary>
        /// Stages error responses for all pending deferred requests during domain reload / shutdown.
        /// Clears the deferred response list afterwards.
        /// </summary>
        internal static void StageErrorsForReload()
        {
            foreach (var pending in _deferredResponses)
            {
                pending.Deferred.InvokeCleanup();
                McpProtocol.CompleteDeferredRequest(pending.SessionId, true);
                LogCompletion(pending, false, 0, "started_outcome_unknown");
                try
                {
                    if (pending.Stream != null)
                        pending.Stream.Complete(EditorResponseWriter.BuildInterruptedReloadResponse(pending.RequestId));
                    else if (pending.ClientId >= 0)
                    {
                        var httpResponse = EditorResponseWriter.BuildReloadErrorHttpResponse(pending.RequestId, pending.Headers);
                        NativeSocketKeeper.StageResponse(pending.ClientId, httpResponse);
                    }
                    else if (pending.Context != null)
                    {
                        // Fallback transport — write directly (no native layer)
                        var errResp = EditorResponseWriter.BuildInterruptedReloadResponse(pending.RequestId);
                        var responseBytes = Encoding.UTF8.GetBytes(errResp.ToString(Formatting.None));
                        var response = pending.Context.Response;
                        response.StatusCode = 200;
                        response.ContentType = "application/json";
                        response.ContentLength64 = responseBytes.Length;
                        response.OutputStream.Write(responseBytes, 0, responseBytes.Length);
                        response.Close();
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[MCP] Failed to stage deferred response: {ex.Message}");
                    if (pending.ClientId >= 0)
                    {
                        try { NativeSocketKeeper.CloseClient(pending.ClientId); }
                        catch { /* best effort */ }
                    }
                    else if (pending.Context != null)
                    {
                        try { pending.Context.Response.Abort(); }
                        catch { /* best effort */ }
                    }
                }
            }
            _deferredResponses.Clear();
        }

        // --- Helpers ---

        private static bool IsDeferredClientAlive(PendingDeferredResponse pending)
        {
            if (pending.ClientId >= 0)
                return NativeSocketKeeper.IsClientConnected(pending.ClientId);

            if (pending.Context != null)
            {
                try { return pending.Context.Response.OutputStream.CanWrite; }
                catch { return false; }
            }

            return false;
        }

        internal static void CloseDeferredClient(PendingDeferredResponse pending)
        {
            if (pending.ClientId >= 0)
            {
                try { NativeSocketKeeper.CloseClient(pending.ClientId); }
                catch { /* best effort */ }
            }
        }

        private static void LogCompletion(PendingDeferredResponse pending, bool success, int bytes, string error)
        {
            StructuredLogger.LogToolInvocation(null, pending.ToolName,
                (DateTime.UtcNow - pending.Deferred.CreatedAtUtc).TotalMilliseconds, success, error, resultBytes: bytes);
        }

        private static void SendDeferredResponse(PendingDeferredResponse pending, int statusCode, JObject response)
        {
            if (pending.Stream != null && response["error"] is JObject streamError)
                streamError["data"] = new JObject { ["dispatch_state"] = "started_outcome_unknown" };
            response = McpProtocol.AdaptResponseForContext(response, pending.Deferred.RequestContext);
            if (pending.Deferred.RequestContext?.IsModern == true) statusCode = McpProtocol.ModernHttpStatus(response);
            var isError = response?["error"] != null || response?["result"]?["isError"]?.Value<bool>() == true;
            McpProtocol.CompleteDeferredRequest(pending.SessionId, isError);
            var responseBytes = Encoding.UTF8.GetBytes(response.ToString(Formatting.None));
            LogCompletion(pending, !isError, responseBytes.Length, isError ? "deferred_error" : null);
            if (pending.Stream != null)
            {
                pending.Stream.Complete(response);
                return;
            }
            var headers = pending.Headers ?? new Dictionary<string, string>();
            headers["Content-Type"] = "application/json";

            var completed = new McpEditorServer.CompletedRequest
            {
                ClientId = pending.ClientId,
                Context = pending.Context,
                ResponseBytes = responseBytes,
                StatusCode = statusCode,
                StatusText = "OK",
                Headers = headers
            };

            try { McpEditorServer._outboundQueue.Add(completed); }
            catch (InvalidOperationException)
            {
                CloseDeferredClient(pending);
            }
        }

        private static int GetFallbackConnectionHash(HttpListenerContext context)
        {
            if (context == null) return 0;
            try
            {
                var endpoint = context.Request.RemoteEndPoint;
                return endpoint?.GetHashCode() ?? context.GetHashCode();
            }
            catch { return context.GetHashCode(); }
        }

    }
}
