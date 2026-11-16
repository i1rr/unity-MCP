using System;
using System.IO;
using System.Linq;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace I1rr.UnityMcp.Editor.Tests
{
    public class DashboardRefreshTests
    {
        [Test]
        public void DashboardAndModuleToolsAreImportedAndDiscoverable()
        {
            var names = McpToolRegistry.ListTools().Children<JObject>().Select(t => (string)t["name"]).ToArray();
            Assert.That(names, Does.Contain("show_dashboard"));
            Assert.That(names, Does.Contain("get_dashboard_state"));
            Assert.That(names, Does.Contain("manage_module"));
            Assert.That(names, Does.Not.Contain("module_gate_fixture_probe"));
            Assert.That(McpToolRegistry.Modules.Any(m => m.ModuleName == "GateFixture"), Is.False);
        }

        [Test]
        public void UnchangedFilesDoNotRereadBodiesAndDeletingGuidanceUpdatesState()
        {
            string root = Path.Combine(Path.GetTempPath(), "McpRefresh-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                McpBacklogSetup.Enable(root);
                var cache = new McpBacklogSetup.StateCache();
                Assert.That(cache.Read(root).Enabled, Is.True);
                for (int i = 0; i < 5; i++) Assert.That(cache.Read(root).Enabled, Is.True);
                Assert.That(cache.Inspections, Is.EqualTo(1));
                File.WriteAllText(Path.Combine(root, "AGENTS.md"), "Owner instructions only.");
                Assert.That(cache.Read(root).Enabled, Is.False);
                Assert.That(cache.Inspections, Is.EqualTo(2));
            }
            finally { Directory.Delete(root, true); }
        }

        [Test]
        public void RepeatedSnapshotsDoNotRebuildAnUnchangedToolTable()
        {
            var window = ScriptableObject.CreateInstance<McpDashboardWindow>();
            try
            {
                window.SelectTab("Tools");
                int before = (int)window.InspectSnapshot()["tool_catalog_builds"];
                for (int i = 0; i < 5; i++) window.RefreshSnapshot();
                Assert.That((int)window.InspectSnapshot()["tool_catalog_builds"], Is.EqualTo(before));
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }
    }
}
