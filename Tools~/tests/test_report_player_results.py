import copy
import importlib.util
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest
import zlib


REPORTER = Path(__file__).resolve().parents[1] / "report_player_results.py"
SPEC = importlib.util.spec_from_file_location("report_player_results", REPORTER)
reporter = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(reporter)


def chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))


def png(width=320, height=240, grass=False, compressed=None, green=64, red=0):
    rows = bytearray()
    for y in range(height):
        row = bytearray(b"\x00\x00\x00\xff" * width)
        if grass and 112 <= y < 120:
            for x in range(150, min(width, 158)):
                row[x * 4 + 1] = green
                row[x * 4] = red
        rows.extend(b"\x00" + row)
    return (reporter.PNG_SIGNATURE +
            chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)) +
            chunk(b"IDAT", zlib.compress(rows) if compressed is None else compressed) +
            chunk(b"IEND", b""))


class PlayerEvidenceReporterTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="grass-player-results-", dir=Path.cwd())
        self.addCleanup(self.temporary.cleanup)
        self.folder = Path(self.temporary.name)
        self.path = self.folder / "grass-smoke-results.json"
        self.report = {
            "schemaVersion": 3, "passed": True, "status": "passed", "attemptId": "1" * 32,
            "failure": "", "unityVersion": "6000.6.0f1", "checksEnabled": True, "instrumentationEnabled": True,
            "renderGraphValidityChecks": True,
            "startedUtc": "2026-10-03T16:00:00Z", "completedUtc": "2026-10-03T16:01:00Z",
            "graphicsApi": "Vulkan", "graphicsDevice": "Synthetic reporter test fixture",
            "supportsCompute": True, "supportsIndirectArguments": True, "supportsAsyncReadback": True,
            "errorCount": 0, "changedGrassPixels": 64, "comparedPixels": 13440,
            "changedWindPixels": 64, "darkenedContactPixels": 64, "brightenedContactPixels": 0,
            "changedBlackMaskPixels": 0, "changedNoSourcesPixels": 0,
            "windTextureWidth": 16, "windTextureHeight": 16, "windStrength": 0.15, "windScrollSpeed": 0.1,
            "windTextureVaries": True,
            "optionalMotionRequested": False, "unsupportedMsaaStages": 0, "msaaCoverageComplete": True,
            "stages": []
        }
        for index, (name, (count, samples, scale, contacts)) in enumerate(reporter.BASELINE.items()):
            self.report["stages"].append({
                "name": name, "status": "passed", "passed": True, "optional": False,
                "countExpectation": count, "requestedMsaa": samples, "requestedRenderScale": scale,
                "contactsRequested": contacts, "postProcessingRequested": False,
                "motionModeRequested": "Off", "cameraTargetsScreen": True,
                "cameraPanRequested": name == "04-camera-pan-no-aa", "maximumCameraDisplacement": 0.2,
                "windStrength": 0 if name in ("05-contacts-off-no-aa", "06-contacts-on-no-aa") else 0.15,
                "cameraPosition": {"x": 0, "y": 4, "z": -10}, "cameraRotation": {"x": 0, "y": 0, "z": 0, "w": 1},
                "visibleGrass": 0 if count != "Positive" else 3000, "overflowGrass": 0,
                "cameraFrames": 6, "activationFrame": index * 10, "capturedFrame": index * 10 + 6,
                "imageWidth": 320, "imageHeight": 240, "screenshot": name + ".png",
                "attachments": {
                    "frame": index * 10 + 6, "requestedMsaa": samples, "hardwareSupportedMsaa": samples,
                    "cameraDescriptorSamples": samples, "colorSamples": samples, "depthSamples": samples,
                    "colorWidth": int(320 * scale), "depthWidth": int(320 * scale),
                    "colorHeight": int(240 * scale), "depthHeight": int(240 * scale),
                    "colorViewportWidth": int(320 * scale), "depthViewportWidth": int(320 * scale),
                    "colorViewportHeight": int(240 * scale), "depthViewportHeight": int(240 * scale),
                    "cameraWidth": 320, "cameraHeight": 240, "cameraRenderScale": scale,
                    "cameraScaledWidth": int(320 * scale), "cameraScaledHeight": int(240 * scale),
                    "cameraDescriptorWidth": int(320 * scale), "cameraDescriptorHeight": int(240 * scale),
                    "colorFormat": "R8G8B8A8_UNorm", "depthFormat": "D32_SFloat",
                    "colorEvidence": "allocated RenderTexture", "depthEvidence": "allocated RenderTexture",
                    "backbuffer": False, "antialiasing": "None"
                }
            })
        self.capture = png()
        for stage in self.report["stages"]:
            pixels = self.capture
            if stage["name"] in ("02-grass-on", "05-contacts-off-no-aa"):
                pixels = png(grass=True)
            elif stage["name"] == "03-wind-no-aa":
                pixels = png(grass=True, green=96)
            elif stage["name"] == "06-contacts-on-no-aa":
                pixels = png(grass=True, green=48)
            (self.folder / stage["screenshot"]).write_bytes(pixels)

    def check(self, required_msaa=(), require_motion=False):
        self.path.write_text(json.dumps(self.report), encoding="utf-8")
        return reporter.check_report(self.path, required_msaa, require_motion)

    def run_cli(self, path=None, *options):
        self.path.write_text(json.dumps(self.report), encoding="utf-8")
        environment = os.environ.copy()
        environment.pop("GITHUB_STEP_SUMMARY", None)
        return subprocess.run([sys.executable, str(REPORTER), str(path or self.folder), *options],
                              capture_output=True, text=True, env=environment, check=False, timeout=10)

    def stage(self, name):
        return next(stage for stage in self.report["stages"] if stage["name"] == name)

    def test_complete_no_aa_and_msaa_baseline_needs_no_motion_stage(self):
        errors, observations = self.check(required_msaa=(2, 4, 8))
        self.assertEqual(errors, [])
        self.assertIn("- Optional motion: not requested.", observations)
        self.assertIn("observed color/depth 8x/8x", "\n".join(observations))
        result = self.run_cli(None, "--require-msaa", "2", "4", "8")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_hardware_fallback_is_unsupported_and_fails_an_explicit_sample_gate(self):
        stage = self.stage("11-msaa8")
        stage.update(status="unsupported", passed=False, skipReason="Device supports 4 samples for these formats.")
        stage["attachments"].update(hardwareSupportedMsaa=4, colorSamples=4, depthSamples=4)
        self.report.update(unsupportedMsaaStages=1, msaaCoverageComplete=False)
        errors, observations = self.check()
        self.assertEqual(errors, [])
        self.assertIn("those sample counts remain unverified", "\n".join(observations))
        result = self.run_cli(None, "--require-msaa", "8")
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("required 8x MSAA has no passed attachment observation", result.stdout)

    def test_supported_request_cannot_pass_or_skip_with_single_sample_attachments(self):
        stage = self.stage("10-msaa4")
        stage["attachments"].update(colorSamples=1, depthSamples=1)
        errors, _ = self.check()
        self.assertTrue(any("do not match device support" in error for error in errors))
        stage.update(status="unsupported", passed=False, skipReason="Fallback")
        self.report.update(unsupportedMsaaStages=1, msaaCoverageComplete=False)
        errors, _ = self.check()
        self.assertTrue(any("lacks a genuine device fallback" in error for error in errors))

    def test_different_color_and_depth_samples_are_rejected(self):
        self.stage("09-msaa2")["attachments"]["depthSamples"] = 1
        errors, _ = self.check()
        self.assertTrue(any("bound color/depth samples" in error for error in errors))

    def test_requested_scale_cannot_pass_with_unchanged_executed_viewports(self):
        sample = self.stage("07-render-scale-below-no-aa")["attachments"]
        for field in ("cameraScaled", "cameraDescriptor", "colorViewport", "depthViewport"):
            sample[field + "Width"], sample[field + "Height"] = 320, 240
        sample.update(colorWidth=320, colorHeight=240, depthWidth=320, depthHeight=240)
        errors, _ = self.check()
        self.assertTrue(any("colorViewport does not match the requested render scale" in error for error in errors))

    def test_larger_reused_allocations_are_valid_only_with_the_correct_active_viewport(self):
        self.stage("07-render-scale-below-no-aa")["attachments"].update(
            colorWidth=400, colorHeight=300, depthWidth=400, depthHeight=300)
        errors, _ = self.check()
        self.assertEqual(errors, [])

    def test_incomplete_attempt_cannot_reuse_a_previous_passed_outcome(self):
        self.report["status"] = "running"
        errors, _ = self.check()
        self.assertIn("player did not write a passed terminal outcome", errors)

    def test_development_player_without_checked_diagnostics_cannot_pass_acceptance(self):
        self.report.update(checksEnabled=False, instrumentationEnabled=False)
        errors, _ = self.check()
        self.assertIn("diagnostic player was compiled without checksEnabled", errors)
        self.assertIn("diagnostic player was compiled without instrumentationEnabled", errors)

    def test_checked_player_still_requires_graphics_validity_checks(self):
        self.report["renderGraphValidityChecks"] = False
        errors, _ = self.check()
        self.assertIn("RenderGraph validity checks were disabled in Graphics settings", errors)

    def test_baseline_cannot_use_temporal_antialiasing_or_motion_history(self):
        stage = self.stage("02-grass-on")
        stage["attachments"]["antialiasing"] = "TemporalAntiAliasing"
        stage["motionModeRequested"] = "Auto"
        errors, _ = self.check()
        self.assertTrue(any("antialiasing must be None" in error for error in errors))
        self.assertTrue(any("cannot depend on motion history" in error for error in errors))

    def test_baseline_cannot_enable_post_processing(self):
        self.stage("06-contacts-on-no-aa")["postProcessingRequested"] = True
        errors, _ = self.check()
        self.assertTrue(any("post processing must be disabled" in error for error in errors))

    def test_no_aa_camera_pan_needs_rendered_camera_travel_and_active_wind(self):
        self.stage("04-camera-pan-no-aa")["maximumCameraDisplacement"] = 0
        self.report["windScrollSpeed"] = 0
        errors, _ = self.check()
        self.assertTrue(any("camera pan did not produce measured travel" in error for error in errors))
        self.assertTrue(any("wind input was disabled" in error for error in errors))

    def test_wind_needs_actual_pixel_motion_at_the_same_camera_pose(self):
        image = self.folder / self.stage("03-wind-no-aa")["screenshot"]
        image.write_bytes(png(grass=True))
        self.stage("03-wind-no-aa")["cameraPosition"]["x"] = 0.2
        errors, _ = self.check()
        self.assertTrue(any("no measurable wind image difference" in error for error in errors))
        self.assertTrue(any("wind comparison did not hold the camera fixed" in error for error in errors))

    def test_contact_flag_cannot_replace_darkening_from_a_controlled_image_pair(self):
        (self.folder / self.stage("06-contacts-on-no-aa")["screenshot"]).write_bytes(png(grass=True))
        errors, _ = self.check()
        self.assertTrue(any("no measurable contact off/on image difference" in error for error in errors))
        self.stage("05-contacts-off-no-aa")["windStrength"] = 0.15
        errors, _ = self.check()
        self.assertTrue(any("wind strength does not match the controlled stage" in error for error in errors))

    def test_contact_darkening_does_not_excuse_incorrect_brightening(self):
        (self.folder / self.stage("06-contacts-on-no-aa")["screenshot"]).write_bytes(png(grass=True, green=48, red=32))
        self.report["brightenedContactPixels"] = 64
        errors, _ = self.check()
        self.assertIn("multiplicative contact blend unexpectedly brightened the controlled image", errors)

    def test_zero_population_counts_cannot_hide_stale_grass_draws(self):
        for name in ("12-black-mask", "13-no-sources"):
            (self.folder / self.stage(name)["screenshot"]).write_bytes(png(grass=True))
        errors, _ = self.check()
        self.assertTrue(any("black mask capture did not restore the grass-disabled image" in error for error in errors))
        self.assertTrue(any("no sources capture did not restore the grass-disabled image" in error for error in errors))

    def test_filtered_run_cannot_omit_the_no_aa_image_comparison(self):
        self.report["stages"] = [stage for stage in self.report["stages"] if stage["name"] != "02-grass-on"]
        errors, _ = self.check()
        self.assertIn("missing baseline stage: 02-grass-on", errors)

    def test_missing_or_stale_attachment_evidence_is_rejected(self):
        stage = self.stage("10-msaa4")
        stage["attachments"]["frame"] = stage["activationFrame"] - 1
        errors, _ = self.check()
        self.assertTrue(any("stale or belongs to another stage" in error for error in errors))
        del stage["attachments"]
        errors, _ = self.check()
        self.assertTrue(any("missing executed attachment evidence" in error for error in errors))

    def test_camera_frames_must_fit_chronological_stage_windows(self):
        for stage in self.report["stages"]:
            stage.update(activationFrame=100, capturedFrame=100)
            stage["attachments"]["frame"] = 100
        errors, _ = self.check()
        self.assertTrue(any("frame count is impossible" in error for error in errors))

    def test_stage_order_and_overlapping_frame_windows_are_rejected(self):
        self.report["stages"][0], self.report["stages"][1] = self.report["stages"][1], self.report["stages"][0]
        errors, _ = self.check()
        self.assertIn("stages were missing, duplicated or executed out of order", errors)
        self.assertTrue(any("before the previous capture completed" in error for error in errors))

    def test_request_only_evidence_is_insufficient(self):
        sample = self.stage("10-msaa4")["attachments"]
        sample["colorEvidence"] = "pipeline.msaaSampleCount"
        errors, _ = self.check()
        self.assertTrue(any("missing color sample provenance" in error for error in errors))

    def test_duplicate_stage_does_not_overwrite_a_failed_observation(self):
        failed = copy.deepcopy(self.stage("10-msaa4"))
        failed.update(status="failed", passed=False)
        self.report["stages"].insert(0, failed)
        errors, _ = self.check()
        self.assertIn("duplicate stage: 10-msaa4", errors)

    def test_null_device_and_error_logs_reject_overall_pass(self):
        self.report.update(graphicsApi="Null", errorCount=1)
        errors, _ = self.check()
        self.assertIn("a Null graphics device cannot verify rendering", errors)
        self.assertIn("Unity errors, assertions or exceptions were recorded", errors)

    def test_incorrect_grass_counts_and_unchanged_image_fail(self):
        self.stage("02-grass-on")["visibleGrass"] = 0
        self.stage("12-black-mask")["visibleGrass"] = 400
        self.report["changedGrassPixels"] = 0
        errors, _ = self.check()
        self.assertEqual(sum("population violates expectation" in error for error in errors), 2)
        self.assertTrue(any("image difference" in error for error in errors))

    def test_screenshot_must_exist_and_have_matching_complete_png_data(self):
        image = self.folder / self.stage("02-grass-on")["screenshot"]
        for name, data in (("missing", None), ("truncated", self.capture[:-5]),
                           ("dimensions", png(640, 480)), ("checksum", self.capture[:-1] + b"\x00")):
            with self.subTest(name=name):
                if data is None:
                    image.unlink()
                else:
                    image.write_bytes(data)
                errors, _ = self.check()
                self.assertTrue(any(error.startswith("02-grass-on: ") for error in errors), errors)

    def test_png_pixels_must_decode_with_bounded_size_and_valid_row_filters(self):
        image = self.folder / self.stage("02-grass-on")["screenshot"]
        size = (320 * 4 + 1) * 240
        for name, compressed in (("invalid zlib", b"not compressed image pixels"),
                                 ("short pixels", zlib.compress(b"\x00" * (size - 1))),
                                 ("excess pixels", zlib.compress(b"\x00" * (size + 1))),
                                 ("invalid filter", zlib.compress(b"\x05" + b"\x00" * (size - 1)))):
            with self.subTest(name=name):
                image.write_bytes(png(compressed=compressed))
                errors, _ = self.check()
                self.assertTrue(any(error.startswith("02-grass-on: screenshot PNG") for error in errors), errors)

    def test_png_rgb_and_rgba_row_filters_decode_the_original_pixels(self):
        image = self.folder / "filters.png"
        for channels, color_type in ((3, 2), (4, 6)):
            first = bytes([10, 20, 30, 40][:channels])
            second = bytes([50, 60, 70, 80][:channels])
            raw = (b"\x00" + first + second +
                   b"\x01" + first + bytes([40] * channels) +
                   b"\x02" + bytes(2 * channels) +
                   b"\x03" + bytes([5, 10, 15, 20][:channels]) + bytes([20] * channels) +
                   b"\x04" + bytes(2 * channels))
            image.write_bytes(reporter.PNG_SIGNATURE +
                              chunk(b"IHDR", struct.pack(">IIBBBBB", 2, 5, 8, color_type, 0, 0, 0)) +
                              chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b""))
            decoded_channels, pixels = reporter.check_png(image, 2, 5)
            self.assertEqual(decoded_channels, channels)
            self.assertEqual(pixels, (first + second) * 5)

    def test_identical_off_on_images_cannot_claim_a_positive_grass_difference(self):
        (self.folder / self.stage("02-grass-on")["screenshot"]).write_bytes(self.capture)
        errors, _ = self.check()
        self.assertIn("no-AA grass off/on pixel counts disagree with the decoded captures", errors)
        self.assertIn("decoded no-AA captures show no measurable grass off/on image difference", errors)

    def test_image_comparison_matches_unity_bottom_origin_and_excludes_the_gui(self):
        before = bytearray(320 * 240 * 3)
        after = bytearray(before)
        # PNG row 100 maps to Unity row 139, which is inside the 240px ROI.
        after[(100 * 320 + 100) * 3] = 20
        # PNG row 80 maps to Unity row 159, outside its top GUI exclusion.
        after[(80 * 320 + 100) * 3] = 20
        self.assertEqual(reporter.compare_grass_images((3, before), (3, after), 320, 240), (13440, 1))

    def test_capture_path_cannot_escape_the_report_directory(self):
        self.stage("02-grass-on")["screenshot"] = "../elsewhere.png"
        errors, _ = self.check()
        self.assertTrue(any("screenshot must be a local filename" in error for error in errors))

    def test_malformed_observations_fail_without_a_parser_crash(self):
        sample = self.stage("02-grass-on")["attachments"]
        sample.update(colorSamples=True, hardwareSupportedMsaa=[], colorEvidence={})
        self.stage("02-grass-on")["imageWidth"] = "320"
        errors, _ = self.check()
        self.assertGreaterEqual(len(errors), 3)

    def test_oversized_numeric_values_fail_without_overflowing_the_reporter(self):
        self.report["windStrength"] = 10 ** 400
        self.stage("02-grass-on")["requestedRenderScale"] = 10 ** 400
        errors, _ = self.check()
        self.assertTrue(any("wind input was disabled" in error for error in errors))
        self.assertTrue(any("incorrect requested render scale" in error for error in errors))

    def test_optional_motion_is_only_required_when_requested(self):
        errors, _ = self.check(require_motion=True)
        self.assertIn("optional motion coverage was required but absent", errors)
        stage = copy.deepcopy(self.stage("14-restored"))
        stage.update(name=reporter.MOTION, optional=True, motionModeRequested="Always", cameraPanRequested=True,
                     screenshot="15-optional-motion.png")
        stage["activationFrame"] += 10
        stage["capturedFrame"] += 10
        stage["attachments"]["frame"] += 10
        self.report["stages"].append(stage)
        self.report["optionalMotionRequested"] = True
        (self.folder / stage["screenshot"]).write_bytes(self.capture)
        errors, _ = self.check(require_motion=True)
        self.assertEqual(errors, [])

    def test_later_passing_report_cannot_hide_a_failed_run(self):
        child = self.folder / "earlier-run"
        child.mkdir()
        (child / "grass-smoke-results.json").write_text('{"passed": false}', encoding="utf-8")
        result = self.run_cli()
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("earlier-run", result.stdout)
        self.assertIn("Evidence checks: passed.", result.stdout)

    def test_no_results_and_invalid_json_fail(self):
        empty = self.folder / "empty"
        empty.mkdir()
        result = self.run_cli(empty)
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("No standalone grass smoke results", result.stdout)
        self.path.write_text("{", encoding="utf-8")
        errors, _ = reporter.check_report(self.path, (), False)
        self.assertTrue(errors)


if __name__ == "__main__":
    unittest.main()
