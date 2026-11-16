using System;
using System.IO;
using System.Threading;
using UnityEngine;
using UnityEngine.Scripting;

namespace I1rr.UnityMcp.Runtime
{
    /// <summary>
    /// Static configuration reader for the MCP runtime server.
    /// Loads from StreamingAssets/mcp_config.json on first access with thread-safe lazy initialization.
    /// </summary>
    [Preserve]
    public static class McpRuntimeConfig
    {
        private const string ConfigFileName = "mcp_config.json";

        private static ConfigData _instance;
        private static readonly object _lock = new object();
        private static volatile bool _loaded;

        /// <summary>
        /// The loaded configuration. Falls back to defaults if the config file is missing or invalid.
        /// </summary>
        [Preserve]
        public static ConfigData Current
        {
            get
            {
                if (_loaded)
                    return _instance;

                lock (_lock)
                {
                    if (_loaded)
                        return _instance;

                    _instance = Load();
                    _loaded = true;
                }

                return _instance;
            }
        }

        /// <summary>
        /// Forces a reload of the configuration from disk on next access.
        /// </summary>
        [Preserve]
        public static void Invalidate()
        {
            lock (_lock)
            {
                _loaded = false;
                _instance = default;
            }
        }

        private static ConfigData Load()
        {
            var defaults = ConfigData.Defaults;

            try
            {
                var configPath = Path.Combine(Application.streamingAssetsPath, ConfigFileName);
                if (!File.Exists(configPath))
                    return defaults;

                var json = File.ReadAllText(configPath);
                var result = ParseConfig(json, defaults);
                LogLanModeWarnings(result);
                return result;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MCP Config] Failed to load config, using defaults: {ex.Message}");
                return defaults;
            }
        }

        private static void LogLanModeWarnings(ConfigData config)
        {
            if (!config.IsLanMode)
                return;

            Debug.LogWarning(
                "[MCP Config] WARNING: Server is binding to non-localhost address '" + config.BindAddress + "'. " +
                "This exposes the MCP server to the local network. Ensure you have configured " +
                "'allowed_ips' in mcp_config.json to restrict access to trusted clients only.");

            if (config.EnableUdpDiscovery)
            {
                Debug.LogWarning(
                    "[MCP Config] WARNING: UDP discovery is enabled with LAN binding. " +
                    "Discovery broadcasts the server port on the local network. " +
                    "Ensure this is intentional for your deployment.");
            }

            if (string.IsNullOrEmpty(config.AllowedIps))
            {
                Debug.LogWarning(
                    "[MCP Config] WARNING: No 'allowed_ips' configured while using LAN binding. " +
                    "All network clients can connect. Set 'allowed_ips' to restrict access, " +
                    "e.g. \"allowed_ips\": \"192.168.1.100,192.168.1.101\"");
            }
        }

        private static ConfigData ParseConfig(string json, ConfigData defaults)
        {
            // Manual parsing to avoid heavy JSON dependency at runtime startup
            var result = defaults;

            try
            {
                // Simple JSON field extraction — robust enough for flat config
                result.BindAddress = ExtractString(json, "bind_address", defaults.BindAddress);
                result.PortOverride = ExtractInt(json, "port_override", defaults.PortOverride);
                result.MaxRequestsPerFrame = ExtractInt(json, "max_requests_per_frame", defaults.MaxRequestsPerFrame);
                result.EnableUdpDiscovery = ExtractBool(json, "enable_udp_discovery", defaults.EnableUdpDiscovery);
                result.UdpBroadcastInterval = ExtractFloat(json, "udp_broadcast_interval", defaults.UdpBroadcastInterval);
                result.LogLevel = ExtractString(json, "log_level", defaults.LogLevel);
                result.AllowedIps = ExtractString(json, "allowed_ips", defaults.AllowedIps);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MCP Config] Parse error, using partial defaults: {ex.Message}");
            }

            return result;
        }

        private static string ExtractString(string json, string key, string fallback)
        {
            var pattern = $"\"{key}\"";
            var idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return fallback;

            idx = json.IndexOf(':', idx + pattern.Length);
            if (idx < 0) return fallback;

            var startQuote = json.IndexOf('"', idx + 1);
            if (startQuote < 0) return fallback;

            var endQuote = json.IndexOf('"', startQuote + 1);
            if (endQuote < 0) return fallback;

            return json.Substring(startQuote + 1, endQuote - startQuote - 1);
        }

        private static int ExtractInt(string json, string key, int fallback)
        {
            var pattern = $"\"{key}\"";
            var idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return fallback;

            idx = json.IndexOf(':', idx + pattern.Length);
            if (idx < 0) return fallback;

            var start = idx + 1;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t'))
                start++;

            var end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-'))
                end++;

            if (end == start) return fallback;

            return int.TryParse(json.Substring(start, end - start), out var val) ? val : fallback;
        }

        private static float ExtractFloat(string json, string key, float fallback)
        {
            var pattern = $"\"{key}\"";
            var idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return fallback;

            idx = json.IndexOf(':', idx + pattern.Length);
            if (idx < 0) return fallback;

            var start = idx + 1;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t'))
                start++;

            var end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '.' || json[end] == '-'))
                end++;

            if (end == start) return fallback;

            return float.TryParse(json.Substring(start, end - start),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var val) ? val : fallback;
        }

        private static bool ExtractBool(string json, string key, bool fallback)
        {
            var pattern = $"\"{key}\"";
            var idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return fallback;

            idx = json.IndexOf(':', idx + pattern.Length);
            if (idx < 0) return fallback;

            var rest = json.Substring(idx + 1).TrimStart();
            if (rest.StartsWith("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (rest.StartsWith("false", StringComparison.OrdinalIgnoreCase)) return false;

            return fallback;
        }

        /// <summary>
        /// Runtime configuration data.
        /// </summary>
        [Preserve]
        public struct ConfigData
        {
            public string BindAddress;
            public int PortOverride;
            public int MaxRequestsPerFrame;
            public bool EnableUdpDiscovery;
            public float UdpBroadcastInterval;
            public string LogLevel;
            /// <summary>
            /// Comma-separated list of allowed client IP addresses when binding to non-localhost.
            /// Empty means all IPs are allowed (when non-localhost binding is active).
            /// </summary>
            public string AllowedIps;

            /// <summary>
            /// Returns true if the bind address is a non-localhost address (LAN mode).
            /// </summary>
            public bool IsLanMode
            {
                get
                {
                    if (string.IsNullOrEmpty(BindAddress))
                        return false;
                    return BindAddress != "127.0.0.1" && BindAddress != "localhost";
                }
            }

            public static ConfigData Defaults => new ConfigData
            {
                BindAddress = "127.0.0.1",
                PortOverride = 0,
                MaxRequestsPerFrame = 3,
                EnableUdpDiscovery = false,
                UdpBroadcastInterval = 5f,
                LogLevel = "warning",
                AllowedIps = ""
            };
        }
    }
}
