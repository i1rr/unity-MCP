using System;
using System.Collections.Generic;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace I1rr.UnityMcp.Editor.Tests
{
    /// <summary>
    /// Tests for <see cref="McpProtocol"/>: single-request parsing, validation and response envelopes.
    /// </summary>
    public class McpProtocolTests
    {
        #region ParseRequest

        [Test]
        public void ParseRequest_NullInput_ReturnsParseError()
        {
            var (requests, error) = McpProtocol.ParseRequest(null);
            Assert.That(error, Is.Not.Null);
            Assert.That(error["error"]["code"].Value<int>(), Is.EqualTo(McpProtocol.ErrorParseError));
        }

        [Test]
        public void ParseRequest_EmptyString_ReturnsParseError()
        {
            var (requests, error) = McpProtocol.ParseRequest("");
            Assert.That(error, Is.Not.Null);
            Assert.That(error["error"]["code"].Value<int>(), Is.EqualTo(McpProtocol.ErrorParseError));
        }

        [Test]
        public void ParseRequest_WhitespaceOnly_ReturnsParseError()
        {
            var (requests, error) = McpProtocol.ParseRequest("   ");
            Assert.That(error, Is.Not.Null);
            Assert.That(error["error"]["code"].Value<int>(), Is.EqualTo(McpProtocol.ErrorParseError));
        }

        [Test]
        public void ParseRequest_InvalidJson_ReturnsParseError()
        {
            var (requests, error) = McpProtocol.ParseRequest("{not valid json}}}");
            Assert.That(error, Is.Not.Null);
            Assert.That(error["error"]["code"].Value<int>(), Is.EqualTo(McpProtocol.ErrorParseError));
        }

        [Test]
        public void ParseRequest_ValidSingleObject_ReturnsList()
        {
            var json = @"{""jsonrpc"":""2.0"",""method"":""ping"",""id"":1}";
            var (requests, error) = McpProtocol.ParseRequest(json);
            Assert.That(error, Is.Null);
            Assert.That(requests, Is.Not.Null);
            Assert.That(requests.Count, Is.EqualTo(1));
            Assert.That(requests[0]["method"].Value<string>(), Is.EqualTo("ping"));
        }

        [Test]
        public void ParseRequest_EmptyBatchArray_ReturnsInvalidRequest()
        {
            var (requests, error) = McpProtocol.ParseRequest("[]");
            Assert.That(error, Is.Not.Null);
            Assert.That(error["error"]["code"].Value<int>(), Is.EqualTo(McpProtocol.ErrorInvalidRequest));
        }

        [Test]
        public void ParseRequest_BatchWithNonObject_ReturnsInvalidRequest()
        {
            var (requests, error) = McpProtocol.ParseRequest(@"[1, 2, 3]");
            Assert.That(error, Is.Not.Null);
            Assert.That(error["error"]["code"].Value<int>(), Is.EqualTo(McpProtocol.ErrorInvalidRequest));
        }

        [Test]
        public void ParseRequest_ValidBatch_IsRejected()
        {
            var (requests, error) = McpProtocol.ParseRequest("[{'jsonrpc':'2.0','method':'tools/list','id':1}]");
            Assert.That(requests, Is.Null);
            Assert.That((int)error["error"]["code"], Is.EqualTo(McpProtocol.ErrorInvalidRequest));
        }

        [Test]
        public void ParseRequest_PrimitiveJson_ReturnsInvalidRequest()
        {
            var (requests, error) = McpProtocol.ParseRequest("42");
            Assert.That(error, Is.Not.Null);
            Assert.That(error["error"]["code"].Value<int>(), Is.EqualTo(McpProtocol.ErrorInvalidRequest));
        }

        #endregion

        #region ValidateJsonRpc

        [Test]
        public void ValidateJsonRpc_MissingJsonrpc_ReturnsError()
        {
            var request = JObject.Parse(@"{""method"":""ping"",""id"":1}");
            var error = McpProtocol.ValidateJsonRpc(request);
            Assert.That(error, Is.Not.Null);
            Assert.That(error["error"]["code"].Value<int>(), Is.EqualTo(McpProtocol.ErrorInvalidRequest));
        }

        [Test]
        public void ValidateJsonRpc_WrongJsonrpcVersion_ReturnsError()
        {
            var request = JObject.Parse(@"{""jsonrpc"":""1.0"",""method"":""ping"",""id"":1}");
            var error = McpProtocol.ValidateJsonRpc(request);
            Assert.That(error, Is.Not.Null);
        }

        [Test]
        public void ValidateJsonRpc_MissingMethod_ReturnsError()
        {
            var request = JObject.Parse(@"{""jsonrpc"":""2.0"",""id"":1}");
            var error = McpProtocol.ValidateJsonRpc(request);
            Assert.That(error, Is.Not.Null);
        }

        [Test]
        public void ValidateJsonRpc_NumericMethod_ReturnsError()
        {
            var request = JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":123,""id"":1}");
            var error = McpProtocol.ValidateJsonRpc(request);
            Assert.That(error, Is.Not.Null);
        }

        [Test]
        public void ValidateJsonRpc_ParamsAsArray_IsValid()
        {
            var request = JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""ping"",""id"":1,""params"":[1,2,3]}");
            var error = McpProtocol.ValidateJsonRpc(request);
            Assert.That(error, Is.Null);
        }

        [Test]
        public void ValidateJsonRpc_ParamsAsString_ReturnsError()
        {
            var request = JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""ping"",""id"":1,""params"":""bad""}");
            var error = McpProtocol.ValidateJsonRpc(request);
            Assert.That(error, Is.Not.Null);
        }

        [Test]
        public void ValidateJsonRpc_IdAsString_IsValid()
        {
            var request = JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""ping"",""id"":""abc""}");
            var error = McpProtocol.ValidateJsonRpc(request);
            Assert.That(error, Is.Null);
        }

        [Test]
        public void ValidateJsonRpc_IdAsBoolean_ReturnsError()
        {
            var request = JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""ping"",""id"":true}");
            var error = McpProtocol.ValidateJsonRpc(request);
            Assert.That(error, Is.Not.Null);
        }

        [Test]
        public void ValidateJsonRpc_ValidRequest_ReturnsNull()
        {
            var request = JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""ping"",""id"":1}");
            var error = McpProtocol.ValidateJsonRpc(request);
            Assert.That(error, Is.Null);
        }

        #endregion

        #region IsNotification

        [Test]
        public void IsNotification_NoId_ReturnsTrue()
        {
            var request = JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""notifications/initialized""}");
            Assert.That(McpProtocol.IsNotification(request), Is.True);
        }

        [Test]
        public void IsNotification_WithId_ReturnsFalse()
        {
            var request = JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""ping"",""id"":1}");
            Assert.That(McpProtocol.IsNotification(request), Is.False);
        }

        #endregion

        #region BuildResponse / BuildErrorResponse

        [Test]
        public void BuildResponse_IncludesJsonRpc20()
        {
            var response = McpProtocol.BuildResponse(new JValue(1), new JObject { ["ok"] = true });
            Assert.That(response["jsonrpc"].Value<string>(), Is.EqualTo("2.0"));
            Assert.That(response["id"].Value<int>(), Is.EqualTo(1));
            Assert.That(response["result"]["ok"].Value<bool>(), Is.True);
        }

        [Test]
        public void BuildResponse_NullId_SetsIdToNull()
        {
            var response = McpProtocol.BuildResponse(null, new JObject());
            Assert.That(response["id"].Type, Is.EqualTo(JTokenType.Null));
        }

        [Test]
        public void BuildErrorResponse_IncludesCodeAndMessage()
        {
            var error = McpProtocol.BuildErrorResponse(new JValue(5), -32600, "Invalid request");
            Assert.That(error["error"]["code"].Value<int>(), Is.EqualTo(-32600));
            Assert.That(error["error"]["message"].Value<string>(), Is.EqualTo("Invalid request"));
        }

        [Test]
        public void BuildErrorResponse_WithData_IncludesData()
        {
            var data = new JObject { ["detail"] = "extra info" };
            var error = McpProtocol.BuildErrorResponse(new JValue(1), -32600, "err", data);
            Assert.That(error["error"]["data"]["detail"].Value<string>(), Is.EqualTo("extra info"));
        }

        [Test]
        public void BuildToolResult_WrapsTextInContentArray()
        {
            var result = McpProtocol.BuildToolResult("hello");
            Assert.That(result["content"], Is.Not.Null);
            Assert.That(result["content"].Type, Is.EqualTo(JTokenType.Array));
            Assert.That(result["content"][0]["type"].Value<string>(), Is.EqualTo("text"));
            Assert.That(result["content"][0]["text"].Value<string>(), Is.EqualTo("hello"));
        }

        [Test]
        public void BuildToolResult_ErrorFlag_SetsIsError()
        {
            var result = McpProtocol.BuildToolResult("fail", isError: true);
            Assert.That(result["isError"].Value<bool>(), Is.True);
        }

        #endregion
    }
}
