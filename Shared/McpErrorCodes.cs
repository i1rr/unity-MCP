namespace I1rr.UnityMcp.Shared
{
    /// <summary>
    /// Well-known error codes returned by MCP tools.
    /// Use these constants with <see cref="McpError.Create"/> to produce standardized error responses.
    /// </summary>
    public static class McpErrorCodes
    {
        /// <summary>A required parameter is missing or has an invalid type/value.</summary>
        public const string InvalidParameter = "invalid_parameter";

        /// <summary>A required parameter was not supplied.</summary>
        public const string MissingParameter = "missing_parameter";

        /// <summary>The requested resource (GameObject, asset, file, etc.) was not found.</summary>
        public const string NotFound = "not_found";

        /// <summary>The operation requires Play Mode but the Editor is not playing.</summary>
        public const string NotInPlayMode = "not_in_play_mode";

        /// <summary>The current editor, player, or graphics state cannot perform the operation.</summary>
        public const string InvalidState = "invalid_state";

        /// <summary>A path failed security validation (outside allowed roots).</summary>
        public const string PathViolation = "path_violation";

        /// <summary>The operation exceeded its allowed time budget.</summary>
        public const string Timeout = "timeout";

        /// <summary>An unexpected internal error occurred.</summary>
        public const string InternalError = "internal_error";

        /// <summary>A configured maximum limit was reached (buffer size, session count, etc.).</summary>
        public const string MaxLimitReached = "max_limit_reached";

        /// <summary>A Perforce operation failed.</summary>
        public const string P4Error = "p4_error";

        /// <summary>Scripts have compilation errors preventing the operation.</summary>
        public const string CompilationError = "compilation_error";

        /// <summary>The requested tool or feature is disabled by configuration.</summary>
        public const string Disabled = "disabled";

        /// <summary>An ambiguous reference was provided (matches multiple targets).</summary>
        public const string Ambiguous = "ambiguous";

        /// <summary>The file or resource is read-only and cannot be modified.</summary>
        public const string ReadOnly = "read_only";

        /// <summary>A destructive action requires explicit confirmation.</summary>
        public const string ConfirmationRequired = "confirmation_required";
    }
}
