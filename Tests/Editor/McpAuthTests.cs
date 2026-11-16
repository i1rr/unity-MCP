using System;
using System.Collections.Generic;
using System.IO;
using I1rr.UnityMcp.Shared;
using NUnit.Framework;

namespace I1rr.UnityMcp.Editor.Tests
{
    /// <summary>
    /// Tests for McpAuth token generation, validation, constant-time comparison,
    /// and file permission restrictions.
    /// </summary>
    public class McpAuthTests
    {
        [TestCase(false, null, true)]
        [TestCase(false, "Bearer stale", true)]
        [TestCase(true, null, false)]
        [TestCase(true, "Bearer stale", false)]
        [TestCase(true, "Bearer expected", true)]
        public void LocalAuthorization_ObeysExplicitMode(bool required, string header, bool allowed)
        {
            Assert.That(McpAuth.AuthorizeLocalRequest(required, header, "expected"), Is.EqualTo(allowed));
        }
        // --- Step 1.1: Token Generation ---

        [Test]
        public void GenerateToken_Returns43CharBase64UrlString()
        {
            var token = McpAuth.GenerateToken();
            Assert.That(token.Length, Is.EqualTo(43),
                "Base64url encoding of 32 bytes without padding should be 43 characters.");
        }

        [Test]
        public void GenerateToken_IsNotGuidShaped()
        {
            var token = McpAuth.GenerateToken();
            Assert.That(Guid.TryParseExact(token, "D", out _), Is.False,
                "New tokens must not be parseable as a GUID.");
        }

        [Test]
        public void GenerateToken_ContainsOnlyBase64UrlCharacters()
        {
            var token = McpAuth.GenerateToken();
            foreach (var c in token)
            {
                bool valid = (c >= 'A' && c <= 'Z') ||
                             (c >= 'a' && c <= 'z') ||
                             (c >= '0' && c <= '9') ||
                             c == '-' || c == '_';
                Assert.That(valid, Is.True,
                    $"Character '{c}' is not valid Base64url.");
            }
        }

        [Test]
        public void GenerateToken_ProducesUniqueTokens()
        {
            var tokens = new HashSet<string>();
            for (int i = 0; i < 100; i++)
                tokens.Add(McpAuth.GenerateToken());

            Assert.That(tokens.Count, Is.EqualTo(100),
                "100 generated tokens must all be unique (CSPRNG).");
        }

        [Test]
        public void GenerateToken_Has256BitsEntropy()
        {
            // 32 bytes = 256 bits; Base64url of 32 bytes = ceil(32 * 4 / 3) = 43 chars (no padding)
            var token = McpAuth.GenerateToken();
            // Decode to verify underlying byte count
            var base64 = token.Replace('-', '+').Replace('_', '/');
            switch (base64.Length % 4)
            {
                case 2: base64 += "=="; break;
                case 3: base64 += "="; break;
            }
            var bytes = Convert.FromBase64String(base64);
            Assert.That(bytes.Length, Is.EqualTo(32),
                "Token must contain exactly 32 bytes (256 bits) of entropy.");
        }

        // --- Step 1.1: Token Validation (dual format) ---

        [Test]
        public void IsValidTokenString_AcceptsLegacyGuidFormat()
        {
            var guid = Guid.NewGuid().ToString("D");
            Assert.That(McpAuth.IsValidTokenString(guid), Is.True,
                "Legacy GUID tokens must remain valid.");
        }

        [Test]
        public void IsValidTokenString_AcceptsNewBase64UrlFormat()
        {
            var token = McpAuth.GenerateToken();
            Assert.That(McpAuth.IsValidTokenString(token), Is.True,
                "New Base64url tokens must be valid.");
        }

        [Test]
        public void IsValidTokenString_RejectsNull()
        {
            Assert.That(McpAuth.IsValidTokenString(null), Is.False);
        }

        [Test]
        public void IsValidTokenString_RejectsEmpty()
        {
            Assert.That(McpAuth.IsValidTokenString(""), Is.False);
        }

        [Test]
        public void IsValidTokenString_RejectsArbitraryString()
        {
            Assert.That(McpAuth.IsValidTokenString("not-a-valid-token"), Is.False);
        }

