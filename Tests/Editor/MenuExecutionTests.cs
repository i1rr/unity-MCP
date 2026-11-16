using System;
using System.Text.RegularExpressions;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace I1rr.UnityMcp.Editor.Tests
{
    public sealed class MenuExecutionTests
    {
        private const string Prefix = "Tools/i1rr-unity-mcp/Test handlers/";
        private static int _calls;
        [MenuItem(Prefix + "Success")] private static void Success() => _calls++;
        [MenuItem(Prefix + "Throw")] private static void Throw() => throw new InvalidOperationException("mcp-menu-test-throw");
        [MenuItem(Prefix + "Error")] private static void Error() => Debug.LogError("mcp-menu-test-error");
        [MenuItem(Prefix + "Warning")] private static void Warning() => Debug.LogWarning("mcp-menu-test-warning");
        [MenuItem(Prefix + "Delete")] private static void Delete() => _calls++;
        [MenuItem(Prefix + "Flood")] private static void Flood()
        {
            for (int i = 0; i < 8; i++) Debug.LogError("mcp-menu-test-flood-" + new string('x', 2048));
        }

        private static JObject Run(string name, bool confirm = false) => EditorControlTools.ExecuteMenuItem(
            new JObject { ["path"] = Prefix + name, ["confirm"] = confirm });

        [Test] public void AcceptedHandlerReportsItsSynchronousScope()
        {
            int before = _calls;
            var result = Run("Success");
            Assert.That(_calls, Is.EqualTo(before + 1));
            Assert.That((string)result["status"], Is.EqualTo("executed"));
            Assert.That((string)result["completion_scope"], Is.EqualTo("synchronous_handler"));
        }

        [Test] public void LoggedExceptionCannotBeReportedAsSuccess()
        {
            LogAssert.Expect(LogType.Exception, new Regex("mcp-menu-test-throw"));
            var result = Run("Throw");
            Assert.That((string)result["error_code"], Is.EqualTo(McpErrorCodes.InternalError));
            Assert.That((string)result["status"], Is.EqualTo("failed"));
            Assert.That((bool)result["execution_may_have_started"], Is.True);
            Assert.That(result["handler_errors"].ToString(), Does.Contain("mcp-menu-test-throw"));
            Assert.That((string)Run("Success")["status"], Is.EqualTo("executed"));
        }

        [Test] public void LoggedErrorFailsWithoutNeedingAnException()
        {
            LogAssert.Expect(LogType.Error, "mcp-menu-test-error");
            var result = Run("Error");
            Assert.That((string)result["status"], Is.EqualTo("failed"));
            Assert.That((int)result["handler_error_count"], Is.EqualTo(1));
        }

        [Test] public void WarningAloneDoesNotFailTheCommand()
        {
            LogAssert.Expect(LogType.Warning, "mcp-menu-test-warning");
            Assert.That((string)Run("Warning")["status"], Is.EqualTo("executed"));
        }

        [Test] public void ErrorsAreBoundedAndTheirTotalIsRetained()
        {
            for (int i = 0; i < 8; i++) LogAssert.Expect(LogType.Error, new Regex("mcp-menu-test-flood-"));
            var result = Run("Flood");
            Assert.That((int)result["handler_error_count"], Is.EqualTo(8));
            var errors = (JArray)result["handler_errors"];
            Assert.That(errors.Count, Is.EqualTo(3));
            foreach (var error in errors)
            {
                Assert.That(((string)error["message"]).Length, Is.EqualTo(512));
                Assert.That((bool)error["truncated"], Is.True);
            }
        }

        [Test] public void DestructiveConfirmationStillPrecedesDispatch()
        {
            int before = _calls;
            Assert.That((string)Run("Delete")["error_code"], Is.EqualTo(McpErrorCodes.ConfirmationRequired));
            Assert.That(_calls, Is.EqualTo(before));
            Assert.That((string)Run("Delete", true)["status"], Is.EqualTo("executed"));
            Assert.That(_calls, Is.EqualTo(before + 1));
        }

        [Test] public void MissingPathStillReturnsMissingParameter()
        {
            Assert.That((string)EditorControlTools.ExecuteMenuItem(new JObject())["error_code"], Is.EqualTo(McpErrorCodes.MissingParameter));
        }
    }
}
