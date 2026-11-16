using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace I1rr.UnityMcp.Shared
{
    /// <summary>
    /// Handles MCP authentication token lifecycle — generation, persistence, file permissions,
    /// and constant-time comparison. Tokens are 256-bit random values stored in the Library folder.
    /// </summary>
    public static class McpAuth
    {
        private const string TokenFileName = "MCPServer.token";
        private const int TokenByteLength = 32; // 256 bits of entropy

        public static string LoadOrCreateToken(string libraryPath)
        {
            var tokenFilePath = Path.Combine(libraryPath, TokenFileName);

            if (File.Exists(tokenFilePath))
            {
                RestrictFilePermissions(tokenFilePath);
                var stored = File.ReadAllText(tokenFilePath).Trim();
                if (IsValidTokenString(stored))
                    return stored;

                UnityEngine.Debug.Log("[MCP] Token file corrupted, regenerating.");
            }

            var token = GenerateToken();
            Directory.CreateDirectory(libraryPath);
            File.WriteAllText(tokenFilePath, token);
            RestrictFilePermissions(tokenFilePath);
            UnityEngine.Debug.Log("[MCP] New auth token generated and saved.");
            return token;
        }

        public static bool ValidateAuthorizationHeader(string headerValue, string expectedToken)
        {
            if (string.IsNullOrEmpty(headerValue) || string.IsNullOrEmpty(expectedToken))
                return false;

            const string prefix = "Bearer ";
            if (!headerValue.StartsWith(prefix, StringComparison.Ordinal))
                return false;

            var provided = headerValue.Substring(prefix.Length);
            return ConstantTimeTokenEquals(provided, expectedToken);
        }

        public static bool AuthorizeLocalRequest(bool requireAuthentication, string headerValue, string expectedToken)
        {
            return !requireAuthentication || ValidateAuthorizationHeader(headerValue, expectedToken);
        }

        public static bool ValidateOrigin(string originHeader, int serverPort)
        {
            if (string.IsNullOrEmpty(originHeader))
                return true;

            if (originHeader == "null")
                return false;

            if (originHeader.StartsWith("vscode-webview://", StringComparison.OrdinalIgnoreCase))
                return true;

            var allowed127 = $"http://127.0.0.1:{serverPort}";
            var allowedLocal = $"http://localhost:{serverPort}";

            if (string.Equals(originHeader, allowed127, StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(originHeader, allowedLocal, StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        /// <summary>
        /// Extended origin validation that also accepts a set of additional allowed origins.
        /// Used when the server binds to a non-localhost address (LAN mode).
        /// </summary>
        public static bool ValidateOrigin(string originHeader, int serverPort, System.Collections.Generic.HashSet<string> additionalOrigins)
        {
            if (ValidateOrigin(originHeader, serverPort))
                return true;

            if (additionalOrigins == null || additionalOrigins.Count == 0 || string.IsNullOrEmpty(originHeader))
                return false;

            return additionalOrigins.Contains(originHeader);
        }

        public static bool ValidateHost(string hostHeader, int serverPort)
        {
            if (string.IsNullOrEmpty(hostHeader))
                return false;

            var expected127 = $"127.0.0.1:{serverPort}";
            var expectedLocal = $"localhost:{serverPort}";

            return string.Equals(hostHeader, expected127, StringComparison.OrdinalIgnoreCase)
                || string.Equals(hostHeader, expectedLocal, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Extended host validation that also accepts a set of additional allowed hosts.
        /// Used when the server binds to a non-localhost address (LAN mode).
        /// </summary>
        public static bool ValidateHost(string hostHeader, int serverPort, System.Collections.Generic.HashSet<string> additionalHosts)
        {
            // Always accept localhost
            if (ValidateHost(hostHeader, serverPort))
                return true;

            if (additionalHosts == null || additionalHosts.Count == 0)
                return false;

            // Check additional hosts (already include port suffix)
            return additionalHosts.Contains(hostHeader);
        }

        public static bool ValidateContentType(string contentTypeHeader)
        {
            if (string.IsNullOrEmpty(contentTypeHeader))
                return false;

            var trimmed = contentTypeHeader.Trim();

            if (string.Equals(trimmed, "application/json", StringComparison.OrdinalIgnoreCase))
                return true;

            if (trimmed.StartsWith("application/json;", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        /// <summary>
        /// Generates a cryptographically secure token with 256 bits of entropy,
        /// encoded as Base64url (RFC 4648 §5) without padding.
        /// </summary>
        public static string GenerateToken()
        {
            var bytes = new byte[TokenByteLength];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            return Base64UrlEncode(bytes);
        }

        /// <summary>
        /// Validates that a token string is either a legacy GUID format (36-char)
        /// or the new Base64url format (43-char). Accepts both to ensure existing
        /// tokens remain valid after upgrade.
        /// </summary>
        public static bool IsValidTokenString(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            // Legacy GUID format: 36 chars, e.g. "550e8400-e29b-41d4-a716-446655440000"
            if (value.Length == 36 && Guid.TryParseExact(value, "D", out _))
                return true;

            // New Base64url format: 43 chars (32 bytes → ceil(32*4/3) = 43 without padding)
            if (value.Length == 43 && IsValidBase64Url(value))
                return true;

            return false;
        }

        /// <summary>
        /// HMAC-based constant-time token comparison. Hashes both tokens to a fixed
        /// 32-byte length before comparing, preventing length oracles when old GUID-format
        /// (36 chars) and new Base64url (43 chars) tokens coexist.
        /// </summary>
        internal static bool ConstantTimeTokenEquals(string provided, string stored)
        {
            if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(stored))
                return false;

            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(stored)))
            {
                var expectedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(stored));
                var providedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(provided));

                int diff = 0;
                for (int i = 0; i < expectedHash.Length; i++)
                    diff |= expectedHash[i] ^ providedHash[i];

                return diff == 0;
            }
        }

        /// <summary>
        /// Restricts file permissions so only the current user can read/write/delete the file.
        /// Uses icacls on Windows, chmod on macOS/Linux.
        /// </summary>
        public static void RestrictFilePermissions(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return;

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX
            var psi = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true };
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            psi.FileName = Path.Combine(Environment.SystemDirectory, "icacls.exe");
            var account = Environment.UserDomainName + "\\" + Environment.UserName;
            psi.Arguments = $"\"{filePath}\" /inheritance:r /grant:r \"{account}:(R,W,D)\"";
#else
            psi.FileName = "/bin/chmod";
            psi.Arguments = $"600 \"{filePath}\"";
#endif
            using var proc = Process.Start(psi);
            if (proc == null || !proc.WaitForExit(5000))
                throw new IOException($"Timed out protecting {filePath}");
            if (proc.ExitCode != 0)
                throw new IOException($"Could not protect {filePath} (exit {proc.ExitCode})");
#else
            UnityEngine.Debug.LogWarning("[MCP] Platform-managed storage; token permissions have not been verified.");
#endif
        }

        private static string Base64UrlEncode(byte[] data)
        {
            return Convert.ToBase64String(data)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static bool IsValidBase64Url(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c >= 'A' && c <= 'Z') continue;
                if (c >= 'a' && c <= 'z') continue;
                if (c >= '0' && c <= '9') continue;
                if (c == '-' || c == '_') continue;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Audits the current platform for token security risks and logs warnings.
        /// Call once at server startup to alert operators of platform-specific concerns.
        /// </summary>
        /// <param name="tokenStoragePath">The directory where the token file is stored.</param>
        public static void AuditPlatformTokenSecurity(string tokenStoragePath)
        {
            var platform = Application.platform;

            switch (platform)
            {
                case RuntimePlatform.Android:
                    // Android's persistentDataPath is app-private on non-rooted devices,
                    // but on rooted devices or with ADB debugging enabled, files may be accessible.
                    UnityEngine.Debug.LogWarning(
                        "[MCP Security] Platform: Android. Token stored in app-private storage. " +
                        "On rooted devices or with USB debugging, token file may be accessible. " +
                        "Ensure device is trusted and ADB debugging is disabled in production.");
                    break;

                case RuntimePlatform.IPhonePlayer:
                    // iOS sandboxes apps heavily — token is secure within the app sandbox.
                    UnityEngine.Debug.Log(
                        "[MCP Security] Platform: iOS. Token stored in sandboxed app directory. " +
                        "Security: HIGH — iOS enforces strict app isolation.");
                    break;

                case RuntimePlatform.WebGLPlayer:
                    // WebGL has no real filesystem — should not be running MCP server.
                    UnityEngine.Debug.LogError(
                        "[MCP Security] Platform: WebGL. MCP Runtime server is NOT supported on WebGL. " +
                        "There is no persistent filesystem and no TCP socket support.");
                    break;

                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.LinuxPlayer:
                case RuntimePlatform.OSXPlayer:
                    // Desktop platforms — file permissions are the primary protection.
                    UnityEngine.Debug.Log(
                        $"[MCP Security] Platform: {platform}. Token stored at: {tokenStoragePath}. " +
                        "Security relies on filesystem permissions (chmod 600 / icacls). " +
                        "Verify token file is not world-readable.");
                    break;

                case RuntimePlatform.PS4:
                case RuntimePlatform.PS5:
                case RuntimePlatform.XboxOne:
                case RuntimePlatform.GameCoreXboxOne:
                case RuntimePlatform.GameCoreXboxSeries:
                case RuntimePlatform.Switch:
                    // Console platforms — highly sandboxed, but MCP server is unusual here.
                    UnityEngine.Debug.LogWarning(
                        $"[MCP Security] Platform: {platform} (console). " +
                        "Console platforms are sandboxed but network access may be restricted. " +
                        "Ensure MCP server is only enabled in development kits.");
                    break;

                default:
                    UnityEngine.Debug.LogWarning(
                        $"[MCP Security] Platform: {platform} (unrecognized). " +
                        $"Token stored at: {tokenStoragePath}. " +
                        "Unable to verify filesystem security for this platform. " +
                        "Manually verify that the token file is not accessible to other applications.");
                    break;
            }
        }
    }
}
