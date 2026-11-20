---
name: unity-mcp-release
description: Prepare, validate, tag and distribute i1rr-unity-mcp releases through its Unity registry and GitHub. Use for version bumps, package tarballs and release publication.
---

# Release i1rr-unity-mcp

## Prepare the version

Work from the repository root. The UPM package lives at the repository root. Inspect remote tags
with `git ls-remote --tags origin` before choosing a version. Version published releases, not working commits. Compatible fixes advance
the patch; compatible features advance the minor; breaking public API changes
advance the major. Use SemVer prereleases such as
`1.1.0-rc.1`; reserve one version and tag for one set of bytes.

Update `package.json`, `Shared/McpProtocol.cs` and the matching changelog
heading together. Describe changes to the client contract and any migration.
Commit the candidate and build it locally:

```sh
python Tools~/pack_release.py --revision HEAD --output dist~
```

The packer requires Python 3.9+ and Git with `archive --mtime` support. It
reads the committed package, checks its declared license, native binary, metadata,
version and changelog, and prints the exact commit and tarball SHA-256.
Use a fresh output directory if rebuilding a changed candidate.

## Exercise the deliverable

First install the generated archive in a new project whose only manifest dependency is the MCP. Confirm a clean import, a focused default catalog, full discovery on request and live stateless discovery. Rebuild the Windows socket keeper with its pinned recipe and verify its recorded hashes when the source or binary changes.

Install the generated `.tgz` in a clean Unity test project using **Install package
from tarball**. Check the resolved package ID and version, run its Editor
and Runtime suites, and check live `server/discover` and a stateless tool call, plus old-version/session rejection. Follow
[package tests](../../../Documentation~/TestAutomation.md)
for assembly selection and native checks. Record the actual Unity versions,
test results, commit and artifact hash. Only claim editor versions tested.

## Publish on a release request

Push the verified commit. Set `TAG` to `v<manifest-version>`, `SHA` to
that commit and `TGZ` to the tested file. Create an annotated tag on that
SHA and push the specific tag. If it already exists, verify its peeled
commit before continuing; a published tag or asset must never be replaced.

```sh
git tag -a "$TAG" "$SHA" -m "$TAG"
git push origin "refs/tags/$TAG"
git ls-remote origin "refs/tags/$TAG" "refs/tags/$TAG^{}"
gh release create "$TAG" "$TGZ" --verify-tag --title "$TAG" --notes-file release-notes.md
```

Prepare the notes outside the tracked package, with the changelog entry,
SHA-256 and the exact Git installation URL
`https://github.com/i1rr/unity-MCP.git#<tag>`.
Pass `--prerelease --latest=false` for prereleases. After a timeout or an
existing release, inspect `gh release view` and its assets first. Download
the published file to a fresh directory, verify the hash and install using
the documented tag. Roll back a consumer by restoring its previous pin. Issue
a new version for corrected releases.

Publish the tested tag through the static Unity registry. Follow
[registry publishing](../../../Documentation~/RegistryPublishing.md). Create the
Unity-signed archive with `Tools~/sign_release.py` and the owner's Unity organization; never
weaken a consumer's security policy. Verify Unity's signature status, the signed
payload against Git, public registry metadata, archive hash and a fresh
Unity registry install. For update verification, start on the previous registry
version and inspect the real Package Manager update control before upgrading.

Registry installation is the primary route for version discovery and updates.
Keep Git installation from immutable tags as an alternative. Creating a tarball
is packaging and verification, not a requirement for Git installation. The native
Windows DLL is already included; rebuild it only when its source or binary changes.

The supported distribution channels are the GitHub Pages Unity registry, a GitHub Git URL
and a GitHub Release tarball, all consumed by Unity Package Manager. Public npm
publishing is retired because Unity dependency IDs collide with unrelated npm packages.
Other registries require an explicit destination and package access. Keep any registry credentials
outside Git. Preparing a candidate alone does not publish a tag or change visibility.

[Unity Git dependencies](https://docs.unity3d.com/2022.3/Documentation/Manual/upm-git.html) · [Semantic Versioning](https://semver.org/) · [GitHub release CLI](https://cli.github.com/manual/gh_release_create)
