# i1rr-unity-mcp

## Install

Requires Unity 2022.3+.

1. Open **Edit > Project Settings > Package Manager** and add a scoped registry:

   | Field | Value |
   | --- | --- |
   | Name | i1rr |
   | URL | `https://i1rr.github.io/unity-MCP` |
   | Scope | `i1rr-unity-mcp` |

2. Open **Window > Package Management > Package Manager**, choose **Install package
   by name** from the **+** menu, and enter `i1rr-unity-mcp`.
3. Open **Tools > i1rr-unity-mcp Dashboard** and connect your AI assistant's HTTP
   MCP client to the endpoint in **Overview**. A VS Code MCP configuration is also
   created in your project.

Refresh Package Manager to check for new releases, then select the MCP to update.

Already using the npm registry? Change its URL to the one above, keeping the
exact scope `i1rr-unity-mcp`, then refresh Package Manager. No npm account is needed.

### Install from Git

With Git installed, choose **Install package from Git URL** in Package Manager:

   ```text
   https://github.com/i1rr/unity-MCP.git#v1.0.2
   ```

Git installs stay on their selected release. To use registry updates, remove the
Git-installed package and follow the registry installation steps above.

## Capabilities

- Inspect and edit scenes, GameObjects, components and assets.
- Run Unity tests and inspect Play Mode and development players.
- Capture Game View screenshots and video.
- Use optional Addressables tools when that integration is installed.
- Create project tools and import or export modules for other projects.
- Let AI assistants record MCP issues locally and read them from the Dashboard.

## Dashboard

### Overview

Check the server, copy the connection endpoint and manage clients and modules.
Use **New module**, **Import…** and **Export…** for your project tools.

![Overview tab](Documentation~/images/dashboard-overview.png)

### Tools

Search the tool catalog, filter results and see which tools are being used.

![Tools tab](Documentation~/images/dashboard-tools.png)

### Settings

Configure authentication, local MCP issue logging and tool settings.

![Settings tab](Documentation~/images/dashboard-settings.png)

### Activity

Read MCP logs and AI-reported issues. Expand either panel or open a backlog entry.
The screenshot shows an example issue from an empty demo project.

![Activity tab](Documentation~/images/dashboard-activity.png)

## Security

Use Unity Package Manager, not `npm install`. Connect trusted AI clients;
arbitrary C# execution is off by default. See [security and scanner reports](SECURITY.md).

## License

[The i1rr Community License](LICENSE.md) allows personal and commercial development,
free forks and modifications, and selling what you build with the MCP.

Selling, hosting or bundling the MCP as a paid offering requires written
permission: [i1rr.dev@gmail.com](mailto:i1rr.dev@gmail.com).
