using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using I1rr.UnityMcp.Runtime.Sessions;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Scripting;

namespace I1rr.UnityMcp.Runtime
{
    /// <summary>
    /// TcpListener-based MCP server running as a MonoBehaviour in player builds.
    /// 2-thread model: accept thread parses HTTP and enqueues; Update() drains and responds.
    /// </summary>
    [Preserve]
    public sealed class McpRuntimeServer : MonoBehaviour
    {
        // --- Constants ---
        private const int PortRangeStart = 9100;
        private const int PortRangeSize = 900;
        private const int MaxPortProbes = 11;
        private const int MaxRequestBodySize = 1_048_576; // 1 MB
        private const int MaxRequestsPerFrame = 3;
        private const string McpEndpoint = "/mcp";
        private const string PortFileName = "MCPServer.port";
        private const int MaxDeferredTotal = 8;
        private const int MaxDeferredPerConnection = 2;

        // --- Configuration ---
        private string _bindAddress = "127.0.0.1";
        private int _portOverride = -1;

        // --- LAN Mode ---
        private bool _isLanMode;
        private HashSet<string> _allowedHosts;        // "ip:port" entries for ValidateHost
        private HashSet<string> _allowedOrigins;      // "http://ip:port" entries for ValidateOrigin
        private HashSet<IPAddress> _allowedClientIps;  // IP allowlist for incoming connections

        // --- Rate Limiting (per IP) ---
        private const int MaxRequestsPerIpPerMinute = 120;
        private readonly ConcurrentDictionary<string, IpRateLimit> _ipRateLimits =
            new ConcurrentDictionary<string, IpRateLimit>();

        private sealed class IpRateLimit
        {
            public int Count;
            public long WindowStartTicks;
        }

        // --- State ---
        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private int _port;
        private string _authToken;
        private string _persistentDataPath;
        private UdpDiscovery _udpDiscovery;

        // --- Pending Responses ---
        // Stores TcpClient alongside request so we can write response on main thread
        private struct PendingRequest
        {
            public TcpClient Client;
            public TcpHttpHandler.HttpRequest HttpRequest;
        }

        private readonly ConcurrentQueue<PendingRequest> _pendingQueue =
            new ConcurrentQueue<PendingRequest>();
        private int _pendingCount;

        // --- Heartbeat / Keepalive ---
        private const float HeartbeatIntervalSeconds = 15f;
        private float _lastHeartbeatTime;
        private volatile bool _shuttingDown;

        // --- Deferred Response State ---
        private sealed class PendingDeferredResponse
        {
            public TcpClient Client;
            public JToken RequestId;
            public Dictionary<string, string> Headers;
            public DeferredResult Deferred;
            public DateTime Deadline;
            public int ConnectionHash;
            public string SessionId;
        }

        private readonly List<PendingDeferredResponse> _deferredResponses = new List<PendingDeferredResponse>();

        /// <summary>
        /// The port the server is listening on, or 0 if not running.
        /// </summary>
        public int Port => _running ? _port : 0;

        /// <summary>
        /// Whether the server is currently accepting connections.
        /// </summary>
        public bool IsRunning => _running;

        [Preserve]
        public void Initialize(string bindAddress = null, int portOverride = -1)
        {
            if (!string.IsNullOrEmpty(bindAddress))
                _bindAddress = bindAddress;
            if (portOverride > 0)
                _portOverride = portOverride;
        }

        [Preserve]
        private void OnEnable()
        {
            if (_running || Application.isEditor) return;

            _persistentDataPath = Application.persistentDataPath;

            LoadConfig();
            WireUpProtocol();

            _port = SelectPort();
            _authToken = LoadRuntimeToken();

            // Audit platform-specific token security
            McpAuth.AuditPlatformTokenSecurity(_persistentDataPath);

            // Build LAN mode allowlists (needs port)
            BuildLanModeAllowlists();

            _running = true;

            StartListener();
            StartAcceptThread();

            Sessions.SessionManager.Initialize();

            DiagnosticsTools.RecordStartTime();

            // Start UDP discovery if enabled
            var config = McpRuntimeConfig.Current;
            if (config.EnableUdpDiscovery)
            {
                _udpDiscovery = gameObject.AddComponent<UdpDiscovery>();
                _udpDiscovery.StartBroadcasting(_port, config.UdpBroadcastInterval, true);
            }

            WritePortFile();

            if (_isLanMode)
                Debug.LogWarning($"[MCP Runtime] Server started in LAN mode on {_bindAddress}:{_port}/mcp");
            else
                Debug.Log($"[MCP Runtime] Server started on {_bindAddress}:{_port}/mcp");
        }

