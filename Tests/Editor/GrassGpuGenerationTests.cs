using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

/// <summary>Executes the shipped kernels, including group barriers and native indirect argument writes.</summary>
[Category("GrassGPU")]
[NonParallelizable]
public sealed class GrassGpuGenerationTests
{
    private const string ComputePath = "Packages/com.cone.grass/Runtime/Compute/GrassPositionsCompute.compute";
    private static readonly Vector4 Sentinel = new Vector4(-901f, -902f, -903f, -904f);
    private ComputeShader shader;
    private GraphicsBuffer positions;
    private GraphicsBuffer counts;
    private GraphicsBuffer arguments;
    private Texture2D height;
    private Texture2D density;
    private Texture2D exclusion;
    private Texture2D white;
    private int resetKernel;
    private int generateKernel;
    private int finalizeKernel;
    private readonly int[] capacities = { 200, 200, 200, 0 };
    private readonly int[] offsets = { 2, 204, 406, 0 };
    private GraphicsBuffer.IndirectDrawIndexedArgs[] initialArguments;

    [SetUp]
    public void SetUp()
    {
        if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ||
            !SystemInfo.SupportsTextureFormat(TextureFormat.RGBAFloat))
            Assert.Ignore("Grass GPU tests require a graphics device with compute and RGBAFloat texture support.");

