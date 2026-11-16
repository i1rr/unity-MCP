using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace I1rr.UnityMcp.Editor
{
    [McpToolCategory("Utility")]
    public static class UtilityTools
    {
        private const int DefaultExecuteTimeoutMs = 5000;
        private const int MinExecuteTimeoutMs = 100;
        private const int MaxExecuteTimeoutMs = 30000;
        private const int DefaultMaxCalls = 50;
        private const int DefaultMaxDurationMs = 55000;

        [ThreadStatic] private static bool _insideBatch;

        #region execute_code

        [McpTool("execute_code",
            "Compile and execute arbitrary C# code via Mono.CSharp.Evaluator on the main thread. C# 7.3 max. Fresh instance per call. Pre-imports UnityEngine, UnityEditor, System, System.Linq, System.Collections.Generic. No state carries between calls. Unity API calls are fully supported.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""code"": { ""type"": ""string"", ""description"": ""C# code to compile and execute."" },
                    ""timeout_ms"": { ""type"": ""integer"", ""description"": ""Max execution time in milliseconds (default 5000, clamped to [100, 30000])."" , ""default"": 5000 }
                },
                ""required"": [""code""]
            }")]
        public static JObject ExecuteCode(JObject parameters)
        {
            var code = parameters["code"]?.Value<string>();

            // Log all code execution invocations regardless of gate state
            UnityEngine.Debug.Log($"[MCP] execute_code invoked. Code length: {code?.Length ?? 0} chars. Enabled: {McpPreferencesProvider.EnableCodeExecution}");

            // Gate check — must be inside the tool method to prevent batch_execute bypass
            if (!McpPreferencesProvider.EnableCodeExecution)
            {
                var err = McpError.Create(McpErrorCodes.Disabled, "execute_code is disabled by default for security. " +
                                "Enable via Project Settings > i1rr-unity-mcp > Security > Enable Code Execution. " +
                                "WARNING: This grants full Editor access to MCP clients. " +
                                "Known limitation: code runs synchronously on the main thread and cannot be interrupted mid-execution.");
                err["success"] = false;
                return err;
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                var err = McpError.Create(McpErrorCodes.MissingParameter, "Parameter 'code' is required and must not be empty.");
                err["success"] = false;
                return err;
            }

            var rawTimeout = parameters["timeout_ms"]?.Value<int>() ?? DefaultExecuteTimeoutMs;
            var timeoutMs = Math.Min(Math.Max(rawTimeout, MinExecuteTimeoutMs), MaxExecuteTimeoutMs);
            bool wasClamped = rawTimeout != timeoutMs;

            var sw = Stopwatch.StartNew();

            try
            {
                var result = ExecuteWithEvaluator(code, timeoutMs);
                sw.Stop();
                result["elapsed_ms"] = sw.ElapsedMilliseconds;
                result["executed_on_main_thread"] = true;
                if (wasClamped)
                    result["timeout_ms_used"] = timeoutMs;
                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                var err = McpError.Create(McpErrorCodes.InternalError, ex.Message);
                err["success"] = false;
                err["elapsed_ms"] = sw.ElapsedMilliseconds;
                err["executed_on_main_thread"] = true;
                return err;
            }
        }

        private static JObject ExecuteWithEvaluator(string code, int timeoutMs)
        {
            // Use reflection to access Mono.CSharp.Evaluator which is available in Unity Editor
            var monoAssembly = FindMonoCSharpAssembly();
            if (monoAssembly == null)
            {
                var err = McpError.Create(McpErrorCodes.InternalError, "Mono.CSharp assembly not found. This tool requires the Unity Editor's Mono runtime.");
                err["success"] = false;
                return err;
            }

            var evaluatorType = monoAssembly.GetType("Mono.CSharp.Evaluator");
            var compilerSettingsType = monoAssembly.GetType("Mono.CSharp.CompilerSettings");
            var compilerContextType = monoAssembly.GetType("Mono.CSharp.CompilerContext");
            var reporterType = monoAssembly.GetType("Mono.CSharp.StreamReportPrinter");

            if (evaluatorType == null || compilerSettingsType == null || compilerContextType == null)
            {
                var err = McpError.Create(McpErrorCodes.InternalError, "Required Mono.CSharp types not found in assembly.");
                err["success"] = false;
                return err;
            }

            // Redirect output
            var outputCapture = new StringWriter();
            var errorCapture = new StringWriter();

            object evaluator;
            object reporter = null;
            try
            {
                var settings = Activator.CreateInstance(compilerSettingsType);
                var reporterCtor = reporterType.GetConstructor(new[] { typeof(TextWriter) });
                reporter = reporterCtor.Invoke(new object[] { errorCapture });

                var contextCtor = compilerContextType.GetConstructor(new[] { compilerSettingsType, reporterType.BaseType ?? reporterType });
                if (contextCtor == null)
                {
                    // Try finding the correct constructor
                    var ctors = compilerContextType.GetConstructors();
                    foreach (var ctor in ctors)
                    {
                        var cParams = ctor.GetParameters();
                        if (cParams.Length == 2)
                        {
                            contextCtor = ctor;
                            break;
                        }
                    }
                }

                var context = contextCtor.Invoke(new[] { settings, reporter });

                var evalCtor = evaluatorType.GetConstructor(new[] { compilerContextType });
                evaluator = evalCtor.Invoke(new[] { context });
            }
            catch (Exception ex)
            {
                var err = McpError.Create(McpErrorCodes.InternalError, $"Failed to create Evaluator: {ex.Message}");
                err["success"] = false;
                return err;
            }

            // Reference assemblies
            var referenceMethod = evaluatorType.GetMethod("ReferenceAssembly", new[] { typeof(Assembly) });
            if (referenceMethod != null)
            {
                foreach (var asm in I1rr.UnityMcp.Shared.UnityCompatibility.GetLoadedAssemblies())
                {
                    try
                    {
                        if (!asm.IsDynamic && !string.IsNullOrEmpty(UnityCompatibility.GetLoadedAssemblyPath(asm)))
                            referenceMethod.Invoke(evaluator, new object[] { asm });
                    }
                    catch
                    {
                        // Skip assemblies that can't be referenced
                    }
                }
            }

            // Pre-import usings
            var runMethod = evaluatorType.GetMethod("Run", new[] { typeof(string) });
            if (runMethod != null)
            {
                var usings = new[]
                {
                    "using UnityEngine;",
                    "using UnityEditor;",
                    "using System;",
                    "using System.Linq;",
                    "using System.Collections.Generic;",
                    "using System.IO;"
                };
                foreach (var u in usings)
                {
                    try { runMethod.Invoke(evaluator, new object[] { u }); }
                    catch { /* ignore using failures */ }
                }
            }

            // Execute on the main thread so Unity API calls work correctly.
            // The MCP server dispatches tool handlers on the main thread via EditorApplication.update,
            // so Evaluator.Evaluate() runs synchronously here with full Unity API access.
            var evaluateMethod = evaluatorType.GetMethod("Evaluate", new[] { typeof(string), typeof(object).MakeByRefType(), typeof(bool).MakeByRefType() });
            var oldOut = Console.Out;
            var oldErr = Console.Error;

            object returnValue = null;
            string evalError = null;

            try
            {
                Console.SetOut(outputCapture);
                Console.SetError(errorCapture);

                try
                {
                    if (evaluateMethod != null)
                    {
                        var evalParams = new object[] { code, null, null };
                        var evalResult = evaluateMethod.Invoke(evaluator, evalParams);
                        returnValue = evalParams[1];

                        // If Evaluate returned non-null string, it's a partial input indicator
                        if (evalResult is string partial && !string.IsNullOrEmpty(partial))
                        {
                            evalError = "Incomplete expression (missing closing bracket or semicolon).";
                        }
                    }
                    else if (runMethod != null)
                    {
                        // Fallback to Run
                        runMethod.Invoke(evaluator, new object[] { code });
                    }
                    else
                    {
                        evalError = "No suitable execution method found on Evaluator.";
                    }
                }
                catch (TargetInvocationException tie)
                {
                    evalError = (tie.InnerException ?? tie).Message;
                }
                catch (Exception ex)
                {
                    evalError = ex.Message;
                }
            }
            finally
            {
                Console.SetOut(oldOut);
                Console.SetError(oldErr);
            }

            var output = outputCapture.ToString();

            // The reporter writes warnings and errors to the same writer, and Unity's Mono setup
            // reports every predefined type as duplicated (CS1685) on each evaluation. Treating a
            // non-empty stream as failure would fail every successful call, so ask the reporter how
            // many real errors it saw and keep that noise out of the message.
            var errors = StripNoiseWarnings(errorCapture.ToString());

            if (string.IsNullOrEmpty(evalError) && HasCompilerErrors(reporter, errors))
                evalError = string.IsNullOrEmpty(errors) ? "Compilation failed." : errors;

            if (evalError != null)
            {
                var err = McpError.Create(McpErrorCodes.CompilationError, evalError);
                err["success"] = false;
                err["output"] = string.IsNullOrEmpty(output) ? null : output;
                return err;
            }

            var result = new JObject
            {
                ["success"] = true,
                ["output"] = string.IsNullOrEmpty(output) ? null : output
            };

            if (returnValue != null)
            {
                try
                {
                    result["return_value"] = JToken.FromObject(returnValue);
                }
                catch
                {
                    result["return_value"] = returnValue.ToString();
                }
            }

            return result;
        }

        /// <summary>
        /// Drops the CS1685 "predefined type is defined multiple times" warnings (and their location
        /// lines) that Unity's duplicate mscorlib/System references produce on every evaluation.
        /// Real errors and any other warning are kept.
        /// </summary>
        private static string StripNoiseWarnings(string diagnostics)
        {
            if (string.IsNullOrEmpty(diagnostics))
                return diagnostics;

            var kept = new List<string>();
            bool lastWasNoise = false;

            foreach (var rawLine in diagnostics.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (line.Length == 0)
                    continue;

                if (line.Contains("warning CS1685"))
                {
                    lastWasNoise = true;
                    continue;
                }

                if (lastWasNoise && line.Contains("related to previous warning"))
                    continue;

                lastWasNoise = false;
                kept.Add(line);
            }

            return string.Join(Environment.NewLine, kept);
        }

        /// <summary>
        /// True when the evaluation produced real compiler errors. Prefers the reporter's own error
        /// count and falls back to inspecting the diagnostic text.
        /// </summary>
        private static bool HasCompilerErrors(object reporter, string diagnostics)
        {
            if (reporter != null)
            {
                try
                {
                    var countProperty = reporter.GetType().GetProperty("ErrorsCount");
                    if (countProperty != null && countProperty.PropertyType == typeof(int))
                        return (int)countProperty.GetValue(reporter) > 0;
                }
                catch
                {
                    // Reporter shape differs from what we expect; fall through to text inspection.
                }
            }

            return diagnostics != null && diagnostics.Contains("error CS");
        }

        private static Assembly FindMonoCSharpAssembly()
        {
            foreach (var asm in I1rr.UnityMcp.Shared.UnityCompatibility.GetLoadedAssemblies())
            {
                if (asm.GetName().Name == "Mono.CSharp")
                    return asm;
            }

            // Try loading it explicitly
            try
            {
                return Assembly.Load("Mono.CSharp");
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region batch_execute

        private const int WaitMinMs = 1;
        private const int WaitMaxMs = 30000;

        [McpTool("batch_execute",
            @"Execute multiple tool calls in one round-trip. All steps execute sequentially on the main thread — parallel execution is architecturally impossible since all Unity APIs require main thread. Max 50 steps per batch. Timeout cap: 55000ms. Returns partial results on timeout. Recursion guard: nested batch calls are rejected.

Supports 'data_flow' on each step to pipe output from a previous step into this step's parameters (simple key mapping, no expression evaluation). Piped values pass through the target tool's normal parameter validation. Deferred tools cannot be used in batches that use data_flow.

Error policies via 'error_policy':
  - 'continue_all' (default): run all steps; data-flow dependents of failed steps are skipped with 'skipped_dependency'.
  - 'abort_on_first': stop on the first error and return partial results.
  - 'skip_failed': like continue_all; dependents of failed steps are skipped.

Supports a 'wait' pseudo-tool to insert timed delays between calls:
  { ""tool"": ""wait"", ""parameters"": { ""ms"": 3000 } }

Also supports an optional 'delay_ms' field on any call entry as a pre-call delay:
  { ""tool"": ""get_fps_realtime"", ""parameters"": {}, ""delay_ms"": 500 }

Each step result includes 'duration_ms' for execution timing.",
            DeferredResponse = true,
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""calls"": {
                        ""type"": ""array"",
                        ""description"": ""Array of tool calls to execute sequentially. Max 50 steps."",
                        ""items"": {
                            ""type"": ""object"",
                            ""properties"": {
                                ""tool"": { ""type"": ""string"", ""description"": ""Tool name to invoke, or 'wait' for a timed delay."" },
                                ""parameters"": { ""type"": ""object"", ""description"": ""Parameters to pass to the tool. For 'wait': { 'ms': <int> }."" },
                                ""delay_ms"": { ""type"": ""integer"", ""description"": ""Optional pre-call delay in milliseconds (1-30000). Applied before executing this step."" },
                                ""data_flow"": {
                                    ""type"": ""object"",
                                    ""description"": ""Pipe output from a previous step into this step's parameters. Piped values pass through normal parameter validation."",
                                    ""properties"": {
                                        ""from_step"": { ""type"": ""integer"", ""description"": ""0-based index of the source step whose result to read from."" },
                                        ""mappings"": { ""type"": ""object"", ""description"": ""Key mapping: { source_result_key: target_parameter_name }. Values are extracted from the source step's result."" }
                                    },
                                    ""required"": [""from_step"", ""mappings""]
                                }
                            },
                            ""required"": [""tool""]
                        }
                    },
                    ""error_policy"": { ""type"": ""string"", ""enum"": [""continue_all"", ""abort_on_first"", ""skip_failed""], ""description"": ""Error handling policy (default: continue_all)."", ""default"": ""continue_all"" },
                    ""max_steps"": { ""type"": ""integer"", ""description"": ""Maximum number of steps to execute (default 50, max 50)."", ""default"": 50 },
                    ""max_duration_ms"": { ""type"": ""integer"", ""description"": ""Maximum total duration in milliseconds (default 55000)."", ""default"": 55000 }
                },
                ""required"": [""calls""]
            }")]
        public static object BatchExecute(JObject parameters)
        {
            // Recursion guard
            if (_insideBatch)
            {
                var err = McpError.Create(McpErrorCodes.InvalidParameter, "Nested batch_execute calls are not allowed.");
                err["code"] = -32600;
                return err;
            }

            var callsToken = parameters["calls"] as JArray;
            if (callsToken == null || callsToken.Count == 0)
            {
                var err = McpError.Create(McpErrorCodes.MissingParameter, "Parameter 'calls' is required and must be a non-empty array.");
                err["code"] = -32602;
                return err;
            }

            var maxSteps = Math.Clamp(
                parameters["max_steps"]?.Value<int>() ?? parameters["max_calls"]?.Value<int>() ?? DefaultMaxCalls,
                1, DefaultMaxCalls);
            var maxDurationMs = Math.Clamp(parameters["max_duration_ms"]?.Value<int>() ?? DefaultMaxDurationMs, 1000, DefaultMaxDurationMs);

            var errorPolicy = parameters["error_policy"]?.Value<string>() ?? "continue_all";
            if (errorPolicy != "continue_all" && errorPolicy != "abort_on_first" && errorPolicy != "skip_failed")
                errorPolicy = "continue_all";

            var calls = new List<JObject>();
            for (int i = 0; i < Math.Min(callsToken.Count, maxSteps); i++)
            {
                if (callsToken[i] is JObject call)
                    calls.Add(call);
            }

            if (calls.Count == 0)
            {
                var err = McpError.Create(McpErrorCodes.InvalidParameter, "No valid call objects found in 'calls' array.");
                err["code"] = -32602;
                return err;
            }

            // Validate data_flow references and detect deferred tool conflicts
            bool hasDataFlow = false;
            for (int i = 0; i < calls.Count; i++)
            {
                var dfToken = calls[i]["data_flow"];
                if (dfToken == null) continue;
                hasDataFlow = true;

                var df = dfToken as JObject;
                int fromStep = df?["from_step"]?.Value<int>() ?? -1;
                if (fromStep < 0 || fromStep >= i)
                {
                    var err = McpError.Create(McpErrorCodes.InvalidParameter,
                        $"Step {i}: data_flow.from_step must reference a previous step (0 to {i - 1}), got {fromStep}.");
                    err["code"] = -32602;
                    return err;
                }

                var mappings = df?["mappings"] as JObject;
                if (mappings == null || !mappings.HasValues)
                {
                    var err = McpError.Create(McpErrorCodes.InvalidParameter,
                        $"Step {i}: data_flow.mappings must be a non-empty object.");
                    err["code"] = -32602;
                    return err;
                }
            }

            // Reject deferred tools in batches that use data_flow
            if (hasDataFlow)
            {
                for (int i = 0; i < calls.Count; i++)
                {
                    var toolName = calls[i]["tool"]?.Value<string>();
                    if (!string.IsNullOrEmpty(toolName) && toolName != "wait" && McpToolRegistry.IsDeferredTool(toolName))
                    {
                        var err = McpError.Create(McpErrorCodes.InvalidParameter,
                            $"Step {i}: tool '{toolName}' uses deferred responses and cannot be used in batches with data_flow. " +
                            "Deferred tools do not produce immediate results that can be piped to subsequent steps.");
                        err["code"] = -32602;
                        return err;
                    }
                }
            }

            // Check if any calls contain waits or delays that require deferred execution
            bool hasWaits = false;
            for (int i = 0; i < calls.Count; i++)
            {
                if (calls[i]["tool"]?.Value<string>() == "wait" || calls[i]["delay_ms"] != null)
                {
                    hasWaits = true;
                    break;
                }
            }

            if (!hasWaits)
            {
                // Fast path: synchronous execution (no waits, no delays)
                return ExecuteBatchSynchronous(calls, maxDurationMs, errorPolicy);
            }

            // Deferred path: coroutine-style execution across frames so waits yield the main thread
            var state = new BatchDeferredState
            {
                Calls = calls,
                MaxDurationMs = maxDurationMs,
                ErrorPolicy = errorPolicy,
                Sw = Stopwatch.StartNew(),
                Results = new JArray(),
                StepResults = new Dictionary<int, JObject>(),
                FailedOrSkippedSteps = new HashSet<int>(),
                CurrentIndex = 0
            };

            return new DeferredResult
            {
                TimeoutSeconds = Math.Min(60f, (float)(maxDurationMs / 1000.0) + 5f),
                IsComplete = () => AdvanceBatch(state),
                GetResult = () => state.FinalResult
            };
        }

        /// <summary>
        /// Applies data_flow mappings to a step's parameters by extracting values from a source step's result.
        /// Returns null if the source step failed/was skipped (caller should mark as skipped_dependency).
        /// </summary>
        private static JObject ApplyDataFlow(JObject call, int stepIndex,
            Dictionary<int, JObject> stepResults, HashSet<int> failedOrSkippedSteps,
            out int fromStep)
        {
            var dataFlow = call["data_flow"] as JObject;
            fromStep = -1;
            if (dataFlow == null)
                return call["parameters"] as JObject ?? call["params"] as JObject ?? new JObject();

            fromStep = dataFlow["from_step"].Value<int>();
            var mappings = dataFlow["mappings"] as JObject;

            // Check if source step failed or was skipped
            if (failedOrSkippedSteps.Contains(fromStep))
                return null;

            if (!stepResults.TryGetValue(fromStep, out var sourceResult) || sourceResult == null)
                return null;

            // Start with existing parameters (deep clone to avoid mutating the original call)
            var toolParams = (JObject)(call["parameters"] as JObject ?? call["params"] as JObject ?? new JObject()).DeepClone();

            // Apply mappings: source_key → target_param
            foreach (var mapping in mappings.Properties())
            {
                var sourceKey = mapping.Name;
                var targetParam = mapping.Value.Value<string>();
                if (string.IsNullOrEmpty(targetParam)) continue;

                var value = sourceResult[sourceKey];
                if (value != null)
                    toolParams[targetParam] = value.DeepClone();
            }

            return toolParams;
        }

        private static JObject ExecuteBatchSynchronous(List<JObject> calls, long maxDurationMs, string errorPolicy)
        {
            var sw = Stopwatch.StartNew();
            var results = new JArray();
            var stepResults = new Dictionary<int, JObject>();
            var failedOrSkippedSteps = new HashSet<int>();
            bool aborted = false;

            _insideBatch = true;
            try
            {
                for (int i = 0; i < calls.Count; i++)
                {
                    if (sw.ElapsedMilliseconds >= maxDurationMs)
                    {
                        sw.Stop();
                        return new JObject
                        {
                            ["completed"] = results,
                            ["timed_out_at_index"] = i,
                            ["remaining_count"] = calls.Count - i,
                            ["elapsed_ms"] = sw.ElapsedMilliseconds
                        };
                    }

                    if (aborted) break;

                    var call = calls[i];
                    var toolName = call["tool"]?.Value<string>();
                    var stepSw = Stopwatch.StartNew();

                    if (string.IsNullOrEmpty(toolName))
                    {
                        stepSw.Stop();
                        results.Add(new JObject
                        {
                            ["index"] = i,
                            ["error"] = "Missing 'tool' property in call object.",
                            ["duration_ms"] = stepSw.Elapsed.TotalMilliseconds
                        });
                        failedOrSkippedSteps.Add(i);
                        if (errorPolicy == "abort_on_first") aborted = true;
                        continue;
                    }

                    // Apply data_flow if present
                    JObject toolParams;
                    if (call["data_flow"] != null)
                    {
                        toolParams = ApplyDataFlow(call, i, stepResults, failedOrSkippedSteps, out int fromStep);
                        if (toolParams == null)
                        {
                            stepSw.Stop();
                            results.Add(new JObject
                            {
                                ["index"] = i,
                                ["tool"] = toolName,
                                ["status"] = "skipped_dependency",
                                ["error"] = $"Skipped: source step {fromStep} failed or was skipped.",
                                ["duration_ms"] = stepSw.Elapsed.TotalMilliseconds
                            });
                            failedOrSkippedSteps.Add(i);
                            continue;
                        }
                    }
                    else
                    {
                        toolParams = call["parameters"] as JObject ?? call["params"] as JObject ?? new JObject();
                    }

                    try
                    {
                        var toolResult = McpToolRegistry.Invoke(toolName, toolParams);
                        stepSw.Stop();
                        results.Add(new JObject
                        {
                            ["index"] = i,
                            ["tool"] = toolName,
                            ["result"] = toolResult,
                            ["duration_ms"] = stepSw.Elapsed.TotalMilliseconds
                        });
                        stepResults[i] = toolResult;

                        if (toolResult["error"] != null)
                        {
                            failedOrSkippedSteps.Add(i);
                            if (errorPolicy == "abort_on_first") aborted = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        stepSw.Stop();
                        results.Add(new JObject
                        {
                            ["index"] = i,
                            ["tool"] = toolName,
                            ["error"] = ex.Message,
                            ["duration_ms"] = stepSw.Elapsed.TotalMilliseconds
                        });
                        failedOrSkippedSteps.Add(i);
                        if (errorPolicy == "abort_on_first") aborted = true;
                    }
                }
            }
            finally
            {
                _insideBatch = false;
            }

            sw.Stop();
            var finalResult = new JObject
            {
                ["results"] = results,
                ["total_calls"] = calls.Count,
                ["elapsed_ms"] = sw.ElapsedMilliseconds
            };
            if (aborted)
                finalResult["aborted"] = true;
            return finalResult;
        }

        /// <summary>
        /// State for deferred batch execution across multiple main-thread ticks.
        /// </summary>
        private class BatchDeferredState
        {
            public List<JObject> Calls;
            public JArray Results;
            public Stopwatch Sw;
            public long MaxDurationMs;
            public string ErrorPolicy;
            public int CurrentIndex;
            public bool IsWaiting;
            public long WaitUntilElapsedMs;
            public long WaitStartedAtMs;
            public int WaitIndex;
            public int WaitRequestedMs;
            public bool WaitClamped;
            public bool PreCallDelayActive;
            public JObject FinalResult;
            public bool Complete;
            public bool Aborted;
            public Dictionary<int, JObject> StepResults;
            public HashSet<int> FailedOrSkippedSteps;
            public long StepStartMs;
        }

        /// <summary>
        /// Advances the deferred batch by one or more steps. Called on the main thread from
        /// DeferredResult.IsComplete each frame. Returns true when the batch is complete.
        /// Waits yield to the caller so frames can advance between calls.
        /// </summary>
        private static bool AdvanceBatch(BatchDeferredState state)
        {
            if (state.Complete) return true;

            // If waiting, check if the wait period has elapsed
            if (state.IsWaiting)
            {
                if (state.Sw.ElapsedMilliseconds < state.WaitUntilElapsedMs)
                    return false;

                // Wait complete — record actual elapsed time in the result
                long actualMs = state.Sw.ElapsedMilliseconds - state.WaitStartedAtMs;
                if (!state.PreCallDelayActive)
                {
                    // This was a 'wait' pseudo-tool; add result entry
                    var waitResult = new JObject
                    {
                        ["index"] = state.WaitIndex,
                        ["tool"] = "wait",
                        ["success"] = true,
                        ["waited_ms"] = actualMs,
                        ["duration_ms"] = actualMs,
                        ["result"] = null
                    };
                    if (state.WaitClamped)
                        waitResult["clamped"] = true;
                    state.Results.Add(waitResult);
                }

                state.IsWaiting = false;
                // PreCallDelayActive is intentionally NOT reset here — it is reset inside
                // the main loop after the delay_ms check so the delay is not re-applied.
            }

            _insideBatch = true;
            try
            {
                while (state.CurrentIndex < state.Calls.Count)
                {
                    // Check timeout
                    if (state.Sw.ElapsedMilliseconds >= state.MaxDurationMs)
                    {
                        state.FinalResult = new JObject
                        {
                            ["completed"] = state.Results,
                            ["timed_out_at_index"] = state.CurrentIndex,
                            ["remaining_count"] = state.Calls.Count - state.CurrentIndex,
                            ["elapsed_ms"] = state.Sw.ElapsedMilliseconds
                        };
                        state.Complete = true;
                        return true;
                    }

                    if (state.Aborted) break;

                    var call = state.Calls[state.CurrentIndex];
                    var toolName = call["tool"]?.Value<string>();

                    // Handle delay_ms (pre-call delay) — enter wait before executing the tool
                    var delayToken = call["delay_ms"];
                    if (delayToken != null && toolName != "wait" && !state.PreCallDelayActive)
                    {
                        int delayMs = Math.Clamp(delayToken.Value<int>(), WaitMinMs, WaitMaxMs);
                        state.WaitStartedAtMs = state.Sw.ElapsedMilliseconds;
                        state.WaitUntilElapsedMs = state.WaitStartedAtMs + delayMs;
                        state.IsWaiting = true;
                        state.PreCallDelayActive = true;
                        return false; // Yield to let frames advance
                    }
                    state.PreCallDelayActive = false;

                    state.StepStartMs = state.Sw.ElapsedMilliseconds;

                    if (string.IsNullOrEmpty(toolName))
                    {
                        long stepDuration = state.Sw.ElapsedMilliseconds - state.StepStartMs;
                        state.Results.Add(new JObject
                        {
                            ["index"] = state.CurrentIndex,
                            ["error"] = "Missing 'tool' property in call object.",
                            ["duration_ms"] = stepDuration
                        });
                        state.FailedOrSkippedSteps.Add(state.CurrentIndex);
                        if (state.ErrorPolicy == "abort_on_first") state.Aborted = true;
                        state.CurrentIndex++;
                        continue;
                    }

                    // Handle 'wait' pseudo-tool
                    if (toolName == "wait")
                    {
                        var waitParams = call["parameters"] as JObject ?? call["params"] as JObject;
                        int requestedMs = waitParams?["ms"]?.Value<int>() ?? 0;

                        if (requestedMs <= 0)
                        {
                            long stepDuration = state.Sw.ElapsedMilliseconds - state.StepStartMs;
                            state.Results.Add(new JObject
                            {
                                ["index"] = state.CurrentIndex,
                                ["tool"] = "wait",
                                ["error"] = "Parameter 'ms' is required and must be a positive integer.",
                                ["duration_ms"] = stepDuration
                            });
                            state.FailedOrSkippedSteps.Add(state.CurrentIndex);
                            if (state.ErrorPolicy == "abort_on_first") state.Aborted = true;
                            state.CurrentIndex++;
                            continue;
                        }

                        bool clamped = requestedMs < WaitMinMs || requestedMs > WaitMaxMs;
                        int clampedMs = Math.Clamp(requestedMs, WaitMinMs, WaitMaxMs);

                        state.WaitIndex = state.CurrentIndex;
                        state.WaitRequestedMs = clampedMs;
                        state.WaitClamped = clamped;
                        state.WaitStartedAtMs = state.Sw.ElapsedMilliseconds;
                        state.WaitUntilElapsedMs = state.WaitStartedAtMs + clampedMs;
                        state.IsWaiting = true;
                        state.PreCallDelayActive = false;
                        state.CurrentIndex++;
                        return false; // Yield to let frames advance
                    }

                    // Apply data_flow if present
                    JObject toolParams;
                    if (call["data_flow"] != null)
                    {
                        toolParams = ApplyDataFlow(call, state.CurrentIndex, state.StepResults, state.FailedOrSkippedSteps, out int fromStep);
                        if (toolParams == null)
                        {
                            long stepDuration = state.Sw.ElapsedMilliseconds - state.StepStartMs;
                            state.Results.Add(new JObject
                            {
                                ["index"] = state.CurrentIndex,
                                ["tool"] = toolName,
                                ["status"] = "skipped_dependency",
                                ["error"] = $"Skipped: source step {fromStep} failed or was skipped.",
                                ["duration_ms"] = stepDuration
                            });
                            state.FailedOrSkippedSteps.Add(state.CurrentIndex);
                            state.CurrentIndex++;
                            continue;
                        }
                    }
                    else
                    {
                        toolParams = call["parameters"] as JObject ?? call["params"] as JObject ?? new JObject();
                    }

                    // Regular tool call
                    try
                    {
                        var toolResult = McpToolRegistry.Invoke(toolName, toolParams);
                        long stepDuration = state.Sw.ElapsedMilliseconds - state.StepStartMs;
                        state.Results.Add(new JObject
                        {
                            ["index"] = state.CurrentIndex,
                            ["tool"] = toolName,
                            ["result"] = toolResult,
                            ["duration_ms"] = stepDuration
                        });
                        state.StepResults[state.CurrentIndex] = toolResult;

                        if (toolResult["error"] != null)
                        {
                            state.FailedOrSkippedSteps.Add(state.CurrentIndex);
                            if (state.ErrorPolicy == "abort_on_first") state.Aborted = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        long stepDuration = state.Sw.ElapsedMilliseconds - state.StepStartMs;
                        state.Results.Add(new JObject
                        {
                            ["index"] = state.CurrentIndex,
                            ["tool"] = toolName,
                            ["error"] = ex.Message,
                            ["duration_ms"] = stepDuration
                        });
                        state.FailedOrSkippedSteps.Add(state.CurrentIndex);
                        if (state.ErrorPolicy == "abort_on_first") state.Aborted = true;
                    }

                    state.CurrentIndex++;
                }

                // All steps complete (or aborted)
                state.Sw.Stop();
                state.FinalResult = new JObject
                {
                    ["results"] = state.Results,
                    ["total_calls"] = state.Calls.Count,
                    ["elapsed_ms"] = state.Sw.ElapsedMilliseconds
                };
                if (state.Aborted)
                    state.FinalResult["aborted"] = true;
                state.Complete = true;
                return true;
            }
            finally
            {
                _insideBatch = false;
            }
        }

        #endregion

        #region get_job_status

        [McpTool("get_job_status",
            "Poll the status of a long-running operation by job_id. Returns progress, result, or error.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""job_id"": { ""type"": ""string"", ""description"": ""The job ID returned by the tool that started the operation."" }
                },
                ""required"": [""job_id""]
            }")]
        public static JObject GetJobStatus(JObject parameters)
        {
            var jobId = parameters["job_id"]?.Value<string>();
            if (string.IsNullOrEmpty(jobId))
            {
                var err = McpError.Create(McpErrorCodes.MissingParameter, "Parameter 'job_id' is required.");
                err["code"] = -32602;
                return err;
            }

            var status = JobManager.GetJobStatus(jobId);
            if (status == null)
            {
                return new JObject
                {
                    ["error"] = $"Unknown job_id: {jobId}",
                    ["code"] = -32602
                };
            }

            return status;
        }

        #endregion
    }
}
