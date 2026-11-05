using NUnit.Framework;
using UnityEngine;

namespace Cone.Grass.Tests
{
    /// <summary>Frustum, Hi-Z occlusion and shadow-cascade culling of mesh kinds, read back from the GPU lists.</summary>
    [Category("GrassGPU")]
    public sealed class GrassMeshDetailCullingTests : GrassMeshDetailFixture
    {
        [Test]
        public void InstancesBehindTheCameraAreCulled()
        {
            CreateTerrain(1, DetailScatterMode.InstanceCountMode);
            Paint(0, (x, y) => 2);
            CreateDetails(Kind(400f));
            GrassMeshDetailRenderer.StatsRect = new Vector4(0f, 0f, TerrainSize, TerrainSize);
            // Looking up and away: nothing of the terrain is in the frustum.
            Look(new Vector3(64f, 2f, 64f), new Vector3(64f, 200f, 300f));
            CameraReadback readback = Render();
            Assert.That(readback.Stats[0], Is.GreaterThan(30000u), "The terrain must be populated for the test to mean anything.");
            Assert.That(CameraCount(readback, 0), Is.Zero);
        }

        [Test]
        public void EveryVisibleInstanceIsInTheFrustumAndOffScreenOnesAreNot()
        {
            CreateTerrain(1, DetailScatterMode.InstanceCountMode);
            Paint(0, (x, y) => 2);
            CreateDetails(Kind(400f));
            GrassMeshDetailRenderer.StatsRect = new Vector4(0f, 0f, TerrainSize, TerrainSize);
            Look(new Vector3(10f, 6f, 10f), new Vector3(40f, 0f, 40f), 40f);
            CameraReadback readback = Render();
            var planes = GeometryUtility.CalculateFrustumPlanes(Camera);
            const float radius = 0.75f; // the plant's bounding sphere plus margin
            int visible = 0;
            foreach ((int slot, _) in Entries(readback, readback.KindListBase[0], false))
            {
                Vector3 root = InstanceMatrix(readback, slot).GetColumn(3);
                Vector3 center = root + Vector3.up * 0.5f;
                foreach (Plane plane in planes)
                {
                    Assert.That(plane.GetDistanceToPoint(center), Is.GreaterThan(-radius), $"slot {slot} at {root} is outside the frustum");
                }

                visible++;
            }

            int inFrustum = 0;
            // Every root of the populated terrain is on a 0.5 m sub-grid of 1 m cells; count the ones the frustum holds.
            for (int z = 0; z < TerrainSize * 4; z++)
            {
                for (int x = 0; x < TerrainSize * 4; x++)
                {
                    var p = new Vector3((x + 0.5f) * 0.25f, 0.5f, (z + 0.5f) * 0.25f);
                    if (GeometryUtility.TestPlanesAABB(planes, new Bounds(p, new Vector3(0.01f, 0.01f, 0.01f))))
                    {
                        inFrustum++;
                    }
                }
            }

            float expected = inFrustum / 8f; // 2 per 1 m cell = 2 per 16 sub-cells
            TestContext.WriteLine($"visible {visible}, frustum share of 2 / m2 ~ {expected:F0}, all {readback.Stats[0]}");
            Assert.That(visible, Is.GreaterThan(500));
            Assert.That(visible, Is.LessThan(readback.Stats[0] / 2), "most of the terrain is outside this narrow view");
            Assert.That(visible, Is.InRange(expected * 0.9f, expected * 1.25f), "the frustum keeps what it sees (plus the bounds margin)");
        }

        [Test]
        public void AWallInFrontHidesTheInstancesBehindItOnTheNextFrame()
        {
            CreateTerrain(1, DetailScatterMode.InstanceCountMode);
            Paint(0, (x, y) => 2);
            CreateDetails(Kind(400f));
            Look(new Vector3(64f, 1.5f, 20f), new Vector3(64f, 1.0f, 100f), 50f);
            uint open = CameraCount(Render(2), 0);

            GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Created.Add(wall);
            wall.GetComponent<Renderer>().sharedMaterial = Lit;
            wall.transform.position = new Vector3(64f, 5f, 22f);
            wall.transform.localScale = new Vector3(40f, 20f, 0.5f);

            // The first frame with the wall builds the Hi-Z that the second one tests against.
            uint occluded = CameraCount(Render(2), 0);
            TestContext.WriteLine($"open {open}, behind the wall {occluded}");
            Assert.That(open, Is.GreaterThan(2000u));
            Assert.That(occluded, Is.LessThan(open / 50), "the wall hides almost everything");

            Details.OcclusionCulling = false;
            uint withoutOcclusion = CameraCount(Render(2), 0);
            Assert.That(withoutOcclusion, Is.EqualTo(open).Within(open / 100 + 1), "without occlusion culling the wall changes nothing");
        }

        [Test]
        public void ACameraCutDoesNotTrustLastFramesDepth()
        {
            CreateTerrain(1, DetailScatterMode.InstanceCountMode);
            Paint(0, (x, y) => 2);
            CreateDetails(Kind(400f));
            GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Created.Add(wall);
            wall.GetComponent<Renderer>().sharedMaterial = Lit;
            wall.transform.position = new Vector3(64f, 5f, 22f);
            wall.transform.localScale = new Vector3(40f, 20f, 0.5f);
            Look(new Vector3(64f, 1.5f, 20f), new Vector3(64f, 1.0f, 100f), 50f);
            Render(2);
            // Cut to the far side of the wall: the old depth would hide everything, so it must be ignored.
            Look(new Vector3(64f, 1.5f, 60f), new Vector3(64f, 1.0f, 140f), 50f);
            uint afterCut = CameraCount(Render(1), 0);
            Details.OcclusionCulling = false;
            uint reference = CameraCount(Render(1), 0);
            Assert.That(afterCut, Is.EqualTo(reference).Within(reference / 100 + 1));
        }

        [Test]
        public void ShadowCascadesTakeCastersUpToTheShadowDistanceOnly()
        {
            CreateTerrain(1, DetailScatterMode.InstanceCountMode);
            Paint(0, (x, y) => 1);
            GrassMeshKind kind = Kind(400f);
            kind.ShadowDistance = 30f;
            CreateDetails(kind);
            Look(new Vector3(64f, 8f, 4f), new Vector3(64f, 0f, 80f), 60f);
            CameraReadback readback = Render(1);
            Assert.That(ShadowCount(readback, 0), Is.GreaterThan(100u), "the sun's cascades take the near plants");
            int lods = readback.KindLodCount[0];
            for (int split = 0; split < GrassMeshDetailRenderer.MaxSplits; split++)
            {
                foreach ((int slot, _) in Entries(readback, readback.KindListBase[0] + lods * 2 + split, false))
                {
                    Vector3 root = InstanceMatrix(readback, slot).GetColumn(3);
                    Assert.That(Vector3.Distance(root, Camera.transform.position), Is.LessThan(30f));
                }
            }

            Details.CastShadows = false;
            Assert.That(ShadowCount(Render(1), 0), Is.Zero);
        }
    }
}
