using System;
using System.IO;
using I1rr.UnityMcp.Editor;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace I1rr.UnityMcp.Editor.Tests
{
    public class ImportReadinessTests
    {
        [Test]
        public void EditorState_IdentifiesTheLoadedProjectPackageAndDomain()
        {
            var state = EditorControlTools.GetEditorState(new JObject());
            Assert.That(state.Value<string>("projectPath"), Is.EqualTo(Path.GetDirectoryName(Application.dataPath)));
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(state.Value<string>("packagePath"), "package.json")));
            Assert.That(manifest.Value<string>("name"), Is.EqualTo("i1rr-unity-mcp"));
            Assert.That(state.Value<string>("packageVersion"), Is.EqualTo(manifest.Value<string>("version")));
            Assert.That(state.Value<string>("mcpAssemblyId"), Is.EqualTo(typeof(EditorControlTools).Assembly.ManifestModule.ModuleVersionId.ToString()));
            Assert.That(Guid.TryParse(state.Value<string>("domainId"), out _), Is.True);
            Assert.That(EditorControlTools.GetEditorState(new JObject()).Value<string>("domainId"), Is.EqualTo(state.Value<string>("domainId")));
            Assert.That(state["isUpdating"].Type, Is.EqualTo(JTokenType.Boolean));
        }

        [Test]
        public void Refresh_DoesNotClaimThatCompilationHasCompleted()
        {
            var result = EditorControlTools.RefreshAssets(new JObject());
            Assert.That(result.Value<string>("status"), Is.EqualTo("refresh_requested"));
            Assert.That(result["compilation_complete"]?.Value<bool>(), Is.False);
        }

        [Test]
        public void RefreshDiagnostics_ReturnsASnapshotWithoutMainThreadSettling()
        {
            var result = CompilationTools.RefreshAndGetCompilationErrors(new JObject { ["settleMs"] = 5000 });
            Assert.That(result.Value<string>("status"), Is.EqualTo("refresh_requested"));
            Assert.That(result["compilation_complete"]?.Value<bool>(), Is.False);
            Assert.That(result.Value<int>("settle_ms"), Is.Zero);
            Assert.That(result["snapshot_only"]?.Value<bool>(), Is.True);
            Assert.That(result["editor_state"]?["domainId"], Is.Not.Null);
        }

        [Test]
        public void Reimport_ExposesChangedImportedDataAndItsDependencyHash()
        {
            var path = "Assets/UnityMcpImport_" + Guid.NewGuid().ToString("N") + ".txt";
            try
            {
                File.WriteAllText(path, "before");
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var before = AssetDatabase.GetAssetDependencyHash(path).ToString();
                File.WriteAllText(path, "after external replacement");
                var result = AssetOperationTools.ReimportAsset(new JObject { ["path"] = path });
                Assert.That(result["error"], Is.Null, result.ToString());
                Assert.That(AssetDatabase.LoadAssetAtPath<TextAsset>(path).text, Is.EqualTo("after external replacement"));
                Assert.That(result.Value<string>("dependency_hash"), Is.EqualTo(AssetDatabase.GetAssetDependencyHash(path).ToString()));
                Assert.That(result.Value<string>("dependency_hash"), Is.Not.EqualTo(before));
                Assert.That(result["force_synchronous_import"]?.Value<bool>(), Is.True);
                Assert.That(result["compilation_complete"]?.Value<bool>(), Is.False);
            }
            finally { AssetDatabase.DeleteAsset(path); }
        }
    }
}
