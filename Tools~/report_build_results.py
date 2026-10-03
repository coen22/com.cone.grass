#!/usr/bin/env python3
"""Verify Checked standalone build records and executable snapshots, without running the player."""

import argparse
import hashlib
import json
import math
import os
import re
import stat
import sys
from datetime import datetime, timedelta
from pathlib import Path


TARGET_EXTENSIONS = {
    "StandaloneLinux64": ".x86_64",
    "StandaloneWindows64": ".exe",
    "StandaloneOSX": ".app",
}
MAX_REPORT_BYTES = 1024 * 1024
HASH_CHUNK_BYTES = 1024 * 1024


def unsigned_integer(value, maximum, minimum=0):
    return type(value) is int and minimum <= value <= maximum


def finite_number(value):
    try:
        return type(value) in (int, float) and math.isfinite(value) and value >= 0
    except OverflowError:
        return False


def utc_time(value):
    if not isinstance(value, str) or not re.fullmatch(
        r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)", value
    ):
        return None
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
        return parsed if parsed.utcoffset() == timedelta(0) else None
    except ValueError:
        return None


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON field: " + key)
        result[key] = value
    return result


def artifact_file(directory, relative):
    """Resolve a canonical report-relative path without following artifact symlinks."""
    if not isinstance(relative, str) or not relative or "\\" in relative or ":" in relative or "\0" in relative:
        raise ValueError("artifact path must be a safe relative path with forward slashes")
    parts = relative.split("/")
    if any(part in ("", ".", "..") for part in parts):
        raise ValueError("artifact path must remain inside its report directory")
    directory = directory.resolve()
    candidate = directory
    for part in parts:
        candidate = candidate / part
        if candidate.is_symlink():
            raise ValueError("artifact path contains a symlink: " + relative)
    try:
        candidate.resolve().relative_to(directory)
    except ValueError as error:
        raise ValueError("artifact path escapes its report directory: " + relative) from error
    return candidate


def verify_executable(directory, relative, expected_bytes, expected_sha256):
    """Hash a regular file in bounded chunks, stopping at the recorded length."""
    path = artifact_file(directory, relative)
    info = path.stat()
    if not stat.S_ISREG(info.st_mode):
        raise ValueError("executable evidence is not a regular file: " + relative)
    if info.st_size != expected_bytes:
        raise ValueError(f"executable length mismatch for {relative}: expected {expected_bytes}, found {info.st_size}")
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        opened = os.fstat(handle.fileno())
        if not stat.S_ISREG(opened.st_mode) or opened.st_size != expected_bytes:
            raise ValueError("executable changed while opening: " + relative)
        remaining = expected_bytes
        while remaining:
            chunk = handle.read(min(HASH_CHUNK_BYTES, remaining))
            if not chunk:
                raise ValueError("executable became shorter while hashing: " + relative)
            digest.update(chunk)
            remaining -= len(chunk)
        if handle.read(1) or os.fstat(handle.fileno()).st_size != expected_bytes:
            raise ValueError("executable length changed while hashing: " + relative)
    if digest.hexdigest() != expected_sha256:
        raise ValueError("executable SHA-256 mismatch for " + relative)


