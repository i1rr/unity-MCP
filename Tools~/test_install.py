"""Verify a tarball or Git dependency in a new, package-only Unity project."""
import argparse
import json
from pathlib import Path
import re
import shutil
import subprocess

NAME = "i1rr-unity-mcp"
PROBE = """using System.IO;
using UnityEditor.PackageManager;
using UnityEngine;
using I1rr.UnityMcp.Editor;

public static class PackageInstallProbe
{
    public static void Check()
    {
        var package = PackageInfo.FindForAssembly(typeof(McpEditorServer).Assembly);
        File.WriteAllLines("Library/install-result.txt", new[] {package.name, package.version, Application.unityVersion});
    }
}
"""

def check(unity, project, dependency, version, commit):
    project.mkdir(parents=True, exist_ok=False)
    editor = project / "Assets/Probe/Editor"
    editor.mkdir(parents=True)
    (editor / "InstallProbe.asmdef").write_text(json.dumps({
        "name": "Release.InstallProbe", "references": ["I1rr.UnityMcp.Editor"], "includePlatforms": ["Editor"]}))
    (editor / "PackageInstallProbe.cs").write_text(PROBE)
    packages = project / "Packages"
    packages.mkdir()
    if dependency.startswith(("https://", "ssh://", "git+")):
        source = dependency
    else:
        archive = Path(dependency).resolve(strict=True)
        shutil.copyfile(archive, packages / archive.name)
        source = "file:" + archive.name
    (packages / "manifest.json").write_text(json.dumps({"dependencies": {NAME: source}}, indent=2))
    # Seed the version file to avoid an interactive upgrade prompt in a fresh project.
    output = subprocess.check_output([unity, "-version"], text=True).strip()
    match = re.search(r"\b\d+\.\d+\.\d+[abfp]\d+\b", output)
    if not match:
        raise RuntimeError("Cannot determine the Unity executable version: " + output)
    (project / "ProjectSettings").mkdir()
    (project / "ProjectSettings/ProjectVersion.txt").write_text("m_EditorVersion: " + match[0] + "\n")
    log = project / "install.log"
    subprocess.run([unity, "-batchmode", "-nographics", "-quit", "-projectPath", project,
                    "-executeMethod", "PackageInstallProbe.Check", "-logFile", log], check=True, timeout=600)
    result = (project / "Library/install-result.txt").read_text(encoding="utf-8-sig").splitlines()
    if result != [NAME, version, match[0]]:
        raise RuntimeError(f"Unexpected installation {result}; see {log}")
    lock = json.loads((packages / "packages-lock.json").read_bytes())["dependencies"][NAME]
    if commit and lock.get("hash") != commit:
        raise RuntimeError(f"Unexpected Git resolution: {lock}")
    print(json.dumps({"package": result[0], "version": result[1], "unity": result[2], "lock": lock}, indent=2))

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unity", required=True, type=Path)
    parser.add_argument("--project", required=True, type=Path, help="A new disposable directory")
    parser.add_argument("--package", required=True, help="A local .tgz or a Unity Git URL")
    parser.add_argument("--version", required=True)
    parser.add_argument("--commit", help="Expected Git resolution for a tagged install")
    args = parser.parse_args()
    check(args.unity.resolve(), args.project.resolve(), args.package, args.version, args.commit)
