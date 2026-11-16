using System;
using System.IO;
using System.Text;
using I1rr.UnityMcp.Shared;
using NUnit.Framework;

namespace I1rr.UnityMcp.Editor.Tests
{
    /// <summary>
    /// Tests for <see cref="TcpHttpHandler"/>: malformed requests, oversized bodies,
    /// missing headers, chunked rejection, and response writing.
    /// </summary>
    public class TcpHttpHandlerTests
    {
        [TestCase("Content-Length: -1\r\n")]
        [TestCase("Content-Length: +1\r\n")]
        [TestCase("Content-Length: 0\r\nContent-Length: 1\r\n")]
        [TestCase("Host: localhost:8080\r\nHost: attacker.invalid\r\n")]
        [TestCase("Transfer-Encoding: identity\r\n")]
        [TestCase("Broken header\r\n")]
        public void ParseRequest_RejectsAmbiguousFraming(string headers)
        {
            using var stream = new MemoryStream(Encoding.ASCII.GetBytes(
                "POST /mcp HTTP/1.1\r\n" + headers + "\r\nx"));
            Assert.That(TcpHttpHandler.ParseRequest(stream).Error, Is.Not.Null);
        }

        #region ParseRequest — Valid Requests

        [Test]
        public void ParseRequest_ValidGetRequest_ParsesCorrectly()
        {
            var raw = "GET /mcp HTTP/1.1\r\nHost: 127.0.0.1:8080\r\n\r\n";
            var stream = new MemoryStream(Encoding.ASCII.GetBytes(raw));

            var request = TcpHttpHandler.ParseRequest(stream);

            Assert.That(request.Error, Is.Null);
            Assert.That(request.Method, Is.EqualTo("GET"));
            Assert.That(request.Path, Is.EqualTo("/mcp"));
            Assert.That(request.HttpVersion, Is.EqualTo("HTTP/1.1"));
        }

        [Test]
        public void ParseRequest_ValidPostWithBody_ParsesBody()
        {
            var body = @"{""jsonrpc"":""2.0"",""method"":""ping"",""id"":1}";
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var raw = $"POST /mcp HTTP/1.1\r\nHost: 127.0.0.1:8080\r\nContent-Length: {bodyBytes.Length}\r\n\r\n{body}";
            var stream = new MemoryStream(Encoding.ASCII.GetBytes(raw));

            var request = TcpHttpHandler.ParseRequest(stream);

            Assert.That(request.Error, Is.Null);
            Assert.That(request.Method, Is.EqualTo("POST"));
            Assert.That(request.Body, Is.Not.Null);
            Assert.That(request.Body.Length, Is.EqualTo(bodyBytes.Length));
            Assert.That(Encoding.UTF8.GetString(request.Body), Is.EqualTo(body));
        }

        [Test]
        public void ParseRequest_HeadersParsedCaseInsensitive()
        {
            var raw = "GET / HTTP/1.1\r\nhost: localhost:8080\r\nContent-Type: application/json\r\n\r\n";
            var stream = new MemoryStream(Encoding.ASCII.GetBytes(raw));

            var request = TcpHttpHandler.ParseRequest(stream);

            Assert.That(request.Error, Is.Null);
            Assert.That(request.Headers.ContainsKey("Host"), Is.True, "Header lookup should be case-insensitive.");
            Assert.That(request.Headers["host"], Is.EqualTo("localhost:8080"));
        }

        #endregion

        #region ParseRequest — Malformed Requests

        [Test]
        public void ParseRequest_EmptyStream_ReturnsError()
        {
            var stream = new MemoryStream(new byte[0]);
            var request = TcpHttpHandler.ParseRequest(stream);
            Assert.That(request.Error, Is.Not.Null);
        }

        [Test]
        public void ParseRequest_MalformedRequestLine_ReturnsError()
        {
            var raw = "BADREQUEST\r\n\r\n";
            var stream = new MemoryStream(Encoding.ASCII.GetBytes(raw));

            var request = TcpHttpHandler.ParseRequest(stream);
            Assert.That(request.Error, Is.Not.Null);
            Assert.That(request.Error, Does.Contain("Malformed"));
        }

        [Test]
        public void ParseRequest_InvalidHttpVersion_ReturnsError()
        {
            var raw = "GET / FTP/1.0\r\n\r\n";
            var stream = new MemoryStream(Encoding.ASCII.GetBytes(raw));

            var request = TcpHttpHandler.ParseRequest(stream);
            Assert.That(request.Error, Is.Not.Null);
            Assert.That(request.Error, Does.Contain("HTTP version").IgnoreCase);
        }

        [Test]
        public void ParseRequest_InvalidContentLength_ReturnsError()
        {
            var raw = "POST / HTTP/1.1\r\nContent-Length: abc\r\n\r\n";
            var stream = new MemoryStream(Encoding.ASCII.GetBytes(raw));

            var request = TcpHttpHandler.ParseRequest(stream);
            Assert.That(request.Error, Is.Not.Null);
            Assert.That(request.Error, Does.Contain("Content-Length"));
        }

        #endregion

