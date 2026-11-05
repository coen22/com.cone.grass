using System.Collections;
using System.Collections.Generic;
using Cone.Grass.Editor;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace Cone.Grass.Tests
{
    /// <summary>
    /// The kinds reach the image through URP's own passes; the converter builds kinds from terrain detail prototypes and
    /// turns Unity's detail drawing off; and the GPU cost of the scatter, cull and Hi-Z passes stays in budget.
    /// </summary>
    [Category("GrassGPU")]
    public sealed class GrassMeshDetailRenderingTests : GrassMeshDetailFixture
    {
        private static float RedShare(RenderTexture target)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            var image = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            image.Apply();
            RenderTexture.active = previous;
            int red = 0;
            Color32[] pixels = image.GetPixels32();
            foreach (Color32 p in pixels)
            {
                if (p.r > 40 && p.r > p.g * 2 && p.r > p.b * 2)
                {
                    red++;
                }
            }

            Object.DestroyImmediate(image);
            return red / (float)pixels.Length;
        }

        [Test]
        public void TheKindsAppearInTheCameraImage()
        {
            CreateTerrain(1, DetailScatterMode.InstanceCountMode);
            Paint(0, (x, y) => 2);
            var green = new Material(Lit) { name = "Mesh Detail Test Ground" };
            green.SetColor("_BaseColor", new Color(0.1f, 0.5f, 0.1f));
            Created.Add(green);
            Terrain.materialTemplate = green;
            Look(new Vector3(64f, 4f, 30f), new Vector3(64f, 0f, 60f), 60f);
            Camera.Render();
            float without = RedShare(Camera.targetTexture);
            CreateDetails(Kind(400f));
            Camera.Render();
            float with = RedShare(Camera.targetTexture);
            TestContext.WriteLine($"red share without {without:P1}, with {with:P1}");
            Assert.That(without, Is.LessThan(0.01f));
            Assert.That(with, Is.GreaterThan(0.2f), "the red plants (URP Lit through the BatchRendererGroup) cover the ground");
        }

        [Test]
        public void TheConverterBuildsSharedKindsAndTurnsUnityDetailsOff()
        {
            CreateTerrain(3, DetailScatterMode.CoverageMode);
            DetailPrototype[] prototypes = Data.detailPrototypes;
            prototypes[0].minWidth = 0.5f;
            prototypes[0].maxWidth = 1.5f;
            prototypes[0].minHeight = 0.8f;
            prototypes[0].maxHeight = 1.2f;
            prototypes[0].noiseSpread = 0.3f;
            prototypes[0].alignToGround = 40f;
            prototypes[0].density = 3f;
            prototypes[2] = new DetailPrototype { usePrototypeMesh = false, renderMode = DetailRenderMode.GrassBillboard };
            Data.detailPrototypes = prototypes;
            Paint(0, (x, y) => 128);
            Paint(1, (x, y) => 64);
            Terrain.detailObjectDistance = 120f;

            GameObject secondObject = Terrain.CreateTerrainGameObject(Data);
            Created.Add(secondObject);
            Terrain second = secondObject.GetComponent<Terrain>();
            second.detailObjectDistance = 120f;
            secondObject.transform.position = new Vector3(TerrainSize, 0f, 0f);

            GrassMeshDetails details = GrassMeshDetailConverter.Convert(new[] { Terrain, second });
            Created.Add(details.gameObject);
            // Layers 0 and 1 share the prefab but differ in settings: two kinds, shared by both terrains. Layer 2 is a texture.
            Assert.That(details.Kinds.Count, Is.EqualTo(2));
            Assert.That(details.Terrains.Count, Is.EqualTo(2));
            Assert.That(details.Terrains[0].LayerKinds, Is.EqualTo(new[] { 0, 1, -1 }));
            Assert.That(details.Terrains[1].LayerKinds, Is.EqualTo(new[] { 0, 1, -1 }));
            GrassMeshKind kind = details.Kinds[0];
            Assert.That(kind.MinWidth, Is.EqualTo(0.5f));
            Assert.That(kind.MaxWidth, Is.EqualTo(1.5f));
            Assert.That(kind.MinHeight, Is.EqualTo(0.8f));
            Assert.That(kind.MaxHeight, Is.EqualTo(1.2f));
            Assert.That(kind.NoiseSpread, Is.EqualTo(0.3f));
            Assert.That(kind.AlignToGround, Is.EqualTo(0.4f).Within(1e-6f));
            Assert.That(kind.Density, Is.EqualTo(3f));
            Assert.That(kind.DrawDistance, Is.EqualTo(120f));
            Assert.That(kind.Lods[0].Mesh, Is.SameAs(PlantMesh()));

            // The texture layer is unpainted, so Unity's detail drawing is turned off and remembered.
            Assert.That(Terrain.detailObjectDistance, Is.Zero);
            Assert.That(second.detailObjectDistance, Is.Zero);
            Assert.That(details.Terrains[0].OriginalDetailDistance, Is.EqualTo(120f));
            GrassMeshDetailConverter.RestoreUnityDetails(details);
            Assert.That(Terrain.detailObjectDistance, Is.EqualTo(120f));
            Assert.That(second.detailObjectDistance, Is.EqualTo(120f));
        }

        [Test]
        public void APaintedTextureLayerKeepsUnitysDetailDrawingOn()
        {
            CreateTerrain(2, DetailScatterMode.CoverageMode);
            DetailPrototype[] prototypes = Data.detailPrototypes;
            prototypes[1] = new DetailPrototype { usePrototypeMesh = false, renderMode = DetailRenderMode.GrassBillboard };
            Data.detailPrototypes = prototypes;
            Paint(0, (x, y) => 128);
            Paint(1, (x, y) => 128);
            Terrain.detailObjectDistance = 90f;
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("not meshes"));
            GrassMeshDetails details = GrassMeshDetailConverter.Convert(new[] { Terrain });
            Created.Add(details.gameObject);
            Assert.That(Terrain.detailObjectDistance, Is.EqualTo(90f));
            Assert.That(details.Terrains[0].OriginalDetailDistance, Is.LessThan(0f));
        }

        [Test]
        public void MaterialsWithoutDotsInstancingAreSkippedWithAWarning()
        {
            CreateTerrain(1, DetailScatterMode.InstanceCountMode);
            Paint(0, (x, y) => 1);
            Assert.That(GrassMeshDetailRenderer.HasDotsInstancing(Lit), Is.True, "URP Lit has the DOTS instancing variant");
            var legacy = new Material(Shader.Find("Unlit/Color"));
            Created.Add(legacy);
            Assert.That(GrassMeshDetailRenderer.HasDotsInstancing(legacy), Is.False);
            GrassMeshKind kind = Kind(400f);
            kind.Lods[0].Materials = new[] { legacy };
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("DOTS"));
            CreateDetails(kind);
            Camera.Render();
        }

        /// <summary>
        /// GPU budget, measured with the passes' own profiling samplers: on a 128 m terrain with two kinds at 3 + 2 instances
        /// per square metre and an 80 m reach (the sample's detail distance), scatter + cull + Hi-Z stay under 0.5 ms at
        /// 1920x1080, a fifth of the cheapest shot's cost of Unity's own detail rendering in the port (2.9 ms).
        /// </summary>
        [UnityTest]
        public IEnumerator ScatterCullAndHiZStayInBudget()
        {
            if (!SystemInfo.supportsGpuRecorder)
            {
                Assert.Ignore("This device has no GPU recorder.");
            }

            CreateTerrain(2, DetailScatterMode.InstanceCountMode);
            Paint(0, (x, y) => 3);
            Paint(1, (x, y) => 2);
            CreateDetails(Kind(30f, 80f, 6f), Kind(30f, 80f, 6f));
            var target = new RenderTexture(1920, 1080, 24) { name = "Mesh Detail Budget Target" };
            Created.Add(target);
            Camera.targetTexture = target;
            Look(new Vector3(64f, 1.7f, 2f), new Vector3(64f, 1f, 100f), 60f);
            Recorder cull = Recorder.Get("Grass Mesh Details Cull");
            Recorder hiZ = Recorder.Get("Grass Mesh Details Hi-Z");
            cull.enabled = true;
            hiZ.enabled = true;
            var samples = new List<double>();
            for (int frame = 0; frame < 40; frame++)
            {
                Camera.Render();
                yield return null;
                if (frame >= 10 && cull.gpuElapsedNanoseconds > 0)
                {
                    samples.Add((cull.gpuElapsedNanoseconds + hiZ.gpuElapsedNanoseconds) / 1e6);
                }
            }

            cull.enabled = false;
            hiZ.enabled = false;
            if (samples.Count < 5)
            {
                Assert.Ignore("The GPU recorder reported no samples for the passes in this editor mode.");
            }

            samples.Sort();
            double median = samples[samples.Count / 2];
            TestContext.WriteLine($"scatter + cull + Hi-Z: median {median:F3} ms over {samples.Count} frames");
            Assert.That(median, Is.LessThan(0.5));
        }
    }
}
