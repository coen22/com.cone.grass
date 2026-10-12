"""Execute the shipped tangent expressions with scalar binary32 CPU arithmetic.

This bounded interpreter is not an HLSL compiler or GPU emulator: no driver
contraction, native half precision, rasterization, or timing is established.
Independent double-precision curve differences supply the numerical oracle.
"""
from __future__ import annotations

import ast
import math
from pathlib import Path
import random
import re
import struct
from types import SimpleNamespace
import unittest


SOURCE = Path(__file__).resolve().parents[2] / "Runtime/Shaders/GrassBladeCommon.hlsl"


def f32(value: float) -> float:
    try:
        return struct.unpack("<f", struct.pack("<f", value))[0]
    except OverflowError:
        return math.copysign(math.inf, value)


class Vec:
    def __init__(self, *values: float) -> None:
        self.values = tuple(f32(value) for value in values)

    def component(self, name: str) -> float:
        if name not in "xyz" or len(name) != 1:
            raise ValueError(f"Unsupported component: {name}")
        return self.values["xyz".index(name)]

    def binary(self, other, operation):
        values = other.values if isinstance(other, Vec) else (other,) * len(self.values)
        if len(values) != len(self.values):
            raise ValueError("Mismatched vector sizes")
        return Vec(*(operation(a, b) for a, b in zip(self.values, values)))

    def __add__(self, other):
        return self.binary(other, lambda a, b: f32(a + b))

    __radd__ = __add__

    def __sub__(self, other):
        return self.binary(other, lambda a, b: f32(a - b))

    def __mul__(self, other):
        return self.binary(other, lambda a, b: f32(a * b))

    __rmul__ = __mul__

    def __truediv__(self, other):
        return self.binary(other, lambda a, b: f32(a / b))


def dot(a: Vec, b: Vec) -> float:
    result = f32(a.values[0] * b.values[0])
    for x, y in zip(a.values[1:], b.values[1:]):
        result = f32(result + f32(x * y))
    return result


def normalize(a: Vec) -> Vec:
    return a * f32(1.0 / math.sqrt(max(dot(a, a), f32(1.17549435e-38))))


def vector(*values) -> Vec:
    if len(values) == 1 and isinstance(values[0], Vec):
        return values[0]  # The GPU's optional half conversion is not emulated.
    if len(values) != 3:
        raise ValueError("Expected a three-component vector")
    return Vec(*values)


def body(source: str, name: str) -> str:
    source = re.sub(r"//[^\n]*|/\*.*?\*/", "", source, flags=re.S)
    match = re.search(r"\b\w+\s+" + re.escape(name) + r"\s*\([^{};]*\)\s*\{", source)
    if match is None:
        raise ValueError(f"Missing function: {name}")
    start, depth = match.end(), 1
    for end in range(start, len(source)):
        depth += (source[end] == "{") - (source[end] == "}")
        if depth == 0:
            return source[start:end]
    raise ValueError(f"Unclosed function: {name}")


