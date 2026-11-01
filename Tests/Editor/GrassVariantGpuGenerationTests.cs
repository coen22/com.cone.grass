using System;
using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Cone.Grass.Tests
{
    /// <summary>The shipped generation kernel's plant kinds and head queues (#88) against the CPU twin.</summary>
    [Category("GrassGPU")]
    public sealed class GrassVariantGpuGenerationTests
    {
        private const string ComputePath = "Packages/com.cone.grass/Runtime/Compute/GrassPositionsCompute.compute";
        // 96 m square, so every flower kind's 12 m drifts and habitat appear.
        private const float Spacing = 1f;
        private const int Cells = 96;
        private const int LodCapacity = Cells * Cells;
        private const int HeadOffset = 3 * LodCapacity;
        private ComputeShader _shader;
        private GraphicsBuffer _positions, _counts, _arguments;
        private Texture2D _height, _white, _black;
        private GrassVariantProfile _profile;
        private int _resetKernel, _generateKernel, _finalizeKernel;

        [SetUp]
        public void SetUp()
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsIndirectArgumentsBuffer ||
                SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ||
                !SystemInfo.SupportsTextureFormat(TextureFormat.RGBAFloat))
            {
                Assert.Ignore("Grass GPU tests require compute, indirect arguments, and RGBAFloat texture support.");
            }

            ComputeShader source = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputePath);
            Assert.That(source, Is.Not.Null);
            _shader = Object.Instantiate(source);
            _resetKernel = _shader.FindKernel("ResetCounts");
            _generateKernel = _shader.FindKernel("CSMain");
            _finalizeKernel = _shader.FindKernel("FinalizeArgs");
            _positions = new GraphicsBuffer(GraphicsBuffer.Target.Structured, HeadOffset + LodCapacity, sizeof(float) * 4);
            _counts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 6, sizeof(uint));
            _arguments = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 5, GraphicsBuffer.IndirectDrawIndexedArgs.size);
            _arguments.SetData(new GraphicsBuffer.IndirectDrawIndexedArgs[5]);
            _height = CreateTexture(new Color(3f, 1f, 0f, 1f));
            _white = CreateTexture(Color.white);
            _black = CreateTexture(Color.black);
            _shader.SetTexture(_generateKernel, "_GrassHeightMapRT", _height);
            _shader.SetTexture(_generateKernel, "_GrassDensityRT", _white);
            _shader.SetTexture(_generateKernel, "_GrassMaskMapRT", _black);
            _shader.SetTexture(_generateKernel, "_TerrainHeightmap", _white);
            _shader.SetTexture(_generateKernel, "_TerrainHoles", _white);
            _shader.SetTexture(_generateKernel, "_GrassVariantWeightsRT0", _white);
            _shader.SetTexture(_generateKernel, "_GrassVariantWeightsRT1", _white);
            foreach (int kernel in new[] { _resetKernel, _generateKernel, _finalizeKernel })
            {
                _shader.SetBuffer(kernel, "_GrassCounts", _counts);
            }

            _shader.SetBuffer(_generateKernel, "_GrassPositions", _positions);
            _shader.SetBuffer(_finalizeKernel, "_GrassIndirectArgs", _arguments);
            _shader.SetFloat("_Spacing", Spacing);
            _shader.SetFloat("_DrawDistance", 1000f);
            _shader.SetFloat("_TextureUpdateThreshold", 1f);
            _shader.SetFloat("_FullDensityDistance", 900f);
            _shader.SetFloat("_DensityFalloffExponent", 4f);
            _shader.SetFloat("_DensityTransition", 0f);
            _shader.SetFloat("_BladeBoundsRadius", 1f);
            _shader.SetFloat("_LodTransitionWidth", 0f);
            _shader.SetVector("_CameraPosition", Vector4.zero);
            _shader.SetVector("_CenterPos", Vector4.zero);
            _shader.SetVector("_LodDistances", new Vector4(100f, 200f, 0f, 0f));
            _shader.SetInt("_AuthoredAreas", 1);
            _shader.SetInt("_UseTerrain", 0);
            _shader.SetInts("_LodCapacities", LodCapacity, LodCapacity, LodCapacity, 0);
            _shader.SetInts("_LodOffsets", 0, LodCapacity, 2 * LodCapacity, 0);
            _shader.SetInt("_ArgsStride", GraphicsBuffer.IndirectDrawIndexedArgs.size);
            _shader.SetInt("_ArgsInstanceCountOffset", FindInstanceCountOffset());
            var planes = new Vector4[6];
            for (int i = 0; i < planes.Length; i++)
            {
                planes[i] = new Vector4(0f, 0f, 0f, 1f);
            }

            _shader.SetVectorArray("_FrustumPlanes", planes);

            _profile = ScriptableObject.CreateInstance<GrassVariantProfile>();
            _profile.ResetToMeadow();
            var data = new GrassVariantGpuData();
            data.Update(_profile, false);
            data.Apply(_shader);
            _shader.SetInt("_GrassHeadQueues", data.HeadQueues);
            _shader.SetInts("_GrassHeadOffsets", HeadOffset, HeadOffset + LodCapacity);
            _shader.SetInts("_GrassHeadCapacities", LodCapacity, 0);
        }

        [TearDown]
        public void TearDown()
        {
            _positions?.Dispose();
            _counts?.Dispose();
            _arguments?.Dispose();
            if (_shader)
            {
                Object.DestroyImmediate(_shader);
            }

            if (_height)
            {
                Object.DestroyImmediate(_height);
            }

            if (_white)
            {
                Object.DestroyImmediate(_white);
            }

            if (_black)
            {
                Object.DestroyImmediate(_black);
            }

            if (_profile)
            {
                Object.DestroyImmediate(_profile);
            }
        }

        [Test]
        public void TheComputeGrowsTheKindsTheCpuTwinPredictsAndHeadsOnlyOnFlowers()
        {
            uint[] counted = Generate();
            Assert.That(counted[0], Is.EqualTo((uint)(Cells * Cells)), "Every cell grows exactly one root.");
            Assert.That(counted[3], Is.Zero, "Nothing overflows.");
            Vector4[] roots = new Vector4[_positions.count];
            _positions.GetData(roots);

            var mix = new GrassVariantSelection.KindMix[8];
            var shares = new float[8];
            _profile.GetMix(mix);
            GrassVariantSelection.Field field = _profile.Field;
            int agree = 0, expectedHeads = 0;
            var kinds = new int[8];
            for (int i = 0; i < counted[0]; i++)
            {
                Vector4 root = roots[i];
                int cellX = Mathf.FloorToInt(root.x / Spacing + 0.5f), cellZ = Mathf.FloorToInt(root.z / Spacing + 0.5f);
                int kind = GrassVariantSelection.UnpackKind(root.w);
                Assert.That(GrassVariantSelection.UnpackCoverage(root.w), Is.EqualTo(1f).Within(1e-6f));
                kinds[kind]++;
                // Flat ground: the compute measures a zero slope, as the twin assumes.
                int predicted = GrassVariantSelection.SelectKind(cellX, cellZ, Spacing, field, mix, _profile.KindCount, shares);
                if (predicted == kind)
                {
                    agree++;
                }

                if (_profile.Kinds[kind].HasHead && ((Vector3)root).magnitude < _profile.HeadDrawDistance)
                {
                    expectedHeads++;
                }
            }
            // A root exactly on a noise-lattice line or a share boundary may round apart between CPU and GPU.
            Assert.That(agree, Is.GreaterThanOrEqualTo((int)(0.995f * counted[0])), "CPU and GPU kinds agree.");
            // Every kind with a weight of at least 1 % grows in a 96 m meadow; the sparse flowers are checked through the heads.
            for (int k = 0; k < _profile.KindCount; k++)
            {
                if (_profile.Kinds[k].Weight >= 0.01f)
                {
                    Assert.That(kinds[k], Is.GreaterThan(0), "Kind " + k + " grows in a 96 m meadow.");
                }
            }

            Assert.That(expectedHeads, Is.GreaterThan(0), "Flowers and clover grow heads in a 96 m meadow.");

            Assert.That(counted[4], Is.EqualTo((uint)expectedHeads), "Each flower or clover root within the head distance queues one head.");
            Assert.That(counted[5], Is.Zero, "No mesh heads without a profile mesh.");
            for (int i = 0; i < counted[4]; i++)
            {
                Vector4 head = roots[HeadOffset + i];
                Assert.That(_profile.Kinds[GrassVariantSelection.UnpackKind(head.w)].HasHead, Is.True);
            }
            var args = new GraphicsBuffer.IndirectDrawIndexedArgs[5];
            _arguments.GetData(args);
            Assert.That(args[0].instanceCount, Is.EqualTo(counted[0]));
            Assert.That(args[3].instanceCount, Is.EqualTo(counted[4]));
            Assert.That(args[4].instanceCount, Is.Zero);
        }

        [Test]
        public void AnAreaWeightOfZeroRemovesThatKindFromTheGpuPopulation()
        {
            // Kind 4 (the meadow's puff flower) weighted out by the area capture; the others remain.
            Texture2D weights0 = CreateTexture(new Color(1f, 1f, 1f, 1f));
            Texture2D weights1 = CreateTexture(new Color(0f, 1f, 1f, 1f));
            try
            {
                _shader.SetTexture(_generateKernel, "_GrassVariantWeightsRT0", weights0);
                _shader.SetTexture(_generateKernel, "_GrassVariantWeightsRT1", weights1);
                uint[] counted = Generate();
                var roots = new Vector4[_positions.count];
                _positions.GetData(roots);
                for (int i = 0; i < counted[0]; i++)
                {
                    Assert.That(GrassVariantSelection.UnpackKind(roots[i].w), Is.Not.EqualTo(4));
                }
            }
            finally
            {
                Object.DestroyImmediate(weights0);
                Object.DestroyImmediate(weights1);
            }
        }

        [Test]
        public void HeadsBeyondTheirDrawDistanceAreNotQueued()
        {
            // Every root is within 17 m; a 1 m head distance leaves almost none.
            _profile.HeadDrawDistance = 1f;
            _profile.MarkDirty();
            var data = new GrassVariantGpuData();
            data.Update(_profile, false);
            data.Apply(_shader);
            uint[] counted = Generate();
            Assert.That(counted[4], Is.LessThan(counted[0] / 200u));
        }

        private uint[] Generate()
        {
            int start = -Cells / 2;
            _shader.SetInts("_GridStartIndex", start, start);
            _shader.SetInts("_GridSize", Cells, Cells);
            _shader.Dispatch(_resetKernel, 1, 1, 1);
            _shader.Dispatch(_generateKernel, Cells / 8, Cells / 8, 1);
            _shader.Dispatch(_finalizeKernel, 1, 1, 1);
            var values = new uint[6];
            _counts.GetData(values);
            return values;
        }

        private static Texture2D CreateTexture(Color value)
        {
            var texture = new Texture2D(4, 4, TextureFormat.RGBAFloat, false, true)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave
            };
            var pixels = new Color[16];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = value;
            }

            texture.SetPixels(pixels);
            texture.Apply(false, false);
            return texture;
        }

        private static int FindInstanceCountOffset()
        {
            const uint Marker = 0x13579bdf;
            int size = Marshal.SizeOf<GraphicsBuffer.IndirectDrawIndexedArgs>();
            IntPtr memory = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(new GraphicsBuffer.IndirectDrawIndexedArgs { instanceCount = Marker }, memory, false);
                var bytes = new byte[size];
                Marshal.Copy(memory, bytes, 0, size);
                for (int offset = 0; offset <= size - sizeof(uint); offset += sizeof(uint))
                {
                    if (BitConverter.ToUInt32(bytes, offset) == Marker)
                    {
                        return offset;
                    }
                }

                Assert.Fail("The indirect argument layout did not expose the instance-count field.");
                return 0;
            }
            finally
            {
                Marshal.FreeHGlobal(memory);
            }
        }
    }
}