        [Preserve]
        private void OnDisable()
        {
            Shutdown();
        }

        [Preserve]
        private void OnDestroy()
        {
            Shutdown();
        }

        [Preserve]
        private void OnApplicationQuit()
        {
            GracefulShutdown();
        }

        [Preserve]
        private void Update()
        {
            if (!_running) return;

            // Tick heartbeat
            TickHeartbeat();

            // Tick deferred responses first
            TickDeferredResponses();

            // Tick session manager
            Sessions.SessionManager.Instance?.Update();

            int processed = 0;
            while (processed < MaxRequestsPerFrame && _pendingQueue.TryDequeue(out var pending))
            {
                Interlocked.Decrement(ref _pendingCount);
                processed++;

                try
                {
                    ProcessAndRespond(pending);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[MCP Runtime] Request processing error: {ex}");
                    SendErrorResponse(pending.Client, 500, "Internal Server Error", null);
                }
            }
        }

        // --- Shutdown ---

        private void Shutdown()
        {
            if (!_running) return;
            _running = false;

            // Stop UDP discovery
            if (_udpDiscovery != null)
            {
                _udpDiscovery.StopBroadcasting();
                _udpDiscovery = null;
            }

            Sessions.SessionManager.Shutdown();

            // Clean up deferred responses
            foreach (var pending in _deferredResponses)
            {
                pending.Deferred.InvokeCleanup();
                McpProtocol.CompleteDeferredRequest(pending.SessionId, true);
                try { pending.Client?.Close(); }
                catch { /* ignore */ }
            }
            _deferredResponses.Clear();

            try { _listener?.Stop(); }
            catch (Exception) { /* ignore */ }

            if (_acceptThread != null && _acceptThread.IsAlive)
                _acceptThread.Join(100);

            // Drain and close any remaining clients
            while (_pendingQueue.TryDequeue(out var pending))
            {
                try { pending.Client?.Close(); }
                catch { /* ignore */ }
            }

            McpProtocol.Reset();
            Debug.Log("[MCP Runtime] Server stopped.");
        }

        /// <summary>
        /// Graceful shutdown: signals shutting-down state so in-flight requests
        /// receive 503 with Retry-After, then performs full shutdown.
        /// Called on Application.Quit to give clients a chance to reconnect.
        /// </summary>
        private void GracefulShutdown()
        {
            if (!_running || _shuttingDown) return;
            _shuttingDown = true;

            // Respond 503 to any pending requests still in the queue
            while (_pendingQueue.TryDequeue(out var pending))
            {
                Interlocked.Decrement(ref _pendingCount);
                var headers = new Dictionary<string, string>
                {
                    ["Retry-After"] = "2",
                    ["X-MCP-Server-State"] = "shutting-down"
                };
                SendErrorResponse(pending.Client, 503, "Service Unavailable", headers);
            }

            // Close deferred responses with 503
            foreach (var deferred in _deferredResponses)
            {
                deferred.Deferred.InvokeCleanup();
                McpProtocol.CompleteDeferredRequest(deferred.SessionId, true);
                var headers = deferred.Headers ?? new Dictionary<string, string>();
                headers["X-MCP-Server-State"] = "shutting-down";
                headers["Retry-After"] = "2";
                var errResp = McpProtocol.BuildErrorResponse(deferred.RequestId, -32000,
                    "Server shutting down — application is quitting");
                SendJsonRpcResponse(deferred.Client, errResp, headers);
            }
            _deferredResponses.Clear();

            Debug.Log("[MCP Runtime] Graceful shutdown complete — application quitting.");
            Shutdown();
        }

        /// <summary>
        /// Periodically logs heartbeat to indicate server liveness.
        /// Clients can use the ping tool as a keepalive mechanism.
        /// </summary>
        private void TickHeartbeat()
        {
            float now = Time.realtimeSinceStartup;
            if (now - _lastHeartbeatTime < HeartbeatIntervalSeconds)
                return;

            _lastHeartbeatTime = now;

            // Clean up stale rate limit entries during heartbeat
            var staleKeys = new List<string>();
            var nowTicks = DateTime.UtcNow.Ticks;
            foreach (var kvp in _ipRateLimits)
            {
                if (nowTicks - Interlocked.Read(ref kvp.Value.WindowStartTicks) > TimeSpan.TicksPerMinute * 5)
                    staleKeys.Add(kvp.Key);
            }
            foreach (var key in staleKeys)
                _ipRateLimits.TryRemove(key, out _);
        }

