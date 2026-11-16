"""Check an immutable release archive with npm; publish only with --publish."""
import argparse
import base64
import hashlib
import json
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import tarfile
import tempfile
import time
import urllib.error
import urllib.request
from urllib.parse import urlparse

from pack_release import ROOT, build, git


def archive_files(archive):
    files = {}
    with tarfile.open(archive) as tar:
        for entry in tar:
            path = PurePosixPath(entry.name)
            if path.is_absolute() or ".." in path.parts or path.parts[:1] != ("package",):
                raise ValueError("Unexpected archive member: " + entry.name)
            if entry.isdir():
                continue
            if not entry.isfile() or entry.name in files:
                raise ValueError("Only unique regular files can ship: " + entry.name)
            files[entry.name] = tar.extractfile(entry).read()
    return files


def verify_signed_archive(signed, reference):
    expected, actual = archive_files(reference), archive_files(signed)
    signature = "package/.attestation.p7m"
    if not actual.pop(signature, b""):
        raise ValueError("Missing Unity signature; create the archive with Tools~/sign_release.py")
    # Unity's packer omits Git's contributor-only ignore file.
    expected.pop("package/.gitignore", None)
    actual.pop("package/.gitignore", None)
    if expected.keys() != actual.keys():
        raise ValueError(f"Signed file set differs from Git: missing {sorted(expected.keys() - actual.keys())}; unexpected {sorted(actual.keys() - expected.keys())}")
    for name, raw in expected.items():
        if name == "package/package.json":
            equal = json.loads(raw) == json.loads(actual[name])
        else:
            equal = raw == actual[name]
        if not equal:
            raise ValueError("Signed content differs from Git: " + name)


def verify_publication(registry, name, version, integrity, timeout=600):
    deadline = time.monotonic() + timeout
    announced = False
    while True:
        try:
            request = urllib.request.Request(registry + name + "/" + version,
                                             headers={"Cache-Control": "no-cache"})
            with urllib.request.urlopen(request, timeout=30) as response:
                published = json.load(response)
            if published["dist"]["integrity"] != integrity:
                raise ValueError("Registry archive differs from the tested archive")
            return
        except urllib.error.HTTPError as error:
            if error.code not in {404, 429, 500, 502, 503, 504}:
                raise
        if time.monotonic() >= deadline:
            raise RuntimeError("npm accepted the upload but the version is not public yet. Check registry metadata or npm's staged packages before retrying; do not resubmit blindly.")
        if not announced:
            print("npm accepted the upload; waiting for the public version and archive checksum...", flush=True)
            announced = True
        time.sleep(5)


def npm_command(cli):
    if cli:
        node = shutil.which("node")
        if not node:
            raise ValueError("Install Node.js before using --npm-cli")
        return [node, str(cli.resolve(strict=True))]
    npm = shutil.which("npm.cmd") or shutil.which("npm")
    if not npm:
        raise ValueError("Install Node.js LTS (including npm), or pass --npm-cli <npm-cli.js>")
    if Path(npm).suffix.lower() == ".cmd":
        # Invoke npm's JavaScript directly, without routing arguments through cmd.exe.
        script = Path(npm).parent / "node_modules/npm/bin/npm-cli.js"
        node = shutil.which("node")
        if not node or not script.is_file():
            raise ValueError("Cannot locate npm's JavaScript entry point; pass --npm-cli <npm-cli.js>")
        return [node, str(script)]
    return [npm]


def npm_json(command, *arguments):
    result = subprocess.run([*command, *arguments, "--json"], cwd=ROOT,
                            capture_output=True, text=True, encoding="utf-8")
    try:
        data = json.loads(result.stdout)
    except json.JSONDecodeError:
        raise RuntimeError("npm did not return JSON; inspect its local debug log") from None
    if result.returncode:
        error = data.get("error", {})
        raise RuntimeError(f"npm {error.get('code', result.returncode)}: {error.get('summary', 'request failed')}")
    return data


def publish(revision, registry, cli, upload, signed_archive=None):
    url = urlparse(registry)
    if (url.scheme != "https" and not (url.scheme == "http" and url.hostname in {"127.0.0.1", "localhost", "::1"})) or url.username or url.password or url.query or url.fragment:
        raise ValueError("Use an HTTPS registry, or HTTP on loopback for an isolated test")
    registry = registry.rstrip("/") + "/"
    if upload and url.hostname in {"registry.npmjs.org", "registry.npmjs.com"}:
        raise ValueError("Public npm publishing is retired: Unity dependency IDs collide with unrelated npm packages. Use Documentation~/RegistryPublishing.md. Local registry tests and archive verification remain available.")
    if upload and not signed_archive:
        raise ValueError("Publishing requires --archive <Unity-signed.tgz>; run Tools~/sign_release.py first")
    command = npm_command(cli)
    with tempfile.TemporaryDirectory(prefix="mcp-npm-") as temporary:
        archive = build(revision, Path(temporary))
        if signed_archive:
            signed_archive = signed_archive.resolve(strict=True)
            verify_signed_archive(signed_archive, archive)
            archive = signed_archive
            print("Validated the Unity-signed archive against the committed files.", flush=True)
        with tarfile.open(archive) as tar:
            manifest = json.load(tar.extractfile("package/package.json"))
        name, version = manifest["name"], manifest["version"]
        commit = git("rev-parse", revision + "^{commit}").decode().strip()
        tag = "v" + version
        if upload:
            # A working commit may be checked, but only its exact release tag can publish.
            tagged = git("rev-parse", "--verify", "refs/tags/" + tag + "^{commit}").decode().strip()
            if commit != tagged:
                raise ValueError(f"Publish the tested {tag} commit; this revision differs")
            identity = npm_json(command, "whoami", "--registry", registry)
            if not isinstance(identity, str) or not identity:
                raise ValueError("Run npm login for this registry before publishing")
            try:
                with urllib.request.urlopen(registry + name + "/" + version, timeout=30):
                    raise ValueError(f"{name}@{version} already exists; published versions cannot be replaced")
            except urllib.error.HTTPError as error:
                if error.code != 404:
                    raise
        arguments = ["publish", str(archive), "--registry", registry, "--access", "public",
                     "--ignore-scripts", "--tag", "next" if "-" in version else "latest"]
        if not upload:
            arguments.append("--dry-run")
        if upload:
            # Keep the terminal attached for npm's own browser/2FA confirmation.
            subprocess.run([*command, *arguments, "--loglevel", "error"], cwd=ROOT, check=True)
        else:
            result = npm_json(command, *arguments)
            item = result[0] if isinstance(result, list) else result.get(name, result)
            if item.get("id") != name + "@" + version:
                raise ValueError("npm reported a different package identity")
        raw = archive.read_bytes()
        if upload:
            verify_publication(registry, name, version,
                               "sha512-" + base64.b64encode(hashlib.sha512(raw).digest()).decode())
        print(json.dumps({"package": name, "version": version, "commit": commit,
                          "registry": registry, "published": upload,
                          "archive_sha256": hashlib.sha256(raw).hexdigest()}, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--revision", required=True, help="Release tag, or a working revision for a dry run")
    parser.add_argument("--registry", default="https://registry.npmjs.org/")
    parser.add_argument("--npm-cli", type=Path, help="Optional portable npm-cli.js")
    parser.add_argument("--archive", type=Path, help="Unity-signed archive; its payload must match the chosen revision")
    parser.add_argument("--publish", action="store_true", help="Upload the exact tagged archive; otherwise dry-run only")
    args = parser.parse_args()
    publish(args.revision, args.registry, args.npm_cli, args.publish, args.archive)
