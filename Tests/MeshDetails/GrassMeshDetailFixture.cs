using System.Collections.Generic;
using Cone.Grass.Editor;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace Cone.Grass.Tests
{
    /// <summary>
    /// A small world for the mesh-detail tests: a temporary URP pipeline whose renderer has the Grass Mesh Detail Renderer
    /// Feature, a terrain with painted detail layers, a camera rendering into a texture and a sun. Everything is torn down
    /// and the project's pipeline restored after each test.
    /// </summary>
    public abstract class GrassMeshDetailFixture
    {
        protected const int TerrainSize = 128;
        protected const int DetailResolution = 128;
        protected readonly List<Object> Created = new List<Object>();
        protected Camera Camera;
        protected Terrain Terrain;
        protected TerrainData Data;
        protected GrassMeshDetails Details;
        protected Material Lit;
        protected Light Sun;
        private RenderPipelineAsset _previousDefault;
        private RenderPipelineAsset _previousQuality;
        protected UniversalRenderPipelineAsset Pipeline;

        [SetUp]
        public void SetUpWorld()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || !SystemInfo.supportsComputeShaders ||
                BatchRendererGroup.BufferTarget != BatchBufferTarget.RawBuffer)
            {
                Assert.Ignore("Mesh details need a graphics device with compute shaders and BatchRendererGroup raw buffers.");
            }

            var renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
            Created.Add(renderer);
            GrassMeshDetailFeature feature = GrassMeshDetailConverter.InstallFeature(renderer);
            Created.Add(feature);
            Pipeline = UniversalRenderPipelineAsset.Create(renderer);
            Pipeline.supportsCameraDepthTexture = true;
            Pipeline.shadowDistance = 120f;
            Pipeline.shadowCascadeCount = 4;
            Created.Add(Pipeline);
            _previousDefault = GraphicsSettings.defaultRenderPipeline;
            _previousQuality = QualitySettings.renderPipeline;
            GraphicsSettings.defaultRenderPipeline = Pipeline;
            QualitySettings.renderPipeline = Pipeline;

            Shader litShader = Shader.Find("Universal Render Pipeline/Lit");
            Assert.That(litShader, Is.Not.Null);
            Lit = new Material(litShader) { name = "Mesh Detail Test Lit" };
            Lit.SetColor("_BaseColor", Color.red);
            Created.Add(Lit);

            var sunObject = new GameObject("Mesh Detail Test Sun");
            Created.Add(sunObject);
            Sun = sunObject.AddComponent<Light>();
            Sun.type = LightType.Directional;
            Sun.shadows = LightShadows.Hard;
            sunObject.transform.rotation = Quaternion.Euler(50f, 30f, 0f);

            var cameraObject = new GameObject("Mesh Detail Test Camera");
            Created.Add(cameraObject);
            Camera = cameraObject.AddComponent<Camera>();
            Camera.cameraType = CameraType.Game;
            var target = new RenderTexture(256, 256, 24) { name = "Mesh Detail Test Target" };
            Created.Add(target);
            Camera.targetTexture = target;
            Camera.nearClipPlane = 0.1f;
            Camera.farClipPlane = 1000f;
            Camera.clearFlags = CameraClearFlags.SolidColor;
            Camera.backgroundColor = Color.black;
            UniversalAdditionalCameraData extra = cameraObject.AddComponent<UniversalAdditionalCameraData>();
            extra.requiresDepthTexture = true;
            extra.antialiasing = AntialiasingMode.None;
            extra.renderPostProcessing = false;
        }

        [TearDown]
        public void TearDownWorld()
        {
            GrassMeshDetailRenderer.StatsRect = Vector4.zero;
            GrassMeshDetailRenderer.TimeOverride = null;
            GrassConeWind.OverrideAvailable = null;
            if (_previousDefault != null || Pipeline != null)
            {
                GraphicsSettings.defaultRenderPipeline = _previousDefault;
                QualitySettings.renderPipeline = _previousQuality;
            }

            for (int i = Created.Count - 1; i >= 0; i--)
            {
                if (Created[i])
                {
                    Object.DestroyImmediate(Created[i]);
                }
            }

            Created.Clear();
        }

        /// <summary>A flat (or sloped) terrain with one or more painted detail layers.</summary>
        protected void CreateTerrain(int layers, DetailScatterMode mode, System.Func<int, int, float> height = null)
        {
            Data = new TerrainData { heightmapResolution = 129 };
            Data.size = new Vector3(TerrainSize, 40f, TerrainSize);
            Data.SetDetailResolution(DetailResolution, 16);
            Data.SetDetailScatterMode(mode);
            var heights = new float[129, 129];
            if (height != null)
            {
                for (int y = 0; y < 129; y++)
                {
                    for (int x = 0; x < 129; x++)
                    {
                        heights[y, x] = Mathf.Clamp01(height(x, y) / 40f);
                    }
                }
            }

            Data.SetHeights(0, 0, heights);
            GameObject prefab = PrototypeObject();
            var prototypes = new DetailPrototype[layers];
            for (int i = 0; i < layers; i++)
            {
                prototypes[i] = new DetailPrototype
                {
                    usePrototypeMesh = true,
                    prototype = prefab,
                    renderMode = DetailRenderMode.VertexLit,
                    useInstancing = true,
                    density = 1f,
                    minWidth = 1f,
                    maxWidth = 1f,
                    minHeight = 1f,
                    maxHeight = 1f
                };
            }

            Data.detailPrototypes = prototypes;
            Created.Add(Data);
            GameObject terrainObject = Terrain.CreateTerrainGameObject(Data);
            terrainObject.name = "Mesh Detail Test Terrain";
            Created.Add(terrainObject);
            Terrain = terrainObject.GetComponent<Terrain>();
            Terrain.detailObjectDistance = 0f; // Unity's own details stay off: the kinds draw them.
            Terrain.materialTemplate = Lit;
        }

        private GameObject _prototype;

        protected GameObject PrototypeObject()
        {
            if (_prototype)
            {
                return _prototype;
            }

            _prototype = new GameObject("Mesh Detail Test Prototype");
            Created.Add(_prototype);
            _prototype.AddComponent<MeshFilter>().sharedMesh = PlantMesh();
            _prototype.AddComponent<MeshRenderer>().sharedMaterial = Lit;
            _prototype.SetActive(false);
            return _prototype;
        }

        private Mesh _plant;

        /// <summary>A unit box standing on its root: x and z in [-0.25, 0.25], y in [0, 1].</summary>
        protected Mesh PlantMesh()
        {
            if (_plant)
            {
                return _plant;
            }

            Mesh cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            var vertices = cube.vertices;
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] = new Vector3(vertices[i].x * 0.5f, vertices[i].y + 0.5f, vertices[i].z * 0.5f);
            }

            _plant = new Mesh { name = "Mesh Detail Test Plant", vertices = vertices, normals = cube.normals, uv = cube.uv, triangles = cube.triangles };
            _plant.RecalculateBounds();
            Created.Add(_plant);
            return _plant;
        }

        protected void Paint(int layer, System.Func<int, int, int> value)
        {
            var values = new int[DetailResolution, DetailResolution];
            for (int y = 0; y < DetailResolution; y++)
            {
                for (int x = 0; x < DetailResolution; x++)
                {
                    values[y, x] = value(x, y);
                }
            }

            Data.SetDetailLayer(0, 0, layer, values);
        }

        protected GrassMeshKind Kind(float lod0, float lod1 = 0f, float crossFade = 4f)
        {
            var kind = new GrassMeshKind { Name = "Test Kind", CrossFadeWidth = crossFade, ShadowDistance = 60f, WindBend = 0f };
            kind.Lods.Add(new GrassMeshKindLod { Mesh = PlantMesh(), Materials = new[] { Lit }, EndDistance = lod0 });
            if (lod1 > 0f)
            {
                kind.Lods.Add(new GrassMeshKindLod { Mesh = PlantMesh(), Materials = new[] { Lit }, EndDistance = lod1 });
            }

            return kind;
        }

        protected void CreateDetails(params GrassMeshKind[] kinds)
        {
            var go = new GameObject("Mesh Detail Test Details");
            Created.Add(go);
            go.SetActive(false);
            Details = go.AddComponent<GrassMeshDetails>();
            Details.Kinds = new List<GrassMeshKind>(kinds);
            var layerKinds = new int[Data.detailPrototypes.Length];
            for (int i = 0; i < layerKinds.Length; i++)
            {
                layerKinds[i] = i < kinds.Length ? i : -1;
            }

            Details.Terrains = new List<GrassMeshDetailTerrain> { new GrassMeshDetailTerrain { Terrain = Terrain, LayerKinds = layerKinds } };
            Details.InitialListCapacity = 1 << 16;
            Details.Wind.Source = GrassMeshWindSource.None;
            go.SetActive(true);
        }

        protected void Look(Vector3 position, Vector3 target, float fieldOfView = 60f)
        {
            Camera.transform.position = position;
            Camera.transform.rotation = Quaternion.LookRotation(target - position, Vector3.up);
            Camera.fieldOfView = fieldOfView;
        }

        private protected CameraReadback Render(int frames = 1)
        {
            for (int i = 0; i < frames; i++)
            {
                Camera.Render();
            }

            Assert.That(GrassMeshDetailRenderer.TryReadNow(Camera, out CameraReadback readback), Is.True,
                "The camera has no mesh-detail state: the feature did not prepare it.");
            Assert.That(readback.Counters[1], Is.Zero, "A draw list overflowed; the test capacity is too small.");
            Assert.That(readback.Counters[2], Is.Zero, "The instance slots overflowed; the test capacity is too small.");
            return readback;
        }

        /// <summary>Visible entries of one list: (instance slot, packed fade or 127 for solid).</summary>
        private protected static IEnumerable<(int slot, int fade)> Entries(CameraReadback readback, int list, bool fadeList)
        {
            uint count = System.Math.Min(readback.ListCounts[list], readback.ListCapacities[list]);
            for (uint i = 0; i < count; i++)
            {
                uint value = readback.Visible[readback.ListOffsets[list] + i];
                int slot = (int)(value & 0x00ffffffu);
                int fade = fadeList ? (sbyte)(value >> 24) : 127;
                yield return (slot, fade);
            }
        }

        /// <summary>The object-to-world matrix of an instance slot, as the compute shader wrote it.</summary>
        private protected static Matrix4x4 InstanceMatrix(CameraReadback readback, int slot)
        {
            int b = 64 / 4 + slot * 12;
            float[] f = readback.InstanceData;
            var m = new Matrix4x4();
            m.SetColumn(0, new Vector4(f[b], f[b + 1], f[b + 2], 0f));
            m.SetColumn(1, new Vector4(f[b + 3], f[b + 4], f[b + 5], 0f));
            m.SetColumn(2, new Vector4(f[b + 6], f[b + 7], f[b + 8], 0f));
            m.SetColumn(3, new Vector4(f[b + 9], f[b + 10], f[b + 11], 1f));
            return m;
        }

        private protected static uint CameraCount(CameraReadback readback, int kind)
        {
            uint total = 0;
            int lists = readback.KindLodCount[kind] * 2;
            for (int l = 0; l < lists; l++)
            {
                total += readback.ListCounts[readback.KindListBase[kind] + l];
            }

            return total;
        }

        private protected static uint ShadowCount(CameraReadback readback, int kind)
        {
            uint total = 0;
            int lods = readback.KindLodCount[kind];
            for (int l = 0; l < lods * GrassMeshDetailRenderer.MaxSplits; l++)
            {
                total += readback.ListCounts[readback.KindListBase[kind] + lods * 2 + l];
            }

            return total;
        }
    }
}
