"""Build and check a Unity Package Manager tarball from an immutable Git commit."""
import argparse
import gzip
import hashlib
import io
import json
import re
import subprocess
import tarfile
import tempfile
from pathlib import Path, PurePosixPath

ROOT = Path(__file__).resolve().parents[1]
NAME = "i1rr-unity-mcp"
# SemVer prerelease identifiers allow leading zeros only in nonnumeric identifiers.
NUMBER = r"(?:0|[1-9]\d*)"
PRE = rf"(?:{NUMBER}|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)"
VERSION = rf"{NUMBER}\.{NUMBER}\.{NUMBER}(?:-{PRE}(?:\.{PRE})*)?"


def git(*args):
    return subprocess.check_output(["git", "-c", "core.autocrlf=false", "-c", "core.eol=lf", *args], cwd=ROOT)


def build(revision, output):
    commit = git("rev-parse", "--verify", "--end-of-options", revision + "^{commit}").decode().strip()
    timestamp = git("show", "-s", "--format=%cI", commit).decode().strip()
    archive = git("archive", "--format=tar", "--prefix=package/", "--mtime=" + timestamp, commit)
    with tarfile.open(fileobj=io.BytesIO(archive)) as tar:
        files = {}
        for entry in tar:
            path = PurePosixPath(entry.name)
            if path.parts[:1] != ("package",) or ".." in path.parts:
                raise ValueError(f"Unexpected archive member: {entry.name}")
            if entry.isdir():
                continue
            if not entry.isfile():
                raise ValueError(f"Only regular files can ship: {entry.name}")
            files[path.relative_to("package").as_posix()] = tar.extractfile(entry).read()

    manifest = json.loads(files["package.json"])
    version = manifest["version"]
    if manifest["name"] != NAME or manifest["license"] != "SEE LICENSE IN LICENSE.md" or not re.fullmatch(VERSION, version, re.ASCII):
        raise ValueError("Expected the project package ID, declared community license and a SemVer release or prerelease")
    if not files["LICENSE.md"].startswith(b"i1rr Community License 1.0"):
        raise ValueError("Missing community license")
    if not re.search(rf'^ *public const string ServerVersion = "{re.escape(version)}";$', files["Shared/McpProtocol.cs"].decode(), re.M):
        raise ValueError("MCP server version differs from the package manifest")
    if not re.search(rf"^## \[{re.escape(version)}\](?:\s+- [^\n]+)?$", files["CHANGELOG.md"].decode(), re.M):
        raise ValueError(f"Missing changelog entry for {version}")
    metadata_guids = set()
    for path in files:
        parts = PurePosixPath(path).parts
        contributor_file = path in {".editorconfig", ".gitignore", ".p4ignore", ".agents/skills/unity-mcp-release/SKILL.md"}
        if not contributor_file and any(p.startswith(".") or p in {"Library", "Temp", "UserSettings", "node_modules"} for p in parts):
            raise ValueError(f"Unexpected private or generated file: {path}")
        if path.endswith((".log", ".tgz", ".token", ".csproj", ".sln")):
            raise ValueError(f"Unexpected generated file: {path}")
        if path.endswith(".meta"):
            match = re.search(rb"^guid: ([a-f0-9]{32})\r?$", files[path], re.M)
            if not match or match[1] in metadata_guids:
                raise ValueError(f"Invalid or duplicate Unity metadata GUID: {path}")
            metadata_guids.add(match[1])
        if parts[0].endswith("~") or contributor_file or path.endswith(".meta"):
            continue
        if parts[0] == "Tests" and any(part.casefold() == "resources" for part in parts):
            raise ValueError(f"Test Resources would enter ordinary consumer players: {path}. Use an in-memory fixture or stage assets only for a test player.")
        for i in range(1, len(parts) + 1):
            asset = "/".join(parts[:i])
            if asset + ".meta" not in files:
                raise ValueError(f"Missing Unity metadata: {asset}")
    plugin = "Editor/Plugins/Windows/x86_64/mcpkeeper.dll"
    native = "Editor/Plugins/native/"
    provenance = json.loads(files[native + "provenance.json"])
    for path, field in [(plugin, "dll_sha256"), (native + "build.py", "recipe_sha256"), (native + "mcpkeeper.c", "source_sha256")]:
        if hashlib.sha256(files[path]).hexdigest() != provenance[field]:
            raise ValueError(f"Native provenance mismatch: {path}. Rebuild and verify the native keeper.")
    if not files[plugin].startswith(b"MZ"):
        raise ValueError("Native plugin must be a hydrated Windows binary")

    output.mkdir(parents=True, exist_ok=True)
    destination = output / f"{NAME}-{version}.tgz"
    with tempfile.TemporaryDirectory(dir=output) as staging:
        temp = Path(staging) / "package.tgz"
        with temp.open("wb") as stream, gzip.GzipFile(filename="", fileobj=stream, mode="wb", mtime=0) as gz:
            gz.write(archive)
        digest = hashlib.sha256(temp.read_bytes()).hexdigest()
        if destination.exists() and hashlib.sha256(destination.read_bytes()).hexdigest() != digest:
            raise ValueError(f"Another build already occupies {destination}; choose a fresh output directory")
        temp.replace(destination)
    print(f"{destination}\nGit {commit}\nSHA256 {digest}\n{len(files)} package files")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--revision", default="HEAD")
    parser.add_argument("--output", type=Path, default=ROOT / "dist~")
    args = parser.parse_args()
    build(args.revision, args.output.resolve())
