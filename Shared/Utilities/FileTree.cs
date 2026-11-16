using System;
using System.Collections.Generic;
using System.IO;

namespace I1rr.UnityMcp.Shared
{
    public static class FileTree
    {
        // Enumerate one level at a time so a linked directory is never a recursion root.
        public static IEnumerable<FileSystemInfo> Walk(string root, bool recursive, Func<bool> stop = null, string pattern = "*")
        {
            if (pattern.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || pattern == "..")
                throw new ArgumentException("The filter must be a filename pattern.", nameof(pattern));

            var options = new EnumerationOptions {
                AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true,
                MatchType = MatchType.Win32
            };
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(root));
            while (pending.Count > 0 && stop?.Invoke() != true)
            {
                var dir = pending.Pop();
                if (McpPathValidator.ContainsSymlink(dir.FullName)) continue;
                foreach (var entry in dir.EnumerateFileSystemInfos(pattern, options))
                {
                    if (stop?.Invoke() == true) yield break;
                    if ((entry.Attributes & FileAttributes.ReparsePoint) == 0) yield return entry;
                }
                if (!recursive) continue;
                foreach (var child in dir.EnumerateDirectories("*", options))
                {
                    if (stop?.Invoke() == true) yield break;
                    if ((child.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(child);
                }
            }
        }
    }
}
