"""Terrain filtering source/math controls; not Unity, HLSL execution or GPU timing.

Interpret the actual C#/HLSL scalar helper expressions with binary32 rounding.
Texture sampling and native half precision remain outside this CPU model.
"""
from __future__ import annotations

import ast
import math
from pathlib import Path
import random
import re
import struct
import unittest

ROOT = Path(__file__).resolve().parents[2]
COMMON = ROOT / "Runtime/Shaders/GrassBladeCommon.hlsl"
BINDING = ROOT / "Runtime/Rendering/GrassTerrainSplat.cs"


def clean(source: str) -> str:
    return re.sub(r"//[^\n]*|/\*.*?\*/", "", source, flags=re.S)


def body(source: str, name: str) -> str:
    match = re.search(r"\b\w+\s+" + re.escape(name) + r"\s*\([^{};]*\)\s*\{", clean(source))
    if match is None:
        raise ValueError(f"Missing function: {name}")
    source, start, depth = clean(source), match.end(), 1
    for end in range(start, len(source)):
        depth += (source[end] == "{") - (source[end] == "}")
        if depth == 0:
            return source[start:end]
    raise ValueError(f"Unclosed function: {name}")


def returned(source: str, name: str) -> str:
    match = re.fullmatch(r"\s*return\s+([^;]+);\s*", body(source, name))
    if match is None:
        raise ValueError(f"Expected a scalar return expression: {name}")
    return match[1]


def f32(value: float) -> float:
    return struct.unpack("<f", struct.pack("<f", value))[0]


def identifier(node: ast.AST) -> str:
    if isinstance(node, ast.Name):
        return node.id
    if isinstance(node, ast.Attribute):
        return identifier(node.value) + "." + node.attr
    raise ValueError("Expected a bounded variable/function name")


def scalar(expression: str, values: dict[str, float]) -> float | bool:
    expression = re.sub(r"(?<=\d)[fh]\b", "", " ".join(expression.split()))
    variables = {key: f32(value) for key, value in values.items()}
    variables["FLT_MIN"] = f32(1.17549435e-38)

    def visit(node: ast.AST):
        if isinstance(node, ast.Constant) and type(node.value) in (int, float):
            return f32(node.value)
        if isinstance(node, (ast.Name, ast.Attribute)):
            return variables[identifier(node)]
        if isinstance(node, ast.BinOp):
            left, right = visit(node.left), visit(node.right)
            operations = {ast.Add: lambda: left + right, ast.Sub: lambda: left - right,
                          ast.Mult: lambda: left * right, ast.Div: lambda: left / right}
            if type(node.op) in operations:
                return f32(operations[type(node.op)]())
        if isinstance(node, ast.Call) and not node.keywords:
            functions = {"max": max, "Mathf.Max": max, "log2": math.log2, "Mathf.Log": math.log}
            name = identifier(node.func)
            if name in functions:
                return f32(functions[name](*(visit(arg) for arg in node.args)))
        if isinstance(node, ast.Compare) and len(node.ops) == 1 and isinstance(node.ops[0], ast.LtE):
            return visit(node.left) <= visit(node.comparators[0])
        raise ValueError(f"Unsupported scalar expression: {ast.dump(node)}")

    return visit(ast.parse(expression, mode="eval").body)


class TerrainSplatTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.common = clean(COMMON.read_text(encoding="utf-8"))
        cls.binding = clean(BINDING.read_text(encoding="utf-8"))
        cls.bias = returned(cls.binding, "TextureLodBias")
        cls.pixel = returned(cls.common, "GrassSplatPixelLod")
        cls.mip = returned(cls.common, "GrassSplatMipLevel")

    def lod(self, width, height, tile_x, tile_y, pixel):
        bias = scalar(self.bias, {"texture.width": width, "texture.height": height,
                                  "tileSize.x": tile_x, "tileSize.y": tile_y})
        footprint = scalar(self.pixel, {"worldUnitsPerPixel": pixel})
        return scalar(self.mip, {"pixelLod": footprint, "textureLodBias": bias})

    def test_square_maps_preserve_the_old_lod(self):
        for size in (32, 256, 1024, 4096):
            for tile in (0.25, 1.0, 8.0, 64.0):
                for pixel in (1e-6, 0.001, 0.125, 1.0, 10.0):
                    expected = max(0.0, math.log2(max(pixel * size / tile, 1e-6)))
                    self.assertAlmostEqual(self.lod(size, size, tile, tile, pixel), expected, delta=4e-6)

    def test_rectangular_map_uses_its_detailed_axis(self):
        self.assertEqual(self.lod(128, 1024, 8.0, 8.0, 0.125), 4.0)
        self.assertEqual(self.lod(1024, 128, 8.0, 8.0, 0.125), 4.0)

    def test_unequal_tiling_uses_both_axes(self):
        self.assertEqual(self.lod(512, 512, 32.0, 2.0, 0.125), 5.0)
        self.assertEqual(self.lod(512, 512, 2.0, 32.0, 0.125), 5.0)

    def test_normal_resolution_has_an_independent_level(self):
        diffuse = self.lod(1024, 1024, 4.0, 4.0, 1.0 / 64.0)
        normal = self.lod(256, 256, 4.0, 4.0, 1.0 / 64.0)
        self.assertEqual((diffuse, normal), (2.0, 0.0))

    def test_old_width_only_control_reproduces_three_mip_error(self):
        old = max(0.0, math.log2(0.125 * 128 / 8.0))
        self.assertEqual(old, 1.0)
        self.assertEqual(self.lod(128, 1024, 8.0, 8.0, 0.125) - old, 3.0)

    def test_axis_swap_invariance(self):
        rng = random.Random(36335)
        for _ in range(128):
            width, height = rng.randint(1, 4096), rng.randint(1, 4096)
            x, y, pixel = rng.uniform(0.01, 100), rng.uniform(0.01, 100), rng.uniform(0.001, 1)
            self.assertEqual(self.lod(width, height, x, y, pixel), self.lod(height, width, y, x, pixel))

    def test_split_log_matches_independent_footprint_reference(self):
        rng = random.Random(7933)
        for index in range(1024):
            width, height = rng.randint(1, 8192), rng.randint(1, 8192)
            x, y, pixel = [f32(2 ** rng.uniform(-12, 12)) for _ in range(3)]
            expected = max(0.0, math.log2(max(width * pixel / x, height * pixel / y)))
            with self.subTest(index=index):
                self.assertAlmostEqual(self.lod(width, height, x, y, pixel), expected, delta=5e-6)

    def test_zero_and_small_footprints_stay_at_base_mip(self):
        for pixel in (0.0, 1e-40, 1e-30, 1e-20):
            value = self.lod(8192, 8192, 0.0001, 0.0001, pixel)
            self.assertTrue(math.isfinite(value))
            self.assertEqual(value, 0.0)

    def test_uniform_world_scaling_preserves_the_footprint(self):
        for scale in (1e-4, 0.1, 1.0, 100.0, 1e6):
            self.assertAlmostEqual(self.lod(256, 1024, 8 * scale, 16 * scale, 0.25 * scale), 4.0, delta=3e-6)

    def test_fractional_mips_are_not_quantized(self):
        self.assertAlmostEqual(self.lod(256, 256, 8, 8, math.sqrt(2) / 8), 2.5, delta=2e-6)

    def test_zero_weight_guard_precedes_all_layer_work(self):
        layer = body(self.common, "GrassSplatLayer")
        guard = re.match(r"\s*UNITY_BRANCH\s+if\s*\(([^)]+)\)\s*return;", layer)
        self.assertIsNotNone(guard)
        self.assertTrue(scalar(guard[1], {"weight": 0.0}))
        for weight in (1e-8, 1e-5, 0.001, 0.5, 1.0):
            self.assertFalse(scalar(guard[1], {"weight": weight}))
        self.assertEqual(layer.count("SAMPLE_TEXTURE2D_LOD("), 2)
        self.assertIn("GrassSplatMipLevel(pixelLod, lodBias.x)", layer)
        self.assertIn("GrassSplatMipLevel(pixelLod, lodBias.y)", layer)
        self.assertIn("normalSampler, layerUV, normalLod)", layer)
        self.assertIn("diffuseSampler, layerUV, diffuseLod)", layer)
        self.assertNotIn("log2(", layer)

    def test_empty_control_returns_to_capture_before_layer_work(self):
        terrain = body(self.common, "GrassTerrainSplat")
        self.assertIn("half weightSum = dot(control, 1.0h);", terrain)
        guard = re.search(r"UNITY_BRANCH\s+if\s*\((weightSum[^)]+)\)\s*return false;", terrain)
        self.assertIsNotNone(guard)
        self.assertTrue(scalar(guard[1], {"weightSum": 0.0}))
        self.assertFalse(scalar(guard[1], {"weightSum": 1e-8}))
        self.assertLess(guard.end(), terrain.index("GrassSplatPixelLod("))
        self.assertLess(guard.end(), terrain.index("GrassSplatLayer("))
        self.assertIn("control /= max(weightSum, 0.0001h);", terrain)

    def test_four_layers_share_one_camera_logarithm(self):
        terrain = body(self.common, "GrassTerrainSplat")
        self.assertEqual(terrain.count("GrassSplatPixelLod("), 1)
        self.assertEqual(terrain.count("GrassSplatLayer("), 4)
        for index, channel in enumerate("rgba"):
            self.assertIn(f"_GrassSplatLodBias[{index}].xy, control.{channel}, positionWS, pixelLod", terrain)
        self.assertNotIn("_GrassSplatWidths", self.common + self.binding)

    def test_binding_uses_actual_diffuse_and_normal_textures(self):
        binding = body(self.binding, "Bind")
        self.assertIn("TextureLodBias(diffuse, tileSize), TextureLodBias(normal, tileSize)", binding)
        self.assertIn("properties.SetTexture(Diffuse[i], diffuse);", binding)
        self.assertIn("properties.SetTexture(Normal[i], normal);", binding)
        self.assertIn("properties.SetVectorArray(LodBias, lodBiases);", binding)
        self.assertIn('Shader.PropertyToID("_GrassSplatLodBias")', self.binding)
        self.assertIn("float4 _GrassSplatLodBias[4];", self.common)
        self.assertIn("private const int Layers = 4;", self.binding)
        self.assertIn("private static readonly Vector4[] lodBiases = new Vector4[Layers];", self.binding)
        self.assertNotIn("new Vector4[", binding)
        self.assertIn("Texture2D.whiteTexture", binding)
        self.assertIn("Texture2D.normalTexture", binding)

    def test_sparse_sampling_model_keeps_all_positive_contributions(self):
        guard = re.match(r"\s*UNITY_BRANCH\s+if\s*\(([^)]+)\)\s*return;", body(self.common, "GrassSplatLayer"))[1]
        samples = (0.2, 0.7, 0.1, 0.9)
        for mask in range(16):
            weights = tuple((i + 1) * 0.1 if mask & (1 << i) else 0.0 for i in range(4))
            active = tuple(not scalar(guard, {"weight": w}) for w in weights)
            dense = sum(w * sample for w, sample in zip(weights, samples))
            sparse = sum(w * sample for w, sample, keep in zip(weights, samples, active) if keep)
            self.assertEqual(sparse, dense)
            self.assertEqual(sum(active), bin(mask).count("1"))

    def test_material_buffer_layout_is_unchanged(self):
        expected = """half4 _Color; half4 _AOColor; float _GrassWidth; float _GrassHeight;
        float _GrassCurving; float _GrassWidthRandomness; float _GrassHeightRandomness;
        float _ExpandDistantGrassWidth; float4 _ExpandDistantGrassRange; float4 _WindTexture_ST;
        float _WindStrength; float4 _WindScroll; half _RandomNormal; half _GroundBlendStrength;
        float _GroundBlendHeight; half _GroundBlendFloor; half _GroundAlbedoAlongBlade;
        half _TerrainSplatAtRoots; half _CanopyOcclusion; float _GrassSpacing; float _UseGroundNormal;
        float _UseAdditionalLights; float _SpecularFadeStart; float _SpecularFadeEnd;
        float _MinimumPixelWidth; float _GrassAlphaToCoverage; float _MaxSubdivision;
        float _SubdivisionDistance; float _SubdivisionHeightBoost; float _SubdivisionBumpWidth;
        float _FullDensityDistance; float _DensityFalloffExponent;"""
        actual = re.search(r"CBUFFER_START\(UnityPerMaterial\)(.*?)CBUFFER_END", self.common, re.S)[1]
        self.assertEqual(actual.split(), expected.split())


if __name__ == "__main__":
    unittest.main()
