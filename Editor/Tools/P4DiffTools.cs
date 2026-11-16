using System;
using System.Collections.Generic;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;

namespace I1rr.UnityMcp.Editor
{
    [McpToolCategory("Perforce")]
    public static class P4DiffTools
    {
        #region p4_diff

        [McpTool("p4_diff",
            "Get unified diff for a file or changelist. Output truncated at max_lines.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""path"": { ""type"": ""string"", ""description"": ""Project-relative file path to diff"" },
                    ""changelist"": { ""type"": ""string"", ""description"": ""Changelist number to diff (alternative to path)"" },
                    ""max_lines"": { ""type"": ""integer"", ""description"": ""Maximum output lines (default 2000)"", ""default"": 2000 },
                    ""context_lines"": { ""type"": ""integer"", ""description"": ""Context lines in unified diff (default 3)"", ""default"": 3 }
                }
            }")]
        public static JObject P4Diff(JObject parameters)
        {
            var path = parameters["path"]?.Value<string>();
            var changelist = parameters["changelist"]?.Value<string>();
            int maxLines = Math.Clamp(parameters["max_lines"]?.Value<int>() ?? P4CommandRunner.DefaultMaxLines, 1, 10000);
            int contextLines = Math.Clamp(parameters["context_lines"]?.Value<int>() ?? P4CommandRunner.DefaultContextLines, 0, 20);

            if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(changelist))
                return Error("Either 'path' or 'changelist' is required.");

            var loginCheck = P4CommandRunner.PreflightLogin();
            if (loginCheck != null) return loginCheck;

            if (!string.IsNullOrWhiteSpace(path))
            {
                var validation = McpPathValidator.ValidatePath(path);
                if (!validation.IsValid)
                    return Error($"Path validation failed: {validation.Error}");

                var args = new List<string> { $"-du{contextLines}", validation.ResolvedPath };
                var diffResult = P4CommandRunner.RunP4CommandStreaming("diff", args.ToArray(), P4CommandRunner.DiffTimeoutMs, maxLines);

                return new JObject
                {
                    ["success"] = true,
                    ["path"] = path,
                    ["truncated"] = diffResult.Truncated,
                    ["line_count"] = diffResult.LineCount,
                    ["diff"] = diffResult.Output
                };
            }
            else
            {
                // Diff all files in the changelist via p4 describe
                return P4DiffChangelist(changelist, maxLines);
            }
        }

        private static JObject P4DiffChangelist(string changelist, int maxLines)
        {
            var args = new[] { "-du", changelist };
            var result = P4CommandRunner.RunP4CommandStreaming("describe", args, P4CommandRunner.DiffTimeoutMs, maxLines);

            return new JObject
            {
                ["success"] = true,
                ["changelist"] = changelist,
                ["truncated"] = result.Truncated,
                ["line_count"] = result.LineCount,
                ["diff"] = result.Output
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
