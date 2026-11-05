using NUnit.Framework;
using UnityEngine;

namespace Cone.Grass.Tests
{
    /// <summary>
    /// The scatter places as many instances per square metre as the terrain's detail density asks for: checked against
    /// Unity's own placement (<see cref="TerrainData.ComputeDetailInstanceTransforms"/>) over the same cells, within 5 %.
    /// </summary>
    [Category("GrassGPU")]
    public sealed class GrassMeshDetailDensityTests : GrassMeshDetailFixture
    {
        private const float Tolerance = 0.05f;

        [TestCase(DetailScatterMode.InstanceCountMode, 1, 1f)]
        [TestCase(DetailScatterMode.InstanceCountMode, 3, 1f)]
        [TestCase(DetailScatterMode.InstanceCountMode, 8, 0.5f)]
        [TestCase(DetailScatterMode.CoverageMode, 64, 1f)]
        [TestCase(DetailScatterMode.CoverageMode, 160, 1f)]
        [TestCase(DetailScatterMode.CoverageMode, 255, 0.5f)]
        public void InstancesPerSquareMetreFollowTheDensityLayer(DetailScatterMode mode, int value, float terrainDensity)
        {
            CreateTerrain(1, mode);
            DetailPrototype[] prototypes = Data.detailPrototypes;
            prototypes[0].density = 2.5f;
            Data.detailPrototypes = prototypes;
            Terrain.detailObjectDensity = terrainDensity;
            Paint(0, (x, y) => value);
            GrassMeshKind kind = Kind(400f);
            kind.Density = 2.5f;
            CreateDetails(kind);

            // A 48 m square in the middle of the terrain, every cell of it in reach.
            var rect = new Vector4(40f, 40f, 88f, 88f);
            GrassMeshDetailRenderer.StatsRect = rect;
            Look(new Vector3(64f, 30f, 64f), new Vector3(64f, 0f, 64.01f));
            CameraReadback readback = Render();

            float area = (rect.z - rect.x) * (rect.w - rect.y);
            float gpuPerM2 = readback.Stats[0] / area;
            float unityPerM2 = UnityCount(rect, terrainDensity) / area;
            float expected = GrassMeshDetailDensity.ExpectedInstancesPerCell(mode, value,
                GrassMeshDetailDensity.SliceScale(kind, Terrain, mode, 1f), 1f);
            TestContext.WriteLine($"{mode} value {value} density x{terrainDensity}: GPU {gpuPerM2:F4}/m2, Unity {unityPerM2:F4}/m2, rule {expected:F4}/m2");
            Assert.That(unityPerM2, Is.GreaterThan(0f), "Unity's own placement found nothing: the reference is broken.");
            Assert.That(gpuPerM2 / unityPerM2, Is.InRange(1f - Tolerance, 1f + Tolerance));
        }

        [Test]
        public void ADensityGradientIsFollowedCellByCell()
        {
            CreateTerrain(1, DetailScatterMode.InstanceCountMode);
            // Columns of 16 cells with 0, 1, 2 ... 7 instances per cell.
            Paint(0, (x, y) => x / 16);
            CreateDetails(Kind(400f));
            Look(new Vector3(64f, 30f, 64f), new Vector3(64f, 0f, 64.01f));
            for (int band = 0; band < 8; band++)
            {
                var rect = new Vector4(band * 16f, 16f, band * 16f + 16f, 112f);
                GrassMeshDetailRenderer.StatsRect = rect;
                CameraReadback readback = Render();
                float area = 16f * 96f;
                float perM2 = readback.Stats[0] / area;
                float unity = UnityCount(rect, 1f) / area;
                TestContext.WriteLine($"band {band}: GPU {perM2:F3}/m2, Unity {unity:F3}/m2");
                if (band == 0)
                {
                    Assert.That(readback.Stats[0], Is.Zero);
                    Assert.That(unity, Is.Zero);
                    continue;
                }

                Assert.That(perM2 / unity, Is.InRange(1f - Tolerance, 1f + Tolerance));
            }
        }

        [Test]
        public void TheRuleMatchesUnityForEveryValueOfBothModes()
        {
            // The C# rule the kernel mirrors, against Unity's placement on a single-value terrain, for many values.
            foreach (DetailScatterMode mode in new[] { DetailScatterMode.InstanceCountMode, DetailScatterMode.CoverageMode })
            {
                CreateTerrain(1, mode);
                DetailPrototype[] prototypes = Data.detailPrototypes;
                prototypes[0].density = 1.5f;
                Data.detailPrototypes = prototypes;
                var kind = new GrassMeshKind { Density = 1.5f };
                foreach (int value in mode == DetailScatterMode.CoverageMode ? new[] { 32, 96, 200, 255 } : new[] { 1, 2, 5, 12 })
                {
                    Paint(0, (x, y) => value);
                    var rect = new Vector4(0f, 0f, TerrainSize, TerrainSize);
                    float unity = UnityCount(rect, 1f) / (TerrainSize * TerrainSize);
                    float rule = GrassMeshDetailDensity.ExpectedInstancesPerCell(mode, value,
                        GrassMeshDetailDensity.SliceScale(kind, Terrain, mode, 1f), (float)TerrainSize / DetailResolution * TerrainSize / DetailResolution);
                    TestContext.WriteLine($"{mode} {value}: Unity {unity:F4}/m2, rule {rule:F4}/m2");
                    Assert.That(rule / unity, Is.InRange(1f - Tolerance, 1f + Tolerance), $"{mode} value {value}");
                }

                TearDownWorld();
                SetUpWorld();
            }
        }

        private float UnityCount(Vector4 rect, float density)
        {
            int count = 0;
            int patches = Data.detailPatchCount;
            for (int py = 0; py < patches; py++)
            {
                for (int px = 0; px < patches; px++)
                {
                    DetailInstanceTransform[] transforms = Data.ComputeDetailInstanceTransforms(px, py, 0, density, out _);
                    foreach (DetailInstanceTransform t in transforms)
                    {
                        if (t.posX >= rect.x && t.posX < rect.z && t.posZ >= rect.y && t.posZ < rect.w)
                        {
                            count++;
                        }
                    }
                }
            }

            return count;
        }
    }
}
