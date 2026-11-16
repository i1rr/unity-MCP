using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Scripting;

namespace I1rr.UnityMcp.Runtime
{
    [Preserve]
    [McpToolCategory("Scene")]
    public static class RuntimeCoroutineTools
    {
        private static JObject Error(string message) => McpError.Create(McpErrorCodes.InternalError, message);

        private static GameObject FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            var parts = path.Split('/');
            GameObject root = null;

            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (var go in scene.GetRootGameObjects())
                {
                    if (go.name == parts[0])
                    {
                        root = go;
                        break;
                    }
                }
                if (root != null) break;
            }

            if (root == null) return null;
            if (parts.Length == 1) return root;

            var current = root.transform;
            for (int i = 1; i < parts.Length; i++)
            {
                var child = current.Find(parts[i]);
                if (child == null) return null;
                current = child;
            }

            return current.gameObject;
        }

        #region runtime_get_active_coroutines

        [McpTool("runtime_get_active_coroutines",
            "List active coroutines on MonoBehaviour instances at runtime. Best-effort: requires Mono backend; reflection on internal fields may fail on some Unity versions.",
            AutoRoute = true,
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""gameobject_path"": { ""type"": ""string"", ""description"": ""Hierarchy path to a specific GameObject to inspect. If omitted, scans all active MonoBehaviours."" },
                    ""max_scan"": { ""type"": ""integer"", ""description"": ""Max MonoBehaviours to scan when no specific object is given (default 200)."", ""default"": 200 }
                }
            }")]
        [Preserve]
        public static JObject GetActiveCoroutines(JObject parameters)
        {
            var gameobjectPath = parameters["gameobject_path"]?.Value<string>();
            int maxScan = Math.Clamp(parameters["max_scan"]?.Value<int>() ?? 200, 1, 10000);

            // Check for IL2CPP
#if ENABLE_IL2CPP
            return new JObject
            {
                ["error_code"] = "not_supported_il2cpp",
                ["suggestion"] = "Coroutine inspection requires Mono scripting backend."
            };
#else
            FieldInfo coroutineField = null;
            try
            {
                var mbType = typeof(MonoBehaviour);
                coroutineField = mbType.GetField("m_ActiveCoroutines",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (coroutineField == null)
                {
                    coroutineField = mbType.GetField("m_Coroutines",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                }
            }
            catch (Exception ex)
            {
                return new JObject
                {
                    ["error_code"] = "reflection_failed",
                    ["suggestion"] = "This Unity version may not support coroutine inspection.",
                    ["details"] = ex.Message
                };
            }

            if (coroutineField == null)
            {
                return new JObject
                {
                    ["error_code"] = "reflection_failed",
                    ["suggestion"] = "This Unity version may not support coroutine inspection. Could not find internal coroutine field on MonoBehaviour."
                };
            }

            var coroutines = new JArray();
            bool truncated = false;
            int scannedCount = 0;
            int totalMonoBehaviours = 0;

            try
            {
                MonoBehaviour[] behaviours;

                if (!string.IsNullOrEmpty(gameobjectPath))
                {
                    var go = FindByPath(gameobjectPath);
                    if (go == null)
                        return Error($"GameObject not found at path: {gameobjectPath}");
                    behaviours = go.GetComponents<MonoBehaviour>();
                    totalMonoBehaviours = behaviours.Length;
                }
                else
                {
                    behaviours = UnityCompatibility.FindObjectsByType<MonoBehaviour>();
                    totalMonoBehaviours = behaviours.Length;
                }

                foreach (var mb in behaviours)
                {
                    if (mb == null) continue;
                    if (string.IsNullOrEmpty(gameobjectPath) && scannedCount >= maxScan)
                    {
                        truncated = true;
                        break;
                    }
                    scannedCount++;

                    object fieldValue;
                    try
                    {
                        fieldValue = coroutineField.GetValue(mb);
                    }
                    catch
                    {
                        continue;
                    }

                    if (fieldValue == null) continue;

                    if (fieldValue is IList list)
                    {
                        foreach (var item in list)
                        {
                            if (item == null) continue;
                            string methodName = "<unknown>";
                            bool isRunning = true;

                            try
                            {
                                var itemType = item.GetType();
                                var nameField = itemType.GetField("m_MethodName",
                                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                                if (nameField != null)
                                {
                                    methodName = nameField.GetValue(item)?.ToString() ?? "<unknown>";
                                }
                                else
                                {
                                    methodName = itemType.Name;
                                }

                                var runningField = itemType.GetField("m_IsRunning",
                                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                                if (runningField != null)
                                {
                                    isRunning = (bool)runningField.GetValue(item);
                                }
                            }
                            catch
                            {
                                // Best-effort: use defaults
                            }

                            var ownerPath = GetGameObjectPath(mb.gameObject);
                            coroutines.Add(new JObject
                            {
                                ["owner"] = ownerPath,
                                ["component"] = mb.GetType().Name,
                                ["method_name"] = methodName,
                                ["is_running"] = isRunning
                            });
                        }
                    }
                    else if (fieldValue is IEnumerable enumerable)
                    {
                        foreach (var item in enumerable)
                        {
                            if (item == null) continue;
                            var ownerPath = GetGameObjectPath(mb.gameObject);
                            coroutines.Add(new JObject
                            {
                                ["owner"] = ownerPath,
                                ["component"] = mb.GetType().Name,
                                ["method_name"] = item.GetType().Name,
                                ["is_running"] = true
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return new JObject
                {
                    ["error_code"] = "reflection_failed",
                    ["suggestion"] = "This Unity version may not support coroutine inspection.",
                    ["details"] = ex.Message
                };
            }

            var result = new JObject
            {
                ["success"] = true,
                ["coroutines"] = coroutines,
                ["count"] = coroutines.Count,
                ["scanned_count"] = scannedCount,
                ["total_monobehaviours"] = totalMonoBehaviours
            };

            if (truncated)
            {
                result["truncated"] = true;
            }

            return result;
#endif
        }

        private static string GetGameObjectPath(GameObject go)
        {
            var path = go.name;
            var parent = go.transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }

        #endregion
    }
}
