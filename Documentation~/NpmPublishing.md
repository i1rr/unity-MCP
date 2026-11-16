# Publish to npm

The package uses the public npm registry at `https://registry.npmjs.org/`.
Its ID stays `i1rr-unity-mcp`, so registry and Git installs use the same assets
and project instruction paths. Do not install two copies in one project.

## Account setup

Create an [npm account](https://www.npmjs.com/signup), verify your email and enable
[two-factor authentication](https://docs.npmjs.com/configuring-two-factor-authentication/).
Install [Node.js LTS](https://nodejs.org/en/download), which includes npm.
Sign in from your own terminal:

```sh
npm login --registry https://registry.npmjs.org/ --auth-type=web
npm whoami --registry https://registry.npmjs.org/
```

Complete login and any publishing confirmation in npm's browser flow. Keep
passwords, tokens and 2FA codes out of Git, chat and project configuration.
Only maintainers need an npm account; public package downloads need no login.

## Sign and publish a tested release

Follow the [release workflow](../.agents/skills/unity-mcp-release/SKILL.md).
Update the manifest, protocol version and changelog together. Test the candidate,
commit it, create its `vX.Y.Z` tag and check the package without uploading:

```sh
python Tools~/publish_npm.py --revision v1.0.1
```

The command builds the committed archive and asks npm for a dry run.
Create a Unity-signed archive using Unity 6.3 or newer, while signed in through
Unity Hub as a member of the publishing organization:

```sh
python Tools~/sign_release.py --revision v1.0.1 --unity <Unity-executable> --project <new-disposable-project> --organization <Unity-organization-ID> --output <new-output-directory>
```

The signer exports committed files into a temporary folder outside Git and calls
Unity's `Client.Pack` with the organization ID. It requires no password or token
in its arguments. It opens a disposable graphical Editor, waits for the Unity
Hub session and confirms organization membership. This flow uses that session
instead of passing login credentials through batch mode. It checks the signed
payload against Git before returning the archive. Package consumers can still
use the older Editors supported by the MCP.

Check and publish the signed archive:

```sh
python Tools~/publish_npm.py --revision v1.0.1 --archive <signed-archive.tgz>
python Tools~/publish_npm.py --revision v1.0.1 --archive <signed-archive.tgz> --publish
```

The publisher checks the exact release tag, refuses an existing npm version,
requires Unity's signature, checks the signed payload against Git, disables
lifecycle scripts, and verifies the uploaded archive's SHA-512. It uploads the
signed bytes without repacking them. Use that same signed archive for GitHub's
release download. The `.attestation.p7m` signature stays in the archive, not Git.
An accepted upload may take time to become public. The command waits for the real
version and checksum; a temporary `0.0.0-stage` placeholder is not the release.
If it times out, inspect npm's metadata and staged packages before retrying.
Stable versions use npm's `latest` tag; prereleases use `next`.
For a portable npm installation, add `--npm-cli <path-to-npm-cli.js>`.

Publish the same release on GitHub and retain the Git install URL. Registry
versions cannot be replaced: publish a new version for corrections. Working
commits do not publish automatically. Automated publishing can be added later
using [npm trusted publishing](https://docs.npmjs.com/trusted-publishers/).

## Verify installation and updates

Use a disposable Unity project, not a game project. Add the registry and exact
`i1rr-unity-mcp` scope from the [installation instructions](../README.md).

1. Install the older version from the registry and verify its lockfile source,
   package ID and MCP server version.
2. Publish the newer tested version. Refresh Package Manager and select the MCP.
3. Confirm the newer version and update control appear in the actual window.
4. Update, wait for compilation and confirm the lockfile, package and MCP server
   all report the new version. Reopen the project and check it remains installed.

A local npm-compatible registry may be used to test the sequence before a public
release. Record its URL and Unity version with the results; a local pass is not
evidence of a successful public npm publication. Keep test-only versions local.

Check the signature in Package Manager too. Signing resolves the missing-signature
status; users outside the signing organization may still see an external-publisher
notice. Do not lower package security settings to make a release pass. See
[Unity package signatures](https://docs.unity3d.com/6000.6/Documentation/Manual/upm-signature.html).