        // --- Configuration ---

        private void LoadConfig()
        {
            var config = McpRuntimeConfig.Current;
            _bindAddress = config.BindAddress ?? "127.0.0.1";
            _portOverride = config.PortOverride;
            _isLanMode = config.IsLanMode;

            if (_isLanMode)
                BuildAllowedClientIps(config.AllowedIps);
        }

        /// <summary>
        /// Loads the auth token from StreamingAssets (written by McpBuildProcessor at build time).
        /// Falls back to generating one in persistentDataPath if not found.
        /// </summary>
        private string LoadRuntimeToken()
        {
            const string runtimeTokenFileName = "mcp_runtime.token";

            try
            {
                var streamingPath = Path.Combine(Application.streamingAssetsPath, runtimeTokenFileName);
                if (File.Exists(streamingPath))
                {
                    var token = File.ReadAllText(streamingPath).Trim();
                    if (!string.IsNullOrEmpty(token) && token.Length >= 20)
                    {
                        Debug.Log("[MCP Runtime] Auth token loaded from StreamingAssets.");
                        return token;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MCP Runtime] Failed to read token from StreamingAssets: {ex.Message}");
            }

            // Fallback: generate/load from persistentDataPath
            Debug.LogWarning("[MCP Runtime] StreamingAssets token not found, generating new token in persistentDataPath.");
            return McpAuth.LoadOrCreateToken(_persistentDataPath);
        }

        /// <summary>
        /// Builds the set of allowed hosts and origins for LAN mode.
        /// Called after port selection since we need the port number.
        /// </summary>
        private void BuildLanModeAllowlists()
        {
            if (!_isLanMode)
                return;

            _allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _allowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Add the server's own IP addresses
            try
            {
                if (_bindAddress == "0.0.0.0" || _bindAddress == "::")
                {
                    // Wildcard bind — accept all local interface IPs
                    var hostName = Dns.GetHostName();
                    var addresses = Dns.GetHostAddresses(hostName);
                    foreach (var addr in addresses)
                    {
                        if (addr.AddressFamily == AddressFamily.InterNetwork)
                        {
                            var ip = addr.ToString();
                            _allowedHosts.Add($"{ip}:{_port}");
                            _allowedOrigins.Add($"http://{ip}:{_port}");
                        }
                    }
                    _allowedHosts.Add($"{hostName}:{_port}");
                    _allowedOrigins.Add($"http://{hostName}:{_port}");
                }
                else
                {
                    // Specific IP bind
                    _allowedHosts.Add($"{_bindAddress}:{_port}");
                    _allowedOrigins.Add($"http://{_bindAddress}:{_port}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MCP Runtime] Failed to enumerate network addresses: {ex.Message}");
                // Fallback: at least add the bind address
                _allowedHosts.Add($"{_bindAddress}:{_port}");
                _allowedOrigins.Add($"http://{_bindAddress}:{_port}");
            }
        }

        private void BuildAllowedClientIps(string allowedIpsConfig)
        {
            if (string.IsNullOrEmpty(allowedIpsConfig))
            {
                _allowedClientIps = null; // null = no restriction
                return;
            }

            _allowedClientIps = new HashSet<IPAddress>();
            var parts = allowedIpsConfig.Split(',');
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (!string.IsNullOrEmpty(trimmed) && IPAddress.TryParse(trimmed, out var ip))
                    _allowedClientIps.Add(ip);
            }

            if (_allowedClientIps.Count == 0)
                _allowedClientIps = null;
        }

        private void WireUpProtocol()
        {
            McpToolRegistry.IsInPlayMode = () => true; // Always in play mode at runtime

            McpProtocol.OnToolsList = (request, sessionId) =>
            {
                var paramsObj = request["params"] as JObject;
                var category = paramsObj?["category"]?.Value<string>();
                var tools = McpToolRegistry.ListTools(category);
                return new JObject { ["tools"] = tools };
            };

            McpProtocol.OnToolsCall = (request, sessionId) =>
            {
                var paramsObj = request["params"] as JObject;
                var toolName = paramsObj?["name"]?.Value<string>();
                var arguments = paramsObj?["arguments"] as JObject;
                return McpToolRegistry.Invoke(toolName, arguments);
            };
        }

        // --- Port Selection ---

        private int SelectPort()
        {
            if (_portOverride > 0)
                return _portOverride;

            var dataPath = Application.dataPath;
            var hash = Fnv1aHash(dataPath);
            var basePort = (int)((hash & 0x7FFFFFFF) % PortRangeSize) + PortRangeStart;

            for (int i = 0; i < MaxPortProbes; i++)
            {
                int candidatePort = basePort + i;
                if (candidatePort >= PortRangeStart + PortRangeSize)
                    candidatePort = PortRangeStart + (candidatePort - PortRangeStart - PortRangeSize);

                if (IsPortAvailable(candidatePort))
                    return candidatePort;
            }

            Debug.LogWarning($"[MCP Runtime] All port probes failed (base={basePort}). Attempting base port.");
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

        private bool IsPortAvailable(int port)
        {
            try
            {
                var addr = IPAddress.Parse(_bindAddress);
                var testListener = new TcpListener(addr, port);
                testListener.Start();
                testListener.Stop();
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        // --- TCP Listener ---

        private void StartListener()
        {
            var address = IPAddress.Parse(_bindAddress);
            _listener = new TcpListener(address, _port);

            // Platform-specific socket options
            var socket = _listener.Server;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            // SO_EXCLUSIVEADDRUSE on Windows prevents port hijacking
            socket.SetSocketOption(SocketOptionLevel.Socket, (SocketOptionName)(-5), true);
#else
            // SO_REUSEADDR on Linux/Mac for quick restart
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
#endif
            _listener.Start();
        }

        private void StartAcceptThread()
        {
            _acceptThread = new Thread(AcceptLoop)
            {
                Name = "MCP-Runtime-Accept",
                IsBackground = true
            };
            _acceptThread.Start();
        }

        // --- Accept Thread ---

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    break; // Listener stopped
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                try
                {
                    // Extract client IP for validation and logging
                    var remoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
                    var clientIp = remoteEndPoint?.Address;
                    var clientIpStr = clientIp?.ToString() ?? "unknown";

                    // IP allowlist check (LAN mode only)
                    if (_isLanMode && _allowedClientIps != null && clientIp != null)
                    {
                        if (!_allowedClientIps.Contains(clientIp) && !IPAddress.IsLoopback(clientIp))
                        {
                            Debug.LogWarning($"[MCP Runtime] Rejected connection from non-allowed IP: {clientIpStr}");
                            try { client.Close(); } catch { }
                            continue;
                        }
                    }

                    // Per-IP rate limiting
                    if (!CheckRateLimit(clientIpStr))
                    {
                        Debug.LogWarning($"[MCP Runtime] Rate limit exceeded for IP: {clientIpStr}");
                        RejectClient(client, 429, "Too Many Requests");
                        continue;
                    }

                    // Connection logging for LAN mode
                    if (_isLanMode)
                        Debug.Log($"[MCP Runtime] Connection from {clientIpStr}");

                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;
                    client.NoDelay = true;
                    client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                    // Check queue cap
                    if (Volatile.Read(ref _pendingCount) >= 200)
                    {
                        RejectClient(client, 503, "Service Unavailable");
                        continue;
                    }

                    var stream = client.GetStream();
                    var httpRequest = TcpHttpHandler.ParseRequest(stream, MaxRequestBodySize);

                    if (httpRequest.Error != null)
                    {
                        RejectClient(client, 400, "Bad Request");
                        continue;
                    }

                    Interlocked.Increment(ref _pendingCount);
                    _pendingQueue.Enqueue(new PendingRequest
                    {
                        Client = client,
                        HttpRequest = httpRequest
                    });
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[MCP Runtime] Accept thread error: {ex.Message}");
                    try { client?.Close(); }
                    catch { /* ignore */ }
                }
            }
        }

        private bool CheckRateLimit(string clientIp)
        {
            var now = DateTime.UtcNow.Ticks;
            var limit = _ipRateLimits.GetOrAdd(clientIp, _ => new IpRateLimit
            {
                Count = 0,
                WindowStartTicks = now
            });

            // Sliding window: reset after 1 minute
            const long ticksPerMinute = TimeSpan.TicksPerMinute;
            if (now - Interlocked.Read(ref limit.WindowStartTicks) > ticksPerMinute)
            {
                Interlocked.Exchange(ref limit.WindowStartTicks, now);
                Interlocked.Exchange(ref limit.Count, 1);
                return true;
            }

            var count = Interlocked.Increment(ref limit.Count);
            return count <= MaxRequestsPerIpPerMinute;
        }

        private static void RejectClient(TcpClient client, int statusCode, string statusText)
        {
            try
            {
                var stream = client.GetStream();
                TcpHttpHandler.WriteResponse(stream, statusCode, statusText, null, null);
            }
            catch { /* ignore */ }
            finally
            {
                try { client.Close(); }
                catch { /* ignore */ }
            }
        }

        // --- Main Thread Processing ---

        private void ProcessAndRespond(PendingRequest pending)
        {
            var client = pending.Client;
            var httpReq = pending.HttpRequest;

            try
            {
                // Path validation
                if (httpReq.Path != McpEndpoint)
                {
                    SendErrorResponse(client, 404, "Not Found", null);
                    return;
                }

                // Method routing
                switch (httpReq.Method)
                {
                    case "OPTIONS":
                        HandleOptions(client, httpReq);
                        return;
                    case "POST":
                        HandlePost(client, httpReq);
                        return;
                    case "DELETE":
                        HandleDelete(client, httpReq);
                        return;
                    default:
                        var headers = new Dictionary<string, string> { ["Allow"] = "POST, DELETE, OPTIONS" };
                        SendErrorResponse(client, 405, "Method Not Allowed", headers);
                        return;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MCP Runtime] ProcessAndRespond error: {ex.Message}");
                SendErrorResponse(client, 500, "Internal Server Error", null);
            }
        }

        private void HandleOptions(TcpClient client, TcpHttpHandler.HttpRequest httpReq)
        {
            httpReq.Headers.TryGetValue("Origin", out var origin);

            var headers = new Dictionary<string, string>
            {
                ["Access-Control-Allow-Methods"] = "POST, DELETE, OPTIONS",
                ["Access-Control-Allow-Headers"] = "Content-Type, Authorization, MCP-Session-Id, MCP-Protocol-Version",
                ["Access-Control-Max-Age"] = "86400"
            };

            bool originValid = _isLanMode
                ? McpAuth.ValidateOrigin(origin, _port, _allowedOrigins)
                : McpAuth.ValidateOrigin(origin, _port);

            if (!string.IsNullOrEmpty(origin) && originValid)
                headers["Access-Control-Allow-Origin"] = origin;

            SendResponse(client, 200, "OK", headers, null);
        }

        private void HandleDelete(TcpClient client, TcpHttpHandler.HttpRequest httpReq)
        {
            var responseHeaders = new Dictionary<string, string>();
            httpReq.Headers.TryGetValue("Origin", out var origin);

            // Origin validation
            bool originValid = _isLanMode
                ? McpAuth.ValidateOrigin(origin, _port, _allowedOrigins)
                : McpAuth.ValidateOrigin(origin, _port);

            if (!originValid)
            {
                SendJsonError(client, 403, "Forbidden: invalid origin", responseHeaders);
                return;
            }
            if (!string.IsNullOrEmpty(origin))
                responseHeaders["Access-Control-Allow-Origin"] = origin;

            // Authorization
            httpReq.Headers.TryGetValue("Authorization", out var authHeader);
            if (!McpAuth.ValidateAuthorizationHeader(authHeader, _authToken))
            {
                SendJsonError(client, 401, "Unauthorized", responseHeaders);
                return;
            }

            // Session header required for DELETE
            httpReq.Headers.TryGetValue("MCP-Session-Id", out var sessionHeader);
            if (string.IsNullOrEmpty(sessionHeader))
            {
                SendJsonError(client, 400, "MCP-Session-Id header required for DELETE", responseHeaders);
                return;
            }

            if (McpProtocol.GetSession(sessionHeader) == null)
            {
                SendJsonError(client, 404, "Session not found", responseHeaders);
                return;
            }

            McpProtocol.TerminateSession(sessionHeader);
            Debug.Log($"[MCP Runtime] Session {sessionHeader} terminated via DELETE.");
            SendResponse(client, 200, "OK", responseHeaders, null);
        }

        private void HandlePost(TcpClient client, TcpHttpHandler.HttpRequest httpReq)
        {
            var responseHeaders = new Dictionary<string, string>();

            // Reject during graceful shutdown
            if (_shuttingDown)
            {
                responseHeaders["Retry-After"] = "2";
                responseHeaders["X-MCP-Server-State"] = "shutting-down";
                SendJsonError(client, 503, "Server is shutting down. Reconnect after restart.", responseHeaders);
                return;
            }

            // Add reconnection guidance headers
            responseHeaders["X-MCP-Keepalive"] = $"interval={HeartbeatIntervalSeconds}";
            responseHeaders["X-MCP-Reconnect-Advice"] = "retry-after=2, max-retries=5, backoff=exponential";

            httpReq.Headers.TryGetValue("Origin", out var origin);

            // 1. Origin validation
            bool originValid = _isLanMode
                ? McpAuth.ValidateOrigin(origin, _port, _allowedOrigins)
                : McpAuth.ValidateOrigin(origin, _port);

            if (!originValid)
            {
                SendJsonError(client, 403, "Forbidden: invalid origin", responseHeaders);
                return;
            }

            if (!string.IsNullOrEmpty(origin))
                responseHeaders["Access-Control-Allow-Origin"] = origin;

            // 2. Host validation
            httpReq.Headers.TryGetValue("Host", out var host);
            bool hostValid = _isLanMode
                ? McpAuth.ValidateHost(host, _port, _allowedHosts)
                : McpAuth.ValidateHost(host, _port);

            if (!hostValid)
            {
                SendJsonError(client, 403, "Forbidden: invalid Host header", responseHeaders);
                return;
            }

            // 3. Authorization
            httpReq.Headers.TryGetValue("Authorization", out var authHeader);
            if (!McpAuth.ValidateAuthorizationHeader(authHeader, _authToken))
            {
                SendJsonError(client, 401, "Unauthorized", responseHeaders);
                return;
            }

            // 4. Session validation
            httpReq.Headers.TryGetValue("MCP-Session-Id", out var sessionHeader);
            var sessionError = McpProtocol.ValidateSessionHeader(sessionHeader, out int sessionHttpStatus, out string matchedSessionId);
            if (sessionError != null)
            {
                SendJsonError(client, sessionHttpStatus, sessionError, responseHeaders);
                return;
            }

            // 5. Protocol version
            httpReq.Headers.TryGetValue("MCP-Protocol-Version", out var protocolVersionHeader);
            var versionError = McpProtocol.ValidateProtocolVersionHeader(protocolVersionHeader);
            if (versionError != null)
            {
                SendJsonError(client, 400, versionError, responseHeaders);
                return;
            }

            // 6. Content-Type
            httpReq.Headers.TryGetValue("Content-Type", out var contentType);
            if (!McpAuth.ValidateContentType(contentType))
            {
                SendJsonError(client, 415, "Unsupported Media Type: expected application/json", responseHeaders);
                return;
            }

            // 7. Parse body
            if (httpReq.Body == null || httpReq.Body.Length == 0)
            {
                SendJsonError(client, 400, "Empty request body", responseHeaders);
                return;
            }

            var bodyStr = Encoding.UTF8.GetString(httpReq.Body);
            var (requests, parseError) = McpProtocol.ParseRequest(bodyStr, matchedSessionId, out var isBatch);

            if (parseError != null)
            {
                responseHeaders["Content-Type"] = "application/json";
                var errorBytes = Encoding.UTF8.GetBytes(parseError.ToString(Formatting.None));
                SendResponse(client, 400, "Bad Request", responseHeaders, errorBytes);
                return;
            }

            httpReq.Headers.TryGetValue("User-Agent", out var userAgent);
            var transportInfo = new McpClientTransportInfo
            {
                UserAgent = userAgent,
                RemoteAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address?.ToString()
            };

            // 8. Process each request — check for deferred tool calls
            // For simplicity, if a single request is a deferred tool call, handle it specially.
            // Batch requests containing deferred tools are not supported (deferred tools must be called individually).
            if (!isBatch)
            {
                var singleReq = requests[0];
                var reqMethod = singleReq["method"]?.Value<string>();
                bool isNotification = McpProtocol.IsNotification(singleReq);

                // Reuse/reclaim the same client family first, then stale sessions; reject only
                // when capacity is genuinely occupied by protected sessions.
                if (!isNotification && reqMethod == McpProtocol.MethodInitialize &&
                    !McpProtocol.TryPrepareInitialize(singleReq, matchedSessionId, out var capacityError,
                        transportInfo))
                {
                    responseHeaders["Retry-After"] = "5";
                    SendJsonError(client, 503, capacityError, responseHeaders,
                        McpErrorCodes.SessionCapacity,
                        "Retry after the Retry-After interval. Reuse MCP-Session-Id or send a stable client instanceId.");
                    return;
                }

                if (!isNotification && reqMethod == McpProtocol.MethodToolsCall)
                {
                    var reqParams = singleReq["params"] as JObject;
                    var toolName = reqParams?["name"]?.Value<string>();
                    if (toolName != null && McpToolRegistry.IsDeferredTool(toolName))
                    {
                        HandleDeferredToolCall(client, singleReq, toolName, reqParams, responseHeaders, matchedSessionId);
                        return;
                    }
                }
            }

            var responses = new List<JObject>();
            string currentSessionId = matchedSessionId;
            foreach (var request in requests)
            {
                var response = McpProtocol.ProcessRequest(request, currentSessionId, out var newSessionId,
                    transportInfo);
                if (newSessionId != null)
                    currentSessionId = newSessionId;
                if (response != null)
                    responses.Add(response);
            }

            // 9. Build HTTP response
            if (responses.Count == 0)
            {
                // All were notifications
                SendResponse(client, 202, "Accepted", responseHeaders, null);
            }
            else
            {
                responseHeaders["Content-Type"] = "application/json";

                // Include session header if we have a session
                if (currentSessionId != null)
                    responseHeaders["MCP-Session-Id"] = currentSessionId;

                string jsonResponse;
                if (!isBatch)
                    jsonResponse = responses[0].ToString(Formatting.None);
                else
                    jsonResponse = new JArray(responses).ToString(Formatting.None);

                var responseBody = Encoding.UTF8.GetBytes(jsonResponse);
                SendResponse(client, 200, "OK", responseHeaders, responseBody);
            }
        }

        // --- Deferred Tool Call Handling ---

        private void HandleDeferredToolCall(TcpClient client, JObject request,
            string toolName, JObject paramsObj, Dictionary<string, string> headers, string sessionId)
        {
            var id = request["id"];

            // Validate JSON-RPC structure
            var validationError = McpProtocol.ValidateJsonRpc(request);
            if (validationError != null)
            {
                SendJsonRpcResponse(client, validationError, headers);
                return;
            }

            // Check pre-init gate
            if (!McpProtocol.IsSessionInitialized(sessionId))
            {
                var errResp = McpProtocol.BuildErrorResponse(id, McpProtocol.ErrorServerNotInitialized, "Server not initialized");
                SendJsonRpcResponse(client, errResp, headers);
                return;
            }

            // Resource limits — total
            if (_deferredResponses.Count >= MaxDeferredTotal)
            {
                var errResp = McpProtocol.BuildErrorResponse(id, -32000, "Too many concurrent deferred requests");
                SendJsonRpcResponse(client, errResp, headers);
                return;
            }

            // Resource limits — per connection
            int connHash = GetConnectionHash(client);
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
                SendJsonRpcResponse(client, errResp, headers);
                return;
            }

            // Include session header
            if (sessionId != null)
                headers["MCP-Session-Id"] = sessionId;

            // Invoke the tool
            McpProtocol.BeginDeferredRequest(sessionId, McpProtocol.MethodToolsCall, toolName);
            var arguments = paramsObj?["arguments"] as JObject;
            object rawResult;
            try
            {
                rawResult = McpToolRegistry.InvokeRaw(toolName, arguments);
            }
            catch
            {
                McpProtocol.CompleteDeferredRequest(sessionId, true);
                throw;
            }

            if (rawResult is DeferredResult deferred)
            {
                // Extend send timeout for deferred connections
                try { client.SendTimeout = (int)(deferred.ClampedTimeout * 1000) + 5000; }
                catch { /* ignore */ }

                deferred.CreatedAtUtc = DateTime.UtcNow;
                deferred.SessionId = sessionId;
                _deferredResponses.Add(new PendingDeferredResponse
                {
                    Client = client,
                    RequestId = id,
                    Headers = headers,
                    Deferred = deferred,
                    Deadline = DateTime.UtcNow.AddSeconds(deferred.ClampedTimeout),
                    ConnectionHash = connHash,
                    SessionId = sessionId
                });
                // Connection stays open — response sent later by TickDeferredResponses
            }
            else
            {
                // Tool returned an immediate result despite being marked deferred
                var toolResponse = rawResult as JObject ?? new JObject { ["result"] = "ok" };
                var wrapped = McpProtocol.WrapToolResult(toolResponse);
                var jsonRpcResp = McpProtocol.BuildResponse(id, wrapped);
                SendJsonRpcResponse(client, jsonRpcResp, headers);
                McpProtocol.CompleteDeferredRequest(sessionId, wrapped["isError"]?.Value<bool>() == true);
            }
        }

        private void TickDeferredResponses()
        {
            if (_deferredResponses.Count == 0) return;

            var now = DateTime.UtcNow;

            for (int i = _deferredResponses.Count - 1; i >= 0; i--)
            {
                var pending = _deferredResponses[i];

                // Check session still valid
                if (pending.SessionId != null && McpProtocol.GetSession(pending.SessionId) == null)
                {
                    pending.Deferred.InvokeCleanup();
                    var errResp = McpProtocol.BuildErrorResponse(pending.RequestId, -32000, "Session terminated");
                    SendJsonRpcResponse(pending.Client, errResp, pending.Headers);
                    _deferredResponses.RemoveAt(i);
                    continue;
                }

                // Check client disconnect
                if (!IsTcpClientAlive(pending.Client))
                {
                    pending.Deferred.InvokeCleanup();
                    McpProtocol.CompleteDeferredRequest(pending.SessionId, true);
                    try { pending.Client?.Close(); }
                    catch { /* ignore */ }
                    _deferredResponses.RemoveAt(i);
                    continue;
                }

                // Check timeout
                if (now >= pending.Deadline)
                {
                    pending.Deferred.InvokeCleanup();
                    var errResp = McpProtocol.BuildErrorResponse(pending.RequestId, -32000,
                        "Deferred request timed out");
                    SendJsonRpcResponse(pending.Client, errResp, pending.Headers);
                    McpProtocol.CompleteDeferredRequest(pending.SessionId, true);
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
                            Debug.LogError($"[MCP Runtime] Deferred result producer threw: {ex}");
                            toolResult = McpProtocol.BuildToolResult(ex.Message, isError: true);
                        }

                        toolResult = ResponseSizeLimiter.Apply(toolResult);
                        var wrapped = McpProtocol.WrapToolResult(toolResult);
                        var jsonRpcResp = McpProtocol.BuildResponse(pending.RequestId, wrapped);
                        SendJsonRpcResponse(pending.Client, jsonRpcResp, pending.Headers);
                        McpProtocol.CompleteDeferredRequest(pending.SessionId,
                            wrapped["isError"]?.Value<bool>() == true);
                        pending.Deferred.InvokeCleanup();
                        _deferredResponses.RemoveAt(i);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[MCP Runtime] Deferred predicate threw: {ex}");
                    pending.Deferred.InvokeCleanup();
                    var errResp = McpProtocol.BuildErrorResponse(pending.RequestId, -32000,
                        $"Deferred predicate error: {ex.Message}");
                    SendJsonRpcResponse(pending.Client, errResp, pending.Headers);
                    McpProtocol.CompleteDeferredRequest(pending.SessionId, true);
                    _deferredResponses.RemoveAt(i);
                }
            }
        }

