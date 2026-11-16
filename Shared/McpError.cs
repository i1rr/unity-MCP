using Newtonsoft.Json.Linq;

namespace I1rr.UnityMcp.Shared
{
    /// <summary>
    /// Factory for standardized MCP error responses.
    /// Produces JObjects with both legacy <c>["error"]</c> (flat string) for backward compatibility
    /// and structured <c>["error_code"]</c>, <c>["error_message"]</c>, <c>["suggestion"]</c> fields.
    /// </summary>
    public static class McpError
    {
        /// <summary>
        /// Creates a standardized error response JObject.
        /// </summary>
        /// <param name="code">Error code from <see cref="McpErrorCodes"/>.</param>
        /// <param name="message">Human-readable error message.</param>
        /// <param name="suggestion">Optional suggestion for the caller to resolve the error.</param>
        /// <returns>A JObject containing error, error_code, error_message, and optionally suggestion.</returns>
        public static JObject Create(string code, string message, string suggestion = null)
        {
            var result = new JObject
            {
                ["error"] = message,
                ["error_code"] = code,
                ["error_message"] = message
            };

            if (suggestion != null)
                result["suggestion"] = suggestion;

            return result;
        }
    }
}
