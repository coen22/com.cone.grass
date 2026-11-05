using System;
using NUnit.Framework;
using UnityEngine;

namespace Cone.Grass.Tests
{
    /// <summary>The grass water line keeps mesh kinds above the water; the wind leans them, from ConeWind when it is installed.</summary>
    [Category("GrassGPU")]
    public sealed class GrassMeshDetailWaterWindTests : GrassMeshDetailFixture
    {
        // A slope along x: 0 m at x = 0 to 16 m at x = 128.
        private static float Slope(int x, int y) => x * 16f / 128f;

        private CameraReadback CountStrip(float x0, float x1)
        {
            GrassMeshDetailRenderer.StatsRect = new Vector4(x0, 8f, x1, 120f);
            return Render();
        }

        [Test]
        public void NoInstanceGrowsAtOrBelowTheWaterLevel()
        {
            CreateTerrain(1, DetailScatterMode.InstanceCountMode, Slope);
            Paint(0, (x, y) => 4);
            CreateDetails(Kind(400f));
            Details.UseGrassWaterLine = false;
            Details.WaterLine.Enabled = true;
            Details.WaterLine.Level = 6f;     // the shore is at x = 48
            Details.WaterLine.ShoreFade = 1f; // full density from x = 56
            Look(new Vector3(64f, 60f, 64f), new Vector3(64f, 0f, 64.01f));

            Assert.That(CountStrip(0f, 47.9f).Stats[0], Is.Zero, "nothing under the water");
            float band = CountStrip(48f, 56f).Stats[0];
            float full = CountStrip(64f, 72f).Stats[0];
            // Over the band, coverage rises linearly from 0 to 1: half the full density on average.
            Assert.That(band / full, Is.EqualTo(0.5f).Within(0.05f));

            CameraReadback readback = Render();
            for (int list = 0; list < readback.KindLodCount[0] * 2; list++)
            {
                foreach ((int slot, _) in Entries(readback, readback.KindListBase[0] + list, list % 2 == 1))
                {
                    Assert.That(InstanceMatrix(readback, slot).m13, Is.GreaterThan(6f));
                }
            }
        }

        [Test]
        public void TheGrassRenderersWaterLineAppliesToMeshKindsToo()
        {
            CreateTerrain(1, DetailScatterMode.InstanceCountMode, Slope);
            Paint(0, (x, y) => 4);
            CreateDetails(Kind(400f));
            var grassObject = new GameObject("Mesh Detail Test Grass");
            Created.Add(grassObject);
            InfiniteGrassRenderer grass = grassObject.AddComponent<InfiniteGrassRenderer>();
            grass.WaterLine.Enabled = true;
            grass.WaterLine.Level = 6f;
            grass.WaterLine.ShoreFade = 0f;
            Look(new Vector3(64f, 60f, 64f), new Vector3(64f, 0f, 64.01f));
            Assert.That(CountStrip(0f, 47.9f).Stats[0], Is.Zero);
            Assert.That(CountStrip(48.2f, 60f).Stats[0], Is.GreaterThan(1000u));

            Details.UseGrassWaterLine = false;
            Assert.That(CountStrip(0f, 47.9f).Stats[0], Is.GreaterThan(1000u), "the component's own (disabled) water line is used");
        }

        [Test]
        public void TheWindLeansInstancesAlongConeWindsDirection()
        {
            CreateTerrain(1, DetailScatterMode.InstanceCountMode);
            Paint(0, (x, y) => 1);
            GrassMeshKind kind = Kind(400f);
            kind.WindBend = 0.4f;
            kind.WindFlutter = 0f;
            CreateDetails(kind);
            Details.Wind.Source = GrassMeshWindSource.Scene;
            Details.OcclusionCulling = false;
            GrassConeWind.OverrideAvailable = true;
            Look(new Vector3(64f, 60f, 64f), new Vector3(64f, 0f, 64.01f));

            foreach (Vector3 direction in new[] { Vector3.right, Vector3.forward, new Vector3(-0.6f, 0f, 0.8f) })
            {
                const float strength = 0.5f;
                PublishConeWind(direction, strength);
                CameraReadback readback = Render();
                int checkedCount = 0;
                foreach ((int slot, _) in Entries(readback, readback.KindListBase[0], false))
                {
                    Vector4 up = InstanceMatrix(readback, slot).GetColumn(1);
                    var lean = new Vector3(up.x, 0f, up.z);
                    // The lean is the wind's direction times bend x strength x (0.5 + gust), gust in [0, 1].
                    Assert.That(Vector3.Angle(lean, direction), Is.LessThan(0.5f));
                    float ratio = lean.magnitude / (kind.WindBend * strength);
                    Assert.That(ratio, Is.InRange(0.5f - 1e-3f, 1.5f + 1e-3f));
                    checkedCount++;
                }

                Assert.That(checkedCount, Is.GreaterThan(1000));
            }

            // No wind published: upright.
            PublishConeWind(Vector3.zero, 0f);
            CameraReadback still = Render();
            foreach ((int slot, _) in Entries(still, still.KindListBase[0], false))
            {
                Vector4 up = InstanceMatrix(still, slot).GetColumn(1);
                Assert.That(new Vector2(up.x, up.z).magnitude, Is.LessThan(1e-5f));
            }
        }

