using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Cone.Grass.Tests
{
    /// <summary>
    /// Runs the shipped placement kernel over ground that slopes through the water line (height = 0.1 x), and checks
    /// that no root grows at or below the water level and that the population follows the shoreline band.
    /// </summary>
    [Category("GrassGPU")]
    public sealed class GrassWaterLineGpuTests
    {
        private const string ComputePath = "Packages/com.cone.grass/Runtime/Compute/GrassPositionsCompute.compute";
        private const float Slope = 0.1f;
        private const float Spacing = 0.5f;
        private const float DrawDistance = 100f;
        private const int GridStart = -64;
        private const int GridCells = 128;
        private const int Capacity = GridCells * GridCells;
        private const int TextureSize = 256;

        private ComputeShader _shader;
        private GraphicsBuffer _positions;
        private GraphicsBuffer _counts;
        private GraphicsBuffer _arguments;
        private Texture2D _height;
        private Texture2D _white;
        private Texture2D _black;
        private int _resetKernel;
        private int _generateKernel;
        private int _finalizeKernel;

        [SetUp]
        public void SetUp()
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsIndirectArgumentsBuffer ||
                SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ||
                !SystemInfo.SupportsTextureFormat(TextureFormat.RGBAFloat))
            {
                Assert.Ignore("Grass GPU tests require compute, indirect arguments, and RGBAFloat texture support.");
            }

            ComputeShader source = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputePath);
            Assert.That(source, Is.Not.Null, "The installed package compute shader must import successfully.");
            _shader = Object.Instantiate(source);
            _resetKernel = _shader.FindKernel("ResetCounts");
            _generateKernel = _shader.FindKernel("CSMain");
            _finalizeKernel = _shader.FindKernel("FinalizeArgs");
            _positions = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Capacity * 3, sizeof(float) * 4);
            _counts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 6, sizeof(uint));
            _arguments = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 3, GraphicsBuffer.IndirectDrawIndexedArgs.size);
            _arguments.SetData(new GraphicsBuffer.IndirectDrawIndexedArgs[3]);

            // The captured surface spans the capture extent (the draw distance here) on each side of the origin; its
            // height rises 0.1 m per metre along X, which bilinear filtering reproduces exactly between texel centres.
            _height = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBAFloat, false, true)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            var pixels = new Color[TextureSize * TextureSize];
            for (int x = 0; x < TextureSize; x++)
            {
                float worldX = ((x + 0.5f) / TextureSize - 0.5f) * 2f * DrawDistance;
                for (int z = 0; z < TextureSize; z++)
                {
                    pixels[z * TextureSize + x] = new Color(Slope * worldX, 1f, 0f, 1f);
                }
            }

            _height.SetPixels(pixels);
            _height.Apply(false, false);
            _white = CreateTexture(Color.white);
            _black = CreateTexture(Color.black);

            _shader.SetTexture(_generateKernel, "_GrassHeightMapRT", _height);
            _shader.SetTexture(_generateKernel, "_GrassDensityRT", _white);
            _shader.SetTexture(_generateKernel, "_GrassMaskMapRT", _black);
            _shader.SetTexture(_generateKernel, "_TerrainHeightmap", _white);
            _shader.SetTexture(_generateKernel, "_TerrainHoles", _white);
            _shader.SetTexture(_generateKernel, "_GrassVariantWeightsRT0", _white);
            _shader.SetTexture(_generateKernel, "_GrassVariantWeightsRT1", _white);
            _shader.SetBuffer(_resetKernel, "_GrassCounts", _counts);
            _shader.SetBuffer(_generateKernel, "_GrassCounts", _counts);
            _shader.SetBuffer(_generateKernel, "_GrassPositions", _positions);
            _shader.SetBuffer(_finalizeKernel, "_GrassCounts", _counts);
            _shader.SetBuffer(_finalizeKernel, "_GrassIndirectArgs", _arguments);

            _shader.SetFloat("_Spacing", Spacing);
            _shader.SetFloat("_DrawDistance", DrawDistance);
            _shader.SetFloat("_TextureUpdateThreshold", 0f);
            _shader.SetFloat("_FullDensityDistance", DrawDistance);
            _shader.SetFloat("_DensityFalloffExponent", 1f);
            _shader.SetFloat("_DensityTransition", 0f);
            _shader.SetFloat("_BladeBoundsRadius", 1f);
            _shader.SetFloat("_LodTransitionWidth", 0f);
            _shader.SetVector("_CameraPosition", Vector4.zero);
            _shader.SetVector("_CenterPos", Vector4.zero);
            _shader.SetVector("_LodDistances", new Vector4(1000f, 2000f, 0f, 0f));
            _shader.SetInt("_AuthoredAreas", 1);
            _shader.SetInt("_DensityShortensBlades", 0);
            _shader.SetInt("_UseTerrain", 0);
            _shader.SetInt("_TerrainHasHoles", 0);
            _shader.SetInts("_LodCapacities", Capacity, Capacity, Capacity, 0);
            _shader.SetInts("_LodOffsets", 0, Capacity, Capacity * 2, 0);
            _shader.SetInt("_ArgsStride", GraphicsBuffer.IndirectDrawIndexedArgs.size);
            _shader.SetInt("_ArgsInstanceCountOffset", 4);
            var planes = new Vector4[6];
            for (int index = 0; index < planes.Length; index++)
            {
                planes[index] = new Vector4(0f, 0f, 0f, 1f);
            }

            _shader.SetVectorArray("_FrustumPlanes", planes);
        }

        [TearDown]
        public void TearDown()
        {
            _positions?.Dispose();
            _counts?.Dispose();
            _arguments?.Dispose();
            foreach (Object owned in new Object[] { _shader, _height, _white, _black })
            {
                if (owned)
                {
                    Object.DestroyImmediate(owned);
                }
            }
        }

        [Test]
        public void DisabledWaterLineLeavesPlacementUnchanged()
        {
            Dictionary<Vector2Int, Vector4> unset = Generate();
            SetWater(false, 2.5f, 1f);
            Dictionary<Vector2Int, Vector4> disabled = Generate();

            Assert.That(unset.Count, Is.EqualTo(Capacity), "Without water every cell of the sloped ground grows a root.");
            CollectionAssert.AreEquivalent(unset.Keys, disabled.Keys);
        }

        [TestCase(0.7f, 0.5f)]
        [TestCase(-1.3f, 0f)]
        [TestCase(1.5f, 1f)]
        public void NoRootGrowsAtOrBelowTheWaterLevel(float level, float shoreFade)
        {
            Dictionary<Vector2Int, Vector4> dry = Generate();
            SetWater(true, level, shoreFade);
            Dictionary<Vector2Int, Vector4> wet = Generate();

            int below = 0;
            foreach (Vector4 root in wet.Values)
            {
                if (root.y <= level)
                {
                    below++;
                }
            }

            Assert.That(below, Is.Zero, $"{below} roots grew at or below the water level {level}.");
            Assert.That(wet.Count, Is.GreaterThan(0));

            // Above the shoreline band nothing changes: every dry root there is still placed, at the same spot.
            int above = 0;
            foreach (KeyValuePair<Vector2Int, Vector4> pair in dry)
            {
                if (pair.Value.y > level + shoreFade + 1e-3f)
                {
                    above++;
                    Assert.That(wet.TryGetValue(pair.Key, out Vector4 kept), Is.True, $"Cell {pair.Key} lost its root above the band.");
                    Assert.That(kept, Is.EqualTo(pair.Value));
                }
            }

            Assert.That(above, Is.GreaterThan(1000));
            // Below the water the dry run had roots, so the cut is the water line's doing.
            Assert.That(CountAtOrBelow(dry.Values, level), Is.GreaterThan(1000));
        }

        [Test]
        public void BladeDensityFollowsTheShorelineBand()
        {
            const float Level = 0.5f, Band = 2f;
            const int Bins = 4;
            Dictionary<Vector2Int, Vector4> dry = Generate();
            SetWater(true, Level, Band);
            Dictionary<Vector2Int, Vector4> wet = Generate();

            var dryCount = new int[Bins];
            var wetCount = new int[Bins];
            var expected = new double[Bins];
            foreach (KeyValuePair<Vector2Int, Vector4> pair in dry)
            {
                float above = pair.Value.y - Level;
                if (above <= 0f || above >= Band)
                {
                    continue;
                }

                int bin = Mathf.Min(Bins - 1, (int)(above / Band * Bins));
                dryCount[bin]++;
                expected[bin] += GrassWaterLine.Coverage(pair.Value.y, Level, Band);
                if (wet.TryGetValue(pair.Key, out Vector4 kept))
                {
                    Assert.That(kept, Is.EqualTo(pair.Value), "A kept root must not move.");
                    wetCount[bin]++;
                }
            }

            foreach (Vector4 root in wet.Values)
            {
                Assert.That(root.y, Is.GreaterThan(Level));
            }

            double previous = 0.0;
            for (int bin = 0; bin < Bins; bin++)
            {
                Assert.That(dryCount[bin], Is.GreaterThan(1000), $"Bin {bin} needs enough candidates to measure.");
                double fraction = (double)wetCount[bin] / dryCount[bin];
                double target = expected[bin] / dryCount[bin];
                // Binomial standard error is at most 0.5 / sqrt(1000) = 0.016; 0.05 is over three of them.
                Assert.That(fraction, Is.EqualTo(target).Within(0.05),
                    $"Bin {bin} ({bin * Band / Bins:F1}-{(bin + 1) * Band / Bins:F1} m above the water) kept {fraction:P1}; the band expects {target:P1}.");
                Assert.That(fraction, Is.GreaterThan(previous), "Density must rise away from the water.");
                previous = fraction;
            }
        }

        [Test]
        public void ManagedCoverageMatchesTheKernelsBand()
        {
            Assert.That(GrassWaterLine.Coverage(1f, 1f, 0.5f), Is.Zero, "At the level itself nothing grows.");
            Assert.That(GrassWaterLine.Coverage(0.5f, 1f, 0.5f), Is.Zero);
            Assert.That(GrassWaterLine.Coverage(1.25f, 1f, 0.5f), Is.EqualTo(0.5f).Within(1e-6f));
            Assert.That(GrassWaterLine.Coverage(1.5f, 1f, 0.5f), Is.EqualTo(1f));
            Assert.That(GrassWaterLine.Coverage(1.0001f, 1f, 0f), Is.EqualTo(1f), "Zero band is a hard line.");
            Assert.That(GrassWaterLine.Coverage(float.NaN, 1f, 0.5f), Is.Zero);
        }

        private void SetWater(bool enabled, float level, float shoreFade)
        {
            _shader.SetInt("_GrassWaterEnabled", enabled ? 1 : 0);
            _shader.SetFloat("_GrassWaterLevel", level);
            _shader.SetFloat("_GrassWaterShoreFade", shoreFade);
        }

        private Dictionary<Vector2Int, Vector4> Generate()
        {
            _shader.SetInts("_GridStartIndex", GridStart, GridStart);
            _shader.SetInts("_GridSize", GridCells, GridCells);
            _shader.Dispatch(_resetKernel, 1, 1, 1);
            _shader.Dispatch(_generateKernel, (GridCells + 7) / 8, (GridCells + 7) / 8, 1);
            _shader.Dispatch(_finalizeKernel, 1, 1, 1);

            var counts = new uint[6];
            _counts.GetData(counts);
            Assert.That(counts[1] + counts[2] + counts[3], Is.Zero, "Every root belongs in the near queue, within capacity.");
            var roots = new Vector4[(int)counts[0]];
            _positions.GetData(roots, 0, 0, roots.Length);
            var byCell = new Dictionary<Vector2Int, Vector4>(roots.Length);
            foreach (Vector4 root in roots)
            {
                var cell = new Vector2Int(Mathf.FloorToInt(root.x / Spacing + 0.5f), Mathf.FloorToInt(root.z / Spacing + 0.5f));
                Assert.That(byCell.ContainsKey(cell), Is.False, $"Cell {cell} was placed twice.");
                byCell.Add(cell, root);
            }

            return byCell;
        }

        private static int CountAtOrBelow(IEnumerable<Vector4> roots, float level)
        {
            int count = 0;
            foreach (Vector4 root in roots)
            {
                if (root.y <= level)
                {
                    count++;
                }
            }

            return count;
        }

        private static Texture2D CreateTexture(Color value)
        {
            var texture = new Texture2D(4, 4, TextureFormat.RGBAFloat, false, true)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            var pixels = new Color[16];
            for (int index = 0; index < pixels.Length; index++)
            {
                pixels[index] = value;
            }

            texture.SetPixels(pixels);
            texture.Apply(false, false);
            return texture;
        }
    }
}
