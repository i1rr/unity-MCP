using System.Collections.Generic;
using I1rr.UnityMcp.Shared;

namespace I1rr.UnityMcp.Editor
{
    internal static class EditorStreamingResponses
    {
        internal static readonly McpStreamPool Pool = new McpStreamPool();

        internal static McpSseStream TryOpen(McpEditorServer.McpRequest request,
            Dictionary<string, string> headers, McpRequestContext context)
        {
            if (request.ClientId >= 0)
            {
                var output = new NativeSocketStream(request.ClientId);
                return Pool.TryOpen(output, () => McpSseStream.WriteHttpHeaders(output, headers),
                    () => NativeSocketKeeper.CloseClient(request.ClientId),
                    () => NativeSocketKeeper.IsClientConnected(request.ClientId), context);
            }
            if (request.Context == null) return null;
            var response = request.Context.Response;
            var stream = response.OutputStream;
            return Pool.TryOpen(stream, () =>
            {
                response.StatusCode = 200;
                response.ContentType = "text/event-stream";
                response.SendChunked = true;
                response.KeepAlive = false;
                response.Headers["Cache-Control"] = "no-store";
                response.Headers["X-Accel-Buffering"] = "no";
                foreach (var header in headers)
                    if (header.Key != "Content-Type" && header.Key != "MCP-Session-Id")
                        response.Headers[header.Key] = header.Value;
            }, () => response.Close(), () => stream.CanWrite, context, () => response.Abort());
        }
    }
}
