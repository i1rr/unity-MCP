using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace I1rr.UnityMcp.Runtime.Tests
{
    public class ModernHttpPipelineTests
    {
        [Test] public void DiscoveryAnnouncementKeepsLegacyDefaultAndListsModernVersion()
        {
            var go = new GameObject("Discovery announcement test");
            try
            {
                var discovery = go.AddComponent<UdpDiscovery>();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(UdpDiscovery).GetField("_serverPort", flags).SetValue(discovery, 8123);
                typeof(UdpDiscovery).GetMethod("BuildPayload", flags).Invoke(discovery, null);
                var bytes = (byte[])typeof(UdpDiscovery).GetField("_payload", flags).GetValue(discovery);
                var payload = JObject.Parse(Encoding.UTF8.GetString(bytes));
                Assert.That((string)payload["protocol"], Is.EqualTo(McpProtocol.ProtocolVersionBaseline));
                Assert.That(((JArray)payload["protocols"]).Values<string>(), Does.Contain(McpProtocol.ProtocolVersionModern));
                Assert.That(payload["token"], Is.Null);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [TestCase(null, "test", "server/discover", 200)]
        [TestCase(null, "test", "tools/list", 400)]
        [TestCase("https://attacker.invalid", "test", "server/discover", 403)]
        [TestCase(null, "wrong", "server/discover", 401)]
        public void RuntimeRoutesModernRequestsWithoutLegacySession(string origin, string token, string methodHeader, int expected)
        {
            var go = new GameObject("Modern HTTP pipeline test");
            var server = go.AddComponent<McpRuntimeServer>();
            var listener = new TcpListener(IPAddress.Loopback, 0);
            TcpClient sender = null, accepted = null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            try
            {
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                typeof(McpRuntimeServer).GetField("_port", flags).SetValue(server, port);
                typeof(McpRuntimeServer).GetField("_authToken", flags).SetValue(server, "test");
                sender = new TcpClient();
                sender.Connect(IPAddress.Loopback, port);
                accepted = listener.AcceptTcpClient();
                var request = new JObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "server/discover",
                    ["params"] = new JObject { ["_meta"] = new JObject
                    {
                        [McpProtocol.VersionMetaKey] = McpProtocol.ProtocolVersionModern,
                        [McpProtocol.CapabilitiesMetaKey] = new JObject()
                    } }
                };
                int sessionsBefore = McpProtocol.SessionCount;
                var headers = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
                {
                    ["Host"] = "127.0.0.1:" + port, ["Origin"] = origin,
                    ["Authorization"] = "Bearer " + token, ["Content-Type"] = "application/json",
                    ["MCP-Protocol-Version"] = McpProtocol.ProtocolVersionModern,
                    ["Mcp-Method"] = methodHeader, ["MCP-Session-Id"] = "stale-session",
                    ["Accept"] = "application/json, text/event-stream"
                };
                typeof(McpRuntimeServer).GetMethod("HandlePost", flags).Invoke(server, new object[]
                {
                    accepted, new TcpHttpHandler.HttpRequest
                    { Method = "POST", Path = "/mcp", Headers = headers, Body = Encoding.UTF8.GetBytes(request.ToString(Formatting.None)) }
                });
                sender.ReceiveTimeout = 3000;
                string wire;
                using (var reader = new StreamReader(sender.GetStream(), Encoding.UTF8)) wire = reader.ReadToEnd();
                Assert.That(wire, Does.StartWith("HTTP/1.1 " + expected));
                Assert.That(wire, Does.Not.Contain("MCP-Session-Id:"));
                Assert.That(McpProtocol.SessionCount, Is.EqualTo(sessionsBefore));
                if (expected == 200)
                    Assert.That((string)JObject.Parse(wire.Substring(wire.IndexOf("\r\n\r\n") + 4))["result"]["resultType"], Is.EqualTo("complete"));
            }
            finally
            {
                accepted?.Dispose();
                sender?.Dispose();
                listener.Stop();
                Object.DestroyImmediate(go);
            }
        }
    }
}
