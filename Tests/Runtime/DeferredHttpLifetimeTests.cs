using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace I1rr.UnityMcp.Runtime.Tests
{
    public class DeferredHttpLifetimeTests
    {
        private static DeferredResult _pending;
        private static bool _complete;
        private static int _cleanup;
        private static object Probe(JObject _) => _pending = new DeferredResult
        {
            IsComplete = () => _complete,
            GetResult = () => new JObject { ["done"] = true },
            Cleanup = () => _cleanup++
        };

        [TestCase("complete", false)]
        [TestCase("timeout", true)]
        [TestCase("disconnect", true)]
        [TestCase("shutdown", true)]
        public void RuntimeDeferredTransportOwnsItsContextAndCleansOnce(string mode, bool cancelled)
        {
            const string name = "temporary_runtime_lifetime_fixture";
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            McpToolRegistry.ListTools();
            var table = (IDictionary)typeof(McpToolRegistry).GetField("_tools", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var entryType = table.GetType().GetGenericArguments()[1];
            var entry = Activator.CreateInstance(entryType, true);
            void Field(string field, object value) => entryType.GetField(field).SetValue(entry, value);
            Field("Name", name); Field("Description", "Temporary lifetime fixture"); Field("DeferredResponse", true);
            Field("InputSchema", new JObject { ["type"] = "object" });
            Field("Method", typeof(DeferredHttpLifetimeTests).GetMethod(nameof(Probe), BindingFlags.Static | BindingFlags.NonPublic));
            table.Add(name, entry);
            _cleanup = 0; _complete = mode == "complete";
            var go = new GameObject("Runtime lifetime test");
            var server = go.AddComponent<McpRuntimeServer>();
            var listener = new TcpListener(IPAddress.Loopback, 0);
            TcpClient sender = null, accepted = null;
            var pool = (McpStreamPool)typeof(McpRuntimeServer).GetField("_streams", flags).GetValue(server);
            var managerField = typeof(Sessions.SessionManager).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic);
            var originalManager = managerField.GetValue(null);
            managerField.SetValue(null, Activator.CreateInstance(typeof(Sessions.SessionManager), true));
            try
            {
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                typeof(McpRuntimeServer).GetField("_port", flags).SetValue(server, port);
                typeof(McpRuntimeServer).GetField("_authToken", flags).SetValue(server, "test");
                sender = new TcpClient(); sender.Connect(IPAddress.Loopback, port);
                accepted = listener.AcceptTcpClient();
                var request = new JObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = 42, ["method"] = "tools/call",
                    ["params"] = new JObject { ["name"] = name, ["arguments"] = new JObject(), ["_meta"] = new JObject
                    {
                        [McpProtocol.VersionMetaKey] = McpProtocol.ProtocolVersionModern,
                        [McpProtocol.CapabilitiesMetaKey] = new JObject()
                    } }
                };
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Host"] = "127.0.0.1:" + port, ["Authorization"] = "Bearer test", ["Content-Type"] = "application/json",
                    ["MCP-Protocol-Version"] = McpProtocol.ProtocolVersionModern, ["Mcp-Method"] = "tools/call", ["Mcp-Name"] = name
                };
                typeof(McpRuntimeServer).GetMethod("HandlePost", flags).Invoke(server, new object[]
                {
                    accepted, new TcpHttpHandler.HttpRequest { Method = "POST", Path = "/mcp", Headers = headers,
                        Body = Encoding.UTF8.GetBytes(request.ToString(Formatting.None)) }
                });
                Assert.That(_pending, Is.Not.Null);
                Assert.That(_pending.SessionId, Is.Null);
                Assert.That(_pending.RequestContext.IsModern, Is.True);
                Assert.That(_pending.RequestContext.Cancellation.CanBeCanceled, Is.True);
                if (mode == "timeout")
                {
                    var pending = (IList)typeof(McpRuntimeServer).GetField("_deferredResponses", flags).GetValue(server);
                    pending[0].GetType().GetField("Deadline").SetValue(pending[0], DateTime.UtcNow.AddSeconds(-1));
                }
                if (mode == "disconnect") sender.Dispose();
                if (mode == "shutdown")
                {
                    typeof(McpRuntimeServer).GetField("_running", flags).SetValue(server, true);
                    typeof(McpRuntimeServer).GetMethod("Shutdown", flags).Invoke(server, null);
                }
                else typeof(McpRuntimeServer).GetMethod("TickDeferredResponses", flags).Invoke(server, null);
                Assert.That(_cleanup, Is.EqualTo(1));
                Assert.That(_pending.RequestContext.Cancellation.IsCancellationRequested, Is.EqualTo(cancelled));
                if (mode != "disconnect")
                {
                    sender.ReceiveTimeout = 3000;
                    string wire;
                    using (var reader = new StreamReader(sender.GetStream())) wire = reader.ReadToEnd();
                    Assert.That(wire, Does.Contain("Content-Type: text/event-stream"));
                    Assert.That(wire, Does.Not.Contain("MCP-Session-Id:"));
                    Assert.That(wire, Does.Contain(mode == "complete" ? "resultType" : "started_outcome_unknown"));
                }
                _pending.InvokeCleanup();
                Assert.That(_cleanup, Is.EqualTo(1));
                Assert.That(SpinWait.SpinUntil(() => pool.Count == 0, 1500), Is.True);
            }
            finally
            {
                pool.Shutdown("test cleanup");
                managerField.SetValue(null, originalManager);
                table.Remove(name); sender?.Dispose(); accepted?.Dispose(); listener.Stop();
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }
}
