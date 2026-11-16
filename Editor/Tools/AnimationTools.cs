#if UNITY_6000_6_OR_NEWER
using ObjectId = System.UInt64;
#else
using ObjectId = System.Int32;
#endif
using System;
using System.Collections.Generic;
using System.Linq;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace I1rr.UnityMcp.Editor
{
    [McpToolCategory("Scene")]
    public static class AnimationTools
    {
        #region get_animator_state

        [McpTool("get_animator_state",
            "Returns the current state, layer info, parameters, and normalized time for an Animator. Play mode only.",
            AutoRoute = true,
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""instanceId"": { ""type"": ""integer"", ""description"": ""Instance ID of the GameObject with the Animator component"" }
                },
                ""required"": [""instanceId""]
            }")]
        public static JObject GetAnimatorState(JObject parameters)
        {
            var instanceId = parameters["instanceId"]?.ToObject<ObjectId>() ?? 0;
            var go = ObjectIdentity.Resolve(instanceId) as GameObject;
            if (go == null) return Error($"GameObject not found with instanceId: {instanceId}");

            var animator = go.GetComponent<Animator>();
            if (animator == null) return Error($"No Animator component found on '{go.name}'.");

            try
            {
                var result = new JObject
                {
                    ["instanceId"] = instanceId,
                    ["gameObject"] = go.name,
                    ["isActiveAndEnabled"] = animator.isActiveAndEnabled,
                    ["speed"] = animator.speed,
                    ["updateMode"] = animator.updateMode.ToString()
                };

                // Layer info
                var layers = new JArray();
                for (int i = 0; i < animator.layerCount; i++)
                {
                    var stateInfo = animator.GetCurrentAnimatorStateInfo(i);
                    var clipInfo = animator.GetCurrentAnimatorClipInfo(i);
                    var nextInfo = animator.GetNextAnimatorStateInfo(i);
                    var isInTransition = animator.IsInTransition(i);

                    var layerEntry = new JObject
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
                            ["isLooping"] = stateInfo.loop,
                            ["tagHash"] = stateInfo.tagHash
                        },
                        ["isInTransition"] = isInTransition
                    };

                    if (clipInfo.Length > 0)
                    {
                        var clips = new JArray();
                        foreach (var ci in clipInfo)
                        {
                            clips.Add(new JObject
                            {
                                ["clipName"] = ci.clip != null ? ci.clip.name : "(null)",
                                ["weight"] = ci.weight
                            });
                        }
                        layerEntry["currentClips"] = clips;
                    }

                    if (isInTransition)
                    {
                        layerEntry["nextState"] = new JObject
                        {
                            ["fullPathHash"] = nextInfo.fullPathHash,
                            ["shortNameHash"] = nextInfo.shortNameHash,
                            ["normalizedTime"] = nextInfo.normalizedTime
                        };
                    }

                    layers.Add(layerEntry);
                }
                result["layers"] = layers;

                // Parameters
                var paramArray = new JArray();
                for (int i = 0; i < animator.parameterCount; i++)
                {
                    var param = animator.GetParameter(i);
                    var paramEntry = new JObject
                    {
                        ["name"] = param.name,
                        ["type"] = param.type.ToString()
                    };

                    switch (param.type)
                    {
                        case AnimatorControllerParameterType.Float:
                            paramEntry["value"] = animator.GetFloat(param.name);
                            break;
                        case AnimatorControllerParameterType.Int:
                            paramEntry["value"] = animator.GetInteger(param.name);
                            break;
                        case AnimatorControllerParameterType.Bool:
                            paramEntry["value"] = animator.GetBool(param.name);
                            break;
                        case AnimatorControllerParameterType.Trigger:
                            paramEntry["value"] = animator.GetBool(param.name);
                            break;
                    }

                    paramArray.Add(paramEntry);
                }
                result["parameters"] = paramArray;

                return result;
            }
            catch (Exception ex)
            {
                return Error($"Failed to read animator state: {ex.Message}");
            }
        }

        #endregion

        #region set_animator_parameter

        [McpTool("set_animator_parameter",
            "Set an Animator parameter (int, float, bool, or trigger). Play mode only.",
            RequiresPlayMode = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""instanceId"": { ""type"": ""integer"", ""description"": ""Instance ID of the GameObject with the Animator"" },
                    ""name"": { ""type"": ""string"", ""description"": ""Parameter name"" },
                    ""value"": { ""description"": ""Value to set. For triggers, any truthy value will fire the trigger."" }
                },
                ""required"": [""instanceId"", ""name"", ""value""]
            }")]
        public static JObject SetAnimatorParameter(JObject parameters)
        {
            var instanceId = parameters["instanceId"]?.ToObject<ObjectId>() ?? 0;
            var paramName = parameters["name"]?.Value<string>();
            var valueToken = parameters["value"];

            if (string.IsNullOrEmpty(paramName)) return Error("Parameter 'name' is required.");
            if (valueToken == null) return Error("Parameter 'value' is required.");

            var go = ObjectIdentity.Resolve(instanceId) as GameObject;
            if (go == null) return Error($"GameObject not found with instanceId: {instanceId}");

            var animator = go.GetComponent<Animator>();
            if (animator == null) return Error($"No Animator component found on '{go.name}'.");

            try
            {
                // Find the parameter type
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

                if (targetParam == null)
                    return Error($"Parameter '{paramName}' not found on Animator of '{go.name}'.");

                switch (targetParam.type)
                {
                    case AnimatorControllerParameterType.Float:
                        var floatVal = valueToken.Value<float>();
                        animator.SetFloat(paramName, floatVal);
                        return Success($"Set float '{paramName}' = {floatVal}");

                    case AnimatorControllerParameterType.Int:
                        var intVal = valueToken.Value<int>();
                        animator.SetInteger(paramName, intVal);
                        return Success($"Set int '{paramName}' = {intVal}");

                    case AnimatorControllerParameterType.Bool:
                        var boolVal = valueToken.Value<bool>();
                        animator.SetBool(paramName, boolVal);
                        return Success($"Set bool '{paramName}' = {boolVal}");

                    case AnimatorControllerParameterType.Trigger:
                        animator.SetTrigger(paramName);
                        return Success($"Triggered '{paramName}'");

                    default:
                        return Error($"Unknown parameter type: {targetParam.type}");
                }
            }
            catch (Exception ex)
            {
                return Error($"Failed to set animator parameter: {ex.Message}");
            }
        }

        #endregion

        #region get_animation_controllers

        [McpTool("get_animation_controllers",
            "Lists all AnimatorController assets in the project.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {}
            }")]
        public static JObject GetAnimationControllers(JObject parameters)
        {
            try
            {
                var guids = AssetDatabase.FindAssets("t:AnimatorController");
                var controllers = new JArray();

                foreach (var guid in guids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
                    if (controller == null) continue;

                    controllers.Add(new JObject
                    {
                        ["path"] = path,
                        ["name"] = controller.name,
                        ["guid"] = guid,
                        ["layerCount"] = controller.layers.Length,
                        ["parameterCount"] = controller.parameters.Length
                    });
                }

                return new JObject
                {
                    ["count"] = controllers.Count,
                    ["controllers"] = controllers
                };
            }
            catch (Exception ex)
            {
                return Error($"Failed to enumerate animation controllers: {ex.Message}");
            }
        }

        #endregion

        #region get_controller_states

        [McpTool("get_controller_states",
            "Returns states, transitions, and parameters of an AnimatorController asset.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""path"": { ""type"": ""string"", ""description"": ""Asset path to the AnimatorController"" }
                },
                ""required"": [""path""]
            }")]
        public static JObject GetControllerStates(JObject parameters)
        {
            var path = parameters["path"]?.Value<string>();
            if (string.IsNullOrEmpty(path))
                return Error("Parameter 'path' is required.");

            try
            {
                var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
                if (controller == null)
                    return Error($"AnimatorController not found at: {path}");

                var result = new JObject
                {
                    ["path"] = path,
                    ["name"] = controller.name
                };

                // Parameters
                var paramArray = new JArray();
                foreach (var param in controller.parameters)
                {
                    var entry = new JObject
                    {
                        ["name"] = param.name,
                        ["type"] = param.type.ToString()
                    };

                    switch (param.type)
                    {
                        case AnimatorControllerParameterType.Float:
                            entry["defaultValue"] = param.defaultFloat;
                            break;
                        case AnimatorControllerParameterType.Int:
                            entry["defaultValue"] = param.defaultInt;
                            break;
                        case AnimatorControllerParameterType.Bool:
                            entry["defaultValue"] = param.defaultBool;
                            break;
                    }

                    paramArray.Add(entry);
                }
                result["parameters"] = paramArray;

                // Layers and states
                var layersArray = new JArray();
                foreach (var layer in controller.layers)
                {
                    var layerEntry = new JObject
                    {
                        ["name"] = layer.name,
                        ["defaultWeight"] = layer.defaultWeight,
                        ["blendingMode"] = layer.blendingMode.ToString()
                    };

                    var statesArray = new JArray();
                    if (layer.stateMachine != null)
                    {
                        SerializeStateMachine(layer.stateMachine, statesArray, "");
                    }
                    layerEntry["states"] = statesArray;

                    layersArray.Add(layerEntry);
                }
                result["layers"] = layersArray;

                return result;
            }
            catch (Exception ex)
            {
                return Error($"Failed to read controller states: {ex.Message}");
            }
        }

        #endregion

        #region get_animation_clips

        [McpTool("get_animation_clips",
            "Lists animation clip assets in the project, optionally filtered by path.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""path_filter"": { ""type"": ""string"", ""description"": ""Optional folder path filter (e.g. 'Assets/Animations')"" }
                }
            }")]
        public static JObject GetAnimationClips(JObject parameters)
        {
            var pathFilter = parameters["path_filter"]?.Value<string>();

            try
            {
                var searchFolders = string.IsNullOrEmpty(pathFilter)
                    ? new string[] { "Assets" }
                    : new string[] { pathFilter };

                var guids = AssetDatabase.FindAssets("t:AnimationClip", searchFolders);
                var clips = new JArray();

                foreach (var guid in guids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                    if (clip == null) continue;

                    clips.Add(new JObject
                    {
                        ["path"] = path,
                        ["name"] = clip.name,
                        ["guid"] = guid,
                        ["length"] = clip.length,
                        ["frameRate"] = clip.frameRate,
                        ["isLooping"] = clip.isLooping,
                        ["isHumanMotion"] = clip.humanMotion,
                        ["legacy"] = clip.legacy,
                        ["wrapMode"] = clip.wrapMode.ToString()
                    });
                }

                return new JObject
                {
                    ["count"] = clips.Count,
                    ["clips"] = clips
                };
            }
            catch (Exception ex)
            {
                return Error($"Failed to enumerate animation clips: {ex.Message}");
            }
        }

        #endregion

        #region get_animation_events

        [McpTool("get_animation_events",
            "Returns all animation events on an animation clip.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""clip_path"": { ""type"": ""string"", ""description"": ""Asset path to the AnimationClip"" }
                },
                ""required"": [""clip_path""]
            }")]
        public static JObject GetAnimationEvents(JObject parameters)
        {
            var clipPath = parameters["clip_path"]?.Value<string>();
            if (string.IsNullOrEmpty(clipPath))
                return Error("Parameter 'clip_path' is required.");

            try
            {
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
                if (clip == null)
                    return Error($"AnimationClip not found at: {clipPath}");

                var events = AnimationUtility.GetAnimationEvents(clip);
                var eventsArray = new JArray();

                foreach (var evt in events)
                {
                    eventsArray.Add(new JObject
                    {
                        ["time"] = evt.time,
                        ["functionName"] = evt.functionName,
                        ["intParameter"] = evt.intParameter,
                        ["floatParameter"] = evt.floatParameter,
                        ["stringParameter"] = evt.stringParameter,
                        ["objectReferenceParameter"] = evt.objectReferenceParameter != null
                            ? evt.objectReferenceParameter.name
                            : null
                    });
                }

                return new JObject
                {
                    ["clip_path"] = clipPath,
                    ["clip_name"] = clip.name,
                    ["clip_length"] = clip.length,
                    ["event_count"] = eventsArray.Count,
                    ["events"] = eventsArray
                };
            }
            catch (Exception ex)
            {
                return Error($"Failed to read animation events: {ex.Message}");
            }
        }

        #endregion

        #region Helpers

        private static void SerializeStateMachine(AnimatorStateMachine sm, JArray statesArray, string prefix)
        {
            var fullPrefix = string.IsNullOrEmpty(prefix) ? "" : prefix + ".";

            foreach (var childState in sm.states)
            {
                var state = childState.state;
                var stateEntry = new JObject
                {
                    ["name"] = state.name,
                    ["fullPath"] = fullPrefix + state.name,
                    ["speed"] = state.speed,
                    ["speedMultiplier"] = state.speedParameterActive ? state.speedParameter : null,
                    ["tag"] = state.tag,
                    ["writeDefaultValues"] = state.writeDefaultValues
                };

                // Motion
                if (state.motion != null)
                {
                    stateEntry["motion"] = new JObject
                    {
                        ["name"] = state.motion.name,
                        ["type"] = state.motion is BlendTree ? "BlendTree" : "AnimationClip"
                    };
                }

                // Transitions
                var transitions = new JArray();
                foreach (var trans in state.transitions)
                {
                    var transEntry = new JObject
                    {
                        ["destinationState"] = trans.destinationState?.name,
                        ["destinationStateMachine"] = trans.destinationStateMachine?.name,
                        ["isExit"] = trans.isExit,
                        ["hasExitTime"] = trans.hasExitTime,
                        ["exitTime"] = trans.exitTime,
                        ["duration"] = trans.duration,
                        ["hasFixedDuration"] = trans.hasFixedDuration
                    };

                    // Conditions
                    var conditions = new JArray();
                    foreach (var cond in trans.conditions)
                    {
                        conditions.Add(new JObject
                        {
                            ["parameter"] = cond.parameter,
                            ["mode"] = cond.mode.ToString(),
                            ["threshold"] = cond.threshold
                        });
                    }
                    transEntry["conditions"] = conditions;

                    transitions.Add(transEntry);
                }
                stateEntry["transitions"] = transitions;

                statesArray.Add(stateEntry);
            }

            // Recurse into sub-state machines
            foreach (var childSM in sm.stateMachines)
            {
                SerializeStateMachine(childSM.stateMachine, statesArray, fullPrefix + childSM.stateMachine.name);
            }
        }

        private static JObject Error(string message) =>
            McpError.Create(McpErrorCodes.InternalError, message);

        private static JObject Success(string message) =>
            new JObject { ["success"] = true, ["message"] = message };

        #endregion
    }
}
