#!/usr/bin/env python3
"""Check standalone no-AA/MSAA smoke evidence, preserving unsupported requests."""

import argparse
import json
import math
import os
from pathlib import Path
import re
import struct
import sys
import zlib


# These are the baseline stages emitted by GrassValidationPlayerProbe. Motion is
# deliberately separate, so a temporal run cannot stand in for no-AA rendering.
BASELINE = {
    "01-grass-off": ("Unchecked", 1, 1.0, False),
    "02-grass-on": ("Positive", 1, 1.0, False),
    "03-wind-no-aa": ("Positive", 1, 1.0, False),
    "04-camera-pan-no-aa": ("Positive", 1, 1.0, False),
    "05-contacts-off-no-aa": ("Positive", 1, 1.0, False),
    "06-contacts-on-no-aa": ("Positive", 1, 1.0, True),
    "07-render-scale-below-no-aa": ("Positive", 1, 0.75, True),
    "08-render-scale-above-no-aa": ("Positive", 1, 1.25, True),
    "09-msaa2": ("Positive", 2, 1.0, True),
    "10-msaa4": ("Positive", 4, 1.0, True),
    "11-msaa8": ("Positive", 8, 1.0, True),
    "12-black-mask": ("Zero", 1, 1.0, True),
    "13-no-sources": ("Zero", 1, 1.0, True),
    "14-restored": ("Positive", 1, 1.0, True),
}
MOTION = "15-optional-motion"
SAMPLES = {1, 2, 4, 8}
SAMPLE_EVIDENCE = {"allocated RenderTexture", "imported target metadata"}
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
MAXIMUM_SCREENSHOT_PIXELS = 4 * 1024 * 1024


def integer(value, minimum=0):
    return type(value) is int and value >= minimum


def finite_number(value):
    try:
        return type(value) in (int, float) and math.isfinite(value)
    except OverflowError:
        return False


def check_png(path, width, height):
    """Decode bounded RGB/RGBA8 captures, including PNG row filters."""
    if path.stat().st_size > MAXIMUM_SCREENSHOT_PIXELS * 8:
        raise ValueError("screenshot exceeds the bounded capture size")
    data = path.read_bytes()
    if not data.startswith(PNG_SIGNATURE):
        raise ValueError("screenshot is not a PNG")
    offset, has_header, has_pixels = len(PNG_SIGNATURE), False, False
    compressed = bytearray()
    channels = 0
    while offset + 12 <= len(data):
        length = struct.unpack_from(">I", data, offset)[0]
        kind = data[offset + 4:offset + 8]
        end = offset + 12 + length
        if end > len(data):
            raise ValueError("screenshot contains a truncated PNG chunk")
        payload = data[offset + 8:offset + 8 + length]
        crc = struct.unpack_from(">I", data, offset + 8 + length)[0]
        if zlib.crc32(kind + payload) != crc:
            raise ValueError("screenshot PNG checksum is invalid")
        if not has_header and kind != b"IHDR":
            raise ValueError("screenshot PNG is missing its initial header")
        if kind == b"IHDR":
            if has_header or length != 13 or struct.unpack_from(">II", payload) != (width, height):
                raise ValueError("screenshot dimensions do not match the stage")
            bits, color_type, compression, filtering, interlace = struct.unpack_from(">BBBBB", payload, 8)
            if bits != 8 or color_type not in (2, 6) or (compression, filtering, interlace) != (0, 0, 0):
                raise ValueError("screenshot must use noninterlaced RGB/RGBA8 PNG data")
            channels = 3 if color_type == 2 else 4
            has_header = True
        elif kind == b"IDAT":
            has_pixels |= length > 0
            compressed.extend(payload)
        elif kind == b"IEND":
            if length != 0 or not has_pixels or end != len(data):
                raise ValueError("screenshot PNG has incomplete or trailing data")
            stride = width * channels
            expected = (stride + 1) * height
            decoder = zlib.decompressobj()
            try:
                decoded = decoder.decompress(compressed, expected + 1)
            except zlib.error as error:
                raise ValueError("screenshot PNG has invalid compressed pixels") from error
            if len(decoded) != expected or not decoder.eof or decoder.unconsumed_tail or decoder.unused_data:
                raise ValueError("screenshot PNG decoded size or compressed stream is invalid")
            pixels = bytearray(stride * height)
            for row in range(height):
                source = row * (stride + 1)
                method = decoded[source]
                if method > 4:
                    raise ValueError("screenshot PNG uses an invalid row filter")
                start = row * stride
                scanline = decoded[source + 1:source + stride + 1]
                if method == 0:
                    pixels[start:start + stride] = scanline
                    continue
                for column, value in enumerate(scanline):
                    left = pixels[start + column - channels] if column >= channels else 0
                    above = pixels[start + column - stride] if row else 0
                    upper_left = pixels[start + column - stride - channels] if row and column >= channels else 0
                    if method == 1:
                        prediction = left
                    elif method == 2:
                        prediction = above
                    elif method == 3:
                        prediction = (left + above) // 2
                    else:
                        estimate = left + above - upper_left
                        distances = (abs(estimate - left), abs(estimate - above), abs(estimate - upper_left))
                        prediction = (left if distances[0] <= distances[1] and distances[0] <= distances[2]
                                      else above if distances[1] <= distances[2] else upper_left)
                    pixels[start + column] = (value + prediction) & 255
            return channels, pixels
        offset = end
    raise ValueError("screenshot PNG is incomplete")


