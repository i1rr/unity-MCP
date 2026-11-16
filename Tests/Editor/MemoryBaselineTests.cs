using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace I1rr.UnityMcp.Editor.Tests
{
    /// <summary>
    /// Integration tests for memory baseline tools: verifies that take_memory_baseline
    /// and compare_memory_baseline detect newly created objects with include_gc: false.
    /// </summary>
    public class MemoryBaselineTests
    {
        private readonly List<GameObject> _createdObjects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _createdObjects)
            {
                if (go != null)
                    Object.DestroyImmediate(go);
            }
            _createdObjects.Clear();
        }

        [Test]
        public void TakeMemoryBaseline_ReturnsSuccess()
        {
            var parameters = new JObject { ["include_gc"] = false };
            var result = MemoryProfilingTools.TakeMemoryBaseline(parameters);

            Assert.That(result["success"].Value<bool>(), Is.True);
            Assert.That(result["baseline_object_count"].Value<int>(), Is.GreaterThan(0),
                "Baseline should capture existing objects.");
            Assert.That(result["timestamp"], Is.Not.Null);
        }

        [Test]
        public void CompareMemoryBaseline_ReturnsError_WithoutBaseline()
        {
            // Clear any existing baseline via reflection
            ClearBaseline();

            var parameters = new JObject();
            var result = MemoryProfilingTools.CompareMemoryBaseline(parameters);

            Assert.That(result["error"], Is.Not.Null,
                "Should return error when no baseline has been taken.");
        }

        [Test]
        public void CompareMemoryBaseline_DetectsNewObjects_WithIncludeGcFalse()
        {
            // Take baseline
            var baselineParams = new JObject { ["include_gc"] = false };
            var baselineResult = MemoryProfilingTools.TakeMemoryBaseline(baselineParams);
            Assert.That(baselineResult["success"].Value<bool>(), Is.True);

            int baselineCount = baselineResult["baseline_object_count"].Value<int>();

            // Create new objects
            for (int i = 0; i < 5; i++)
            {
                var go = new GameObject($"MemoryTest_Object_{i}");
                _createdObjects.Add(go);
            }

            // Compare â€” should detect new objects
            var compareParams = new JObject { ["include_gc"] = false };
            var compareResult = MemoryProfilingTools.CompareMemoryBaseline(compareParams);

            Assert.That(compareResult["success"].Value<bool>(), Is.True);
            Assert.That(compareResult["new_objects_count"].Value<int>(), Is.GreaterThan(0),
                "Should detect newly created GameObjects.");
            Assert.That(compareResult["baseline_count"].Value<int>(), Is.EqualTo(baselineCount));
        }

        [Test]
        public void CompareMemoryBaseline_DetectsDestroyedObjects()
        {
            // Create some objects
            var tempObjects = new List<GameObject>();
            for (int i = 0; i < 3; i++)
                tempObjects.Add(new GameObject($"DestroyTest_{i}"));

            // Take baseline with those objects
            var baselineParams = new JObject { ["include_gc"] = false };
            MemoryProfilingTools.TakeMemoryBaseline(baselineParams);

            // Destroy the temp objects
            foreach (var go in tempObjects)
                Object.DestroyImmediate(go);

            // Compare â€” should detect destroyed objects
            var compareParams = new JObject { ["include_gc"] = false };
            var compareResult = MemoryProfilingTools.CompareMemoryBaseline(compareParams);

            Assert.That(compareResult["success"].Value<bool>(), Is.True);
            Assert.That(compareResult["destroyed_count"].Value<int>(), Is.GreaterThan(0),
                "Should detect destroyed objects.");
        }

        [Test]
        public void CompareMemoryBaseline_IncludesMemoryDelta()
        {
            var baselineParams = new JObject { ["include_gc"] = false };
            MemoryProfilingTools.TakeMemoryBaseline(baselineParams);

            // Create some objects to change memory
            for (int i = 0; i < 3; i++)
                _createdObjects.Add(new GameObject($"MemDelta_{i}"));

            var compareParams = new JObject { ["include_gc"] = false };
            var compareResult = MemoryProfilingTools.CompareMemoryBaseline(compareParams);

            Assert.That(compareResult["success"].Value<bool>(), Is.True);
            Assert.That(compareResult["memory_delta"], Is.Not.Null,
                "Compare result should include memory_delta.");
        }

        [Test]
        public void TakeMemoryBaseline_WithGC_ReportsGCFlag()
        {
            var parameters = new JObject { ["include_gc"] = true };
            var result = MemoryProfilingTools.TakeMemoryBaseline(parameters);

            Assert.That(result["success"].Value<bool>(), Is.True);
            Assert.That(result["include_gc"].Value<bool>(), Is.True);
        }

        private void ClearBaseline()
        {
            // Use reflection to clear the static baseline state
            var type = typeof(MemoryProfilingTools);
            var field = type.GetField("_baselineInstanceIds",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (field != null)
                field.SetValue(null, null);
        }
    }
}
