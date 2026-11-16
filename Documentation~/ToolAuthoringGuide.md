# Tool Authoring Guide

This guide covers how to add new MCP tools to i1rr-unity-mcp. Tools are the primary interface between AI agents and the Unity Editor/Runtime.

For tools specific to a game or framework, create a [project module](Modules.md)
from the Dashboard or `manage_module`. Edit its source under `Assets/McpModules`
instead of modifying the installed package. Each module uses its own assembly
so the Dashboard can control all its tools together.

## Quick Start

1. Create a new static class in the appropriate `Tools/` folder (Editor or Runtime).
2. Add `[McpToolCategory("YourCategory")]` to the class.
3. Add a static method with signature `static JObject ToolName(JObject parameters)`.
4. Decorate it with `[McpTool("tool_name", "Description")]`.
5. The tool is auto-discovered on next domain reload.

---

## The `[McpTool]` Attribute

```csharp
[McpTool("tool_name", "Human-readable description of what this tool does.")]
public static JObject ToolName(JObject parameters) { ... }
```

### Required Properties

| Property | Type | Description |
|----------|------|-------------|
| `Name` | `string` | Snake_case tool name exposed to MCP clients |
| `Description` | `string` | Clear description for AI consumers |

### Optional Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `InputSchemaJson` | `string` | `null` | JSON Schema for parameters |
| `RequiresPlayMode` | `bool` | `false` | Gate tool to Play Mode only |
| `DeferredResponse` | `bool` | `false` | Allow returning `DeferredResult` |
| `AutoRoute` | `bool` | `false` | Auto-route between Editor/Runtime |
| `AutoRouteCanonicalName` | `string` | `null` | Override canonical name for routing |
| `Category` | `string` | `null` | Override class-level category |
| `Version` | `string` | `"1.0"` | Semantic version of the tool |
| `DeprecatedBy` | `string` | `null` | Names the replacement tool |

---

## Input Schema Definition

Define parameter schemas using `InputSchemaJson`. This is a JSON Schema string that describes accepted parameters:

```csharp
[McpTool("get_component",
    "Get component data from a GameObject",
    InputSchemaJson = @"{
        ""type"": ""object"",
        ""properties"": {
            ""instance_id"": { ""type"": ""integer"", ""description"": ""GameObject instance ID"" },
            ""component_type"": { ""type"": ""string"", ""description"": ""Fully qualified component type name"" },
            ""page"": { ""type"": ""integer"", ""description"": ""Page number (1-indexed)"", ""default"": 1 },
            ""page_size"": { ""type"": ""integer"", ""description"": ""Items per page (1-500)"", ""default"": 50 }
        },
        ""required"": [""instance_id"", ""component_type""]
    }")]
```

Guidelines:
- Always use `"type": "object"` at the top level.
- Mark truly required fields in the `"required"` array.
- Provide `"description"` for every property — AI agents rely on it.
- Use `"default"` values where sensible.
- Supported types: `string`, `integer`, `number`, `boolean`, `array`, `object`.

---

## Parameter Extraction

Use `ToolHelper` for consistent parameter handling:

```csharp
// Required parameter — returns error JObject if missing
var instanceId = ToolHelper.RequireParam<int>(parameters, "instance_id", out var error);
if (error != null) return error;

// Optional parameter with default
var pageSize = ToolHelper.OptionalParam(parameters, "page_size", 50);
var filter = ToolHelper.OptionalParam<string>(parameters, "filter");
```

---

## Error Handling

### Using `McpError`

Return standardized errors with error codes from `McpErrorCodes`:

```csharp
if (go == null)
    return McpError.Create(McpErrorCodes.NotFound,
        $"GameObject with instance ID {instanceId} not found.",
        "Use get_hierarchy to find valid instance IDs.");
```

### Error Code Taxonomy

| Code | When to Use |
|------|-------------|
| `McpErrorCodes.NotFound` | Requested resource doesn't exist |
| `McpErrorCodes.InvalidParameter` | Parameter value is invalid |
| `McpErrorCodes.MissingParameter` | Required parameter not provided |
| `McpErrorCodes.InvalidState` | Operation not valid in current state |
| `McpErrorCodes.Timeout` | Operation timed out |
| `McpErrorCodes.InternalError` | Unexpected exception |
| `McpErrorCodes.PermissionDenied` | Operation not allowed |

### Exception Handling

The server catches unhandled exceptions and returns internal errors, but prefer explicit error handling:

```csharp
try
{
    // operation that might fail
}
catch (InvalidOperationException ex)
{
    return McpError.Create(McpErrorCodes.InvalidState, ex.Message,
        "Ensure the editor is not in the middle of a compilation.");
}
```

---

## Deferred Responses

For tools that must wait for an async condition (e.g., waiting for Play Mode, a build to complete):

```csharp
[McpTool("wait_for_condition", "Waits until a condition is met",
    DeferredResponse = true,
    InputSchemaJson = @"{ ""type"": ""object"", ""properties"": {
        ""timeout"": { ""type"": ""number"", ""description"": ""Max seconds to wait"", ""default"": 10 }
    }}")]
public static object WaitForCondition(JObject parameters)
{
    var timeout = ToolHelper.OptionalParam(parameters, "timeout", 10f);

    return new DeferredResult
    {
        TimeoutSeconds = timeout,
        IsComplete = () => /* check condition each frame */ SomeCondition(),
        GetResult = () => new JObject { ["status"] = "complete", ["data"] = "..." },
        Cleanup = () => { /* optional resource cleanup */ }
    };
}
```

