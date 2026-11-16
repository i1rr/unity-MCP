using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;

namespace I1rr.UnityMcp.Editor
{
    /// <summary>
    /// Perforce CLI wrapper with allowlist-based command validation, timeout enforcement,
    /// and output size limits. Prevents arbitrary shell commands via strict argument sanitization.
    /// </summary>
    internal static class P4CommandRunner
    {
        public const int SimpleTimeoutMs = 10000;
        public const int DiffTimeoutMs = 30000;
        public const int DefaultMaxLines = 2000;
        public const int DefaultContextLines = 3;
        public const int MaxChangelists = 100;

        internal static readonly HashSet<string> AllowedCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            "edit", "revert", "opened", "diff", "submit", "describe",
            "changes", "fstat", "login", "info"
        };

        public static JObject PreflightLogin()
        {
            var result = RunP4Command("login", new[] { "-s" }, SimpleTimeoutMs);
            if (!result.Success)
            {
                var err = McpError.Create(McpErrorCodes.P4Error, $"Perforce login check failed: {result.Error ?? "not logged in"}");
                err["success"] = false;
                return err;
            }
            return null;
        }

        public readonly struct P4Result
        {
            public bool Success { get; }
            public string Output { get; }
            public string Error { get; }

            public P4Result(bool success, string output, string error)
            {
                Success = success;
                Output = output;
                Error = error;
            }
        }

        public readonly struct P4StreamingResult
        {
            public string Output { get; }
            public int LineCount { get; }
            public bool Truncated { get; }

            public P4StreamingResult(string output, int lineCount, bool truncated)
            {
                Output = output;
                LineCount = lineCount;
                Truncated = truncated;
            }
        }

        public readonly struct P4TaggedResult
        {
            public bool Success { get; }
            public List<Dictionary<string, string>> Records { get; }
            public string Error { get; }

            public P4TaggedResult(bool success, List<Dictionary<string, string>> records, string error)
            {
                Success = success;
                Records = records;
                Error = error;
            }
        }