def check_report(path):
    """Return errors and verified observations for one Builds/build-results.json."""
    errors = []
    observations = []
    try:
        absolute = path.absolute()
        if any(candidate.is_symlink() for candidate in (absolute, *absolute.parents)):
            raise ValueError("build report path contains a symlink")
        if not stat.S_ISREG(path.stat().st_mode):
            raise ValueError("build report must be a regular file")
        with path.open("rb") as handle:
            raw = handle.read(MAX_REPORT_BYTES + 1)
        if len(raw) > MAX_REPORT_BYTES:
            raise ValueError("build report exceeds the size limit")
        record = json.loads(raw.decode("utf-8"), object_pairs_hook=unique_object)
    except (OSError, UnicodeError, ValueError, RecursionError) as error:
        return ["cannot read build report: " + str(error)], observations
    if not isinstance(record, dict):
        return ["build report must be a JSON object"], observations

    def require(condition, message):
        if not condition:
            errors.append(message)

    require(type(record.get("schemaVersion")) is int and record["schemaVersion"] == 1,
            "schemaVersion must be 1")
    attempt = record.get("attemptId")
    valid_attempt = isinstance(attempt, str) and re.fullmatch(r"[0-9a-f]{32}", attempt) is not None
    require(valid_attempt, "attemptId must be 32 lowercase hexadecimal characters")
    require(record.get("status") == "passed", "build attempt must have terminal status passed")
    require(record.get("succeeded") is True, "build attempt succeeded must be true")
    require(record.get("failure") == "", "passed build attempt must have an empty failure string")
    for field in ("unityVersion", "previousManagedCodeVariant"):
        value = record.get(field)
        require(isinstance(value, str) and bool(value.strip()), field + " must be recorded")
    require(record.get("managedCodeVariant") == "Checked", "build attempt managedCodeVariant must be Checked")
    require(record.get("managedCodeVariantRestored") is True, "previous managed code variant must be restored")
    require(record.get("renderGraphValidityChecks") is True, "RenderGraph validity checks must be enabled")
    started = utc_time(record.get("startedUtc"))
    completed = utc_time(record.get("completedUtc"))
    require(started is not None, "startedUtc must be an ISO UTC timestamp")
    require(completed is not None, "completedUtc must be an ISO UTC timestamp")
    if started is not None and completed is not None:
        require(completed >= started, "completedUtc must not precede startedUtc")
    target = record.get("target")
    valid_target = isinstance(target, str) and target in TARGET_EXTENSIONS
    require(valid_target, "target must be a supported standalone target")
    output = target + "/GrassValidation" + TARGET_EXTENSIONS[target] if valid_target else None
    executable = output + "/Contents/MacOS/GrassValidation" if target == "StandaloneOSX" else output

    builds = record.get("builds")
    if not isinstance(builds, list) or len(builds) != 2:
        errors.append("exactly two ordered builds are required: clean, incremental")
        return errors, observations
    for index, kind in enumerate(("clean", "incremental")):
        build = builds[index]
        prefix = kind + ": "
        if not isinstance(build, dict):
            errors.append(prefix + "build must be a JSON object")
            continue
        require(build.get("kind") == kind, prefix + "build kind is missing, duplicated or out of order")
        require(build.get("result") == "Succeeded", prefix + "result must be Succeeded")
        require(valid_target and build.get("target") == target, prefix + "target must match the build attempt")
        require(build.get("managedCodeVariant") == "Checked", prefix + "managedCodeVariant must be Checked")
        require(build.get("development") is True, prefix + "actual build must be Development")
        require(build.get("cleanBuildCache") is (index == 0), prefix + "actual CleanBuildCache flag does not match build kind")
        count = build.get("errors")
        require(unsigned_integer(count, 2**31 - 1) and count == 0, prefix + "errors must be zero")
        require(unsigned_integer(build.get("warnings"), 2**31 - 1), prefix + "warnings must be a nonnegative signed 32-bit integer")
        require(finite_number(build.get("seconds")), prefix + "seconds must be finite and nonnegative")
        valid_bytes = unsigned_integer(build.get("executableBytes"), 2**63 - 1, minimum=1)
        require(valid_bytes, prefix + "executableBytes must be a positive signed 64-bit integer")
        checksum = build.get("executableSha256")
        valid_checksum = isinstance(checksum, str) and re.fullmatch(r"[0-9a-f]{64}", checksum) is not None
        require(valid_checksum, prefix + "executableSha256 must be 64 lowercase hexadecimal characters")
        require(output is not None and build.get("output") == output, prefix + "output must be the canonical target output")
        require(executable is not None and build.get("executable") == executable,
                prefix + "executable must be the canonical target executable")
        evidence = f"Evidence/{attempt}/{kind}/{executable.rsplit('/', 1)[-1]}" if valid_attempt and executable else None
        valid_evidence = evidence is not None and build.get("evidence") == evidence
        require(valid_evidence, prefix + "evidence must name this attempt's build snapshot")
        if valid_evidence and valid_bytes and valid_checksum:
            try:
                verify_executable(path.parent, evidence, build["executableBytes"], checksum)
                observations.append(f"{kind}: executable snapshot length and SHA-256 verified ({build['executableBytes']} bytes).")
            except (OSError, ValueError) as error:
                errors.append(prefix + str(error))
        if index == 1 and executable is not None and build.get("executable") == executable and valid_bytes and valid_checksum:
            try:
                verify_executable(path.parent, executable, build["executableBytes"], checksum)
                observations.append("Current player executable matches the incremental build's recorded length and SHA-256.")
            except (OSError, ValueError) as error:
                errors.append("current player: " + str(error))
    return errors, observations


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("artifacts", type=Path, help="Directory containing build-results.json reports, or one report file")
    args = parser.parse_args()
    paths = [args.artifacts] if args.artifacts.is_file() else sorted(args.artifacts.rglob("build-results.json"))
    lines = ["## Standalone build evidence", "",
             "Checks recorded clean and incremental Checked builds and executable snapshots. "
             "This does not execute the player or verify GPU rendering."]
    failed = not paths
    if not paths:
        lines.extend(["", "ERROR: No build-results.json reports were produced."])
    for path in paths:
        errors, observations = check_report(path)
        failed |= bool(errors)
        lines.extend(["", f"### {'FAIL' if errors else 'PASS'}: {path}", ""])
        lines.extend("- " + observation for observation in observations)
        lines.extend("- ERROR: " + error for error in errors)
    if len(paths) > 1:
        lines.extend(["", "Every build report must pass; a later successful attempt does not erase a failed or incomplete report."])
    report = "\n".join(lines) + "\n"
    print(report)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as handle:
            handle.write(report)
    return int(failed)


if __name__ == "__main__":
    sys.exit(main())
