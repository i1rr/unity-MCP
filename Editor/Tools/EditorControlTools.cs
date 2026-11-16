#if UNITY_6000_6_OR_NEWER
using ObjectId = System.UInt64;
#else
using ObjectId = System.Int32;
#endif
using System;
using System.Linq;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace I1rr.UnityMcp.Editor
{
    [McpToolCategory("Editor")]
    public static class EditorControlTools
    {
        private static readonly string DomainId = Guid.NewGuid().ToString();

        private static readonly string[] DestructiveSegments =
        {
            "Build Player", "Build AssetBundles", "Delete",
            "Remove All", "Clear All", "Reset"
        };

        [McpTool("ping", "Health check — returns server version, Unity version, and editor state")]
        public static JObject Ping(JObject parameters)
        {
            var assemblies = new JArray();
            foreach (var asm in McpToolRegistry.ScannedAssemblies)
                assemblies.Add(asm);

            return new JObject
            {
                ["status"] = "ok",
                ["server"] = "i1rr-unity-mcp",
                ["version"] = McpProtocol.ServerVersion,
                ["api_version"] = 2,
                ["unity_version"] = Application.unityVersion,
                ["is_playing"] = EditorApplication.isPlaying,
                ["is_paused"] = EditorApplication.isPaused,
                ["is_compiling"] = EditorApplication.isCompiling,
                ["platform"] = EditorUserBuildSettings.activeBuildTarget.ToString(),
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["mcp_assemblies"] = assemblies
            };
        }

        [McpTool("get_editor_state", "Returns current editor state: play mode, compilation, active scene, platform, Unity version, prefab stage")]
        public static JObject GetEditorState(JObject parameters)
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(EditorControlTools).Assembly);
            var scene = SceneManager.GetActiveScene();
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();

            return new JObject
            {
                ["isPlaying"] = EditorApplication.isPlaying,
                ["isPaused"] = EditorApplication.isPaused,
                ["isCompiling"] = EditorApplication.isCompiling,
                ["isUpdating"] = EditorApplication.isUpdating,
                ["hasCompilationErrors"] = EditorUtility.scriptCompilationFailed,
                ["projectPath"] = System.IO.Path.GetDirectoryName(Application.dataPath),
                ["packagePath"] = package?.resolvedPath,
                ["packageVersion"] = package?.version,
                ["domainId"] = DomainId,
                ["mcpAssemblyId"] = typeof(EditorControlTools).Assembly.ManifestModule.ModuleVersionId.ToString(),
                ["activeScene"] = scene.path,
                ["activeSceneName"] = scene.name,
                ["platform"] = EditorUserBuildSettings.activeBuildTarget.ToString(),
                ["unityVersion"] = Application.unityVersion,
                ["prefabStage"] = new JObject
                {
                    ["active"] = prefabStage != null,
                    ["assetPath"] = prefabStage != null ? prefabStage.assetPath : ""
                }
            };
        }

        [McpTool("enter_play_mode", "Start normal Unity Play Mode using the Editor configuration. Returns immediately with transition status.")]
        public static JObject EnterPlayMode(JObject parameters)
        {
            if (EditorApplication.isCompiling)
            {
                return new JObject
                {
                    ["status"] = "waiting_for_compilation"
                };
            }

            if (EditorApplication.isPlaying)
            {
                return new JObject
                {
                    ["status"] = "already_playing"
                };
            }

            EditorApplication.isPlaying = true;
            return new JObject
            {
                ["status"] = "transitioning",
                ["mode"] = "normal"
            };
        }

        [McpTool("exit_play_mode", "Exit play mode. Returns immediately with transitioning status.")]
        public static JObject ExitPlayMode(JObject parameters)
        {
            if (!EditorApplication.isPlaying)
            {
                return new JObject
                {
                    ["status"] = "not_playing"
                };
            }

            EditorApplication.ExitPlaymode();
            return new JObject
            {
                ["status"] = "transitioning"
            };
        }

        [McpTool("pause_editor", "Toggle pause state of the editor")]
        public static JObject PauseEditor(JObject parameters)
        {
            EditorApplication.isPaused = !EditorApplication.isPaused;
            return new JObject
            {
                ["isPaused"] = EditorApplication.isPaused
            };
        }

        [McpTool("step_frame", "Advance one frame while paused")]
        public static JObject StepFrame(JObject parameters)
        {
            if (!EditorApplication.isPlaying)
            {
                return McpError.Create(McpErrorCodes.NotInPlayMode, "Editor is not in play mode");
            }

            EditorApplication.Step();
            return new JObject
            {
                ["status"] = "stepped"
            };
        }

        [McpTool("execute_menu_item", "Execute a Unity menu item by path and report synchronous handler errors. Deferred work needs its own completion check. Destructive items require confirm: true.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""path"": { ""type"": ""string"", ""description"": ""Menu item path, e.g. 'File/Save'"" },
                    ""confirm"": { ""type"": ""boolean"", ""description"": ""Required true for destructive menu items"" }
                },
                ""required"": [""path""]
            }")]
        public static JObject ExecuteMenuItem(JObject parameters)
        {
            var path = parameters["path"]?.Value<string>();
            if (string.IsNullOrEmpty(path))
            {
                return McpError.Create(McpErrorCodes.MissingParameter, "Parameter 'path' is required", "Provide the 'path' to the menu item.");
            }

            var lastSegment = path.Split('/').Last().Trim();
            bool isDestructive = DestructiveSegments.Any(s =>
                string.Equals(lastSegment, s, StringComparison.OrdinalIgnoreCase));

            if (isDestructive)
            {
                var confirm = parameters["confirm"]?.Value<bool>() ?? false;
                if (!confirm)
                {
                    var err = McpError.Create(McpErrorCodes.ConfirmationRequired, "Destructive menu item requires confirm: true");
                    err["path"] = path;
                    err["destructiveSegment"] = lastSegment;
                    return err;
                }
            }

            // Unity can log a handler exception and still return true. Observe only
            // this synchronous call; do not block the Editor waiting for delayed work.
            const int maxErrors = 3, maxMessageCharacters = 512;
            var errors = new JArray();
            int errorCount = 0;
            void Record(string message, LogType type)
            {
                errorCount++;
                if (errors.Count >= maxErrors) return;
                message ??= string.Empty;
                errors.Add(new JObject
                {
                    ["type"] = type.ToString(),
                    ["message"] = message.Length <= maxMessageCharacters ? message : message.Substring(0, maxMessageCharacters),
                    ["truncated"] = message.Length > maxMessageCharacters
                });
            }
            void Observe(string message, string stack, LogType type)
            {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) Record(message, type);
            }
            bool success;
            Application.logMessageReceived += Observe;
            try { success = EditorApplication.ExecuteMenuItem(path); }
            catch (Exception exception)
            {
                success = true; // The API threw; side effects cannot be ruled out.
                Record(exception.GetBaseException().Message, LogType.Exception);
            }
            finally { Application.logMessageReceived -= Observe; }
            if (!success)
            {
                var err = McpError.Create(McpErrorCodes.NotFound, "Menu item not found or could not be executed");
                err["path"] = path;
                return err;
            }

            if (errorCount > 0)
            {
                var error = McpError.Create(McpErrorCodes.InternalError,
                    "Menu execution reported a handler error; side effects may already have occurred.");
                error["status"] = "failed";
                error["path"] = path;
                error["execution_may_have_started"] = true;
                error["completion_scope"] = "synchronous_handler";
                error["handler_error_count"] = errorCount;
                error["handler_errors"] = errors;
                return error;
            }

            return new JObject
            {
                ["status"] = "executed",
                ["path"] = path,
                ["completion_scope"] = "synchronous_handler"
            };
        }

        [McpTool("get_selection", "Get currently selected objects in the editor")]
        public static JObject GetSelection(JObject parameters)
        {
            var objects = Selection.objects;
            var items = new JArray();

            foreach (var obj in objects)
            {
                var item = new JObject
                {
                    ["name"] = obj.name,
                    ["instanceId"] = obj.GetMcpId(),
                    ["type"] = obj.GetType().Name
                };

                var assetPath = AssetDatabase.GetAssetPath(obj);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    item["assetPath"] = assetPath;
                }

                if (obj is GameObject go)
                {
                    var goPath = GetGameObjectPath(go);
                    if (!string.IsNullOrEmpty(goPath))
                    {
                        item["hierarchyPath"] = goPath;
                    }
                }

                items.Add(item);
            }

            return new JObject
            {
                ["count"] = objects.Length,
                ["objects"] = items
            };
        }

        [McpTool("set_selection", "Set editor selection by instance IDs or asset paths",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""instanceIds"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""description"": ""Array of instance IDs to select"" },
                    ""paths"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Array of asset paths to select"" }
                }
            }")]
        public static JObject SetSelection(JObject parameters)
        {
            var instanceIds = parameters["instanceIds"]?.ToObject<ObjectId[]>();
            var paths = parameters["paths"]?.ToObject<string[]>();

            if (instanceIds != null && instanceIds.Length > 0)
            {
                Selection.objects = instanceIds.Select(ObjectIdentity.Resolve).Where(obj => obj != null).ToArray();
                return new JObject
                {
                    ["status"] = "selected",
                    ["count"] = Selection.objects.Length
                };
            }

            if (paths != null && paths.Length > 0)
            {
                var objects = paths
                    .Select(p => AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(p))
                    .Where(o => o != null)
                    .ToArray();

                Selection.objects = objects;
                return new JObject
                {
                    ["status"] = "selected",
                    ["count"] = objects.Length,
                    ["requestedCount"] = paths.Length
                };
            }

            return McpError.Create(McpErrorCodes.MissingParameter, "Provide 'instanceIds' or 'paths'");
        }

        [McpTool("undo", "Undo the last editor operation")]
        public static JObject UndoOperation(JObject parameters)
        {
            Undo.PerformUndo();
            return new JObject
            {
                ["status"] = "undone",
                ["currentGroup"] = Undo.GetCurrentGroupName()
            };
        }

        [McpTool("redo", "Redo the last undone editor operation")]
        public static JObject RedoOperation(JObject parameters)
        {
            Undo.PerformRedo();
            return new JObject
            {
                ["status"] = "redone",
                ["currentGroup"] = Undo.GetCurrentGroupName()
            };
        }

        [McpTool("refresh_assets", "Request an AssetDatabase refresh. Follow its request_id with get_refresh_status. Supply a UUID to make retries idempotent within the retained history.",
            InputSchemaJson = @"{""type"":""object"",""properties"":{""request_id"":{""type"":""string""}}}")]
        public static JObject RefreshAssets(JObject parameters)
        {
            return RefreshRequests.Request(parameters, ImportAssetOptions.Default);
        }

        private static string GetGameObjectPath(GameObject go)
        {
            var path = go.name;
            var current = go.transform.parent;
            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }
            return path;
        }
    }
}