        [Test]
        public void IsValidTokenString_RejectsWrongLengthBase64()
        {
            // 42 chars is not a valid Base64url encoding of 32 bytes
            Assert.That(McpAuth.IsValidTokenString("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnop"), Is.False);
        }

        // --- Step 1.2: Constant-Time Token Comparison ---

        [Test]
        public void ConstantTimeTokenEquals_MatchingTokens_ReturnsTrue()
        {
            var token = McpAuth.GenerateToken();
            Assert.That(McpAuth.ConstantTimeTokenEquals(token, token), Is.True);
        }

        [Test]
        public void ConstantTimeTokenEquals_DifferentTokens_ReturnsFalse()
        {
            var token1 = McpAuth.GenerateToken();
            var token2 = McpAuth.GenerateToken();
            Assert.That(McpAuth.ConstantTimeTokenEquals(token1, token2), Is.False);
        }

        [Test]
        public void ConstantTimeTokenEquals_NullProvided_ReturnsFalse()
        {
            var token = McpAuth.GenerateToken();
            Assert.That(McpAuth.ConstantTimeTokenEquals(null, token), Is.False);
        }

        [Test]
        public void ConstantTimeTokenEquals_NullStored_ReturnsFalse()
        {
            var token = McpAuth.GenerateToken();
            Assert.That(McpAuth.ConstantTimeTokenEquals(token, null), Is.False);
        }

        [Test]
        public void ConstantTimeTokenEquals_EmptyProvided_ReturnsFalse()
        {
            var token = McpAuth.GenerateToken();
            Assert.That(McpAuth.ConstantTimeTokenEquals("", token), Is.False);
        }

        [Test]
        public void ConstantTimeTokenEquals_EmptyStored_ReturnsFalse()
        {
            var token = McpAuth.GenerateToken();
            Assert.That(McpAuth.ConstantTimeTokenEquals(token, ""), Is.False);
        }

        [Test]
        public void ConstantTimeTokenEquals_MismatchedLength_ReturnsFalse()
        {
            // Legacy GUID (36 chars) vs new Base64url (43 chars) — must reject
            var guid = Guid.NewGuid().ToString("D");
            var newToken = McpAuth.GenerateToken();
            Assert.That(McpAuth.ConstantTimeTokenEquals(guid, newToken), Is.False);
            Assert.That(McpAuth.ConstantTimeTokenEquals(newToken, guid), Is.False);
        }

        [Test]
        public void ConstantTimeTokenEquals_LegacyGuidMatch_ReturnsTrue()
        {
            var guid = Guid.NewGuid().ToString("D");
            Assert.That(McpAuth.ConstantTimeTokenEquals(guid, guid), Is.True);
        }

        // --- Step 1.2: ValidateAuthorizationHeader uses constant-time comparison ---

        [Test]
        public void ValidateAuthorizationHeader_ValidBearerToken_ReturnsTrue()
        {
            var token = McpAuth.GenerateToken();
            Assert.That(McpAuth.ValidateAuthorizationHeader($"Bearer {token}", token), Is.True);
        }

        [Test]
        public void ValidateAuthorizationHeader_WrongToken_ReturnsFalse()
        {
            var token = McpAuth.GenerateToken();
            var wrong = McpAuth.GenerateToken();
            Assert.That(McpAuth.ValidateAuthorizationHeader($"Bearer {wrong}", token), Is.False);
        }

        [Test]
        public void ValidateAuthorizationHeader_MissingBearer_ReturnsFalse()
        {
            var token = McpAuth.GenerateToken();
            Assert.That(McpAuth.ValidateAuthorizationHeader(token, token), Is.False);
        }

        [Test]
        public void ValidateAuthorizationHeader_NullHeader_ReturnsFalse()
        {
            Assert.That(McpAuth.ValidateAuthorizationHeader(null, "token"), Is.False);
        }

        [Test]
        public void ValidateAuthorizationHeader_NullExpected_ReturnsFalse()
        {
            Assert.That(McpAuth.ValidateAuthorizationHeader("Bearer token", null), Is.False);
        }

