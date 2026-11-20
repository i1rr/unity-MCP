using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using I1rr.UnityMcp.Shared;
using NUnit.Framework;

namespace I1rr.UnityMcp.Editor.Tests
{
    public class LocalAuthPipelineTests
    {
        [TestCase("POST", false, null, 415)]
        [TestCase("POST", true, null, 401)]
        [TestCase("POST", true, "Bearer test", 415)]
        [TestCase("DELETE", false, null, 405)]
        [TestCase("DELETE", true, null, 405)]
        public void Pipeline_EnforcesAuthMode(string method, bool required, string auth, int expected)
            => AssertStatus(method, required, auth, "127.0.0.1:8123", null, expected);

        [TestCase("GET")]
        [TestCase("OPTIONS")]
        [TestCase("POST")]
        [TestCase("DELETE")]
        public void Pipeline_RejectsForeignHostAndOriginWithoutAuth(string method)
        {
            AssertStatus(method, false, null, "attacker.invalid:8123", null, 403);
            AssertStatus(method, false, null, "127.0.0.1:8123", "https://attacker.invalid", 403);
        }

        private static void AssertStatus(string method, bool required, string auth, string host, string origin, int expected)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            var server = typeof(McpEditorServer);
            var fields = new[] { "_port", "_authToken", "_requireAuthentication", "_outboundQueue" };
            var saved = new object[fields.Length];
            for (int i = 0; i < fields.Length; i++) saved[i] = server.GetField(fields[i], flags).GetValue(null);
            var queue = Activator.CreateInstance(server.GetField("_outboundQueue", flags).FieldType);
            try
            {
                server.GetField("_port", flags).SetValue(null, 8123);
                server.GetField("_authToken", flags).SetValue(null, "test");
                server.GetField("_requireAuthentication", flags).SetValue(null, required);
                server.GetField("_outboundQueue", flags).SetValue(null, queue);
                var type = server.GetNestedType("McpRequest", BindingFlags.NonPublic);
                var request = Activator.CreateInstance(type);
                type.GetField("ClientId").SetValue(request, -1);
                type.GetField("HttpRequest").SetValue(request, new TcpHttpHandler.HttpRequest
                {
                    Method = method, Path = method == "GET" ? "/health" : "/mcp",
                    Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    { ["Host"] = host, ["Origin"] = origin, ["Authorization"] = auth }
                });
                server.Assembly.GetType("I1rr.UnityMcp.Editor.EditorRequestPipeline")
                    .GetMethod("ProcessRequest", flags).Invoke(null, new[] { request });
                var iterator = ((IEnumerable)queue).GetEnumerator();
                Assert.That(iterator.MoveNext(), Is.True);
                Assert.That((int)iterator.Current.GetType().GetField("StatusCode").GetValue(iterator.Current), Is.EqualTo(expected));
            }
            finally
            {
                for (int i = 0; i < fields.Length; i++) server.GetField(fields[i], flags).SetValue(null, saved[i]);
                ((IDisposable)queue).Dispose();
            }
        }
    }
}
