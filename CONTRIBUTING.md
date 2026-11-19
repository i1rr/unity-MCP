# Contributing

Small, well-explained contributions are welcome. You can improve setup instructions,
report a reproducible bug, verify a client/Unity/OS combination or fix an existing
issue. Start with [good first issues](https://github.com/i1rr/unity-MCP/labels/good%20first%20issue)
and [help wanted](https://github.com/i1rr/unity-MCP/labels/help%20wanted).

## Before you start

Search open and closed issues. For a larger feature, describe the workflow and
expected result in an issue before investing in an implementation. Keep fixes
focused on the package; game-specific tools belong in a consuming project's
module, as described in the [module guide](Documentation~/Modules.md).

The project uses the [i1rr Community License](LICENSE.md), including for modified
versions. It allows commercial game development and free forks, but restricts
selling or hosting the MCP itself as a paid offering. Read the terms before
contributing. No license change or extra contributor agreement is requested by
this guide.

## Make a pull request

1. Fork this repository and create a topic branch from current `main` in your fork.
2. Make one logical change. Preserve Unity `.meta` files and asset GUIDs; include
   metadata for new files that Unity imports. Files under folders ending in `~`
   and `.github` are not imported as Unity assets.
3. Explain the observed behavior, desired result and how to verify the change.
4. Run the smallest relevant checks below, then open a PR against `main`.

You do not need to bump the package version, publish a release or obtain a Unity
signing credential. Maintainers handle releases. The direct-to-main instructions
in `AGENTS.md` describe the maintainer's automation, not access expected of
external contributors.

## Develop in an isolated Unity project

Clone your fork outside a Unity project's `Assets` folder. In a separate empty
Unity project, install the package from your local checkout with Unity Package
Manager's **Install package from disk** and select its `package.json`.
This is a local development dependency; do not replace the repository's package
identity or commit your machine's absolute paths.

The manifest declares Unity 2022.3.19f1 as its minimum. Record the exact Editor
version you actually use. Recent release validation used Windows and Unity
6000.6.9f1; other versions/platforms need their own evidence.

- For documentation changes, run `python Tools~/check_docs.py` from the repository
  root with Python 3.9 or later. It checks local links and images and reports
  missing targets by file and line. Check commands and screenshots against the
  current UI and source. No Unity test run is needed for prose alone.
- Behavior changes: reproduce the old failure and add a focused regression case
  where practical. Exercise affected callers, not just a mock of the new code.
- Package tests: follow [TestAutomation.md](Documentation~/TestAutomation.md),
  including its `testables` setup and exact fixture filtering. PlayMode capture
  tests require a graphical Editor; batch-only results do not establish them.
- Visual changes: inspect the real composited Game View in your disposable
  project and include an image or clip.
- New tools: follow [ToolAuthoringGuide.md](Documentation~/ToolAuthoringGuide.md).
  Keep the Editor main thread free while waiting for work, compilation or frames.

Cloud CI is currently deferred. State which checks you ran, their results, and
anything you could not test; a missing cloud result is not a local test pass.
For maintainer-created commits, keep `[skip ci]` until cloud validation resumes.

## Useful reports

Include package and Unity versions, OS, AI client/version, transport, precise
steps, expected/actual behavior and a small disposable reproduction. For a
compatibility report, say whether installation, `get_editor_state`, a scene edit
and a screenshot actually worked. A configuration file alone proves no handshake.

Remove tokens, client credentials and private project data from attachments.
Report vulnerabilities through [SECURITY.md](SECURITY.md), not a public bug issue.
Use clear, respectful discussion; maintainers may ask to narrow a PR or provide
the missing recovery check. Review timing depends on maintainer availability.
