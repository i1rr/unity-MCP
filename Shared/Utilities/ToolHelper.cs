using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace I1rr.UnityMcp.Shared
{
    /// <summary>
    /// Shared utility methods for MCP tool implementations.
    /// Provides standardized parameter extraction, error formatting, and pagination.
    /// </summary>
    public static class ToolHelper
    {
        public const int DefaultPageSize = 50;
        public const int MaxPageSize = 500;

        /// <summary>
        /// Extracts a required parameter from the JObject. Returns an error JObject if missing or null.
        /// </summary>
        /// <returns>The extracted value, or default if the parameter was missing (check the error output).</returns>
        public static T RequireParam<T>(JObject parameters, string name, out JObject error)
        {
            var token = parameters?[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                error = ErrorResult("missing_parameter", $"Required parameter '{name}' is missing.",
                    $"Provide the '{name}' parameter.");
                return default;
            }

            try
            {
                error = null;
                return typeof(T) == typeof(ulong) ? token.ToObject<T>() : token.Value<T>();
            }
            catch (Exception)
            {
                error = ErrorResult("invalid_parameter",
                    $"Parameter '{name}' has invalid type. Expected {typeof(T).Name}.",
                    $"Provide '{name}' as a valid {typeof(T).Name}.");
                return default;
            }
        }

        /// <summary>
        /// Extracts an optional parameter with a default value.
        /// </summary>
        public static T OptionalParam<T>(JObject parameters, string name, T defaultValue = default)
        {
            var token = parameters?[name];
            if (token == null || token.Type == JTokenType.Null)
                return defaultValue;

            try
            {
                return typeof(T) == typeof(ulong) ? token.ToObject<T>() : token.Value<T>();
            }
            catch (Exception)
            {
                return defaultValue;
            }
        }

        /// <summary>
        /// Builds a standardized pagination response from an enumerable source.
        /// Pages are 1-indexed. The returned JObject contains items, page, page_size, total_count, has_more.
        /// </summary>
        public static JObject Paginate<T>(IEnumerable<T> source, int page, int pageSize,
            Func<T, JToken> itemSerializer)
        {
            pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
            page = Math.Max(page, 1);

            // Materialise only if needed for count
            var list = source as IList<T> ?? source.ToList();
            int totalCount = list.Count;
            int skip = (page - 1) * pageSize;

            var items = new JArray();
            foreach (var item in list.Skip(skip).Take(pageSize))
            {
                items.Add(itemSerializer(item));
            }

            bool hasMore = skip + pageSize < totalCount;

            return new JObject
            {
                ["items"] = items,
                ["page"] = page,
                ["page_size"] = pageSize,
                ["total_count"] = totalCount,
                ["has_more"] = hasMore
            };
        }

        /// <summary>
        /// Builds a standardized pagination response without materialising the full collection.
        /// Use when total count is expensive to compute. The total_count field is omitted.
        /// </summary>
        public static JObject Paginate<T>(IEnumerable<T> source, int page, int pageSize,
            Func<T, JToken> itemSerializer, bool omitTotalCount)
        {
            if (!omitTotalCount)
                return Paginate(source, page, pageSize, itemSerializer);

            pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
            page = Math.Max(page, 1);

            int skip = (page - 1) * pageSize;
            var items = new JArray();
            int count = 0;
            bool hasMore = false;

            foreach (var item in source.Skip(skip).Take(pageSize + 1))
            {
                if (count < pageSize)
                    items.Add(itemSerializer(item));
                else
                    hasMore = true;
                count++;
            }

            return new JObject
            {
                ["items"] = items,
                ["page"] = page,
                ["page_size"] = pageSize,
                ["has_more"] = hasMore
            };
        }

        /// <summary>
        /// Builds a standardized error JObject with code, message, and optional suggestion.
        /// Uses <see cref="McpError.Create"/> to produce a backward-compatible response.
        /// </summary>
        public static JObject ErrorResult(string code, string message, string suggestion = null)
        {
            return McpError.Create(code, message, suggestion);
        }
    }
}
