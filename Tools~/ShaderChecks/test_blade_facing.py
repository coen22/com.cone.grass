"""Shared blade geometry source/math regressions; not native GPU image or timing tests.

The numeric model checks end-on facing and center-line invariants. Structural
checks read the actual shaders to ensure the production path uses these steps.
"""
from __future__ import annotations

import math
from pathlib import Path
import random
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]
COMMON = ROOT / "Runtime/Shaders/GrassBladeCommon.hlsl"
MOTION = ROOT / "Runtime/Shaders/GrassMotionVectors.hlsl"
BLADE = ROOT / "Runtime/Shaders/GrassBladeShader.shader"


def clean(source: str) -> str:
    return re.sub(r"//[^\n]*|/\*.*?\*/", "", source, flags=re.S)


def body(source: str, name: str) -> str:
    source = clean(source)
    match = re.search(r"\b\w+\s+" + re.escape(name) + r"\s*\([^;{}]*\)\s*\{", source)
    if match is None:
        raise ValueError("Missing function: " + name)
    start, depth = match.end(), 1
    for end in range(start, len(source)):
        depth += (source[end] == "{") - (source[end] == "}")
        if depth == 0:
            return source[start:end]
    raise ValueError("Unclosed function: " + name)


def dot(a, b):
    return sum(x * y for x, y in zip(a, b))


def norm(v):
    length = math.sqrt(dot(v, v))
    if length < 1e-20:
        raise ValueError("Degenerate numeric test vector")
    return tuple(x / length for x in v)


def cross(a, b):
    return (a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0])


def right(blade, ray, camera_right):
    direction = cross(blade, ray)
    if dot(direction, direction) < 0.00001:
        parallel = dot(camera_right, blade)
        direction = tuple(x - b * parallel for x, b in zip(camera_right, blade))
        if dot(direction, direction) < 0.00001:
            up_axis = (0, 1, 0) if abs(blade[1]) < 0.9 else (0, 0, 1)
            direction = cross(blade, up_axis)
    return norm(direction)


def center(root, direction, height):
    pivot, blade_height, curvature = root
    return (pivot[0] + direction[0] * height * blade_height + height * height * curvature[0],
            pivot[1] + direction[1] * height * blade_height,
            pivot[2] + direction[2] * height * blade_height + height * height * curvature[1])


def blade_direction(slope, wind, height):
    return norm((slope[0] + wind[0] * height, slope[1],
                 slope[2] + wind[1] * height))


class BladeFacingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.common = COMMON.read_text(encoding="utf-8")
        cls.motion = MOTION.read_text(encoding="utf-8")
        cls.blade = BLADE.read_text(encoding="utf-8")

    def test_end_on_width_is_orthogonal_even_for_off_axis_perspective(self):
        blade = norm((0.73, 0.3, 0.61))
        camera_right = norm((0.7, 0.7, -0.1))
        # The previous fallback used camera_right unprojected.
        self.assertGreater(abs(dot(blade, camera_right)), 0.2)
        facing = right(blade, blade, camera_right)
        self.assertAlmostEqual(dot(blade, facing), 0.0, delta=1e-12)
        self.assertAlmostEqual(dot(facing, facing), 1.0, delta=1e-12)

    def test_doubly_degenerate_facing_always_returns_a_unit_perpendicular(self):
        for blade in ((0, 1, 0), (0, 0, 1), (1, 0, 0), (-1, 0, 0),
                      (0.75, 0.6, 0.2783882181)):
            blade = norm(blade)
            facing = right(blade, blade, blade)
            with self.subTest(blade=blade):
                self.assertTrue(all(math.isfinite(x) for x in facing))
                self.assertAlmostEqual(dot(blade, facing), 0.0, delta=1e-12)
                self.assertAlmostEqual(dot(facing, facing), 1.0, delta=1e-12)

    def test_normal_crossing_facing_is_unchanged(self):
        rng = random.Random(8145)
        for i in range(1000):
            blade = norm(tuple(rng.uniform(-1, 1) for _ in range(3)))
            ray = norm(tuple(rng.uniform(-1, 1) for _ in range(3)))
            original = cross(blade, ray)
            if dot(original, original) < 0.00001:
                continue
            expected = norm(original)
            actual = right(blade, ray, (1, 0, 0))
            with self.subTest(i=i):
                for a, b in zip(actual, expected):
                    self.assertAlmostEqual(a, b, delta=1e-12)

    def test_near_parallel_facing_is_stable_and_perpendicular(self):
        rng = random.Random(8842)
        for i in range(2000):
            blade = norm(tuple(rng.uniform(-1, 1) for _ in range(3)))
            perturbation = tuple(rng.uniform(-0.0001, 0.0001) for _ in range(3))
            ray = norm(tuple(b + offset for b, offset in zip(blade, perturbation)))
            camera_right = norm(tuple(rng.uniform(-1, 1) for _ in range(3)))
            facing = right(blade, ray, camera_right)
            with self.subTest(i=i):
                self.assertAlmostEqual(dot(facing, blade), 0.0, delta=1e-9)
                self.assertAlmostEqual(dot(facing, facing), 1.0, delta=1e-12)

    def test_centers_match_previous_wind_curvature_curve(self):
        rng = random.Random(8843)
        for i in range(800):
            pivot = tuple(rng.uniform(-100000, 100000) for _ in range(3))
            slope = norm((rng.uniform(-1, 1), rng.uniform(0.2, 2), rng.uniform(-1, 1)))
            wind = (rng.uniform(-4, 4), rng.uniform(-4, 4))
            curvature = (rng.uniform(-2, 2), rng.uniform(-2, 2))
            blade_height = rng.uniform(0.01, 3)
            h = rng.choice((0, 0.1, 0.5, 0.75, 1))
            d = blade_direction(slope, wind, h)
            expected = (
                pivot[0] + d[0] * h * blade_height + h*h*curvature[0],
                pivot[1] + d[1] * h * blade_height,
                pivot[2] + d[2] * h * blade_height + h*h*curvature[1]
            )
            self.assertEqual(center((pivot, blade_height, curvature), d, h), expected)

    def test_shared_center_reuses_the_single_blade_direction(self):
        row = body(self.common, "EvaluateGrassRow")
        self.assertEqual(row.count("GrassDirectionAtHeight("), 1)
        self.assertIn("center = GrassCenterFromDirection(root, height, bladeDirection);", row)
        self.assertIn("float3 rightTangent = GrassFacingRight(bladeDirection, viewDirection, view.cameraRight);", row)
        self.assertNotIn("center = GrassCenterAtHeight(", row)
        self.assertIn("height = saturate(height);", row)
        self.assertIn("lerp(root.width * 0.5, root.tipWidth, height)", row)

        helper = body(self.common, "GrassCenterFromDirection")
        self.assertIn("root.pivot + bladeDirection * height * root.height", helper)
        self.assertIn("center.xz += height * height * root.curvature;", helper)
        self.assertNotIn("GrassDirectionAtHeight(", helper)

        old_entry = body(self.common, "GrassCenterAtHeight")
        self.assertIn("GrassCenterFromDirection(root, height, GrassDirectionAtHeight(root, height))", old_entry)

    def test_source_facing_handles_both_fallbacks(self):
        facing = body(self.common, "GrassFacingRight")
        self.assertIn("cross(bladeDirection, viewDirection)", facing)
        self.assertIn("cameraRight - bladeDirection * dot(cameraRight, bladeDirection)", facing)
        self.assertIn("abs(bladeDirection.y) < 0.9", facing)
        self.assertIn("float3(0.0, 1.0, 0.0)", facing)
        self.assertIn("float3(0.0, 0.0, 1.0)", facing)
        self.assertIn("SafeNormalize(rightTangent)", facing)

    def test_color_vertices_reuse_a_single_view_snapshot(self):
        vertex = body(self.common, "BuildGrassVertex")
        self.assertEqual(vertex.count("GrassCurrentView("), 1)
        self.assertIn("BuildGrassRootForView(", vertex)
        self.assertNotIn("BuildCurrentGrassRoot(", vertex)
        self.assertIn("EvaluateGrassPosition(root, view, input.uv)", vertex)

    def test_motion_vertices_share_current_view_and_rows_share_previous_view(self):
        vertex = body(self.motion, "GrassMotionVertex")
        self.assertEqual(vertex.count("GrassCurrentView("), 1)
        self.assertIn("BuildGrassRootForView(positionData, currentView)", vertex)
        self.assertIn("EvaluateGrassPosition(current, currentView, input.uv)", vertex)

        previous = body(self.motion, "PreviousGrassSurface")
        self.assertEqual(previous.count("GrassPreviousView("), 1)
        self.assertIn("EvaluateGrassRow(root, previousView, lowerHeight", previous)
        self.assertIn("EvaluateGrassRow(root, previousView, upperHeight", previous)

    def test_all_passes_keep_shared_geometry_and_existing_coverage(self):
        self.assertIn("GrassVertexData blade = BuildGrassVertex(input, instanceID);",
                      body(self.blade, "GrassForwardVertex"))
        self.assertIn("GrassVertexData blade = BuildGrassVertex(input, instanceID);",
                      body(self.blade, "GrassContactDepthVertex"))
        motion = body(self.motion, "GrassMotionVertex")
        self.assertIn("GrassRootData current = BuildGrassRootForView", motion)
        self.assertIn("GrassFragmentCoverageWithMode(", body(self.motion, "GrassMotionFragment"))
        self.assertIn("GrassForwardCoverage(", body(self.blade, "GrassForwardFragment"))
        self.assertIn("GrassFragmentCoverage(", body(self.blade, "GrassContactDepthFragment"))


if __name__ == "__main__":
    unittest.main()
