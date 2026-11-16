# Unity registry publishing

Registry URL: `https://i1rr.github.io/unity-MCP`

Scope: `i1rr-unity-mcp`

The registry is static metadata on the repository's `gh-pages` branch, hosted by
GitHub Pages. It serves only this package. Downloads point to immutable, signed
GitHub Release archives. There is no npm proxy, server process, account or token
for consumers. Keep Git installation available too.

## Publish

1. Follow the [release workflow](../.agents/skills/unity-mcp-release/SKILL.md):
   validate and commit the candidate, create its version tag, then build and sign
   the archive. Use Unity 6.3 or newer, signed into Unity Hub as a member of the
   publishing organization:

   ```sh
   python Tools~/sign_release.py --revision vX.Y.Z --unity <Unity-executable> --project <new-disposable-project> --organization <Unity-organization-ID> --output <new-output-directory>
   ```

2. Upload that exact archive to the corresponding GitHub Release. Download it
   again and compare its hash. Never replace an existing tag or archive. Verify
   Unity's signature status too; checking for an attestation file alone does not
   establish a valid signature.
3. Check out the existing `gh-pages` branch in a separate worktree. Download and
   retain every archive already listed there, then generate metadata with one
   `--release` argument per version, including the new one:

   ```sh
   python Tools~/build_registry.py --release v1.0.1=<signed-1.0.1.tgz> --release v1.0.2=<signed-1.0.2.tgz> --output <registry-worktree>
   ```

   The generator compares every archive with its committed tag, checks the
   package identity and signature presence, and records SHA-1 and SHA-512. It
   preserves real Unity dependencies. `latest` selects the highest stable version;
   prereleases use `next` without replacing `latest`.
4. Review and commit the generated registry files, then push `gh-pages`. GitHub
   Pages must use that branch's root with HTTPS. This deployment publishes only
   static metadata and a small landing page; game content and credentials never
   belong there. Future source pushes do not publish a release automatically.
5. Fetch the public package document at `/i1rr-unity-mcp`, check `dist-tags.latest`
   and download the advertised archive. Verify both hashes. Test installation in
   a new Unity project and inspect its lockfile sources: modules must be `builtin`
   and Newtonsoft must use `https://packages.unity.com`.
6. In a disposable project on the previous version, refresh Package Manager,
   observe its real update control, update and confirm the installed package,
   live MCP version and signature. Record the Unity version and evidence paths.

The static endpoints `/i1rr-unity-mcp`, `/-/all` and `/-/v1/search` provide package
metadata and discovery. This is a read-only Unity registry, not a general npm
service; it does not implement authentication, uploads or arbitrary searches.

## Migrate from npm

Change the existing scoped registry URL from `https://registry.npmjs.org` to the
URL above. Keep its scope exactly `i1rr-unity-mcp`, then refresh Package Manager.
Do not add `com.unity` to that scope. Git users can keep their selected tag, or
remove the Git dependency and install by name from the registry.

Published npm versions and their historical scanner reports remain unchanged.
Mark the old npm listing deprecated with a migration link after public registry
verification; npm may require the maintainer's own browser/2FA confirmation.
