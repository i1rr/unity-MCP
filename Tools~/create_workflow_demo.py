"""Create a new disposable project for the public inspect/edit/test/capture demo."""
import argparse
import json
from pathlib import Path
import re
import shutil

ROOT = Path(__file__).resolve().parents[1]


def create(destination, unity_version):
    if not re.fullmatch(r"\d+\.\d+\.\d+[abfp]\d+", unity_version):
        raise ValueError("Use the exact installed Unity version, for example 6000.6.9f1")
    destination.mkdir(parents=True, exist_ok=False)
    try:
        shutil.copytree(ROOT / "Examples~/WorkflowDemo", destination / "Assets/McpWorkflowDemo")
        packages = destination / "Packages"
        packages.mkdir()
        manifest = {
            "scopedRegistries": [{"name": "i1rr", "url": "https://i1rr.github.io/unity-MCP", "scopes": ["i1rr-unity-mcp"]}],
            "dependencies": {"i1rr-unity-mcp": "1.0.3", "com.unity.test-framework": "1.8.0"}
        }
        (packages / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
        settings = destination / "ProjectSettings"
        settings.mkdir()
        (settings / "ProjectVersion.txt").write_text(f"m_EditorVersion: {unity_version}\n", encoding="utf-8")
        (destination / ".gitignore").write_text("Library/\nTemp/\nLogs/\nUserSettings/\n.vscode/mcp.json\n.cursor/mcp.json\n.mcp.json\n", encoding="utf-8")
    except Exception:
        # Keep partial output for diagnosis. Never recursively delete a user path.
        raise
    print(f"Created {destination}\nOpen it in Unity {unity_version}; choose Tools > MCP Workflow Demo > Create scene.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", type=Path, required=True, help="A new directory; existing directories are refused")
    parser.add_argument("--unity-version", default="6000.6.9f1", help="Exact installed Editor version")
    args = parser.parse_args()
    create(args.project.resolve(), args.unity_version)
