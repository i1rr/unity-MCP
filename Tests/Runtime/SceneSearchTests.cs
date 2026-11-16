using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace I1rr.UnityMcp.Runtime.Tests
{
    public class SceneSearchTests
    {
        [Test]
        public void EditorPlayKeepsEditorRootAndRuntimeSamplingWithoutPlayerListener()
        {
            if (!Application.isEditor) Assert.Ignore("Editor hosting check.");
            Assert.That(McpPathValidator.ProjectRoot.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar),
                Is.EqualTo(System.IO.Path.GetDirectoryName(Application.dataPath)));
            Assert.That(RuntimeMainThreadDispatcher.Instance, Is.Not.Null);
            Assert.That(Runtime.Sessions.SessionManager.Instance, Is.Not.Null);
            Assert.That(I1rr.UnityMcp.Shared.UnityCompatibility.FindObjectsByType<McpRuntimeServer>(), Is.Empty);
        }

        [Test]
        public void CombinedFiltersFindOnlyMatchingInactivePersistentObjects()
        {
            var match = new GameObject("SceneSearchProof_target");
            var decoy = new GameObject("SceneSearchProof_decoy");
            try
            {
                Object.DontDestroyOnLoad(match);
                match.tag = decoy.tag = "MainCamera";
                match.AddComponent<BoxCollider>();
                match.SetActive(false);
                var filter = new JObject { ["name"] = "SceneSearchProof", ["tag"] = "MainCamera",
                    ["component_type"] = "BoxCollider", ["inactive"] = true };
                var found = RuntimeSceneTools.FindGameObjects(filter);
                Assert.That((int)found["count"], Is.EqualTo(1));
                Assert.That((string)found["results"][0]["name"], Is.EqualTo(match.name));
                filter["inactive"] = false;
                Assert.That((int)RuntimeSceneTools.FindGameObjects(filter)["count"], Is.Zero);
                Assert.That((int)RuntimeSceneTools.FindGameObjects(new JObject { ["name"] = match.name })["count"], Is.EqualTo(1));
            }
            finally { Object.DestroyImmediate(match); Object.DestroyImmediate(decoy); }
        }
    }
}
