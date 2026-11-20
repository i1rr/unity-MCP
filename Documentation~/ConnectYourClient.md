# Connect your AI client

Install the package using the [README](../README.md), leave Unity open, then open
**Tools > i1rr-unity-mcp Dashboard > Overview**. Copy this project's endpoint. It
looks like `http://127.0.0.1:PORT/mcp`; replace `PORT` in the examples below with
the actual number. Do not copy another project's endpoint or assume a fixed port.

On a first registry install, Unity can show **Importing a scoped registry**.
Close that informational notice before connecting. Native modal dialogs can
suspend the Editor main thread even after the server has started listening.

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

For a private project-local Codex entry, `.codex/config.toml` uses TOML:

```toml
[mcp_servers.i1rr-unity-mcp]
url = "http://127.0.0.1:PORT/mcp"
enabled = true
startup_timeout_sec = 30
tool_timeout_sec = 90
```

Keep local credentials/configuration outside Git. A root `.mcp.json` written for
another client does not register tools in Codex. Refresh the actual client's MCP
connections after configuration changes, then verify directly callable tools in
the chat. Script helpers remain useful for scripts and wire diagnostics.

## Required stateless protocol

Version 1.2.0 accepts only MCP **2026-07-28** per-request HTTP. The
2025 initialization/session protocols are removed. Each request must carry
matching version, capabilities, method and applicable name metadata/headers.
`initialize`, `notifications/initialized`, protocol-level `ping`, HTTP session
deletion and `MCP-Session-Id` are not supported. Probe `server/discover` before
effects. Wire JSON-RPC batches are rejected; the `batch_execute` tool remains.

Codex `0.169.0-alpha.11.1` includes modern support behind a disabled-by-default
feature flag. Merge this into the project's private `.codex/config.toml`:

```toml
[features]
mcp_2026_07_28 = true
```

Reconnect through a fresh Codex connection, then verify Dashboard stateless
activity and `get_editor_state`. Existing connections do not change protocol
when config is edited. This Codex build marks the flag under development.
Check the actual client build and effective features; a product name alone
does not establish modern support. Older requests get supported-version
information and upgrade guidance, without session fallback.

The official TypeScript SDK opt-in is explicit:

```typescript
import { Client, StreamableHTTPClientTransport } from '@modelcontextprotocol/client';
const client = new Client({ name: 'my-client', version: '1.0' }, {
  versionNegotiation: { mode: { pin: '2026-07-28' } }
});
await client.connect(new StreamableHTTPClientTransport(new URL(endpoint)));
```

Provide bearer authentication through private transport request headers.
See the [SDK version-negotiation guide](https://github.com/modelcontextprotocol/typescript-sdk/blob/main/docs/migration/support-2026-07-28.md).
Helpers must not load, mint or retain protocol session IDs. Authentication,
metadata errors and interrupted mutations never justify downgrade or replay.
Keep explicit application handles for tests, imports, UI actions and recordings.

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
helpers, scopes and server status. Registration alone does not enable a protocol version. Modern Claude Code
compatibility with this stateless-only release has not been verified;
confirm the actual client protocol before adopting it.

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
See [Cursor MCP documentation](https://cursor.com/docs/mcp). Registration alone does not establish modern-protocol compatibility; verify
the actual Cursor build against the required stateless protocol.

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
