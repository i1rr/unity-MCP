# Connect your AI client

Install the package using the [README](../README.md), leave Unity open, then open
**Tools > i1rr-unity-mcp Dashboard > Overview**. Copy this project's endpoint. It
looks like `http://127.0.0.1:PORT/mcp`; replace `PORT` in the examples below with
the actual number. Do not copy another project's endpoint or assume a fixed port.

The Editor listens on the same computer only. Use a local client that supports
HTTP MCP. A cloud-only connector cannot normally reach your computer's loopback
address. See [authentication and runtime differences](AIConsumerGuide.md#local-connection-and-authentication)
before using a development-player endpoint.

## VS Code

The package writes this project's entry to `.vscode/mcp.json` and preserves
other servers. Open the Unity project folder in VS Code, review the generated
configuration and start/enable the MCP server through VS Code's MCP controls.
When authentication is required, its generated password prompt asks for the
token shown in the Unity Dashboard. Do not commit the machine-specific file.

## Codex

From a terminal on the Unity computer:

```text
codex mcp add i1rr-unity-mcp --url http://127.0.0.1:PORT/mcp
codex mcp get i1rr-unity-mcp
```

If **Require authentication** is enabled, put the Dashboard token in an environment
variable named `UNITY_MCP_TOKEN` in the environment that launches Codex, then add
`--bearer-token-env-var UNITY_MCP_TOKEN` to the add command. The variable's value
stays out of the command and repository. This is bearer-token authentication,
not an OAuth login. If replacing an existing entry, inspect it first; use
`codex mcp remove i1rr-unity-mcp` only for the entry you intend to replace.

Use `codex mcp add --help` to check the flags supported by your CLI. A saved entry
does not establish a working connection; perform the first check below.

## Claude Code

Use a local-scoped HTTP server from your project directory:

```text
claude mcp add --transport http i1rr-unity-mcp http://127.0.0.1:PORT/mcp
claude mcp get i1rr-unity-mcp
```

For authentication, configure an `Authorization: Bearer <token>` header through
Claude Code's private MCP configuration. Do not paste a real token into a shared
issue, committed configuration or example. The default local scope avoids
publishing this machine's endpoint in the project. Consult the current
[Claude Code MCP documentation](https://code.claude.com/docs/en/mcp) for header
helpers, scopes and server status. These setup instructions do not claim a
new end-to-end Claude Code compatibility test.

## Cursor

Add an HTTP server in Cursor's MCP settings, or merge this entry into your
private project `.cursor/mcp.json` without replacing other servers:

```json
{
  "mcpServers": {
    "i1rr-unity-mcp": {
      "url": "http://127.0.0.1:PORT/mcp"
    }
  }
}
```

If authentication is enabled, add the bearer header using Cursor's supported
private configuration. Keep tokens and local endpoints out of source control.
See [Cursor MCP documentation](https://cursor.com/docs/mcp). This example is
documentation-based; it is not a new end-to-end Cursor validation result.

## Confirm the first result

Ask the agent to call `get_editor_state`. Check that its project path and Unity
version match the Editor you intended. Then try the [first workflow](../README.md#try-your-first-workflow).
Keep a note of the package, Unity, client and OS versions and which operations
actually succeeded when sharing a [compatibility report](https://github.com/i1rr/unity-MCP/issues/new?template=workflow.yml).

## Troubleshooting

| Symptom | Next check |
| --- | --- |
| Connection refused | Confirm Unity is open, the Dashboard server is running and the current endpoint matches the client's entry. |
| Unauthorized | Match the Dashboard's authentication setting and current token. VS Code password-input syntax is client-specific. |
| Connected, tool missing | Use `discover_tools`; for clients with fixed catalogs, select the full catalog and reconnect. Module availability and permissions still apply. |
| Wrong project | Compare `get_editor_state` with the intended Editor. Give different projects distinct client entry names. |
| Busy/reloading or a timeout | Let compilation settle and close native menus/dialogs. Read current state before repeating a mutation: a timeout does not prove it never ran. |
| Screenshot misses the object | Verify Play Mode, the active camera and composited Game View. Functional state is not visual proof. |

Keep arbitrary C# execution disabled unless you intentionally need it. The first
workflow uses ordinary scene tools. For detailed discovery, test and readiness
contracts, see the [consumer guide](AIConsumerGuide.md).
