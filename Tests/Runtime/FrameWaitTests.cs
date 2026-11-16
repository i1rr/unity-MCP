using System.Collections;
using I1rr.UnityMcp.Shared;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace I1rr.UnityMcp.Runtime.Tests
{
    public class FrameWaitTests
    {
        [UnityTest]
        public IEnumerator WaitUsesUnityFramesRatherThanPollCount()
        {
            var wait = DeferredResult.WaitForFrames(2, 5);
            int initial = Time.frameCount;
            for (int i = 0; i < 20; i++) Assert.That(wait.IsComplete(), Is.False);
            while (Time.frameCount - initial < 2) yield return null;
            Assert.That(wait.IsComplete(), Is.True);
            Assert.That((int)wait.GetResult()["frames_elapsed"], Is.EqualTo(Time.frameCount - initial));
        }
    }
}
