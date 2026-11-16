# Contributor instructions

This repository contains i1rr-unity-mcp. Keep game-specific tooling in consuming
projects, not in the package. Follow the [community license](LICENSE.md).

- Inspect the branch, working tree and remote before editing. Preserve unrelated
  changes and stage only the current task. Work on main unless asked otherwise.
- Validate focused behavior before committing. Keep commits small and describe
  the user-visible result. Use the maintainer's GitHub noreply email for public
  commits. Do not publish private contact details, tokens or project data.
- Version published releases using SemVer; working commits do not need a bump.
  Update package.json, Shared/McpProtocol.cs and CHANGELOG.md together.
  Follow the [release workflow](.agents/skills/unity-mcp-release/SKILL.md).
  Do not move published tags or overwrite release artifacts.
- Cloud validation requires maintainer authorization. Run appropriate local
  checks and distinguish Editor, runtime and player-build results.
- Discover tools by intent. Unity operations run on the main thread; never block
  it waiting for compilation, frames or asynchronous jobs. Inspect the actual
  result after a timeout before retrying a mutation.
- Keep Editor, Runtime and Shared assembly boundaries and optional dependency
  gates. Dashboard OnGUI renders cached snapshots, without registry discovery
  or filesystem scans. Cache expensive module availability probes.
- Test changes in an isolated Unity consumer. Begin live work with
  get_editor_state and verify the loaded assembly after refresh or reload.
  Never open a second Editor on an already-open project.
- Use the [consumer guide](Documentation~/AIConsumerGuide.md),
  [tool guide](Documentation~/ToolAuthoringGuide.md) and
  [test guide](Documentation~/TestAutomation.md).
  Functional state is not pixel-level proof. Capture real rendered output for
  visual claims, using a disposable demo project for public media.
- Keep credentials, local client configuration, build output and test artifacts
  out of Git. Do not enable owner-disabled tools to bypass a limitation.