class TangentProgram:
    """Accept only the helper's declarations, assignments, and guarded returns."""
    def __init__(self, source: str) -> None:
        self.instructions = []
        for statement in body(source, "GrassTangentAtHeight").split(";"):
            statement = statement.strip()
            if not statement:
                continue
            guarded = re.fullmatch(r"if\s*\((.*?)\)\s*return\s+(.+)", statement, re.S)
            if guarded:
                self.instructions.append(("guard", self.parse(guarded[1]), self.parse(guarded[2])))
            elif statement.startswith("return "):
                self.instructions.append(("return", self.parse(statement[7:])))
            else:
                assignment = re.fullmatch(r"(?:(?:float|float3)\s+)?(\w+)\s*=\s*(.+)", statement, re.S)
                if assignment is None:
                    raise ValueError(f"Unsupported tangent statement: {statement}")
                self.instructions.append(("assign", assignment[1], self.parse(assignment[2])))

    @staticmethod
    def parse(expression: str) -> ast.AST:
        expression = " ".join(expression.split())
        if "?" in expression:
            condition, alternatives = expression.split("?", 1)
            positive, negative = alternatives.split(":", 1)
            expression = f"({positive}) if ({condition}) else ({negative})"
        return ast.parse(expression, mode="eval").body

    @staticmethod
    def evaluate(node: ast.AST, environment: dict):
        def visit(item: ast.AST):
            if isinstance(item, ast.Constant) and type(item.value) in (int, float):
                return f32(item.value)
            if isinstance(item, ast.Name) and item.id in environment:
                return environment[item.id]
            if isinstance(item, ast.Attribute):
                owner = visit(item.value)
                if isinstance(owner, Vec):
                    return owner.component(item.attr)
                if owner is environment["root"] and item.attr in vars(owner):
                    return getattr(owner, item.attr)
            if isinstance(item, ast.BinOp):
                left, right = visit(item.left), visit(item.right)
                operations = {ast.Add: lambda: left + right, ast.Sub: lambda: left - right,
                              ast.Mult: lambda: left * right, ast.Div: lambda: left / right}
                if type(item.op) in operations:
                    result = operations[type(item.op)]()
                    return result if isinstance(result, Vec) else f32(result)
            if isinstance(item, ast.UnaryOp) and isinstance(item.op, ast.USub):
                value = visit(item.operand)
                return value * -1.0 if isinstance(value, Vec) else f32(-value)
            if isinstance(item, ast.Compare) and len(item.ops) == 1:
                left, right = visit(item.left), visit(item.comparators[0])
                operations = {ast.Gt: lambda: left > right, ast.LtE: lambda: left <= right}
                if type(item.ops[0]) in operations:
                    return operations[type(item.ops[0])]()
            if isinstance(item, ast.IfExp):
                return visit(item.body if visit(item.test) else item.orelse)
            if isinstance(item, ast.Call) and isinstance(item.func, ast.Name) and not item.keywords:
                functions = {"float3": vector, "half3": vector, "dot": dot, "max": max, "abs": abs,
                             "saturate": lambda x: max(0.0, min(1.0, x)),
                             "rsqrt": lambda x: f32(1.0 / math.sqrt(x))}
                if item.func.id in functions:
                    return functions[item.func.id](*(visit(arg) for arg in item.args))
            raise ValueError(f"Unsupported tangent expression: {ast.dump(item)}")
        return visit(node)

    def run(self, root, height: float) -> tuple[float, ...]:
        environment = {"root": root, "height": f32(height), "FLT_MIN": f32(1.17549435e-38)}
        for instruction in self.instructions:
            if instruction[0] == "assign":
                environment[instruction[1]] = self.evaluate(instruction[2], environment)
            elif instruction[0] == "guard":
                if self.evaluate(instruction[1], environment):
                    return self.evaluate(instruction[2], environment).values
            else:
                return self.evaluate(instruction[1], environment).values
        raise ValueError("Tangent did not return")


def root(slope=(0.0, 1.0, 0.0), wind=(1.2, -0.7), curvature=(0.12, -0.08),
         length=0.8, pivot=(0.0, 0.0, 0.0)):
    return SimpleNamespace(slopeDirection=normalize(Vec(*slope)), wind=Vec(*wind),
                           curvature=Vec(*curvature), height=f32(length), pivot=Vec(*pivot))


def unit64(values) -> tuple[float, ...]:
    magnitude = math.sqrt(sum(x * x for x in values))
    return tuple(x / magnitude for x in values)


def center64(blade, height: float) -> tuple[float, ...]:
    s, w, c = blade.slopeDirection.values, blade.wind.values, blade.curvature.values
    direction = unit64((s[0] + height * w[0], s[1], s[2] + height * w[1]))
    return tuple(blade.height * height * d + height * height * bend
                 for d, bend in zip(direction, (c[0], 0.0, c[1])))


