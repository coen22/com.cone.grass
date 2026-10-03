import contextlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock


PROBE_PATH = Path(__file__).resolve().parents[1] / "probe_unity_cli.py"
SPEC = importlib.util.spec_from_file_location("grass_cli_probe", PROBE_PATH)
PROBE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PROBE)


class UnityCliProbeTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="grass-cli-probe-", dir=Path.cwd())
        self.addCleanup(temporary.cleanup)
        self.folder = Path(temporary.name)
        self.editor = self.folder / "Unity"
        self.editor.write_text("Synthetic executable; subprocess is mocked.\n")
        self.project = self.folder / "Project"
        self.project.mkdir()
        (self.project / ".grass-validation-project").write_text("Test fixture\n")
        self.output = self.folder / "Results"
        self.output.mkdir()

    def run_probe(self, launch):
        arguments = [str(PROBE_PATH), "--editor", str(self.editor), "--project", str(self.project),
                     "--output", str(self.output)]
        with mock.patch.object(sys, "argv", arguments), mock.patch.object(PROBE.subprocess, "run", side_effect=launch), \
                mock.patch.dict(PROBE.os.environ, {"GITHUB_STEP_SUMMARY": ""}), contextlib.redirect_stdout(io.StringIO()):
            code = PROBE.main()
        return code, json.loads((self.output / "cli-attempt.json").read_text())

    def test_actual_license_rejection_is_unavailable_without_test_results(self):
        def launch(command, **kwargs):
            self.assertIn("-runTests", command)
            self.assertNotIn("-quit", command)
            (self.output / "editor.log").write_text("No valid Unity Editor license found.\n")
            return subprocess.CompletedProcess(command, 198)
        code, report = self.run_probe(launch)
        self.assertEqual(code, 0)
        self.assertEqual(report["status"], "blocked_by_license")
        self.assertFalse(report["tests_executed"])
        self.assertEqual(report["editor_exit_code"], 198)

    def test_stale_license_log_and_xml_cannot_hide_new_startup_failure(self):
        (self.output / "editor.log").write_text("No valid Unity Editor license found.\n")
        (self.output / "editmode.xml").write_text("Old results must be removed.\n")
        def launch(command, **kwargs):
            self.assertFalse((self.output / "editor.log").exists())
            self.assertFalse((self.output / "editmode.xml").exists())
            kwargs["stdout"].write("error while loading shared libraries\n")
            return subprocess.CompletedProcess(command, 127)
        code, report = self.run_probe(launch)
        self.assertEqual(code, 1)
        self.assertEqual(report["status"], "failed")
        self.assertFalse(report["tests_executed"])

    def test_zero_exit_without_xml_does_not_count_as_a_test_pass(self):
        code, report = self.run_probe(lambda command, **kwargs: subprocess.CompletedProcess(command, 0))
        self.assertEqual(code, 1)
        self.assertEqual(report["status"], "failed")
        self.assertFalse(report["tests_executed"])

    def test_new_attempt_replaces_previous_success_before_launch(self):
        result_path = self.output / "cli-attempt.json"
        result_path.write_text(json.dumps({"status": "headless_tests_passed", "tests_executed": True}))
        def launch(command, **kwargs):
            running = json.loads(result_path.read_text())
            self.assertEqual(running["status"], "running")
            self.assertFalse(running["tests_executed"])
            self.assertEqual(running["command"], command)
            return subprocess.CompletedProcess(command, 127)
        code, report = self.run_probe(launch)
        self.assertEqual(code, 1)
        self.assertEqual(report["status"], "failed")

    def test_old_sibling_result_cannot_supply_a_missing_current_grass_pass(self):
        (self.output / "older-export.xml").write_text(
            '<test-run><test-case fullname="GrassDensityAssetTests.OldRun" result="Passed" /></test-run>')
        real_run = subprocess.run
        def launch(command, **kwargs):
            if command[0] == str(self.editor):
                (self.output / "editmode.xml").write_text(
                    '<test-run><test-case fullname="UnrelatedTests.CurrentRun" result="Passed" /></test-run>')
                return subprocess.CompletedProcess(command, 0)
            return real_run(command, **kwargs)
        code, report = self.run_probe(launch)
        self.assertEqual(code, 1)
        self.assertEqual(report["status"], "failed")
        self.assertEqual(report["result_reporter_exit_code"], 1)
        self.assertIn("No grass NUnit test results", (self.output / "test-scope.log").read_text())

    def test_current_passing_result_is_not_combined_with_an_old_failure(self):
        (self.output / "older-export.xml").write_text(
            '<test-run><test-case fullname="GrassDensityAssetTests.OldRun" result="Failed" /></test-run>')
        real_run = subprocess.run
        def launch(command, **kwargs):
            if command[0] == str(self.editor):
                (self.output / "editmode.xml").write_text(
                    '<test-run><test-case fullname="GrassDensityAssetTests.CurrentRun" result="Passed" /></test-run>')
                return subprocess.CompletedProcess(command, 0)
            return real_run(command, **kwargs)
        code, report = self.run_probe(launch)
        self.assertEqual(code, 0)
        self.assertEqual(report["status"], "headless_tests_passed")
        self.assertEqual(report["result_reporter_exit_code"], 0)

    def test_timeout_is_a_failure_with_a_persisted_outcome(self):
        def launch(command, **kwargs):
            raise subprocess.TimeoutExpired(command, kwargs["timeout"])
        code, report = self.run_probe(launch)
        self.assertEqual(code, 1)
        self.assertEqual(report["status"], "timed_out")
        self.assertFalse(report["tests_executed"])


if __name__ == "__main__":
    unittest.main()
