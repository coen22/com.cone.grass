using Cone.Grass.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Cone.Grass.Tests
{
    /// <summary>
    /// Renders a grass meadow through the active URP renderer with a game camera and a planar reflection camera at the
    /// same pose: the reflection draws grass, fewer blades than the game view, and none when reflections are off.
    /// </summary>
    [Category("GrassGPU")]
    public sealed class GrassReflectionRenderingTests
    {
        private GameObject _terrainObject, _grassObject, _gameObject, _reflectionObject;
        private TerrainData _terrainData;
        private Texture2D _coverage;
        private RenderTexture _target;
        private GrassDataRendererFeature _feature;
        private InfiniteGrassRenderer _grass;

        [SetUp]
        public void SetUp()
        {
            if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset) || !SystemInfo.supportsComputeShaders ||
                SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Assert.Ignore("Needs URP as the active pipeline and a GPU with compute shaders.");
            }

            _feature = GrassRendererSetup.FindFeature(GrassRendererSetup.ActiveRendererData());
            if (_feature == null || !_feature.isActive)
            {
                Assert.Ignore("The active URP renderer has no active Grass Data Renderer Feature.");
            }

            _terrainData = new TerrainData { heightmapResolution = 33 };
            _terrainData.size = new Vector3(40f, 2f, 40f);
            _terrainObject = Terrain.CreateTerrainGameObject(_terrainData);
            _terrainObject.transform.position = new Vector3(-20f, 0f, -20f);
            _coverage = new Texture2D(4, 4, TextureFormat.RGBA32, false, true);
            _coverage.SetPixels(new Color[16] { Color.white, Color.white, Color.white, Color.white, Color.white, Color.white,
                Color.white, Color.white, Color.white, Color.white, Color.white, Color.white, Color.white, Color.white, Color.white, Color.white });
            _coverage.Apply();

            _grassObject = new GameObject("Grass reflection test renderer");
            _grassObject.SetActive(false);
            _grass = _grassObject.AddComponent<InfiniteGrassRenderer>();
            _grass.GrassMaterial = AssetDatabase.LoadAssetAtPath<Material>("Packages/com.cone.grass/Runtime/Materials/Grass Blade.mat");
            _grass.Spacing = 0.1f;
            _grass.DrawDistance = 40f;
            _grassObject.AddComponent<GrassPlacementArea>().ConfigureTexture(_terrainObject.GetComponent<Terrain>(), _coverage);
            _grassObject.SetActive(true);
            Assert.That(InfiniteGrassRenderer.Instance, Is.SameAs(_grass));

            _target = new RenderTexture(320, 180, 24, RenderTextureFormat.ARGB32);
            _gameObject = CreateCamera("Grass reflection test game camera", CameraType.Game);
            _reflectionObject = CreateCamera("Grass reflection test planar camera", CameraType.Reflection);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (Object owned in new Object[] { _reflectionObject, _gameObject, _grassObject, _terrainObject, _terrainData, _coverage })
            {
                if (owned)
                {
                    Object.DestroyImmediate(owned);
                }
            }

            if (_target)
            {
                _target.Release();
                Object.DestroyImmediate(_target);
            }
        }

        [Test]
        public void PlanarReflectionDrawsFewerBladesThanTheGameView()
        {
            uint game = Render(_gameObject);
            uint reflection = Render(_reflectionObject);

            Assert.That(game, Is.GreaterThan(10000u), "The game view must grow a meadow to compare against.");
            Assert.That(reflection, Is.GreaterThan(0u), "The planar reflection camera must draw grass.");
            // Spacing x2 is a quarter of the blades and half the draw distance trims more; well under half remains.
            Assert.That(reflection, Is.LessThan(game / 2u), $"Reflection {reflection} vs game {game}: the cheap path must draw fewer.");

            _grass.Reflections.SpacingScale = 1f;
            _grass.Reflections.DrawDistanceScale = 1f;
            uint fullReflection = Render(_reflectionObject);
            Assert.That((double)fullReflection, Is.EqualTo((double)game).Within(5).Percent,
                "At scale 1 the reflection draws what the game view draws from the same pose.");

            _grass.Reflections.SpacingScale = 2f;
            uint spacingOnly = Render(_reflectionObject);
            Assert.That((double)spacingOnly, Is.EqualTo(fullReflection / 4.0).Within(15).Percent, "Spacing x2 grows a quarter of the blades.");
            _grass.Reflections.SpacingScale = 1f;
            _grass.Reflections.DrawDistanceScale = 0.5f;
            uint distanceOnly = Render(_reflectionObject);
            Assert.That(distanceOnly, Is.LessThan(fullReflection), "Half the draw distance draws fewer blades.");
        }

        [Test]
        public void ReflectionsOffDrawNoGrass()
        {
            Assert.That(Render(_reflectionObject), Is.GreaterThan(0u), "With Planar the reflection draws grass first.");
            _grass.Reflections.Cameras = GrassReflectionCameras.None;
            _reflectionObject.GetComponent<Camera>().Render();
            Assert.That(_feature.TryReadCounts(_reflectionObject.GetComponent<Camera>(), new uint[4]), Is.False,
                "A refused reflection camera keeps no grass state.");
        }

        private GameObject CreateCamera(string name, CameraType type)
        {
            var go = new GameObject(name);
            go.transform.SetPositionAndRotation(new Vector3(0f, 2.5f, -15f), Quaternion.Euler(12f, 0f, 0f));
            Camera camera = go.AddComponent<Camera>();
            camera.enabled = false;
            camera.cameraType = type;
            camera.fieldOfView = 60f;
            camera.targetTexture = _target;
            return go;
        }

        private uint Render(GameObject cameraObject)
        {
            Camera camera = cameraObject.GetComponent<Camera>();
            var counts = new uint[4];
            // The first frames capture the placement maps; generation follows from cached data.
            for (int frame = 0; frame < 3; frame++)
            {
                camera.Render();
            }

            Assert.That(_feature.TryReadCounts(camera, counts), Is.True, $"{camera.name} kept no grass state.");
            Assert.That(counts[3], Is.Zero, "No blades may overflow the capacity.");
            return counts[0] + counts[1] + counts[2];
        }
    }
}
