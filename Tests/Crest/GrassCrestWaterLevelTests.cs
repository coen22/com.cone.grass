#if CONE_GRASS_CREST
using System.Collections;
using Cone.Grass.Crest;
#endif
using NUnit.Framework;
#if CONE_GRASS_CREST
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using WaveHarmonic.Crest;
#endif
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Cone.Grass.Tests
{
    /// <summary>
    /// Play Mode: the Crest 5 bridge feeds the water level into the grass water line, and the placement kernel's cut-off
    /// follows the Crest water as it moves. Skipped (with the reason) when Crest isn't installed.
    /// </summary>
    public sealed class GrassCrestWaterLevelTests
    {
        private const string NoCrest = "Crest 5 (com.waveharmonic.crest) is not installed in this project, so the optional " +
            "Cone.Grass.Crest bridge isn't compiled. Install Crest 5 to run this test.";

#if !CONE_GRASS_CREST
        [Test]
        public void BridgeReadsCrestSeaLevel() => Assert.Ignore(NoCrest);

        [Test]
        public void BridgeReadsCrestSurfaceHeight() => Assert.Ignore(NoCrest);

        [Test]
        public void BladeCutOffFollowsTheCrestSurface() => Assert.Ignore(NoCrest);
#else
        private GameObject _sun, _camera, _water, _grass, _bridge;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _sun = new GameObject("Crest test sun", typeof(Light));
            _sun.GetComponent<Light>().type = LightType.Directional;
            RenderSettings.sun = _sun.GetComponent<Light>();
            _camera = new GameObject("Crest test camera", typeof(Camera));
            _camera.tag = "MainCamera";
            _camera.transform.position = new Vector3(0f, 6f, -10f);

            _water = new GameObject("Crest test water");
            _water.SetActive(false);
            _water.transform.position = new Vector3(0f, 1.25f, 0f);
            WaterRenderer water = _water.AddComponent<WaterRenderer>();
            GiveInternalData(water);
            // Calm water: the surface height is the sea level.
            water.WindSpeed = 0f;
            _water.SetActive(true);

            _grass = new GameObject("Crest test grass");
            _grass.AddComponent<InfiniteGrassRenderer>();

            _bridge = new GameObject("Crest test bridge");
            _bridge.AddComponent<GrassCrestWaterLevel>();
            yield return WaitFrames(5);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Object.Destroy(_bridge);
            Object.Destroy(_grass);
            Object.Destroy(_water);
            Object.Destroy(_camera);
            Object.Destroy(_sun);
            yield return null;
        }

        [UnityTest]
        public IEnumerator BridgeReadsCrestSeaLevel()
        {
            var bridge = _bridge.GetComponent<GrassCrestWaterLevel>();
            var grass = _grass.GetComponent<InfiniteGrassRenderer>();
            bridge.Mode = GrassCrestWaterLevel.LevelMode.SeaLevel;
            Assert.That(WaterRenderer.Instance, Is.Not.Null, "The Crest Water Renderer must be the active instance.");
            Assert.That(bridge.AttachedRenderer, Is.SameAs(grass), "With no renderer set, the bridge drives the active one.");
            Assert.That(grass.WaterLine.Source, Is.SameAs(bridge));
            yield return Frame();

            Assert.That(grass.WaterLine.TryResolve(out float level, out _), Is.True);
            Assert.That(level, Is.EqualTo(1.25f).Within(1e-4f));

            bridge.Offset = 0.2f;
            _water.transform.position = new Vector3(0f, -3f, 0f);
            yield return WaitFrames(3);
            Assert.That(grass.WaterLine.TryResolve(out level, out _), Is.True);
            Assert.That(level, Is.EqualTo(-2.8f).Within(1e-4f), "The level follows the Water Renderer, plus the offset.");

            _bridge.SetActive(false);
            Assert.That(grass.WaterLine.Source, Is.Null, "Disabling the bridge detaches it.");
            Assert.That(grass.WaterLine.TryResolve(out _, out _), Is.False);
        }

        [UnityTest]
        public IEnumerator BridgeReadsCrestSurfaceHeight()
        {
            var bridge = _bridge.GetComponent<GrassCrestWaterLevel>();
            var grass = _grass.GetComponent<InfiniteGrassRenderer>();
            bridge.Mode = GrassCrestWaterLevel.LevelMode.SurfaceHeight;
            bridge.MinimumWaveLength = 0f;
            var position = new Vector3(3f, 0f, 4f);
            _bridge.transform.position = position;
            // Waves, so the surface height differs from the sea level and a fallback to it can't pass.
            _water.GetComponent<WaterRenderer>().WindSpeed = 30f;
            var waves = new GameObject("Crest test waves");
            waves.transform.SetParent(_water.transform, false);
            waves.AddComponent<ShapeFFT>();

            // An independent query at the same point: Crest's answers arrive a frame or more late.
            var reference = new SampleCollisionHelper();
            int matched = 0, offSeaLevel = 0;
            for (int frame = 0; frame < 240; frame++)
            {
                yield return Frame();
                Assert.That(grass.WaterLine.TryResolve(out float level, out _), Is.True, "A Crest level is available every frame.");
                if (!reference.SampleHeight(position, out float surface, 0f))
                {
                    continue;
                }

                Assert.That(level, Is.EqualTo(surface).Within(1e-3f), $"Frame {frame}: the bridge must report Crest's surface height.");
                matched++;
                if (Mathf.Abs(surface - 1.25f) > 0.01f)
                {
                    offSeaLevel++;
                }
            }

            Assert.That(matched, Is.GreaterThan(60), "Crest's height query must answer for most frames.");
            Assert.That(offSeaLevel, Is.GreaterThan(0), "With waves the surface must leave the sea level at least once.");
        }

        [UnityTest]
        public IEnumerator BladeCutOffFollowsTheCrestSurface()
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Assert.Ignore("The placement kernel needs compute shader support.");
            }

            var bridge = _bridge.GetComponent<GrassCrestWaterLevel>();
            var grass = _grass.GetComponent<InfiniteGrassRenderer>();
            bridge.Mode = GrassCrestWaterLevel.LevelMode.SeaLevel;
            grass.WaterLine.ShoreFade = 0.25f;
            using var kernel = new SlopedPlacement();

            float previousMinimum = float.NegativeInfinity;
            foreach (float seaLevel in new[] { -1.5f, 0.5f, 2.25f })
            {
                _water.transform.position = new Vector3(0f, seaLevel, 0f);
                yield return WaitFrames(3);
                Assert.That(grass.WaterLine.TryResolve(out float level, out float fade), Is.True);
                Assert.That(level, Is.EqualTo(seaLevel).Within(1e-4f));

                Vector4[] roots = kernel.Generate(level, fade);
                Assert.That(roots.Length, Is.GreaterThan(0));
                float minimum = float.PositiveInfinity;
                foreach (Vector4 root in roots)
                {
                    minimum = Mathf.Min(minimum, root.y);
                }

                Assert.That(minimum, Is.GreaterThan(seaLevel), $"A blade grew at or below Crest's water at {seaLevel} m.");
                Assert.That(minimum, Is.LessThan(seaLevel + fade), "The shoreline band starts at the Crest water line.");
                Assert.That(minimum, Is.GreaterThan(previousMinimum), "The cut-off rises with the water.");
                previousMinimum = minimum;
            }
        }

        // A renderer added from a script doesn't always get Crest's internal resources and chunk prefab from the
        // script's default references, and Crest's validation then switches it off. Set both, as the defaults would.
        private static void GiveInternalData(WaterRenderer water)
        {
#if UNITY_EDITOR
            const System.Reflection.BindingFlags Field = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            const string Folder = "Packages/com.waveharmonic.crest/Runtime/";
            System.Reflection.FieldInfo resources = typeof(WaterRenderer).GetField("_Resources", Field);
            System.Reflection.FieldInfo chunk = typeof(SurfaceRenderer).GetField("_ChunkTemplate", Field);
            if (resources != null && resources.GetValue(water) == null)
            {
                resources.SetValue(water, AssetDatabase.LoadAssetAtPath<ScriptableObject>(Folder + "Settings/Resources.asset"));
            }

            if (chunk != null && chunk.GetValue(water.Surface) == null)
            {
                chunk.SetValue(water.Surface, AssetDatabase.LoadAssetAtPath<GameObject>(Folder + "Prefabs/Chunk.prefab"));
            }

            if (water.Surface.Material == null)
            {
                water.Surface.Material = AssetDatabase.LoadAssetAtPath<Material>(Folder + "Materials/Water.mat");
            }
#endif
        }

        private IEnumerator WaitFrames(int count)
        {
            for (int frame = 0; frame < count; frame++)
            {
                yield return Frame();
            }
        }

        // Crest moves its water and answers height queries for cameras that render; batch mode renders none by itself.
        private IEnumerator Frame()
        {
            yield return null;
            _camera.GetComponent<Camera>().Render();
        }

        /// <summary>The shipped placement kernel over captured ground rising 0.1 m per metre along X.</summary>
        private sealed class SlopedPlacement : System.IDisposable
        {
            private const int Cells = 128;
            private const float Spacing = 0.5f, Extent = 100f;
            private readonly ComputeShader _shader;
            private readonly GraphicsBuffer _positions, _counts, _arguments;
            private readonly Texture2D _height, _white, _black;
            private readonly int _reset, _generate, _finalize;

            public SlopedPlacement()
            {
#if UNITY_EDITOR
                ComputeShader source = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                    "Packages/com.cone.grass/Runtime/Compute/GrassPositionsCompute.compute");
#else
                ComputeShader source = null;
#endif
                Assert.That(source, Is.Not.Null, "The placement kernel must load (Play Mode in the Editor).");
                _shader = Object.Instantiate(source);
                _reset = _shader.FindKernel("ResetCounts");
                _generate = _shader.FindKernel("CSMain");
                _finalize = _shader.FindKernel("FinalizeArgs");
                _positions = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Cells * Cells * 3, 16);
                _counts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 6, 4);
                _arguments = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 3, GraphicsBuffer.IndirectDrawIndexedArgs.size);

                const int Size = 256;
                _height = new Texture2D(Size, Size, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                var pixels = new Color[Size * Size];
                for (int index = 0; index < pixels.Length; index++)
                {
                    float x = ((index % Size + 0.5f) / Size - 0.5f) * 2f * Extent;
                    pixels[index] = new Color(0.1f * x, 1f, 0f, 1f);
                }

                _height.SetPixels(pixels);
                _height.Apply(false, false);
                _white = Solid(Color.white);
                _black = Solid(Color.black);
                _shader.SetTexture(_generate, "_GrassHeightMapRT", _height);
                _shader.SetTexture(_generate, "_GrassDensityRT", _white);
                _shader.SetTexture(_generate, "_GrassMaskMapRT", _black);
                _shader.SetTexture(_generate, "_TerrainHeightmap", _white);
                _shader.SetTexture(_generate, "_TerrainHoles", _white);
                _shader.SetTexture(_generate, "_GrassVariantWeightsRT0", _white);
                _shader.SetTexture(_generate, "_GrassVariantWeightsRT1", _white);
                _shader.SetBuffer(_reset, "_GrassCounts", _counts);
                _shader.SetBuffer(_generate, "_GrassCounts", _counts);
                _shader.SetBuffer(_generate, "_GrassPositions", _positions);
                _shader.SetBuffer(_finalize, "_GrassCounts", _counts);
                _shader.SetBuffer(_finalize, "_GrassIndirectArgs", _arguments);
                _shader.SetFloat("_Spacing", Spacing);
                _shader.SetFloat("_DrawDistance", Extent);
                _shader.SetFloat("_TextureUpdateThreshold", 0f);
                _shader.SetFloat("_FullDensityDistance", Extent);
                _shader.SetFloat("_DensityFalloffExponent", 1f);
                _shader.SetFloat("_BladeBoundsRadius", 1f);
                _shader.SetVector("_LodDistances", new Vector4(1000f, 2000f, 0f, 0f));
                _shader.SetInt("_AuthoredAreas", 1);
                _shader.SetInts("_LodCapacities", Cells * Cells, Cells * Cells, Cells * Cells, 0);
                _shader.SetInts("_LodOffsets", 0, Cells * Cells, Cells * Cells * 2, 0);
                _shader.SetInt("_ArgsStride", GraphicsBuffer.IndirectDrawIndexedArgs.size);
                _shader.SetInt("_ArgsInstanceCountOffset", 4);
                _shader.SetVectorArray("_FrustumPlanes", new[]
                {
                    new Vector4(0f, 0f, 0f, 1f), new Vector4(0f, 0f, 0f, 1f), new Vector4(0f, 0f, 0f, 1f),
                    new Vector4(0f, 0f, 0f, 1f), new Vector4(0f, 0f, 0f, 1f), new Vector4(0f, 0f, 0f, 1f)
                });
                _shader.SetInts("_GridStartIndex", -Cells / 2, -Cells / 2);
                _shader.SetInts("_GridSize", Cells, Cells);
            }

            public Vector4[] Generate(float level, float shoreFade)
            {
                _shader.SetInt("_GrassWaterEnabled", 1);
                _shader.SetFloat("_GrassWaterLevel", level);
                _shader.SetFloat("_GrassWaterShoreFade", shoreFade);
                _shader.Dispatch(_reset, 1, 1, 1);
                _shader.Dispatch(_generate, Cells / 8, Cells / 8, 1);
                _shader.Dispatch(_finalize, 1, 1, 1);
                var counts = new uint[6];
                _counts.GetData(counts);
                var roots = new Vector4[(int)counts[0]];
                _positions.GetData(roots, 0, 0, roots.Length);
                return roots;
            }

            public void Dispose()
            {
                _positions.Dispose();
                _counts.Dispose();
                _arguments.Dispose();
                Object.DestroyImmediate(_shader);
                Object.DestroyImmediate(_height);
                Object.DestroyImmediate(_white);
                Object.DestroyImmediate(_black);
            }

            private static Texture2D Solid(Color value)
            {
                var texture = new Texture2D(4, 4, TextureFormat.RGBAFloat, false, true);
                texture.SetPixels(new[]
                {
                    value, value, value, value, value, value, value, value,
                    value, value, value, value, value, value, value, value
                });
                texture.Apply(false, false);
                return texture;
            }
        }
#endif
    }
}
