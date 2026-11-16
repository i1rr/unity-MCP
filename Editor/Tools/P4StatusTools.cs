using System;
using System.Collections.Generic;
using System.IO;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;

namespace I1rr.UnityMcp.Editor
{
    [McpToolCategory("Perforce")]
    public static class P4StatusTools
    {
        #region p4_status

        [McpTool("p4_status",
            "Get current Perforce workspace, client, and connection status.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {}
            }")]
        public static JObject P4Status(JObject parameters)
        {
            var loginCheck = P4CommandRunner.RunP4Command("login", new[] { "-s" }, P4CommandRunner.SimpleTimeoutMs);
            if (!loginCheck.Success)
            {
                var err = McpError.Create(McpErrorCodes.P4Error, loginCheck.Error ?? "Not logged in to Perforce");
                err["success"] = false;
                err["connected"] = false;
                return err;
            }

            var infoResult = P4CommandRunner.RunP4Command("info", Array.Empty<string>(), P4CommandRunner.SimpleTimeoutMs);
            if (!infoResult.Success)
            {
                var err = McpError.Create(McpErrorCodes.P4Error, infoResult.Error);
                err["success"] = false;
                return err;
            }

            var fields = P4CommandRunner.ParseTaggedOutput(infoResult.Output);

            return new JObject
            {
                ["success"] = true,
                ["connected"] = true,
                ["user"] = fields.GetValueOrDefault("userName"),
                ["client"] = fields.GetValueOrDefault("clientName"),
                ["client_root"] = fields.GetValueOrDefault("clientRoot"),
                ["server_address"] = fields.GetValueOrDefault("serverAddress"),
                ["server_version"] = fields.GetValueOrDefault("serverVersion"),
                ["case_handling"] = fields.GetValueOrDefault("caseHandling")
            };
        }

        #endregion

        #region p4_opened

        [McpTool("p4_opened",
            "List files currently checked out (opened for edit/add/delete).",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""changelist"": { ""type"": ""string"", ""description"": ""Filter by changelist number. Use 'default' for the default changelist."" }
                }
            }")]
        public static JObject P4Opened(JObject parameters)
        {
            var changelist = parameters["changelist"]?.Value<string>();

            var loginCheck = P4CommandRunner.PreflightLogin();
            if (loginCheck != null) return loginCheck;

            var args = new List<string>();
            if (!string.IsNullOrEmpty(changelist))
            {
                args.Add("-c");
                args.Add(changelist);
            }

            var result = P4CommandRunner.RunP4Command("opened", args.ToArray(), P4CommandRunner.SimpleTimeoutMs);
            if (!result.Success)
                return Error(result.Error);

            var files = new JArray();
            var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                // Format: //depot/path/file.ext#rev - action change NNNN (type)
                var parts = trimmed.Split(new[] { " - " }, 2, StringSplitOptions.None);
                var entry = new JObject { ["raw"] = trimmed };

                if (parts.Length >= 2)
                {
                    entry["depot_file"] = parts[0].Trim();
                    var actionPart = parts[1].Trim();
                    var actionTokens = actionPart.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (actionTokens.Length >= 1) entry["action"] = actionTokens[0];
                    if (actionTokens.Length >= 3) entry["changelist"] = actionTokens[2];
                }

                files.Add(entry);
            }

            return new JObject
            {
                ["success"] = true,
                ["count"] = files.Count,
                ["files"] = files
            };
        }

        #endregion

        #region p4_changes

        [McpTool("p4_changes",
            "List recent changelists.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""max"": { ""type"": ""integer"", ""description"": ""Maximum changelists to return (default 20, max 100)"", ""default"": 20 },
                    ""status"": { ""type"": ""string"", ""enum"": [""pending"", ""submitted"", ""shelved""], ""description"": ""Filter by status (default: all)"" }
                }
            }")]
        public static JObject P4Changes(JObject parameters)
        {
            int max = Math.Clamp(parameters["max"]?.Value<int>() ?? 20, 1, P4CommandRunner.MaxChangelists);
            var status = parameters["status"]?.Value<string>();

            var loginCheck = P4CommandRunner.PreflightLogin();
            if (loginCheck != null) return loginCheck;

            var args = new List<string> { "-m", max.ToString() };
            if (!string.IsNullOrEmpty(status))
            {
                args.Add("-s");
                args.Add(status);
            }

            var result = P4CommandRunner.RunP4TaggedCommand("changes", args.ToArray(), P4CommandRunner.SimpleTimeoutMs);
            if (!result.Success)
                return Error(result.Error);

            var changelists = new JArray();
            foreach (var record in result.Records)
            {
                changelists.Add(new JObject
                {
                    ["change"] = record.GetValueOrDefault("change"),
                    ["date"] = record.GetValueOrDefault("time"),
                    ["user"] = record.GetValueOrDefault("user"),
                    ["client"] = record.GetValueOrDefault("client"),
                    ["status"] = record.GetValueOrDefault("status"),
                    ["description"] = record.GetValueOrDefault("desc")?.Trim()
                });
            }

            return new JObject
            {
                ["success"] = true,
                ["count"] = changelists.Count,
                ["changelists"] = changelists
            };
        }

        #endregion

        #region p4_fstat

        [McpTool("p4_fstat",
            "Get detailed Perforce file status.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""path"": { ""type"": ""string"", ""description"": ""Project-relative file path to query"" }
                },
                ""required"": [""path""]
            }")]
        public static JObject P4Fstat(JObject parameters)
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

            var result = P4CommandRunner.RunP4TaggedCommand("fstat", new[] { validation.ResolvedPath }, P4CommandRunner.SimpleTimeoutMs);
            if (!result.Success)
                return Error(result.Error);

            if (result.Records.Count == 0)
                return Error($"No fstat data for: {path}");

            var record = result.Records[0];
            var fields = new JObject { ["success"] = true, ["path"] = path };

            foreach (var kv in record)
            {
                fields[kv.Key] = kv.Value;
            }

            return fields;
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
