#!/usr/bin/env python3
"""Compile real package HLSL with DXC; this does not run the Unity shader importer."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys

from prepare import HERE, LOCK, prepare


# Deliberately bounded, named coverage. This is not Unity's variant enumeration.
FORWARD_VARIANTS = {
    "default": [],
    "main_shadow": ["_MAIN_LIGHT_SHADOWS"],
    "cascade_soft_ground_fog": ["_MAIN_LIGHT_SHADOWS_CASCADE", "_SHADOWS_SOFT",
                                "_GRASS_GROUND_NORMAL", "FOG_LINEAR"],
    "screen_soft_ground_fog": ["_MAIN_LIGHT_SHADOWS_SCREEN", "_SHADOWS_SOFT_HIGH",
                               "_GRASS_GROUND_NORMAL", "FOG_EXP"],
    "additional_forward": ["_MAIN_LIGHT_SHADOWS", "_ADDITIONAL_LIGHTS",
                            "_ADDITIONAL_LIGHT_SHADOWS", "_GRASS_ADDITIONAL_LIGHTS"],
    "additional_cluster": ["_MAIN_LIGHT_SHADOWS_CASCADE", "_ADDITIONAL_LIGHTS",
                            "_CLUSTER_LIGHT_LOOP", "_ADDITIONAL_LIGHT_SHADOWS",
                            "_GRASS_ADDITIONAL_LIGHTS", "_GRASS_GROUND_NORMAL",
                            "_SHADOWS_SOFT_MEDIUM", "FOG_EXP2"],
}
TERRAIN_VARIANTS = {
    "default": [], "mask": ["_MASKMAP"], "height": ["_TERRAIN_BLEND_HEIGHT"],
    "height_mask": ["_TERRAIN_BLEND_HEIGHT", "_MASKMAP"],
}
BACKENDS = {
    "dxil": ("SHADER_API_D3D11", []),
    "spirv": ("SHADER_API_VULKAN", ["-spirv", "-fspv-target-env=vulkan1.1", "-fvk-use-dx-layout"]),
}
STAGES = {"vertex": ("vs_6_0", "SHADER_STAGE_VERTEX"),
          "fragment": ("ps_6_0", "SHADER_STAGE_FRAGMENT"),
          "compute": ("cs_6_0", "SHADER_STAGE_COMPUTE")}


def mask_comments(text):
    """Ignore commented ShaderLab block markers while preserving offsets/lines."""
    return re.sub(r'//[^\n]*|/\*[\s\S]*?\*/',
                  lambda match: re.sub(r'[^\n]', ' ', match.group()), text)


def source_segment(text, start, end, path):
    line = text.count("\n", 0, start) + 1
    return '#line %d "%s"\n%s\n' % (line, path.as_posix(), text[start:end])


def pass_ranges(masked, blocks):
    # ShaderLab braces must not be confused with braces inside HLSL functions,
    # comments, or strings. Retain positions for accurate source diagnostics.
    control = list(masked)
    for block in blocks:
        control[block.start():block.end()] = re.sub(r'[^\n]', ' ', block.group())
    control = "".join(control)
    braces = re.sub(r'"(?:\\.|[^"\\])*"',
                    lambda match: re.sub(r'[^\n]', ' ', match.group()), control)
    if len(re.findall(r'\bSubShader\b', braces)) != 1:
        raise ValueError("Offline extraction supports exactly one SubShader per file.")
    closing = {}
    stack = []
    for index, character in enumerate(braces):
        if character == "{":
            stack.append(index)
        elif character == "}":
            if not stack:
                raise ValueError("Unbalanced ShaderLab braces.")
            closing[stack.pop()] = index
    if stack:
        raise ValueError("Unbalanced ShaderLab braces.")
    ranges = []
    for match in re.finditer(r'\bPass\s*\{', braces):
        opening = match.end() - 1
        ranges.append((opening, closing[opening], control))
    return ranges, braces


def shader_programs(path, repository):
    """Extract the HLSL blocks, without modifying shader functions or includes.

    The current package uses either no HLSLINCLUDE or one shared block before
    every Pass. Fail closed if that format changes; this is not a ShaderLab parser.
    """
    text = path.read_text()
    masked = mask_comments(text)
    relative = path.relative_to(repository)
    matches = list(re.finditer(r'\b(HLSLINCLUDE|HLSLPROGRAM)\b([\s\S]*?)\bENDHLSL\b', masked))
    if re.search(r'\b(CGPROGRAM|CGINCLUDE)\b', masked):
        raise ValueError("Legacy CG blocks are outside offline coverage: " + str(relative))
    starts = list(re.finditer(r'\b(HLSLINCLUDE|HLSLPROGRAM)\b', masked))
    if len(starts) != len(matches):
        raise ValueError("Unclosed HLSL block: " + str(relative))
    shared = [match for match in matches if match.group(1) == "HLSLINCLUDE"]
    programs = [match for match in matches if match.group(1) == "HLSLPROGRAM"]
    if not programs:
        raise ValueError("No shader programs found: " + str(relative))
    if len(shared) > 1 or (shared and shared[0].start() > programs[0].start()):
        raise ValueError("Scoped HLSLINCLUDE needs explicit extractor support: " + str(relative))
    passes, braces = pass_ranges(masked, matches)
    if shared:
        depth = braces[:shared[0].start()].count("{") - braces[:shared[0].start()].count("}")
        if depth not in (1, 2) or any(start < shared[0].start() for start, _, _ in passes):
            raise ValueError("Scoped HLSLINCLUDE needs explicit extractor support: " + str(relative))
    prefix = "".join(source_segment(text, match.start(2), match.end(2), relative) for match in shared)
    for index, match in enumerate(programs):
        owners = [(start, end, control) for start, end, control in passes
                  if start < match.start() < end]
        if len(owners) != 1:
            raise ValueError("HLSLPROGRAM must belong to one Pass: " + str(relative))
        start, end, control = owners[0]
        if sum(start < other.start() < end for other in programs) != 1:
            raise ValueError("Multiple HLSLPROGRAM blocks in one Pass: " + str(relative))
        before = control[start:match.start()]
        names = re.findall(r'\bName\s+"([^"\n]+)"', before)
        if len(names) > 1:
            raise ValueError("Multiple names in one Pass: " + str(relative))
        name = names[-1] if names else "pass_" + str(index)
        block = masked[match.start(2):match.end(2)]
        target = re.search(r'^\s*#pragma\s+target\s+(\d+)\.(\d+)\s*$', block, re.M)
        if not target:
            raise ValueError("Explicit target missing: " + str(relative) + " / " + name)
        target_value = int(target.group(1)) * 10 + int(target.group(2))
        source = prefix + source_segment(text, match.start(2), match.end(2), relative)
        for stage in ("vertex", "fragment"):
            entries = re.findall(r'^\s*#pragma\s+' + stage + r'\s+(\w+)\s*$', block, re.M)
            if len(entries) != 1:
                raise ValueError("Expected one " + stage + " entry: " + str(relative) + " / " + name)
            variants = FORWARD_VARIANTS if name == "GrassForward" else (
                TERRAIN_VARIANTS if name == "TerrainAlbedo" else {"default": []})
            for variant, keywords in variants.items():
                yield {"path": str(relative), "pass": name, "stage": stage, "entry": entries[0],
                       "variant": variant, "keywords": keywords, "target": target_value,
                       "source": source}


def compute_programs(path, repository):
    text = path.read_text()
    entries = re.findall(r'^\s*#pragma\s+kernel\s+(\w+)([^\n]*)$', mask_comments(text), re.M)
    if not entries:
        raise ValueError("No compute kernels found: " + str(path))
    relative = path.relative_to(repository)
    for entry, defines in entries:
        yield {"path": str(relative), "pass": entry, "stage": "compute", "entry": entry,
               "variant": "default", "keywords": defines.split(), "target": 50,
               "source": source_segment(text, 0, len(text), relative)}


def programs(repository):
    for root in (repository / "Runtime", repository / "Editor/GroundColor"):
        for path in sorted(root.rglob("*.shader")):
            yield from shader_programs(path, repository)
        for path in sorted(root.rglob("*.compute")):
            yield from compute_programs(path, repository)


def command(compiler, source, output, program, backend, repository, graphics):
    profile, stage_define = STAGES[program["stage"]]
    api_define, backend_flags = BACKENDS[backend]
    definitions = [api_define + "=1", "UNITY_COMPILER_DXC=1", "UNITY_COMPILER_HLSL=1",
                   "UNITY_VERSION=60060000", "SHADER_TARGET=" + str(program["target"]),
                   stage_define + "=1", *program["keywords"]]
    result = [str(compiler), "-T", profile, "-E", program["entry"], "-HV", "2018", "-O3",
              "-I", str(graphics), "-I", str(repository / Path(program["path"]).parent),
              "-I", str(repository),
              "-Fo", str(output), *backend_flags]
    for definition in definitions:
        result.extend(["-D", definition])
    return result + [str(source)]


def check(repository, dependencies, output, backends):
    prepare(dependencies)  # Verifies the cache; does not accept substitute headers.
    compiler = dependencies / "dxc/bin/dxc"
    graphics = dependencies / "Graphics"
    output.mkdir(parents=True, exist_ok=True)
    environment = dict(os.environ)
    environment["LD_LIBRARY_PATH"] = str(dependencies / "dxc/lib") + (
        ":" + environment["LD_LIBRARY_PATH"] if environment.get("LD_LIBRARY_PATH") else "")
    version = subprocess.run([str(compiler), "--version"], text=True, capture_output=True,
                             env=environment, check=True, timeout=30).stdout.strip()
    print("Compiler: " + version, flush=True)
    results = []
    identities = set()
    for program in programs(repository):
        identity = "__".join([Path(program["path"]).stem, program["pass"],
                               program["variant"], program["stage"]])
        if not re.fullmatch(r'[A-Za-z0-9_]+', identity):
            raise ValueError("Unsupported program identity: " + identity)
        if identity in identities:
            raise ValueError("Duplicate artifact identity: " + identity)
        identities.add(identity)
        source = output / (identity + ".hlsl")
        source.write_text(program["source"])
        for backend in backends:
            binary = output / (identity + "." + backend)
            binary.unlink(missing_ok=True)
            arguments = command(compiler, source, binary, program, backend, repository, graphics)
            result = subprocess.run(arguments, env=environment, text=True, capture_output=True, timeout=120)
            log = output / (identity + "." + backend + ".log")
            log.write_text(result.stdout + result.stderr)
            passed = result.returncode == 0 and binary.is_file() and binary.stat().st_size > 0
            row = {key: value for key, value in program.items() if key != "source"}
            row.update({"backend": backend, "passed": passed, "log": log.name,
                        "command": arguments, "source_sha256": hashlib.sha256(program["source"].encode()).hexdigest()})
            results.append(row)
            print(("PASS " if passed else "FAIL ") + backend + " " + identity, flush=True)
            if not passed:
                print(result.stdout + result.stderr, flush=True)
    report = {"scope": "Offline DXC compilation; no Unity import or GPU execution",
              "compiler": version, "dependencies": json.loads(LOCK.read_text()),
              "results": results, "passed": sum(row["passed"] for row in results),
              "failed": sum(not row["passed"] for row in results)}
    (output / "report.json").write_text(json.dumps(report, indent=2) + "\n")
    summary = ("## Offline shader compilation\n\n" + str(report["passed"]) + " passed; " +
               str(report["failed"]) + " failed. DXC compiles the package's actual HLSL with pinned, "
               "unmodified Unity Graphics headers. This checks the listed DXIL/SPIR-V variants, "
               "not Unity shader import, legacy shader-model support, runtime bindings, or rendered output.\n\n")
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a") as stream:
            stream.write(summary)
    print(summary)
    return 0 if results and report["failed"] == 0 else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=HERE.parents[1])
    parser.add_argument("--dependencies", type=Path, default=HERE.parents[1] / "Validation~/ShaderDependencies")
    parser.add_argument("--output", type=Path, default=HERE.parents[1] / "artifacts/shaders")
    parser.add_argument("--backend", choices=[*BACKENDS, "all"], default="all")
    arguments = parser.parse_args()
    try:
        return check(arguments.repo.resolve(), arguments.dependencies.resolve(), arguments.output.resolve(),
                     list(BACKENDS) if arguments.backend == "all" else [arguments.backend])
    except (ValueError, OSError, subprocess.SubprocessError) as exception:
        if isinstance(exception, subprocess.CalledProcessError):
            print(exception.stderr)
        parser.exit(1, str(exception) + "\n")


if __name__ == "__main__":
    sys.exit(main())
