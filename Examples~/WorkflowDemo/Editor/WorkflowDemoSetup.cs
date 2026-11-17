using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace WorkflowDemo {
    public static class WorkflowDemoSetup {
        public const string ScenePath = "Assets/McpWorkflowDemo/WorkflowDemo.unity";

        [MenuItem("Tools/MCP Workflow Demo/Create scene")]
        public static void CreateScene() {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new System.InvalidOperationException("Exit Play Mode first.");
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.6f, 0.67f, 0.8f);
            RenderSettings.skybox = null;
            var ground = Material("Ground", new Color(0.10f, 0.17f, 0.26f));
            var pad = Material("Landing pad", new Color(0.18f, 0.75f, 0.85f));
            var coral = Material("Delivery crate", new Color(1f, 0.32f, 0.16f));
            var stripe = Material("Lane markings", new Color(0.36f, 0.48f, 0.6f));
            Cube("Ground", new Vector3(0, -0.15f, 0), new Vector3(12, 0.3f, 7), ground);
            Cube("Landing Pad", new Vector3(0, 0.04f, 0), new Vector3(2, 0.08f, 2), pad);
            var crate = Cube("Delivery Crate", new Vector3(-2.5f, 0.63f, 0), Vector3.one * 1.1f, coral);
            for (int i = -5; i <= 5; i++) {
                Cube("Lane Marking " + (i + 5), new Vector3(i, 0.015f, 2.4f), new Vector3(0.45f, 0.025f, 0.06f), stripe);
                Cube("Lane Marking Back " + (i + 5), new Vector3(i, 0.015f, -2.4f), new Vector3(0.45f, 0.025f, 0.06f), stripe);
            }
            var camera = new GameObject("Main Camera").AddComponent<Camera>();
            camera.tag = "MainCamera";
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.025f, 0.045f, 0.08f);
            camera.orthographic = true;
            camera.orthographicSize = 4.8f;
            camera.transform.position = new Vector3(7, 7, -10);
            camera.transform.LookAt(new Vector3(0, 0.25f, 0));
            var light = new GameObject("Key Light").AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.25f;
            light.shadows = LightShadows.Soft;
            light.transform.rotation = Quaternion.Euler(40, -35, 0);
            new GameObject("Workflow HUD").AddComponent<WorkflowDemoHud>().crate = crate.transform;
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log("MCP workflow demo created. The crate is deliberately misaligned; run its EditMode test before fixing it.");
        }

        private static Material Material(string name, Color color) {
            const string folder = "Assets/McpWorkflowDemo/Materials";
            Directory.CreateDirectory(folder);
            string path = folder + "/" + name.Replace(" ", "") + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null) {
                material = new Material(Shader.Find("Standard"));
                AssetDatabase.CreateAsset(material, path);
            }
            material.color = color;
            material.SetFloat("_Glossiness", 0.15f);
            return material;
        }

        private static GameObject Cube(string name, Vector3 position, Vector3 scale, Material material) {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            cube.transform.position = position;
            cube.transform.localScale = scale;
            cube.GetComponent<Renderer>().sharedMaterial = material;
            return cube;
        }
    }
}
