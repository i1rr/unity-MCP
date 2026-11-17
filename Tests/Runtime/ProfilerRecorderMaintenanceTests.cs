using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = NUnit.Framework.Is;
using UnityEngine;

namespace I1rr.UnityMcp.Runtime.Tests
{
    public class ProfilerRecorderMaintenanceTests
    {
        [Test]
        public void ConcurrentAdmissionsRetainThePerClientCeiling()
        {
            var owned = new GameObject("Concurrent rate limit regression"); owned.SetActive(false);
            try
            {
                var server = owned.AddComponent<McpRuntimeServer>();
                var admit = (Func<string, bool>)typeof(McpRuntimeServer).GetMethod("CheckRateLimit", BindingFlags.NonPublic | BindingFlags.Instance)
                    .CreateDelegate(typeof(Func<string, bool>), server);
                int accepted = 0;
                Parallel.For(0, 240, _ => { if (admit("127.0.0.1")) Interlocked.Increment(ref accepted); });
                Assert.That(accepted, Is.EqualTo(120));
                Assert.That(admit("127.0.0.2"), Is.True, "Another client's quota remains independent.");
            }
            finally { UnityEngine.Object.DestroyImmediate(owned); }
        }

        [Test]
        public void MixedExpiryPreservesLiveAndReadmittedWindows()
        {
            var owned = new GameObject("Mixed rate expiry regression"); owned.SetActive(false);
            try
            {
                var server = owned.AddComponent<McpRuntimeServer>(); var type = typeof(McpRuntimeServer);
                var admit = (Func<string, bool>)type.GetMethod("CheckRateLimit", BindingFlags.NonPublic | BindingFlags.Instance)
                    .CreateDelegate(typeof(Func<string, bool>), server);
                var heartbeat = (Action)type.GetMethod("TickHeartbeat", BindingFlags.NonPublic | BindingFlags.Instance)
                    .CreateDelegate(typeof(Action), server);
                var last = type.GetField("_lastHeartbeatTime", BindingFlags.NonPublic | BindingFlags.Instance);
                var entries = (System.Collections.IDictionary)type.GetField("_ipRateLimits", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(server);
                foreach (string ip in new[] { "127.0.0.1", "127.0.0.2", "127.0.0.3" }) Assert.That(admit(ip), Is.True);
                foreach (string ip in new[] { "127.0.0.1", "127.0.0.3" }) {
                    var entry = entries[ip]; entry.GetType().GetField("WindowStartTicks").SetValue(entry, DateTime.UtcNow.Ticks - TimeSpan.FromMinutes(6).Ticks);
                }
                last.SetValue(server, float.MinValue); heartbeat();
                Assert.That(entries.Count, Is.EqualTo(1)); Assert.That(entries.Contains("127.0.0.2"), Is.True);
                Assert.That(admit("127.0.0.1"), Is.True);
                var remaining = entries["127.0.0.2"];
                remaining.GetType().GetField("WindowStartTicks").SetValue(remaining, DateTime.UtcNow.Ticks - TimeSpan.FromMinutes(6).Ticks);
                last.SetValue(server, float.MinValue); heartbeat();
                Assert.That(entries.Count, Is.EqualTo(1)); Assert.That(entries.Contains("127.0.0.1"), Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(owned); }
        }

        [Test]
        public void HeartbeatRetainsRateLimitAdmissionAndRemovesExpiredEntries()
        {
            var owned = new GameObject("Rate limit maintenance regression"); owned.SetActive(false);
            try
            {
                var server = owned.AddComponent<McpRuntimeServer>();
                var type = typeof(McpRuntimeServer);
                var admit = (Func<string, bool>)type.GetMethod("CheckRateLimit", BindingFlags.NonPublic | BindingFlags.Instance)
                    .CreateDelegate(typeof(Func<string, bool>), server);
                var last = type.GetField("_lastHeartbeatTime", BindingFlags.NonPublic | BindingFlags.Instance);
                var heartbeat = (TestDelegate)type.GetMethod("TickHeartbeat", BindingFlags.NonPublic | BindingFlags.Instance)
                    .CreateDelegate(typeof(TestDelegate), server);
                float startup = Time.realtimeSinceStartup;
                last.SetValue(server, startup); heartbeat();
                Assert.That((float)last.GetValue(server), Is.EqualTo(startup));
                for (int i = 0; i < 120; i++) Assert.That(admit("127.0.0.1"), Is.True);
                Assert.That(admit("127.0.0.1"), Is.False, "Maintenance changes must preserve the per-IP request ceiling.");
                var entries = (System.Collections.IDictionary)type.GetField("_ipRateLimits", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(server);
                Assert.That(entries.Count, Is.EqualTo(1));
                last.SetValue(server, float.MinValue);
                Assert.That(heartbeat, Is.Not.AllocatingGCMemory(), "A tracked healthy client must not cause periodic snapshot garbage.");
                var entry = entries["127.0.0.1"];
                entry.GetType().GetField("WindowStartTicks").SetValue(entry, DateTime.UtcNow.Ticks - TimeSpan.FromMinutes(6).Ticks);
                last.SetValue(server, float.MinValue);
                Assert.That(heartbeat, Is.Not.AllocatingGCMemory(), "Request setup must reserve cleanup capacity before the first expiry.");
                Assert.That(entries.Count, Is.Zero, "Expired client windows must still be retired.");
                last.SetValue(server, float.MinValue);
                Assert.That(heartbeat, Is.Not.AllocatingGCMemory(), "An emptied store must return to allocation-free idle maintenance.");
                Assert.That(admit("127.0.0.1"), Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(owned); }
        }

        [Test]
        public void IdleHeartbeatDoesNotAllocateOnFirstEligibility()
        {
            var owned = new GameObject("Heartbeat maintenance regression");
            owned.SetActive(false); // Isolate maintenance without opening a listener.
            try
            {
                var server = owned.AddComponent<McpRuntimeServer>();
                var lastHeartbeat = typeof(McpRuntimeServer).GetField("_lastHeartbeatTime", BindingFlags.NonPublic | BindingFlags.Instance);
                var heartbeat = (TestDelegate)typeof(McpRuntimeServer)
                    .GetMethod("TickHeartbeat", BindingFlags.NonPublic | BindingFlags.Instance)
                    .CreateDelegate(typeof(TestDelegate), server);
                // Production Update calls this timer from startup, before its
                // first due cycle. Keep Mono's cold method/JIT setup separate;
                // never warm the actual eligible cleanup being measured.
                float startup = Time.realtimeSinceStartup;
                lastHeartbeat.SetValue(server, startup); heartbeat();
                Assert.That((float)lastHeartbeat.GetValue(server), Is.EqualTo(startup), "Setup must not execute an eligible heartbeat.");
                lastHeartbeat.SetValue(server, float.MinValue);
                Assert.That((TestDelegate)(() => GC.KeepAlive(new byte[1024])),
                    new AllocatingGCMemoryConstraint(), "Calibrate the actual Unity allocation recorder.");
                Assert.That(heartbeat, Is.Not.AllocatingGCMemory(), "Exercise the first due heartbeat, not its timer's ineligible branch.");
            }
            finally { UnityEngine.Object.DestroyImmediate(owned); }
        }

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
