using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace I1rr.UnityMcp.Editor
{
    /// <summary>
    /// Handles HTTP accept loops (native and fallback), request parsing, header validation,
    /// and routing of parsed requests to the protocol layer.
    /// </summary>
    internal static class EditorRequestPipeline
    {
        // --- Constants ---
        internal const int MaxRequestBodySize = 1_048_576; // 1 MB
        internal const int InboundQueueCap = 200;
        internal const int NativeDequeueTimeoutMs = 100;
        internal const string McpEndpoint = "/mcp";
        internal const string HealthEndpoint = "/health";

        // --- Accept Thread ---

        internal static void StartAcceptThread(bool useNativeTransport)
        {
            McpEditorServer._acceptThread = new Thread(useNativeTransport ? (ThreadStart)NativeAcceptLoop : FallbackAcceptLoop)
            {
                Name = "MCP-Accept",
                IsBackground = true
            };
            McpEditorServer._acceptThread.Start();
        }

        // --- Native Accept Loop ---

        private static void NativeAcceptLoop()
        {
            while (McpEditorServer._state == 1)
            {
                int clientId;
                try
                {
                    clientId = NativeSocketKeeper.Dequeue(NativeDequeueTimeoutMs);
                }
                catch (Exception)
                {
                    break;
                }

                if (clientId < 0)
                    continue; // timeout — loop and check _state

                lock (McpEditorServer._acceptGate)
                {
                    if (McpEditorServer._state != 1)
                    {
                        NativeSocketKeeper.StageResponse(clientId, EditorResponseWriter.BuildReload503HttpResponse());
                        break;
                    }
                    McpEditorServer._readingClientId = clientId;
                }

                try
                {
                    if (McpEditorServer._inboundCount >= InboundQueueCap)
                    {
                        RejectNativeClient(clientId, 503, "Service Unavailable");
                        continue;
                    }

                    var stream = new NativeSocketStream(clientId);
                    var httpReq = TcpHttpHandler.ParseRequest(stream, MaxRequestBodySize);

                    if (httpReq.Error != null)
                    {
                        RejectNativeClient(clientId, 400, "Bad Request");
                        continue;
                    }

                    Interlocked.Increment(ref McpEditorServer._inboundCount);
                    McpEditorServer._inboundQueue.Enqueue(new McpEditorServer.McpRequest
                    {
                        ClientId = clientId,
                        HttpRequest = httpReq,
                        Context = null,
                        EnqueuedAt = System.Diagnostics.Stopwatch.GetTimestamp()
                    });
                }
                catch (Exception ex)
                {
                    if (McpEditorServer._state == 1) Debug.LogError($"[MCP] Native accept error: {ex.Message}");
                    try { NativeSocketKeeper.CloseClient(clientId); }
                    catch { /* best effort */ }
                }
                finally
                {
                    lock (McpEditorServer._acceptGate) McpEditorServer._readingClientId = -1;
                }
            }
        }

        internal static void RejectNativeClient(int clientId, int statusCode, string statusText)
        {
            try
            {
                var stream = new NativeSocketStream(clientId);
                TcpHttpHandler.WriteResponse(stream, statusCode, statusText, null, null);
            }
            catch { /* ignore */ }
            finally
            {
                try { NativeSocketKeeper.CloseClient(clientId); }
                catch { /* ignore */ }
            }
        }

        // --- Fallback Accept Loop (HttpListener) ---

        private static void FallbackAcceptLoop()
        {
            while (McpEditorServer._state == 1)
            {
                HttpListenerContext context;
                try
                {
                    context = McpEditorServer._listener.GetContext();
                }
                catch (ObjectDisposedException) { break; }
                catch (HttpListenerException) { break; }

                try
                {
                    if (McpEditorServer._inboundCount >= InboundQueueCap)
                    {
                        RejectFallbackClient(context, 503, "Service Unavailable");
                        continue;
                    }

                    byte[] body = null;
                    if (context.Request.HasEntityBody)
                    {
                        if (context.Request.ContentLength64 > MaxRequestBodySize)
                        {
                            RejectFallbackClient(context, 413, "Payload Too Large");
                            continue;
                        }

                        using (var ms = new MemoryStream())
                        {
                            var buffer = BufferPool.RentBuffer(8192);
                            try
                            {
                                int bytesRead;
                                long totalRead = 0;
                                var inputStream = context.Request.InputStream;

                                while ((bytesRead = inputStream.Read(buffer, 0, 8192)) > 0)
                                {
                                    totalRead += bytesRead;
                                    if (totalRead > MaxRequestBodySize)
                                    {
                                        RejectFallbackClient(context, 413, "Payload Too Large");
                                        body = null;
                                        break;
                                    }
                                    ms.Write(buffer, 0, bytesRead);
                                }

                                if (body == null && totalRead > MaxRequestBodySize)
                                    continue;

                                body = ms.ToArray();
                            }
                            finally
                            {
                                BufferPool.ReturnBuffer(buffer);
                            }
                        }
                    }

                    // Build a TcpHttpHandler.HttpRequest from HttpListenerContext for unified processing
                    var httpReq = new TcpHttpHandler.HttpRequest
                    {
                        Method = context.Request.HttpMethod,
                        Path = context.Request.Url.AbsolutePath,
                        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                        Body = body
                    };

                    // Copy relevant headers
                    foreach (string key in context.Request.Headers.AllKeys)
                    {
                        if (key != null)
                            httpReq.Headers[key] = context.Request.Headers[key];
                    }

                    Interlocked.Increment(ref McpEditorServer._inboundCount);
                    McpEditorServer._inboundQueue.Enqueue(new McpEditorServer.McpRequest
                    {
                        ClientId = -1,
                        HttpRequest = httpReq,
                        Context = context,
                        EnqueuedAt = System.Diagnostics.Stopwatch.GetTimestamp()
                    });
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[MCP] Fallback accept error: {ex.Message}");
                    try { context.Response.Abort(); }
                    catch { /* best effort */ }
                }
            }
        }

        internal static void RejectFallbackClient(HttpListenerContext context, int statusCode, string statusDescription)
        {
            try
            {
                context.Response.StatusCode = statusCode;
                context.Response.StatusDescription = statusDescription;
                context.Response.Close();
            }
            catch { /* client may have disconnected */ }
        }

        // --- Unified Request Processing (runs on main thread) ---

        internal static void ProcessRequest(McpEditorServer.McpRequest request)
        {
            var httpReq = request.HttpRequest;
            if (!ValidateHostAndOrigin(request)) return;

            ProcessValidatedRequest(request, httpReq);
        }

        private static bool ValidateHostAndOrigin(McpEditorServer.McpRequest request)
        {
            var httpReq = request.HttpRequest;

            // These checks protect every endpoint/method even when loopback auth is disabled.
            httpReq.Headers.TryGetValue("Host", out var requestHost);
            httpReq.Headers.TryGetValue("Origin", out var requestOrigin);
            if (!McpAuth.ValidateHost(requestHost, McpEditorServer._port) ||
                !McpAuth.ValidateOrigin(requestOrigin, McpEditorServer._port))
            {
                EditorResponseWriter.EnqueueResponse(request, 403, "Forbidden",
                    EditorResponseWriter.BuildJsonError("Forbidden: invalid Host or Origin"), null);
                return false;
            }
            return true;
        }

        // Background-safe: no Unity objects or tool dispatch.
        internal static void RejectUndispatchedRequest(McpEditorServer.McpRequest request)
        {
            if (!ValidateHostAndOrigin(request)) return;
            var httpReq = request.HttpRequest;
            httpReq.Headers.TryGetValue("Authorization", out var auth);
            if (httpReq.Path == McpEndpoint && httpReq.Method == "POST" &&
                !McpAuth.AuthorizeLocalRequest(McpEditorServer._requireAuthentication, auth, McpEditorServer._authToken))
            {
                EditorResponseWriter.EnqueueResponse(request, 401, "Unauthorized",
                    EditorResponseWriter.BuildJsonError("Unauthorized"), null);
                return;
            }
            var error = McpError.Create("editor_not_ready",
                "Unity's main thread did not dispatch this request within two seconds.",
                "Fully dismiss native menus or dialogs and wait for Unity to finish its current work. This request did not start; retry after the Editor responds.");
            error["dispatch_state"] = "not_started";
            error["main_thread_update_age_ms"] = Math.Round(EditorDispatchReadiness.UpdateAgeMilliseconds);
            EditorResponseWriter.EnqueueResponse(request, 503, "Service Unavailable",
                Encoding.UTF8.GetBytes(error.ToString(Formatting.None)),
                new Dictionary<string, string> { ["Content-Type"] = "application/json", ["Retry-After"] = "1" });
        }

        private static void ProcessValidatedRequest(McpEditorServer.McpRequest request, TcpHttpHandler.HttpRequest httpReq)
        {

            // Health check endpoint — handled before auth
            if (httpReq.Path == HealthEndpoint)
            {
                HandleHealth(request);
                return;
            }

            if (httpReq.Path != McpEndpoint)
            {
                EditorResponseWriter.EnqueueResponse(request, 404, "Not Found", null, null);
                ServerMetrics.RecordRequest(0, true);
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            ServerMetrics.IncrementConnections();
            bool isError = false;
            try
            {
                switch (httpReq.Method)
                {
                    case "OPTIONS":
                        HandleOptions(request);
                        return;
                    case "POST":
                        HandlePost(request);
                        return;
                    default:
                        isError = true;
                        EditorResponseWriter.EnqueueResponse(request, 405, "Method Not Allowed", null,
                            new Dictionary<string, string> { ["Allow"] = "POST, OPTIONS" });
                        return;
                }
            }
            finally
            {
                sw.Stop();
                ServerMetrics.DecrementConnections();
                ServerMetrics.RecordRequest(sw.Elapsed.TotalMilliseconds, isError);
            }
        }

        private static void HandleHealth(McpEditorServer.McpRequest request)
        {
            var httpReq = request.HttpRequest;

            if (httpReq.Method != "GET")
            {
                EditorResponseWriter.EnqueueResponse(request, 405, "Method Not Allowed", null,
                    new Dictionary<string, string> { ["Allow"] = "GET" });
                return;
            }

            var headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" };

            // Check if auth token is provided for detailed health info
            httpReq.Headers.TryGetValue("Authorization", out var authHeader);
            bool authenticated = McpAuth.AuthorizeLocalRequest(McpEditorServer._requireAuthentication, authHeader, McpEditorServer._authToken);

            JObject health;
            if (authenticated)
            {
                float uptime = UnityEngine.Time.realtimeSinceStartup;
                health = new JObject
                {
                    ["status"] = "ok",
                    ["version"] = McpProtocol.ServerVersion,
                    ["uptime_seconds"] = Math.Round(uptime, 1),
                    ["tool_count"] = McpToolRegistry.ToolCount,
                    ["modern_activity_count"] = McpRequestActivity.Snapshot().Count,
                    ["active_request_streams"] = EditorStreamingResponses.Pool.Count
                };
            }
            else
            {
                health = new JObject { ["status"] = "ok" };
            }

            var body = System.Text.Encoding.UTF8.GetBytes(health.ToString(Newtonsoft.Json.Formatting.None));
            EditorResponseWriter.EnqueueResponse(request, 200, "OK", body, headers);
        }

        private static void HandleOptions(McpEditorServer.McpRequest request)
        {
            request.HttpRequest.Headers.TryGetValue("Origin", out var origin);
            var headers = new Dictionary<string, string>
            {
                ["Access-Control-Allow-Methods"] = "POST, OPTIONS",
                ["Access-Control-Allow-Headers"] = McpModernHttp.CorsAllowedHeaders(request.HttpRequest.Headers),
                ["Access-Control-Max-Age"] = "86400"
            };

            if (!string.IsNullOrEmpty(origin) && McpAuth.ValidateOrigin(origin, McpEditorServer._port))
                headers["Access-Control-Allow-Origin"] = origin;

            EditorResponseWriter.EnqueueResponse(request, 200, "OK", null, headers);
        }


        private static void HandlePost(McpEditorServer.McpRequest request)
        {
            var httpReq = request.HttpRequest;
            var headers = new Dictionary<string, string>();
            httpReq.Headers.TryGetValue("Origin", out var origin);

            // 1. Origin validation
            if (!McpAuth.ValidateOrigin(origin, McpEditorServer._port))
            {
                EditorResponseWriter.EnqueueResponse(request, 403, "Forbidden",
                    EditorResponseWriter.BuildJsonError("Forbidden: invalid origin"), null);
                return;
            }

            if (!string.IsNullOrEmpty(origin))
                headers["Access-Control-Allow-Origin"] = origin;

            // 2. Host validation
            httpReq.Headers.TryGetValue("Host", out var host);
            if (!McpAuth.ValidateHost(host, McpEditorServer._port))
            {
                EditorResponseWriter.EnqueueResponse(request, 403, "Forbidden",
                    EditorResponseWriter.BuildJsonError("Forbidden: invalid Host header"), headers);
                return;
            }

            // 3. Authorization
            httpReq.Headers.TryGetValue("Authorization", out var authHeader);
            if (!McpAuth.AuthorizeLocalRequest(McpEditorServer._requireAuthentication, authHeader, McpEditorServer._authToken))
            {
                EditorResponseWriter.EnqueueResponse(request, 401, "Unauthorized",
                    EditorResponseWriter.BuildJsonError("Unauthorized"), headers);
                return;
            }

            httpReq.Headers.TryGetValue("Content-Type", out var modernContentType);
            if (!McpAuth.ValidateContentType(modernContentType))
            {
                EditorResponseWriter.EnqueueJsonRpcResponse(request, 415,
                    McpProtocol.BuildErrorResponse(null, McpProtocol.ErrorInvalidRequest, "Expected application/json"), headers);
                return;
            }
            if (!McpModernHttp.TryPrepare(httpReq.Headers, httpReq.Body, BuildClientTransportInfo(request),
                out var modernRequest, out var context, out var error, out var status))
            {
                EditorResponseWriter.EnqueueJsonRpcResponse(request, status, error, headers);
                return;
            }
            if (!context.IsNotification && context.Method == McpProtocol.MethodToolsCall)
            {
                var parameters = (JObject)modernRequest["params"];
                var name = (string)parameters["name"];
                if (McpToolRegistry.IsDeferredTool(name))
                {
                    DeferredResponseManager.HandleDeferredToolCall(request, modernRequest, name, parameters, headers, context);
                    return;
                }
            }
            if (!context.IsNotification && context.Method == McpSubscriptions.Method)
            {
                var subscriptionError = McpSubscriptions.Validate(modernRequest);
                if (subscriptionError != null)
                    EditorResponseWriter.EnqueueJsonRpcResponse(request, 400, subscriptionError, headers);
                else
                {
                    var stream = EditorStreamingResponses.TryOpen(request, headers, context);
                    if (stream == null)
                        EditorResponseWriter.EnqueueJsonRpcResponse(request, 503, McpProtocol.BuildErrorResponse(
                            context.RequestId, -32000, "Stream capacity reached", new JObject { ["dispatch_state"] = "not_started" }), headers);
                    else stream.Send(McpSubscriptions.Acknowledgment(context));
                }
                return;
            }
            var modernResponse = McpProtocol.ProcessRequestWithContext(modernRequest, context);
            if (context.IsNotification)
                EditorResponseWriter.EnqueueResponse(request, 202, "Accepted", null, headers);
            else
                EditorResponseWriter.EnqueueJsonRpcResponse(request, McpProtocol.ModernHttpStatus(modernResponse), modernResponse, headers);
            return;
        }

        private static McpClientTransportInfo BuildClientTransportInfo(McpEditorServer.McpRequest request)
        {
            string userAgent = null;
            request.HttpRequest.Headers?.TryGetValue("User-Agent", out userAgent);
            var remoteAddress = request.Context?.Request?.RemoteEndPoint?.Address?.ToString();
            return new McpClientTransportInfo
            {
                UserAgent = userAgent,
                RemoteAddress = remoteAddress
            };
        }
    }
}
