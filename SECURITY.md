# Security

This is a Unity package. Install it through Unity Package Manager using the
[installation instructions](README.md#install), or use the pinned Git URL.
Do not install it with `npm install` or `npx`.

## Dependency sources

Use the exact registry scope `i1rr-unity-mcp`. Do not add `com.unity` or a wildcard
to a third-party registry. Unity's built-in modules come from the Editor;
`com.unity.nuget.newtonsoft-json` comes from `https://packages.unity.com`.
Check `Packages/packages-lock.json` to confirm those sources in your project.

Versions 1.0.0 and 1.0.1 were also published on public npm. npm security scanners
interpret their Unity dependency IDs as npm packages. Those npm names are
unrelated packages with malware advisories; they are not Unity's packages.
For example, [the animation advisory](https://github.com/advisories/GHSA-qqqx-23r5-prvc)
and [the Newtonsoft advisory](https://github.com/advisories/GHSA-g4hh-rpf3-w9hp)
identify the **npm** ecosystem. The reported 16 entries cover 13 distinct names,
with three repeated advisories.

Our clean Unity 6000.6.9f1 consumer resolved all 12 declared modules as `builtin`
and Newtonsoft from `https://packages.unity.com`. This verifies that installation,
not every possible consumer configuration. Existing npm reports remain attached
to the published versions. The Unity registry preserves the actual dependencies
and serves signed GitHub release archives without proxying public npm.

## Client permissions

Connect only trusted clients. The Editor server listens on loopback and checks
Host and Origin headers. Token authentication is available in Dashboard Settings;
enable it when other processes or users on the machine must not connect freely.
The normal tools can change project files and scenes. They are not a sandbox.

`execute_code` is disabled by default and requires a separate opt-in per project.
If enabled, it runs arbitrary C# with the Editor process's file, network and
assembly access. It runs on the main thread and cannot be interrupted. Its
`timeout_ms` parameter only reports a budget overrun after the call completes;
it does not terminate code or undo changes. Batch budgets are checked between
steps and cannot interrupt an active tool either. After a client timeout,
inspect the result before retrying a mutation. Prefer reviewed project modules
for repeatable tasks.

Socket's AI warning about arbitrary execution describes this capability risk.
It is not evidence of malware, and signing does not remove that risk. Keep code
execution off unless you need it and trust every connected client.

## Report a security issue

Use [GitHub private vulnerability reporting](https://github.com/i1rr/unity-MCP/security/advisories/new)
if available, or contact the maintainer through their GitHub profile. Do not
include tokens, private project files or credentials in a public report.
