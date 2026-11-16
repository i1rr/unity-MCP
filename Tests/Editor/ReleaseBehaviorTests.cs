using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Linq;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace I1rr.UnityMcp.Editor.Tests
{
    public class ReleaseBehaviorTests
    {
        // Keep the wait in compiled fixture code: Unity's evaluator can see
        // duplicate framework Thread types when referencing loaded assemblies.
        public static void WaitPastCodeBudget() => System.Threading.Thread.Sleep(120);

        [Test]
        public void CodeBudgetOverrunReportsCompletedExecutionWithoutClaimingCancellation()
        {
            bool enabled = McpPreferencesProvider.EnableCodeExecution;
            try
            {
                McpPreferencesProvider.EnableCodeExecution = true;
                var result = UtilityTools.ExecuteCode(new JObject
                {
                    ["code"] = "I1rr.UnityMcp.Editor.Tests.ReleaseBehaviorTests.WaitPastCodeBudget();",
                    ["timeout_ms"] = 100
                });
                Assert.That((bool)result["success"], Is.True, result.ToString());
                Assert.That((bool)result["timeout_enforced"], Is.False);
                Assert.That((bool)result["exceeded_timeout"], Is.True);
                Assert.That((long)result["elapsed_ms"], Is.GreaterThanOrEqualTo(100));
            }
            finally { McpPreferencesProvider.EnableCodeExecution = enabled; }
        }

        [Test]
        public void NewProjectsUseFocusedDiscoveryAndExplicitFullIsPreserved()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            try
            {
                Assert.That(ToolCatalog.IsFocused(path), Is.True);
                File.WriteAllText(path, "Full\n");
                Assert.That(ToolCatalog.IsFocused(path), Is.False);
                File.WriteAllText(path, "Focused\n");
                Assert.That(ToolCatalog.IsFocused(path), Is.True);
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void CodeOptInIsProjectScopedAndRejectedPayloadIsNeverLogged()
        {
            Assert.That(McpPreferencesProvider.CodeExecutionKey("/test/a"), Is.Not.EqualTo(McpPreferencesProvider.CodeExecutionKey("/test/b")));
            bool enabled = McpPreferencesProvider.EnableCodeExecution;
            string payload = "test-private-" + Guid.NewGuid().ToString("N");
            bool disclosed = false;
            void Observe(string message, string stack, LogType type) { disclosed |= message.Contains(payload); }
            Application.logMessageReceived += Observe;
            try
            {
                McpPreferencesProvider.EnableCodeExecution = false;
                Assert.That((bool)UtilityTools.ExecuteCode(new JObject { ["code"] = payload })["success"], Is.False);
                Assert.That(disclosed, Is.False);
            }
            finally
            {
                Application.logMessageReceived -= Observe;
                McpPreferencesProvider.EnableCodeExecution = enabled;
            }
        }

        [TestCase("{'jsonrpc':'2.0','method':'ping','id':1}", false, 1)]
        [TestCase("[{'jsonrpc':'2.0','method':'ping','id':1}]", true, 1)]
        [TestCase("[{'jsonrpc':'2.0','method':'ping','id':1},{'jsonrpc':'2.0','method':'ping'}]", true, 2)]
        public void ParserPreservesWireShape(string json, bool batch, int count)
        {
            var (requests, error) = McpProtocol.ParseRequest(json, null, out bool isBatch);
            Assert.That(error, Is.Null);
            Assert.That(isBatch, Is.EqualTo(batch));
            Assert.That(requests.Count, Is.EqualTo(count));
        }

        [TestCase("[{'jsonrpc':'2.0','method':'ping','id':1}]", 1)]
        [TestCase("[{'jsonrpc':'2.0','method':'ping','id':1},{'jsonrpc':'2.0','method':'ping'}]", 1)]
        [TestCase("[{'jsonrpc':'2.0','method':'ping','id':1},{'jsonrpc':'2.0','method':'ping','id':2}]", 2)]
        [TestCase("[{'jsonrpc':'2.0','method':'ping'}]", 0)]
        public void BaselineHttpBatchPreservesArrayOrReturnsAccepted(string json, int replies)
        {
            var previous = McpEditorServer._outboundQueue;
            using var queue = new BlockingCollection<McpEditorServer.CompletedRequest>();
            string session = Guid.NewGuid().ToString("N");
            McpProtocol.RestoreSession(session, McpProtocol.ProtocolVersionBaseline);
            try
            {
                McpEditorServer._outboundQueue = queue;
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                    ["Host"] = "127.0.0.1:" + McpEditorServer.Port, ["Content-Type"] = "application/json",
                    ["MCP-Session-Id"] = session, ["Authorization"] = "Bearer " + McpEditorServer._authToken
                };
                EditorRequestPipeline.ProcessRequest(new McpEditorServer.McpRequest { ClientId = -1,
                    HttpRequest = new TcpHttpHandler.HttpRequest { Method = "POST", Path = "/mcp",
                        Headers = headers, Body = Encoding.UTF8.GetBytes(json.Replace('\'','"')) } });
                Assert.That(queue.TryTake(out var response), Is.True);
                Assert.That(response.StatusCode, Is.EqualTo(replies == 0 ? 202 : 200));
                if (replies == 0) Assert.That(response.ResponseBytes, Is.Null);
                else Assert.That(JArray.Parse(Encoding.UTF8.GetString(response.ResponseBytes)).Count, Is.EqualTo(replies));
            }
            finally
            {
                McpEditorServer._outboundQueue = previous;
                McpProtocol.TerminateSession(session);
            }
        }

        [UnityTest]
        public IEnumerator PrefabMaterialDependencyIsFoundInBothDirections()
        {
            string folder = "Assets/McpReference_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            string material = folder + "/material.mat", prefab = folder + "/holder.prefab";
            try
            {
                var mat = new Material(Shader.Find("Standard"));
                AssetDatabase.CreateAsset(mat, material);
                var obj = GameObject.CreatePrimitive(PrimitiveType.Cube);
                try
                {
                    obj.GetComponent<MeshRenderer>().sharedMaterial = mat;
                    PrefabUtility.SaveAsPrefabAsset(obj, prefab);
                }
                finally { UnityEngine.Object.DestroyImmediate(obj); }
                Assert.That(AssetDatabase.GetDependencies(prefab), Does.Contain(material));
                string id = (string)AssetSearchTools.GetAssetReferences(new JObject { ["path"] = material })["job_id"];
                var deadline = DateTime.UtcNow.AddSeconds(30);
                JObject status;
                do
                {
                    yield return null;
                    status = JobManager.GetJobStatus(id);
                } while ((string)status["status"] == "running" && DateTime.UtcNow < deadline);
                Assert.That((string)status["status"], Is.EqualTo("completed"), status.ToString());
                Assert.That(status["result"]["references"].Any(r => (string)r["path"] == prefab &&
                    (string)r["guid"] == AssetDatabase.AssetPathToGUID(prefab)), Is.True);
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }
    }
}
