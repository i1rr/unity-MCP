using System.Collections.Generic;
using System.IO;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace I1rr.UnityMcp.Editor
{
    [McpToolCategory("Perforce")]
    public static class P4EditTools
    {
        #region p4_checkout

        [McpTool("p4_checkout",
            "Mark a file for edit (checkout) in Perforce.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""path"": { ""type"": ""string"", ""description"": ""Project-relative file path to checkout (e.g. 'Assets/Scripts/MyScript.cs')"" }
                },
                ""required"": [""path""]
            }")]
        public static JObject P4Checkout(JObject parameters)
        {
            var path = parameters["path"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(path))
                return Error("Parameter 'path' is required.");

            var validation = McpPathValidator.ValidatePath(path);
            if (!validation.IsValid)
                return Error($"Path validation failed: {validation.Error}");

            if (!File.Exists(validation.ResolvedPath))
                return Error($"File not found: {path}");

            var loginCheck = P4CommandRunner.PreflightLogin();
            if (loginCheck != null) return loginCheck;

            var result = P4CommandRunner.RunP4Command("edit", new[] { validation.ResolvedPath }, P4CommandRunner.SimpleTimeoutMs);
            if (!result.Success)
                return Error(result.Error);

            return new JObject
            {
                ["success"] = true,
                ["path"] = path,
                ["output"] = result.Output?.Trim()
            };
        }

        #endregion

        #region p4_revert

        [McpTool("p4_revert",
            "Revert a file in Perforce.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""path"": { ""type"": ""string"", ""description"": ""Project-relative file path to revert"" },
                    ""unchanged_only"": { ""type"": ""boolean"", ""description"": ""Only revert if file content is unchanged (default: false)"", ""default"": false }
                },
                ""required"": [""path""]
            }")]
        public static JObject P4Revert(JObject parameters)
        {
            var path = parameters["path"]?.Value<string>();
            bool unchangedOnly = parameters["unchanged_only"]?.Value<bool>() ?? false;

            if (string.IsNullOrWhiteSpace(path))
                return Error("Parameter 'path' is required.");

            var validation = McpPathValidator.ValidatePath(path);
            if (!validation.IsValid)
                return Error($"Path validation failed: {validation.Error}");

            var loginCheck = P4CommandRunner.PreflightLogin();
            if (loginCheck != null) return loginCheck;

            var args = new List<string>();
            if (unchangedOnly) args.Add("-a");
            args.Add(validation.ResolvedPath);

            var result = P4CommandRunner.RunP4Command("revert", args.ToArray(), P4CommandRunner.SimpleTimeoutMs);
            if (!result.Success)
                return Error(result.Error);

            return new JObject
            {
                ["success"] = true,
                ["path"] = path,
                ["unchanged_only"] = unchangedOnly,
                ["output"] = result.Output?.Trim()
            };
        }

        #endregion

        #region p4_submit

        [McpTool("p4_submit",
            "Submit a changelist. Requires confirm: true and shows a blocking confirmation dialog.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""changelist"": { ""type"": ""string"", ""description"": ""Changelist number to submit"" },
                    ""description"": { ""type"": ""string"", ""description"": ""Submit description"" },
                    ""confirm"": { ""type"": ""boolean"", ""description"": ""Must be true to proceed with submit. Safety mechanism."" }
                },
                ""required"": [""changelist"", ""description"", ""confirm""]
            }")]
        public static JObject P4Submit(JObject parameters)
        {
            var changelist = parameters["changelist"]?.Value<string>();
            var description = parameters["description"]?.Value<string>();
            bool confirm = parameters["confirm"]?.Value<bool>() ?? false;

            if (string.IsNullOrWhiteSpace(changelist))
                return Error("Parameter 'changelist' is required.");
            if (string.IsNullOrWhiteSpace(description))
                return Error("Parameter 'description' is required.");

            if (!confirm)
            {
                var err = McpError.Create(McpErrorCodes.ConfirmationRequired, "Submit requires 'confirm: true'. This is a safety mechanism. Set confirm to true to proceed.");
                err["success"] = false;
                err["changelist"] = changelist;
                err["description"] = description;
                return err;
            }

            var loginCheck = P4CommandRunner.PreflightLogin();
            if (loginCheck != null) return loginCheck;

            // Show blocking confirmation dialog to the developer
            bool userConfirmed = EditorUtility.DisplayDialog(
                "MCP: Confirm P4 Submit",
                $"Submit changelist {changelist}?\n\nDescription:\n{description}",
                "Submit",
                "Cancel");

            if (!userConfirmed)
            {
                var err = McpError.Create(McpErrorCodes.P4Error, "Submit cancelled by user via confirmation dialog.");
                err["success"] = false;
                err["changelist"] = changelist;
                return err;
            }

            Debug.LogWarning($"[MCP] P4 SUBMIT: {description}");

            var result = P4CommandRunner.RunP4Command("submit", new[] { "-c", changelist }, P4CommandRunner.DiffTimeoutMs);
            if (!result.Success)
                return Error(result.Error);

            return new JObject
            {
                ["success"] = true,
                ["changelist"] = changelist,
                ["description"] = description,
                ["output"] = result.Output?.Trim()
            };
        }

        #endregion

        private static JObject Error(string message)
        {
            var result = McpError.Create(McpErrorCodes.P4Error, message);
            result["success"] = false;
            return result;
        }
    }
}