def compare_image_pixels(before, after, width, height, threshold):
    """Match the probe's RGB deltas and GUI-excluding, bottom-origin ROI."""
    before_channels, before_pixels = before
    after_channels, after_pixels = after
    compared = changed = darkened = brightened = 0
    for y in range(height // 4, min(height * 3 // 4, height - 96)):
        # Unity GetPixels32 rows start at the bottom; PNG rows start at the top.
        row = (height - 1 - y) * width
        for x in range(width // 4, width * 3 // 4):
            a, b = (row + x) * before_channels, (row + x) * after_channels
            compared += 1
            delta = [after_pixels[b + channel] - before_pixels[a + channel] for channel in range(3)]
            if max(abs(value) for value in delta) > threshold:
                changed += 1
            if -min(delta) > threshold:
                darkened += 1
            if max(delta) > threshold:
                brightened += 1
    return compared, changed, darkened, brightened


def compare_grass_images(before, after, width, height):
    return compare_image_pixels(before, after, width, height, 8)[:2]


def valid_pose(stage):
    return all(isinstance(stage.get(field), dict) and
               all(finite_number(stage[field].get(axis)) for axis in axes)
               for field, axes in (("cameraPosition", "xyz"), ("cameraRotation", "xyzw")))


def same_pose(left, right):
    return valid_pose(left) and valid_pose(right) and all(
        abs(left[field][axis] - right[field][axis]) < 0.000001
        for field, axes in (("cameraPosition", "xyz"), ("cameraRotation", "xyzw")) for axis in axes)


def check_report(path, required_msaa, require_motion):
    errors, observations = [], []

    def require(condition, reason):
        if not condition:
            errors.append(reason)

    try:
        report = json.loads(path.read_text(encoding="utf-8"))
        if not isinstance(report, dict):
            raise ValueError("report root must be an object")
    except (OSError, ValueError) as error:
        return [str(error)], observations
    require(report.get("schemaVersion") == 3, "expected smoke evidence schemaVersion 3")
    require(report.get("status") == "passed", "player did not write a passed terminal outcome")
    require(isinstance(report.get("attemptId"), str) and re.fullmatch(r"[0-9a-f]{32}", report["attemptId"]),
            "missing current-attempt identity")
    require(report.get("passed") is True, "player did not report a successful completed run")
    require(not report.get("failure"), "player recorded a failure: " + str(report.get("failure", "")))
    for field in ("unityVersion", "startedUtc", "completedUtc", "graphicsApi", "graphicsDevice"):
        require(isinstance(report.get(field), str) and bool(report[field].strip()), "missing " + field)
    require(report.get("graphicsApi") != "Null", "a Null graphics device cannot verify rendering")
    for field in ("supportsCompute", "supportsIndirectArguments", "supportsAsyncReadback"):
        require(report.get(field) is True, "required graphics capability was absent: " + field)
    for field in ("checksEnabled", "instrumentationEnabled"):
        require(report.get(field) is True, "diagnostic player was compiled without " + field)
    require(report.get("renderGraphValidityChecks") is True, "RenderGraph validity checks were disabled in Graphics settings")
    require(type(report.get("errorCount")) is int and report["errorCount"] == 0,
            "Unity errors, assertions or exceptions were recorded")
    changed, compared = report.get("changedGrassPixels"), report.get("comparedPixels")
    require(integer(changed, 32) and integer(compared, 32) and changed <= compared,
            "no valid no-AA grass off/on image difference was recorded")
    require(integer(report.get("windTextureWidth"), 2) and integer(report.get("windTextureHeight"), 2),
            "no generated wind texture was recorded")
    require(report.get("windTextureVaries") is True, "wind texture contained no changing RG values")
    for field in ("windStrength", "windScrollSpeed"):
        require(finite_number(report.get(field)) and report[field] > 0, "wind input was disabled: " + field)
    raw_stages = report.get("stages")
    if not isinstance(raw_stages, list):
        return errors + ["stages must be an array"], observations
    stages = {}
    for stage in raw_stages:
        if not isinstance(stage, dict) or not isinstance(stage.get("name"), str):
            errors.append("stage must be an object with a name")
            continue
        name = stage["name"]
        if name in stages:
            errors.append("duplicate stage: " + name)
        stages[name] = stage
    for name in BASELINE:
        require(name in stages, "missing baseline stage: " + name)
    unknown = set(stages) - set(BASELINE) - {MOTION}
    require(not unknown, "unknown stages: " + ", ".join(sorted(unknown)))
    motion_requested = report.get("optionalMotionRequested")
    require(type(motion_requested) is bool, "optionalMotionRequested must be a boolean")
    require((MOTION in stages) == (motion_requested is True), "optional motion request and executed stage disagree")
    require(not require_motion or motion_requested is True, "optional motion coverage was required but absent")
    expected_order = list(BASELINE) + ([MOTION] if motion_requested is True else [])
    observed_order = [stage.get("name") for stage in raw_stages if isinstance(stage, dict)]
    require(observed_order == expected_order, "stages were missing, duplicated or executed out of order")
    unsupported, successful_msaa = 0, set()
    image_paths = set()
    compared_images = {}
    previous_capture = None
    for name, stage in stages.items():
        if name not in BASELINE and name != MOTION:
            continue
        optional = name == MOTION
        count, requested, scale, contacts = BASELINE.get(name, ("Positive", 1, 1.0, True))
        prefix = name + ": "
        require(stage.get("optional") is optional, prefix + "optional flag disagrees with baseline membership")
        require(stage.get("motionModeRequested") == ("Always" if optional else "Off"),
                prefix + "no-AA/MSAA baseline cannot depend on motion history")
        require(stage.get("contactsRequested") is contacts, prefix + "unexpected contact-shadow setting")
        require(stage.get("postProcessingRequested") is False, prefix + "baseline post processing must be disabled")
        expected_wind = 0 if name in ("05-contacts-off-no-aa", "06-contacts-on-no-aa") else report.get("windStrength")
        require(finite_number(stage.get("windStrength")) and finite_number(expected_wind) and
                abs(stage["windStrength"] - expected_wind) < 0.000001,
                prefix + "wind strength does not match the controlled stage")
        require(valid_pose(stage), prefix + "missing finite camera pose")
        require(stage.get("cameraTargetsScreen") is True, prefix + "camera must use normal screen output")
        require(type(stage.get("requestedMsaa")) is int and stage["requestedMsaa"] == requested,
                prefix + "incorrect requested MSAA count")
        actual_scale = stage.get("requestedRenderScale")
        require(finite_number(actual_scale) and abs(actual_scale - scale) < 0.001,
                prefix + "incorrect requested render scale")
        pan = optional or name == "04-camera-pan-no-aa"
        require(stage.get("cameraPanRequested") is pan, prefix + "camera-pan coverage does not match the stage")
        travel = stage.get("maximumCameraDisplacement")
        require(finite_number(travel) and travel >= (0.08 if pan else 0),
                prefix + "camera pan did not produce measured travel")
        require(stage.get("countExpectation") == count, prefix + "incorrect grass-count expectation")
        visible, overflow = stage.get("visibleGrass"), stage.get("overflowGrass")
        require(integer(visible) and (count == "Unchecked" or
                (visible > 0 if count == "Positive" else visible == 0)), prefix + "grass population violates expectation")
        require(integer(overflow) and (count == "Unchecked" or overflow == 0), prefix + "grass capacity overflowed")
        require(integer(stage.get("cameraFrames"), 6), prefix + "too few camera frames were observed")
        sample = stage.get("attachments")
        if not isinstance(sample, dict):
            errors.append(prefix + "missing executed attachment evidence")
            continue
        activation, captured, frame = stage.get("activationFrame"), stage.get("capturedFrame"), sample.get("frame")
        require(integer(activation) and integer(captured) and integer(frame) and
                activation <= frame <= captured and captured - frame <= 1,
                prefix + "attachment observation is stale or belongs to another stage")
        require(integer(activation) and integer(captured) and integer(stage.get("cameraFrames"), 6) and
                captured - activation + 1 >= stage["cameraFrames"],
                prefix + "camera frame count is impossible within the recorded stage window")
        require(previous_capture is None or (integer(activation) and activation >= previous_capture),
                prefix + "stage began before the previous capture completed")
        if integer(captured):
            previous_capture = captured
        require(sample.get("antialiasing") == "None", prefix + "camera antialiasing must be None")
        require(type(sample.get("requestedMsaa")) is int and sample["requestedMsaa"] == requested,
                prefix + "attachment evidence belongs to a different MSAA request")
        color, depth, supported = sample.get("colorSamples"), sample.get("depthSamples"), sample.get("hardwareSupportedMsaa")
        valid_samples = all(type(value) is int and value in SAMPLES for value in (color, depth, supported))
        require(valid_samples and color == depth == supported and supported <= requested,
                prefix + "bound color/depth samples do not match device support")
        widths = (sample.get("colorWidth"), sample.get("depthWidth"))
        heights = (sample.get("colorHeight"), sample.get("depthHeight"))
        require(all(integer(value, 1) for value in widths + heights) and
                widths[0] == widths[1] and heights[0] == heights[1], prefix + "incompatible attachment dimensions")
        camera_width, camera_height = sample.get("cameraWidth"), sample.get("cameraHeight")
        camera_dimensions_valid = (integer(camera_width, 1) and integer(camera_height, 1) and
                                   camera_width <= MAXIMUM_SCREENSHOT_PIXELS and camera_height <= MAXIMUM_SCREENSHOT_PIXELS and
                                   camera_width * camera_height <= MAXIMUM_SCREENSHOT_PIXELS)
        require(camera_dimensions_valid, prefix + "missing camera output dimensions")
        require(finite_number(sample.get("cameraRenderScale")) and abs(sample["cameraRenderScale"] - scale) < 0.00001,
                prefix + "camera did not adopt the requested render scale")
        if camera_dimensions_valid:
            scaled_width, scaled_height = max(1, int(camera_width * scale)), max(1, int(camera_height * scale))
            for field in ("cameraScaled", "cameraDescriptor", "colorViewport", "depthViewport"):
                require(type(sample.get(field + "Width")) is int and type(sample.get(field + "Height")) is int and
                        sample[field + "Width"] == scaled_width and sample[field + "Height"] == scaled_height,
                        prefix + field + " does not match the requested render scale")
            require(all(integer(value, 1) for value in widths + heights) and
                    min(widths) >= scaled_width and min(heights) >= scaled_height,
                    prefix + "the allocated attachments cannot contain the scaled viewport")
        for channel in ("color", "depth"):
            require(isinstance(sample.get(channel + "Evidence"), str) and sample[channel + "Evidence"] in SAMPLE_EVIDENCE,
                    prefix + "missing " + channel + " sample provenance")
            require(isinstance(sample.get(channel + "Format"), str) and
                    sample[channel + "Format"] not in ("", "None"), prefix + "missing " + channel + " format")
        status = stage.get("status")
        if status == "unsupported":
            unsupported += 1
            require(stage.get("passed") is False and requested > 1 and valid_samples and supported < requested and
                    isinstance(stage.get("skipReason"), str) and bool(stage["skipReason"].strip()),
                    prefix + "unsupported stage lacks a genuine device fallback")
        else:
            require(status == "passed" and stage.get("passed") is True, prefix + "stage did not pass")
            require(valid_samples and color == requested, prefix + "requested MSAA was not observed on the bound attachments")
            if valid_samples and color == depth == requested and stage.get("passed") is True and status == "passed":
                successful_msaa.add(requested)
        if requested > 1:
            observations.append(f"- {name}: {status}; requested {requested}x, observed color/depth {color}x/{depth}x, device supports {supported}x.")
        width, height = stage.get("imageWidth"), stage.get("imageHeight")
        dimensions_valid = integer(width, 320) and integer(height, 240) and width * height <= MAXIMUM_SCREENSHOT_PIXELS
        require(dimensions_valid, prefix + "invalid screenshot dimensions")
        require((width, height) == (camera_width, camera_height), prefix + "capture does not match the full-screen camera output")
        screenshot = stage.get("screenshot")
        if not isinstance(screenshot, str) or not screenshot or Path(screenshot).name != screenshot:
            errors.append(prefix + "screenshot must be a local filename")
        else:
            require(screenshot not in image_paths, prefix + "screenshot was reused by another stage")
            image_paths.add(screenshot)
            image_path = path.parent / screenshot
            require(not image_path.is_symlink(), prefix + "screenshot cannot be a symbolic link")
            if dimensions_valid and not image_path.is_symlink():
                try:
                    pixels = check_png(image_path, width, height)
                    if name in ("01-grass-off", "02-grass-on", "03-wind-no-aa", "05-contacts-off-no-aa",
                                "06-contacts-on-no-aa", "12-black-mask", "13-no-sources"):
                        compared_images[name] = (width, height, pixels)
                except (OSError, ValueError) as error:
                    errors.append(prefix + str(error))
    pairs = (
        ("01-grass-off", "02-grass-on", 8, "changedGrassPixels", 32, "grass off/on"),
        ("02-grass-on", "03-wind-no-aa", 8, "changedWindPixels", 8, "wind"),
        ("05-contacts-off-no-aa", "06-contacts-on-no-aa", 2, "darkenedContactPixels", 8, "contact off/on"),
        ("01-grass-off", "12-black-mask", 2, "changedBlackMaskPixels", 0, "black mask"),
        ("01-grass-off", "13-no-sources", 2, "changedNoSourcesPixels", 0, "no sources"),
    )
    for before_name, after_name, threshold, field, minimum, label in pairs:
        if before_name not in compared_images or after_name not in compared_images:
            continue
        before, after = compared_images[before_name], compared_images[after_name]
        require(before[:2] == after[:2], "no-AA " + label + " capture dimensions differ")
        require(same_pose(stages[before_name], stages[after_name]), "no-AA " + label + " comparison did not hold the camera fixed")
        if before[:2] == after[:2]:
            actual_compared, actual_changed, darkened, brightened = compare_image_pixels(
                before[2], after[2], before[0], before[1], threshold)
            observed = darkened if field == "darkenedContactPixels" else actual_changed
            require(actual_compared == compared and type(report.get(field)) is int and observed == report[field],
                    "no-AA " + label + " pixel counts disagree with the decoded captures")
            if minimum:
                require(observed >= minimum, "decoded no-AA captures show no measurable " + label + " image difference")
            else:
                require(observed == 0, "decoded no-AA " + label + " capture did not restore the grass-disabled image")
            if field == "darkenedContactPixels":
                require(type(report.get("brightenedContactPixels")) is int and brightened == report["brightenedContactPixels"],
                        "contact brightening count disagrees with the decoded captures")
                require(brightened == 0, "multiplicative contact blend unexpectedly brightened the controlled image")
            observations.append(f"- {label}: {observed} pixels {'darkened' if field == 'darkenedContactPixels' else 'changed'} in the fixed-camera ROI.")
    require(type(report.get("unsupportedMsaaStages")) is int and report["unsupportedMsaaStages"] == unsupported,
            "unsupported stage total does not match observations")
    require(report.get("msaaCoverageComplete") is (unsupported == 0), "MSAA completeness flag does not match observations")
    for samples in required_msaa:
        require(samples in successful_msaa, f"required {samples}x MSAA has no passed attachment observation")
    observations.append("- Optional motion: " + ("requested and checked separately." if motion_requested else "not requested."))
    if unsupported:
        observations.append(f"- {unsupported} MSAA request(s) were unsupported; those sample counts remain unverified.")
    return errors, observations


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("artifacts", type=Path, help="Smoke result JSON or directory containing player runs")
    parser.add_argument("--require-msaa", type=int, nargs="+", choices=(2, 4, 8), default=[],
                        help="Fail if any requested count was unsupported or unobserved")
    parser.add_argument("--require-motion", action="store_true", help="Also require the separately opted-in motion stage")
    args = parser.parse_args()
    paths = [args.artifacts] if args.artifacts.is_file() else sorted(args.artifacts.rglob("grass-smoke-results.json"))
    lines, failed = ["## Standalone grass evidence", ""], not paths
    if not paths:
        lines.append("ERROR: No standalone grass smoke results were produced.")
    for path in paths:
        errors, observations = check_report(path, args.require_msaa, args.require_motion)
        failed |= bool(errors)
        lines.extend([f"### {path}", "", "Evidence checks: " + ("failed." if errors else "passed."), ""] + observations)
        if errors:
            lines.extend(["", "Errors:"] + ["- " + error for error in errors])
        lines.append("")
    lines.append("The required baseline uses no camera antialiasing and no motion history. These checks verify recorded execution, attachment samples and capture files; visual quality and performance still need separate measurements.")
    summary = "\n".join(lines) + "\n"
    print(summary)
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as output:
            output.write(summary)
    return int(failed)


if __name__ == "__main__":
    sys.exit(main())
