using Newtonsoft.Json.Linq;

namespace I1rr.UnityMcp.Shared
{
    public static class McpSubscriptions
    {
        public const string Method = "subscriptions/listen";
        public const string SubscriptionIdMetaKey = "io.modelcontextprotocol/subscriptionId";

        public static JObject Validate(JObject request)
        {
            var filter = (request["params"] as JObject)?["notifications"] as JObject;
            if (filter == null)
                return McpProtocol.BuildErrorResponse(request["id"], McpProtocol.ErrorInvalidParams, "notifications must be an object");
            foreach (var name in new[] { "toolsListChanged", "promptsListChanged", "resourcesListChanged" })
                if (filter[name] != null && filter[name].Type != JTokenType.Boolean)
                    return McpProtocol.BuildErrorResponse(request["id"], McpProtocol.ErrorInvalidParams, name + " must be boolean");
            if (filter["resourceSubscriptions"] != null)
            {
                if (!(filter["resourceSubscriptions"] is JArray uris))
                    return McpProtocol.BuildErrorResponse(request["id"], McpProtocol.ErrorInvalidParams, "resourceSubscriptions must be an array");
                foreach (var uri in uris)
                    if (uri.Type != JTokenType.String)
                        return McpProtocol.BuildErrorResponse(request["id"], McpProtocol.ErrorInvalidParams, "resourceSubscriptions must contain strings");
            }
            return null;
        }

        // No optional change feed is advertised yet. Acknowledge only supported filters.
        public static JObject Acknowledgment(McpRequestContext context) => new JObject
        {
            ["jsonrpc"] = "2.0", ["method"] = "notifications/subscriptions/acknowledged",
            ["params"] = new JObject
            {
                ["notifications"] = new JObject(),
                ["_meta"] = new JObject { [SubscriptionIdMetaKey] = context.RequestId }
            }
        };
    }
}
