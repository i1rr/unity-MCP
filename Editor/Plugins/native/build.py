"""Rebuild the Windows x64 socket keeper using the pinned MSVC and Windows SDK."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile

NATIVE = Path(__file__).resolve().parent
TOOLSET = "14.51.36231"
SDK = "10.0.26100.0"

def digest(data):
    return hashlib.sha256(data).hexdigest()

def build(output):
    program_files = Path(os.environ["ProgramFiles(x86)"])
    vswhere = program_files / "Microsoft Visual Studio/Installer/vswhere.exe"
    roots = subprocess.check_output([vswhere, "-all", "-products", "*", "-property", "installationPath"], text=True)
    tools = next((Path(p) / "VC/Tools/MSVC" / TOOLSET for p in roots.splitlines()
                  if (Path(p) / "VC/Tools/MSVC" / TOOLSET / "bin/Hostx64/x64/cl.exe").is_file()), None)
    if tools is None:
        raise RuntimeError(f"Install MSVC toolset {TOOLSET} with the x64 compiler")
    kits = program_files / "Windows Kits/10"
    includes = [tools / "include"] + [kits / "Include" / SDK / d for d in ("ucrt", "shared", "um")]
    libraries = [tools / "lib/x64"] + [kits / "Lib" / SDK / d / "x64" for d in ("ucrt", "um")]
    for directory in includes + libraries:
        if not directory.is_dir():
            raise RuntimeError(f"Missing compiler/SDK directory: {directory}")
    # Do not inherit flags or include/library overrides from a developer shell.
    env = os.environ.copy()
    for key in ("CL", "_CL_", "LINK", "_LINK_", "INCLUDE", "LIB", "LIBPATH"):
        env.pop(key, None)
    source = (NATIVE / "mcpkeeper.c").read_bytes().replace(b"\r\n", b"\n")
    binaries = tools / "bin/Hostx64/x64"
    with tempfile.TemporaryDirectory() as temp:
        work = Path(temp)
        (work / "mcpkeeper.c").write_bytes(source)
        subprocess.run([binaries / "cl.exe", "/nologo", "/c", "/O2", "/W4", "/WX", "/MT", "/Brepro",
                        "/D_CRT_SECURE_NO_WARNINGS", *("/I" + str(d) for d in includes), "mcpkeeper.c"],
                       cwd=work, env=env, check=True)
        subprocess.run([binaries / "link.exe", "/nologo", "/DLL", "/MACHINE:X64", "/INCREMENTAL:NO", "/Brepro",
                        *("/LIBPATH:" + str(d) for d in libraries), "/OUT:mcpkeeper.dll", "mcpkeeper.obj", "ws2_32.lib"],
                       cwd=work, env=env, check=True)
        dll = (work / "mcpkeeper.dll").read_bytes()
    provenance = {"msvc": TOOLSET, "windows_sdk": SDK, "architecture": "x64",
                  "source_sha256": digest(source), "recipe_sha256": digest(Path(__file__).read_bytes().replace(b"\r\n", b"\n")),
                  "dll_sha256": digest(dll)}
    output.mkdir(parents=True, exist_ok=True)
    (output / "mcpkeeper.dll").write_bytes(dll)
    (output / "provenance.json").write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(provenance))

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    build(parser.parse_args().output)
