using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = NUnit.Framework.Is;

namespace I1rr.UnityMcp.Editor.Tests
{
    public class ProfilerRecorderMaintenanceTests
    {
        [Test]
        public void IdleEditorMaintenance_DoesNotAllocate()
        {
            var type = typeof(ProfilerRecorderManager);
            var instance = type.GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            Assert.That(instance, Is.Not.Null);
            var tick = (Action)type.GetMethod("Tick", BindingFlags.NonPublic | BindingFlags.Instance)
                .CreateDelegate(typeof(Action), instance);
            for (int i = 0; i < 64; i++) tick();
            Assert.That((TestDelegate)(() => GC.KeepAlive(new byte[1024])),
                new AllocatingGCMemoryConstraint(), "Calibrate Unity's allocation recorder first.");
            Assert.That((TestDelegate)(() => { for (int i = 0; i < 256; i++) tick(); }),
                Is.Not.AllocatingGCMemory(), "Idle recorder maintenance must not add per-frame garbage.");
        }
    }
}