        public static P4Result RunP4Command(string command, string[] args, int timeoutMs)
        {
            if (!AllowedCommands.Contains(command))
                return new P4Result(false, null, $"Command '{command}' is not in the allowed list.");

            var arguments = BuildP4Arguments(command, args);
            var stderr = new StringBuilder();

            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = "p4",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null) stderr.AppendLine(e.Data);
                };

                process.Start();
                process.BeginErrorReadLine();

                string output = process.StandardOutput.ReadToEnd();
                bool exited = process.WaitForExit(timeoutMs);

                if (!exited)
                {
                    KillProcess(process);
                    return new P4Result(false, null, $"P4 command timed out after {timeoutMs}ms: p4 {command}");
                }

                var stderrText = stderr.ToString().Trim();

                if (process.ExitCode != 0)
                {
                    var errorMsg = !string.IsNullOrEmpty(stderrText) ? stderrText : output?.Trim();
                    return new P4Result(false, null, $"P4 exit code {process.ExitCode}: {errorMsg}");
                }

                // Some p4 commands output errors to stdout
                if (!string.IsNullOrEmpty(stderrText) && stderrText.Contains("error", StringComparison.OrdinalIgnoreCase))
                {
                    return new P4Result(false, null, stderrText);
                }

                return new P4Result(true, output, null);
            }
            catch (Exception ex)
            {
                return new P4Result(false, null, $"Failed to execute p4: {ex.Message}");
            }
        }

        public static P4StreamingResult RunP4CommandStreaming(string command, string[] args, int timeoutMs, int maxLines)
        {
            if (!AllowedCommands.Contains(command))
                return new P4StreamingResult($"Command '{command}' is not in the allowed list.", 0, false);

            var arguments = BuildP4Arguments(command, args);

            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = "p4",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                process.Start();

                var output = new StringBuilder();
                int lineCount = 0;
                bool truncated = false;

                var cts = new CancellationTokenSource(timeoutMs);
                using var reader = process.StandardOutput;

                while (!cts.IsCancellationRequested)
                {
                    string line = reader.ReadLine();
                    if (line == null) break;

                    lineCount++;
                    if (lineCount > maxLines)
                    {
                        truncated = true;
                        KillProcess(process);
                        break;
                    }

                    output.AppendLine(line);
                }

                if (!process.HasExited)
                {
                    if (!truncated)
                    {
                        // Timeout occurred
                        KillProcess(process);
                        return new P4StreamingResult(output.ToString(), lineCount, true);
                    }
                    process.WaitForExit(1000);
                }

                return new P4StreamingResult(output.ToString(), lineCount, truncated);
            }
            catch (Exception ex)
            {
                return new P4StreamingResult($"Failed to execute p4: {ex.Message}", 0, false);
            }
        }

        public static P4TaggedResult RunP4TaggedCommand(string command, string[] args, int timeoutMs)
        {
            if (!AllowedCommands.Contains(command))
                return new P4TaggedResult(false, null, $"Command '{command}' is not in the allowed list.");

            // Insert -ztag for machine-parseable output
            var taggedArgs = new[] { "-ztag" }.Concat(args).ToArray();
            var result = RunP4Command(command, taggedArgs, timeoutMs);

            if (!result.Success)
                return new P4TaggedResult(false, null, result.Error);

            var records = ParseTaggedRecords(result.Output);
            return new P4TaggedResult(true, records, null);
        }

        private static string BuildP4Arguments(string command, string[] args)
        {
            var sb = new StringBuilder();

            // Load p4 config from environment or p4config if available
            var p4Port = Environment.GetEnvironmentVariable("P4PORT");
            var p4Client = Environment.GetEnvironmentVariable("P4CLIENT");
            var p4User = Environment.GetEnvironmentVariable("P4USER");

            if (!string.IsNullOrEmpty(p4Port)) sb.Append($"-p \"{p4Port}\" ");
            if (!string.IsNullOrEmpty(p4Client)) sb.Append($"-c \"{p4Client}\" ");
            if (!string.IsNullOrEmpty(p4User)) sb.Append($"-u \"{p4User}\" ");

            sb.Append(command);

            foreach (var arg in args)
            {
                sb.Append(' ');
                // Escape embedded double-quote characters to prevent argument injection.
                // On macOS/Linux, " is a valid filename character, so a path like
                // Assets/x"-f "y would inject the -f flag without escaping.
                var escaped = arg.Replace("\"", "\\\"");
                if (escaped.Contains(' ') || escaped.Contains('"'))
                    sb.Append($"\"{escaped}\"");
                else
                    sb.Append(escaped);
            }

            return sb.ToString();
        }

        private static void KillProcess(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch
            {
                // Best effort
            }
        }

        public static Dictionary<string, string> ParseTaggedOutput(string output)
        {
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(output)) return fields;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("...")) continue;

                var content = trimmed.Substring(4); // Skip "... "
                var spaceIdx = content.IndexOf(' ');
                if (spaceIdx > 0)
                {
                    var key = content.Substring(0, spaceIdx);
                    var value = content.Substring(spaceIdx + 1);
                    fields[key] = value;
                }
                else
                {
                    fields[content] = "";
                }
            }

            return fields;
        }

        public static List<Dictionary<string, string>> ParseTaggedRecords(string output)
        {
            var records = new List<Dictionary<string, string>>();
            if (string.IsNullOrEmpty(output)) return records;

            Dictionary<string, string> current = null;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                if (!trimmed.StartsWith("..."))
                {
                    // Non-tagged line — might be a record separator or info message
                    continue;
                }

                var content = trimmed.Substring(4); // Skip "... "
                var spaceIdx = content.IndexOf(' ');
                string key, value;

                if (spaceIdx > 0)
                {
                    key = content.Substring(0, spaceIdx);
                    value = content.Substring(spaceIdx + 1);
                }
                else
                {
                    key = content;
                    value = "";
                }

                // Detect new record by seeing the first field repeated
                if (current != null && current.ContainsKey(key))
                {
                    records.Add(current);
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                if (current == null)
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                current[key] = value;
            }

            if (current != null && current.Count > 0)
                records.Add(current);

            return records;
        }

        public static JObject Error(string message)
        {
            var err = McpError.Create(McpErrorCodes.P4Error, message);
            err["success"] = false;
            return err;
        }
    }
}
