using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using System.Linq;
#if UNITY_6000_6_OR_NEWER
using ObjectId = System.UInt64;
#else
using ObjectId = System.Int32;
#endif

namespace I1rr.UnityMcp.Editor.Tests
{
    public class ObjectIdentityTests
    {
        [Test]
        public void LiveObjectsRoundTripThroughToolParametersAndSelection()
        {
            var first = new GameObject("IdentityFirst");
            var second = new GameObject("IdentitySecond");
            var previous = Selection.objects;
            try
            {
                var id = first.GetMcpId();
                Assert.That(id, Is.Not.EqualTo(second.GetMcpId()));
                var parameters = JObject.Parse(new JObject { ["instanceId"] = id }.ToString());
                Assert.That(ObjectIdentity.Resolve(parameters["instanceId"].ToObject<ObjectId>()), Is.SameAs(first));
                var components = ComponentReadTools.GetComponents(parameters);
                Assert.That(components["name"].Value<string>(), Is.EqualTo(first.name));
                EditorControlTools.SetSelection(new JObject { ["instanceIds"] = new JArray(id, second.GetMcpId()) });
                Assert.That(Selection.objects.Contains(first), Is.True);
                Assert.That(Selection.objects.Contains(second), Is.True);
                Object.DestroyImmediate(first);
                Assert.That(ObjectIdentity.Resolve(id), Is.Null);
            }
            finally
            {
                Selection.objects = previous;
                if (first != null) Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
            }
        }

#if UNITY_6000_6_OR_NEWER
        [TestCase(0x100000001UL)]
        [TestCase(0xFEDCBA9876543210UL)]
        public void JsonParametersRetainAllEntityIdBits(ulong raw)
        {
            var parameters = JObject.Parse(new JObject { ["instanceId"] = raw }.ToString());
            var id = EntityId.FromULong(parameters["instanceId"].ToObject<ObjectId>());
            Assert.That(EntityId.ToULong(id), Is.EqualTo(raw));
            Assert.That(ToolHelper.RequireParam<ulong>(parameters, "instanceId", out var error), Is.EqualTo(raw));
            Assert.That(error, Is.Null);
            Assert.That(ToolHelper.OptionalParam<ulong>(parameters, "instanceId"), Is.EqualTo(raw));
        }
#endif
    }
}
