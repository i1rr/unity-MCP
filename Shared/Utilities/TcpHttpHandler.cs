using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Scripting;

namespace I1rr.UnityMcp.Shared
{
    /// <summary>
    /// Minimal HTTP request/response parser for raw TCP sockets.
    /// Handles buffered reads, Content-Length body parsing, and connection resets.
    /// Sends Connection: close on all responses (no keep-alive support).
    /// </summary>
    [Preserve]
    public static class TcpHttpHandler
    {
        private const int HeaderBufferSize = 8192;
        private const int ReadChunkSize = 4096;
        private static readonly byte[] CrLfCrLf = { 0x0D, 0x0A, 0x0D, 0x0A };

        [Preserve]
        public struct HttpRequest
        {
            public string Method;
            public string Path;
            public string HttpVersion;
            public Dictionary<string, string> Headers;
            public byte[] Body;
            public string Error;
        }

        /// <summary>
        /// Reads and parses an HTTP request from a stream.
        /// Returns an HttpRequest with Error set if parsing failed.
        /// </summary>
        [Preserve]
        public static HttpRequest ParseRequest(Stream stream, int maxBodySize = 1_048_576)
        {
            var result = new HttpRequest();

            byte[] headerBytes;
            int headerEnd;
            try
            {
                (headerBytes, headerEnd) = ReadUntilHeaderEnd(stream);
            }
            catch (IOException ex)
            {
                result.Error = $"Connection reset during header read: {ex.Message}";
                return result;
            }
            catch (ObjectDisposedException)
            {
                result.Error = "Connection closed during header read";
                return result;
            }

            if (headerBytes == null)
            {
                result.Error = "Failed to read HTTP headers (buffer exceeded or connection closed)";
                return result;
            }

            // Parse the header block as ASCII
            var headerText = Encoding.ASCII.GetString(headerBytes, 0, headerEnd);
            var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);

            if (lines.Length == 0)
            {
                result.Error = "Empty request";
                return result;
            }

            // Parse request line: METHOD PATH HTTP/VERSION
            var requestLine = lines[0];
            var parts = requestLine.Split(' ');
            if (parts.Length != 3 || string.IsNullOrEmpty(parts[0]) || !parts[1].StartsWith("/", StringComparison.Ordinal))
            {
                result.Error = $"Malformed request line: {requestLine}";
                return result;
            }

            result.Method = parts[0];
            result.Path = parts[1];
            result.HttpVersion = parts[2];

            // Validate HTTP version
            if (result.HttpVersion != "HTTP/1.1" && result.HttpVersion != "HTTP/1.0")
            {
                result.Error = $"Invalid HTTP version: {result.HttpVersion}";
                return result;
            }

            // Parse headers
            result.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrEmpty(line))
                    break;

                var colonIndex = line.IndexOf(':');
                if (colonIndex <= 0 || char.IsWhiteSpace(line[0]))
                {
                    result.Error = "Malformed HTTP header";
                    return result;
                }

