using System;
using System.Diagnostics;
using System.IO;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.Build;

namespace I1rr.UnityMcp.Editor.Tests
{
    public class ReleaseBoundaryTests
    {
        private string root, assets, staged, original, link;
        [SetUp]
        public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            assets = Directory.CreateDirectory(Path.Combine(root, "project", "Assets")).FullName;
            staged = Path.Combine(root, "stage", "mcp_runtime.token");
            original = McpPathValidator.ProjectRoot;
            McpPathValidator.ProjectRoot = Path.GetDirectoryName(assets);
            link = Path.Combine(assets, "link");
        }

        [TearDown]
        public void TearDown()
        {
            McpPathValidator.ProjectRoot = original;
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(root, true);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BuildRefusesToPackageOrDeleteProjectToken(bool development)
        {
            var sentinel = Path.Combine(assets, "mcp_runtime.token");
            File.WriteAllText(sentinel, "user data");
            Assert.Throws<BuildFailedException>(() => McpBuildProcessor.PrepareToken(assets, staged, development,
                (src, dst) => Assert.Fail("Must not register this asset")));
            Assert.That(File.ReadAllText(sentinel), Is.EqualTo("user data"));
            Assert.That(File.Exists(staged), Is.False);
        }

        [Test]
        public void OnlyDevelopmentBuildsStageTokensAndFailureIsRecoverable()
        {
            Assert.Throws<IOException>(() => McpBuildProcessor.PrepareToken(assets, staged, true,
                (src, dst) => throw new IOException("cancelled")));
            Assert.That(File.Exists(staged), Is.False);
            McpBuildProcessor.PrepareToken(assets, staged, true, (src, dst) => {
                Assert.That(dst, Is.EqualTo("mcp_runtime.token"));
                Assert.That(McpAuth.IsValidTokenString(File.ReadAllText(src)), Is.True);
            });
            McpBuildProcessor.PrepareToken(assets, staged, false, (src, dst) => Assert.Fail("Release token"));
            Assert.That(File.Exists(staged), Is.False);
            Assert.That(Directory.GetFiles(assets), Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ProjectTraversalSkipsExternalDirectoriesAndCycles(bool cycle)
        {
            var target = cycle ? assets : Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
            File.WriteAllText(Path.Combine(assets, "local.txt"), "marker");
            if (!cycle) File.WriteAllText(Path.Combine(target, "outside.txt"), "marker");
            bool windows = Path.DirectorySeparatorChar == '\\';
            var info = new ProcessStartInfo(windows ? "cmd.exe" : "ln", windows
                ? $"/c mklink /J \"{link}\" \"{target}\"" : $"-s \"{target}\" \"{link}\"")
                { UseShellExecute = false, CreateNoWindow = true };
            using (var process = Process.Start(info))
            {
                Assert.That(process.WaitForExit(5000) && process.ExitCode == 0, Is.True, "Create link fixture");
            }
            var result = CodeSearchTools.SearchInFiles(new JObject { ["query"] = "marker" });
            Assert.That((int)result["match_count"], Is.EqualTo(1));
            var listing = FileTools.ListDirectory(new JObject { ["path"] = "Assets", ["recursive"] = true });
            Assert.That((int)listing["count"], Is.EqualTo(1));
            Assert.That((string)listing["entries"][0]["name"], Is.EqualTo("local.txt"));
        }
    }
}
