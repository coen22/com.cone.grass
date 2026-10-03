#!/usr/bin/env python3
"""Report NUnit results without treating skipped graphics tests as validation."""

import argparse
import os
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("artifacts", type=Path)
    parser.add_argument("--require-gpu", action="store_true")
    args = parser.parse_args()
    cases = {}
    for path in sorted(args.artifacts.rglob("*.xml")):
        try:
            root = ET.parse(path).getroot()
        except ET.ParseError:
            continue
        for test in root.iter("test-case"):
            full_name = test.get("fullname", test.get("name", ""))
            if "Grass" in full_name:
                cases[full_name] = test
    if not cases:
        print("ERROR: No grass NUnit test results were produced.", file=sys.stderr)
        return 1
    passed = [name for name, test in cases.items() if test.get("result") == "Passed"]
    failed = [name for name, test in cases.items() if test.get("result") == "Failed"]
    skipped = [name for name, test in cases.items() if test.get("result") not in {"Passed", "Failed"}]
    gpu = [name for name in cases if "GrassGpu" in name or any(prop.get("value") == "GrassGPU" for prop in cases[name].iter("property"))]
    gpu_passed = [name for name in gpu if name in passed]
    lines = ["## Unity test evidence", "", f"Grass tests: {len(passed)} passed, {len(failed)} failed, {len(skipped)} skipped/inconclusive.", f"GPU cases: {len(gpu_passed)} of {len(gpu)} passed."]
    if not gpu or len(gpu_passed) != len(gpu):
        lines.append("GPU validation is incomplete. Skipped tests and a headless import do not verify rendering.")
    if skipped:
        lines.extend(["", "Skipped/inconclusive cases:"] + [f"- {name}" for name in skipped])
    if failed:
        lines.extend(["", "Failed cases:"] + [f"- {name}" for name in failed])
    report = "\n".join(lines) + "\n"
    print(report)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as handle:
            handle.write(report)
    return int(bool(failed) or not passed or (args.require_gpu and (not gpu or len(gpu_passed) != len(gpu))))


if __name__ == "__main__":
    sys.exit(main())
