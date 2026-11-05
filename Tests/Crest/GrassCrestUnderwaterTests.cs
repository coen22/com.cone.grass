#if CONE_GRASS_CREST
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using WaveHarmonic.Crest;
#endif
using NUnit.Framework;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Cone.Grass.Tests
{
    /// <summary>
    /// Play Mode, windowed: grass under Crest water is fogged like any other submerged surface. A patch of blades and a
    /// lit cube given the blades' own colour in air stand side by side at the same distance; under water the blades'
    /// colour (blade pixels only, averaged over 30 frames) must shift about as far as the cube's (0.75 to 1.25 times)
    /// in either draw order. Drawn after the copies, the grass must also not cancel Crest's effect for the frame (#100).
    /// </summary>
    public sealed class GrassCrestUnderwaterTests
    {
#if !CONE_GRASS_CREST
        private const string NoCrest = "Crest 5 (com.waveharmonic.crest) is not installed in this project. Install Crest 5 to run this test.";

        [Test]
        public void DrawnBeforeOpaqueCopies_SubmergedGrassIsFoggedLikeACube() => Assert.Ignore(NoCrest);

        [Test]
        public void DrawnAfterOpaqueCopies_CrestStillFogsTheScene() => Assert.Ignore(NoCrest);
#else
        private const string CrestFolder = "Packages/com.waveharmonic.crest/Runtime/";
        private const int Size = 256;
        private GameObject _sun, _camera, _water, _grass, _terrainObject, _cube;
        private TerrainData _terrainData;
        private Texture2D _coverage, _readback;
        private RenderTexture _target;
        private const int AverageFrames = 30, GrassBlock = 48, CubeBlock = 24;
        private Material _cubeMaterial, _waterMaterial;
        private double _ratio;
        private Light _previousSun;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
#if UNITY_EDITOR
            if (GraphicsSettings.currentRenderPipeline == null ||
                GraphicsSettings.currentRenderPipeline.GetType().Name != "UniversalRenderPipelineAsset" || Application.isBatchMode)
            {
                Assert.Ignore("Needs URP and a windowed editor: Crest's underwater effect renders only for cameras that render.");
            }

            _sun = new GameObject("Underwater test sun", typeof(Light));
            _sun.GetComponent<Light>().type = LightType.Directional;
            _sun.transform.rotation = Quaternion.Euler(50f, 30f, 0f);
            _previousSun = RenderSettings.sun;
            RenderSettings.sun = _sun.GetComponent<Light>();

            _terrainData = new TerrainData { heightmapResolution = 33 };
            _terrainData.size = new Vector3(24f, 1f, 24f);
            _terrainObject = Terrain.CreateTerrainGameObject(_terrainData);
            _terrainObject.transform.position = new Vector3(-12f, 0f, -8f);
            _coverage = new Texture2D(4, 4, TextureFormat.RGBA32, false, true);
            var white = new Color[16];
            for (int i = 0; i < white.Length; i++)
            {
                white[i] = Color.white;
            }

            _coverage.SetPixels(white);
            _coverage.Apply();

            _grass = new GameObject("Underwater test grass");
            _grass.SetActive(false);
            InfiniteGrassRenderer grass = _grass.AddComponent<InfiniteGrassRenderer>();
            grass.GrassMaterial = AssetDatabase.LoadAssetAtPath<Material>("Packages/com.cone.grass/Runtime/Materials/Grass Blade.mat");
            grass.Spacing = 0.03f;
            grass.DrawDistance = 20f;
            _grass.AddComponent<GrassPlacementArea>().ConfigureTexture(_terrainObject.GetComponent<Terrain>(), _coverage);
            _grass.SetActive(true);

            // A lit cube to the right of the grass patch, at the same distance from the camera.
            _cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.Destroy(_cube.GetComponent<Collider>());
            _cube.transform.position = new Vector3(2.4f, 0.6f, 6f);
            _cube.transform.localScale = new Vector3(1.4f, 1.8f, 0.2f);
            // Lit, like the blades: under water both then get the same lighting changes (Crest's light absorption and
            // caustics), so the comparison isolates the fog.
            _cubeMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            _cubeMaterial.SetFloat("_Smoothness", 0f);
            _cubeMaterial.SetFloat("_Metallic", 0f);
            _cube.GetComponent<MeshRenderer>().sharedMaterial = _cubeMaterial;

            _target = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            _readback = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            _camera = new GameObject("Underwater test camera", typeof(Camera)) { tag = "MainCamera" };
            Camera camera = _camera.GetComponent<Camera>();
            camera.targetTexture = _target;
            camera.fieldOfView = 50f;
            camera.nearClipPlane = 0.05f;
            _camera.transform.SetPositionAndRotation(new Vector3(0f, 0.9f, -2.5f), Quaternion.identity);

            _water = new GameObject("Underwater test water");
            _water.SetActive(false);
            _water.transform.position = new Vector3(0f, -5f, 0f);
            WaterRenderer water = _water.AddComponent<WaterRenderer>();
            const System.Reflection.BindingFlags Field = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            typeof(WaterRenderer).GetField("_Resources", Field)?.SetValue(water,
                AssetDatabase.LoadAssetAtPath<ScriptableObject>(CrestFolder + "Settings/Resources.asset"));
            typeof(SurfaceRenderer).GetField("_ChunkTemplate", Field)?.SetValue(water.Surface,
                AssetDatabase.LoadAssetAtPath<GameObject>(CrestFolder + "Prefabs/Chunk.prefab"));
            // Murky water, so the fog over a few metres is far above the ΔE tolerance.
            _waterMaterial = new Material(AssetDatabase.LoadAssetAtPath<Material>(CrestFolder + "Materials/Water.mat"));
            var murk = new Color(0.25f, 0.45f, 0.5f, 0.6f);
            float Channel(float c) => -murk.a * 32f * Mathf.Log(Mathf.Max(c, 0.0001f)) / 5f;
            _waterMaterial.SetColor("_Crest_AbsorptionColor", murk);
            _waterMaterial.SetVector("_Crest_Absorption", new Vector4(Channel(murk.r), Channel(murk.g), Channel(murk.b), 1f));
            _waterMaterial.SetColor("_Crest_Scattering", new Color(0.05f, 0.12f, 0.14f));
            water.Surface.Material = _waterMaterial;
            water.WindSpeed = 0f;
            water.PrimaryLight = RenderSettings.sun;
            water.Underwater.Material = AssetDatabase.LoadAssetAtPath<Material>(CrestFolder + "Materials/Water Volume.mat");
            water.Underwater.CopyWaterMaterialParametersEachFrame = true;
            water.Underwater.Enabled = true;
            _water.SetActive(true);
            yield return Frames(30);
#else
            Assert.Ignore("Needs the Editor to load Crest's materials.");
            yield break;
#endif
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            RenderSettings.sun = _previousSun;
            foreach (Object owned in new Object[] { _water, _camera, _cube, _grass, _terrainObject, _sun, _cubeMaterial, _waterMaterial, _readback, _coverage })
            {
                if (owned)
                {
                    Object.Destroy(owned);
                }
            }

            yield return null;
            if (_target)
            {
                _target.Release();
                Object.Destroy(_target);
            }

            if (_terrainData)
            {
                Object.Destroy(_terrainData);
            }
        }

        [UnityTest]
        public IEnumerator DrawnBeforeOpaqueCopies_SubmergedGrassIsFoggedLikeACube()
        {
            yield return Measure(GrassDrawOrder.BeforeOpaqueCopies);
            Assert.That(_ratio, Is.InRange(0.75, 1.25), "Grass drawn before the opaque copies must be fogged like the cube.");
        }

        // #100: grass drawn after Crest's underwater pass cancelled the effect for the whole frame (cube shift 0.5 to 1.1
        // instead of about 60). Measure asserts that the cube is still fogged; the blades must be fogged like it.
        [UnityTest]
        public IEnumerator DrawnAfterOpaqueCopies_CrestStillFogsTheScene()
        {
            yield return Measure(GrassDrawOrder.AfterOpaqueCopies);
            Assert.That(_ratio, Is.InRange(0.75, 1.25), "Grass drawn after the opaque copies must be fogged like the cube.");
        }

        // One draw order per test, each on a fresh scene. The water rises once; the ratio is how far it moves the
        // blades' colour over how far it moves the cube's, into _ratio.
        private IEnumerator Measure(GrassDrawOrder order)
        {
            InfiniteGrassRenderer grass = _grass.GetComponent<InfiniteGrassRenderer>();
            Camera camera = _camera.GetComponent<Camera>();
            Vector2Int grassPixel = Pixel(camera, new Vector3(-2.4f, 0.12f, 6f));
            Vector2Int cubePixel = Pixel(camera, new Vector3(2.4f, 0.3f, 5.85f));
            grass.DrawOrder = order;
            _water.transform.position = new Vector3(0f, -5f, 0f);

            // Blade mask: the block's pixels whose colour with grass differs from the bare ground's. Only those are
            // compared, so blade placement and sway don't mix ground into the reading.
            grass.enabled = false;
            yield return Frames(20);
            Color[] ground = null;
            yield return AverageBlock(grassPixel, GrassBlock, r => ground = r);
            grass.enabled = true;
            yield return Frames(20);
            Color[] grassAirBlock = null;
            yield return AverageBlock(grassPixel, GrassBlock, r => grassAirBlock = r);
            bool[] blades = new bool[grassAirBlock.Length];
            int bladeCount = 0;
            for (int i = 0; i < blades.Length; i++)
            {
                blades[i] = DeltaE(grassAirBlock[i], ground[i]) > 10.0;
                bladeCount += blades[i] ? 1 : 0;
            }

            Assert.That(bladeCount, Is.GreaterThan(blades.Length / 4), "The sampled block must show blades.");
            Color grassAir = MaskedMean(grassAirBlock, blades);

            // The camera's colour path (tonemapping, sky fog) need not be the identity, so correct the lit cube's
            // colour against what it renders as, a few times, until it is near the blades'.
            Color cubeBase = grassAir.linear;
            Color cubeAir = Color.clear;
            for (int pass = 0; pass < 4; pass++)
            {
                _cubeMaterial.SetColor("_BaseColor", cubeBase);
                yield return Frames(10);
                Color[] cubeBlock = null;
                yield return AverageBlock(cubePixel, CubeBlock, r => cubeBlock = r);
                cubeAir = MaskedMean(cubeBlock, null);
                if (DeltaE(grassAir, cubeAir) <= 1.0)
                {
                    break;
                }

                Color error = grassAir.linear - cubeAir.linear;
                cubeBase = new Color(Mathf.Clamp01(cubeBase.r + error.r), Mathf.Clamp01(cubeBase.g + error.g), Mathf.Clamp01(cubeBase.b + error.b), 1f);
            }

            // The comparison is of shifts, so the cube only needs to be near the blades' colour (both then move through
            // the same part of colour space). 5 dE of start difference moves the shift ratio by about 0.16 at most.
            Assert.That(DeltaE(grassAir, cubeAir), Is.LessThanOrEqualTo(5.0),
                $"Calibration: in air the cube ({cubeAir}) must be close to the blades ({grassAir}).");

            // Under water: both behind a few metres of murky water.
            _water.transform.position = new Vector3(0f, 3f, 0f);
            yield return Frames(120);
            Color[] grassWetBlock = null, cubeWetBlock = null;
            yield return AverageBlock(grassPixel, GrassBlock, r => grassWetBlock = r);
            yield return AverageBlock(cubePixel, CubeBlock, r => cubeWetBlock = r);
            Color grassWet = MaskedMean(grassWetBlock, blades);
            Color cubeWet = MaskedMean(cubeWetBlock, null);
            double cubeShift = DeltaE(cubeAir, cubeWet);
            Assert.That(cubeShift, Is.GreaterThan(10.0),
                $"Crest's underwater fog must visibly change the cube ({cubeAir} in air, {cubeWet} under water) for the comparison to mean anything.");

            // Blades stand at many depths along the view ray, so the patch and the cube never sit under exactly the
            // same water column: compare how far the water moves each colour. Fogged grass shifts about as far as
            // the fogged cube; grass the underwater effect misses keeps most of its colour.
            double grassShift = DeltaE(grassAir, grassWet);
            _ratio = grassShift / cubeShift;
            Debug.Log($"GRASS UNDERWATER {order}: blades {bladeCount}/{blades.Length} px, grass {grassAir} -> {grassWet} shift {grassShift:F2}, cube {cubeAir} -> {cubeWet} shift {cubeShift:F2}, ratio {_ratio:F2}");
        }

        // Each pixel of a size x size block around a pixel, averaged over AverageFrames consecutive frames.
        private IEnumerator AverageBlock(Vector2Int centre, int size, System.Action<Color[]> done)
        {
            var sum = new Color[size * size];
            for (int f = 0; f < AverageFrames; f++)
            {
                yield return null;
                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = _target;
                _readback.ReadPixels(new Rect(0, 0, Size, Size), 0, 0, false);
                _readback.Apply(false);
                RenderTexture.active = previous;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        int px = Mathf.Clamp(centre.x - (size / 2) + x, 0, Size - 1), py = Mathf.Clamp(centre.y - (size / 2) + y, 0, Size - 1);
                        sum[(y * size) + x] += _readback.GetPixel(px, py);
                    }
                }
            }

            for (int i = 0; i < sum.Length; i++)
            {
                sum[i] /= AverageFrames;
            }

            done(sum);
        }

        private static Color MaskedMean(Color[] block, bool[] mask)
        {
            Color sum = Color.clear;
            int count = 0;
            for (int i = 0; i < block.Length; i++)
            {
                if (mask == null || mask[i])
                {
                    sum += block[i];
                    count++;
                }
            }

            return count > 0 ? sum / count : Color.clear;
        }

        private static IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++)
            {
                yield return null;
            }
        }

        private static Vector2Int Pixel(Camera camera, Vector3 world)
        {
            Vector3 screen = camera.WorldToScreenPoint(world);
            return new Vector2Int((int)screen.x, (int)screen.y);
        }

        // CIE76 ΔE*ab between two sRGB colours.
        private static double DeltaE(Color a, Color b)
        {
            Vector3 la = Lab(a), lb = Lab(b);
            return (la - lb).magnitude;
        }

        private static Vector3 Lab(Color srgb)
        {
            Color c = srgb.linear;
            double x = 0.4124 * c.r + 0.3576 * c.g + 0.1805 * c.b;
            double y = 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b;
            double z = 0.0193 * c.r + 0.1192 * c.g + 0.9505 * c.b;
            double F(double t) => t > 0.008856 ? System.Math.Pow(t, 1.0 / 3.0) : 7.787 * t + 16.0 / 116.0;
            double fx = F(x / 0.95047), fy = F(y), fz = F(z / 1.08883);
            return new Vector3((float)(116 * fy - 16), (float)(500 * (fx - fy)), (float)(200 * (fy - fz)));
        }
#endif
    }
}
