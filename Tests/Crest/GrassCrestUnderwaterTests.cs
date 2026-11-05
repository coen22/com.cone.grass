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
    /// Play Mode, windowed: grass under Crest water is fogged like any other submerged surface. A patch of blades and an
    /// unlit cube given the blades' own colour in air stand side by side at the same distance; under water the blades'
    /// colour must shift about as far as the cube's (0.75 to 1.25 times) when the grass draws before URP's opaque copies,
    /// and much less when it draws after them.
    /// </summary>
    public sealed class GrassCrestUnderwaterTests
    {
#if !CONE_GRASS_CREST
        [Test]
        public void SubmergedGrassIsFoggedLikeASubmergedCube() => Assert.Ignore(
            "Crest 5 (com.waveharmonic.crest) is not installed in this project. Install Crest 5 to run this test.");
#else
        private const string CrestFolder = "Packages/com.waveharmonic.crest/Runtime/";
        private const int Size = 256;
        private GameObject _sun, _camera, _water, _grass, _terrainObject, _cube;
        private TerrainData _terrainData;
        private Texture2D _coverage, _readback;
        private RenderTexture _target;
        private Material _cubeMaterial, _waterMaterial;
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

            // An unlit cube to the right of the grass patch, at the same distance from the camera.
            _cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.Destroy(_cube.GetComponent<Collider>());
            _cube.transform.position = new Vector3(2.4f, 0.6f, 6f);
            _cube.transform.localScale = new Vector3(1.4f, 1.8f, 0.2f);
            _cubeMaterial = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
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
        public IEnumerator SubmergedGrassIsFoggedLikeASubmergedCube()
        {
            InfiniteGrassRenderer grass = _grass.GetComponent<InfiniteGrassRenderer>();
            Camera camera = _camera.GetComponent<Camera>();
            Vector2 grassPixel = Pixel(camera, new Vector3(-2.4f, 0.12f, 6f));
            Vector2 cubePixel = Pixel(camera, new Vector3(2.4f, 0.3f, 5.85f));

            foreach (GrassDrawOrder order in new[] { GrassDrawOrder.BeforeOpaqueCopies, GrassDrawOrder.AfterOpaqueCopies })
            {
                grass.DrawOrder = order;

                // In air: give the unlit cube the blades' mean colour.
                _water.transform.position = new Vector3(0f, -5f, 0f);
                yield return Frames(20);
                Color grassAir = Mean(grassPixel);
                grass.enabled = false;
                yield return Frames(10);
                Color groundAir = Mean(grassPixel);
                grass.enabled = true;
                yield return Frames(10);
                Assert.That(DeltaE(grassAir, groundAir), Is.GreaterThan(10.0),
                    $"The sampled patch must show grass ({grassAir}), not the bare ground ({groundAir}).");
                // The camera's colour path (tonemapping, sky fog) need not be the identity, so correct the cube's
                // colour against what it actually renders as, a few times, until it matches the blades.
                Color cubeBase = grassAir.linear;
                Color cubeAir = Color.clear;
                for (int pass = 0; pass < 4; pass++)
                {
                    _cubeMaterial.SetColor("_BaseColor", cubeBase);
                    yield return Frames(10);
                    cubeAir = Mean(cubePixel);
                    if (DeltaE(grassAir, cubeAir) <= 1.0)
                    {
                        break;
                    }

                    Color error = grassAir.linear - cubeAir.linear;
                    cubeBase = new Color(Mathf.Clamp01(cubeBase.r + error.r), Mathf.Clamp01(cubeBase.g + error.g), Mathf.Clamp01(cubeBase.b + error.b), 1f);
                }

                Assert.That(DeltaE(grassAir, cubeAir), Is.LessThanOrEqualTo(3.0),
                    $"Calibration: in air the cube ({cubeAir}) must match the blades ({grassAir}).");

                // Under water: both behind the same water column.
                _water.transform.position = new Vector3(0f, 3f, 0f);
                yield return Frames(120);
                Color grassWet = Mean(grassPixel);
                Color cubeWet = Mean(cubePixel);
                Assert.That(DeltaE(cubeAir, cubeWet), Is.GreaterThan(10.0),
                    $"Crest's underwater fog must visibly change the cube ({cubeAir} in air, {cubeWet} under water) for the comparison to mean anything.");
                // Blades stand at many depths along the view ray, so the patch and the cube never sit under exactly
                // the same water column: compare how far the water moves each colour instead. Fogged grass shifts about
                // as far as the fogged cube; grass the underwater effect misses keeps most of its colour.
                double deltaE = DeltaE(grassWet, cubeWet);
                double grassShift = DeltaE(grassAir, grassWet);
                double cubeShift = DeltaE(cubeAir, cubeWet);
                double ratio = grassShift / cubeShift;
                Debug.Log($"GRASS UNDERWATER {order}: grass {grassWet} cube {cubeWet} dE {deltaE:F2} shift grass {grassShift:F2} cube {cubeShift:F2} ratio {ratio:F2} (air {grassAir})");
                if (order == GrassDrawOrder.BeforeOpaqueCopies)
                {
                    Assert.That(ratio, Is.InRange(0.75, 1.25), $"Grass drawn before the opaque copies must be fogged like the cube (grass wet {grassWet}, cube wet {cubeWet}, dE {deltaE:F2}).");
                }
                else
                {
                    Assert.That(ratio, Is.LessThan(0.75), $"Drawn after the copies, Crest's effect misses the grass: the test must see it (grass wet {grassWet}, cube wet {cubeWet}).");
                }
            }
        }

        private static IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++)
            {
                yield return null;
            }
        }

        private static Vector2 Pixel(Camera camera, Vector3 world)
        {
            Vector3 screen = camera.WorldToScreenPoint(world);
            return new Vector2(screen.x, screen.y);
        }

        // The mean sRGB colour of a 16 x 16 block around a pixel of the camera's target.
        private Color Mean(Vector2 pixel)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = _target;
            _readback.ReadPixels(new Rect(0, 0, Size, Size), 0, 0, false);
            _readback.Apply(false);
            RenderTexture.active = previous;
            Color sum = Color.clear;
            int count = 0;
            for (int y = -8; y < 8; y++)
            {
                for (int x = -8; x < 8; x++)
                {
                    int px = Mathf.Clamp((int)pixel.x + x, 0, Size - 1), py = Mathf.Clamp((int)pixel.y + y, 0, Size - 1);
                    sum += _readback.GetPixel(px, py);
                    count++;
                }
            }

            return sum / count;
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
