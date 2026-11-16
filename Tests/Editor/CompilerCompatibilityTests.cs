using System;
using System.IO;
using System.Linq;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace I1rr.UnityMcp.Editor.Tests
{
    public class CompilerCompatibilityTests
    {
        [Test]
        public void LoadedAssembliesAndPathsReachCurrentPackageCode()
        {
            var assembly = typeof(McpToolRegistry).Assembly;
            Assert.That(UnityCompatibility.GetLoadedAssemblies(), Does.Contain(assembly));
            Assert.That(File.Exists(UnityCompatibility.GetLoadedAssemblyPath(assembly)), Is.True);
        }

        [Test]
        public void UnorderedQueriesRetainInactiveSelection()
        {
            var active = new GameObject("CompatibilityActive");
            var inactive = new GameObject("CompatibilityInactive");
            try
            {
                var activeCollider = active.AddComponent<BoxCollider>();
                var inactiveCollider = inactive.AddComponent<BoxCollider>();
                inactive.SetActive(false);
                Assert.That(UnityCompatibility.FindObjectsByType<BoxCollider>(), Does.Contain(activeCollider));
                Assert.That(UnityCompatibility.FindObjectsByType<BoxCollider>(), Has.No.Member(inactiveCollider));
                Assert.That(UnityCompatibility.FindObjectsByType<BoxCollider>(FindObjectsInactive.Include), Does.Contain(inactiveCollider));
            }
            finally { Object.DestroyImmediate(active); Object.DestroyImmediate(inactive); }
        }

        [Test]
        public void PlayerSettingsReportRetainsActiveTargetAndDefaultIconMeaning()
        {
            var target = NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget));
            var result = BuildTools.GetPlayerSettings(new JObject());
            Assert.That((string)result["scripting_backend"], Is.EqualTo(PlayerSettings.GetScriptingBackend(target).ToString()));
            Assert.That((string)result["api_compatibility_level"], Is.EqualTo(PlayerSettings.GetApiCompatibilityLevel(target).ToString()));
            Assert.That((bool)result["default_icon"], Is.EqualTo(PlayerSettings.GetIcons(NamedBuildTarget.Unknown, IconKind.Any).Length > 0));
        }

        [Test]
        public void PhysicsReportReadsBothLegacySyncConfigurationValues()
        {
            var manager = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/DynamicsManager.asset").First();
            using (var serialized = new SerializedObject(manager))
            {
                var sync = serialized.FindProperty("m_AutoSyncTransforms");
                Assert.That(sync, Is.Not.Null);
                bool original = sync.boolValue;
                try
                {
                    foreach (bool enabled in new[] { false, true })
                    {
                        sync.boolValue = enabled;
                        serialized.ApplyModifiedPropertiesWithoutUndo();
                        var report = BuildTools.GetProjectSettings(new JObject { ["category"] = "physics" });
                        Assert.That((bool)report["auto_sync_transforms"], Is.EqualTo(enabled));
                    }
                }
                finally
                {
                    sync.boolValue = original;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
            }
        }

        [Test]
        public void CompilationSnapshotIncludesAnalyzerWarnings()
        {
            string warning = "CompilerCompatibility-" + Guid.NewGuid().ToString("N") + ": warning UAC0005: analyzer evidence";
            LogAssert.Expect(LogType.Warning, warning);
            Debug.LogWarning(warning);
            var diagnostics = CompilationTools.GetCompilationErrors(new JObject());
            Assert.That(((JArray)diagnostics["messages"]).Any(message => ((string)message["message"]).Contains(warning)), Is.True,
                "Analyzer warnings must not disappear from a compiler snapshot that recognizes CS warnings.");
        }
    }
}
