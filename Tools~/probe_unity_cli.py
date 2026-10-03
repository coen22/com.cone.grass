#!/usr/bin/env python3
"""Attempt the real Unity CLI and report test execution separately from licensing."""

import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--editor", type=Path, required=True)
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    args = parser.parse_args()
    editor, project, output = args.editor.resolve(), args.project.resolve(), args.output.resolve()
    if not editor.is_file() or not (project / ".grass-validation-project").is_file():
        parser.error("Use an installed Editor and the generated disposable validation project.")
    if args.timeout < 1:
        parser.error("--timeout must be positive.")
    output.mkdir(parents=True, exist_ok=True)
    results = output / "editmode.xml"
    # A previous successful XML must never turn a failed new attempt into a pass.
    if results.exists():
        results.unlink()
    command = [str(editor), "-batchmode", "-nographics", "-projectPath", str(project),
               "-runTests", "-testPlatform", "EditMode", "-testResults", str(results),
               "-logFile", str(output / "editor.log")]
    record = {"unity": "6000.6.0f1", "status": "failed", "tests_executed": False,
              "graphics": "Null device requested; GPU results are not inferred",
              "command": command}
    started = time.monotonic()
    try:
        with (output / "launcher.log").open("w") as log:
            process = subprocess.run(command, cwd=project, stdout=log, stderr=subprocess.STDOUT,
                                     timeout=args.timeout, check=False)
        record["editor_exit_code"] = process.returncode
        editor_log = output / "editor.log"
        log = editor_log.read_text(errors="replace") if editor_log.exists() else ""
        launcher = (output / "launcher.log").read_text(errors="replace")
        combined = log + "\n" + launcher
        if results.is_file():
            reporter = Path(__file__).with_name("report_unity_results.py")
            with (output / "test-scope.log").open("w") as report:
                check = subprocess.run([sys.executable, str(reporter), str(output)],
                                       stdout=report, stderr=subprocess.STDOUT, check=False)
            record["tests_executed"] = True
            record["result_reporter_exit_code"] = check.returncode
            if process.returncode == 0 and check.returncode == 0:
                record["status"] = "headless_tests_passed"
        elif process.returncode != 0 and any(message in combined for message in (
                "No valid Unity Editor license found",
                "Unity has not been activated with a valid license",
                "No valid license found")):
            record["status"] = "blocked_by_license"
            record["reason"] = "The Editor rejected startup without a valid license; no tests ran."
    except subprocess.TimeoutExpired:
        record["status"] = "timed_out"
        record["reason"] = "The Editor did not complete within the configured deadline."
    except OSError as error:
        record["reason"] = str(error)
    finally:
        record["seconds"] = round(time.monotonic() - started, 3)
        (output / "cli-attempt.json").write_text(json.dumps(record, indent=2) + "\n")
    summary = ("## Actual Unity CLI attempt\n\n"
               + "**Outcome: " + record["status"] + "**\n\n"
               + "Editor exit code: " + str(record.get("editor_exit_code", "unavailable")) + ". "
               + "Test results produced: " + str(record["tests_executed"]) + ".\n\n"
               + "This is an availability probe with no license credentials supplied. "
               + "An explicit license block is reported as unavailable, not a test pass. "
               + "The licensed validation workflow remains the acceptance gate.\n")
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a") as destination:
            destination.write(summary)
    print(summary)
    print(json.dumps(record, indent=2))
    return 0 if record["status"] in ("headless_tests_passed", "blocked_by_license") else 1


if __name__ == "__main__":
    raise SystemExit(main())
