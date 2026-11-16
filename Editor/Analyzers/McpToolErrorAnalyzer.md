# McpTool Error Handling Analyzer — Design Document

## Rule: MCP0001 — McpTool methods must use McpError.Create()

**Severity**: Warning
**Category**: Correctness

### Description

Methods decorated with `[McpTool]` should return errors using `McpError.Create()` rather than
constructing raw `JObject` error responses manually. This ensures consistent error codes,
proper error metadata, and compatibility with client-side error handling.

### Violation Examples

```csharp
// BAD: Raw JObject error
[McpTool("my_tool", "Does something")]
public static JObject MyTool(JObject parameters)
{
    if (parameters["path"] == null)
        return new JObject { ["error"] = "Missing path" };  // MCP0001
}
```

### Correct Usage

```csharp
// GOOD: Using McpError.Create()
[McpTool("my_tool", "Does something")]
public static JObject MyTool(JObject parameters)
{
    if (parameters["path"] == null)
        return McpError.Create(McpErrorCode.InvalidParams, "Missing path");
}
```

### Implementation Notes

A full Roslyn analyzer requires a separate .NET Standard 2.0 project targeting
`Microsoft.CodeAnalysis.CSharp`. For Unity packages, this is typically distributed as a
DLL in the package's `Analyzers/` folder with a corresponding `.asmdef` that marks it
as a RoslynAnalyzer.

**Steps to implement a compiled analyzer:**

1. Create a .NET Standard 2.0 class library project outside Unity
2. Reference `Microsoft.CodeAnalysis.CSharp` (4.x+)
3. Implement `DiagnosticAnalyzer` that:
   - Registers for `SyntaxKind.MethodDeclaration`
   - Checks if method has `[McpTool]` attribute
   - Walks return statements looking for `new JObject { ["error"] = ... }`
   - Reports `MCP0001` diagnostic if found
4. Build the DLL and place in `Editor/Analyzers/`
5. Create an `.asmdef` with `"defineConstraints": ["UNITY_EDITOR"]` and
   reference override set to `RoslynAnalyzer`

**Current enforcement**: The `.editorconfig` in this package configures CA1031
(Do not catch general exception types) as a suggestion, which partially catches
the pattern of swallowing errors. The full MCP0001 rule requires the compiled analyzer
described above.