        [Test]
        public void OwnWindIsUsedWhenConeWindIsNotInstalled()
        {
            CreateTerrain(1, DetailScatterMode.InstanceCountMode);
            Paint(0, (x, y) => 1);
            GrassMeshKind kind = Kind(400f);
            kind.WindBend = 0.4f;
            kind.WindFlutter = 0f;
            CreateDetails(kind);
            GrassConeWind.OverrideAvailable = false;
            Details.Wind.Source = GrassMeshWindSource.Auto;
            Details.Wind.Direction = 90f; // towards +x
            Details.Wind.Main = 0.5f;
            Details.Wind.PulseMagnitude = 0f;
            Details.OcclusionCulling = false;
            Look(new Vector3(64f, 60f, 64f), new Vector3(64f, 0f, 64.01f));
            CameraReadback readback = Render();
            int count = 0;
            foreach ((int slot, _) in Entries(readback, readback.KindListBase[0], false))
            {
                Vector4 up = InstanceMatrix(readback, slot).GetColumn(1);
                Assert.That(Vector3.Angle(new Vector3(up.x, 0f, up.z), Vector3.right), Is.LessThan(0.5f));
                count++;
            }

            Assert.That(count, Is.GreaterThan(1000));
        }

        [Test]
        public void TheSceneWindZoneDrivesTheLeanThroughConeWind()
        {
            Type coneWind = Type.GetType("Cone.RenderFeatures.ConeWind, Cone.RenderFeatures.Core");
            if (coneWind == null)
            {
                Assert.Ignore("com.cone.render-features with ConeWind (render-features PR #73) is not installed in this project.");
            }

            CreateTerrain(1, DetailScatterMode.InstanceCountMode);
            Paint(0, (x, y) => 1);
            GrassMeshKind kind = Kind(400f);
            kind.WindBend = 0.4f;
            kind.WindFlutter = 0f;
            CreateDetails(kind);
            Details.Wind.Source = GrassMeshWindSource.Auto;
            Details.OcclusionCulling = false;
            var zoneObject = new GameObject("Mesh Detail Test WindZone");
            Created.Add(zoneObject);
            WindZone zone = zoneObject.AddComponent<WindZone>();
            zone.mode = WindZoneMode.Directional;
            zone.windMain = 0.6f;
            zone.windTurbulence = 0f;
            zone.windPulseMagnitude = 0f;
            zoneObject.transform.rotation = Quaternion.LookRotation(new Vector3(0f, 0f, -1f));
            coneWind.GetMethod("Invalidate")?.Invoke(null, null);
            coneWind.GetMethod("Refresh")?.Invoke(null, null);
            Assert.That(GrassConeWind.IsAvailable, Is.True);
            Look(new Vector3(64f, 60f, 64f), new Vector3(64f, 0f, 64.01f));
            CameraReadback readback = Render();
            int count = 0;
            foreach ((int slot, _) in Entries(readback, readback.KindListBase[0], false))
            {
                Vector4 up = InstanceMatrix(readback, slot).GetColumn(1);
                Assert.That(Vector3.Angle(new Vector3(up.x, 0f, up.z), Vector3.back), Is.LessThan(0.5f));
                count++;
            }

            Assert.That(count, Is.GreaterThan(1000));
        }

        private static void PublishConeWind(Vector3 direction, float strength)
        {
            Vector3 d = direction.sqrMagnitude > 0f ? direction.normalized : Vector3.zero;
            Shader.SetGlobalVector("_ConeWindDirection", new Vector4(d.x, d.y, d.z, strength));
            Shader.SetGlobalVector("_ConeWindParams", new Vector4(0f, 0f, 0f, strength));
            Shader.SetGlobalVector("_ConeWindOffset", Vector4.zero);
        }
    }
}
