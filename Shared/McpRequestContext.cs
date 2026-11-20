using System.Threading;
using Newtonsoft.Json.Linq;

namespace I1rr.UnityMcp.Shared
{
    /// <summary>
    /// Metadata captured for one admitted request. JSON and transport properties return
    /// copies so later requests and callbacks cannot rewrite the snapshot.
    /// ClientInfo is self-reported; only the host may supply AuthenticatedPrincipal.
    /// Cancellation is deliberately live, while the metadata is immutable.
    /// </summary>
    public sealed class McpRequestContext
    {
        private readonly JToken _requestId;
        private readonly JObject _capabilities;
        private readonly JObject _clientInfo;
        private readonly string _userAgent;
        private readonly string _remoteAddress;
        private readonly bool _hasTransport;

        public string Method { get; }
        public string OperationId { get; }
        public JToken RequestId => _requestId?.DeepClone();
        public string ProtocolVersion { get; }
        public bool IsNotification => _requestId == null;
        public JObject ClientCapabilities => (JObject)_capabilities.DeepClone();
        public JObject ClientInfo => (JObject)_clientInfo.DeepClone();
        public string AuthenticatedPrincipal { get; }
        public CancellationToken Cancellation { get; }
        public McpClientTransportInfo TransportInfo => !_hasTransport ? null :
            new McpClientTransportInfo { UserAgent = _userAgent, RemoteAddress = _remoteAddress };

        public McpRequestContext WithCancellation(CancellationToken cancellation) =>
            new McpRequestContext(Method, _requestId, ProtocolVersion,
                _capabilities, _clientInfo, TransportInfo, AuthenticatedPrincipal, cancellation, OperationId);

        internal McpRequestContext(string method, JToken requestId, string protocolVersion,
            JObject capabilities, JObject clientInfo,
            McpClientTransportInfo transportInfo, string authenticatedPrincipal,
            CancellationToken cancellation, string operationId = null)
        {
            OperationId = operationId ?? System.Guid.NewGuid().ToString("N");
            Method = method;
            _requestId = requestId?.DeepClone();
            ProtocolVersion = protocolVersion;
            _capabilities = (JObject)(capabilities?.DeepClone() ?? new JObject());
            _clientInfo = (JObject)(clientInfo?.DeepClone() ?? new JObject());
            _hasTransport = transportInfo != null;
            _userAgent = transportInfo?.UserAgent;
            _remoteAddress = transportInfo?.RemoteAddress;
            AuthenticatedPrincipal = authenticatedPrincipal;
            Cancellation = cancellation;
        }
    }
}
