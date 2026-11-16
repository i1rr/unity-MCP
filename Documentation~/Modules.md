# Project modules

Modules group tools for a game or framework. In **Dashboard > Overview**, the
Modules panel shows installed, disabled and unavailable integrations, their
descriptions and tool counts. The checkbox controls whether a module's tools
can be discovered and called. Addressables requires its Unity package and project settings; a checkbox does not install or uninstall them.

## Create tools for your project

Choose **New module**, enter an ID such as `GameTools` and a short description.
The equivalent MCP call is:

```json
{"action":"create","id":"GameTools","description":"Tools for your game"}
```

Call `manage_module` with those arguments. The starter is created under
`Assets/McpModules/GameTools/`, with an Editor assembly, a module manifest and a
read-only `gametools_module_info` example. Add your own tools to the generated C#
source using the [tool authoring guide](ToolAuthoringGuide.md). Add named assembly
references for the game or framework APIs you use. Keep these project-owned
files in your project's version control; the installed MCP package stays unchanged.

Creation returns before compilation finishes. After Unity settles, check
`get_editor_state`, `get_module_status`, rediscover tools and call the example.
An unavailable or awaiting-compilation row does not prove a working integration.
Clients that retain a fixed catalog may need to reconnect after tools change.

Use one `IMcpModule` implementation per assembly. Its `ModuleName` must match the
manifest ID; all tools in that assembly belong to it. Keep tool names unique and
prefixed with the lowercase module ID. Use lightweight `IsAvailable` dependency
checks and cache expensive probes. Availability is sampled at discovery, after
project changes settle, or with `manage_module {"action":"refresh"}`.

Enable/disable preferences live in
`ProjectSettings/i1rr-unity-mcp-modules.json`. They apply to the Editor server;
project module assemblies are Editor-only. Disabling tools does not prevent C#
compilation, static initializers or module lifecycle code from running.

## Reuse a module

The panel's bottom controls provide **Import…** and **Export…**. Select a project
module before exporting. ZIP archives contain its source, documentation, assembly
definition and Unity metadata. Install framework dependencies separately. Built-in
integrations ship with the package and are not exported as project modules.

Through MCP:

1. Call `manage_module` with `{"action":"export","id":"GameTools"}`. The response
   gives a private export path under `UserSettings/i1rr-unity-mcp/exports/`.
2. Copy the ZIP into the destination project. Call
   `{"action":"inspect_import","path":"UserSettings/GameTools.zip"}` to check its
   identity, source preview and missing dependencies. Review the complete source
   when the preview is truncated. Import only source you trust: it is Editor C#.
3. Call `{"action":"import","path":"UserSettings/GameTools.zip","sha256":"<returned hash>"}`.
   Existing modules and conflicting asset GUIDs are refused. Missing assembly
   dependencies must be installed first. Imported tools start disabled.
4. Wait for compilation, inspect module status, then explicitly enable with
   `{"action":"set_enabled","id":"GameTools","enabled":true}` and verify a tool call.

MCP archive paths stay within the current project; native Dashboard file pickers
can select external files. Exports require a new filename. Archives accept up to
128 entries, 256 KiB per file and 4 MiB of text source, in an 8 MiB ZIP. Binaries,
linked paths and source outside the module's Editor folder are not imported.
