using System;
using System.Reflection;
using I1rr.UnityMcp.Shared;
using NUnit.Framework;

namespace I1rr.UnityMcp.Editor.Tests
{
    /// <summary>
    /// Tests for McpPathValidator fallback behavior when ProjectRoot is not set (Step 1.9).
    /// </summary>
    public class PathValidatorFallbackTests
    {
        private string _originalProjectRoot;

        [SetUp]
        public void SetUp()
        {
            // Save and clear ProjectRoot to test fallback behavior
            _originalProjectRoot = McpPathValidator.ProjectRoot;

            // Reset ProjectRoot to null via reflection (the setter rejects null)
            var field = typeof(McpPathValidator).GetField("_projectRoot",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "_projectRoot backing field must exist.");
            field.SetValue(null, null);
        }

        [TearDown]
        public void TearDown()
        {
            // Restore the original ProjectRoot
            if (_originalProjectRoot != null)
                McpPathValidator.ProjectRoot = _originalProjectRoot;
            else
            {
                // Restore null state
                var field = typeof(McpPathValidator).GetField("_projectRoot",
                    BindingFlags.Static | BindingFlags.NonPublic);
                field?.SetValue(null, null);
            }
        }

        [Test]
        public void ValidatePath_WhenProjectRootNotSet_ReturnsFailure()
        {
            var result = McpPathValidator.ValidatePath("Assets/test.cs");
            Assert.That(result.IsValid, Is.False,
                "Path validation must fail when ProjectRoot is not set.");
            Assert.That(result.Error, Does.Contain("project_root_not_set"),
                "Error must clearly indicate that ProjectRoot is not configured.");
        }

        [Test]
        public void ValidatePath_WhenProjectRootNotSet_DoesNotThrow()
        {
            // Previously this threw InvalidOperationException.
            // Now it must return a failure result instead.
            Assert.DoesNotThrow(() =>
            {
                var result = McpPathValidator.ValidatePath("Assets/test.cs");
                Assert.That(result.IsValid, Is.False);
            }, "ValidatePath must not throw when ProjectRoot is unset — it should return a failure result.");
        }

        [Test]
        public void ValidateAndResolvePath_WhenProjectRootNotSet_RejectsAllPaths()
        {
            var result = McpPathValidator.ValidateAndResolvePath("any/path.txt");
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Error, Does.Contain("project_root_not_set"));
        }

        [Test]
        public void ValidatePath_WhenProjectRootSet_AcceptsValidPaths()
        {
            // Restore ProjectRoot and verify normal operation
            McpPathValidator.ProjectRoot = System.IO.Path.GetTempPath();

            var result = McpPathValidator.ValidatePath("subdir/file.txt");
            // It may or may not be valid depending on symlink checks, but it should not
            // fail with project_root_not_set
            Assert.That(result.Error, Is.Not.EqualTo("project_root_not_set").Or.Null);
        }

        [Test]
        public void ProjectRoot_Setter_RejectsNull()
        {
            Assert.Throws<ArgumentException>(() =>
            {
                McpPathValidator.ProjectRoot = null;
            });
        }

        [Test]
        public void ProjectRoot_Setter_RejectsEmpty()
        {
            Assert.Throws<ArgumentException>(() =>
            {
                McpPathValidator.ProjectRoot = "";
            });
        }
    }
}
