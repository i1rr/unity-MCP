namespace I1rr.UnityMcp.Shared
{
    /// <summary>
    /// Interface for optional MCP modules that manage their own lifecycle.
    /// Implementations are discovered via reflection during tool registry initialization.
    /// Modules report availability so the registry can filter tools from unavailable modules.
    /// </summary>
    public interface IMcpModule
    {
        /// <summary>
        /// Human-readable name of the module (e.g., "Addressables").
        /// </summary>
        string ModuleName { get; }

        /// <summary>
        /// Whether the module's dependencies are present and it can function.
        /// When false, tools from this module will not be listed or invocable.
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Called once during tool registry initialization to allow the module to perform setup.
        /// </summary>
        void Initialize();

        /// <summary>
        /// Called during server shutdown to allow the module to clean up resources.
        /// </summary>
        void Shutdown();
    }
}
