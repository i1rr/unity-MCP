using System;

namespace I1rr.UnityMcp.Shared
{
    /// <summary>
    /// Marks a static method as an MCP tool, making it discoverable by the tool registry.
    /// The method must have the signature: static JObject ToolName(JObject parameters)
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class McpToolAttribute : Attribute
    {
        /// <summary>Tool name exposed to MCP clients (e.g., "ping", "get_hierarchy").</summary>
        public string Name { get; }

        /// <summary>Human-readable description of the tool for MCP clients.</summary>
        public string Description { get; }

        /// <summary>Whether this tool can only be invoked while Unity is in Play Mode.</summary>
        public bool RequiresPlayMode { get; set; }

        /// <summary>
        /// When true, the tool method may return a <see cref="DeferredResult"/> instead of a JObject.
        /// The server holds the connection open until the deferred condition is met or the timeout elapses.
        /// </summary>
        public bool DeferredResponse { get; set; }

        /// <summary>
        /// Optional JSON Schema string describing the tool's input parameters.
        /// If null or empty, a minimal schema {"type": "object"} is used.
        /// </summary>
        public string InputSchemaJson { get; set; }

        /// <summary>
        /// When true, the tool registry automatically routes calls between editor and runtime
        /// implementations based on play mode state. The agent sees a single canonical tool name.
        /// For runtime tools (prefixed with "runtime_"), the canonical name is derived by stripping
        /// the prefix unless <see cref="AutoRouteCanonicalName"/> is explicitly set.
        /// </summary>
        public bool AutoRoute { get; set; }

        /// <summary>
        /// Overrides the canonical name used for auto-routing grouping. If not set, the canonical
        /// name is derived automatically: for tools named "runtime_X", the canonical name is "X";
        /// for other tools, the canonical name is the tool's own name.
        /// </summary>
        public string AutoRouteCanonicalName { get; set; }

        /// <summary>
        /// Tool category for grouping in tools/list. If not set, falls back to the
        /// <see cref="McpToolCategoryAttribute"/> on the declaring class.
        /// Standard categories: Scene, Component, Asset, Profiling, Editor, Perforce,
        /// Session, Utility, Addressables.
        /// </summary>
        public string Category { get; set; }

        /// <summary>
        /// Semantic version string for the tool (e.g., "1.0", "1.1").
        /// Defaults to "1.0". Additive changes bump the minor version;
        /// breaking changes require a new tool name.
        /// </summary>
        public string Version { get; set; } = "1.0";

        /// <summary>
        /// When set, indicates this tool is deprecated and names the replacement tool.
        /// The tools/list response will include a deprecation notice, and invoking the
        /// tool will include a warning in the response.
        /// </summary>
        public string DeprecatedBy { get; set; }

        public McpToolAttribute(string name, string description)
        {
            Name = name;
            Description = description;
        }
    }

    /// <summary>
    /// Sets the default tool category for all <see cref="McpToolAttribute"/> methods in the class.
    /// Individual tools can override this via <see cref="McpToolAttribute.Category"/>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class McpToolCategoryAttribute : Attribute
    {
        public string Category { get; }

        public McpToolCategoryAttribute(string category)
        {
            Category = category;
        }
    }
}
