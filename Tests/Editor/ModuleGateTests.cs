using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace I1rr.UnityMcp.Editor.Tests
{
    public class ModuleGateTests
    {
        private const string ToolName = "module_gate_fixture_probe";
        private static readonly string AssemblyKey = typeof(ModuleGateTests).Assembly.GetName().Name;
        private Func<string, bool> _enabled;
        private Action _refresh;
        private IDictionary _tools;
        private IDictionary<string, bool> _availability;
        private IList<IMcpModule> _modules;
        private ProbeModule<int> _module;
        private static int _calls;

        // Open generic types are skipped by discovery; only this test creates one.
        private class ProbeModule<T> : IMcpModule
        {
            public string ModuleName => "GateFixture";
            public bool Available = true;
            public bool IsAvailable => Available;
            public void Initialize() { }
            public void Shutdown() { }
        }

        // Install only during a test. Attributed test tools or IMcpModule fixtures
        // would otherwise leak into ordinary consumers that opt into package tests.
        [SetUp] public void SetUp()
        {
            _enabled = McpToolRegistry.ModuleEnabled; _refresh = McpToolRegistry.RefreshModulePolicy;
            McpToolRegistry.RefreshModulePolicy = null; McpToolRegistry.ModuleEnabled = _ => true;
            McpToolRegistry.ListTools();
            var registry = typeof(McpToolRegistry);
            _tools = (IDictionary)registry.GetField("_tools", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            _availability = (IDictionary<string, bool>)registry.GetField("_moduleAvailability", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            _modules = (IList<IMcpModule>)registry.GetField("_modules", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            _module = new ProbeModule<int>();
            _modules.Add(_module);
            var type = _tools.GetType().GetGenericArguments()[1];
            object entry = Activator.CreateInstance(type, true);
            void Field(string name, object value) => type.GetField(name).SetValue(entry, value);
            Field("Name", ToolName); Field("Description", "Temporary module gate probe.");
            Field("InputSchema", new JObject { ["type"] = "object" });
            Field("Method", typeof(ModuleGateTests).GetMethod(nameof(Probe), BindingFlags.NonPublic | BindingFlags.Static));
            Field("ModuleName", "GateFixture"); Field("ModuleAssembly", AssemblyKey);
            _tools.Add(ToolName, entry); _availability.Add(AssemblyKey, true);
        }
        private static JObject Probe(JObject args) { _calls++; return new JObject { ["ok"] = true }; }
        [TearDown] public void TearDown()
        {
            _tools?.Remove(ToolName); _availability?.Remove(AssemblyKey);
            if (_module != null) _modules?.Remove(_module);
            McpToolRegistry.ModuleEnabled = _enabled; McpToolRegistry.RefreshModulePolicy = _refresh; McpToolRegistry.NotifyCatalogChanged();
        }
        private static bool Listed() => McpToolRegistry.ListTools().Children<JObject>().Any(t => (string)t["name"] == ToolName);
        [Test] public void UninitializedModuleToolsAreNotTreatedAsCoreTools()
        {
            var entry = _tools[ToolName];
            entry.GetType().GetField("ModuleName").SetValue(entry, null);
            Assert.That(Listed(), Is.False);
            Assert.That(McpToolRegistry.Invoke(ToolName, new JObject())["error"], Is.Not.Null);
            Assert.That(((JObject)McpToolRegistry.InvokeRaw(ToolName, new JObject()))["error"], Is.Not.Null);
        }
        [Test] public void DisabledModuleIsHiddenAndCannotRunThroughEitherDispatchPath()
        {
            Assert.That(Listed(), Is.True); int calls = _calls;
            McpToolRegistry.ModuleEnabled = id => id != "GateFixture"; McpToolRegistry.NotifyCatalogChanged();
            Assert.That(Listed(), Is.False);
            Assert.That(McpToolRegistry.Invoke(ToolName, new JObject())["error"], Is.Not.Null);
            Assert.That(((JObject)McpToolRegistry.InvokeRaw(ToolName, new JObject()))["error"], Is.Not.Null);
            Assert.That(_calls, Is.EqualTo(calls));
            McpToolRegistry.ModuleEnabled = _ => true;
            Assert.That((bool)McpToolRegistry.Invoke(ToolName, new JObject())["ok"], Is.True);
            _availability[AssemblyKey] = false;
            Assert.That(Listed(), Is.False);
            Assert.That(McpToolRegistry.Invoke(ToolName, new JObject())["error"], Is.Not.Null);
        }
        [Test] public void AvailabilityIsCachedAndExplicitRecheckProbesOncePerModule()
        {
            int probes = McpToolRegistry.ModuleAvailabilityChecks;
            for (int i = 0; i < 5; i++) Assert.That(Listed(), Is.True);
            Assert.That(McpToolRegistry.ModuleAvailabilityChecks, Is.EqualTo(probes));
            int count = McpToolRegistry.Modules.Count;
            Assert.That(count, Is.GreaterThan(0));
            McpToolRegistry.RecheckModuleAvailability();
            Assert.That(McpToolRegistry.ModuleAvailabilityChecks, Is.EqualTo(probes + count));
            _module.Available = false;
            McpToolRegistry.RecheckModuleAvailability();
            Assert.That(Listed(), Is.False);
            Assert.That(McpToolRegistry.Invoke(ToolName, new JObject())["error"], Is.Not.Null);
        }

        [Test] public void ModernCatalogReflectsPolicyChangeAndDisabledToolCannotExecute()
        {
            var savedList = McpProtocol.OnToolsList;
            var savedCall = McpProtocol.OnToolsCall;
            try
            {
                McpProtocol.OnToolsList = (_, __) => new JObject { ["tools"] = McpToolRegistry.ListTools() };
                McpProtocol.OnToolsCall = (_, __) => McpToolRegistry.Invoke(ToolName, new JObject());
                JObject Dispatch(JObject request) => McpProtocol.ProcessRequestWithContext(request,
                    McpProtocol.CreateModernRequestContext(request), out _);
                var list = McpModernProtocolTests.Request("tools/list");
                Assert.That(Dispatch(list)["result"]["tools"].Any(t => (string)t["name"] == ToolName), Is.True);
                int calls = _calls;
                McpToolRegistry.ModuleEnabled = id => id != "GateFixture";
                McpToolRegistry.NotifyCatalogChanged();
                var result = Dispatch(list);
                Assert.That(result["result"]["tools"].Any(t => (string)t["name"] == ToolName), Is.False);
                Assert.That((int)result["result"]["ttlMs"], Is.Zero);
                var call = McpModernProtocolTests.Request("tools/call");
                call["params"]["name"] = ToolName;
                Assert.That((bool)Dispatch(call)["result"]["isError"], Is.True);
                Assert.That(_calls, Is.EqualTo(calls));
            }
            finally
            {
                McpProtocol.OnToolsList = savedList;
                McpProtocol.OnToolsCall = savedCall;
            }
        }
    }
}
