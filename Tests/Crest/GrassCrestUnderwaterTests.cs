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
    /// unlit cube given the blades' own colour in air stand side by side at the same distance; under water their colours
    /// must still match (ΔE*ab ≤ 3) when the grass draws before URP's opaque copies, and differ when it draws after them.
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
        private Material _cubeMaterial;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
#if UNITY_EDITOR
            if (!(GraphicsSettings.currentRenderPipeline is UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset) ||
                Application.isBatchMode)
            {
                Assert.Ignore("Needs URP and a windowed editor: Crest's underwater effect renders only for cameras that render.");
            }

            _sun = new GameObject("Underwater test sun", typeof(Light));
            _sun.GetComponent<Light>().type = LightType.Directional;
            _sun.transform.rotation = Quaternion.Euler(50f, 30f, 0f);
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
            _cube.transform.position = new Vector3(1.6f, 0.9f, 2f);
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
            water.Surface.Material = AssetDatabase.LoadAssetAtPath<Material>(CrestFolder + "Materials/Water.mat");
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
            foreach (Object owned in new Object[] { _water, _camera, _cube, _grass, _terrainObject, _sun, _cubeMaterial, _readback, _coverage })
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
            Vector2 grassPixel = Pixel(camera, new Vector3(-1.6f, 0.12f, 2f));
            Vector2 cubePixel = Pixel(camera, new Vector3(1.6f, 0.9f, 1.85f));

            foreach (GrassDrawOrder order in new[] { GrassDrawOrder.BeforeOpaqueCopies, GrassDrawOrder.AfterOpaqueCopies })
            {
                grass.DrawOrder = order;

                // In air: give the unlit cube the blades' mean colour.
                _water.transform.position = new Vector3(0f, -5f, 0f);
                yield return Frames(20);
                Color grassAir = Mean(grassPixel);
                _cubeMaterial.SetColor("_BaseColor", grassAir.linear);
                yield return Frames(10);
                Color cubeAir = Mean(cubePixel);
                Assert.That(DeltaE(grassAir, cubeAir), Is.LessThanOrEqualTo(3.0),
                    $"Calibration: in air the cube ({cubeAir}) must match the blades ({grassAir}).");

                // Under water: both behind the same water column.
                _water.transform.position = new Vector3(0f, 3f, 0f);
                yield return Frames(60);
                Color grassWet = Mean(grassPixel);
                Color cubeWet = Mean(cubePixel);
                double deltaE = DeltaE(grassWet, cubeWet);
                Debug.Log($"GRASS UNDERWATER {order}: grass {grassWet} cube {cubeWet} dE {deltaE:F2} (air {grassAir})");
                if (order == GrassDrawOrder.BeforeOpaqueCopies)
                {
                    Assert.That(deltaE, Is.LessThanOrEqualTo(3.0), "Grass drawn before the opaque copies must be fogged like the cube.");
                }
                else
                {
                    Assert.That(deltaE, Is.GreaterThan(3.0), "Drawn after the copies, Crest's effect misses the grass: the test must see it.");
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
