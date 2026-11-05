using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Cone.Grass.Tests
{
    /// <summary>
    /// Distance LODs: each instance is in the LOD its distance picks; across the cross-fade band it is in both LODs with
    /// complementary screen-dither fades (URP's LOD_FADE_CROSSFADE, no TAA), and after the last LOD it fades out.
    /// </summary>
    [Category("GrassGPU")]
    public sealed class GrassMeshDetailLodTests : GrassMeshDetailFixture
    {
        private const float Lod0 = 20f, Lod1 = 40f, Band = 5f;

        private CameraReadback RenderLodScene()
        {
            CreateTerrain(1, DetailScatterMode.InstanceCountMode);
            Paint(0, (x, y) => 1);
            CreateDetails(Kind(Lod0, Lod1, Band));
            Details.OcclusionCulling = false;
            Look(new Vector3(64f, 1f, 2f), new Vector3(64f, 1f, 100f), 90f);
            return Render();
        }

        private static float Distance(CameraReadback readback, int slot, Vector3 camera) =>
            Vector3.Distance(InstanceMatrix(readback, slot).GetColumn(3), camera);

        [Test]
        public void SolidListsHoldTheirDistanceRange()
        {
            CameraReadback readback = RenderLodScene();
            Vector3 camera = Camera.transform.position;
            int b = readback.KindListBase[0];
            int lod0 = 0, lod1 = 0;
            foreach ((int slot, _) in Entries(readback, b + 0, false))
            {
                Assert.That(Distance(readback, slot, camera), Is.LessThanOrEqualTo(Lod0 - Band + 0.05f));
                lod0++;
            }

            foreach ((int slot, _) in Entries(readback, b + 2, false))
            {
                float d = Distance(readback, slot, camera);
                Assert.That(d, Is.GreaterThanOrEqualTo(Lod0 - 0.05f));
                Assert.That(d, Is.LessThanOrEqualTo(Lod1 - Band + 0.05f));
                lod1++;
            }

            Assert.That(lod0, Is.GreaterThan(100));
            Assert.That(lod1, Is.GreaterThan(100));
        }

        [Test]
        public void TheCrossFadeBandIsInBothLodsWithComplementaryFades()
        {
            CameraReadback readback = RenderLodScene();
            Vector3 camera = Camera.transform.position;
            int b = readback.KindListBase[0];
            var outgoing = new Dictionary<int, int>();
            foreach ((int slot, int fade) in Entries(readback, b + 1, true))
            {
                float d = Distance(readback, slot, camera);
                Assert.That(d, Is.InRange(Lod0 - Band - 0.05f, Lod0 + 0.05f));
                Assert.That(fade, Is.GreaterThan(0), "the outgoing LOD keeps a positive share");
                float expected = Mathf.Clamp01((Lod0 - d) / Band) * 127f;
                Assert.That(fade, Is.EqualTo(expected).Within(1.01f));
                outgoing[slot] = fade;
            }

            int pairs = 0;
            foreach ((int slot, int fade) in Entries(readback, b + 3, true))
            {
                float d = Distance(readback, slot, camera);
                if (d > Lod0 + 1f)
                {
                    // The last LOD's own fade-out.
                    Assert.That(d, Is.InRange(Lod1 - Band - 0.05f, Lod1 + 0.05f));
                    Assert.That(fade, Is.GreaterThan(0));
                    continue;
                }

                Assert.That(fade, Is.LessThan(0), "the incoming LOD keeps the complement");
                Assert.That(outgoing.ContainsKey(slot), Is.True, "an instance fading in must be fading out of the LOD before");
                Assert.That(fade, Is.EqualTo(-outgoing[slot]), "the two fades cover each pixel exactly once");
                pairs++;
            }

            Assert.That(pairs, Is.EqualTo(outgoing.Count));
            Assert.That(pairs, Is.GreaterThan(30));
        }

        [Test]
        public void NothingIsDrawnPastTheLastLod()
        {
            CameraReadback readback = RenderLodScene();
            Vector3 camera = Camera.transform.position;
            int b = readback.KindListBase[0];
            for (int list = 0; list < 4; list++)
            {
                foreach ((int slot, _) in Entries(readback, b + list, list % 2 == 1))
                {
                    Assert.That(Distance(readback, slot, camera), Is.LessThanOrEqualTo(Lod1 + 0.05f));
                }
            }
        }

        [Test]
        public void ConvertedLodGroupsEndWhereTheirScreenHeightsSay()
        {
            GameObject prefab = new GameObject("Mesh Detail Test LOD Prefab");
            Created.Add(prefab);
            var lods = new LOD[2];
            for (int i = 0; i < 2; i++)
            {
                var child = new GameObject("LOD" + i);
                child.transform.SetParent(prefab.transform);
                child.AddComponent<MeshFilter>().sharedMesh = PlantMesh();
                MeshRenderer renderer = child.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = Lit;
                lods[i] = new LOD(i == 0 ? 0.4f : 0.1f, new Renderer[] { renderer });
            }

            prefab.AddComponent<LODGroup>().SetLODs(lods);
            var prototype = new DetailPrototype { usePrototypeMesh = true, prototype = prefab, renderMode = DetailRenderMode.VertexLit, useInstancing = true };
            GrassMeshKind kind = GrassMeshKind.FromDetailPrototype(prototype, 80f);
            Assert.That(kind.Lods.Count, Is.EqualTo(2));
            // Screen height falls as 1 / distance: LOD0 ends at 80 * 0.1 / 0.4 = 20 m, the last at the detail distance.
            Assert.That(kind.Lods[0].EndDistance, Is.EqualTo(20f).Within(1e-3f));
            Assert.That(kind.Lods[1].EndDistance, Is.EqualTo(80f).Within(1e-3f));
        }
    }
}
