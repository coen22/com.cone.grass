"""Source/math regressions, not rendered-image or GPU timing acceptance.

Evaluate the shipped compensation expression componentwise and inspect the
explicit optional-work branches. DXC/Unity validation remains separate.
"""
from __future__ import annotations

import ast
import math
from pathlib import Path
import re
import unittest


SHADER = Path(__file__).resolve().parents[2] / "Runtime/Shaders/GrassBladeShader.shader"


def without_comments(source: str) -> str:
    return re.sub(r"//[^\n]*|/\*.*?\*/", "", source, flags=re.DOTALL)


def block_end(source: str, start: int) -> int:
    if source[start] != "{":
        raise ValueError("Expected opening brace")
    depth = 0
    for index in range(start, len(source)):
        depth += (source[index] == "{") - (source[index] == "}")
        if depth == 0:
            return index + 1
    raise ValueError("Unclosed source block")


def function_body(source: str, name: str) -> str:
    match = re.search(r"\b\w+\s+" + re.escape(name) + r"\s*\([^;{}]*\)[^;{}]*\{", source)
    if match is None:
        raise ValueError(f"Missing function: {name}")
    start = match.end() - 1
    return source[start + 1:block_end(source, start) - 1]


def remove_guarded_blocks(source: str, condition: str) -> tuple[str, int]:
    pattern = re.compile(r"UNITY_BRANCH\s+if\s*\(\s*" + condition + r"\s*\)\s*\{")
    count = 0
    while (match := pattern.search(source)) is not None:
        end = block_end(source, match.end() - 1)
        source = source[:match.start()] + source[end:]
        count += 1
    return source, count


def evaluate_expression(expression: str, bare: float, seen: float) -> float:
    """Interpret only scalar constants/names, division and max; never use eval."""
    variables = {"bareLight": bare, "seenLight": seen}

    def visit(node: ast.AST) -> float:
        if isinstance(node, ast.Constant) and type(node.value) in (int, float):
            return float(node.value)
        if isinstance(node, ast.Name) and node.id in variables:
            return variables[node.id]
        if isinstance(node, ast.BinOp) and isinstance(node.op, ast.Div):
            return visit(node.left) / visit(node.right)
        if (isinstance(node, ast.Call) and isinstance(node.func, ast.Name)
                and node.func.id == "max" and len(node.args) == 2 and not node.keywords):
            return max(visit(node.args[0]), visit(node.args[1]))
        raise ValueError(f"Unsupported compensation expression: {ast.dump(node)}")

    return visit(ast.parse(expression, mode="eval").body)


class CanopyLightingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.source = without_comments(SHADER.read_text(encoding="utf-8"))
        body = function_body(cls.source, "GrassCanopyLightScale").strip()
        match = re.fullmatch(r"return\s+([^;]+);", body)
        if match is None:
            raise ValueError("Compensation must remain a directly testable componentwise expression")
        cls.expression = match.group(1)

    def scale(self, bare: float, seen: float) -> float:
        return evaluate_expression(self.expression, bare, seen)

    def test_no_extinction_is_neutral_from_black_through_hdr(self) -> None:
        for value in (0.0, 1e-8, 1e-6, 0.00005, 0.000099, 0.0001, 0.1, 1.0, 100.0, 65504.0):
            with self.subTest(value=value):
                self.assertEqual(self.scale(value, value), 1.0)

    def test_colored_light_does_not_erase_a_dark_channel(self) -> None:
        # A bent blade can receive light when the bare-ground reference is black.
        blade = (0.25, 0.125, 0.5)
        reference = (0.0, 0.00005, 2.0)
        actual = tuple(channel * self.scale(ref, ref) for channel, ref in zip(blade, reference))
        self.assertEqual(actual, blade)

    def test_old_denominator_only_floor_reproduces_the_regression(self) -> None:
        old = "bareLight / max(seenLight, 1e-4)"
        self.assertEqual(evaluate_expression(old, 0.0, 0.0), 0.0)
        self.assertEqual(evaluate_expression(old, 0.00005, 0.00005), 0.5)
        self.assertEqual(self.scale(0.0, 0.0), 1.0)
        self.assertEqual(self.scale(0.00005, 0.00005), 1.0)

    def test_resolved_canopy_compensation_preserves_original_ratio(self) -> None:
        for bare in (0.0001, 0.01, 0.5, 1.0, 10.0, 100.0):
            for visibility in (0.001, 0.01, 0.25, 0.5, 1.0):
                seen = bare * visibility
                if seen >= 0.0001:
                    with self.subTest(bare=bare, visibility=visibility):
                        self.assertAlmostEqual(self.scale(bare, seen), bare / seen)

    def test_scale_is_finite_and_never_darkens_valid_extinction(self) -> None:
        for bare in (0.0, 1e-8, 1e-5, 1e-4, 0.01, 1.0, 100.0, 65504.0):
            previous = math.inf
            for visibility in (0.0, 0.001, 0.01, 0.1, 0.5, 1.0):
                scale = self.scale(bare, bare * visibility)
                with self.subTest(bare=bare, visibility=visibility):
                    self.assertTrue(math.isfinite(scale))
                    self.assertGreaterEqual(scale, 1.0)
                    self.assertLessEqual(scale, previous)
                previous = scale

    def test_material_and_empty_canopy_admission_are_both_required(self) -> None:
        body = function_body(self.source, "GrassForwardFragment")
        self.assertRegex(body, r"bool\s+canopyEnabled\s*=\s*_CanopyOcclusion\s*>\s*0\.5\s*&&\s*input\.canopyArea\s*>\s*0\.0\s*;")
        self.assertIn("[ToggleUI] _CanopyOcclusion", self.source)
        self.assertNotRegex(self.source, r"#pragma[^\n]*CANOPY")

    def test_disabled_vertex_avoids_canopy_density_and_view_work(self) -> None:
        body = function_body(self.source, "GrassForwardVertex")
        self.assertIn("GrassForwardVaryings output = (GrassForwardVaryings)0;", body)
        self.assertIn("output.canopySeen = 1.0h;", body)
        body, density_blocks = remove_guarded_blocks(body, r"_CanopyOcclusion\s*>\s*0\.5")
        body, view_blocks = remove_guarded_blocks(body, r"output\.canopyArea\s*>\s*0\.0")
        self.assertEqual((density_blocks, view_blocks), (1, 1))
        self.assertNotIn("GrassAuthoredDensity(", body)
        self.assertNotRegex(body, r"\bGrassCanopy\w*\s*\(")
        self.assertIn("BuildGrassVertex(input, instanceID)", body)

    def test_disabled_fragment_skips_only_optional_lighting_work(self) -> None:
        body = function_body(self.source, "GrassForwardFragment")
        body, blocks = remove_guarded_blocks(body, "canopyEnabled")
        self.assertEqual(blocks, 4)  # Main/ambient, clustered directional, punctual, normalization.
        self.assertNotRegex(body, r"\bGrassCanopy\w*\s*\(")
        self.assertNotIn("GrassGroundLight(", body)
        self.assertNotIn("GrassAmbientLight(canopyNormal)", body)
        self.assertNotRegex(body, r"(?:bareLight|seenLight)\s*\+=")
        self.assertEqual(body.count("GrassLight("), 3)
        self.assertEqual(body.count("lighting += directLight;"), 3)
        self.assertEqual(body.count("GlobalIllumination("), 1)
        self.assertEqual(body.count("GetAdditionalLight("), 2)
        for retained in ("GrassMainLight(", "GrassForwardCoverage(", "InitializeBRDFData(",
                         "GrassGroundBlend(", "MixFog(", "LIGHT_LOOP_BEGIN(pixelLightCount)"):
            self.assertIn(retained, body)

    def test_enabled_canopy_keeps_sky_and_every_light_contribution(self) -> None:
        body = function_body(self.source, "GrassForwardFragment")
        self.assertEqual(body.count("GrassCanopySkyVisibility("), 1)
        self.assertEqual(body.count("GrassCanopyTransmission("), 3)
        self.assertEqual(body.count("GrassGroundLight("), 3)
        self.assertEqual(body.count("GrassCanopySeen("), 2)
        self.assertEqual(body.count("bareLight += groundDirect;"), 3)
        self.assertEqual(body.count("GrassCanopyLightScale("), 1)

    def test_no_extra_pass_or_render_state_cost(self) -> None:
        self.assertEqual(re.findall(r'\bName\s+"([^"]+)"', self.source),
                         ["GrassForward", "GrassContactDepth", "GrassMotionVectors"])
        self.assertEqual(self.source.count("Cull Off"), 3)
        self.assertIn("AlphaToMask [_GrassAlphaToCoverage]", self.source)


if __name__ == "__main__":
    unittest.main()