                var name = line.Substring(0, colonIndex).Trim();
                var value = line.Substring(colonIndex + 1).Trim();
                if (result.Headers.ContainsKey(name))
                {
                    result.Error = $"Duplicate HTTP header: {name}";
                    return result;
                }
                result.Headers[name] = value;
            }

            // Check for chunked encoding (reject)
            if (result.Headers.ContainsKey("Transfer-Encoding"))
            {
                result.Error = "Chunked and other transfer encodings are not supported";
                return result;
            }

            // Read body if Content-Length present
            if (result.Headers.TryGetValue("Content-Length", out var clStr))
            {
                if (!int.TryParse(clStr, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var contentLength))
                {
                    result.Error = $"Invalid Content-Length: {clStr}";
                    return result;
                }

                if (contentLength > maxBodySize)
                {
                    result.Error = $"Content-Length {contentLength} exceeds max body size {maxBodySize}";
                    return result;
                }

                if (contentLength > 0)
                {
                    // Some body bytes may already be in the header buffer after \r\n\r\n
                    int bodyBytesAlreadyRead = headerBytes.Length - (headerEnd + 4);
                    try
                    {
                        result.Body = ReadBody(stream, headerBytes, headerEnd + 4, bodyBytesAlreadyRead, contentLength);
                    }
                    catch (IOException ex)
                    {
                        result.Error = $"Connection reset during body read: {ex.Message}";
                        return result;
                    }
                    catch (ObjectDisposedException)
                    {
                        result.Error = "Connection closed during body read";
                        return result;
                    }

                    if (result.Body == null)
                    {
                        result.Error = "Connection closed before body fully received";
                        return result;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Writes an HTTP response to a stream.
        /// Always includes Connection: close.
        /// </summary>
        [Preserve]
        public static void WriteResponse(Stream stream, int statusCode, string statusText,
            Dictionary<string, string> headers, byte[] body)
        {
            var sb = BufferPool.RentStringBuilder(256);
            try
            {
                sb.Append("HTTP/1.1 ");
                sb.Append(statusCode);
                sb.Append(' ');
                sb.Append(statusText);
                sb.Append("\r\n");

                sb.Append("Connection: close\r\n");

                if (body != null && body.Length > 0)
                {
                    sb.Append("Content-Length: ");
                    sb.Append(body.Length);
                    sb.Append("\r\n");
                }
                else
                {
                    sb.Append("Content-Length: 0\r\n");
                }

                if (headers != null)
                {
                    foreach (var kvp in headers)
                    {
                        // Skip Connection/Content-Length since we handle them above
                        if (string.Equals(kvp.Key, "Connection", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(kvp.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                            continue;

                        sb.Append(kvp.Key);
                        sb.Append(": ");
                        sb.Append(kvp.Value);
                        sb.Append("\r\n");
                    }
                }

                sb.Append("\r\n");

                var headerData = Encoding.ASCII.GetBytes(sb.ToString());
                BufferPool.ReturnStringBuilder(sb);
                sb = null;

                stream.Write(headerData, 0, headerData.Length);

                if (body != null && body.Length > 0)
                {
                    stream.Write(body, 0, body.Length);
                }

                stream.Flush();
            }
            finally
            {
                if (sb != null)
                    BufferPool.ReturnStringBuilder(sb);
            }
        }

        /// <summary>
        /// Reads from stream until \r\n\r\n is found. Returns (buffer, endIndex) where
        /// endIndex is the position of the first byte of \r\n\r\n in the buffer.
        /// Note: The returned buffer is rented from ArrayPool. Caller must return it via BufferPool.ReturnBuffer
        /// after use, OR the buffer is trimmed to a new array and the rented buffer is returned internally.
        /// </summary>
        private static (byte[] buffer, int headerEndIndex) ReadUntilHeaderEnd(Stream stream)
        {
            var buffer = BufferPool.RentBuffer(HeaderBufferSize);
            int totalRead = 0;

            while (totalRead < HeaderBufferSize)
            {
                int bytesRead = stream.Read(buffer, totalRead, Math.Min(ReadChunkSize, HeaderBufferSize - totalRead));
                if (bytesRead == 0)
                {
                    BufferPool.ReturnBuffer(buffer);
                    return (null, -1); // Connection closed
                }

                totalRead += bytesRead;

                // Search for \r\n\r\n in the buffer
                int searchStart = Math.Max(0, totalRead - bytesRead - 3);
                int idx = FindSequence(buffer, searchStart, totalRead, CrLfCrLf);
                if (idx >= 0)
                {
                    // Always copy to a right-sized array and return the rented buffer
                    var trimmed = new byte[totalRead];
                    Buffer.BlockCopy(buffer, 0, trimmed, 0, totalRead);
                    BufferPool.ReturnBuffer(buffer);
                    return (trimmed, idx);
                }
            }

            // Header exceeded buffer — reject
            BufferPool.ReturnBuffer(buffer);
            return (null, -1);
        }

        /// <summary>
        /// Reads exactly contentLength bytes, combining any already-buffered bytes with additional reads.
        /// </summary>
        private static byte[] ReadBody(Stream stream, byte[] headerBuffer, int bodyStart,
            int bytesAlreadyInBuffer, int contentLength)
        {
            var body = new byte[contentLength];
            int copied = 0;

            // Copy any bytes already read past the header
            if (bytesAlreadyInBuffer > 0)
            {
                int toCopy = Math.Min(bytesAlreadyInBuffer, contentLength);
                Buffer.BlockCopy(headerBuffer, bodyStart, body, 0, toCopy);
                copied = toCopy;
            }

            // Read remaining from stream
            while (copied < contentLength)
            {
                int bytesRead = stream.Read(body, copied, contentLength - copied);
                if (bytesRead == 0)
                    return null; // Connection closed before body complete

                copied += bytesRead;
            }

            return body;
        }

        /// <summary>
        /// Finds a byte sequence within a buffer range. Returns index of first match or -1.
        /// </summary>
        private static int FindSequence(byte[] buffer, int start, int end, byte[] sequence)
        {
            int seqLen = sequence.Length;
            int searchEnd = end - seqLen;

            for (int i = start; i <= searchEnd; i++)
            {
                bool match = true;
                for (int j = 0; j < seqLen; j++)
                {
                    if (buffer[i + j] != sequence[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                    return i;
            }
            return -1;
        }
    }
}