        ComputeShader source = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputePath);
        Assert.That(source, Is.Not.Null, "The installed package compute shader must import successfully.");
        shader = Object.Instantiate(source);
        resetKernel = shader.FindKernel("ResetCounts");
        generateKernel = shader.FindKernel("CSMain");
        finalizeKernel = shader.FindKernel("FinalizeArgs");
        positions = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 608, sizeof(float) * 4);
        counts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 4, sizeof(uint));
        arguments = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.IndirectArguments,
            3, GraphicsBuffer.IndirectDrawIndexedArgs.size);
        var untouched = new Vector4[positions.count];
        for (int index = 0; index < untouched.Length; index++)
            untouched[index] = Sentinel;
        positions.SetData(untouched);
        initialArguments = new GraphicsBuffer.IndirectDrawIndexedArgs[3];
        for (int lod = 0; lod < 3; lod++)
        {
            initialArguments[lod] = new GraphicsBuffer.IndirectDrawIndexedArgs
            {
                indexCountPerInstance = (uint)(33 - lod * 6),
                instanceCount = 777,
                startIndex = (uint)(lod + 2),
                baseVertexIndex = (uint)(lod + 5),
                startInstance = (uint)(lod + 9)
            };
        }
        arguments.SetData(initialArguments);
        counts.SetData(new uint[] { 123, 456, 789, 99 });

        height = CreateTexture(new Color(3f, 1f, 0f, 1f));
        density = CreateTexture(Color.white);
        exclusion = CreateTexture(Color.black);
        white = CreateTexture(Color.white);
        shader.SetTexture(generateKernel, "_GrassHeightMapRT", height);
        shader.SetTexture(generateKernel, "_GrassDensityRT", density);
        shader.SetTexture(generateKernel, "_GrassMaskMapRT", exclusion);
        shader.SetTexture(generateKernel, "_TerrainHeightmap", white);
        shader.SetTexture(generateKernel, "_TerrainHoles", white);
        shader.SetBuffer(resetKernel, "_GrassCounts", counts);
        shader.SetBuffer(generateKernel, "_GrassCounts", counts);
        shader.SetBuffer(generateKernel, "_GrassPositions", positions);
        shader.SetBuffer(finalizeKernel, "_GrassCounts", counts);
        shader.SetBuffer(finalizeKernel, "_GrassIndirectArgs", arguments);

        shader.SetFloat("_Spacing", 1f);
        shader.SetFloat("_DrawDistance", 1000f);
        shader.SetFloat("_TextureUpdateThreshold", 1f);
        shader.SetFloat("_FullDensityDistance", 900f);
        shader.SetFloat("_DensityFalloffExponent", 4f);
        shader.SetFloat("_DensityTransition", 0f);
        shader.SetFloat("_BladeBoundsRadius", 1f);
        shader.SetFloat("_LodTransitionWidth", 0f);
        shader.SetVector("_CameraPosition", Vector4.zero);
        shader.SetVector("_CenterPos", Vector4.zero);
        shader.SetVector("_LodDistances", new Vector4(100f, 200f, 0f, 0f));
        shader.SetInt("_AuthoredAreas", 1);
        shader.SetInt("_UseTerrain", 0);
        shader.SetInt("_TerrainHasHoles", 0);
        shader.SetVector("_TerrainOrigin", Vector4.zero);
        shader.SetVector("_TerrainSize", new Vector4(16f, 20f, 16f, 0f));
        shader.SetInts("_LodCapacities", capacities);
        shader.SetInts("_LodOffsets", offsets);
        shader.SetInt("_ArgsStride", GraphicsBuffer.IndirectDrawIndexedArgs.size);
        shader.SetInt("_ArgsInstanceCountOffset", FindInstanceCountOffset());
        var planes = new Vector4[6];
        for (int index = 0; index < planes.Length; index++)
            planes[index] = new Vector4(0f, 0f, 0f, 1f);
        shader.SetVectorArray("_FrustumPlanes", planes);
    }

    [TearDown]
    public void TearDown()
    {
        positions?.Dispose();
        counts?.Dispose();
        arguments?.Dispose();
        if (shader) Object.DestroyImmediate(shader);
        if (height) Object.DestroyImmediate(height);
        if (density) Object.DestroyImmediate(density);
        if (exclusion) Object.DestroyImmediate(exclusion);
        if (white) Object.DestroyImmediate(white);
    }

    [Test]
    public void RoundedEdgeGroupsGenerateEachSignedGridCellExactlyOnce()
    {
        Generate(-7, -5, 13, 9);
        Assert.That(ReadCounts(), Is.EqualTo(new uint[] { 117, 0, 0, 0 }));
        AssertArguments(117, 0, 0);
        Vector4[] roots = ReadPositions();
        var cells = new HashSet<Vector2Int>();
        for (int index = offsets[0]; index < offsets[0] + 117; index++)
        {
            Vector4 root = roots[index];
            Assert.That(root.y, Is.EqualTo(3f).Within(0.0001f));
            Assert.That(root.w, Is.EqualTo(1f).Within(0.0001f));
            var cell = new Vector2Int(Mathf.FloorToInt(root.x + 0.5f), Mathf.FloorToInt(root.z + 0.5f));
            Assert.That(cell.x, Is.InRange(-7, 5));
            Assert.That(cell.y, Is.InRange(-5, 3));
            Assert.That(cells.Add(cell), Is.True, "Each candidate must reserve a unique slot even across workgroups.");
        }
        Assert.That(cells.Count, Is.EqualTo(117));
        AssertGuards(roots, 0, 117);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void CapacityOverflowCannotWriteOutsideItsLodPartition(int lod)
    {
        shader.SetInts("_LodCapacities", 3, 3, 3, 0);
        shader.SetVector("_LodDistances", lod == 0 ? new Vector4(100f, 200f, 0f, 0f) :
            lod == 1 ? new Vector4(0f, 100f, 0f, 0f) : Vector4.zero);
        Generate(-7, -5, 13, 9);
        var expected = new uint[4];
        expected[lod] = 117;
        expected[3] = 114;
        Assert.That(ReadCounts(), Is.EqualTo(expected), "Attempted counts and rejected overflow retain separate meanings.");
        AssertArguments(lod == 0 ? 3u : 0u, lod == 1 ? 3u : 0u, lod == 2 ? 3u : 0u);
        AssertGuards(ReadPositions(), lod, 3);
    }

    [Test]
    public void NoDispatchAfterAPopulatedFrameClearsAllIndirectInstanceCounts()
    {
        Generate(-7, -5, 13, 9);
        AssertArguments(117, 0, 0);
        shader.Dispatch(resetKernel, 1, 1, 1);
        shader.Dispatch(finalizeKernel, 1, 1, 1);
        Assert.That(ReadCounts(), Is.EqualTo(new uint[4]));
        AssertArguments(0, 0, 0);
    }

    [TestCase(0f, 0f, 1f)]
    [TestCase(1f, 1f, 1f)]
    [TestCase(1f, 0f, 0f)]
    public void EmptyDensityExclusionAndMissingSurfaceRejectAllCandidates(float authored, float mask, float valid)
    {
        FillTexture(density, new Color(authored, 0f, 0f, 1f));
        FillTexture(exclusion, new Color(mask, 0f, 0f, 1f));
        FillTexture(height, new Color(3f, valid, 0f, 1f));
        Generate(-7, -5, 13, 9);
        Assert.That(ReadCounts(), Is.EqualTo(new uint[4]));
        AssertArguments(0, 0, 0);
        AssertGuards(ReadPositions(), 0, 0);
    }

    [Test]
    public void NativeTerrainHeightUsesWorldOriginAndAllHolesRemoveGrass()
    {
        var data = new TerrainData { heightmapResolution = 33, size = new Vector3(16f, 20f, 16f) };
        try
        {
            var heights = new float[33, 33];
            for (int y = 0; y < 33; y++)
                for (int x = 0; x < 33; x++)
                    heights[y, x] = 0.35f;
            data.SetHeights(0, 0, heights);
            var holes = new bool[data.holesResolution, data.holesResolution];
            for (int y = 0; y < data.holesResolution; y++)
                for (int x = 0; x < data.holesResolution; x++)
                    holes[y, x] = true;
            data.SetHoles(0, 0, holes);
            shader.SetInt("_UseTerrain", 1);
            shader.SetInt("_TerrainHasHoles", 1);
            shader.SetVector("_TerrainOrigin", new Vector4(0f, 11f, 0f, 0f));
            shader.SetTexture(generateKernel, "_TerrainHeightmap", data.heightmapTexture);
            shader.SetTexture(generateKernel, "_TerrainHoles", data.holesTexture);
            Generate(4, 4, 8, 8);
            Assert.That(ReadCounts(), Is.EqualTo(new uint[] { 64, 0, 0, 0 }));
            Vector4[] roots = ReadPositions();
            for (int index = offsets[0]; index < offsets[0] + 64; index++)
                Assert.That(roots[index].y, Is.EqualTo(18f).Within(0.005f),
                    "Native Terrain height encoding must decode on this graphics API, including the world Y offset.");

            data.SetHoles(0, 0, new bool[data.holesResolution, data.holesResolution]);
            shader.SetTexture(generateKernel, "_TerrainHoles", data.holesTexture);
            Generate(4, 4, 8, 8);
            Assert.That(ReadCounts(), Is.EqualTo(new uint[4]));
            AssertArguments(0, 0, 0);
        }
        finally
        {
            Object.DestroyImmediate(data);
        }
    }

    [TestCase(1f)]
    [TestCase(0.25f)]
    public void FilteringAValidMeshEdgeCannotPullRootsTowardClearedWorldHeight(float valid)
    {
        var pixels = new Color[16];
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                pixels[y * 4 + x] = x < 2 ? new Color(10f, valid, 0f, 1f) : Color.clear;
        height.SetPixels(pixels);
        height.Apply(false, false);
        // These roots straddle the two center texels. Their bilinear validity
        // remains fractional while every contributing valid surface is at Y=10.
        Generate(-7, -5, 13, 9);
        uint[] generated = ReadCounts();
        Assert.That(generated[0], Is.InRange(1u, 116u));
        Assert.That(generated[1], Is.Zero);
        Assert.That(generated[2], Is.Zero);
        Assert.That(generated[3], Is.Zero);
        Vector4[] roots = ReadPositions();
        for (int index = offsets[0]; index < offsets[0] + generated[0]; index++)
            Assert.That(roots[index].y, Is.EqualTo(10f).Within(0.0001f),
                "Invalid clear texels must not lower height; partial vertex validity must not scale world height either.");
        AssertArguments(generated[0], 0, 0);
        AssertGuards(roots, 0, (int)generated[0]);
    }

    private void Generate(int startX, int startZ, int width, int heightCount)
    {
        shader.SetInts("_GridStartIndex", startX, startZ);
        shader.SetInts("_GridSize", width, heightCount);
        shader.Dispatch(resetKernel, 1, 1, 1);
        shader.Dispatch(generateKernel, (width + 7) / 8, (heightCount + 7) / 8, 1);
        shader.Dispatch(finalizeKernel, 1, 1, 1);
    }

    private uint[] ReadCounts()
    {
        var values = new uint[4];
        counts.GetData(values); // Synchronous readback is intentional in small correctness tests only.
        return values;
    }

    private Vector4[] ReadPositions()
    {
        var values = new Vector4[positions.count];
        positions.GetData(values);
        return values;
    }

    private void AssertArguments(uint near, uint middle, uint far)
    {
        var actual = new GraphicsBuffer.IndirectDrawIndexedArgs[3];
        arguments.GetData(actual);
        uint[] expectedCounts = { near, middle, far };
        for (int lod = 0; lod < 3; lod++)
        {
            Assert.That(actual[lod].instanceCount, Is.EqualTo(expectedCounts[lod]));
            Assert.That(actual[lod].indexCountPerInstance, Is.EqualTo(initialArguments[lod].indexCountPerInstance));
            Assert.That(actual[lod].startIndex, Is.EqualTo(initialArguments[lod].startIndex));
            Assert.That(actual[lod].baseVertexIndex, Is.EqualTo(initialArguments[lod].baseVertexIndex));
            Assert.That(actual[lod].startInstance, Is.EqualTo(initialArguments[lod].startInstance));
        }
    }

    private void AssertGuards(Vector4[] roots, int populatedLod, int populatedCount)
    {
        int start = offsets[populatedLod];
        for (int index = 0; index < roots.Length; index++)
        {
            if (index >= start && index < start + populatedCount)
                Assert.That(roots[index].w, Is.InRange(0f, 1f));
            else
                Assert.That(roots[index], Is.EqualTo(Sentinel), "A write crossed a partition boundary or came from an invalid candidate.");
        }
    }

    private static Texture2D CreateTexture(Color value)
    {
        var texture = new Texture2D(4, 4, TextureFormat.RGBAFloat, false, true)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        FillTexture(texture, value);
        return texture;
    }

    private static void FillTexture(Texture2D texture, Color value)
    {
        var pixels = new Color[texture.width * texture.height];
        for (int index = 0; index < pixels.Length; index++)
            pixels[index] = value;
        texture.SetPixels(pixels);
        texture.Apply(false, false);
    }

    private static int FindInstanceCountOffset()
    {
        const uint marker = 0x13579bdf;
        int size = Marshal.SizeOf<GraphicsBuffer.IndirectDrawIndexedArgs>();
        Assert.That(size, Is.EqualTo(GraphicsBuffer.IndirectDrawIndexedArgs.size));
        IntPtr memory = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(new GraphicsBuffer.IndirectDrawIndexedArgs { instanceCount = marker }, memory, false);
            var bytes = new byte[size];
            Marshal.Copy(memory, bytes, 0, size);
            for (int offset = 0; offset <= size - sizeof(uint); offset += sizeof(uint))
                if (BitConverter.ToUInt32(bytes, offset) == marker)
                    return offset;
            Assert.Fail("The platform's typed indirect argument layout did not expose the instance-count field.");
            return 0;
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }
}
