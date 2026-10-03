#!/usr/bin/env python3
"""Fetch a checksum-pinned Microsoft compiler and unmodified Unity headers."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import tarfile
import urllib.request


HERE = Path(__file__).resolve().parent
LOCK = HERE / "dependencies.json"
MARKER = ".grass-shader-dependencies.json"


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def compiler_files(directory):
    return {str(path.relative_to(directory)): sha256(path)
            for path in sorted(directory.rglob("*")) if path.is_file()}


def run(arguments, cwd=None):
    environment = dict(os.environ, GIT_LFS_SKIP_SMUDGE="1", GIT_TERMINAL_PROMPT="0")
    return subprocess.run(arguments, cwd=cwd, env=environment, check=True,
                          text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                          timeout=240).stdout.strip()


def extract_compiler(archive, destination):
    # The official Linux release contains plain files/directories. Reject links,
    # devices, and escaping paths instead of trusting an archive's member names.
    destination.mkdir(parents=True, exist_ok=True)
    with tarfile.open(archive, "r:gz") as package:
        for member in package.getmembers():
            path = Path(member.name)
            if path.is_absolute() or ".." in path.parts or not (member.isfile() or member.isdir()):
                raise ValueError("Unsafe compiler archive member: " + member.name)
        package.extractall(destination, filter="data")


def prepare(destination):
    if platform.system() != "Linux" or platform.machine().lower() not in ("x86_64", "amd64"):
        raise ValueError("The pinned compiler distribution requires Linux x86-64.")
    lock = json.loads(LOCK.read_text())
    lock_digest = sha256(LOCK)
    marker = destination / MARKER
    if destination.exists():
        if not marker.is_file():
            raise ValueError("Refusing to replace an unmarked dependency directory: " + str(destination))
        receipt = json.loads(marker.read_text())
        if receipt.get("lock_sha256") == lock_digest:
            compiler = destination / "dxc/bin/dxc"
            graphics = destination / "Graphics"
            if (compiler.is_file() and compiler_files(destination / "dxc") == receipt.get("compiler_files") and
                    sha256(destination / "dxc.tar.gz") == lock["dxc"]["sha256"] and
                    run(["git", "rev-parse", "HEAD"], graphics) == lock["graphics"]["commit"] and
                    not run(["git", "status", "--porcelain", "--untracked-files=all"], graphics)):
                return destination
        raise ValueError("Dependency cache changed or is stale; remove this marked directory and prepare again.")

    destination.mkdir(parents=True)
    # Mark ownership before downloading, so interrupted preparation is safe to remove.
    marker.write_text(json.dumps({"lock_sha256": "incomplete"}) + "\n")
    archive = destination / "dxc.tar.gz"
    request = urllib.request.Request(lock["dxc"]["url"], headers={"User-Agent": "cone-grass-shader-checks"})
    with urllib.request.urlopen(request, timeout=120) as response, archive.open("wb") as output:
        shutil.copyfileobj(response, output)
    if sha256(archive) != lock["dxc"]["sha256"]:
        raise ValueError("DXC archive SHA-256 does not match the official release digest.")
    extract_compiler(archive, destination / "dxc")

    graphics = destination / "Graphics"
    graphics.mkdir()
    run(["git", "init", "--quiet"], graphics)
    run(["git", "remote", "add", "origin", lock["graphics"]["repository"]], graphics)
    run(["git", "sparse-checkout", "init", "--cone"], graphics)
    run(["git", "sparse-checkout", "set", *lock["graphics"]["sparse_paths"]], graphics)
    run(["git", "-c", "protocol.file.allow=never", "fetch", "--quiet", "--depth=1",
         "--filter=blob:none", "origin", lock["graphics"]["commit"]], graphics)
    run(["git", "checkout", "--quiet", "--detach", "FETCH_HEAD"], graphics)
    package = json.loads((graphics / "Packages/com.unity.render-pipelines.universal/package.json").read_text())
    if package["version"] != lock["graphics"]["urp_version"]:
        raise ValueError("Pinned Graphics checkout does not declare the expected URP version.")
    marker.write_text(json.dumps({
        "lock_sha256": lock_digest,
        "compiler_files": compiler_files(destination / "dxc"),
        "graphics_commit": lock["graphics"]["commit"],
        "urp_version": package["version"],
    }, indent=2) + "\n")
    return destination


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=HERE.parents[1] / "Validation~/ShaderDependencies")
    arguments = parser.parse_args()
    try:
        destination = prepare(arguments.output.resolve())
    except (ValueError, OSError, subprocess.SubprocessError) as exception:
        if isinstance(exception, subprocess.CalledProcessError):
            print(exception.stderr)
        parser.exit(1, str(exception) + "\n")
    print("Prepared pinned shader dependencies: " + str(destination))


if __name__ == "__main__":
    main()
