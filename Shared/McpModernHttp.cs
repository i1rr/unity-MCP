using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace I1rr.UnityMcp.Shared
{
    /// <summary>Modern HTTP validation shared by Editor and development-player transports.</summary>
    public static class McpModernHttp
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public static string CorsAllowedHeaders(IDictionary<string, string> requestHeaders)
        {
            const string standard = "Content-Type, Authorization, MCP-Session-Id, MCP-Protocol-Version, Mcp-Method, Mcp-Name";
            var requested = Header(requestHeaders, "Access-Control-Request-Headers");
            if (string.IsNullOrEmpty(requested)) return standard;
            var custom = requested.Split(',').Select(h => h.Trim())
                .Where(h => h.StartsWith("Mcp-Param-", StringComparison.OrdinalIgnoreCase) &&
                    h.Length > 10 && h.All(c => c <= 127 && (char.IsLetterOrDigit(c) || "!#$%&'*+-.^_`|~".IndexOf(c) >= 0)))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(64);
            return string.Join(", ", new[] { standard }.Concat(custom));
        }

        private static string Header(IDictionary<string, string> headers, string name) =>
            headers?.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

        public static bool IsModernRequest(IDictionary<string, string> headers, byte[] body)
        {
            var version = Header(headers, "MCP-Protocol-Version");
            if (!string.IsNullOrEmpty(version) && version != McpProtocol.ProtocolVersionBaseline &&
                version != McpProtocol.ProtocolVersionNext) return true;
            try
            {
                var token = JToken.Parse(Utf8.GetString(body ?? Array.Empty<byte>()));
                return token is JObject one ? HasModernMetadata(one) :
                    token is JArray batch && batch.OfType<JObject>().Any(HasModernMetadata);
            }
            catch (Exception e) when (e is JsonException || e is DecoderFallbackException) { return false; }
        }

        private static bool HasModernMetadata(JObject request) =>
            request["method"]?.Type == JTokenType.String && (string)request["method"] == "server/discover" ||
            ((request["params"] as JObject)?["_meta"] as JObject)?.Property(McpProtocol.VersionMetaKey) != null;

        public static bool TryPrepare(IDictionary<string, string> headers, byte[] body,
            McpClientTransportInfo transport, out JObject request, out McpRequestContext context,
            out JObject error, out int httpStatus)
        {
            request = null; context = null; error = null; httpStatus = 400;
            try { request = JToken.Parse(Utf8.GetString(body ?? Array.Empty<byte>())) as JObject; }
            catch (Exception e) when (e is JsonException || e is DecoderFallbackException)
            { error = McpProtocol.BuildErrorResponse(null, McpProtocol.ErrorParseError, "Invalid JSON or UTF-8"); return false; }
            if (request == null)
            { error = McpProtocol.BuildErrorResponse(null, McpProtocol.ErrorInvalidRequest, "Modern HTTP requires one JSON-RPC request"); return false; }
            error = McpProtocol.ValidateJsonRpc(request);
            if (error != null) return false;
            var id = request["id"];
            var meta = (request["params"] as JObject)?["_meta"] as JObject;
            var version = Header(headers, "MCP-Protocol-Version");
            var requested = meta?[McpProtocol.VersionMetaKey];
            if (string.IsNullOrEmpty(version) || requested?.Type != JTokenType.String || (string)requested != version)
            { error = Mismatch(id, "Missing or mismatched protocol version header/metadata"); return false; }
            if (version != McpProtocol.ProtocolVersionModern)
            {
                error = McpProtocol.BuildErrorResponse(id, McpProtocol.ErrorUnsupportedProtocolVersion,
                    "Unsupported protocol version", new JObject { ["supported"] = McpProtocol.SupportedVersions, ["requested"] = version });
                return false;
            }
            var method = (string)request["method"];
            if (Header(headers, "Mcp-Method") != method)
            { error = Mismatch(id, "Missing or mismatched Mcp-Method header"); return false; }
            var parameters = request["params"] as JObject;
            string field = method == "resources/read" ? "uri" :
                method == "tools/call" || method == "prompts/get" ? "name" : null;
            if (field != null && (parameters?[field]?.Type != JTokenType.String ||
                !TryDecode(Header(headers, "Mcp-Name"), out var name) || name != (string)parameters[field]))
            { error = Mismatch(id, "Missing or mismatched Mcp-Name header"); return false; }
            context = McpProtocol.CreateModernRequestContext(request, transport);
            error = McpProtocol.ValidateRequestForDispatch(request, context);
            if (error != null) { context = null; return false; }
            if (method == "tools/call")
            {
                var schema = McpToolRegistry.InputSchemaForHeaders((string)parameters["name"]);
                if (!ValidateToolHeaders(schema, parameters["arguments"] as JObject, headers))
                { error = Mismatch(id, "Custom tool headers do not match the input schema and arguments"); context = null; return false; }
            }
            httpStatus = 200;
            return true;
        }

        private static JObject Mismatch(JToken id, string message) =>
            McpProtocol.BuildErrorResponse(id, McpProtocol.ErrorHeaderMismatch, message);

        internal static bool ValidateToolHeaders(JObject schema, JObject arguments, IDictionary<string, string> headers)
        {
            if (schema == null) return true;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return ValidateParameters(schema, arguments, headers, seen) &&
                seen.Count == schema.Descendants().OfType<JProperty>().Count(p => p.Name == "x-mcp-header");
        }

        internal static bool IsValidToolHeaderSchema(JObject schema) =>
            ValidateToolHeaders(schema, null, null);

        private static bool TryDecode(string header, out string value)
        {
            value = null;
            if (header == null) return false;
            if (header.StartsWith("=?base64?", StringComparison.Ordinal) && header.EndsWith("?=", StringComparison.Ordinal))
            {
                try { value = Utf8.GetString(Convert.FromBase64String(header.Substring(9, header.Length - 11))); return true; }
                catch (Exception e) when (e is FormatException || e is DecoderFallbackException || e is ArgumentException) { return false; }
            }
            if (header.Length != header.Trim().Length || header.Any(c => c > 126 || c < 32 && c != '\t')) return false;
            value = header;
            return true;
        }

        private static bool ValidateParameters(JObject schema, JObject arguments,
            IDictionary<string, string> headers, HashSet<string> seen)
        {
            if (!(schema?["properties"] is JObject properties)) return true;
            foreach (var property in properties.Properties())
            {
                if (!(property.Value is JObject definition)) continue;
                var value = arguments?[property.Name];
                if (definition.Property("x-mcp-header") != null)
                {
                    var annotation = definition["x-mcp-header"];
                    if (annotation.Type != JTokenType.String) return false;
                    var name = (string)annotation;
                    if (string.IsNullOrEmpty(name) || name.Any(c => !char.IsLetterOrDigit(c) && "!#$%&'*+-.^_`|~".IndexOf(c) < 0 || c > 127) || !seen.Add(name)) return false;
                    if (definition["type"]?.Type != JTokenType.String) return false;
                    var type = (string)definition["type"];
                    if (type != "string" && type != "integer" && type != "boolean") return false;
                    var header = Header(headers, "Mcp-Param-" + name);
                    if (value == null || value.Type == JTokenType.Null) { if (header != null) return false; }
                    else if (!TryDecode(header, out var decoded) || !ParameterEquals(value, type, decoded)) return false;
                }
                if (!ValidateParameters(definition, value as JObject, headers, seen)) return false;
            }
            return true;
        }

        private static bool ParameterEquals(JToken value, string type, string header)
        {
            if (type == "string") return value.Type == JTokenType.String && (string)value == header;
            if (type == "boolean") return value.Type == JTokenType.Boolean && header == ((bool)value ? "true" : "false");
            if (value.Type != JTokenType.Integer && value.Type != JTokenType.Float) return false;
            return decimal.TryParse(value.ToString(Formatting.None), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
                decimal.Truncate(number) == number && Math.Abs(number) <= 9007199254740991m &&
                decimal.TryParse(header, NumberStyles.Float, CultureInfo.InvariantCulture, out var other) && number == other;
        }
    }
}
