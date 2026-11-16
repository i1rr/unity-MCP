using Newtonsoft.Json.Linq;

namespace I1rr.UnityMcp.Shared
{
    /// <summary>
    /// Enforces configurable maximum response sizes for tool results.
    /// When a response exceeds the limit, it is replaced with a truncation placeholder
    /// containing metadata and a hint suggesting narrower query parameters.
    /// </summary>
    public static class ResponseSizeLimiter
    {
        /// <summary>
        /// Maximum response size in characters (approximates bytes for ASCII-heavy JSON).
        /// Default 256 KB. Set to 0 to disable.
        /// </summary>
        public static int MaxResponseSizeBytes { get; set; } = 256 * 1024;

        /// <summary>
        /// Checks if a JObject response exceeds the max size limit and truncates if needed.
        /// Returns the original result if within limits, or a truncated placeholder.
        /// Error responses are never truncated.
        /// </summary>
        public static JObject Apply(JObject result)
        {
            if (MaxResponseSizeBytes <= 0 || result == null)
                return result;

            // Never truncate error responses
            if (result["error"] != null)
                return result;

            int size = result.ToString(Newtonsoft.Json.Formatting.None).Length;
            if (size <= MaxResponseSizeBytes)
                return result;

            return new JObject
            {
                ["truncated"] = true,
                ["total_size"] = size,
                ["max_size"] = MaxResponseSizeBytes,
                ["hint"] = "Response exceeded size limit. Try narrowing your query with filters, pagination (page_size, cursor, depth), or more specific parameters."
            };
        }
    }
}
