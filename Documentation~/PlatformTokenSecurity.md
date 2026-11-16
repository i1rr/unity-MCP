# Token storage

## Editor

The Editor binds to loopback. When authentication is enabled, VS Code prompts
for the dashboard token through a password input. The generated client config
stores an input reference. Other clients can read `Library/MCPServer.token`
locally and send it in a Bearer header. Keep this file out of version control.
If an older client configuration containing a live bearer was committed,
stop the Editor and rotate its token before reconnecting clients.

## Development players

The build processor stages a fresh token under the project's `Temp` directory
and registers it with the current development build as
`StreamingAssets/mcp_runtime.token`. It never writes the token into the
project's Assets folder. Builds reject a pre-existing file at that reserved
project path so that it cannot enter an ordinary player. Move the file before
retrying. A cancelled build can leave a staged file under Temp. The next build
clears that file before reuse.

A development player first attempts to read its packaged token through ordinary
file I/O. Anyone with the development build can obtain that credential. When
the token cannot be read this way, the runtime loads or creates
`MCPServer.token` in `Application.persistentDataPath`. On platforms where
StreamingAssets is a URL or an archive entry, use the per-install token. The
runtime listener requires authentication and defaults to loopback; enable LAN
access only for a controlled test environment.

Desktop token files are restricted with checked OS permission commands.
Mobile and console file isolation depends on the platform, build configuration
and storage location. WebGL is unsupported. Validate the transport and token
access on the intended target before distributing a development player.
