using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEditor.PackageManager;

namespace I1rr.UnityMcp.Editor.Tests
{
    public class NativeKeeperMigrationTests
    {
        private static readonly Type Module = typeof(McpEditorServer).Assembly
            .GetType("I1rr.UnityMcp.Editor.NativeKeeperModule");
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
        private string[] paths;
        private object[] modules;

        [OneTimeSetUp]
        public void LoadSeparatePhysicalProviders()
        {
            if (UnityEngine.Application.platform != UnityEngine.RuntimePlatform.WindowsEditor)
                Assert.Ignore("Windows keeper migration; no non-Windows native claim.");
            string source = Path.Combine(PackageInfo.FindForAssembly(typeof(McpEditorServer).Assembly).resolvedPath,
                "Editor/Plugins/Windows/x86_64/mcpkeeper.dll");
            string root = Path.Combine(Path.GetTempPath(), "i1rr-keeper-migration-" + Guid.NewGuid().ToString("N"));
            paths = Enumerable.Range(0, 3).Select(i => Path.Combine(root, i.ToString(),
                "Editor/Plugins/Windows/x86_64/mcpkeeper.dll")).ToArray();
            foreach (string path in paths)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.Copy(source, path);
            }
            modules = paths.Select(Open).ToArray();
            // Loaded native modules intentionally remain resident until process exit, like Unity plugins.
        }

        [TearDown]
        public void StopOnlyOwnedProviders()
        {
            if (modules == null) return;
            foreach (object module in modules) Call(module, "Stop");
        }

        private static object Open(string path) => Module.GetMethod("Open", Members).Invoke(null, new object[] { path });
        private static object Call(object module, string member, params object[] args) =>
            ((Delegate)Module.GetField(member, Members).GetValue(module)).DynamicInvoke(args);
        private static int Query(object module, string member) => (int)Call(module, member);
        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
        private static void Start(object module, int port)
        {
            Assert.That((int)Call(module, "Start", port), Is.Zero);
            Call(module, "Attach");
        }
        private static int Retire(object module, string[] previousPaths, int port) =>
            (int)Module.GetMethod("RetirePreviousProviders", Members).Invoke(module, new object[] { previousPaths, port });
        private static TcpClient Connect(int port)
        {
            var client = new TcpClient { ReceiveTimeout = 2000, SendTimeout = 2000 };
            client.Connect(IPAddress.Loopback, port);
            return client;
        }

        [Test]
        public void PathChange_RetiresOldProviderAndReusesItsPort()
        {
            int port = FreePort();
            Start(modules[0], port);
            Assert.That(Retire(modules[1], paths.Take(2).ToArray(), port), Is.EqualTo(port));
            Assert.That(Query(modules[0], "IsRunning"), Is.Zero);
            Start(modules[1], port);
            using (var client = Connect(port))
            {
                int handle = (int)Call(modules[1], "Dequeue", 1000);
                Assert.That(handle, Is.GreaterThanOrEqualTo(0));
                byte[] input = Encoding.ASCII.GetBytes("new provider");
                client.GetStream().Write(input, 0, input.Length);
                byte[] received = new byte[input.Length];
                Assert.That((int)Call(modules[1], "Recv", handle, received, received.Length), Is.EqualTo(input.Length));
                Assert.That(received, Is.EqualTo(input));
                Call(modules[1], "CloseClient", handle);
            }
        }

        [Test]
        public void SamePathReload_KeepsListenerAndClientHandle()
        {
            int port = FreePort();
            Start(modules[0], port);
            using (var client = Connect(port))
            {
                int handle = (int)Call(modules[0], "Dequeue", 1000);
                Assert.That(handle, Is.GreaterThanOrEqualTo(0));
                Call(modules[0], "Detach");
                object rebound = Open(paths[0]);
                Assert.That(Retire(rebound, new[] { paths[0], paths[0] }, FreePort()), Is.EqualTo(port));
                Assert.That((int)Call(rebound, "IsClientConnected", handle), Is.EqualTo(1));
                Call(rebound, "Attach");
                byte[] reply = Encoding.ASCII.GetBytes("same provider");
                Assert.That((int)Call(rebound, "Send", handle, reply, reply.Length), Is.EqualTo(reply.Length));
                var received = new byte[reply.Length];
                Assert.That(client.GetStream().Read(received, 0, received.Length), Is.EqualTo(reply.Length));
                Assert.That(received, Is.EqualTo(reply));
                Call(rebound, "CloseClient", handle);
            }
        }

        [Test]
        public void MultipleOrphans_RetiresAllAndPrefersRecordedOwnedPort()
        {
            int first = FreePort(), recorded = FreePort();
            Start(modules[0], first);
            Start(modules[1], recorded);
            Assert.That(Retire(modules[2], paths, recorded), Is.EqualTo(recorded));
            Assert.That(Query(modules[0], "IsRunning"), Is.Zero);
            Assert.That(Query(modules[1], "IsRunning"), Is.Zero);
            Start(modules[2], recorded);
            Assert.That(Query(modules[2], "GetPort"), Is.EqualTo(recorded));
        }

        [Test]
        public void PathChange_FlushesStagedOutcomeBeforeClosingOldClient()
        {
            int port = FreePort();
            Start(modules[0], port);
            using (var client = Connect(port))
            {
                int handle = (int)Call(modules[0], "Dequeue", 1000);
                Assert.That(handle, Is.GreaterThanOrEqualTo(0));
                byte[] reply = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}");
                Call(modules[0], "StageResponse", handle, reply, reply.Length);
                Assert.That(Retire(modules[1], paths, port), Is.EqualTo(port));
                using (var received = new MemoryStream())
                {
                    client.GetStream().CopyTo(received);
                    Assert.That(received.ToArray(), Is.EqualTo(reply));
                }
                Assert.That(Query(modules[0], "IsRunning"), Is.Zero);
            }
        }

        [Test]
        public void InvalidProvider_AbortsBeforeRetiringAnyRunningProvider()
        {
            int port = FreePort();
            Start(modules[0], port);
            var exception = Assert.Throws<TargetInvocationException>(() => Retire(modules[1],
                new[] { paths[0], Path.Combine(Environment.SystemDirectory, "kernel32.dll") }, port));
            Assert.That(exception.InnerException, Is.TypeOf<EntryPointNotFoundException>());
            Assert.That(Query(modules[0], "IsRunning"), Is.EqualTo(1));
            Assert.That(Query(modules[1], "IsRunning"), Is.Zero);
        }

        [Test]
        public void CurrentAlreadyRunning_RetiresOrphansWithoutMovingItsEndpoint()
        {
            int oldPort = FreePort(), currentPort = FreePort();
            Start(modules[0], oldPort);
            Start(modules[1], currentPort);
            Assert.That(Retire(modules[1], paths, oldPort), Is.EqualTo(currentPort));
            Assert.That(Query(modules[0], "IsRunning"), Is.Zero);
            Assert.That(Query(modules[1], "IsRunning"), Is.EqualTo(1));
            using (var client = Connect(currentPort))
                Assert.That((int)Call(modules[1], "Dequeue", 1000), Is.GreaterThanOrEqualTo(0));
        }
    }
}
