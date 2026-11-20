using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using I1rr.UnityMcp.Editor.Sessions;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace I1rr.UnityMcp.Editor
{
    /// <summary>
    /// Manages server lifecycle — start/stop, port selection, domain reload hooks, native/fallback transport initialization, and resource cleanup.
    /// </summary>
    internal static class ServerLifecycleManager
    {
        // --- Constants ---
        private const int PortRangeStart = 8100;
        private const int PortRangeSize = 900;
        private const int MaxPortProbes = 11; // base + 10 retries
        private const int ShutdownJoinTimeoutMs = 6500;
        private const string PortFileName = "MCPServer.port";

        // --- Public Lifecycle Methods ---

        /// <summary>
        /// Starts the MCP server. Safe to call multiple times (no-op if already running).
        /// </summary>
        internal static void Start()
        {
            SessionState.SetBool("MCP.ManuallyStopped", false);
            if (Interlocked.CompareExchange(ref McpEditorServer._state, 1, 0) != 0)
                return; // Already running

            try
            {
                InitializePaths();
                WireUpProtocol();

                McpEditorServer._useNativeTransport = NativeSocketKeeper.IsAvailable;
                McpEditorServer._authToken = McpAuth.LoadOrCreateToken(McpEditorServer._libraryPath);
                McpEditorServer._requireAuthentication = McpPreferencesProvider.RequireAuthentication;

                if (McpEditorServer._useNativeTransport)
                    StartNativeTransport();
                else
                    StartFallbackTransport();

                McpEditorServer._inboundQueue = new ConcurrentQueue<McpEditorServer.McpRequest>();
                McpEditorServer._outboundQueue = new BlockingCollection<McpEditorServer.CompletedRequest>();
                McpEditorServer._inboundCount = 0;

                EditorRequestPipeline.StartAcceptThread(McpEditorServer._useNativeTransport);
                EditorResponseWriter.StartResponseWriterThread();

                EditorApplication.update += McpEditorServer.DrainInboundQueue;
                EditorDispatchReadiness.Start();

                SessionManager.Initialize();

                DiagnosticsTools.RecordStartTime();

                // Initialize structured logging and metrics
                var logFilePath = Path.Combine(McpEditorServer._libraryPath, "MCPServer.log");
                StructuredLogger.Initialize(logFilePath);
                var metricsExportPath = Path.Combine(McpEditorServer._libraryPath, "MCPMetrics.json");
                ServerMetrics.Initialize(metricsExportPath);
                ServerMetrics.GetDeferredPendingCount = () => DeferredResponseManager._deferredResponses.Count;

                WritePortFile(McpEditorServer._port);
                WriteMcpJsonIfNeeded(McpEditorServer._port);
                // Do not accept managed work until queues and configuration are ready.
                if (McpEditorServer._useNativeTransport) NativeSocketKeeper.Attach();

                var transport = McpEditorServer._useNativeTransport ? "native (domain-reload resilient)" : "HttpListener (fallback)";
                Debug.Log($"[MCP] Server started on http://127.0.0.1:{McpEditorServer._port}/mcp [{transport}]");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MCP] Failed to start server: {ex}");
                McpEditorServer._state = 0;
                StopManagedLayer();
                if (McpEditorServer._useNativeTransport && NativeSocketKeeper.IsRunning) NativeSocketKeeper.Stop();
                CleanupResources();
            }
        }

        /// <summary>
        /// Full shutdown — stops everything including the native socket.
        /// Use for manual stop or Editor quit. For domain reloads, use OnDomainReload().
        /// </summary>
        internal static void Shutdown()
        {
            SessionState.SetBool("MCP.ManuallyStopped", true);
            if (Interlocked.CompareExchange(ref McpEditorServer._state, 0, 1) != 1)
                return;

            StopManagedLayer();

            // Full shutdown: also stop the native socket keeper
            if (McpEditorServer._useNativeTransport && NativeSocketKeeper.IsAvailable)
            {
                try { NativeSocketKeeper.Stop(); }
                catch (Exception ex) { Debug.LogWarning($"[MCP] Native stop error: {ex.Message}"); }
            }

            CleanupResources();

            StructuredLogger.Shutdown();

            McpEditorServer._lastWrittenPort = 0;
            McpRequestActivity.Clear();
            Debug.Log("[MCP] Server stopped.");
        }

        // --- Lifecycle Callbacks ---

        /// <summary>
        /// Called before domain reload. Detaches from native socket (keeps it alive for 503 responses)
        /// or fully shuts down if using fallback transport.
        /// </summary>
        internal static void OnDomainReload()
        {
            if (Interlocked.CompareExchange(ref McpEditorServer._state, 0, 1) != 1)
                return;

            StopManagedLayer();

            if (McpEditorServer._useNativeTransport && NativeSocketKeeper.IsAvailable)
            {
                // Native owns the listener and replies 503 while managed code is unavailable.
                try { NativeSocketKeeper.FlushStaged(); }
                catch (Exception ex) { Debug.LogWarning($"[MCP] Flush staged error: {ex.Message}"); }
                Debug.Log("[MCP] Domain reload — native listener returning temporary-unavailable responses.");
            }

            CleanupResources();

            McpEditorServer._lastWrittenPort = 0;
        }

        /// <summary>
        /// Called when the Editor is quitting. Full teardown including native socket.
        /// </summary>
        internal static void OnEditorQuit()
        {
            Shutdown();
            SessionState.EraseBool("MCP.ManuallyStopped");
        }

        // --- Stop Managed Layer ---

        /// <summary>
        /// Stops all managed threads, cleans up deferred responses, and signals queue completion.
        /// Does NOT touch the native socket keeper or reset protocol state.
        /// </summary>
        private static void StopManagedLayer()
        {
            EditorDispatchReadiness.Stop();
            EditorApplication.update -= McpEditorServer.DrainInboundQueue;

            // Open SSE replies already have headers; finish them on their own writers before detaching.
            EditorStreamingResponses.Pool.Shutdown("Unity interrupted this request; inspect its state before retrying");

            if (McpEditorServer._useNativeTransport && NativeSocketKeeper.IsRunning)
            {
                NativeSocketKeeper.Detach();
                lock (McpEditorServer._acceptGate)
                    if (McpEditorServer._readingClientId >= 0)
                        NativeSocketKeeper.CloseClient(McpEditorServer._readingClientId);
            }
            else
            {
                try { McpEditorServer._listener?.Stop(); } catch (ObjectDisposedException) { }
            }
            // Join the producer BEFORE draining; it must never enqueue into a disposed/new domain queue.
            if (McpEditorServer._acceptThread != null && McpEditorServer._acceptThread.IsAlive)
                McpEditorServer._acceptThread.Join(ShutdownJoinTimeoutMs);

            SessionManager.Shutdown();

            // Stage error responses for deferred clients
            DeferredResponseManager.StageErrorsForReload();

            // Drain inbound queue — requests accepted by the accept thread but not yet
            // processed by DrainInboundQueue. Stage 503 responses for native clients.
            if (McpEditorServer._inboundQueue != null)
            {
                while (McpEditorServer._inboundQueue.TryDequeue(out var stale))
                {
                    Interlocked.Decrement(ref McpEditorServer._inboundCount);
                    try
                    {
                        if (stale.ClientId >= 0)
                        {
                            var httpResponse = EditorResponseWriter.BuildReload503HttpResponse();
                            NativeSocketKeeper.StageResponse(stale.ClientId, httpResponse);
                        }
                        else if (stale.Context != null)
                        {
                            stale.Context.Response.StatusCode = 503;
                            stale.Context.Response.Headers["Retry-After"] = "3";
                            stale.Context.Response.Close();
                        }
                    }
                    catch
                    {
                        if (stale.ClientId >= 0)
                        {
                            try { NativeSocketKeeper.CloseClient(stale.ClientId); }
                            catch { /* ignore */ }
                        }
                        else if (stale.Context != null)
                        {
                            try { stale.Context.Response.Abort(); }
                            catch { /* ignore */ }
                        }
                    }
                }
            }

            // Signal outbound queue completion
            // Preserve completed replies rather than making one writer drain many sockets on reload.
            if (McpEditorServer._useNativeTransport && McpEditorServer._outboundQueue != null)
                while (McpEditorServer._outboundQueue.TryTake(out var completed))
                {
                    using (var stream = new MemoryStream())
                    {
                        TcpHttpHandler.WriteResponse(stream, completed.StatusCode, completed.StatusText,
                            completed.Headers, completed.ResponseBytes);
                        NativeSocketKeeper.StageResponse(completed.ClientId, stream.ToArray());
                    }
                }
            try { McpEditorServer._outboundQueue?.CompleteAdding(); }
            catch (ObjectDisposedException) { }

            // Stop fallback listener (unblocks accept thread in fallback mode)
            if (!McpEditorServer._useNativeTransport)
            {
                try { McpEditorServer._listener?.Stop(); }
                catch (ObjectDisposedException) { }
            }

            // Join accept thread
            if (McpEditorServer._acceptThread != null && McpEditorServer._acceptThread.IsAlive)
                McpEditorServer._acceptThread.Join(ShutdownJoinTimeoutMs);

            // Join response-writer thread
            if (McpEditorServer._responseWriterThread != null && McpEditorServer._responseWriterThread.IsAlive)
                McpEditorServer._responseWriterThread.Join(ShutdownJoinTimeoutMs);
        }

        // --- Transport Initialization ---

        private static void StartNativeTransport()
        {
            // A UPM path change may load a second physical DLL while the previous one
            // still owns its detached listener. Retire it before selecting/binding a port.
            int.TryParse(File.Exists(Path.Combine(McpEditorServer._libraryPath, PortFileName))
                ? File.ReadAllText(Path.Combine(McpEditorServer._libraryPath, PortFileName)) : null,
                out int previousPort);
            int migratedPort = NativeSocketKeeper.PrepareForStart(previousPort);
            if (NativeSocketKeeper.IsRunning)
            {
                // Domain reload recovery — native socket is already listening
                McpEditorServer._port = NativeSocketKeeper.GetPort();
                Debug.Log($"[MCP] Reattached to native socket on port {McpEditorServer._port}.");
            }
            else
            {
                // Fresh start — select port and start native listener
                McpEditorServer._port = migratedPort > 0 && IsPortAvailable(migratedPort)
                    ? migratedPort : SelectPort();
                if (!NativeSocketKeeper.Start(McpEditorServer._port))
                {
                    // Native start failed — fall back to HttpListener
                    Debug.LogWarning("[MCP] Native socket keeper failed to start — falling back to HttpListener.");
                    McpEditorServer._useNativeTransport = false;
                    StartFallbackTransport();
                    return;
                }
            }
        }

        private static void StartFallbackTransport()
        {
            McpEditorServer._port = SelectPort();
            McpEditorServer._listener = new HttpListener();
            McpEditorServer._listener.Prefixes.Add($"http://127.0.0.1:{McpEditorServer._port}/");
            McpEditorServer._listener.Start();
        }

        // --- Initialization Helpers ---

        private static void InitializePaths()
        {
            var dataPath = Application.dataPath;
            McpEditorServer._projectRoot = Directory.GetParent(dataPath).FullName;
            McpEditorServer._libraryPath = Path.Combine(McpEditorServer._projectRoot, "Library");
            McpPathValidator.ProjectRoot = McpEditorServer._projectRoot;
        }

        private static void WireUpProtocol()
        {
            McpToolRegistry.IsInPlayMode = () => EditorApplication.isPlaying;

            McpProtocol.OnToolsList = (request, context) =>
            {
                var paramsObj = request["params"] as JObject;
                var tools = ToolCatalog.Advertise(paramsObj);
                return new JObject { ["tools"] = tools };
            };

            McpProtocol.OnToolsCall = (request, context) =>
            {
                var paramsObj = request["params"] as JObject;
                var toolName = paramsObj?["name"]?.Value<string>();
                var arguments = paramsObj?["arguments"] as JObject;
                return McpToolRegistry.Invoke(toolName, arguments);
            };
        }

        // --- Port Selection ---

        private static int SelectPort()
        {
            var dataPath = Application.dataPath;
            var hash = Fnv1aHash(dataPath);
            var basePort = (int)((hash & 0x7FFFFFFF) % PortRangeSize) + PortRangeStart;

            for (int i = 0; i < MaxPortProbes; i++)
            {
                var candidatePort = basePort + i;
                if (candidatePort >= PortRangeStart + PortRangeSize)
                    candidatePort = PortRangeStart + (candidatePort - PortRangeStart - PortRangeSize);

                if (IsPortAvailable(candidatePort))
                    return candidatePort;
            }

            Debug.LogWarning($"[MCP] All port probes failed (base={basePort}). Attempting base port.");
            return basePort;
        }

        private static uint Fnv1aHash(string input)
        {
            const uint fnvOffsetBasis = 2166136261u;
            const uint fnvPrime = 16777619u;

            uint hash = fnvOffsetBasis;
            for (int i = 0; i < input.Length; i++)
            {
                hash ^= input[i];
                hash *= fnvPrime;
            }
            return hash;
        }

        private static bool IsPortAvailable(int port)
        {
            try
            {
                var tcpListener = new TcpListener(IPAddress.Loopback, port);
                tcpListener.Start();
                tcpListener.Stop();
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        // --- Port File Management ---

        private static void WritePortFile(int port)
        {
            try
            {
                var portFilePath = Path.Combine(McpEditorServer._libraryPath, PortFileName);
                File.WriteAllText(portFilePath, port.ToString());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MCP] Failed to write port file: {ex.Message}");
            }
        }

        // --- .vscode/mcp.json Writer ---

        internal static JObject MergeClientConfiguration(string existingJson, int port, bool requireAuthentication)
        {
            // Parse before writing: malformed user configuration must never be replaced.
            var config = string.IsNullOrWhiteSpace(existingJson) ? new JObject() : JObject.Parse(existingJson);
            var servers = config["servers"] as JObject;
            if (config["servers"] != null && servers == null)
                throw new FormatException("MCP configuration 'servers' must be an object.");
            servers ??= new JObject();
            var server = servers["i1rr-unity-mcp"] as JObject ?? new JObject();
            server["type"] = "http";
            server["url"] = $"http://127.0.0.1:{port}/mcp";
            var headers = server["headers"] as JObject ?? new JObject();
            // Remove every casing of an old Authorization header before applying the mode.
            foreach (var property in new System.Collections.Generic.List<JProperty>(headers.Properties()))
                if (string.Equals(property.Name, "Authorization", StringComparison.OrdinalIgnoreCase)) property.Remove();
            if (requireAuthentication)
            {
                if (config["inputs"] != null && config["inputs"] is not JArray)
                    throw new FormatException("MCP configuration 'inputs' must be an array.");
                var inputs = config["inputs"] as JArray ?? new JArray();
                const string prefix = "i1rr-unity-mcp-token";
                for (int index = 0; ; index++)
                {
                    string id = index == 0 ? prefix : prefix + "-" + index;
                    var prompt = new JObject { ["type"] = "promptString", ["id"] = id, ["password"] = true,
                        ["description"] = "Paste this Unity project's MCP token from the Editor dashboard" };
                    var existing = inputs.FirstOrDefault(input => input is JObject && (string)input["id"] == id);
                    if (existing != null && !JToken.DeepEquals(existing, prompt)) continue;
                    if (existing == null) inputs.Add(prompt);
                    headers["Authorization"] = "Bearer ${input:" + id + "}";
                    break;
                }
                config["inputs"] = inputs;
            }
            server["headers"] = headers;
            servers["i1rr-unity-mcp"] = server;
            config["servers"] = servers;
            return config;
        }

        internal static void WriteMcpJsonIfNeeded(int port)
        {
            if (port == McpEditorServer._lastWrittenPort &&
                McpEditorServer._lastWrittenRequireAuthentication == McpEditorServer._requireAuthentication &&
                string.Equals(McpEditorServer._lastWrittenToken, McpEditorServer._authToken, StringComparison.Ordinal))
                return;

            try
            {
                var vscodeDir = Path.Combine(McpEditorServer._projectRoot, ".vscode");
                var mcpJsonPath = Path.Combine(vscodeDir, "mcp.json");

                Directory.CreateDirectory(vscodeDir);

                var mcpConfig = MergeClientConfiguration(
                    File.Exists(mcpJsonPath) ? File.ReadAllText(mcpJsonPath) : null,
                    port, McpEditorServer._requireAuthentication);

                File.WriteAllText(mcpJsonPath, mcpConfig.ToString(Formatting.Indented));
                McpAuth.RestrictFilePermissions(mcpJsonPath);
                McpEditorServer._lastWrittenPort = port;
                McpEditorServer._lastWrittenToken = McpEditorServer._authToken;
                McpEditorServer._lastWrittenRequireAuthentication = McpEditorServer._requireAuthentication;

                ManageP4Ignore();

                Debug.Log("[MCP] .vscode/mcp.json updated.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MCP] Failed to write .vscode/mcp.json: {ex.Message}");
            }
        }

        private static void ManageP4Ignore()
        {
            try
            {
                var p4IgnorePath = Path.Combine(McpEditorServer._projectRoot, ".p4ignore");
                const string entry = ".vscode/mcp.json";

                if (File.Exists(p4IgnorePath))
                {
                    var content = File.ReadAllText(p4IgnorePath);
                    if (!content.Contains(entry))
                    {
                        var suffix = content.Length > 0 && !content.EndsWith("\n") ? "\n" : "";
                        File.AppendAllText(p4IgnorePath, suffix + entry + "\n");
                    }
                }
                else
                {
                    File.WriteAllText(p4IgnorePath, entry + "\n");
                }

                Debug.Log("[MCP] .vscode/mcp.json is machine-specific — excluded from P4 via .p4ignore");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MCP] Failed to manage .p4ignore: {ex.Message}");
            }
        }

        // --- Cleanup ---

        internal static void CleanupResources()
        {
            if (!McpEditorServer._useNativeTransport)
            {
                try { McpEditorServer._listener?.Close(); }
                catch { /* best effort */ }
                McpEditorServer._listener = null;
            }

            McpEditorServer._acceptThread = null;
            McpEditorServer._responseWriterThread = null;
            McpEditorServer._inboundQueue = null;

            try { McpEditorServer._outboundQueue?.Dispose(); }
            catch { /* best effort */ }

            McpEditorServer._outboundQueue = null;
        }
    }
}
