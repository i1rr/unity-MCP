using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections;
using Newtonsoft.Json.Linq;
using I1rr.UnityMcp.Shared;
using NUnit.Framework;

namespace I1rr.UnityMcp.Editor.Tests
{
    public class ToolUsageStoreTests
    {
        private string _directory, _path;
        [SetUp] public void SetUp() { _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); _path = Path.Combine(_directory, "usage.json"); }
        [TearDown] public void TearDown() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

        [Test] public void CountsSurviveReloadAndRetainRemovedTools()
        {
            var store = new ToolUsageStore(_path);
            store.Record("old_tool"); store.Record("get_hierarchy"); store.Record("get_hierarchy"); store.Flush();
            var loaded = new ToolUsageStore(_path);
            Assert.That(loaded.Snapshot()["get_hierarchy"], Is.EqualTo(2));
            Assert.That(loaded.Snapshot()["old_tool"], Is.EqualTo(1));
            Assert.That(loaded.StartedAtUtc, Is.EqualTo(store.StartedAtUtc));
            loaded.Record("get_hierarchy"); loaded.Flush();
            Assert.That(new ToolUsageStore(_path).Snapshot()["get_hierarchy"], Is.EqualTo(3));
        }

        [Test] public void SnapshotCannotMutateStoredCounts()
        {
            var store = new ToolUsageStore(_path); store.Record("tool"); store.Snapshot()["tool"] = 99;
            Assert.That(store.Snapshot()["tool"], Is.EqualTo(1));
        }

        [Test] public void DashboardRanksUsageAcrossCategoriesInBothDirections()
        {
            var window = UnityEngine.ScriptableObject.CreateInstance<McpDashboardWindow>();
            try
            {
                const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
                var type = typeof(McpDashboardWindow);
                var order = type.GetField("_toolOrder", fields);
                foreach (string sort in new[] { "MostUsed", "LeastUsed" })
                {
                    order.SetValue(window, Enum.Parse(order.FieldType, sort));
                    type.GetMethod("RefreshTools", fields).Invoke(window, null);
                    var rows = ((IList)type.GetField("_byUsage", fields).GetValue(window)).Cast<object>().ToList();
                    var counts = rows.Select(row => (long)row.GetType().GetField("Count", fields).GetValue(row)).ToArray();
                    Assert.That(counts.Length, Is.GreaterThan(10));
                    Assert.That(counts, Is.EqualTo(sort == "MostUsed" ? counts.OrderByDescending(n => n) : counts.OrderBy(n => n)));
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }

        [Test] public void SearchFindsSpecialistSchemaAndDescription()
        {
            var found = ToolCatalog.Discover(new JObject { ["query"] = "local file IDs" });
            Assert.That(found["names"].Values<string>(), Does.Contain("get_asset_object_index"));
            var schema = ToolCatalog.Discover(new JObject { ["name"] = "p4_status" });
            Assert.That((string)schema["inputSchema"]["type"], Is.EqualTo("object"));
        }

        [Test] public void RegistryCountsImmediateAndRawAttemptsOnceButNotDiscovery()
        {
            var previous = McpToolRegistry.ToolInvoked;
            var store = new ToolUsageStore(_path);
            try
            {
                McpToolRegistry.ToolInvoked = store.Record;
                McpToolRegistry.ListTools();
                McpToolRegistry.Invoke("get_editor_state", null);
                McpToolRegistry.InvokeRaw("get_editor_state", null);
                McpToolRegistry.Invoke("missing_tool_for_usage_test", null);
                Assert.That(store.Snapshot().Count, Is.EqualTo(1));
                Assert.That(store.Snapshot()["get_editor_state"], Is.EqualTo(2));
            }
            finally { McpToolRegistry.ToolInvoked = previous; }
        }

        [Test] public void CorruptHistoryRecoversBackupAndRetainsUnreadableFile()
        {
            var store = new ToolUsageStore(_path); store.Record("tool"); store.Flush(); store.Record("tool"); store.Flush();
            File.WriteAllText(_path, "broken");
            var recovered = new ToolUsageStore(_path);
            Assert.That(recovered.PersistenceError, Is.Not.Null);
            Assert.That(recovered.Snapshot()["tool"], Is.EqualTo(1));
            recovered.Record("tool"); recovered.Flush();
            Assert.That(File.ReadAllText(_path + ".unreadable"), Is.EqualTo("broken"));
            Assert.That(new ToolUsageStore(_path).Snapshot()["tool"], Is.EqualTo(2));
        }
    }
}
