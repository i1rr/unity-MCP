using System;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace I1rr.UnityMcp.Editor.Tests
{
    public class AssetObjectIndexToolsTests
    {
        private string _testFolder;

        [SetUp]
        public void SetUp()
        {
            _testFolder = $"Assets/__UnityMcpAssetObjectIndexTests_{Guid.NewGuid():N}";
            AssetDatabase.CreateFolder("Assets", _testFolder.Substring("Assets/".Length));
        }

        [TearDown]
        public void TearDown()
        {
            if (!string.IsNullOrEmpty(_testFolder))
                AssetDatabase.DeleteAsset(_testFolder);
        }

        [Test]
        public void GetAssetObjectIndex_PrefabMapsComponentsToFullHierarchyPaths()
        {
            var prefabPath = $"{_testFolder}/SM_Nested.prefab";
            var root = new GameObject("SM_Nested");
            try
            {
                var tally = new GameObject("Tally");
                tally.transform.SetParent(root.transform, false);
                var cashOnReels = new GameObject("CashOnReels");
                cashOnReels.transform.SetParent(tally.transform, false);
                cashOnReels.AddComponent<BoxCollider>();

                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }

            var result = AssetObjectIndexTools.GetAssetObjectIndex(new JObject
            {
                ["assetPath"] = prefabPath
            });

            Assert.That(result["error"], Is.Null, result.ToString());
            Assert.That(result["assetPath"]?.Value<string>(), Is.EqualTo(prefabPath));
            Assert.That(result["assetGuid"]?.Value<string>(), Is.Not.Null.And.Not.Empty);

            var objects = (JArray)result["objects"];
            Assert.That(result["objectCount"]?.Value<int>(), Is.EqualTo(objects.Count));

            var collider = objects.Children<JObject>().SingleOrDefault(item =>
                item["hierarchyPath"]?.Value<string>() == "SM_Nested/Tally/CashOnReels" &&
                item["componentType"]?.Value<string>() == nameof(BoxCollider));

            Assert.That(collider, Is.Not.Null, result.ToString());
            Assert.That(collider["localFileId"].Type, Is.EqualTo(JTokenType.String),
                "64-bit file IDs must remain exact when consumed by JavaScript.");
            Assert.That(long.TryParse(collider["localFileId"].Value<string>(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var localFileId), Is.True);
            Assert.That(localFileId, Is.Not.EqualTo(0));
            var filtered = AssetObjectIndexTools.GetAssetObjectIndex(new JObject {
                ["assetPath"] = prefabPath, ["name"] = "cash", ["component_type"] = "boxcollider"
            });
            Assert.That((int)filtered["totalObjectCount"], Is.EqualTo(objects.Count));
            Assert.That((int)filtered["objectCount"], Is.EqualTo(1));
            Assert.That(JToken.DeepEquals(filtered["objects"][0], collider), Is.True);
        }

        [Test]
        public void GetAssetObjectIndex_ReturnsObjectsOrderedByNumericLocalFileId()
        {
            var prefabPath = $"{_testFolder}/Ordered.prefab";
            var root = new GameObject("Root");
            try
            {
                new GameObject("Child").transform.SetParent(root.transform, false);
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }

            var result = AssetObjectIndexTools.GetAssetObjectIndex(new JObject
            {
                ["assetPath"] = prefabPath
            });

            var ids = ((JArray)result["objects"])
                .Children<JObject>()
                .Select(item => long.Parse(item["localFileId"].Value<string>(), CultureInfo.InvariantCulture))
                .ToArray();

            Assert.That(ids, Is.Ordered);
        }

        [Test]
        public void GetAssetObjectIndex_SceneMapsComponentsToFullHierarchyPaths()
        {
            // A prefab's GameObject YAML is also valid scene content. This fixture keeps
            // the user's active scene (including an untitled scene) untouched.
            var prefabPath = $"{_testFolder}/SceneRoot.prefab";
            var scenePath = $"{_testFolder}/IndexedScene.unity";
            var previousActiveScene = SceneManager.GetActiveScene();
            var root = new GameObject("SceneRoot");
            try
            {
                var child = new GameObject("SceneChild");
                child.transform.SetParent(root.transform, false);
                child.AddComponent<BoxCollider>();
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
            AssetDatabase.ForceReserializeAssets(new[] { prefabPath });
            System.IO.File.Copy(prefabPath, scenePath);
            AssetDatabase.ImportAsset(scenePath, ImportAssetOptions.ForceSynchronousImport);
            var sceneCount = SceneManager.sceneCount;
            var result = AssetObjectIndexTools.GetAssetObjectIndex(new JObject { ["assetPath"] = scenePath });
            Assert.That(result["error"], Is.Null, result.ToString());
            var collider = ((JArray)result["objects"]).Children<JObject>().SingleOrDefault(item =>
                item["hierarchyPath"]?.Value<string>() == "SceneRoot/SceneChild" &&
                item["componentType"]?.Value<string>() == nameof(BoxCollider));
            Assert.That(collider, Is.Not.Null, result.ToString());
            Assert.That(collider["localFileId"].Type, Is.EqualTo(JTokenType.String));
            Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(previousActiveScene));
            Assert.That(SceneManager.sceneCount, Is.EqualTo(sceneCount));
        }

        [Test]
        public void GetAssetObjectIndex_MissingAssetPathReturnsError()
        {
            var result = AssetObjectIndexTools.GetAssetObjectIndex(new JObject());

            Assert.That(result["error"], Is.Not.Null);
            Assert.That(result.ToString(), Does.Contain("assetPath"));
        }

        [Test]
        public void GetAssetObjectIndex_UnknownAssetReturnsNotFound()
        {
            var result = AssetObjectIndexTools.GetAssetObjectIndex(new JObject
            {
                ["assetPath"] = $"{_testFolder}/Missing.prefab"
            });

            Assert.That(result["error"], Is.Not.Null);
            Assert.That(result["error_code"]?.Value<string>(), Is.EqualTo("not_found"));
        }
    }
}
