using System.Collections.Generic;

namespace I1rr.UnityMcp.Shared
{
    /// <summary>
    /// Provides access to long-lived ProfilerRecorder data.
    /// Implementations exist for Editor (<c>I1rr.UnityMcp.Editor.ProfilerRecorderManager</c>)
    /// and Runtime (<c>I1rr.UnityMcp.Runtime.ProfilerRecorderManager</c>).
    /// </summary>
    public interface IProfilerRecorderProvider
    {
        /// <summary>
        /// Returns up to <paramref name="count"/> most recent sample values for the named counter.
        /// Returns an empty list if the counter is not available or has no data.
        /// </summary>
        List<long> GetSamples(string counterName, int count);

        /// <summary>
        /// Returns the most recent value for the named counter, or 0 if unavailable.
        /// </summary>
        long GetLastValue(string counterName);

        /// <summary>
        /// Returns true if the named counter is actively recording and has valid data.
        /// </summary>
        bool IsCounterValid(string counterName);

        /// <summary>
        /// Requests an on-demand counter to be started. Returns true if data is already available,
        /// false if the counter was just started and needs time to accumulate samples.
        /// </summary>
        bool RequestOnDemandCounter(string category, string counterName);
    }

    /// <summary>
    /// Static service locator for the active <see cref="IProfilerRecorderProvider"/>.
    /// Set by the Editor or Runtime manager during initialization.
    /// </summary>
    public static class ProfilerRecorderService
    {
        /// <summary>
        /// The currently active provider, or null if no manager has been initialized.
        /// </summary>
        public static IProfilerRecorderProvider Provider { get; set; }
    }
}
