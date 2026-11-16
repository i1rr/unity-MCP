using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using I1rr.UnityMcp.Shared;

namespace I1rr.UnityMcp.Editor
{
    /// <summary>A bounded, read-only preview. Markdown and linked files are never executed.</summary>
    internal sealed class McpBacklogReader
    {
        internal const int MaxBytes = 1024 * 1024;
        internal const int MaxIssues = 100;
        internal sealed class Issue
        {
            internal string Title, Status = "Unspecified", Summary = "";
            internal int Line;
            internal readonly List<Link> Links = new();
        }
        internal sealed class Link
        {
            internal string Label, Target;
        }
        internal sealed class Snapshot
        {
            internal string Path, Error;
            internal bool Exists;
            internal int Total;
            internal Issue[] Issues = Array.Empty<Issue>();
        }
        private static readonly Regex Heading = new(@"^ {0,3}(#{2,6})[ \t]+(.+?)\s*$");
        private static readonly Regex Status = new(@"^\s*(?:[-*]\s+)?(?:\*\*)?Status:?(?:\*\*)?:?\s+(.+)$", RegexOptions.IgnoreCase);
        private static readonly Regex Links = new(@"\[([^\]\r\n]{1,120})\]\(([^)\r\n]{1,1024})\)");
        private string _path;
        private long _length;
        private DateTime _modified;
        private Snapshot _cached;

        internal Snapshot Read(string projectRoot)
        {
            string path = System.IO.Path.Combine(projectRoot, McpBacklogSetup.LogName);
            var snapshot = new Snapshot { Path = path };
            try
            {
                if (Directory.Exists(path)) throw new IOException("The MCP backlog path is a directory. Open its folder to inspect it.");
                var file = new FileInfo(path);
                if (!file.Exists) { _cached = null; return snapshot; }
                snapshot.Exists = true;
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The MCP backlog is a link; open its owner file manually.");
                if (file.Length > MaxBytes) throw new IOException("The MCP backlog exceeds 1 MiB. Open the backlog to view it.");
                if (_cached != null && _path == path && _length == file.Length && _modified == file.LastWriteTimeUtc)
                    return _cached;
                using var reader = new StreamReader(path, new UTF8Encoding(false, true), true);
                var buffer = new char[MaxBytes + 1];
                int count = reader.ReadBlock(buffer, 0, buffer.Length);
                if (count > MaxBytes) throw new IOException("The MCP backlog grew beyond the preview limit. Open the backlog to view it.");
                snapshot = Parse(projectRoot, new string(buffer, 0, count));
                _path = path;
                _length = file.Length;
                _modified = file.LastWriteTimeUtc;
                return _cached = snapshot;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is DecoderFallbackException)
            { snapshot.Error = ex.Message; _cached = null; return snapshot; }
        }

        internal static Snapshot Parse(string root, string text)
        {
            var snapshot = new Snapshot { Path = System.IO.Path.Combine(root, McpBacklogSetup.LogName), Exists = true };
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var headings = new List<(int line, int level, string title)>();
            var inCode = new bool[lines.Length];
            char fence = '\0';
            int fenceLength = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
                {
                    char marker = trimmed[0];
                    int length = trimmed.TakeWhile(c => c == marker).Count();
                    if (fence == '\0') { fence = marker; fenceLength = length; }
                    else if (marker == fence && length >= fenceLength && trimmed.Substring(length).Trim().Length == 0) fence = '\0';
                    inCode[i] = true;
                    continue;
                }
                inCode[i] = fence != '\0';
                if (inCode[i]) continue;
                var match = Heading.Match(Shorten(lines[i], 4096));
                if (!match.Success) continue;
                string title = match.Groups[2].Value;
                // Container and old generated headings are not issue entries.
                if (title.Equals("Issues", StringComparison.OrdinalIgnoreCase) || title == "Logging instructions — package managed") continue;
                headings.Add((i, match.Groups[1].Length, title));
            }
            if (headings.Count == 0) return snapshot;
            int issueLevel = headings.Min(h => h.level);
            var entries = headings.Where(h => h.level == issueLevel).ToArray();
            snapshot.Total = entries.Length;
            var issues = new List<Issue>();
            for (int n = 0; n < Math.Min(entries.Length, MaxIssues); n++)
            {
                var entry = entries[n];
                var issue = new Issue { Title = Shorten(entry.title, 160), Line = entry.line + 1 };
                int end = n + 1 < entries.Length ? entries[n + 1].line : lines.Length;
                for (int i = entry.line + 1; i < end; i++)
                {
                    if (inCode[i]) continue;
                    string line = Shorten(lines[i].Trim(), 4096);
                    var status = Status.Match(line);
                    if (status.Success) issue.Status = Shorten(status.Groups[1].Value, 60);
                    else if (issue.Summary.Length == 0 && line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal))
                        issue.Summary = Shorten(line.Replace("**", ""), 240);
                    if (issue.Links.Count >= 4) continue;
                    foreach (Match link in Links.Matches(line))
                    {
                        string target = link.Groups[2].Value.Trim().Trim('<', '>');
                        if (TryResolveLink(root, target, out _))
                            issue.Links.Add(new Link { Label = Shorten(link.Groups[1].Value, 100), Target = target });
                        if (issue.Links.Count >= 4) break;
                    }
                }
                issues.Add(issue);
            }
            snapshot.Issues = issues.ToArray();
            return snapshot;
        }

        internal static bool TryResolveLink(string root, string target, out string resolved)
        {
            resolved = null;
            if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
            {
                if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return false;
                resolved = uri.AbsoluteUri;
                return true;
            }
            try
            {
                target = Uri.UnescapeDataString(target.Split('#')[0]);
                if (string.IsNullOrWhiteSpace(target) || System.IO.Path.IsPathRooted(target) || target.Contains(":")) return false;
                string prefix = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
                string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(prefix, target));
                var comparison = System.IO.Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!path.StartsWith(prefix, comparison) || McpPathValidator.ContainsSymlink(path) ||
                    (!File.Exists(path) && !Directory.Exists(path))) return false;
                resolved = path;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
            { return false; }
        }

        private static string Shorten(string value, int limit) => value.Length <= limit ? value : value.Substring(0, limit - 1) + "…";
    }
}
