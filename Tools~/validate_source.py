#!/usr/bin/env python3
"""Validate package metadata and shader source links without claiming compilation."""

import argparse
import json
import re
import sys
from pathlib import Path


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate JSON key: {key}")
        result[key] = value
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", nargs="?", type=Path, default=Path(__file__).resolve().parents[1])
    args = parser.parse_args()
    root = args.root.resolve()
    errors = []
    excluded = {".git", "Validation~", "Library", "Temp", "bin", "obj", "__pycache__"}
    paths = sorted(p for p in root.rglob("*") if p.is_file() and not excluded.intersection(p.relative_to(root).parts))
    if not (root / "package.json").is_file():
        parser.error("root must contain package.json")

    assemblies = {}
    json_count = 0
    for path in paths:
        if path.suffix not in {".json", ".asmdef"}:
            continue
        try:
            data = json.loads(path.read_text(encoding="utf-8-sig"), object_pairs_hook=unique_object)
            json_count += 1
            if path.suffix == ".asmdef":
                name = data["name"]
                if name in assemblies:
                    errors.append(f"Duplicate assembly {name}: {path} and {assemblies[name]}")
                assemblies[name] = path
        except (ValueError, KeyError) as error:
            errors.append(f"{path.relative_to(root)}: {error}")

    guids = {}
    for path in paths:
        if path.suffix != ".meta":
            continue
        match = re.search(r"^guid: ([0-9a-f]{32})$", path.read_text(encoding="utf-8-sig"), re.MULTILINE)
        if not match:
            errors.append(f"Missing/invalid GUID in {path.relative_to(root)}")
        elif match[1] in guids:
            errors.append(f"Duplicate GUID in {path.relative_to(root)} and {guids[match[1]]}")
        else:
            guids[match[1]] = path.relative_to(root)

    unity_sources = {".cs", ".asmdef", ".shader", ".hlsl", ".compute"}
    source_roots = {"Runtime", "Editor", "Tests", "Integrations~", "Samples~"}
    for path in paths:
        relative = path.relative_to(root)
        if relative.parts[0] in source_roots and path.suffix in unity_sources:
            if not Path(str(path) + ".meta").is_file():
                errors.append(f"Missing Unity metadata: {relative}.meta")

    shader_paths = [p for p in paths if p.suffix in {".shader", ".hlsl", ".compute"}]
    include_count = 0
    declared_kernels = set()
    declared_passes = set()
    for path in shader_paths:
        source = path.read_text(encoding="utf-8-sig")
        for include in re.findall(r'^\s*#\s*include\s+"([^"]+)"', source, re.MULTILINE):
            include_count += 1
            if include.startswith("Packages/com.cone.grass/"):
                target = root / include.removeprefix("Packages/com.cone.grass/")
            elif include.startswith("Packages/") or include in {"UnityCG.cginc", "Lighting.cginc", "AutoLight.cginc"}:
                continue  # Supplied and checked by the installed Unity packages.
            else:
                target = path.parent / include
            if not target.is_file():
                errors.append(f"Unresolved include in {path.relative_to(root)}: {include}")
        for kernel in re.findall(r"^\s*#pragma\s+kernel\s+(\w+)", source, re.MULTILINE):
            declared_kernels.add(kernel)
            if not re.search(r"\bvoid\s+" + re.escape(kernel) + r"\s*\(", source):
                errors.append(f"Missing kernel body {kernel}: {path.relative_to(root)}")
        declared_passes.update(re.findall(r'\bName\s+"([^"]+)"', source))

        # This only catches damaged source structure, not HLSL type/semantic errors.
        clean = re.sub(r'//[^\n]*|/\*[\s\S]*?\*/|"(?:\\.|[^"\\])*"', "", source)
        stack = []
        matching = {")": "(", "]": "[", "}": "{"}
        for character in clean:
            if character in "([{":
                stack.append(character)
            elif character in ")]}":
                if not stack or stack.pop() != matching[character]:
                    errors.append(f"Unbalanced shader delimiter: {path.relative_to(root)}")
                    break
        else:
            if stack:
                errors.append(f"Unclosed shader delimiter: {path.relative_to(root)}")

        conditionals = []
        for line, directive in enumerate(source.splitlines(), 1):
            match = re.match(r"\s*#\s*(if|ifdef|ifndef|else|elif|endif)\b", directive)
            if not match:
                continue
            if match[1] in {"if", "ifdef", "ifndef"}:
                conditionals.append(line)
            elif match[1] == "endif":
                if conditionals:
                    conditionals.pop()
                else:
                    errors.append(f"Unmatched #endif: {path.relative_to(root)}:{line}")
            elif not conditionals:
                errors.append(f"Unmatched #{match[1]}: {path.relative_to(root)}:{line}")
        if conditionals:
            errors.append(f"Unclosed shader conditional: {path.relative_to(root)}:{conditionals[-1]}")

    for path in paths:
        if path.suffix != ".cs" or "Tools~" in path.parts:
            continue
        source = path.read_text(encoding="utf-8-sig")
        for name in re.findall(r'\.FindKernel\("([^"]+)"\)', source):
            if name not in declared_kernels:
                errors.append(f"Unknown compute kernel {name}: {path.relative_to(root)}")
        for name in re.findall(r'\.FindPass\("([^"]+)"\)', source):
            if name not in declared_passes:
                errors.append(f"Unknown shader pass {name}: {path.relative_to(root)}")

    for error in errors:
        print(f"ERROR: {error}", file=sys.stderr)
    print(f"Validated {json_count} JSON files, {len(guids)} unique Unity GUIDs, {len(shader_paths)} shader sources and {include_count} includes; {len(errors)} errors.")
    print("Metadata/source contracts only; this does not compile Unity C# or HLSL or measure GPU performance.")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
