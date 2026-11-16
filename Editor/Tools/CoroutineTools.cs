#if UNITY_6000_6_OR_NEWER
using ObjectId = System.UInt64;
#else
using ObjectId = System.Int32;
#endif
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace I1rr.UnityMcp.Editor
{
    [McpToolCategory("Scene")]
    public static class CoroutineTools
    {
        private static JObject Error(string message) => McpError.Create(McpErrorCodes.InternalError, message);

        #region get_active_coroutines

        [McpTool("get_active_coroutines",
            "List active coroutines on MonoBehaviour instances. Best-effort: requires Mono backend; reflection on internal fields may fail on some Unity versions.",
            AutoRoute = true,
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""instanceId"": { ""type"": ""integer"", ""description"": ""Instance ID of a specific GameObject to inspect. If omitted, scans all active MonoBehaviours."" },
                    ""max_scan"": { ""type"": ""integer"", ""description"": ""Max MonoBehaviours to scan when no specific object is given (default 200)."", ""default"": 200 }
                }
            }")]
        public static JObject GetActiveCoroutines(JObject parameters)
        {
            var instanceId = parameters["instanceId"]?.ToObject<ObjectId>();
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
                // Unity stores active coroutines in an internal field on MonoBehaviour.
                // The field name has varied across versions; try known names.
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

                if (instanceId.HasValue)
                {
                    var go = ObjectIdentity.Resolve(instanceId.Value) as GameObject;
                    if (go == null)
                        return Error($"GameObject not found with instanceId: {instanceId.Value}");
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
                    if (!instanceId.HasValue && scannedCount >= maxScan)
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
                                // Try to extract method name from the coroutine enumerator
                                var itemType = item.GetType();
                                var nameField = itemType.GetField("m_MethodName",
                                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                                if (nameField != null)
                                {
                                    methodName = nameField.GetValue(item)?.ToString() ?? "<unknown>";
                                }
                                else
                                {
                                    // Fall back to type name which often encodes the method
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
                                ["owner_instanceId"] = mb.gameObject.GetMcpId(),
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
                                ["owner_instanceId"] = mb.gameObject.GetMcpId(),
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
