using System;
using System.Collections.Generic;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Scripting;

namespace I1rr.UnityMcp.Runtime
{
    [Preserve]
    [McpToolCategory("Scene")]
    public static class RuntimeAnimationTools
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

        #region runtime_get_animator_state

        [McpTool("runtime_get_animator_state",
            "Get current animator state, transition info, and parameters for a GameObject.",
            AutoRoute = true,
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""gameobject_path"": { ""type"": ""string"", ""description"": ""Hierarchy path to the GameObject with an Animator"" }
                },
                ""required"": [""gameobject_path""]
            }")]
        [Preserve]
        public static JObject GetAnimatorState(JObject parameters)
        {
            var path = parameters["gameobject_path"]?.Value<string>();
            if (string.IsNullOrEmpty(path)) return Error("'gameobject_path' is required.");

            var go = FindByPath(path);
            if (go == null) return Error($"GameObject not found at path: {path}");

            var animator = go.GetComponent<Animator>();
            if (animator == null) return Error($"No Animator component on '{go.name}'.");

            var result = new JObject
            {
                ["gameobject_path"] = path,
                ["isActiveAndEnabled"] = animator.isActiveAndEnabled,
                ["speed"] = animator.speed,
                ["updateMode"] = animator.updateMode.ToString()
            };

            // Layers
            var layers = new JArray();
            for (int i = 0; i < animator.layerCount; i++)
            {
                var stateInfo = animator.GetCurrentAnimatorStateInfo(i);
                var isInTransition = animator.IsInTransition(i);

                var layerObj = new JObject
                {
                    ["index"] = i,
                    ["name"] = animator.GetLayerName(i),
                    ["weight"] = animator.GetLayerWeight(i),
                    ["currentState"] = new JObject
                    {
                        ["fullPathHash"] = stateInfo.fullPathHash,
                        ["shortNameHash"] = stateInfo.shortNameHash,
                        ["normalizedTime"] = stateInfo.normalizedTime,
                        ["length"] = stateInfo.length,
                        ["speed"] = stateInfo.speed,
                        ["isLooping"] = stateInfo.loop
                    },
                    ["isInTransition"] = isInTransition
                };

                if (isInTransition)
                {
                    var nextInfo = animator.GetNextAnimatorStateInfo(i);
                    var transInfo = animator.GetAnimatorTransitionInfo(i);
                    layerObj["nextState"] = new JObject
                    {
                        ["fullPathHash"] = nextInfo.fullPathHash,
                        ["normalizedTime"] = nextInfo.normalizedTime
                    };
                    layerObj["transition"] = new JObject
                    {
                        ["normalizedTime"] = transInfo.normalizedTime,
                        ["duration"] = transInfo.duration
                    };
                }

                // Clip info
                var clipInfos = animator.GetCurrentAnimatorClipInfo(i);
                if (clipInfos.Length > 0)
                {
                    var clips = new JArray();
                    foreach (var ci in clipInfos)
                    {
                        clips.Add(new JObject
                        {
                            ["name"] = ci.clip != null ? ci.clip.name : "(null)",
                            ["weight"] = ci.weight
                        });
                    }
                    layerObj["clips"] = clips;
                }

                layers.Add(layerObj);
            }
            result["layers"] = layers;

            // Parameters
            var paramArray = new JArray();
            for (int i = 0; i < animator.parameterCount; i++)
            {
                var p = animator.GetParameter(i);
                var paramObj = new JObject
                {
                    ["name"] = p.name,
                    ["type"] = p.type.ToString()
                };

                switch (p.type)
                {
                    case AnimatorControllerParameterType.Bool:
                        paramObj["value"] = animator.GetBool(p.nameHash);
                        break;
                    case AnimatorControllerParameterType.Float:
                        paramObj["value"] = animator.GetFloat(p.nameHash);
                        break;
                    case AnimatorControllerParameterType.Int:
                        paramObj["value"] = animator.GetInteger(p.nameHash);
                        break;
                    case AnimatorControllerParameterType.Trigger:
                        paramObj["value"] = animator.GetBool(p.nameHash);
                        break;
                }

                paramArray.Add(paramObj);
            }
            result["parameters"] = paramArray;

            return result;
        }

        #endregion

        #region runtime_set_animator_param

        [McpTool("runtime_set_animator_param",
            "Set an animator parameter value at runtime.",
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""gameobject_path"": { ""type"": ""string"", ""description"": ""Hierarchy path to the GameObject with an Animator"" },
                    ""param_name"": { ""type"": ""string"", ""description"": ""Animator parameter name"" },
                    ""value"": { ""description"": ""Value to set (bool, float, int, or 'trigger' to set trigger)"" }
                },
                ""required"": [""gameobject_path"", ""param_name"", ""value""]
            }")]
        [Preserve]
        public static JObject SetAnimatorParam(JObject parameters)
        {
            var path = parameters["gameobject_path"]?.Value<string>();
            var paramName = parameters["param_name"]?.Value<string>();
            var valueToken = parameters["value"];

            if (string.IsNullOrEmpty(path)) return Error("'gameobject_path' is required.");
            if (string.IsNullOrEmpty(paramName)) return Error("'param_name' is required.");
            if (valueToken == null) return Error("'value' is required.");

            var go = FindByPath(path);
            if (go == null) return Error($"GameObject not found at path: {path}");

            var animator = go.GetComponent<Animator>();
            if (animator == null) return Error($"No Animator component on '{go.name}'.");

            // Find the parameter
            AnimatorControllerParameter targetParam = null;
            for (int i = 0; i < animator.parameterCount; i++)
            {
                var p = animator.GetParameter(i);
                if (p.name == paramName)
                {
                    targetParam = p;
                    break;
                }
            }

            if (targetParam == null) return Error($"Animator parameter '{paramName}' not found.");

            switch (targetParam.type)
            {
                case AnimatorControllerParameterType.Bool:
                    animator.SetBool(paramName, valueToken.Value<bool>());
                    break;
                case AnimatorControllerParameterType.Float:
                    animator.SetFloat(paramName, valueToken.Value<float>());
                    break;
                case AnimatorControllerParameterType.Int:
                    animator.SetInteger(paramName, valueToken.Value<int>());
                    break;
                case AnimatorControllerParameterType.Trigger:
                    var strVal = valueToken.Type == JTokenType.String ? valueToken.Value<string>() : null;
                    if (strVal == "reset")
                        animator.ResetTrigger(paramName);
                    else
                        animator.SetTrigger(paramName);
                    break;
                default:
                    return Error($"Unsupported parameter type: {targetParam.type}");
            }

            return new JObject
            {
                ["success"] = true,
                ["gameobject_path"] = path,
                ["param_name"] = paramName,
                ["param_type"] = targetParam.type.ToString(),
                ["new_value"] = valueToken
            };
        }

        #endregion
    }
}
