using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace I1rr.UnityMcp.Editor
{
    /// <summary>
    /// Shared long-running operation tracking. Any tool can register a background job
    /// and return a job_id for the client to poll via get_job_status.
    /// </summary>
    public static class JobManager
    {
        internal enum JobStatus
        {
            Running,
            Completed,
            CompletedWithErrors,
            Failed
        }

        internal sealed class JobEntry
        {
            public readonly string Id;
            public readonly string Description;
            public readonly Stopwatch Timer;
            public readonly DateTime StartedAtUtc;
            public readonly CancellationTokenSource Cts;

            public volatile JobStatus Status;
            public volatile float Progress;
            public JObject Result;
            public string Error;
            public JArray Errors;

            public JobEntry(string id, string description)
            {
                Id = id;
                Description = description;
                StartedAtUtc = DateTime.UtcNow;
                Timer = Stopwatch.StartNew();
                Cts = new CancellationTokenSource();
                Status = JobStatus.Running;
            }
        }

        /// <summary>
        /// Context passed into the job work delegate to report progress and completion.
        /// </summary>
        public sealed class JobContext
        {
            private readonly JobEntry _entry;
            private int _completed;

            internal JobContext(JobEntry entry)
            {
                _entry = entry;
            }

            public CancellationToken CancellationToken => _entry.Cts.Token;

            /// <summary>Report progress from 0.0 to 1.0.</summary>
            public void ReportProgress(float progress)
            {
                _entry.Progress = Math.Clamp(progress, 0f, 1f);
            }

            /// <summary>Mark the job as successfully completed with a result payload.</summary>
            public void Complete(JObject result)
            {
                if (Interlocked.Exchange(ref _completed, 1) != 0) return;
                _entry.Result = result;
                _entry.Progress = 1f;
                _entry.Status = JobStatus.Completed;
                _entry.Timer.Stop();
            }

            /// <summary>Mark the job as completed with partial errors.</summary>
            public void CompleteWithErrors(JObject result, JArray errors)
            {
                if (Interlocked.Exchange(ref _completed, 1) != 0) return;
                _entry.Result = result;
                _entry.Errors = errors;
                _entry.Progress = 1f;
                _entry.Status = JobStatus.CompletedWithErrors;
                _entry.Timer.Stop();
            }

            /// <summary>Mark the job as failed with an error message.</summary>
            public void Fail(string error)
            {
                if (Interlocked.Exchange(ref _completed, 1) != 0) return;
                _entry.Error = error;
                _entry.Status = JobStatus.Failed;
                _entry.Timer.Stop();
            }
        }

        private static readonly ConcurrentDictionary<string, JobEntry> _jobs = new();

        /// <summary>
        /// Register a new background job. The work delegate runs on ThreadPool.
        /// Returns the job_id immediately.
        /// </summary>
        public static string CreateJob(string description, Action<JobContext> work)
        {
            var id = Guid.NewGuid().ToString("N");
            var entry = new JobEntry(id, description);
            _jobs[id] = entry;

            var ctx = new JobContext(entry);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    work(ctx);
                }
                catch (OperationCanceledException)
                {
                    ctx.Fail("Job was cancelled.");
                }
                catch (Exception ex)
                {
                    ctx.Fail(ex.Message);
                }
            });

            return id;
        }

        /// <summary>
        /// Get the status of a job by ID.
        /// Returns a JObject with status, progress, result/error, timing info.
        /// Returns null if the job_id is unknown.
        /// </summary>
        public static JObject GetJobStatus(string jobId)
        {
            if (string.IsNullOrEmpty(jobId) || !_jobs.TryGetValue(jobId, out var entry))
                return null;

            var result = new JObject
            {
                ["job_id"] = entry.Id,
                ["description"] = entry.Description,
                ["status"] = StatusToString(entry.Status),
                ["progress"] = entry.Progress,
                ["started_at"] = entry.StartedAtUtc.ToString("O"),
                ["elapsed_ms"] = entry.Timer.ElapsedMilliseconds
            };

            switch (entry.Status)
            {
                case JobStatus.Completed:
                    if (entry.Result != null)
                        result["result"] = entry.Result;
                    break;

                case JobStatus.CompletedWithErrors:
                    if (entry.Result != null)
                        result["result"] = entry.Result;
                    if (entry.Errors != null)
                        result["errors"] = entry.Errors;
                    break;

                case JobStatus.Failed:
                    result["error"] = entry.Error ?? "Unknown error";
                    break;
            }

            return result;
        }

        /// <summary>
        /// Cancel a running job by ID. Returns true if the job was found and cancellation was requested.
        /// </summary>
        public static bool CancelJob(string jobId)
        {
            if (string.IsNullOrEmpty(jobId) || !_jobs.TryGetValue(jobId, out var entry))
                return false;

            if (entry.Status == JobStatus.Running)
            {
                entry.Cts.Cancel();
                return true;
            }

            return false;
        }

        private static string StatusToString(JobStatus status) => status switch
        {
            JobStatus.Running => "running",
            JobStatus.Completed => "completed",
            JobStatus.CompletedWithErrors => "completed_with_errors",
            JobStatus.Failed => "failed",
            _ => "unknown"
        };
    }
}