Key points:
- Method return type changes to `object` (can return `JObject` or `DeferredResult`).
- Set `DeferredResponse = true` on the attribute.
- `IsComplete` is polled each frame on the main thread.
- `TimeoutSeconds` is clamped to [1, 60].
- `Cleanup` is called regardless of completion reason.

---

## AutoRoute (Editor/Runtime Tool Pairs)

AutoRoute exposes a single canonical tool name that routes to the Editor or Runtime implementation based on Play Mode state:

**Editor tool:**
```csharp
[McpTool("get_hierarchy", "Get scene hierarchy", AutoRoute = true)]
public static JObject GetHierarchy(JObject parameters) { ... }
```

**Runtime tool:**
```csharp
[McpTool("runtime_get_hierarchy", "Get runtime scene hierarchy", AutoRoute = true)]
public static JObject RuntimeGetHierarchy(JObject parameters) { ... }
```

The AI agent sees only `get_hierarchy`. In Edit Mode, the Editor implementation runs. In Play Mode, the Runtime implementation runs automatically.

To override the canonical name:
```csharp
[McpTool("my_custom_name", "...", AutoRoute = true, AutoRouteCanonicalName = "canonical_name")]
```

---

## Categories

### Class-Level Category

```csharp
[McpToolCategory("Scene")]
public static class SceneTools { ... }
```

### Standard Categories

| Category | Purpose |
|----------|---------|
| `Scene` | Hierarchy, GameObjects, transforms |
| `Component` | Component read/write |
| `Asset` | Asset search, info, operations |
| `Profiling` | Performance metrics, memory |
| `Editor` | Editor state, compilation, console |
| `Perforce` | Source control operations |
| `Session` | Recording sessions, frame capture |
| `Utility` | Code execution, screenshots, misc |
| `Addressables` | Asset management (optional module) |

---

## Naming Conventions

- Tool names: `snake_case` (e.g., `get_hierarchy`, `set_component_property`)
- Verbs: `get_`, `set_`, `list_`, `find_`, `create_`, `delete_`, `execute_`, `wait_for_`
- Be specific: `get_component_properties` not `get_component`
- Runtime variants: prefix with `runtime_` (stripped by AutoRoute)

---

## Pagination

For tools that return potentially large result sets, use `ToolHelper.Paginate`:

```csharp
var items = GetAllItems();
return ToolHelper.Paginate(items,
    page: ToolHelper.OptionalParam(parameters, "page", 1),
    pageSize: ToolHelper.OptionalParam(parameters, "page_size", 50),
    itemSerializer: item => JObject.FromObject(item));
```

Or for cursor-based pagination (preferred for hierarchies):

```csharp
// Return page_size items starting after cursor, include has_more and next_cursor
```

---

## Testing

Place tests in `Tests/Editor/` (editor tools) or `Tests/Runtime/` (runtime tools):

```csharp
using NUnit.Framework;
using Newtonsoft.Json.Linq;

[TestFixture]
public class MyToolTests
{
    [Test]
    public void MyTool_WithValidParams_ReturnsExpectedResult()
    {
        var parameters = new JObject { ["instance_id"] = 12345 };
        var result = MyTools.MyTool(parameters);

        Assert.IsNull(result["error"]);
        Assert.IsNotNull(result["data"]);
    }

    [Test]
    public void MyTool_WithMissingParam_ReturnsError()
    {
        var result = MyTools.MyTool(new JObject());

        Assert.AreEqual("missing_parameter", result["error_code"]?.Value<string>());
    }
}
```

---

## Template Tool File

```csharp
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace I1rr.UnityMcp.Editor
{
    [McpToolCategory("MyCategory")]
    public static class MyFeatureTools
    {
        [McpTool("my_tool_name",
            "Clear, actionable description of what this tool does and what it returns.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""required_param"": {
                        ""type"": ""string"",
                        ""description"": ""What this parameter controls""
                    },
                    ""optional_param"": {
                        ""type"": ""integer"",
                        ""description"": ""Optional setting"",
                        ""default"": 10
                    }
                },
                ""required"": [""required_param""]
            }")]
        public static JObject MyToolName(JObject parameters)
        {
            // 1. Extract parameters
            var requiredParam = ToolHelper.RequireParam<string>(parameters, "required_param", out var error);
            if (error != null) return error;

            var optionalParam = ToolHelper.OptionalParam(parameters, "optional_param", 10);

            // 2. Validate state
            if (!SomePreCondition())
                return McpError.Create(McpErrorCodes.InvalidState,
                    "Cannot perform operation in current state.",
                    "Ensure the editor is not compiling.");

            // 3. Perform operation
            var data = DoWork(requiredParam, optionalParam);

            // 4. Return structured result
            return new JObject
            {
                ["result"] = JToken.FromObject(data),
                ["metadata"] = new JObject
                {
                    ["count"] = data.Length
                }
            };
        }
    }
}
```
