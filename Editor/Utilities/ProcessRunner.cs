using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using UnityEngine;

namespace I1rr.UnityMcp.Editor
{
    /// <summary>
    /// Safe wrapper for launching external processes with timeout, streaming, and kill semantics.
    /// Primarily used for P4 and other CLI tools.
    /// </summary>
    public static class ProcessRunner
    {
        private static readonly string ProjectRoot = System.IO.Path.GetDirectoryName(Application.dataPath);

        public struct ProcessResult
        {
            public int ExitCode;
            public string Stdout;
            public string Stderr;
            public bool TimedOut;
            public bool Truncated;
            public int LinesRead;
        }

        /// <summary>
        /// Runs an external process, waits for it to exit (or times out), and returns stdout + stderr.
        /// </summary>
        /// <param name="executable">Path or name of the executable to run.</param>
        /// <param name="arguments">Command-line arguments.</param>
        /// <param name="workingDirectory">Working directory. If null or empty, defaults to the Unity project root.</param>
        /// <param name="timeoutMs">Maximum milliseconds to wait before killing the process.</param>
        public static ProcessResult Run(string executable, string arguments, string workingDirectory = null, int timeoutMs = 10000)
        {
            if (string.IsNullOrEmpty(workingDirectory))
                workingDirectory = ProjectRoot;

            var stderr = new StringBuilder();

            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = workingDirectory
                };

                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                        stderr.AppendLine(e.Data);
                };

                process.Start();
                process.BeginErrorReadLine();

                // Read stdout asynchronously so WaitForExit timeout is effective
                // even if the child process (or an inherited handle) holds stdout open.
                var stdoutBuilder = new StringBuilder();
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                        stdoutBuilder.AppendLine(e.Data);
                };
                process.BeginOutputReadLine();

                bool exited = process.WaitForExit(timeoutMs);

                if (!exited)
                {
                    KillProcess(process);
                    return new ProcessResult
                    {
                        ExitCode = -1,
                        Stdout = stdoutBuilder.ToString(),
                        Stderr = stderr.ToString(),
                        TimedOut = true,
                        Truncated = false,
                        LinesRead = 0
                    };
                }

                // After WaitForExit(int) returns true, call WaitForExit() without
                // timeout to ensure async output buffers are flushed.
                process.WaitForExit();

                return new ProcessResult
                {
                    ExitCode = process.ExitCode,
                    Stdout = stdoutBuilder.ToString(),
                    Stderr = stderr.ToString(),
                    TimedOut = false,
                    Truncated = false,
                    LinesRead = 0
                };
            }
            catch (Exception ex)
            {
                return new ProcessResult
                {
                    ExitCode = -1,
                    Stdout = null,
                    Stderr = ex.Message,
                    TimedOut = false,
                    Truncated = false,
                    LinesRead = 0
                };
            }
        }

        /// <summary>
        /// Runs an external process, reading stdout line-by-line. Stops at maxLines or timeout, kills the process.
        /// Useful for potentially large outputs (e.g., p4 diff, log commands).
        /// </summary>
        /// <param name="executable">Path or name of the executable to run.</param>
        /// <param name="arguments">Command-line arguments.</param>
        /// <param name="workingDirectory">Working directory. If null or empty, defaults to the Unity project root.</param>
        /// <param name="maxLines">Maximum number of lines to read before truncating.</param>
        /// <param name="timeoutMs">Maximum milliseconds to wait before killing the process.</param>
        public static ProcessResult RunStreaming(string executable, string arguments, string workingDirectory = null, int maxLines = 2000, int timeoutMs = 30000)
        {
            if (string.IsNullOrEmpty(workingDirectory))
                workingDirectory = ProjectRoot;

            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = workingDirectory
                };

                var stderr = new StringBuilder();
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                        stderr.AppendLine(e.Data);
                };

                process.Start();
                process.BeginErrorReadLine();

                var output = new StringBuilder();
                int lineCount = 0;
                bool truncated = false;

                using var cts = new CancellationTokenSource(timeoutMs);
                using var reader = process.StandardOutput;

                while (!cts.IsCancellationRequested)
                {
                    string line = reader.ReadLine();
                    if (line == null)
                        break;

                    lineCount++;
                    if (lineCount > maxLines)
                    {
                        truncated = true;
                        KillProcess(process);
                        break;
                    }

                    output.AppendLine(line);
                }

                bool timedOut = false;
                if (!process.HasExited)
                {
                    if (!truncated)
                        timedOut = true;

                    KillProcess(process);
                }
                else
                {
                    process.WaitForExit(); // Ensure async stderr completes
                }

                return new ProcessResult
                {
                    ExitCode = process.HasExited ? process.ExitCode : -1,
                    Stdout = output.ToString(),
                    Stderr = stderr.ToString(),
                    TimedOut = timedOut,
                    Truncated = truncated,
                    LinesRead = lineCount
                };
            }
            catch (Exception ex)
            {
                return new ProcessResult
                {
                    ExitCode = -1,
                    Stdout = null,
                    Stderr = ex.Message,
                    TimedOut = false,
                    Truncated = false,
                    LinesRead = 0
                };
            }
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
                // Process may have already exited between the check and Kill call
            }
        }
    }
}
