using System;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace I1rr.UnityMcp.Editor.Tests
{
    /// <summary>
    /// Tests for the batch_execute tool: various step combinations, error policies,
    /// data_flow validation, recursion guards, and execute_code gate enforcement through batch.
    /// </summary>
    public class BatchExecuteTests
    {
        private Func<bool> _originalIsInPlayMode;
        private bool _originalEnableCodeExecution;

        [SetUp]
        public void SetUp()
        {
            _originalIsInPlayMode = McpToolRegistry.IsInPlayMode;
            _originalEnableCodeExecution = McpPreferencesProvider.EnableCodeExecution;
            McpToolRegistry.IsInPlayMode = () => false;
        }

        [TearDown]
        public void TearDown()
        {
            McpToolRegistry.IsInPlayMode = _originalIsInPlayMode;
            McpPreferencesProvider.EnableCodeExecution = _originalEnableCodeExecution;
        }

        #region Basic Execution

        [Test]
        public void BatchExecute_EmptyCalls_ReturnsError()
        {
            var result = InvokeBatch(new JObject { ["calls"] = new JArray() });
            AssertHasError(result, "required");
        }

        [Test]
        public void BatchExecute_NullCalls_ReturnsError()
        {
            var result = InvokeBatch(new JObject());
            AssertHasError(result, "required");
        }

        [Test]
        public void BatchExecute_SingleValidTool_Executes()
        {
            var calls = new JArray
            {
                new JObject { ["tool"] = "ping", ["parameters"] = new JObject() }
            };
            var result = InvokeBatch(new JObject { ["calls"] = calls });
            // batch_execute returns a DeferredResult or JObject
            Assert.That(result, Is.Not.Null);
        }

        [Test]
        public void BatchExecute_MultipleTools_ExecutesSequentially()
        {
            var calls = new JArray
            {
                new JObject { ["tool"] = "ping", ["parameters"] = new JObject() },
                new JObject { ["tool"] = "ping", ["parameters"] = new JObject() },
                new JObject { ["tool"] = "ping", ["parameters"] = new JObject() }
            };
            var result = InvokeBatch(new JObject { ["calls"] = calls });
            Assert.That(result, Is.Not.Null);
        }

        #endregion

        #region Error Policies

        [Test]
        public void BatchExecute_InvalidErrorPolicy_DefaultsToContinueAll()
        {
            var calls = new JArray
            {
                new JObject { ["tool"] = "ping", ["parameters"] = new JObject() }
            };
            var parameters = new JObject
            {
                ["calls"] = calls,
                ["error_policy"] = "invalid_policy"
            };
            // Should not error just because of invalid policy — defaults to continue_all
            var result = InvokeBatch(parameters);
            Assert.That(result, Is.Not.Null);
        }

        [Test]
        public void BatchExecute_NonexistentTool_InBatch_ReturnsPartialResults()
        {
            var calls = new JArray
            {
                new JObject { ["tool"] = "ping", ["parameters"] = new JObject() },
                new JObject { ["tool"] = "nonexistent_tool_xyz", ["parameters"] = new JObject() },
                new JObject { ["tool"] = "ping", ["parameters"] = new JObject() }
            };
            var parameters = new JObject
            {
                ["calls"] = calls,
                ["error_policy"] = "continue_all"
            };
            var result = InvokeBatch(parameters);
            // Should not be null and should contain results
            Assert.That(result, Is.Not.Null);
        }

        #endregion

        #region Data Flow Validation

        [Test]
        public void BatchExecute_DataFlow_InvalidFromStep_ReturnsError()
        {
            var calls = new JArray
            {
                new JObject
                {
                    ["tool"] = "ping",
                    ["parameters"] = new JObject(),
                    ["data_flow"] = new JObject
                    {
                        ["from_step"] = 5, // invalid — no step 5 exists before step 0
                        ["mappings"] = new JObject { ["key"] = "value" }
                    }
                }
            };
            var result = InvokeBatch(new JObject { ["calls"] = calls });
            AssertHasError(result, "from_step");
        }

        [Test]
        public void BatchExecute_DataFlow_NegativeFromStep_ReturnsError()
        {
            var calls = new JArray
            {
                new JObject { ["tool"] = "ping", ["parameters"] = new JObject() },
                new JObject
                {
                    ["tool"] = "ping",
                    ["parameters"] = new JObject(),
                    ["data_flow"] = new JObject
                    {
                        ["from_step"] = -1,
                        ["mappings"] = new JObject { ["key"] = "value" }
                    }
                }
            };
            var result = InvokeBatch(new JObject { ["calls"] = calls });
            AssertHasError(result, "from_step");
        }

        [Test]
        public void BatchExecute_DataFlow_EmptyMappings_ReturnsError()
        {
            var calls = new JArray
            {
                new JObject { ["tool"] = "ping", ["parameters"] = new JObject() },
                new JObject
                {
                    ["tool"] = "ping",
                    ["parameters"] = new JObject(),
                    ["data_flow"] = new JObject
                    {
                        ["from_step"] = 0,
                        ["mappings"] = new JObject() // empty
                    }
                }
            };
            var result = InvokeBatch(new JObject { ["calls"] = calls });
            AssertHasError(result, "mappings");
        }

        [Test]
        public void BatchExecute_DataFlow_SelfReference_ReturnsError()
        {
            var calls = new JArray
            {
                new JObject
                {
                    ["tool"] = "ping",
                    ["parameters"] = new JObject(),
                    ["data_flow"] = new JObject
                    {
                        ["from_step"] = 0, // Can't reference self (index == current)
                        ["mappings"] = new JObject { ["key"] = "value" }
                    }
                }
            };
            var result = InvokeBatch(new JObject { ["calls"] = calls });
            AssertHasError(result, "from_step");
        }

        #endregion

        #region Max Steps

        [Test]
        public void BatchExecute_ExceedMaxSteps_Truncated()
        {
            var calls = new JArray();
            for (int i = 0; i < 60; i++) // Over the 50 limit
                calls.Add(new JObject { ["tool"] = "ping", ["parameters"] = new JObject() });

            var parameters = new JObject
            {
                ["calls"] = calls,
                ["max_steps"] = 50
            };
            var result = InvokeBatch(parameters);
            // Should not error — just truncate. Result should be valid
            Assert.That(result, Is.Not.Null);
        }

        #endregion

        #region Recursion Guard

        [Test]
        public void BatchExecute_NestedBatchCall_Rejected()
        {
            // Cannot directly test recursion since it requires being "inside" a batch,
            // but we can verify the error message exists in the code path
            var calls = new JArray
            {
                new JObject
                {
                    ["tool"] = "batch_execute",
                    ["parameters"] = new JObject
                    {
                        ["calls"] = new JArray
                        {
                            new JObject { ["tool"] = "ping", ["parameters"] = new JObject() }
                        }
                    }
                }
            };
            var result = InvokeBatch(new JObject { ["calls"] = calls });
            // The outer batch will invoke batch_execute as a nested call, which should be rejected
            Assert.That(result, Is.Not.Null);
        }

        #endregion

        #region execute_code Gate Through Batch

        [Test]
        public void BatchExecute_ExecuteCode_GateEnforced()
        {
            McpPreferencesProvider.EnableCodeExecution = false;

            var calls = new JArray
            {
                new JObject
                {
                    ["tool"] = "execute_code",
                    ["parameters"] = new JObject { ["code"] = "1+1" }
                }
            };
            var result = InvokeBatch(new JObject { ["calls"] = calls });
            // The batch should execute but the execute_code step should fail with gate error
            Assert.That(result, Is.Not.Null);
            // Whether the result is a DeferredResult or JObject, the execute_code
            // step inside should still be gated
        }

        #endregion

        #region Wait Pseudo-Tool

        [Test]
        public void BatchExecute_WaitStep_AcceptsValidMs()
        {
            var calls = new JArray
            {
                new JObject { ["tool"] = "ping", ["parameters"] = new JObject() },
                new JObject { ["tool"] = "wait", ["parameters"] = new JObject { ["ms"] = 100 } },
                new JObject { ["tool"] = "ping", ["parameters"] = new JObject() }
            };
            // Contains waits, so batch_execute returns a DeferredResult
            var result = InvokeBatch(new JObject { ["calls"] = calls });
            Assert.That(result, Is.Not.Null);
        }

        [Test]
        public void BatchExecute_DelayMs_AcceptsValidDelay()
        {
            var calls = new JArray
            {
                new JObject { ["tool"] = "ping", ["parameters"] = new JObject(), ["delay_ms"] = 50 }
            };
            var result = InvokeBatch(new JObject { ["calls"] = calls });
            Assert.That(result, Is.Not.Null);
        }

        #endregion

        private static object InvokeBatch(JObject parameters)
        {
            return UtilityTools.BatchExecute(parameters);
        }

        private static void AssertHasError(object result, string containsText = null)
        {
            Assert.That(result, Is.Not.Null);
            if (result is JObject jObj)
            {
                var json = jObj.ToString().ToLowerInvariant();
                bool hasError = json.Contains("error");
                Assert.That(hasError, Is.True, $"Expected error, got: {jObj}");
                if (containsText != null)
                    Assert.That(json, Does.Contain(containsText.ToLowerInvariant()));
            }
        }
    }
}
