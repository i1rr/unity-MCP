using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using UnityEngine.Scripting;

namespace I1rr.UnityMcp.Runtime
{
    /// <summary>
    /// UDP broadcast announcement for network discovery of the MCP runtime server.
    /// Sends a periodic broadcast so that external tools can locate the server's IP and port.
    /// Broadcasts only: port, protocol version, and server name. No tokens or sensitive data.
    /// Off by default — must be explicitly enabled via mcp_config.json.
    /// </summary>
    [Preserve]
    public sealed class UdpDiscovery : MonoBehaviour
    {
        private const int BroadcastPort = 8199;
        private const float DefaultInterval = 5f;
        private const string ServiceName = "i1rr-unity-mcp";
        private const string ProtocolVersion = "2025-03-26";

        /// <summary>
        /// Rate limit: max 1 response per source IP per 10 seconds.
        /// This limits broadcast frequency per unique destination observed.
        /// </summary>
        private const float RateLimitWindowSeconds = 10f;

        private UdpClient _udpClient;
        private IPEndPoint _broadcastEndpoint;
        private byte[] _payload;
        private float _interval = DefaultInterval;
        private bool _enabled = true;
        private int _serverPort;

        // Rate limiting for discovery responses
        private readonly Dictionary<string, float> _lastResponseTime = new Dictionary<string, float>();
        private float _lastCleanupTime;
        private const float CleanupIntervalSeconds = 60f;

        /// <summary>
        /// Starts UDP discovery broadcasts for the given server port.
        /// </summary>
        [Preserve]
        public void StartBroadcasting(int serverPort, float interval = DefaultInterval, bool enabled = true)
        {
            _serverPort = serverPort;
            _interval = Mathf.Max(1f, interval);
            _enabled = enabled;

            if (!_enabled)
                return;

            BuildPayload();
            OpenSocket();

            InvokeRepeating(nameof(SendBroadcast), 0f, _interval);
        }

        /// <summary>
        /// Stops UDP discovery broadcasts and releases the socket.
        /// </summary>
        [Preserve]
        public void StopBroadcasting()
        {
            CancelInvoke(nameof(SendBroadcast));
            CloseSocket();
        }

        [Preserve]
        private void OnDisable()
        {
            StopBroadcasting();
        }

        [Preserve]
        private void OnDestroy()
        {
            StopBroadcasting();
        }

        private void BuildPayload()
        {
            // Only include: port, protocol version, server name (human-friendly)
            // Do NOT include tokens, token hashes, or other sensitive data
            var serverName = Application.productName ?? "Unknown";

            var json = $"{{\"service\":\"{ServiceName}\",\"port\":{_serverPort},\"protocol\":\"{ProtocolVersion}\",\"protocols\":{Shared.McpProtocol.SupportedVersions.ToString(Newtonsoft.Json.Formatting.None)},\"name\":\"{EscapeJson(serverName)}\"}}";
            _payload = Encoding.UTF8.GetBytes(json);
        }

        private void OpenSocket()
        {
            try
            {
                _broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, BroadcastPort);
                _udpClient = new UdpClient();
                _udpClient.EnableBroadcast = true;
                // Don't bind to a specific port — we're only sending
                _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, 1);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MCP Discovery] Failed to open UDP socket: {ex.Message}");
                _udpClient = null;
            }
        }

        private void CloseSocket()
        {
            try
            {
                _udpClient?.Close();
            }
            catch (Exception)
            {
                // Ignore close errors
            }
            finally
            {
                _udpClient = null;
            }
        }

        [Preserve]
        private void SendBroadcast()
        {
            if (_udpClient == null || _payload == null)
                return;

            // Rate limit broadcasts
            var now = Time.unscaledTime;
            var broadcastKey = _broadcastEndpoint.ToString();

            if (_lastResponseTime.TryGetValue(broadcastKey, out var lastTime))
            {
                if (now - lastTime < RateLimitWindowSeconds)
                    return;
            }

            _lastResponseTime[broadcastKey] = now;

            // Periodic cleanup of stale entries
            if (now - _lastCleanupTime > CleanupIntervalSeconds)
            {
                CleanupRateLimitEntries(now);
                _lastCleanupTime = now;
            }

            try
            {
                _udpClient.Send(_payload, _payload.Length, _broadcastEndpoint);
            }
            catch (SocketException ex)
            {
                // Network may be unavailable; log once then stop to avoid spam
                Debug.LogWarning($"[MCP Discovery] Broadcast failed, stopping: {ex.Message}");
                StopBroadcasting();
            }
            catch (ObjectDisposedException)
            {
                // Socket was closed externally
                CancelInvoke(nameof(SendBroadcast));
            }
        }

        private void CleanupRateLimitEntries(float now)
        {
            var keysToRemove = new List<string>();
            foreach (var kvp in _lastResponseTime)
            {
                if (now - kvp.Value > RateLimitWindowSeconds * 2)
                    keysToRemove.Add(kvp.Key);
            }
            foreach (var key in keysToRemove)
                _lastResponseTime.Remove(key);
        }

        private static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
        }
    }
}
