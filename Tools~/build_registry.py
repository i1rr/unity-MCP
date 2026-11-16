"""Generate a read-only Unity registry from signed, immutable GitHub releases.

Serve the output directory as static files; no Node runtime or npm proxy is used.
"""
import argparse
import base64
import hashlib
import json
from pathlib import Path
import re
import tempfile

from pack_release import NAME, VERSION, build, git
from publish_npm import archive_files, verify_signed_archive

REPOSITORY = "https://github.com/i1rr/unity-MCP"


def version_key(value):
    core, _, prerelease = value.partition("-")
    parts = tuple((0, int(p)) if p.isdigit() else (1, p) for p in prerelease.split("."))
    return (*map(int, core.split(".")), not bool(prerelease), parts)


def generate(releases, output):
    versions, times = {}, {}
    for revision, archive in releases:
        if not re.fullmatch("v" + VERSION, revision, re.ASCII):
            raise ValueError("Use immutable release tags: vX.Y.Z")
        version = revision[1:]
        if version in versions:
            raise ValueError("Duplicate release: " + version)
        with tempfile.TemporaryDirectory(prefix="mcp-registry-") as temporary:
            reference = build(revision, Path(temporary))
            verify_signed_archive(archive, reference)
        files = archive_files(archive)
        manifest = json.loads(files["package/package.json"])
        if manifest["name"] != NAME or manifest["version"] != version:
            raise ValueError("Archive does not match its release tag")
        if manifest.get("scripts"):
            raise ValueError("Unity registry packages must not contain npm lifecycle scripts")
        # Keep Unity dependency declarations intact. The consumer's exact registry
        # scope routes those dependencies to Unity, never through an npm proxy.
        payload = archive.read_bytes()
        manifest["_id"] = NAME + "@" + version
        manifest["dist"] = {
            "tarball": f"{REPOSITORY}/releases/download/{revision}/{NAME}-{version}.tgz",
            "shasum": hashlib.sha1(payload).hexdigest(),
            "integrity": "sha512-" + base64.b64encode(hashlib.sha512(payload).digest()).decode(),
        }
        versions[version] = manifest
        times[version] = git("show", "-s", "--format=%cI", revision + "^{commit}").decode().strip()
    stable = [v for v in versions if "-" not in v]
    if not stable:
        raise ValueError("At least one stable signed release is required")
    latest = max(stable, key=version_key)
    package = {
        "_id": NAME, "name": NAME, "description": versions[latest]["description"],
        "license": versions[latest]["license"], "repository": versions[latest]["repository"],
        "dist-tags": {"latest": latest}, "versions": versions,
        "time": {"created": min(times.values()), "modified": max(times.values()), **times},
    }
    prereleases = [v for v in versions if "-" in v]
    if prereleases:
        package["dist-tags"]["next"] = max(prereleases, key=version_key)
    existing = output / NAME
    if existing.exists():
        previous = json.loads(existing.read_text(encoding="utf-8"))["versions"]
        for version, metadata in previous.items():
            if version not in versions:
                raise ValueError("Retain every published registry version: missing " + version)
            if metadata != versions[version]:
                raise ValueError("Published registry metadata cannot be replaced: " + version)
    search = {"objects": [{"package": {
        "name": NAME, "version": latest, "description": package["description"],
        "keywords": versions[latest].get("keywords", []),
        "links": {"repository": REPOSITORY},
    }}], "total": 1, "time": package["time"]["modified"]}
    output.mkdir(parents=True, exist_ok=True)
    documents = {NAME: package, "-/all": {NAME: package}, "-/v1/search": search}
    for name, document in documents.items():
        path = output / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
    (output / ".nojekyll").write_text("", encoding="utf-8")
    (output / "index.html").write_text(
        '<!doctype html><html lang="en"><meta charset="utf-8">'
        '<title>i1rr Unity registry</title><h1>i1rr Unity registry</h1>'
        '<p>Signed Unity packages. Use this site URL as a scoped registry with scope '
        '<code>i1rr-unity-mcp</code>.</p>'
        f'<p><a href="{REPOSITORY}#install">Installation instructions</a> · '
        f'<a href="{REPOSITORY}/blob/main/SECURITY.md">Security</a></p></html>\n',
        encoding="utf-8")
    print(json.dumps({"output": str(output), "latest": latest,
                      "versions": sorted(versions, key=version_key)}, indent=2))


def release_argument(value):
    revision, separator, archive = value.partition("=")
    if not separator:
        raise argparse.ArgumentTypeError("Expected vX.Y.Z=/path/to/signed.tgz")
    return revision, Path(archive).resolve(strict=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--release", action="append", required=True, type=release_argument,
                        help="vX.Y.Z=<signed.tgz>; repeat for every retained release")
    parser.add_argument("--output", required=True, type=Path, help="Static hosting checkout")
    args = parser.parse_args()
    generate(args.release, args.output.resolve())
