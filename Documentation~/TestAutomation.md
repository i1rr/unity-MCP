# Package tests

Use a separate Unity project with the package installed. Include
`com.unity.test-framework` in its dependencies and `i1rr-unity-mcp` in
its manifest's `testables` array. Close that project before a command-line run.

From the package's `Tests` directory run in PowerShell 7 or later:

```powershell
./run-tests.ps1 -UnityPath <Unity-executable> -ProjectPath <test-project> -Platform Both -OutputDir <results-directory>
```

Check the fresh NUnit XML for both `I1rr.UnityMcp.Editor.Tests` and
`I1rr.UnityMcp.Runtime.Tests`. `Both` runs EditMode in a batch Editor, then
opens a graphical Editor for PlayMode and waits for its exit. Use a fresh output
directory for each invocation. No manual MCP call is needed to start the run.

The runtime suite includes framebuffer and UI Toolkit composition assertions
that yield `WaitForEndOfFrame`. Unity Test Framework rejects that yield in batch
mode, even when a graphics device is available. PlayMode therefore requires an
interactive desktop and a visible Game View; do not minimize the test Editor or
switch it to Scene View. The runner does not use `-batchmode` or `-nographics`
for PlayMode, and retains the ordinary frame/pixel assertions. Batch-only agents
can select `-Platform EditMode`; that result does not establish runtime coverage.
`-Filter` selects a named fixture or test using Unity's CLI filter syntax.
On Windows, `-GraphicsApi D3D11` selects Direct3D 11 for both test platforms.
Use it when investigating a Direct3D 12 device failure and record the backend
with the results; a native GPU crash is not a managed test assertion failure.

For broader composited Game View checks, use a graphical Editor with settled UI.
Verify server identity and tool discovery in a development player
when changing runtime bootstrap or transport.

When changing Game View controls, verify rendered landscape/portrait dimensions,
domain-reload survival, repeated replacement and explicit restoration. Also select
a non-final built-in preset (for example Full HD), configure a temporary size, quit
the Editor normally without explicit restoration, reopen it and compare both the
original selection and custom-preset count. In-session cleanup alone misses layout
serialization: `EditorApplication.quitting` is too late to restore the saved index;
cleanup must run during `wantsToQuit`. Forced process termination is outside that
ordinary-quit contract. Keep this check in an isolated consumer.

For capture/conversion changes, exercise both Gamma and Linear project color
spaces. Include midtone framebuffer-to-resized-image parity: black and saturated
primaries can pass despite an extra gamma conversion. Compare a real consumer's
saved image with its composited Game View before accepting integration.

For MP4 detail changes, retain identical unencoded frames by Unity frame number
and compare every decoded frame, including first/middle/last and native-size HUD
strips across the full duration. Encoder presets alone do not establish recovery:
the legacy Low and High modes both lost fine detail in a detailed 720p/60 match.
Use the explicit H.264 target, record requested settings and actual bytes, and
verify a real marker-armed consumer clip after installing the committed archive.
Keep PNG fallback proof separate until the reported video failure is recovered.

On Windows, the native socket tests take the shipped DLL as their argument:

```powershell
python native-socket-tests.py ../Editor/Plugins/Windows/x86_64/mcpkeeper.dll
```

## Release checks

From the repository root, use `python Tools~/test_install.py --unity <editor>
--project <new-directory> --package <archive-or-git-url> --version <version>`
to create an empty project whose only dependency is the package. For Git
installs, pass `--commit <sha>` to check the lockfile resolution. Also run the test-enabled consumer separately. Check focused and
full catalog sizes and a real protocol handshake. For build changes, verify a
development player with its packaged token, a normal player without the token,
and a build that refuses a project-owned token collision without modifying it.

The Windows native socket keeper is built from `Editor/Plugins/native/mcpkeeper.c`.
Run `python Editor/Plugins/native/build.py --output <temporary-directory>`
with the toolset and Windows SDK named in `Editor/Plugins/native/provenance.json`.
Compare the rebuilt DLL and source/recipe hashes with the committed provenance
and run `Tests/native-socket-tests.py` on the rebuilt file. The release packer
rejects mismatched provenance.

The package manifest specifies the minimum Unity version. Record which Editor
versions, transports, scripting backends, and optional integrations were actually
exercised for each release. A Windows socket test does not validate the fallback
transport on macOS or Linux.

## Compiler/API compatibility

Unity 2022.3 remains the declared minimum. Shared compatibility helpers select
Unity-managed assembly enumeration/paths and unsorted queries on 6000.6+, with
compile-time older-API branches below that version. NamedBuildTarget player and
icon APIs are available at the minimum. Record the Editor versions actually
executed; guarded source does not by itself establish a 2022.3 test pass.

CompilerCompatibilityTests checks active assemblies and paths, inactive selection,
player/icon reporting, both persisted physics sync values and analyzer diagnostic
visibility. For clean-compiler proof, force a fresh complete script compilation
and retain its complete compiler output. get_compilation_errors reports only a
bounded snapshot (including its availability/source/truncation); a zero after a
reload or cleared console does not establish a warning-free compile.
