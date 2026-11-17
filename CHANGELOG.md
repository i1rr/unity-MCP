# Changelog

## [1.1.2]

- Create Windows listening sockets without handle inheritance and clear inheritance on accepted sockets. Import/compiler children can no longer keep stopped MCP listeners bound and unresponsive.
- Add a real child-process regression that fails with the previous native DLL; rebuild the keeper with the pinned recipe and record reproducible provenance.
- Retain 1.1.1's physical-provider handover. Already inherited sockets from older releases require one-time cleanup of the owning child processes; upgrading cannot retroactively revoke inherited handles.

## [1.1.1]

- Bind the Windows socket keeper to the installed package's physical DLL, rather than an ambiguous DLL basename after UPM changes its cache path.
- Retire detached keepers from previous package paths before binding the replacement, preferring their recorded endpoint when available. Preserve same-path managed reloads, legacy sessions and staged request outcomes.
- Verify physical-DLL handover, stale-provider cleanup and interrupted requests; resident native modules are not forcibly unloaded.

## [1.1.0]

- Capture immutable per-request metadata for ordinary and deferred dispatch, retaining legacy callbacks and session APIs.
- Share request admission checks and preserve negotiated client capabilities through legacy reload recovery.
- Add MCP 2026-07-28 HTTP dispatch without legacy sessions in Editor and runtime, with metadata/header admission, discovery, deterministic catalogs and modern envelopes.
- Isolate bounded subscription/deferred SSE writers, cancel work on disconnect or timeout, finish interrupted streams on shutdown/reload where possible and run deferred cleanup once. No optional change feed is advertised.
- Reject refresh request-ID reuse with different import options; keep older records without captured options readable through status lookup. Document per-operation retry/retention limits and verify consumed UI handles across stateless requests.
- Separate bounded modern activity/cancellation from legacy Dashboard sessions, retain the legacy UDP discovery field and add supported versions, and document observed native/modern SDK client behavior.

## [1.0.3]

- Preserve exact floating UI values and bounds through inspection JSON.
- Read action values as numeric tokens so inspected inclusive endpoints remain valid.
- Keep strict finite/range checks and single-use action handles; no client migration is required.

## [1.0.2]

- Move registry installation to a dedicated Unity registry on GitHub Pages.
- Keep signed release archives, Unity dependencies and pinned Git installation.
- Explain npm scanner dependency-name collisions and the one-time registry migration.
- Correct code and batch timeout descriptions: running tools cannot be interrupted.
- Report completed code calls that exceeded their time budget without implying cancellation.

## [1.0.1]

- Add npm publishing and registry installation with Package Manager updates.
- Keep Git installation and the existing package ID.
- Sign registry archives with the publisher's Unity organization.

## [1.0.0]

First public release.

- Inspect and edit Unity scenes, GameObjects, components and assets through MCP.
- Discover specialist tools and run named Unity tests.
- Inspect development players and capture Game View images and video.
- Manage connections, tools and optional integrations in the Dashboard.
- Create, enable, import and export project-specific tool modules.
- Keep tools from failed module initializations unavailable instead of treating
  them as core tools.
- Enable local AI issue logging with instructions separate from issue data.
- Use the i1rr Community License: commercial development is allowed; selling or
  offering the MCP's capabilities for a fee requires written permission.
