using System;
using System.IO;
using System.Threading;
using I1rr.UnityMcp.Shared;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = NUnit.Framework.Is;

namespace I1rr.UnityMcp.Editor.Tests
{
    public class McpStreamPoolAllocationTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void FirstAndRepeatedTicksDoNotAllocate(bool liveStream)
        {
            var pool = new McpStreamPool();
            using var ready = new ManualResetEventSlim();
            int checks = 0;
            try
            {
                if (liveStream)
                {
                    var context = McpProtocol.CreateModernRequestContext(McpModernProtocolTests.Request());
                    Assert.That(pool.TryOpen(Stream.Null, () => ready.Set(), () => { },
                        () => { Interlocked.Increment(ref checks); return true; }, context), Is.Not.Null);
                    Assert.That(ready.Wait(2000), Is.True, "Production stream setup must finish before its first tick.");
                }
                TestDelegate first = pool.Tick;
                TestDelegate repeat = () => { for (int i = 0; i < 256; i++) pool.Tick(); };
                Assert.That((TestDelegate)(() => GC.KeepAlive(new byte[1024])),
                    new AllocatingGCMemoryConstraint(), "Calibrate the actual Unity allocation recorder.");
                Assert.That(first, Is.Not.AllocatingGCMemory(), "Do not hide first eligibility with a warm tick.");
                Assert.That(repeat, Is.Not.AllocatingGCMemory(), "Maintenance must not allocate collection snapshots per frame.");
                if (liveStream) Assert.That(Volatile.Read(ref checks), Is.GreaterThanOrEqualTo(257), "Healthy live streams must actually be checked.");
            }
            finally { pool.Shutdown("test cleanup"); }
        }

        [Test]
        public void TicksObserveNewMembershipAfterWriterRemovesItsSlot()
        {
            var pool = new McpStreamPool();
            int firstAlive = 1, firstChecks = 0, secondChecks = 0;
            var context = McpProtocol.CreateModernRequestContext(McpModernProtocolTests.Request());
            try
            {
                var first = pool.TryOpen(Stream.Null, () => { }, () => { },
                    () => { Interlocked.Increment(ref firstChecks); return Volatile.Read(ref firstAlive) != 0; }, context);
                Assert.That(first, Is.Not.Null);
                pool.Tick();
                Assert.That(Volatile.Read(ref firstChecks), Is.GreaterThan(0));
                Volatile.Write(ref firstAlive, 0); pool.Tick();
                Assert.That(SpinWait.SpinUntil(() => pool.Count == 0, 2000), Is.True);
                var second = pool.TryOpen(Stream.Null, () => { }, () => { },
                    () => { Interlocked.Increment(ref secondChecks); return true; },
                    McpProtocol.CreateModernRequestContext(McpModernProtocolTests.Request()));
                Assert.That(second, Is.Not.Null);
                pool.Tick();
                Assert.That(Volatile.Read(ref secondChecks), Is.GreaterThan(0), "Published membership must include the replacement stream.");
            }
            finally { pool.Shutdown("test cleanup"); }
        }

        [Test]
        public void LifetimeCallbackMembershipChangesApplyOnTheNextTick()
        {
            var pool = new McpStreamPool();
            int mainThread = Thread.CurrentThread.ManagedThreadId, addedChecks = 0;
            bool opened = false;
            McpSseStream added = null;
            try
            {
                var first = pool.TryOpen(Stream.Null, () => { }, () => { }, () =>
                {
                    if (Thread.CurrentThread.ManagedThreadId == mainThread && !opened)
                    {
                        opened = true;
                        added = pool.TryOpen(Stream.Null, () => { }, () => { }, () =>
                        {
                            if (Thread.CurrentThread.ManagedThreadId == mainThread) addedChecks++;
                            return true;
                        }, McpProtocol.CreateModernRequestContext(McpModernProtocolTests.Request()));
                    }
                    return true;
                }, McpProtocol.CreateModernRequestContext(McpModernProtocolTests.Request()));
                Assert.That(first, Is.Not.Null);
                pool.Tick();
                Assert.That(added, Is.Not.Null);
                Assert.That(addedChecks, Is.Zero, "The current tick retains its original membership view.");
                pool.Tick();
                Assert.That(addedChecks, Is.EqualTo(1), "The next tick sees callback-created membership.");
            }
            finally { pool.Shutdown("test cleanup"); }
        }
    }
}
