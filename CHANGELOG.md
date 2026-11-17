# Changelog

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
