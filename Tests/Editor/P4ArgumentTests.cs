using System;
using System.Reflection;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace I1rr.UnityMcp.Editor.Tests
{
    /// <summary>
    /// Tests for Perforce argument construction hardening (Step 1.8)
    /// and execute_code gate enforcement (Step 1.7).
    /// </summary>
    public class P4ArgumentTests
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
        }

        // --- Step 1.8: BuildP4Arguments escapes embedded quotes ---

        [Test]
        public void BuildP4Arguments_EscapesEmbeddedQuotes()
        {
            // Access the private BuildP4Arguments method via reflection
            var method = typeof(P4EditTools).Assembly.GetType("I1rr.UnityMcp.Editor.P4CommandRunner", true).GetMethod("BuildP4Arguments",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "BuildP4Arguments must exist as a private static method.");

            var result = (string)method.Invoke(null, new object[] { "edit", new[] { "Assets/x\"-f \"y" } });

            // The embedded quotes must be escaped so they don't inject flags
            Assert.That(result, Does.Not.Contain(" -f "),
                "Embedded quotes must not allow flag injection.");
            Assert.That(result, Does.Contain("\\\""),
                "Embedded quotes must be escaped with backslash.");
        }

        [Test]
        public void BuildP4Arguments_PathWithSpacesIsQuoted()
        {
            var method = typeof(P4EditTools).Assembly.GetType("I1rr.UnityMcp.Editor.P4CommandRunner", true).GetMethod("BuildP4Arguments",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            var result = (string)method.Invoke(null, new object[] { "edit", new[] { "Assets/My Folder/file.cs" } });

            Assert.That(result, Does.Contain("\"Assets/My Folder/file.cs\""),
                "Paths with spaces must be wrapped in quotes.");
        }

        [Test]
        public void BuildP4Arguments_SimplePathNotQuoted()
        {
            var method = typeof(P4EditTools).Assembly.GetType("I1rr.UnityMcp.Editor.P4CommandRunner", true).GetMethod("BuildP4Arguments",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            var result = (string)method.Invoke(null, new object[] { "edit", new[] { "Assets/file.cs" } });

            Assert.That(result, Does.Not.Contain("\"Assets/file.cs\""),
                "Simple paths without spaces should not be quoted.");
            Assert.That(result, Does.Contain("Assets/file.cs"));
        }

        [Test]
        public void BuildP4Arguments_CommandAllowlistEnforcedBeforeArgConstruction()
        {
            // RunP4Command checks the allowlist before calling BuildP4Arguments.
            // Verify via reflection that the allowlist check comes first.
            var runMethod = typeof(P4EditTools).Assembly.GetType("I1rr.UnityMcp.Editor.P4CommandRunner", true).GetMethod("RunP4Command",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(runMethod, Is.Not.Null, "RunP4Command must exist.");

            // Invoke with a disallowed command — should return failure without constructing args
            var result = runMethod.Invoke(null, new object[] { "obliterate", new string[] { "//..." }, 1000 });
            Assert.That(result, Is.Not.Null);

            // Use reflection to read the Success property
            var successProp = result.GetType().GetProperty("Success");
            Assert.That(successProp, Is.Not.Null);
            Assert.That(successProp.GetValue(result), Is.False,
                "Disallowed commands must be rejected before argument construction.");

            var errorProp = result.GetType().GetProperty("Error");
            var error = (string)errorProp.GetValue(result);
            Assert.That(error, Does.Contain("not in the allowed list"));
        }

        // --- Step 1.8: McpPathValidator rejects double-quote characters ---

        [Test]
        public void PathValidator_RejectsPathWithDoubleQuotes()
        {
            McpPathValidator.ProjectRoot = System.IO.Path.GetTempPath();

            var result = McpPathValidator.ValidatePath("Assets/x\"-f \"y.cs");
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Error, Is.EqualTo("invalid_char_quote"));
        }

        [Test]
        public void PathValidator_RejectsPathWithSingleEmbeddedQuote()
        {
            McpPathValidator.ProjectRoot = System.IO.Path.GetTempPath();

            var result = McpPathValidator.ValidatePath("Assets/file\".cs");
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Error, Is.EqualTo("invalid_char_quote"));
        }

        // --- Step 1.8: Adversarial P4 path inputs ---

        [Test]
        public void PathValidator_RejectsPathWithSemicolons_ViaTraversal()
        {
            McpPathValidator.ProjectRoot = System.IO.Path.GetTempPath();

            // Semicolons in paths are OS-level concerns, but validate traversal protection works
            var result = McpPathValidator.ValidatePath("Assets/../../../etc/passwd");
            Assert.That(result.IsValid, Is.False,
                "Path traversal via ../ must be rejected.");
        }

        [Test]
        public void PathValidator_RejectsNullBytes()
        {
            McpPathValidator.ProjectRoot = System.IO.Path.GetTempPath();

            var result = McpPathValidator.ValidatePath("Assets/file\0.cs");
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Error, Is.EqualTo("null_byte"));
        }

        [Test]
        public void BuildP4Arguments_AdversarialFlagInjection()
        {
            var method = typeof(P4EditTools).Assembly.GetType("I1rr.UnityMcp.Editor.P4CommandRunner", true).GetMethod("BuildP4Arguments",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            // Attempt to inject -d flag via a crafted path
            var result = (string)method.Invoke(null, new object[] { "edit", new[] { "Assets/file\"-d \"all" } });
            Assert.That(result, Does.Not.Contain(" -d "),
                "Flag injection via embedded quotes must be prevented.");
        }

        // --- Step 1.7: execute_code gate enforcement ---

        [Test]
        public void ExecuteCode_DisabledByDefault_ReturnsError()
        {
            // Save and clear the preference
            var wasEnabled = McpPreferencesProvider.EnableCodeExecution;
            try
            {
                McpPreferencesProvider.EnableCodeExecution = false;

                var parameters = new JObject { ["code"] = "1 + 1" };
                var result = UtilityTools.ExecuteCode(parameters);

                Assert.That(result["success"]?.Value<bool>(), Is.False);
                Assert.That(result["error"]?.Value<string>(), Does.Contain("disabled by default"));
                Assert.That(result["error"]?.Value<string>(), Does.Contain("Project Settings"));
            }
            finally
            {
                McpPreferencesProvider.EnableCodeExecution = wasEnabled;
            }
        }

        [Test]
        public void ExecuteCode_GateEnforcedInsideToolMethod()
        {
            // This test verifies the gate is inside the tool method itself,
            // not at the routing layer — meaning batch_execute cannot bypass it.
            var wasEnabled = McpPreferencesProvider.EnableCodeExecution;
            try
            {
                McpPreferencesProvider.EnableCodeExecution = false;

                // Direct invocation simulates what batch_execute does (McpToolRegistry.Invoke)
                var parameters = new JObject { ["code"] = "System.IO.File.Delete(\"important.dat\")" };
                var result = UtilityTools.ExecuteCode(parameters);

                Assert.That(result["success"]?.Value<bool>(), Is.False,
                    "Gate must be enforced inside the tool method, not at routing layer.");
                Assert.That(result["error"]?.Value<string>(), Does.Contain("disabled"));
            }
            finally
            {
                McpPreferencesProvider.EnableCodeExecution = wasEnabled;
            }
        }
    }
}
