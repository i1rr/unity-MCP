using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = NUnit.Framework.Is;
using UnityEngine;

namespace I1rr.UnityMcp.Runtime.Tests
{
    public class ProfilerRecorderMaintenanceTests
    {
        [Test]
        public void IdleRuntimeMaintenance_DoesNotAllocate()
        {
            var previous = Shared.ProfilerRecorderService.Provider;
            GameObject owned = null;
            var manager = ProfilerRecorderManager.Instance;
            if (manager == null)
            {
                owned = new GameObject("Profiler maintenance regression");
                manager = owned.AddComponent<ProfilerRecorderManager>();
            }
            try
            {
                var update = (Action)typeof(ProfilerRecorderManager)
                    .GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance)
                    .CreateDelegate(typeof(Action), manager);
                for (int i = 0; i < 64; i++) update();
                Assert.That((TestDelegate)(() => GC.KeepAlive(new byte[1024])),
                    new AllocatingGCMemoryConstraint(), "Calibrate Unity's allocation recorder first.");
                Assert.That((TestDelegate)(() => { for (int i = 0; i < 256; i++) update(); }),
                    Is.Not.AllocatingGCMemory(), "Idle recorder maintenance must not add per-frame garbage.");
            }
            finally
            {
                if (owned != null) UnityEngine.Object.DestroyImmediate(owned);
                Shared.ProfilerRecorderService.Provider = previous;
            }
        }
    }
}
