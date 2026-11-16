using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace I1rr.UnityMcp.Editor.Tests
{
    public class ReloadResponseTests
    {
        private static object Call(string method, params object[] args) => typeof(McpEditorServer).Assembly
            .GetType("I1rr.UnityMcp.Editor.EditorResponseWriter")
            .GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);

        [Test] public void QueuedRequest503ExplicitlySaysItDidNotStart()
        {
            var response = Encoding.UTF8.GetString((byte[])Call("BuildReload503HttpResponse"));
            Assert.That(response, Does.StartWith("HTTP/1.1 503"));
            Assert.That(response, Does.Contain("Retry-After: 3"));
            var body = JObject.Parse(response.Substring(response.IndexOf("\r\n\r\n") + 4));
            Assert.That((string)body["dispatch_state"], Is.EqualTo("not_started"));
        }

        [Test] public void InterruptedDeferredRequestNeverRecommendsBlindReplay()
        {
            var result = (JObject)Call("BuildInterruptedReloadResponse", new JValue(42));
            Assert.That((int)result["id"], Is.EqualTo(42));
            Assert.That((string)result["error"]["data"]["dispatch_state"], Is.EqualTo("started_outcome_unknown"));
            Assert.That((string)result["error"]["message"], Does.Contain("Inspect its state before retrying"));
        }
    }
}
