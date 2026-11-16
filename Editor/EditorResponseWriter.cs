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
    /// Manages the outbound response queue, response writer thread, and response serialization
    /// for both native and managed (HttpListener) transports.
    /// </summary>
    internal static class EditorResponseWriter
    {
        // --- Response Writer Thread ---

        internal static void StartResponseWriterThread()
        {
            McpEditorServer._responseWriterThread = new Thread(ResponseWriterLoop)
            {
                Name = "MCP-ResponseWriter",
                IsBackground = true
            };
            McpEditorServer._responseWriterThread.Start();
        }

        private static void ResponseWriterLoop()
        {
            try
            {
                foreach (var completed in McpEditorServer._outboundQueue.GetConsumingEnumerable())
                    WriteResponse(completed);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        private static void WriteResponse(McpEditorServer.CompletedRequest completed)
        {
            if (completed.ClientId >= 0)
                WriteNativeResponse(completed);
            else if (completed.Context != null)
                WriteFallbackResponse(completed);
        }

        private static void WriteNativeResponse(McpEditorServer.CompletedRequest completed)
        {
            try
            {
                if (!NativeSocketKeeper.IsClientConnected(completed.ClientId))
                {
                    Debug.LogWarning($"[MCP] Client {completed.ClientId} disconnected before response could be sent.");
                    return;
                }

                var stream = new NativeSocketStream(completed.ClientId);
                TcpHttpHandler.WriteResponse(stream, completed.StatusCode,
                    completed.StatusText ?? "OK", completed.Headers, completed.ResponseBytes);
            }
            catch (System.IO.IOException)
            {
                // Client disconnected mid-write — expected when agents time out
                Debug.LogWarning($"[MCP] Client {completed.ClientId} disconnected during response write.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MCP] Native response write error: {ex.Message}");
            }
            finally
            {
                try { NativeSocketKeeper.CloseClient(completed.ClientId); }
                catch { /* best effort */ }
            }
        }

        private static void WriteFallbackResponse(McpEditorServer.CompletedRequest completed)
        {
            try
            {
                var response = completed.Context.Response;
                response.StatusCode = completed.StatusCode;

                if (completed.Headers != null)
                {
                    foreach (var kvp in completed.Headers)
                    {
                        if (string.Equals(kvp.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                            response.ContentType = kvp.Value;
                        else
                            response.Headers[kvp.Key] = kvp.Value;
                    }
                }

                if (completed.ResponseBytes != null && completed.ResponseBytes.Length > 0)
                {
                    response.ContentLength64 = completed.ResponseBytes.Length;
                    response.OutputStream.Write(completed.ResponseBytes, 0, completed.ResponseBytes.Length);
                }

                response.Close();
            }
            catch (ObjectDisposedException) { }
            catch (HttpListenerException) { }
            catch (Exception ex)
            {
                Debug.LogError($"[MCP] Fallback response write error: {ex.Message}");
                try { completed.Context.Response.Abort(); }
                catch { /* best effort */ }
            }
        }

        // --- Enqueue Helpers ---

        internal static void EnqueueJsonRpcResponse(McpEditorServer.McpRequest request, int statusCode, JObject response, Dictionary<string, string> headers)
        {
            byte[] responseBytes = null;
            if (response != null)
            {
                responseBytes = Encoding.UTF8.GetBytes(response.ToString(Formatting.None));
                if (headers == null) headers = new Dictionary<string, string>();
                headers["Content-Type"] = "application/json";
            }

            EnqueueResponse(request, statusCode, "OK", responseBytes, headers);
        }

        internal static void EnqueueResponse(McpEditorServer.McpRequest request, int statusCode, string statusText, byte[] responseBytes, Dictionary<string, string> headers)
        {
            var completed = new McpEditorServer.CompletedRequest
            {
                ClientId = request.ClientId,
                Context = request.Context,
                ResponseBytes = responseBytes,
                StatusCode = statusCode,
                StatusText = statusText ?? "OK",
                Headers = headers
            };

            try
            {
                McpEditorServer._outboundQueue.Add(completed);
            }
            catch (InvalidOperationException)
            {
                // Queue completed adding (shutdown in progress)
                if (request.ClientId >= 0)
                {
                    try { NativeSocketKeeper.CloseClient(request.ClientId); }
                    catch { /* best effort */ }
                }
                else if (request.Context != null)
                {
                    try { request.Context.Response.Abort(); }
                    catch { /* best effort */ }
                }
            }
        }

        internal static void EnqueueErrorResponse(McpEditorServer.McpRequest request, int statusCode, string message)
        {
            var body = BuildJsonError(message);
            var headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" };
            EnqueueResponse(request, statusCode, "Error", body, headers);
        }

        internal static byte[] BuildJsonError(string message,
            string errorCode = McpErrorCodes.InternalError, string suggestion = null)
        {
            var obj = McpError.Create(errorCode, message, suggestion);
            return Encoding.UTF8.GetBytes(obj.ToString(Formatting.None));
        }

        // --- Staged Response Helpers ---

        /// <summary>
        /// Builds a complete HTTP response (headers + body) for a deferred tool that is being
        /// interrupted by domain reload. Returns raw bytes suitable for staging in native layer.
        /// </summary>
        internal static byte[] BuildReloadErrorHttpResponse(JToken requestId, Dictionary<string, string> headers)
        {
            var errResp = BuildInterruptedReloadResponse(requestId);
            var body = Encoding.UTF8.GetBytes(errResp.ToString(Formatting.None));

            var sb = BufferPool.RentStringBuilder(256);
            sb.Append("HTTP/1.1 200 OK\r\n");
            sb.Append("Connection: close\r\n");
            sb.Append("Content-Type: application/json\r\n");
            sb.Append("Retry-After: 3\r\n");
            sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");

            if (headers != null)
            {
                foreach (var kvp in headers)
                {
                    if (string.Equals(kvp.Key, "Content-Type", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(kvp.Key, "Content-Length", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(kvp.Key, "Connection", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(kvp.Key, "Retry-After", StringComparison.OrdinalIgnoreCase))
                        continue;
                    sb.Append(kvp.Key).Append(": ").Append(kvp.Value).Append("\r\n");
                }
            }

            sb.Append("\r\n");

            var headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
            BufferPool.ReturnStringBuilder(sb);
            var result = new byte[headerBytes.Length + body.Length];
            Buffer.BlockCopy(headerBytes, 0, result, 0, headerBytes.Length);
            Buffer.BlockCopy(body, 0, result, headerBytes.Length, body.Length);
            return result;
        }

        /// <summary>
        /// Builds a complete HTTP 503 response for inbound queue items interrupted by reload.
        /// </summary>
        internal static byte[] BuildReload503HttpResponse()
        {
            var body = Encoding.UTF8.GetBytes(new JObject
            {
                ["error_code"] = "server_reloading",
                ["error_message"] = "Unity is reloading. Retry after the delay.",
                ["dispatch_state"] = "not_started"
            }.ToString(Formatting.None));
            using (var stream = new MemoryStream())
            {
                TcpHttpHandler.WriteResponse(stream, 503, "Service Unavailable",
                    new Dictionary<string, string> { ["Retry-After"] = "3", ["Content-Type"] = "application/json" }, body);
                return stream.ToArray();
            }
        }

        internal static JObject BuildInterruptedReloadResponse(JToken requestId) => McpProtocol.BuildErrorResponse(
            requestId, -32000, "Unity reloaded while this operation was pending. Inspect its state before retrying; it may have already changed the project.",
            new JObject { ["error_code"] = "reload_interrupted", ["dispatch_state"] = "started_outcome_unknown", ["retry_after_seconds"] = 3 });
    }
}
