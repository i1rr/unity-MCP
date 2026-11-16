using System;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace I1rr.UnityMcp.Editor.Tests
{
    public class ClientConfigurationTests
    {
        private static JObject Merge(string json, bool requireAuth = true) => (JObject)typeof(McpEditorServer).Assembly
            .GetType("I1rr.UnityMcp.Editor.ServerLifecycleManager")
            .GetMethod("MergeClientConfiguration", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { json, 8123, requireAuth });

        [Test]
        public void Merge_OptionalAuthRemovesOldAuthorizationOnly()
        {
            var result = Merge(@"{'servers':{'i1rr-unity-mcp':{'headers':{'authorization':'Bearer old','X-Custom':'keep'}}}}", false);
            var headers = (JObject)result["servers"]["i1rr-unity-mcp"]["headers"];
            Assert.That(headers.Count, Is.EqualTo(1));
            Assert.That((string)headers["X-Custom"], Is.EqualTo("keep"));
            Assert.That((string)Merge(result.ToString())["servers"]["i1rr-unity-mcp"]["headers"]["Authorization"], Is.EqualTo("Bearer ${input:i1rr-unity-mcp-token}"));
        }

        [Test]
        public void Merge_PreservesOtherServersInputsAndCustomHeaders()
        {
            var original = JObject.Parse(@"{'inputs':[{'id':'secret'}],'servers':{'other':{'command':'custom'},'i1rr-unity-mcp':{'headers':{'X-Custom':'keep'},'custom':true}}}");
            var merged = Merge(original.ToString());
            Assert.That(JToken.DeepEquals(original["inputs"][0], merged["inputs"][0]), Is.True);
            Assert.That(merged["inputs"][1].Value<bool>("password"), Is.True);
            Assert.That(JToken.DeepEquals(merged, Merge(merged.ToString())), Is.True);
            Assert.That(JToken.DeepEquals(original["servers"]["other"], merged["servers"]["other"]), Is.True);
            Assert.That((string)merged["servers"]["i1rr-unity-mcp"]["headers"]["X-Custom"], Is.EqualTo("keep"));
            Assert.That((string)merged["servers"]["i1rr-unity-mcp"]["url"], Is.EqualTo("http://127.0.0.1:8123/mcp"));
        }

        [Test]
        public void Merge_DoesNotUseAConflictingInputOrPersistAnOldBearer()
        {
            var merged = Merge(@"{'inputs':[{'id':'i1rr-unity-mcp-token','type':'command'}],
                'servers':{'i1rr-unity-mcp':{'headers':{'authorization':'Bearer stale-secret'}}}}");
            Assert.That(merged.ToString(), Does.Not.Contain("stale-secret"));
            Assert.That((string)merged["servers"]["i1rr-unity-mcp"]["headers"]["Authorization"],
                Is.EqualTo("Bearer ${input:i1rr-unity-mcp-token-1}"));
        }

        [TestCase("{broken")]
        [TestCase("{'servers':[]}")]
        public void Merge_RefusesMalformedExistingConfiguration(string json)
        {
            Assert.Throws<TargetInvocationException>(() => Merge(json));
        }
    }
}
