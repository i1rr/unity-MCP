using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace I1rr.UnityMcp.Editor.Tests
{
    public class ManagedStreamingTests
    {
        [Test] public void HttpListenerDisconnectCancelsThroughHeartbeatWrite()
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            using var listener = new HttpListener();
            listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
            listener.Start();
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            var bytes = Encoding.ASCII.GetBytes("POST /mcp HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nContent-Length: 0\r\n\r\n");
            client.GetStream().Write(bytes, 0, bytes.Length);
            var context = listener.GetContext();
            var captured = McpProtocol.CreateModernRequestContext(McpModernProtocolTests.Request(McpSubscriptions.Method));
            var stream = EditorStreamingResponses.TryOpen(new McpEditorServer.McpRequest { ClientId = -1, Context = context },
                new Dictionary<string, string>(), captured);
            try
            {
                stream.Send(McpSubscriptions.Acknowledgment(captured));
                client.Client.LingerState = new LingerOption(true, 0);
                client.Dispose();
                Assert.That(SpinWait.SpinUntil(() => stream.Cancellation.IsCancellationRequested, 3500), Is.True);
                Assert.That(SpinWait.SpinUntil(() => stream.IsStopped, 1000), Is.True);
            }
            finally { stream?.Abort(); listener.Stop(); }
        }

        [Test] public void HttpListenerFallbackDeliversAckAndFinalChunkedResponse()
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            using var listener = new HttpListener();
            listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
            listener.Start();
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            client.ReceiveTimeout = 3000;
            var bytes = Encoding.ASCII.GetBytes("POST /mcp HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            client.GetStream().Write(bytes, 0, bytes.Length);
            var context = listener.GetContext();
            var request = new McpEditorServer.McpRequest { ClientId = -1, Context = context };
            var captured = McpProtocol.CreateModernRequestContext(McpModernProtocolTests.Request(McpSubscriptions.Method));
            var stream = EditorStreamingResponses.TryOpen(request, new Dictionary<string, string>(), captured);
            try
            {
                Assert.That(stream, Is.Not.Null);
                Assert.That(stream.Send(McpSubscriptions.Acknowledgment(captured)), Is.True);
                stream.Complete(McpProtocol.BuildResponse(1, new JObject()));
                string wire;
                using (var reader = new StreamReader(client.GetStream())) wire = reader.ReadToEnd();
                Assert.That(wire, Does.Contain("text/event-stream"));
                Assert.That(wire.ToLowerInvariant(), Does.Contain("transfer-encoding: chunked"));
                Assert.That(wire, Does.Contain("notifications/subscriptions/acknowledged"));
                Assert.That(wire, Does.Contain("resultType"));
                Assert.That(wire, Does.EndWith("0\r\n\r\n"), "Managed response must terminate its chunk framing");
                Assert.That(SpinWait.SpinUntil(() => stream.IsStopped, 1000), Is.True);
            }
            finally { stream?.Abort(); listener.Stop(); }
        }
    }
}