        #region ParseRequest — Oversized Bodies

        [Test]
        public void ParseRequest_OversizedBody_ReturnsError()
        {
            int maxBodySize = 100;
            var raw = $"POST / HTTP/1.1\r\nContent-Length: 200\r\n\r\n";
            var stream = new MemoryStream(Encoding.ASCII.GetBytes(raw));

            var request = TcpHttpHandler.ParseRequest(stream, maxBodySize);
            Assert.That(request.Error, Is.Not.Null);
            Assert.That(request.Error, Does.Contain("exceeds"));
        }

        #endregion

        #region ParseRequest — Missing Headers

        [Test]
        public void ParseRequest_NoContentLength_NoBody()
        {
            var raw = "POST / HTTP/1.1\r\nHost: localhost\r\n\r\n";
            var stream = new MemoryStream(Encoding.ASCII.GetBytes(raw));

            var request = TcpHttpHandler.ParseRequest(stream);
            Assert.That(request.Error, Is.Null);
            Assert.That(request.Body, Is.Null);
        }

        [Test]
        public void ParseRequest_ZeroContentLength_NoBody()
        {
            var raw = "POST / HTTP/1.1\r\nContent-Length: 0\r\n\r\n";
            var stream = new MemoryStream(Encoding.ASCII.GetBytes(raw));

            var request = TcpHttpHandler.ParseRequest(stream);
            Assert.That(request.Error, Is.Null);
            Assert.That(request.Body, Is.Null);
        }

        #endregion

        #region ParseRequest — Chunked Encoding

        [Test]
        public void ParseRequest_ChunkedEncoding_Rejected()
        {
            var raw = "POST / HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n";
            var stream = new MemoryStream(Encoding.ASCII.GetBytes(raw));

            var request = TcpHttpHandler.ParseRequest(stream);
            Assert.That(request.Error, Is.Not.Null);
            Assert.That(request.Error, Does.Contain("chunked").IgnoreCase);
        }

        #endregion

        #region ParseRequest — Connection Close

        [Test]
        public void ParseRequest_BodyReadIncompleteOnConnectionClose_ReturnsError()
        {
            // Declare 100 bytes body but only provide 10
            var raw = "POST / HTTP/1.1\r\nContent-Length: 100\r\n\r\n0123456789";
            var stream = new MemoryStream(Encoding.ASCII.GetBytes(raw));

            var request = TcpHttpHandler.ParseRequest(stream);
            Assert.That(request.Error, Is.Not.Null);
        }

        #endregion

        #region WriteResponse

        [Test]
        public void WriteResponse_WritesValidHttpResponse()
        {
            var output = new MemoryStream();
            var body = Encoding.UTF8.GetBytes("Hello");

            TcpHttpHandler.WriteResponse(output, 200, "OK", null, body);

            output.Position = 0;
            var responseText = Encoding.ASCII.GetString(output.ToArray());

            Assert.That(responseText, Does.StartWith("HTTP/1.1 200 OK"));
            Assert.That(responseText, Does.Contain("Connection: close"));
            Assert.That(responseText, Does.Contain("Content-Length: 5"));
            Assert.That(responseText, Does.Contain("Hello"));
        }

        [Test]
        public void WriteResponse_NoBody_WritesZeroContentLength()
        {
            var output = new MemoryStream();
            TcpHttpHandler.WriteResponse(output, 204, "No Content", null, null);

            output.Position = 0;
            var responseText = Encoding.ASCII.GetString(output.ToArray());

            Assert.That(responseText, Does.Contain("Content-Length: 0"));
        }

        [Test]
        public void WriteResponse_CustomHeaders_Included()
        {
            var output = new MemoryStream();
            var headers = new System.Collections.Generic.Dictionary<string, string>
            {
                { "X-Custom", "TestValue" },
                { "MCP-Session-Id", "sess-123" }
            };

            TcpHttpHandler.WriteResponse(output, 200, "OK", headers, null);

            output.Position = 0;
            var responseText = Encoding.ASCII.GetString(output.ToArray());

            Assert.That(responseText, Does.Contain("X-Custom: TestValue"));
            Assert.That(responseText, Does.Contain("MCP-Session-Id: sess-123"));
        }

        [Test]
        public void WriteResponse_DuplicateConnectionHeader_NotDuplicated()
        {
            var output = new MemoryStream();
            var headers = new System.Collections.Generic.Dictionary<string, string>
            {
                { "Connection", "keep-alive" } // should be ignored — always closes
            };

            TcpHttpHandler.WriteResponse(output, 200, "OK", headers, null);

            output.Position = 0;
            var responseText = Encoding.ASCII.GetString(output.ToArray());

            // Should have exactly one Connection: close
            var idx1 = responseText.IndexOf("Connection: close", StringComparison.Ordinal);
            var idx2 = responseText.IndexOf("Connection:", idx1 + 1, StringComparison.Ordinal);
            Assert.That(idx1, Is.GreaterThanOrEqualTo(0));
            Assert.That(idx2, Is.EqualTo(-1), "Connection header should appear only once.");
        }

        #endregion
    }
}
