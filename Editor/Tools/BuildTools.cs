using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;

namespace I1rr.UnityMcp.Editor
{
    [McpToolCategory("Editor")]
    public static class BuildTools
    {
        #region get_build_settings

        [McpTool("get_build_settings",
            "Returns scenes in build settings, active build target, scripting backend, and related build configuration.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {}
            }")]
        public static JObject GetBuildSettings(JObject parameters)
        {
            var scenes = EditorBuildSettings.scenes;
            var scenesArray = new JArray();

            foreach (var scene in scenes)
            {
                scenesArray.Add(new JObject
                {
                    ["path"] = scene.path,
                    ["enabled"] = scene.enabled,
                    ["guid"] = scene.guid.ToString()
                });
            }

            var buildTarget = EditorUserBuildSettings.activeBuildTarget;
            var buildTargetGroup = BuildPipeline.GetBuildTargetGroup(buildTarget);
            var namedBuildTarget = NamedBuildTarget.FromBuildTargetGroup(buildTargetGroup);
            var scriptingBackend = PlayerSettings.GetScriptingBackend(namedBuildTarget);

            return new JObject
            {
                ["scenes"] = scenesArray,
                ["active_build_target"] = buildTarget.ToString(),
                ["build_target_group"] = buildTargetGroup.ToString(),
                ["scripting_backend"] = scriptingBackend.ToString(),
                ["il2cpp_compiler_config"] = PlayerSettings.GetIl2CppCompilerConfiguration(namedBuildTarget).ToString(),
                ["development_build"] = EditorUserBuildSettings.development,
                ["connect_profiler"] = EditorUserBuildSettings.connectProfiler
            };
        }

        #endregion

        #region get_player_settings

        [McpTool("get_player_settings",
            "Returns player settings: product name, company name, version, bundle identifier, and other key configuration.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {}
            }")]
        public static JObject GetPlayerSettings(JObject parameters)
        {
            var buildTarget = EditorUserBuildSettings.activeBuildTarget;
            var buildTargetGroup = BuildPipeline.GetBuildTargetGroup(buildTarget);
            var namedBuildTarget = NamedBuildTarget.FromBuildTargetGroup(buildTargetGroup);

            return new JObject
            {
                ["product_name"] = PlayerSettings.productName,
                ["company_name"] = PlayerSettings.companyName,
                ["version"] = PlayerSettings.bundleVersion,
                ["bundle_identifier"] = PlayerSettings.GetApplicationIdentifier(namedBuildTarget),
                ["default_icon"] = PlayerSettings.GetIcons(NamedBuildTarget.Unknown, IconKind.Any)?.Length > 0,
                ["scripting_backend"] = PlayerSettings.GetScriptingBackend(namedBuildTarget).ToString(),
                ["api_compatibility_level"] = PlayerSettings.GetApiCompatibilityLevel(namedBuildTarget).ToString(),
                ["color_space"] = PlayerSettings.colorSpace.ToString(),
                ["managed_stripping_level"] = PlayerSettings.GetManagedStrippingLevel(namedBuildTarget).ToString(),
                ["target_architecture"] = GetTargetArchitecture(buildTarget),
                ["allow_unsafe_code"] = PlayerSettings.allowUnsafeCode,
                ["incremental_gc"] = PlayerSettings.gcIncremental
            };
        }

        #endregion

        #region get_project_packages

        [McpTool("get_project_packages",
            "Reads and parses the project's Packages/manifest.json, returning all declared dependencies (name → version).",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {}
            }")]
        public static JObject GetProjectPackages(JObject parameters)
        {
            var manifestPath = Path.Combine(Application.dataPath, "..", "Packages", "manifest.json");
            manifestPath = Path.GetFullPath(manifestPath);

            if (!File.Exists(manifestPath))
                return Error($"manifest.json not found at: {manifestPath}");

            var json = File.ReadAllText(manifestPath);
            JObject manifest;
            try
            {
                manifest = JObject.Parse(json);
            }
            catch (Exception ex)
            {
                return Error($"Failed to parse manifest.json: {ex.Message}");
            }

            var dependencies = manifest["dependencies"] as JObject;
            var packagesArray = new JArray();

            if (dependencies != null)
            {
                foreach (var kvp in dependencies)
                {
                    packagesArray.Add(new JObject
                    {
                        ["name"] = kvp.Key,
                        ["version"] = kvp.Value?.ToString()
                    });
                }
            }

            var result = new JObject
            {
                ["package_count"] = packagesArray.Count,
                ["packages"] = packagesArray
            };

            // Include scoped registries if present
            var scopedRegistries = manifest["scopedRegistries"] as JArray;
            if (scopedRegistries != null && scopedRegistries.Count > 0)
            {
                var registriesArray = new JArray();
                foreach (var registry in scopedRegistries)
                {
                    registriesArray.Add(new JObject
                    {
                        ["name"] = registry["name"]?.ToString(),
                        ["url"] = registry["url"]?.ToString(),
                        ["scopes"] = registry["scopes"] as JArray ?? new JArray()
                    });
                }
                result["scoped_registries"] = registriesArray;
            }

            return result;
        }

        #endregion

        #region get_quality_settings

        [McpTool("get_quality_settings",
            "Returns the current quality level, all available quality level names, and key rendering quality settings.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {}
            }")]
        public static JObject GetQualitySettings(JObject parameters)
        {
            var currentLevel = QualitySettings.GetQualityLevel();
            var names = QualitySettings.names;

            var levelsArray = new JArray();
            for (int i = 0; i < names.Length; i++)
            {
                levelsArray.Add(new JObject
                {
                    ["index"] = i,
                    ["name"] = names[i],
                    ["is_current"] = i == currentLevel
                });
            }

            return new JObject
            {
                ["current_level"] = currentLevel,
                ["current_level_name"] = names[currentLevel],
                ["levels"] = levelsArray,
                ["pixel_light_count"] = QualitySettings.pixelLightCount,
                ["texture_quality"] = QualitySettings.globalTextureMipmapLimit,
                ["anisotropic_filtering"] = QualitySettings.anisotropicFiltering.ToString(),
                ["anti_aliasing"] = QualitySettings.antiAliasing,
                ["shadow_quality"] = QualitySettings.shadows.ToString(),
                ["shadow_resolution"] = QualitySettings.shadowResolution.ToString(),
                ["shadow_distance"] = QualitySettings.shadowDistance,
                ["vsync_count"] = QualitySettings.vSyncCount,
                ["lod_bias"] = QualitySettings.lodBias,
                ["max_lod_level"] = QualitySettings.maximumLODLevel,
                ["particle_raycast_budget"] = QualitySettings.particleRaycastBudget
            };
        }

        #endregion

        #region get_project_settings

        [McpTool("get_project_settings",
            "Read project settings for a given category (Physics, Time, Input, Audio, Tags, Layers). Returns serialized properties from the relevant ProjectSettings asset.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""category"": { ""type"": ""string"", ""enum"": [""physics"", ""time"", ""audio"", ""tags"", ""layers""], ""description"": ""Settings category to read"" }
                },
                ""required"": [""category""]
            }")]
        public static JObject GetProjectSettings(JObject parameters)
        {
            var category = parameters["category"]?.Value<string>()?.ToLowerInvariant();
            if (string.IsNullOrEmpty(category))
                return Error("'category' is required.");

            return category switch
            {
                "physics" => GetPhysicsSettings(),
                "time" => GetTimeSettings(),
                "audio" => GetAudioSettings(),
                "tags" => GetTagsSettings(),
                "layers" => GetLayersSettings(),
                _ => Error($"Unknown category: {category}. Supported: physics, time, audio, tags, layers")
            };
        }

        #endregion

        #region get_rendering_pipeline

        [McpTool("get_rendering_pipeline",
            "Returns the active rendering pipeline asset, its type, and renderer features (if URP).",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {}
            }")]
        public static JObject GetRenderingPipeline(JObject parameters)
        {
            var currentPipeline = GraphicsSettings.currentRenderPipeline;

            if (currentPipeline == null)
            {
                return new JObject
                {
                    ["pipeline"] = "Built-in Render Pipeline",
                    ["asset"] = (string)null,
                    ["type"] = "BuiltIn"
                };
            }

            var assetPath = AssetDatabase.GetAssetPath(currentPipeline);
            var pipelineType = currentPipeline.GetType();

            var result = new JObject
            {
                ["pipeline"] = currentPipeline.name,
                ["asset_path"] = assetPath,
                ["type"] = pipelineType.Name,
                ["full_type"] = pipelineType.FullName
            };

            // Try to extract renderer features for URP
            if (pipelineType.Name.Contains("Universal") || pipelineType.Name.Contains("URP"))
            {
                var features = ExtractUrpRendererFeatures(currentPipeline);
                if (features != null)
                    result["renderer_features"] = features;
            }

            // Quality-level pipeline overrides
            var qualityOverrides = new JArray();
            var names = QualitySettings.names;
            for (int i = 0; i < names.Length; i++)
            {
                var so = new SerializedObject(
                    AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset")
                        .FirstOrDefault(a => a is QualitySettings));

                if (so != null)
                {
                    var qualityLevels = so.FindProperty("m_QualitySettings");
                    if (qualityLevels != null && i < qualityLevels.arraySize)
                    {
                        var level = qualityLevels.GetArrayElementAtIndex(i);
                        var pipelineProp = level.FindPropertyRelative("customRenderPipeline");
                        if (pipelineProp?.objectReferenceValue != null)
                        {
                            qualityOverrides.Add(new JObject
                            {
                                ["level"] = names[i],
                                ["pipeline_asset"] = pipelineProp.objectReferenceValue.name,
                                ["asset_path"] = AssetDatabase.GetAssetPath(pipelineProp.objectReferenceValue)
                            });
                        }
                    }
                    so.Dispose();
                }
            }

            if (qualityOverrides.Count > 0)
                result["quality_pipeline_overrides"] = qualityOverrides;

            return result;
        }

        #endregion

        #region Helpers

        private static string GetTargetArchitecture(BuildTarget target)
        {
            return target switch
            {
                BuildTarget.StandaloneWindows => "x86",
                BuildTarget.StandaloneWindows64 => "x86_64",
                BuildTarget.StandaloneOSX => "x86_64_ARM64",
                BuildTarget.StandaloneLinux64 => "x86_64",
                BuildTarget.Android => PlayerSettings.Android.targetArchitectures.ToString(),
                BuildTarget.iOS => "ARM64",
                _ => target.ToString()
            };
        }

        private static JObject GetPhysicsSettings()
        {
            // This diagnostic reports the retained legacy configuration; it does
            // not enable automatic syncing or replace explicit Physics.SyncTransforms calls.
            var manager = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/DynamicsManager.asset").FirstOrDefault();
            if (manager == null) return Error("Physics settings asset is unavailable.");
            bool autoSync;
            using (var settings = new SerializedObject(manager))
            {
                var property = settings.FindProperty("m_AutoSyncTransforms");
                if (property == null) return Error("Physics automatic transform sync configuration is unavailable.");
                autoSync = property.boolValue;
            }
            return new JObject
            {
                ["category"] = "physics",
                ["gravity"] = new JObject
                {
                    ["x"] = Physics.gravity.x,
                    ["y"] = Physics.gravity.y,
                    ["z"] = Physics.gravity.z
                },
                ["default_solver_iterations"] = Physics.defaultSolverIterations,
                ["default_solver_velocity_iterations"] = Physics.defaultSolverVelocityIterations,
                ["bounce_threshold"] = Physics.bounceThreshold,
                ["default_contact_offset"] = Physics.defaultContactOffset,
                ["sleep_threshold"] = Physics.sleepThreshold,
                ["queries_hit_triggers"] = Physics.queriesHitTriggers,
                ["queries_hit_backfaces"] = Physics.queriesHitBackfaces,
                ["auto_simulation"] = Physics.simulationMode.ToString(),
                ["auto_sync_transforms"] = autoSync
            };
        }

        private static JObject GetTimeSettings()
        {
            return new JObject
            {
                ["category"] = "time",
                ["fixed_delta_time"] = Time.fixedDeltaTime,
                ["maximum_delta_time"] = Time.maximumDeltaTime,
                ["time_scale"] = Time.timeScale,
                ["maximum_particle_timestep"] = Time.maximumParticleDeltaTime,
                ["capture_framerate"] = Time.captureFramerate
            };
        }

        private static JObject GetAudioSettings()
        {
            var config = AudioSettings.GetConfiguration();
            return new JObject
            {
                ["category"] = "audio",
                ["speaker_mode"] = config.speakerMode.ToString(),
                ["sample_rate"] = config.sampleRate,
                ["dsp_buffer_size"] = config.dspBufferSize,
                ["num_real_voices"] = config.numRealVoices,
                ["num_virtual_voices"] = config.numVirtualVoices,
                ["global_volume"] = AudioListener.volume,
                ["spatializer_plugin"] = AudioSettings.GetSpatializerPluginName()
            };
        }

        private static JObject GetTagsSettings()
        {
            var tags = UnityEditorInternal.InternalEditorUtility.tags;
            var tagsArray = new JArray();
            foreach (var tag in tags)
                tagsArray.Add(tag);

            return new JObject
            {
                ["category"] = "tags",
                ["count"] = tags.Length,
                ["tags"] = tagsArray
            };
        }

        private static JObject GetLayersSettings()
        {
            var layers = new JArray();
            for (int i = 0; i < 32; i++)
            {
                var layerName = LayerMask.LayerToName(i);
                if (!string.IsNullOrEmpty(layerName))
                {
                    layers.Add(new JObject
                    {
                        ["index"] = i,
                        ["name"] = layerName
                    });
                }
            }

            return new JObject
            {
                ["category"] = "layers",
                ["layers"] = layers
            };
        }

        private static JArray ExtractUrpRendererFeatures(RenderPipelineAsset pipelineAsset)
        {
            try
            {
                var so = new SerializedObject(pipelineAsset);
                var rendererListProp = so.FindProperty("m_RendererDataList");
                if (rendererListProp == null || rendererListProp.arraySize == 0)
                {
                    so.Dispose();
                    return null;
                }

                var features = new JArray();

                for (int r = 0; r < rendererListProp.arraySize; r++)
                {
                    var rendererRef = rendererListProp.GetArrayElementAtIndex(r);
                    var rendererData = rendererRef.objectReferenceValue;
                    if (rendererData == null) continue;

                    var rendererSo = new SerializedObject(rendererData);
                    var featuresProp = rendererSo.FindProperty("m_RendererFeatures");

                    if (featuresProp != null)
                    {
                        for (int f = 0; f < featuresProp.arraySize; f++)
                        {
                            var featureRef = featuresProp.GetArrayElementAtIndex(f);
                            var featureObj = featureRef.objectReferenceValue;
                            if (featureObj == null) continue;

                            features.Add(new JObject
                            {
                                ["renderer_index"] = r,
                                ["renderer_name"] = rendererData.name,
                                ["feature_name"] = featureObj.name,
                                ["feature_type"] = featureObj.GetType().Name,
                                ["enabled"] = (featureObj as MonoBehaviour)?.enabled ?? true
                            });
                        }
                    }

                    rendererSo.Dispose();
                }

                so.Dispose();
                return features.Count > 0 ? features : null;
            }
            catch
            {
                return null;
            }
        }

        private static JObject Error(string message) =>
            McpError.Create(McpErrorCodes.InternalError, message);

        #endregion
    }
}