def oracle(blade, height: float) -> tuple[float, ...]:
    # Five-point differences of the original curve, not the analytic formula.
    step = 1e-4
    samples = [center64(blade, height + offset * step) for offset in (-2, -1, 1, 2)]
    return unit64(tuple((a - 8.0 * b + 8.0 * c - d) / (12.0 * step)
                        for a, b, c, d in zip(*samples)))


def old_tangent(blade, height: float) -> tuple[float, ...]:
    # Written binary32 operations from the pre-change world-space difference.
    def direction(h):
        return normalize(blade.slopeDirection + Vec(blade.wind.values[0], 0.0, blade.wind.values[1]) * h)

    def center(h):
        result = blade.pivot + (direction(h) * h) * blade.height
        return result + Vec(blade.curvature.values[0], 0.0, blade.curvature.values[1]) * f32(h * h)

    low, high = max(0.0, f32(height - f32(0.01))), min(1.0, f32(height + f32(0.01)))
    tangent = center(high) - center(low)
    return (normalize(tangent) if dot(tangent, tangent) > f32(1e-12) else direction(height)).values


class BladeTangentTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.source = SOURCE.read_text(encoding="utf-8")
        cls.program = TangentProgram(cls.source)

    def assertVectorClose(self, actual, expected, tolerance=2e-5):
        for a, b in zip(actual, expected):
            self.assertTrue(math.isfinite(a))
            self.assertAlmostEqual(a, b, delta=tolerance)
        self.assertAlmostEqual(sum(x * x for x in actual), 1.0, delta=2e-5)

    def test_straight_blades_keep_the_support_direction(self):
        for slope in ((0, 1, 0), (0.5, 1, -0.5), (-1, 0.5, 1)):
            blade = root(slope=slope, wind=(0, 0), curvature=(0, 0))
            for height in (0.0, 0.1, 0.5, 1.0):
                self.assertVectorClose(self.program.run(blade, height), blade.slopeDirection.values)

    def test_analytic_tangent_matches_independent_curve_differences(self):
        rng = random.Random(36335)
        for index in range(256):
            blade = root(slope=(rng.uniform(-1, 1), rng.uniform(0.5, 1), rng.uniform(-1, 1)),
                         wind=(rng.uniform(-4, 4), rng.uniform(-4, 4)),
                         curvature=(rng.uniform(-0.5, 0.5), rng.uniform(-0.5, 0.5)),
                         length=rng.uniform(0.05, 3.0))
            for height in (0.0, 0.125, 0.5, 0.875, 1.0):
                with self.subTest(index=index, height=height):
                    self.assertVectorClose(self.program.run(blade, height), oracle(blade, height))

    def test_root_is_not_tilted_by_wind_or_curvature(self):
        for wind in ((-4, 3), (0, 0), (3, -4)):
            for curvature in ((-1, 1), (0, 0), (1, -1)):
                blade = root(slope=(0.3, 1.0, -0.2), wind=wind, curvature=curvature)
                self.assertVectorClose(self.program.run(blade, 0.0), blade.slopeDirection.values)

    def test_tip_uses_the_true_derivative_not_a_one_sided_chord(self):
        blade = root(wind=(3, -2), curvature=(-0.3, 0.4), length=0.5)
        expected = oracle(blade, 1.0)
        old_error = max(abs(a - b) for a, b in zip(old_tangent(blade, 1.0), expected))
        new_error = max(abs(a - b) for a, b in zip(self.program.run(blade, 1.0), expected))
        self.assertGreater(old_error, 1e-4)
        self.assertLess(new_error, old_error * 0.01)

    def test_translation_cannot_change_the_tangent(self):
        baseline = self.program.run(root(), 0.625)
        for pivot in ((1000, 2000, -3000), (1e6, -1e6, 1e6), (-1e9, 1e9, -1e9)):
            self.assertEqual(self.program.run(root(pivot=pivot), 0.625), baseline)

    def test_old_world_position_subtraction_reproduces_precision_loss(self):
        blade = root(pivot=(1e6, -1e6, 1e6))
        expected = oracle(blade, 0.625)
        self.assertGreater(max(abs(a - b) for a, b in zip(old_tangent(blade, 0.625), expected)), 0.05)
        self.assertVectorClose(self.program.run(blade, 0.625), expected)

    def test_uniform_shape_scaling_preserves_lighting(self):
        expected = self.program.run(root(), 0.625)
        for scale in (1e-18, 1e-12, 1e-6, 0.01, 1.0, 100.0, 1e6, 1e12, 1e18):
            blade = root(length=0.8 * scale, curvature=(0.12 * scale, -0.08 * scale))
            self.assertVectorClose(self.program.run(blade, 0.625), expected)

    def test_zero_size_falls_back_without_normalizing_zero(self):
        blade = root(length=0.0, curvature=(0, 0))
        for height in (0.0, 0.5, 1.0):
            s, w = blade.slopeDirection.values, blade.wind.values
            expected = unit64((s[0] + height * w[0], s[1], s[2] + height * w[1]))
            self.assertVectorClose(self.program.run(blade, height), expected)

    def test_curvature_only_blade_has_a_finite_tangent(self):
        blade = root(length=0.0, curvature=(0.3, -0.4))
        self.assertVectorClose(self.program.run(blade, 0.0), blade.slopeDirection.values)
        for height in (0.01, 0.5, 1.0):
            self.assertVectorClose(self.program.run(blade, height), (0.6, 0.0, -0.8))

    def test_height_clamping_preserves_endpoint_behavior(self):
        blade = root()
        self.assertEqual(self.program.run(blade, -1.0), self.program.run(blade, 0.0))
        self.assertEqual(self.program.run(blade, 2.0), self.program.run(blade, 1.0))

    def test_tangent_does_not_read_world_positions_or_sample_the_curve(self):
        tangent = body(self.source, "GrassTangentAtHeight")
        for forbidden in ("root.pivot", "GrassCenterAtHeight", "GrassDirectionAtHeight", "SAMPLE_TEXTURE"):
            self.assertNotIn(forbidden, tangent)
        self.assertNotRegex(tangent, r"height\s*[+-]\s*0\.01")

    def test_vertices_reuse_root_direction_and_keep_the_same_geometry_path(self):
        vertex = body(self.source, "BuildGrassVertex")
        self.assertRegex(vertex, r"blade\.rootTangentWS\s*=\s*half3\(root\.slopeDirection\)\s*;")
        self.assertEqual(vertex.count("GrassTangentAtHeight("), 1)
        self.assertIn("blade.tangentWS = GrassTangentAtHeight(root, blade.height);", vertex)
        self.assertIn("blade.positionWS = EvaluateGrassPosition(root, view, input.uv);", vertex)

    def test_oracle_rejects_missing_wind_derivative(self):
        mutated = self.source.replace("height * directionDerivative", "0.0 * directionDerivative")
        self.assertNotEqual(mutated, self.source)
        candidate = TangentProgram(mutated)
        blade = root(curvature=(0.0, 0.0))
        error = max(abs(a - b) for a, b in zip(candidate.run(blade, 0.5), oracle(blade, 0.5)))
        self.assertGreater(error, 0.05)

    def test_oracle_rejects_missing_curvature_factor(self):
        mutated = self.source.replace("(2.0 * height) * curvature", "height * curvature")
        self.assertNotEqual(mutated, self.source)
        candidate = TangentProgram(mutated)
        blade = root(wind=(0, 0), curvature=(0.5, -0.5))
        error = max(abs(a - b) for a, b in zip(candidate.run(blade, 0.5), oracle(blade, 0.5)))
        self.assertGreater(error, 0.05)


if __name__ == "__main__":
    unittest.main()
