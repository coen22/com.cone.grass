#!/usr/bin/env python3
"""Report NUnit results without treating skipped graphics tests as validation."""

import argparse
import os
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def collect_cases(element, cases, source, categories=frozenset()):
    """NUnit categories can belong to a containing fixture, not only a case."""
    own_categories = {
        prop.get("value") for prop in element.findall("./properties/property")
        if prop.get("name") == "Category"
    }
    categories = categories | own_categories
    if element.tag == "test-case":
        full_name = element.get("fullname", element.get("name", ""))
        if "Grass" in full_name:
            case = cases.setdefault(full_name, {"results": set(), "gpu": False, "sources": set(), "reasons": set()})
            case["results"].add(element.get("result", "Unknown"))
            case["gpu"] |= "GrassGpu" in full_name or "GrassGPU" in categories
            case["sources"].add(source)
            reason = element.findtext("./reason/message")
            if reason:
                case["reasons"].add(reason.strip())
        return
    for child in element:
        collect_cases(child, cases, source, categories)


def case_result(case):
    # Several result files can contain the same name (APIs, retries or exported
    # copies). A later pass must not erase a failure or incomplete observation.
    if "Failed" in case["results"]:
        return "Failed"
    return "Passed" if case["results"] == {"Passed"} else "Skipped"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("artifacts", type=Path, help="A single NUnit result file or a directory of result XML files")
    parser.add_argument("--require-gpu", action="store_true")
    args = parser.parse_args()
    cases = {}
    invalid_xml = []
    failed_runs = []
    paths = [args.artifacts] if args.artifacts.is_file() else sorted(args.artifacts.rglob("*.xml"))
    for path in paths:
        try:
            root = ET.parse(path).getroot()
        except (ET.ParseError, OSError) as error:
            invalid_xml.append(str(path) + ": " + str(error))
            continue
        collect_cases(root, cases, str(path))
        # A fixture's OneTimeTearDown can fail after all its cases passed.
        if any(test.tag in {"test-run", "test-suite"} and test.get("result") == "Failed" for test in root.iter()):
            failed_runs.append(str(path))
    if not cases:
        print("ERROR: No grass NUnit test results were produced.", file=sys.stderr)
        for error in invalid_xml:
            print("ERROR: " + error, file=sys.stderr)
        return 1
    passed = [name for name, case in cases.items() if case_result(case) == "Passed"]
    failed = [name for name, case in cases.items() if case_result(case) == "Failed"]
    skipped = [name for name, case in cases.items() if case_result(case) == "Skipped"]
    gpu = [name for name, case in cases.items() if case["gpu"]]
    gpu_passed = [name for name in gpu if name in passed]
    lines = ["## Unity test evidence", "", f"Grass tests: {len(passed)} passed, {len(failed)} failed, {len(skipped)} skipped/inconclusive.", f"GPU cases: {len(gpu_passed)} of {len(gpu)} passed."]
    if not gpu or len(gpu_passed) != len(gpu):
        lines.append("GPU validation is incomplete. Skipped tests and a headless import do not verify rendering.")
    if skipped:
        lines.extend(["", "Skipped/inconclusive cases:"] + [
            f"- {name}" + (": " + "; ".join(sorted(cases[name]["reasons"])) if cases[name]["reasons"] else "")
            for name in skipped
        ])
    if failed:
        lines.extend(["", "Failed cases:"] + [f"- {name}" for name in failed])
    if failed_runs:
        lines.extend(["", "NUnit reported failed runs/fixtures:"] + [f"- {path}" for path in failed_runs])
    if invalid_xml:
        lines.extend(["", "Unreadable result XML:"] + [f"- {error}" for error in invalid_xml])
    if any(len(case["sources"]) > 1 or len(case["results"]) > 1 for case in cases.values()):
        lines.extend(["", "Repeated test names pass only when every recorded observation passes."])
    report = "\n".join(lines) + "\n"
    print(report)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as handle:
            handle.write(report)
    return int(bool(failed or failed_runs or invalid_xml) or not passed or
               (args.require_gpu and (not gpu or len(gpu_passed) != len(gpu))))


if __name__ == "__main__":
    sys.exit(main())
