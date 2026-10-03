import copy
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "report_build_results.py"
SPEC = importlib.util.spec_from_file_location("report_build_results", SCRIPT)
REPORTER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(REPORTER)


class BuildResultTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="grass-build-results-", dir=Path.cwd())
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        self.path, self.record = self.make_report(self.directory / "Builds")

    def make_report(self, directory, target="StandaloneLinux64", clean=b"synthetic clean player", incremental=b"synthetic incremental player"):
        # These bytes are test fixtures for evidence integrity, never a claim
        # that Unity compiled or executed a player in the Python test runner.
        extensions = {"StandaloneLinux64": ".x86_64", "StandaloneWindows64": ".exe", "StandaloneOSX": ".app"}
        output = target + "/GrassValidation" + extensions[target]
        executable = output + "/Contents/MacOS/GrassValidation" if target == "StandaloneOSX" else output
        attempt = "a" * 32
        record = {
            "schemaVersion": 1, "attemptId": attempt, "status": "passed", "succeeded": True, "failure": "",
            "startedUtc": "2026-10-03T10:00:00.1234567Z", "completedUtc": "2026-10-03T10:02:00.1234567Z",
            "unityVersion": "6000.6.0f1", "target": target, "managedCodeVariant": "Checked",
            "previousManagedCodeVariant": "Release", "managedCodeVariantRestored": True,
            "renderGraphValidityChecks": True, "builds": [],
        }
        for kind, payload in (("clean", clean), ("incremental", incremental)):
            evidence = f"Evidence/{attempt}/{kind}/{executable.rsplit('/', 1)[-1]}"
            snapshot = directory / evidence
            snapshot.parent.mkdir(parents=True, exist_ok=True)
            snapshot.write_bytes(payload)
            record["builds"].append({
                "kind": kind, "result": "Succeeded", "target": target, "output": output,
                "executable": executable, "evidence": evidence, "managedCodeVariant": "Checked",
                "development": True, "cleanBuildCache": kind == "clean", "errors": 0, "warnings": 0,
                "seconds": 32.5, "executableBytes": len(payload), "executableSha256": hashlib.sha256(payload).hexdigest(),
            })
        final = directory / executable
        final.parent.mkdir(parents=True, exist_ok=True)
        final.write_bytes(incremental)
        path = directory / "build-results.json"
        path.write_text(json.dumps(record), encoding="utf-8")
        return path, record

    def check(self, record=None, path=None):
        path = path or self.path
        path.write_text(json.dumps(self.record if record is None else record), encoding="utf-8")
        return REPORTER.check_report(path)

    def invalid(self, record=None, message=None):
        errors, _ = self.check(record)
        self.assertTrue(errors)
        if message:
            self.assertIn(message, "\n".join(errors))

    def cli(self, path):
        environment = os.environ.copy()
        environment.pop("GITHUB_STEP_SUMMARY", None)
        return subprocess.run([sys.executable, str(SCRIPT), str(path)], capture_output=True, text=True,
                              env=environment, timeout=10)

    def test_valid_target_paths_and_both_distinct_snapshots(self):
        for target in ("StandaloneLinux64", "StandaloneWindows64", "StandaloneOSX"):
            with self.subTest(target=target):
                path, _ = self.make_report(self.directory / target, target)
                errors, observations = REPORTER.check_report(path)
                self.assertEqual(errors, [])
                self.assertEqual(len(observations), 3)
        result = self.cli(self.directory)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("does not execute the player or verify GPU rendering", result.stdout)

    def test_identical_clean_and_incremental_executables_are_valid(self):
        path, _ = self.make_report(self.directory / "identical", clean=b"same player", incremental=b"same player")
        self.assertEqual(REPORTER.check_report(path)[0], [])

    def test_streamed_hash_accepts_executable_larger_than_one_chunk(self):
        payload = b"synthetic data" * (REPORTER.HASH_CHUNK_BYTES // 14 + 2)
        self.assertGreater(len(payload), REPORTER.HASH_CHUNK_BYTES)
        path, _ = self.make_report(self.directory / "large", clean=payload, incremental=payload + b"incremental")
        self.assertEqual(REPORTER.check_report(path)[0], [])

    def test_running_failed_and_contradictory_success_are_rejected(self):
        for field, value in (("status", "running"), ("status", "failed"), ("succeeded", False),
                             ("succeeded", 1), ("failure", "build threw after returning a report"), ("failure", None)):
            with self.subTest(field=field, value=value):
                record = copy.deepcopy(self.record)
                record[field] = value
                self.invalid(record)

    def test_setup_failure_and_interrupted_build_cannot_reuse_success(self):
        record = copy.deepcopy(self.record)
        record.update(status="failed", succeeded=False, failure="scene setup failed", builds=[])
        self.invalid(record, "exactly two ordered builds")
        record.update(status="running", completedUtc=None, failure="")
        record["builds"] = [{"kind": "clean", "result": "Running"}]
        self.invalid(record, "terminal status passed")

    def test_exactly_two_ordered_builds_are_required(self):
        variants = ([], self.record["builds"][:1], self.record["builds"] * 2,
                    list(reversed(self.record["builds"])), [self.record["builds"][0]] * 2, [None, None])
        for builds in variants:
            with self.subTest(builds=builds):
                record = copy.deepcopy(self.record)
                record["builds"] = builds
                self.invalid(record)

    def test_diagnostic_configuration_must_be_checked_enabled_and_restored(self):
        for field, value in (("managedCodeVariant", "Release"), ("managedCodeVariantRestored", False),
                             ("managedCodeVariantRestored", 1), ("renderGraphValidityChecks", False),
                             ("renderGraphValidityChecks", 1), ("previousManagedCodeVariant", ""), ("unityVersion", None)):
            with self.subTest(field=field, value=value):
                record = copy.deepcopy(self.record)
                record[field] = value
                self.invalid(record)
        for index in range(2):
            record = copy.deepcopy(self.record)
            record["builds"][index]["managedCodeVariant"] = "Debug"
            self.invalid(record, "managedCodeVariant must be Checked")

    def test_actual_build_result_target_and_flags_must_match(self):
        for index in range(2):
            for field, value in (("result", "Failed"), ("result", "Running"), ("errors", 1),
                                 ("target", "StandaloneOSX"), ("development", False), ("development", 1),
                                 ("cleanBuildCache", index != 0), ("cleanBuildCache", int(index == 0))):
                with self.subTest(index=index, field=field, value=value):
                    record = copy.deepcopy(self.record)
                    record["builds"][index][field] = value
                    self.invalid(record)

    def test_warnings_do_not_turn_a_successful_build_into_a_failure(self):
        self.record["builds"][0]["warnings"] = 7
        self.record["builds"][1]["warnings"] = 2**31 - 1
        self.assertEqual(self.check()[0], [])

    def test_invalid_numeric_metadata_is_rejected_without_crashing(self):
        for field, values in {
            "errors": (True, False, -1, 0.0), "warnings": (True, -1, 2**31, "0"),
            "seconds": (True, -1, float("nan"), float("inf"), 10**400, "12"),
            "executableBytes": (True, 0, -1, 2**63, 21.0),
        }.items():
            for value in values:
                with self.subTest(field=field, value=value):
                    record = copy.deepcopy(self.record)
                    record["builds"][0][field] = value
                    self.invalid(record)

    def test_schema_attempt_target_and_checksum_are_strict(self):
        for field, values in {"schemaVersion": (True, "1", 2), "attemptId": ("A" * 32, "a" * 31, None),
                              "target": ("Android", None, [])}.items():
            for value in values:
                with self.subTest(field=field, value=value):
                    record = copy.deepcopy(self.record)
                    record[field] = value
                    self.invalid(record)
        for value in ("a" * 63, "A" * 64, "z" * 64, None):
            record = copy.deepcopy(self.record)
            record["builds"][0]["executableSha256"] = value
            self.invalid(record, "executableSha256")

    def test_utc_timestamps_must_be_recorded_and_chronological(self):
        for field, value in (("startedUtc", None), ("completedUtc", ""),
                             ("completedUtc", "2026-10-03T10:02:00"),
                             ("completedUtc", "2026-10-03T10:02:00+01:00"),
                             ("completedUtc", "2026-02-31T10:02:00Z"),
                             ("completedUtc", "2026-10-03T09:00:00Z")):
            with self.subTest(field=field, value=value):
                record = copy.deepcopy(self.record)
                record[field] = value
                self.invalid(record)

    def test_each_snapshot_must_exist(self):
        for index in range(2):
            path, record = self.make_report(self.directory / f"missing-{index}")
            (path.parent / record["builds"][index]["evidence"]).unlink()
            errors, _ = REPORTER.check_report(path)
            self.assertTrue(errors)
            self.assertIn(record["builds"][index]["kind"], "\n".join(errors))

    def test_truncated_appended_and_same_size_edited_snapshots_fail(self):
        for index in range(2):
            for edit in ("truncate", "append", "same-size"):
                with self.subTest(index=index, edit=edit):
                    path, record = self.make_report(self.directory / f"edited-{index}-{edit}")
                    snapshot = path.parent / record["builds"][index]["evidence"]
                    original = snapshot.read_bytes()
                    changed = original[:-1] if edit == "truncate" else original + b"!" if edit == "append" else b"!" + original[1:]
                    snapshot.write_bytes(changed)
                    errors, _ = REPORTER.check_report(path)
                    self.assertIn("SHA-256 mismatch" if edit == "same-size" else "length mismatch", "\n".join(errors))

    def test_final_player_must_match_incremental_even_when_both_snapshots_match(self):
        final = self.path.parent / self.record["builds"][1]["executable"]
        incremental = final.read_bytes()
        final.write_bytes(b"!" + incremental[1:])
        self.invalid(message="current player: executable SHA-256 mismatch")
        final.write_bytes((self.path.parent / self.record["builds"][0]["evidence"]).read_bytes())
        self.invalid(message="current player: executable length mismatch")
        final.unlink()
        self.invalid(message="current player:")

    def test_old_attempt_snapshot_cannot_satisfy_current_evidence(self):
        for index in range(2):
            record = copy.deepcopy(self.record)
            build = record["builds"][index]
            current = self.path.parent / build["evidence"]
            build["evidence"] = build["evidence"].replace("a" * 32, "b" * 32)
            old = self.path.parent / build["evidence"]
            old.parent.mkdir(parents=True, exist_ok=True)
            old.write_bytes(current.read_bytes())
            self.invalid(record, "evidence must name this attempt's build snapshot")

    def test_output_executable_and_evidence_require_exact_safe_paths(self):
        for field in ("output", "executable", "evidence"):
            for value in ("../outside-player", "/tmp/outside-player", r"C:\outside-player", "other/player", "Evidence//player"):
                with self.subTest(field=field, value=value):
                    record = copy.deepcopy(self.record)
                    record["builds"][0][field] = value
                    self.invalid(record)
        self.record["builds"][1]["evidence"] = self.record["builds"][0]["evidence"]
        self.invalid(message="evidence must name this attempt's build snapshot")

    def symlink(self, link, target, directory=False):
        try:
            link.symlink_to(target, target_is_directory=directory)
        except OSError as error:
            self.skipTest("symlinks unavailable: " + str(error))

    def test_snapshot_file_symlink_is_rejected_even_with_matching_content(self):
        snapshot = self.path.parent / self.record["builds"][0]["evidence"]
        target = self.directory / "elsewhere-player"
        snapshot.rename(target)
        self.symlink(snapshot, target)
        self.invalid(message="artifact path contains a symlink")

    def test_snapshot_directory_symlink_is_rejected(self):
        original = self.path.parent / "Evidence"
        moved = self.directory / "elsewhere-evidence"
        original.rename(moved)
        self.symlink(original, moved, directory=True)
        self.invalid(message="artifact path contains a symlink")

    def test_final_executable_symlink_is_rejected(self):
        final = self.path.parent / self.record["builds"][1]["executable"]
        target = self.path.parent / self.record["builds"][1]["evidence"]
        final.unlink()
        self.symlink(final, target)
        self.invalid(message="current player: artifact path contains a symlink")

    def test_final_app_directory_symlink_is_rejected(self):
        path, record = self.make_report(self.directory / "mac-symlink", "StandaloneOSX")
        original = path.parent / record["builds"][1]["output"]
        moved = self.directory / "elsewhere.app"
        original.rename(moved)
        self.symlink(original, moved, directory=True)
        self.assertIn("current player: artifact path contains a symlink", "\n".join(REPORTER.check_report(path)[0]))

    def test_malformed_empty_or_duplicate_json_is_rejected(self):
        for value in ("", "{", "[]", "null", '{"status":"failed",' + json.dumps(self.record)[1:]):
            with self.subTest(value=value[:60]):
                self.path.write_text(value, encoding="utf-8")
                self.assertTrue(REPORTER.check_report(self.path)[0])
                self.assertEqual(self.cli(self.path).returncode, 1)

    def test_report_parent_symlink_is_rejected(self):
        original = self.path.parent
        moved = self.directory / "elsewhere-builds"
        original.rename(moved)
        self.symlink(original, moved, directory=True)
        errors, _ = REPORTER.check_report(self.path)
        self.assertIn("build report path contains a symlink", "\n".join(errors))
        self.assertEqual(self.cli(self.path).returncode, 1)

    @unittest.skipUnless(hasattr(os, "mkfifo"), "named pipes are unavailable")
    def test_nonregular_report_is_rejected_without_waiting_for_input(self):
        self.path.unlink()
        os.mkfifo(self.path)
        result = self.cli(self.directory)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("build report must be a regular file", result.stdout)

    def test_no_report_fails(self):
        empty = self.directory / "empty"
        empty.mkdir()
        result = self.cli(empty)
        self.assertEqual(result.returncode, 1)
        self.assertIn("No build-results.json", result.stdout)

    def test_later_pass_does_not_erase_failed_incomplete_or_malformed_report(self):
        for state in ("failed", "running", "malformed"):
            with self.subTest(state=state):
                directory = self.directory / state
                old_path, old = self.make_report(directory / "old")
                self.make_report(directory / "latest")
                if state == "malformed":
                    old_path.write_text("{", encoding="utf-8")
                else:
                    old.update(status=state, succeeded=False, failure="earlier attempt did not complete")
                    old_path.write_text(json.dumps(old), encoding="utf-8")
                result = self.cli(directory)
                self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
                self.assertIn("Every build report must pass", result.stdout)


if __name__ == "__main__":
    unittest.main()
