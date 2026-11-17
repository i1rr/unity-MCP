using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace I1rr.UnityMcp.Editor
{
    /// <summary>
    /// Bind one physical keeper explicitly. Windows can retain several copies after a UPM
    /// path change; a bare DLL name does not identify the provider whose state we inspect.
    /// Loaded modules stay resident for the Editor lifetime; never unload code with live threads.
    /// </summary>
    internal sealed class NativeKeeperModule
    {
        private readonly IntPtr _handle;
        internal string Path { get; }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Query();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int IntArgument(int value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Command();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void ClientCommand(int client);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Read(int client, [Out] byte[] data, int length);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Write(int client, [In] byte[] data, int length);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Stage(int client, [In] byte[] data, int length);

        internal readonly IntArgument Start, Dequeue, IsClientConnected;
        internal readonly Query IsRunning, GetPort;
        internal readonly Command Attach, Detach, FlushStaged, Stop;
        internal readonly ClientCommand CloseClient;
        internal readonly Read Recv;
        internal readonly Write Send;
        internal readonly Stage StageResponse;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetModuleHandleW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        internal static NativeKeeperModule Open(string path) => new NativeKeeperModule(path);

        private NativeKeeperModule(string path)
        {
            Path = System.IO.Path.GetFullPath(path);
            // One reference per physical provider, rather than one extra reference per reload.
            _handle = GetModuleHandleW(Path);
            if (_handle == IntPtr.Zero) _handle = LoadLibraryW(Path);
            if (_handle == IntPtr.Zero)
                throw new DllNotFoundException("Could not load the package's native socket keeper.",
                    new Win32Exception(Marshal.GetLastWin32Error()));

            // Validate the entire ABI before a migration can stop any existing provider.
            Start = Export<IntArgument>("mcpkeeper_start");
            IsRunning = Export<Query>("mcpkeeper_is_running");
            GetPort = Export<Query>("mcpkeeper_get_port");
            Attach = Export<Command>("mcpkeeper_attach");
            Detach = Export<Command>("mcpkeeper_detach");
            Dequeue = Export<IntArgument>("mcpkeeper_dequeue");
            Recv = Export<Read>("mcpkeeper_recv");
            Send = Export<Write>("mcpkeeper_send");
            IsClientConnected = Export<IntArgument>("mcpkeeper_is_client_connected");
            CloseClient = Export<ClientCommand>("mcpkeeper_close_client");
            StageResponse = Export<Stage>("mcpkeeper_stage_response");
            FlushStaged = Export<Command>("mcpkeeper_flush_staged");
            Stop = Export<Command>("mcpkeeper_stop");
        }

        private T Export<T>(string name) where T : Delegate
        {
            var address = GetProcAddress(_handle, name);
            if (address == IntPtr.Zero) throw new EntryPointNotFoundException(name);
            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }

        internal static string[] LoadedPackagePaths()
        {
            using (var process = Process.GetCurrentProcess())
                return process.Modules.Cast<ProcessModule>()
                    .Select(module => module.FileName)
                    .Where(path => path.Replace('\\', '/').EndsWith(
                        "/Editor/Plugins/Windows/x86_64/mcpkeeper.dll", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
        }

        /// <summary>
        /// Run only after the previous managed domain has drained/staged its work. Keep the
        /// current provider on ordinary reload; retire other physical providers before binding.
        /// Returns an owned former port to prefer, never an arbitrary process listener.
        /// </summary>
        internal int RetirePreviousProviders(IEnumerable<string> paths, int previousPort)
        {
            var seen = new HashSet<IntPtr> { _handle };
            var previous = paths.Select(Open).Where(module => seen.Add(module._handle))
                .Where(module => module.IsRunning() == 1).ToArray();
            var activePort = IsRunning() == 1 ? GetPort() : 0;
            var ports = previous.Select(module => module.GetPort()).ToArray();
            foreach (var module in previous)
            {
                module.Detach();
                module.FlushStaged();
                module.Stop();
                if (module.IsRunning() != 0)
                    throw new InvalidOperationException("Previous native keeper did not stop; refusing another listener.");
            }
            if (activePort > 0) return activePort;
            if (ports.Contains(previousPort)) return previousPort;
            return ports.FirstOrDefault();
        }
    }
}
