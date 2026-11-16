# Manual integration checks

Use a disposable Unity project. Select the checks relevant to the change; this
list is a procedure, not a record of completed validation. See
[package tests](../Documentation~/TestAutomation.md) for automated checks.

- Connect a client and verify the project, package version and loaded assembly.
- Discover tools, invoke a normal tool and verify the resulting scene or asset.
- Check available, disabled and missing modules. Disabled tools must be hidden
  from discovery and reject direct calls.
- Create and compile a project module. Export its source, import into a separate
  project, verify it starts disabled, then enable it and run a tool.
- Enable local issue logging. Confirm project instructions link to the packaged
  rules and that issue data stays in the project. Check the Activity preview.
- For profiler changes, verify live counters and memory comparisons in Play Mode.
- For capture changes, inspect actual composited Game View output and frame timing.
- For transport changes, verify reconnects after reload and the authentication
  settings in both Editor and a development player.
- Verify normal player builds exclude the MCP runtime service and its token.

Record the Unity version, test scope and evidence with the release. Do not
infer runtime, visual or player-build success from compilation alone.
