import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


REPORTER = Path(__file__).resolve().parents[1] / "report_unity_results.py"


class UnityResultReporterTests(unittest.TestCase):
    def run_report(self, files, require_gpu=False, result_file=None):
        with tempfile.TemporaryDirectory(prefix="grass-results-", dir=Path.cwd()) as folder:
            folder = Path(folder)
            for name, text in files.items():
                (folder / name).write_text(text, encoding="utf-8")
            environment = os.environ.copy()
            environment.pop("GITHUB_STEP_SUMMARY", None)
            command = [sys.executable, str(REPORTER), str(folder / result_file if result_file else folder)]
            if require_gpu:
                command.append("--require-gpu")
            return subprocess.run(command, capture_output=True, text=True, env=environment, check=False)

    def test_fixture_category_counts_skipped_gpu_cases(self):
        result = self.run_report({"results.xml": """
            <test-run>
              <test-suite fullname="TerrainGrassAlbedoBakerTests">
                <properties><property name="Category" value="GrassGPU" /></properties>
                <test-case fullname="TerrainGrassAlbedoBakerTests.BlendedLayers" result="Skipped">
                  <reason><message>No graphics device</message></reason>
                </test-case>
              </test-suite>
              <test-case fullname="GrassGpuGenerationTests.Dispatch" result="Passed" />
            </test-run>"""}, require_gpu=True)
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("GPU cases: 1 of 2 passed.", result.stdout)
        self.assertIn("No graphics device", result.stdout)

    def test_later_xml_cannot_overwrite_a_failure(self):
        result = self.run_report({
            "a.xml": '<test-run><test-case fullname="GrassGpuGenerationTests.Dispatch" result="Failed" /></test-run>',
            "z.xml": '<test-run><test-case fullname="GrassGpuGenerationTests.Dispatch" result="Passed" /></test-run>'
        }, require_gpu=True)
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("0 passed, 1 failed", result.stdout)
        self.assertIn("GPU cases: 0 of 1 passed.", result.stdout)

    def test_later_xml_cannot_hide_skipped_gpu_observation(self):
        result = self.run_report({
            "a.xml": '<test-run><test-case fullname="GrassGpuGenerationTests.Dispatch" result="Skipped" /></test-run>',
            "z.xml": '<test-run><test-case fullname="GrassGpuGenerationTests.Dispatch" result="Passed" /></test-run>'
        }, require_gpu=True)
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("1 skipped/inconclusive", result.stdout)

    def test_fixture_teardown_failure_is_not_lost_after_cases_pass(self):
        result = self.run_report({"results.xml": """
            <test-run result="Failed"><test-suite fullname="GrassFixture" result="Failed" site="TearDown">
              <failure><message>Cleanup failed</message></failure>
              <test-case fullname="GrassGpuGenerationTests.Dispatch" result="Passed" />
            </test-suite></test-run>"""}, require_gpu=True)
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("NUnit reported failed runs/fixtures", result.stdout)

    def test_truncated_report_is_not_ignored_next_to_a_passing_report(self):
        result = self.run_report({
            "valid.xml": '<test-run><test-case fullname="GrassGpuGenerationTests.Dispatch" result="Passed" /></test-run>',
            "truncated.xml": '<test-run><test-case fullname="GrassGpuGenerationTests.Other"'
        }, require_gpu=True)
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("Unreadable result XML", result.stdout)

    def test_headless_run_reports_gpu_gap_without_failing_optional_gpu_gate(self):
        result = self.run_report({"results.xml": """
            <test-run>
              <test-case fullname="GrassDensityAssetTests.Paint" result="Passed" />
              <test-suite><properties><property name="Category" value="GrassGPU" /></properties>
                <test-case fullname="TerrainGrassAlbedoBakerTests.Blend" result="Skipped" />
              </test-suite>
            </test-run>"""})
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertIn("GPU validation is incomplete", result.stdout)
        self.assertIn("GPU cases: 0 of 1 passed.", result.stdout)

    def test_successful_inherited_gpu_category_satisfies_required_gate(self):
        result = self.run_report({"results.xml": """
            <test-run result="Passed"><test-suite>
              <properties><property name="Category" value="GrassGPU" /></properties>
              <test-case fullname="TerrainGrassAlbedoBakerTests.Blend" result="Passed" />
            </test-suite></test-run>"""}, require_gpu=True)
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertIn("GPU cases: 1 of 1 passed.", result.stdout)

    def test_empty_results_fail(self):
        result = self.run_report({})
        self.assertEqual(result.returncode, 1)
        self.assertIn("No grass NUnit test results", result.stderr)

    def test_single_result_file_does_not_merge_sibling_results(self):
        result = self.run_report({
            "current.xml": '<test-run><test-case fullname="GrassGpuGenerationTests.CurrentRun" result="Passed" /></test-run>',
            "older.xml": '<test-run><test-case fullname="GrassGpuGenerationTests.OldRun" result="Failed" /></test-run>'
        }, require_gpu=True, result_file="current.xml")
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertIn("1 passed, 0 failed", result.stdout)
        self.assertIn("GPU cases: 1 of 1 passed.", result.stdout)


if __name__ == "__main__":
    unittest.main()
