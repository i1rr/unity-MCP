using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using I1rr.UnityMcp.Editor.Sessions;
using I1rr.UnityMcp.Shared;
using UnityEditor;
using UnityEngine;

namespace I1rr.UnityMcp.Editor
{
    /// <summary>
    /// Main MCP Editor HTTP server — thin façade that wires together the extracted components:
    /// <see cref="ServerLifecycleManager"/>, <see cref="EditorRequestPipeline"/>,
    /// <see cref="EditorResponseWriter"/>, and <see cref="DeferredResponseManager"/>.
    ///
    /// Transport layer: When the native socket keeper (mcpkeeper.dll) is available, the listening
    /// socket survives Unity domain reloads — clients receive HTTP 503 + Retry-After during reload
    /// instead of connection refused. Falls back to HttpListener when the native DLL is unavailable.
    /// </summary>
    [InitializeOnLoad]
    public static class McpEditorServer
    {
        // --- Constants ---
        internal const int MaxRequestsPerTick = 20;

        // --- Shared State (accessed by extracted components) ---
        internal static int _state; // 0 = stopped, 1 = running
        internal static bool _useNativeTransport;
        internal static HttpListener _listener; // fallback only
        internal static Thread _acceptThread;
        internal static Thread _responseWriterThread;
        internal static ConcurrentQueue<McpRequest> _inboundQueue;
        internal static BlockingCollection<CompletedRequest> _outboundQueue;
        internal static int _inboundCount; // Interlocked counter for queue cap enforcement
        internal static readonly object _acceptGate = new object();
        internal static int _readingClientId = -1;
        internal static int _port;
        internal static string _authToken;
        internal static bool _requireAuthentication;
        internal static bool _lastWrittenRequireAuthentication;
        internal static string _libraryPath;
        internal static string _projectRoot;
        internal static int _lastWrittenPort; // track port changes for .vscode/mcp.json
        internal static string _lastWrittenToken; // track token changes for .vscode/mcp.json

        // --- Data Structures ---
        internal struct McpRequest
        {
            // Native transport: ClientId >= 0, HttpRequest populated, Context null
            // Fallback transport: ClientId = -1, HttpRequest default, Context populated
            public int ClientId;
            public TcpHttpHandler.HttpRequest HttpRequest;
            public HttpListenerContext Context;
            public long EnqueuedAt; // Stopwatch ticks; expires only before dispatch
        }

        internal struct CompletedRequest
        {
            public int ClientId;            // native transport (-1 for fallback)
            public HttpListenerContext Context; // fallback transport (null for native)
            public byte[] ResponseBytes;    // null for 202 notifications
            public int StatusCode;
            public string StatusText;
            public Dictionary<string, string> Headers;
        }

        // --- Static Constructor (Lifecycle Entry Point) ---
        static McpEditorServer()
        {
            McpModuleManager.EnsureInstalled();
            AssemblyReloadEvents.beforeAssemblyReload += ServerLifecycleManager.OnDomainReload;
            EditorApplication.quitting += ServerLifecycleManager.OnEditorQuit;
            EditorApplication.update += StartWhenReady;

            // Wire up tool registry on after-reload for cache invalidation
            AssemblyReloadEvents.afterAssemblyReload += McpToolRegistry.InvalidateCache;
        }

        // --- Public API ---

        private static void StartWhenReady()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            EditorApplication.update -= StartWhenReady;
            if (!SessionState.GetBool("MCP.ManuallyStopped", false)) Start();
        }

        /// <summary>
        /// The port the server is currently listening on, or 0 if not running.
        /// </summary>
        public static int Port => _state == 1 ? _port : 0;

        /// <summary>
        /// Whether the server is currently running.
        /// </summary>
        public static bool IsRunning => _state == 1;

        /// <summary>
        /// Starts the MCP server. Safe to call multiple times (no-op if already running).
        /// </summary>
        public static void Start() => ServerLifecycleManager.Start();

        /// <summary>
        /// Full shutdown — stops everything including the native socket.
        /// Use for manual stop or Editor quit. For domain reloads, use OnDomainReload().
        /// </summary>
        public static void Shutdown() => ServerLifecycleManager.Shutdown();

        // --- Main Thread Drain (EditorApplication.update) ---

        /// <summary>
        /// Main-thread tick: processes deferred responses and drains up to
        /// <see cref="MaxRequestsPerTick"/> queued requests through the request pipeline.
        /// Called from EditorApplication.update.
        /// </summary>
        internal static void DrainInboundQueue()
        {
            if (_state != 1) return;
            EditorDispatchReadiness.RecordUpdate();

            EditorStreamingResponses.Pool.Tick();

            DeferredResponseManager.TickDeferredResponses();
            SessionManager.Instance?.Update();

            int processed = 0;
            while (processed < MaxRequestsPerTick && EditorDispatchReadiness.TryTake(out var request))
            {
                processed++;

                try
                {
                    if (EditorDispatchReadiness.HasExpired(request))
                        EditorRequestPipeline.RejectUndispatchedRequest(request);
                    else
                        EditorRequestPipeline.ProcessRequest(request);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[MCP] Request processing error: {ex}");
                    EditorResponseWriter.EnqueueErrorResponse(request, 500, "Internal Server Error");
                }
            }
        }
    }
}
