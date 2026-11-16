using System;
using System.IO;
using System.Runtime.InteropServices;

namespace I1rr.UnityMcp.Shared
{
    /// <summary>
    /// Result of a path validation check — carries the resolved canonical path on success
    /// or a human-readable error message on failure.
    /// </summary>
    public readonly struct PathValidationResult
    {
        public bool IsValid { get; }
        public string ResolvedPath { get; }
        public string Error { get; }

        private PathValidationResult(bool isValid, string resolvedPath, string error)
        {
            IsValid = isValid;
            ResolvedPath = resolvedPath;
            Error = error;
        }

        public static PathValidationResult Success(string resolvedPath) =>
            new PathValidationResult(true, resolvedPath, null);

        public static PathValidationResult Failure(string error) =>
            new PathValidationResult(false, null, error);
    }

    /// <summary>
    /// Validates and resolves file paths to prevent directory traversal attacks.
    /// All paths must resolve within the configured project root.
    /// </summary>
    public static class McpPathValidator
    {
        private static string _projectRoot;

        /// <summary>
        /// Project root directory, normalized with trailing directory separator.
        /// Must be set once at server startup.
        /// </summary>
        public static string ProjectRoot
        {
            get => _projectRoot;
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                    throw new ArgumentException("Project root cannot be null or empty.", nameof(value));

                var normalized = Path.GetFullPath(value);
                if (!normalized.EndsWith(Path.DirectorySeparatorChar.ToString()))
                    normalized += Path.DirectorySeparatorChar;

                _projectRoot = normalized;
            }
        }

        /// <summary>
        /// Validates and resolves a path relative to the project root.
        /// </summary>
        public static PathValidationResult ValidatePath(string inputPath)
        {
            return ValidateAndResolvePath(inputPath, null);
        }

        /// <summary>
        /// Validates and resolves a path, optionally constraining to a sub-directory within the project root.
        /// </summary>
        /// <param name="inputPath">The input path to validate.</param>
        /// <param name="subRoot">Optional sub-directory constraint (e.g., "Assets/"). Relative to project root.</param>
        public static PathValidationResult ValidateAndResolvePath(string inputPath, string subRoot = null)
        {
            if (_projectRoot == null)
                return PathValidationResult.Failure("project_root_not_set: McpPathValidator.ProjectRoot must be configured before validating paths.");

            if (string.IsNullOrWhiteSpace(inputPath))
                return PathValidationResult.Failure("empty_path");

            // Check for null bytes — some .NET implementations truncate at null,
            // allowing bypass of path checks.
            if (inputPath.Contains('\0'))
                return PathValidationResult.Failure("null_byte");

            // Reject paths containing double-quote characters.
            // No legitimate Unity asset path contains double quotes, and they can be
            // used for argument injection when paths are passed to external processes.
            if (inputPath.Contains('"'))
                return PathValidationResult.Failure("invalid_char_quote");

            // Reject UNC paths
            if (IsUncPath(inputPath))
                return PathValidationResult.Failure("unc_path");

            // Reject absolute paths
            if (IsAbsolutePath(inputPath))
                return PathValidationResult.Failure("absolute_path");

            // Determine the effective root (project root or sub-root within it)
            string effectiveRoot = _projectRoot;
            if (!string.IsNullOrEmpty(subRoot))
            {
                // Ensure subRoot doesn't contain null bytes or escape attempts
                if (subRoot.Contains('\0'))
                    return PathValidationResult.Failure("null_byte");

                effectiveRoot = Path.GetFullPath(Path.Combine(_projectRoot, subRoot));
                if (!effectiveRoot.EndsWith(Path.DirectorySeparatorChar.ToString()))
                    effectiveRoot += Path.DirectorySeparatorChar;

                // The sub-root itself must be within the project root
                if (!effectiveRoot.StartsWith(_projectRoot, GetPathComparison()))
                    return PathValidationResult.Failure("outside_project_root");
            }

            // Resolve the full path
            string resolved;
            try
            {
                resolved = Path.GetFullPath(Path.Combine(effectiveRoot, inputPath));
            }
            catch (Exception)
            {
                return PathValidationResult.Failure("path_traversal");
            }

            // Verify resolved path is within effective root.
            // Must compare with trailing separator to prevent prefix attacks:
            // e.g., C:\Projects\MyGame must not match C:\Projects\MyGameEvil\payload.exe
            if (!IsWithinRoot(resolved, effectiveRoot))
                return PathValidationResult.Failure("outside_project_root");

            // Also verify it's within the project root (in case subRoot logic has issues)
            if (!IsWithinRoot(resolved, _projectRoot))
                return PathValidationResult.Failure("outside_project_root");

            // Check for symlinks/junctions along the path
            if (ContainsSymlink(resolved))
                return PathValidationResult.Failure("symlink_not_allowed");

            return PathValidationResult.Success(resolved);
        }

        /// <summary>
        /// Checks each existing segment of the path for FileAttributes.ReparsePoint
        /// (symlinks/junctions that could redirect I/O outside the project tree).
        /// </summary>
        public static bool ContainsSymlink(string resolvedPath)
        {
            if (string.IsNullOrEmpty(resolvedPath))
                return false;

            // Walk from root toward leaf, checking each existing directory segment
            string current = Path.GetPathRoot(resolvedPath);
            if (string.IsNullOrEmpty(current))
                return false;

            // Get remaining path segments after root
            string relativePart = resolvedPath.Substring(current.Length);
            string[] segments = relativePart.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            foreach (string segment in segments)
            {
                current = Path.Combine(current, segment);

                try
                {
                    var attributes = File.GetAttributes(current);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                        return true;
                }
                catch (FileNotFoundException) { break; }
                catch (DirectoryNotFoundException) { break; }
                catch (UnauthorizedAccessException) { return true; }
                catch (IOException) { return true; }
            }

            return false;
        }

        /// <summary>
        /// Detects absolute paths in a platform-aware manner.
        /// </summary>
        public static bool IsAbsolutePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            // Unix-style absolute path
            if (path[0] == '/' || path[0] == '\\')
                return true;

            // Windows drive letter (e.g., C: or C:\)
            if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]))
                return true;

            return false;
        }

        /// <summary>
        /// Detects UNC paths (\\server\share or //server/share).
        /// </summary>
        public static bool IsUncPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length < 2)
                return false;

            // Standard UNC: \\server\share
            if (path[0] == '\\' && path[1] == '\\')
                return true;

            // Forward-slash UNC variant: //server/share
            if (path[0] == '/' && path[1] == '/')
                return true;

            return false;
        }

        private static bool IsWithinRoot(string resolvedPath, string root)
        {
            var comparison = GetPathComparison();

            // Exact match with root (without trailing separator) is the root itself — allow it
            string rootWithoutSep = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(resolvedPath, rootWithoutSep, comparison))
                return true;

            // Must start with root (which includes trailing separator)
            return resolvedPath.StartsWith(root, comparison);
        }

        private static StringComparison GetPathComparison()
        {
            // Windows file system is case-insensitive; Unix is case-sensitive
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
        }
    }
}
