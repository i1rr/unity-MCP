# Releases and versions

Versions follow [Semantic Versioning](https://semver.org/), starting at 1.0.0.
Version each published release, not each working commit.

- Patch: compatible fixes, such as 1.0.0 to 1.0.1.
- Minor: compatible features, such as 1.0.1 to 1.1.0.
- Major: breaking changes, such as 1.1.0 to 2.0.0.

The public API includes tool names, required arguments, schemas and documented
response meanings. Removing a tool, making an optional argument required or
changing documented behavior incompatibly requires a major release. Clients
should tolerate additional response fields and rediscover optional module tools.

Update package.json, Shared/McpProtocol.cs and CHANGELOG.md together. Run local
checks, then publish an immutable vX.Y.Z tag on the tested commit. Never move a
published version or replace its contents; publish a new version for a correction.

Unity can install directly from the Git tag. A tarball is an optional download;
users do not need to compile the included Windows native plugin. See the
[release workflow](https://github.com/i1rr/unity-MCP/blob/main/.agents/skills/unity-mcp-release/SKILL.md).
