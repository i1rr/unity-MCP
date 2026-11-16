using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace I1rr.UnityMcp.Editor
{
    [InitializeOnLoad, McpToolCategory("Editor")]
    public static class TestRunTools
    {
        private const string ActiveKey = "i1rr-unity-mcp.ActiveTestRun";
        private static readonly string DirectoryPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "i1rr-unity-mcp-runs");
        // UTF broadcasts callbacks globally. Its own Test Runner window uses these
        // queries to detect a busy runner and to track a particular scheduled job.
        private static readonly MethodInfo AnyRunning = typeof(TestRunnerApi).GetMethod("IsRunActive", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo IsRunning = typeof(TestRunnerApi).GetMethod("IsRunning", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly PropertyInfo Jobs = typeof(TestRunnerApi).GetProperty("m_testJobDataHolder", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo GetRunners = Jobs?.PropertyType.GetMethod("GetAllRunners");
        private static readonly Callbacks Listener = new();
        private static JObject active;

        static TestRunTools()
        {
            var id = SessionState.GetString(ActiveKey, "");
            if (id.Length == 0) return;
            active = Read(id);
            if (active == null || (string)active["status"] != "running")
            {
                active = null;
                SessionState.EraseString(ActiveKey);
            }
            else TestRunnerApi.RegisterTestCallback(Listener);
        }

        private static JObject Read(string id)
        {
            string path = Path.Combine(DirectoryPath, id + ".json");
            return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null;
        }

        private static void Save(JObject job)
        {
            string path = Path.Combine(DirectoryPath, (string)job["run_id"] + ".json");
            File.WriteAllText(path + ".tmp", job.ToString());
            if (File.Exists(path)) File.Replace(path + ".tmp", path, null);
            else File.Move(path + ".tmp", path);
        }

        private static void Finish(string status, string message = null)
        {
            if (message != null) active["message"] = message;
            active["status"] = status;
            Save(active);
            TestRunnerApi.UnregisterTestCallback(Listener);
            SessionState.EraseString(ActiveKey);
            active = null;
        }

        [McpTool("list_tests", "Search Unity Test Runner for exact fixture and case names to pass to run_tests. Filters by test mode and a case-insensitive name fragment. Pages default to 20 rows.",
            DeferredResponse = true, InputSchemaJson = @"{""type"":""object"",""properties"":{
            ""mode"":{""type"":""string"",""enum"":[""EditMode"",""PlayMode""]},""query"":{""type"":""string""},""offset"":{""type"":""integer"",""minimum"":0},
            ""limit"":{""type"":""integer"",""minimum"":1,""maximum"":100}},""required"":[""mode""]}")]
        public static object ListTests(JObject args)
        {
            string mode = (string)args["mode"];
            int offset = (int?)args["offset"] ?? 0, limit = (int?)args["limit"] ?? 20;
            if ((mode != "EditMode" && mode != "PlayMode") || offset < 0 || limit < 1 || limit > 100)
                return McpError.Create(McpErrorCodes.InvalidParameter, "Use EditMode or PlayMode, nonnegative offset and limit 1–100.");
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                return McpError.Create(McpErrorCodes.InvalidState, "List tests from idle Edit Mode.");
            string query = (string)args["query"] ?? "";
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            JObject result = null;
            try
            {
                api.RetrieveTestList(mode == "EditMode" ? TestMode.EditMode : TestMode.PlayMode, root =>
                {
                    try
                    {
                        var matches = new SortedDictionary<string, string>(StringComparer.Ordinal);
                        AddMatches(root, query, matches);
                        result = new JObject {
                            ["total"] = matches.Count, ["next_offset"] = matches.Count - offset > limit ? offset + limit : (int?)null,
                            ["tests"] = new JArray(matches.Skip(offset).Take(limit).Select(m =>
                                new JObject { ["kind"] = m.Value, ["name"] = m.Key }))
                        };
                    }
                    catch (Exception ex) { result = McpError.Create(McpErrorCodes.InternalError, ex.Message); }
                });
            }
            catch { UnityEngine.Object.DestroyImmediate(api); throw; }
            return new DeferredResult { IsComplete = () => result != null, GetResult = () => result,
                Cleanup = () => UnityEngine.Object.DestroyImmediate(api), TimeoutSeconds = 30 };
        }

        private static void AddMatches(ITestAdaptor node, string query, SortedDictionary<string, string> matches)
        {
            if (!node.IsSuite)
            {
                if (node.FullName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) matches[node.FullName] = "case";
                return;
            }
            bool fixture = false;
            foreach (var child in node.Children)
            {
                // Cached UTF trees can lack reflection metadata. A parameterized
                // method's children are cases named Method(args), not fixtures.
                if (child.FullName.StartsWith(node.FullName + ".", StringComparison.Ordinal) &&
                    (!child.IsSuite || child.Children.Any(c => !c.IsSuite && c.FullName.StartsWith(child.FullName + "(", StringComparison.Ordinal))))
                    fixture = true;
                AddMatches(child, query, matches);
            }
            if (fixture && !node.IsTestAssembly && node.FullName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                matches[node.FullName] = "fixture";
        }

        [McpTool("run_tests", "Run exact named Unity fixtures or tests in EditMode or PlayMode. Returns a run ID for get_test_run. A caller UUID makes retries safe.",
            InputSchemaJson = @"{""type"":""object"",""properties"":{""mode"":{""type"":""string"",""enum"":[""EditMode"",""PlayMode""]},
                ""tests"":{""type"":""array"",""minItems"":1,""maxItems"":32,""items"":{""type"":""string""}},""run_id"":{""type"":""string""}},""required"":[""mode"",""tests""]}")]
        public static JObject Run(JObject args)
        {
            string mode = (string)args["mode"];
            if ((mode != "EditMode" && mode != "PlayMode") || args["tests"] is not JArray tests ||
                tests.Count < 1 || tests.Count > 32 || tests.Any(t => t.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)t)))
                return McpError.Create(McpErrorCodes.InvalidParameter, "Specify EditMode or PlayMode and 1–32 exact fixture or test names.");
            string id = (string)args["run_id"] ?? Guid.NewGuid().ToString("N");
            if (!Guid.TryParse(id, out var guid)) return McpError.Create(McpErrorCodes.InvalidParameter, "run_id must be a UUID.");
            id = guid.ToString("N");
            var previous = Read(id);
            if (previous != null)
            {
                if ((string)previous["mode"] != mode || !JToken.DeepEquals(previous["tests"], tests))
                    return McpError.Create(McpErrorCodes.InvalidParameter, "run_id already identifies a different request.");
                return Status(new JObject { ["run_id"] = id });
            }
            if (EditorUtility.scriptCompilationFailed)
                return McpError.Create(McpErrorCodes.InvalidState,
                    "Script compilation failed. Fix compiler errors and wait for a successful refresh before starting tests. No run was queued.");
            RecoverInterrupted();
            if (AnyRunning == null || IsRunning == null || GetRunners == null)
                return McpError.Create(McpErrorCodes.InvalidState, "Cannot identify active jobs in this version of Unity Test Framework.");
            if (active != null || (bool)AnyRunning.Invoke(null, null) || EditorApplication.isCompiling ||
                EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                return McpError.Create(McpErrorCodes.InvalidState, "Start a test run from an idle Editor in Edit Mode.");

            Directory.CreateDirectory(DirectoryPath);
            active = new JObject { ["run_id"] = id, ["mode"] = mode, ["tests"] = tests.DeepClone(), ["status"] = "running" };
            var job = active;
            Save(job);
            SessionState.SetString(ActiveKey, id);
            TestRunnerApi.RegisterTestCallback(Listener);
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            try
            {
                string unityId = api.Execute(new ExecutionSettings(new Filter {
                    testMode = mode == "EditMode" ? TestMode.EditMode : TestMode.PlayMode, testNames = tests.Values<string>().ToArray()
                }));
                job["unity_run_id"] = unityId;
                Save(job);
            }
            catch (Exception ex) { Finish("failed", ex.Message); }
            finally { UnityEngine.Object.DestroyImmediate(api); }
            return (JObject)job.DeepClone();
        }

        private static void RecoverInterrupted()
        {
            if (active != null && IsRunning != null && active["unity_run_id"] != null &&
                !(bool)IsRunning.Invoke(null, new object[] { (string)active["unity_run_id"] }))
                Finish("interrupted", "Unity no longer has this job and did not deliver a final result.");
        }

        [McpTool("get_test_run", "Get status, executed test names, counts and the NUnit XML path for a filtered Unity test run.",
            InputSchemaJson = @"{""type"":""object"",""properties"":{""run_id"":{""type"":""string""}},""required"":[""run_id""]}")]
        public static JObject Status(JObject args)
        {
            if (!Guid.TryParse((string)args["run_id"], out var guid))
                return McpError.Create(McpErrorCodes.InvalidParameter, "run_id must be a UUID.");
            RecoverInterrupted();
            var job = Read(guid.ToString("N"));
            if (job == null) return McpError.Create(McpErrorCodes.NotFound, "Unknown test run.");
            if ((string)job["status"] == "running" && (string)active?["run_id"] != (string)job["run_id"])
            {
                job["status"] = "interrupted";
                Save(job);
            }
            return job;
        }

        private sealed class Callbacks : IErrorCallbacks
        {
            private static bool OwnsCallback()
            {
                if (active == null) return false;
                var runners = (Array)GetRunners.Invoke(Jobs.GetValue(null), null);
                if (runners.Length > 1)
                {
                    Finish("interrupted", "Concurrent Unity test jobs make global callback attribution ambiguous.");
                    return false;
                }
                return runners.Length == 1 && (bool)IsRunning.Invoke(null, new object[] { (string)active["unity_run_id"] });
            }

            public void RunStarted(ITestAdaptor tests) {}
            public void TestStarted(ITestAdaptor test) {}
            public void TestFinished(ITestResultAdaptor test) {}
            public void OnError(string message) { if (OwnsCallback()) Finish("failed", message); }
            public void RunFinished(ITestResultAdaptor result)
            {
                if (!OwnsCallback()) return;
                try
                {
                    var cases = new List<ITestResultAdaptor>();
                    Collect(result, cases);
                    string path = Path.Combine(DirectoryPath, (string)active["run_id"] + ".xml");
                    TestRunnerApi.SaveResultToFile(result, path);
                    var names = cases.Select(c => c.FullName).ToArray();
                    var filters = active["tests"].Values<string>().ToArray();
                    var missing = filters.Where(f => !names.Any(n => Matches(n, f))).ToArray();
                    bool unexpected = names.Any(n => !filters.Any(f => Matches(n, f)));
                    active["result_path"] = path;
                    active["test_count"] = names.Length;
                    active["test_names"] = new JArray(names.Take(50));
                    active["passed"] = result.PassCount;
                    active["failed"] = result.FailCount;
                    active["skipped"] = result.SkipCount;
                    if (missing.Length > 0 || unexpected)
                        Finish("failed", "Result does not match requested filters. Unmatched: " + string.Join(", ", missing));
                    else Finish(result.ResultState == "Passed" && result.PassCount > 0 ? "passed" : "failed", result.Message);
                }
                catch (Exception ex) { Finish("failed", ex.Message); }
            }

            private static bool Matches(string name, string filter) => name == filter ||
                name.StartsWith(filter + ".", StringComparison.Ordinal) || name.StartsWith(filter + "(", StringComparison.Ordinal);

            private static void Collect(ITestResultAdaptor node, List<ITestResultAdaptor> cases)
            {
                if (!node.Test.IsSuite) cases.Add(node);
                else foreach (var child in node.Children) Collect(child, cases);
            }
        }
    }
}
