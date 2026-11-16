"""Sign committed release files with Unity's Client.Pack and your Unity organization."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile

from pack_release import ROOT, build, git
from publish_npm import verify_signed_archive


def sign(revision, unity, project, organization, output):
    version_text = subprocess.check_output([str(unity), "-version"], text=True, encoding="utf-8").strip()
    version = re.search(r"\b(\d+)\.(\d+)\.\d+[abfp]\d+\b", version_text)
    if not version or tuple(map(int, version.groups())) < (6000, 3):
        raise ValueError("Signing requires Unity 6.3 or newer; package consumers can use older supported Editors")
    project.mkdir(parents=True, exist_ok=False)
    output.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="mcp-sign-") as temporary:
        archive = build(revision, Path(temporary))
        if (output / archive.name).exists():
            raise ValueError("Choose a fresh output directory; signed releases must not be overwritten")
        # Keep signing input outside any checkout. Unity otherwise rewrites the
        # manifest's repository URL/revision using the nearest parent Git repo.
        source_root = Path(temporary) / "input"
        source_root.mkdir()
        with tarfile.open(archive) as tar:
            # The packer has already checked all members; refuse links and traversal here too.
            for entry in tar:
                path = PurePosixPath(entry.name)
                if not (entry.isfile() or entry.isdir()) or path.is_absolute() or ".." in path.parts or path.parts[:1] != ("package",):
                    raise ValueError("Unsafe archive member: " + entry.name)
            tar.extractall(source_root, filter="data")
        source = source_root / "package"
        editor = project / "Assets/Editor"
        editor.mkdir(parents=True)
        shutil.copyfile(ROOT / "Tools~/SignMcpRelease.cs", editor / "SignMcpRelease.cs")
        packages = project / "Packages"
        packages.mkdir()
        (packages / "manifest.json").write_text(json.dumps({"dependencies": {
            "i1rr-unity-mcp": "file:" + source.as_posix()}}, indent=2), encoding="utf-8")
        settings = project / "ProjectSettings"
        settings.mkdir()
        (settings / "ProjectVersion.txt").write_text("m_EditorVersion: " + version[0] + "\n", encoding="utf-8")
        arguments = [str(unity), "-projectPath", str(project),
                                 "-executeMethod", "SignMcpRelease.Run", "-mcpSignSource", str(source),
                                 "-mcpSignOutput", str(output), "-mcpSignOrganization", organization,
                                 "-logFile", str(project / "signing.log")]
        if sys.platform == "win32":
            arguments.append("-force-d3d11")
        result = subprocess.run(arguments, timeout=600)
        marker = project / "Library/mcp-signed-archive.txt"
        if result.returncode or not marker.exists():
            error = project / "Library/mcp-sign-error.txt"
            detail = error.read_text(encoding="utf-8-sig") if error.exists() else "Inspect the isolated project's signing.log"
            raise RuntimeError("Unity did not create a signed archive: " + detail)
        signed = Path(marker.read_text(encoding="utf-8-sig").strip()).resolve(strict=True)
        if signed.parent != output:
            raise ValueError("Unity returned an unexpected signing destination")
        verify_signed_archive(signed, archive)
        print(json.dumps({"archive": str(signed), "commit": git("rev-parse", revision + "^{commit}").decode().strip(),
                          "organization": organization, "unity": version[0],
                          "sha256": hashlib.sha256(signed.read_bytes()).hexdigest()}, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--revision", required=True)
    parser.add_argument("--unity", required=True, type=Path)
    parser.add_argument("--project", required=True, type=Path, help="A new disposable signing project")
    parser.add_argument("--organization", required=True, help="Unity organization ID; never a token or password")
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    sign(args.revision, args.unity.resolve(strict=True), args.project.resolve(), args.organization, args.output.resolve())