        // --- Step 1.3: Token File Permissions ---

#if UNITY_EDITOR_WIN
        [Test]
        public void RestrictFilePermissions_AllowsOwnerToReadWriteAndDelete()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "mcp_permissions_" + Guid.NewGuid().ToString("N"));
            var tokenFile = Path.Combine(tempDir, "runtime.token");
            try
            {
                Directory.CreateDirectory(tempDir);
                // Do not let inherited parent DELETE_CHILD rights mask a missing file DELETE right.
                SetWindowsOwnerPermissions(tempDir, "R,W,D");
                File.WriteAllText(tokenFile, "permission-test");
                McpAuth.RestrictFilePermissions(tokenFile);

                Assert.That(File.ReadAllText(tokenFile), Is.EqualTo("permission-test"));
                File.WriteAllText(tokenFile, "updated");
                Assert.That(File.ReadAllText(tokenFile), Is.EqualTo("updated"));
                Assert.DoesNotThrow(() => File.Delete(tokenFile),
                    "Development build cleanup must be able to delete the restricted token.");
                Assert.That(File.Exists(tokenFile), Is.False);
            }
            finally
            {
                if (File.Exists(tokenFile)) SetWindowsOwnerPermissions(tokenFile, "F");
                if (Directory.Exists(tempDir))
                {
                    SetWindowsOwnerPermissions(tempDir, "F");
                    Directory.Delete(tempDir, true);
                }
            }
        }

        private static void SetWindowsOwnerPermissions(string path, string rights)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "icacls",
                Arguments = $"\"{path}\" /inheritance:r /grant:r \"{Environment.UserName}:({rights})\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var process = System.Diagnostics.Process.Start(psi))
            {
                Assert.That(process, Is.Not.Null);
                Assert.That(process.WaitForExit(5000), Is.True, "icacls timed out.");
                Assert.That(process.ExitCode, Is.Zero, process.StandardError.ReadToEnd());
            }
        }
#endif

        [Test]
        public void RestrictFilePermissions_DoesNotThrowOnMissingFile()
        {
            Assert.DoesNotThrow(() => McpAuth.RestrictFilePermissions("/nonexistent/path/file.token"));
        }

        [Test]
        public void RestrictFilePermissions_DoesNotThrowOnNull()
        {
            Assert.DoesNotThrow(() => McpAuth.RestrictFilePermissions(null));
        }

        [Test]
        public void RestrictFilePermissions_DoesNotThrowOnEmpty()
        {
            Assert.DoesNotThrow(() => McpAuth.RestrictFilePermissions(""));
        }

        [Test]
        public void LoadOrCreateToken_CreatesTokenFile_WithRestrictedAccess()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "mcp_test_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tempDir);
                var token = McpAuth.LoadOrCreateToken(tempDir);

                Assert.That(token, Is.Not.Null.And.Not.Empty);
                Assert.That(McpAuth.IsValidTokenString(token), Is.True);

                var tokenFile = Path.Combine(tempDir, "MCPServer.token");
                Assert.That(File.Exists(tokenFile), Is.True, "Token file should be created.");

                // On Windows, verify the file is not world-readable by checking icacls output
#if UNITY_EDITOR_WIN
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "icacls",
                    Arguments = $"\"{tokenFile}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                var proc = System.Diagnostics.Process.Start(psi);
                var output = proc?.StandardOutput.ReadToEnd() ?? "";
                proc?.WaitForExit(5000);

                // Should NOT contain BUILTIN\Users or Everyone
                Assert.That(output, Does.Not.Contain("BUILTIN\\Users"),
                    "Token file should not be readable by BUILTIN\\Users.");
                Assert.That(output, Does.Not.Contain("Everyone"),
                    "Token file should not be readable by Everyone.");
#endif
            }
            finally
            {
                try { Directory.Delete(tempDir, true); }
                catch { /* best effort cleanup */ }
            }
        }

        [Test]
        public void LoadOrCreateToken_ReturnsExistingValidToken()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "mcp_test_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tempDir);
                var tokenFile = Path.Combine(tempDir, "MCPServer.token");

                // Write a legacy GUID token
                var legacyToken = Guid.NewGuid().ToString("D");
                File.WriteAllText(tokenFile, legacyToken);

                var loaded = McpAuth.LoadOrCreateToken(tempDir);
                Assert.That(loaded, Is.EqualTo(legacyToken),
                    "Should return existing valid legacy token without regeneration.");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); }
                catch { /* best effort cleanup */ }
            }
        }
    }
}
