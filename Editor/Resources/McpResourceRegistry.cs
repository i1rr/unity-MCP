using System;
using System.Collections.Generic;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;

namespace I1rr.UnityMcp.Editor
{
    /// <summary>
    /// Static registry that maps MCP resource URIs to handler functions.
    /// Resources provide read-only state snapshots to MCP clients.
    /// </summary>
    public static class McpResourceRegistry
    {
        private sealed class ResourceEntry
        {
            public string Uri;
            public string Name;
            public string Description;
            public Func<JObject> Handler;
        }

        private static readonly Dictionary<string, ResourceEntry> _resources = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _lock = new();

        /// <summary>
        /// Registers a resource handler. Called from [InitializeOnLoadMethod] or static constructors.
        /// </summary>
        /// <param name="uri">Unique URI identifying the resource (e.g., "editor://state").</param>
        /// <param name="name">Human-readable name for MCP clients.</param>
        /// <param name="description">Description of what the resource provides.</param>
        /// <param name="handler">Function that returns the resource data as a JObject.</param>
        public static void Register(string uri, string name, string description, Func<JObject> handler)
        {
            if (string.IsNullOrEmpty(uri))
                throw new ArgumentNullException(nameof(uri));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            lock (_lock)
            {
                _resources[uri] = new ResourceEntry
                {
                    Uri = uri,
                    Name = name ?? uri,
                    Description = description ?? string.Empty,
                    Handler = handler
                };
            }
        }

        /// <summary>
        /// Returns all registered resources formatted as an MCP resources/list response array.
        /// </summary>
        public static JArray ListResources()
        {
            lock (_lock)
            {
                var array = new JArray();
                foreach (var entry in _resources.Values)
                {
                    array.Add(new JObject
                    {
                        ["uri"] = entry.Uri,
                        ["name"] = entry.Name,
                        ["description"] = entry.Description
                    });
                }
                return array;
            }
        }

        /// <summary>
        /// Reads a resource by URI. Returns the handler's JObject result,
        /// or an error object if the URI is not registered or the handler throws.
        /// </summary>
        public static JObject ReadResource(string uri)
        {
            if (string.IsNullOrEmpty(uri))
            {
                return McpError.Create(McpErrorCodes.MissingParameter, "Resource URI is required.");
            }

            Func<JObject> handler;
            lock (_lock)
            {
                if (!_resources.TryGetValue(uri, out var entry))
                {
                    return McpError.Create(McpErrorCodes.NotFound, $"Resource not found: {uri}");
                }
                handler = entry.Handler;
            }

            try
            {
                return handler();
            }
            catch (Exception ex)
            {
                return McpError.Create(McpErrorCodes.InternalError, $"Resource handler failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks whether a resource URI is registered.
        /// </summary>
        public static bool HasResource(string uri)
        {
            lock (_lock)
            {
                return _resources.ContainsKey(uri);
            }
        }
    }
}
