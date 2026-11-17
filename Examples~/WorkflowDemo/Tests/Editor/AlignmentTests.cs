using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace WorkflowDemo.Tests {
    public sealed class AlignmentTests {
        [Test]
        public void CrateIsCenteredOnLandingPad() {
            // A preview reads the saved scene without replacing the user's open scenes.
            // Unsaved or Play Mode adjustments cannot satisfy this assertion.
            var scene = EditorSceneManager.OpenPreviewScene("Assets/McpWorkflowDemo/WorkflowDemo.unity");
            try {
                var roots = scene.GetRootGameObjects();
                var crate = System.Array.Find(roots, root => root.name == "Delivery Crate");
                var pad = System.Array.Find(roots, root => root.name == "Landing Pad");
                Assert.That(crate, Is.Not.Null, "Create the demo scene first.");
                Assert.That(pad, Is.Not.Null);
                Assert.That(crate.transform.position.x, Is.EqualTo(pad.transform.position.x).Within(0.001f), "The delivery crate must be centered on the landing pad's X axis.");
                Assert.That(crate.transform.position.z, Is.EqualTo(pad.transform.position.z).Within(0.001f), "The delivery crate must be centered on the landing pad's Z axis.");
            }
            finally {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }
    }
}
