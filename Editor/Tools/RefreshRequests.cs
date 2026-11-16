using System;
using System.Linq;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;

namespace I1rr.UnityMcp.Editor
{
    [InitializeOnLoad, McpToolCategory("Editor")]
    public static class RefreshRequests
    {
        private const string SessionKey = "i1rr-unity-mcp.RefreshRequests.v1";
        private static readonly JObject Jobs = JObject.Parse(SessionState.GetString(SessionKey, "{}"));
        private static readonly string Domain = Guid.NewGuid().ToString("N");
        private static int quietUpdates;

        static RefreshRequests()
        {
            CompilationPipeline.assemblyCompilationFinished += (_, messages) =>
            {
                foreach (var job in Pending())
                {
                    job["compiled"] = true;
                    if (messages.Any(m => m.type == CompilerMessageType.Error)) job["errors"] = true;
                }
                Save();
            };
            EditorApplication.update += Update;
        }

        private static System.Collections.Generic.IEnumerable<JObject> Pending() =>
            Jobs.PropertyValues().Cast<JObject>().Where(job => (string)job["status"] == "refresh_requested");

        private static void Save() => SessionState.SetString(SessionKey, Jobs.ToString(Newtonsoft.Json.Formatting.None));

        internal static JObject Request(JObject args, ImportAssetOptions options)
        {
            string id = (string)args?["request_id"] ?? Guid.NewGuid().ToString("N");
            if (!Guid.TryParse(id, out var guid))
                return McpError.Create(McpErrorCodes.InvalidParameter, "request_id must be a UUID.");
            id = guid.ToString("N");
            if (Jobs[id] is JObject existing) return (JObject)existing.DeepClone();

            if (Jobs.Count >= 64)
            {
                var oldest = Jobs.Properties().FirstOrDefault(p => (string)p.Value["status"] != "refresh_requested");
                if (oldest == null) return McpError.Create(McpErrorCodes.MaxLimitReached, "Too many pending refresh requests.");
                oldest.Remove();
            }

            var job = new JObject { ["request_id"] = id, ["status"] = "refresh_requested",
                ["compilation_complete"] = false, ["domain"] = Domain, ["compiled"] = false, ["errors"] = false };
            Jobs[id] = job;
            Save();
            quietUpdates = 0;
            try { AssetDatabase.Refresh(options); }
            catch (Exception ex)
            {
                job["status"] = "failed";
                job["error"] = ex.Message;
                Save();
            }
            return (JObject)job.DeepClone();
        }

        private static void Update()
        {
            if (quietUpdates >= 2) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) { quietUpdates = 0; return; }
            if (++quietUpdates < 2) return;
            bool changed = false;
            foreach (var job in Pending())
            {
                bool reloaded = Domain != (string)job["domain"];
                bool failed = (bool)job["errors"] || EditorUtility.scriptCompilationFailed;
                if (!failed && (bool)job["compiled"] && !reloaded) continue;
                job["reloaded"] = reloaded;
                job["compilation_complete"] = true;
                job["status"] = failed ? "failed" : "settled";
                changed = true;
            }
            if (changed) Save();
        }

        [McpTool("get_refresh_status", "Check a refresh request across domain reload. Settled means Unity finished importing and observed compilation, including reload for newly compiled assemblies.",
            InputSchemaJson = @"{""type"":""object"",""properties"":{""request_id"":{""type"":""string""}},""required"":[""request_id""]}")]
        public static JObject Status(JObject args)
        {
            if (Guid.TryParse((string)args["request_id"], out var guid) && Jobs[guid.ToString("N")] is JObject job)
                return (JObject)job.DeepClone();
            return McpError.Create(McpErrorCodes.NotFound, "Unknown refresh request in this Editor session.");
        }
    }
}
