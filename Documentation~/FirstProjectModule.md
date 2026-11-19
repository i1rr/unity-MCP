# Build your first project module

Create an Editor tool that reports how many 3D `Collider` components are in the
active scene. Use a disposable Unity project with i1rr-unity-mcp installed and
an [MCP client connected](ConnectYourClient.md).

## Generate the module

In Edit Mode, call `get_editor_state` to confirm the client is connected to your
disposable project. Call `manage_module` with these arguments.

```json
{"action":"create","id":"SceneAudit","description":"Read-only checks for the active scene"}
```

Unity creates `Assets/McpModules/SceneAudit/` with a manifest, a starter tool
and an assembly definition. After compilation and reload, call
`get_module_status` and look for `SceneAudit` with status `enabled` and assembly
`Project.Mcp.SceneAudit.Editor`.

## Add the collider tool

Copy [SceneColliderTools.cs](../Examples~/FirstProjectModule/SceneColliderTools.cs)
into `Assets/McpModules/SceneAudit/Editor/` alongside the generated
`SceneAuditModule.cs`. Keep the generated `IMcpModule` implementation and manifest.
The new class uses the `sceneaudit_` tool prefix derived from the module ID.

The generated Editor assembly already references `I1rr.UnityMcp.Shared` for
`McpTool` and `McpError`, and `Newtonsoft.Json.dll` for the JSON result. Unity
provides the scene and collider APIs, so this example needs no extra references.
For future tools that use your game's APIs, add the named assembly references
to `SceneAudit.Editor.asmdef`.

Return focus to Unity or call `refresh_assets`. Wait for compilation and reload
to finish, then check `get_editor_state` for a settled Editor without compilation
errors. Call `discover_tools` with `{"name":"sceneaudit_count_colliders"}`, or
ask your client to refresh the full tool catalog. For raw MCP requests, call
`tools/list` with `{"profile":"full","category":"SceneAudit"}`. Reconnect if
your client caches its tool catalog.

## Call the tool and change the scene

Create a new empty scene and save it as `Assets/ColliderDemo.unity`. Add a Cube
and Sphere from **GameObject > 3D Object**. Each primitive has one 3D collider.
Call `sceneaudit_count_colliders` with `{}`. Within the tool result, expect
these values.

```json
{"scene":"ColliderDemo","collider_count":2}
```

Delete the Sphere and call again. The count should be `1`. The tool reads the
active scene each time and counts components on inactive objects and disabled
colliders. Other loaded scenes, prefab assets and `Collider2D` components are
outside its scope.

## Verify module controls

Call `manage_module` with `{"action":"set_enabled","id":"SceneAudit","enabled":false}`.
Rediscover tools and verify the collider tool is absent. A direct call by name
should return an error beginning with `Tool not found`. Set `enabled` to `true`,
rediscover, and call again. The count should still be `1`.

To use the tool in another project, follow the [export and import steps](Modules.md#reuse-a-module).
The archive includes the added source file. Enable the imported module after
compilation and repeat the same scene check in the destination.
