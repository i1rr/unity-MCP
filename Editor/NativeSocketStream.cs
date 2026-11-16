using System;
using System.IO;

namespace I1rr.UnityMcp.Editor
{
    /// <summary>
    /// Stream adapter that wraps a native socket client ID from <see cref="NativeSocketKeeper"/>.
    /// Enables <see cref="I1rr.UnityMcp.Shared.TcpHttpHandler"/> to read/write via the native
    /// socket keeper without requiring a managed Socket or NetworkStream.
    /// </summary>
    internal sealed class NativeSocketStream : Stream
    {
        private readonly int _clientId;
        private bool _disposed;

        public NativeSocketStream(int clientId)
        {
            if (clientId < 0)
                throw new ArgumentOutOfRangeException(nameof(clientId), "Invalid native client ID.");
            _clientId = clientId;
        }

        /// <summary>
        /// The native client ID this stream wraps.
        /// </summary>
        public int ClientId => _clientId;

        public override bool CanRead => !_disposed;
        public override bool CanWrite => !_disposed;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(NativeSocketStream));
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
                throw new ArgumentOutOfRangeException();

            if (count == 0) return 0;

            // NativeSocketKeeper.Recv expects a buffer starting at index 0.
            // If offset != 0, we need a temporary buffer.
            byte[] recvBuf;
            if (offset == 0)
            {
                recvBuf = buffer;
            }
            else
            {
                recvBuf = new byte[count];
            }

            int result = NativeSocketKeeper.Recv(_clientId, recvBuf, count);

            if (result < 0)
                throw new IOException("Native socket recv failed.");

            if (result > 0 && offset != 0)
                Buffer.BlockCopy(recvBuf, 0, buffer, offset, result);

            return result; // 0 = connection closed gracefully
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(NativeSocketStream));
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
                throw new ArgumentOutOfRangeException();

            if (count == 0) return;

            // NativeSocketKeeper.Send expects a contiguous buffer.
            byte[] sendBuf;
            if (offset == 0 && count == buffer.Length)
            {
                sendBuf = buffer;
            }
            else
            {
                sendBuf = new byte[count];
                Buffer.BlockCopy(buffer, offset, sendBuf, 0, count);
            }

            int result = NativeSocketKeeper.Send(_clientId, sendBuf, count);
            if (result < 0)
                throw new IOException("Native socket send failed.");
        }

        public override void Flush()
        {
            // TCP with NoDelay sends immediately — no buffering to flush
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                // Don't close the native client here — the server manages client lifecycle
                // via explicit CloseClient calls after writing the response.
            }
            base.Dispose(disposing);
        }
    }
}