        private static int GetConnectionHash(TcpClient client)
        {
            try
            {
                return client.Client.Handle.GetHashCode();
            }
            catch
            {
                return client.GetHashCode();
            }
        }

        private static bool IsTcpClientAlive(TcpClient client)
        {
            try
            {
                if (client?.Connected != true) return false;
                var socket = client.Client;
                // Poll with zero timeout to check for disconnect without blocking
                return !(socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
            }
            catch
            {
                return false;
            }
        }

        private void SendJsonRpcResponse(TcpClient client, JObject response, Dictionary<string, string> headers)
        {
            headers["Content-Type"] = "application/json";
            var body = Encoding.UTF8.GetBytes(response.ToString(Formatting.None));
            SendResponse(client, 200, "OK", headers, body);
        }


        // --- Response Helpers ---

        private static void SendResponse(TcpClient client, int statusCode, string statusText,
            Dictionary<string, string> headers, byte[] body)
        {
            try
            {
                if (client?.Connected != true) return;
                var stream = client.GetStream();
                TcpHttpHandler.WriteResponse(stream, statusCode, statusText, headers, body);
            }
            catch (Exception) { /* client disconnected */ }
            finally
            {
                try { client?.Close(); }
                catch { /* ignore */ }
            }
        }

        private static void SendErrorResponse(TcpClient client, int statusCode, string statusText,
            Dictionary<string, string> headers)
        {
            SendResponse(client, statusCode, statusText, headers, null);
        }

        private static void SendJsonError(TcpClient client, int statusCode, string message,
            Dictionary<string, string> headers, string errorCode = null, string suggestion = null)
        {
            headers["Content-Type"] = "application/json";
            JObject error;
            if (!string.IsNullOrEmpty(errorCode))
            {
                error = McpError.Create(errorCode, message, suggestion);
            }
            else
            {
                error = new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = JValue.CreateNull(),
                    ["error"] = new JObject
                    {
                        ["code"] = -32600,
                        ["message"] = message
                    }
                };
            }
            var body = Encoding.UTF8.GetBytes(error.ToString(Formatting.None));
            SendResponse(client, statusCode, statusCode == 200 ? "OK" : "Error", headers, body);
        }

        // --- Port File ---

        private void WritePortFile()
        {
            try
            {
                var portFilePath = Path.Combine(_persistentDataPath, PortFileName);
                File.WriteAllText(portFilePath, _port.ToString());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MCP Runtime] Failed to write port file: {ex.Message}");
            }
        }
    }
}
