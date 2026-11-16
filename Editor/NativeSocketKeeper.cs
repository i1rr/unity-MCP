using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace I1rr.UnityMcp.Editor
{
    /// <summary>
    /// P/Invoke wrapper for mcpkeeper.dll — a native socket keeper that holds the
    /// TCP listening socket alive across Unity domain reloads. When C# is detached
    /// during reload, the native layer sends HTTP 503 + Retry-After to all clients.
    /// </summary>
    internal static class NativeSocketKeeper
    {
        private const string DllName = "mcpkeeper";

        private static bool _available = true;
        private static bool _checkedAvailability;

        // --- Native imports ---

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int mcpkeeper_start(int port);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int mcpkeeper_is_running();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int mcpkeeper_get_port();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void mcpkeeper_attach();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void mcpkeeper_detach();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int mcpkeeper_dequeue(int timeout_ms);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int mcpkeeper_recv(int client_id, byte[] buf, int len);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int mcpkeeper_send(int client_id, byte[] buf, int len);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int mcpkeeper_is_client_connected(int client_id);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void mcpkeeper_close_client(int client_id);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void mcpkeeper_stage_response(int client_id, byte[] data, int len);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void mcpkeeper_flush_staged();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void mcpkeeper_stop();

        // --- Public API ---

        /// <summary>
        /// Whether the native DLL is available. Checked once on first access.
        /// Returns false if DLL is missing (fallback to HttpListener).
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
                if (!_checkedAvailability)
                {
                    _checkedAvailability = true;
                    try
                    {
                        mcpkeeper_is_running();
                    }
                    catch (DllNotFoundException)
                    {
                        _available = false;
                        Debug.LogWarning("[MCP] mcpkeeper.dll not found — domain reload resilience unavailable.");
                    }
                    catch (EntryPointNotFoundException)
                    {
                        _available = false;
                        Debug.LogWarning("[MCP] mcpkeeper.dll version mismatch — domain reload resilience unavailable.");
                    }
                }
                return _available;
            }
        }

        /// <summary>
        /// Whether the native socket is currently listening (survives domain reload).
        /// </summary>
        public static bool IsRunning => IsAvailable && mcpkeeper_is_running() == 1;

        /// <summary>
        /// Returns the port the native socket is bound to.
        /// </summary>
        public static int GetPort() => mcpkeeper_get_port();

        /// <summary>
        /// Start the native listener on the given port.
        /// Returns true on success, false on failure.
        /// </summary>
        public static bool Start(int port)
        {
            int result = mcpkeeper_start(port);
            if (result != 0)
            {
                Debug.LogError($"[MCP] Native socket keeper failed to start (error={result}).");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Attach C# to the native socket keeper. Accepted connections will be
        /// queued for managed code instead of getting 503 responses.
        /// </summary>
        public static void Attach() => mcpkeeper_attach();

        /// <summary>
        /// Detach C# from the native socket keeper. New connections will
        /// receive HTTP 503 + Retry-After:3. Queued connections are drained with 503.
        /// Called before domain reload.
        /// </summary>
        public static void Detach() => mcpkeeper_detach();

        /// <summary>
        /// Dequeue the next accepted client. Blocks up to timeout_ms.
        /// Returns client ID >= 0 on success, -1 on timeout.
        /// </summary>
        public static int Dequeue(int timeoutMs) => mcpkeeper_dequeue(timeoutMs);

        /// <summary>
        /// Read data from a client socket.
        /// Returns bytes read, 0 on graceful close, -1 on error.
        /// </summary>
        public static int Recv(int clientId, byte[] buffer, int length) =>
            mcpkeeper_recv(clientId, buffer, length);

        /// <summary>
        /// Write data to a client socket. Sends all bytes (blocking).
        /// Returns bytes sent or -1 on error.
        /// </summary>
        public static int Send(int clientId, byte[] buffer, int length) =>
            mcpkeeper_send(clientId, buffer, length);

        /// <summary>
        /// Check if a client is still connected (non-blocking poll).
        /// </summary>
        public static bool IsClientConnected(int clientId) =>
            mcpkeeper_is_client_connected(clientId) == 1;

        /// <summary>
        /// Close and release a client socket slot.
        /// </summary>
        public static void CloseClient(int clientId) => mcpkeeper_close_client(clientId);

        /// <summary>
        /// Stage a pre-built HTTP response for a client. The native layer stores the bytes
        /// and attempts to send them when FlushStaged() is called. Native code survives
        /// assembly unload; a disconnected or stalled peer can still prevent delivery.
        /// </summary>
        public static void StageResponse(int clientId, byte[] data)
        {
            if (data == null || data.Length == 0) return;
            mcpkeeper_stage_response(clientId, data, data.Length);
        }

        /// <summary>
        /// Send all staged responses and gracefully close those clients.
        /// Called before domain reload to ensure deferred responses reach clients
        /// even as C# assemblies are being unloaded.
        /// </summary>
        public static void FlushStaged() => mcpkeeper_flush_staged();

        /// <summary>
        /// Full shutdown — close listening socket, join accept thread, close all clients.
        /// Only call on Editor quit, NOT on domain reload.
        /// </summary>
        public static void Stop() => mcpkeeper_stop();
    }
}
