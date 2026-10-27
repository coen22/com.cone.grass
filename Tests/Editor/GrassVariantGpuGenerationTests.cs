using System;
using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

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
    private ComputeShader shader;
    private GraphicsBuffer positions, counts, arguments;
    private Texture2D height, white, black;
    private GrassVariantProfile profile;
    private int resetKernel, generateKernel, finalizeKernel;

    [SetUp]
    public void SetUp()
    {
        if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsIndirectArgumentsBuffer ||
            SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ||
            !SystemInfo.SupportsTextureFormat(TextureFormat.RGBAFloat))
            Assert.Ignore("Grass GPU tests require compute, indirect arguments, and RGBAFloat texture support.");
        ComputeShader source = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputePath);
        Assert.That(source, Is.Not.Null);
        shader = Object.Instantiate(source);
        resetKernel = shader.FindKernel("ResetCounts");
        generateKernel = shader.FindKernel("CSMain");
        finalizeKernel = shader.FindKernel("FinalizeArgs");
        positions = new GraphicsBuffer(GraphicsBuffer.Target.Structured, HeadOffset + LodCapacity, sizeof(float) * 4);
        counts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 6, sizeof(uint));
        arguments = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 5, GraphicsBuffer.IndirectDrawIndexedArgs.size);
        arguments.SetData(new GraphicsBuffer.IndirectDrawIndexedArgs[5]);
        height = CreateTexture(new Color(3f, 1f, 0f, 1f));
        white = CreateTexture(Color.white);
        black = CreateTexture(Color.black);
        shader.SetTexture(generateKernel, "_GrassHeightMapRT", height);
        shader.SetTexture(generateKernel, "_GrassDensityRT", white);
        shader.SetTexture(generateKernel, "_GrassMaskMapRT", black);
        shader.SetTexture(generateKernel, "_TerrainHeightmap", white);
        shader.SetTexture(generateKernel, "_TerrainHoles", white);
        shader.SetTexture(generateKernel, "_GrassVariantWeightsRT0", white);
        shader.SetTexture(generateKernel, "_GrassVariantWeightsRT1", white);
        foreach (int kernel in new[] { resetKernel, generateKernel, finalizeKernel })
            shader.SetBuffer(kernel, "_GrassCounts", counts);
        shader.SetBuffer(generateKernel, "_GrassPositions", positions);
        shader.SetBuffer(finalizeKernel, "_GrassIndirectArgs", arguments);
        shader.SetFloat("_Spacing", Spacing);
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
        shader.SetInts("_LodCapacities", LodCapacity, LodCapacity, LodCapacity, 0);
        shader.SetInts("_LodOffsets", 0, LodCapacity, 2 * LodCapacity, 0);
        shader.SetInt("_ArgsStride", GraphicsBuffer.IndirectDrawIndexedArgs.size);
        shader.SetInt("_ArgsInstanceCountOffset", FindInstanceCountOffset());
        var planes = new Vector4[6];
        for (int i = 0; i < planes.Length; i++)
            planes[i] = new Vector4(0f, 0f, 0f, 1f);
        shader.SetVectorArray("_FrustumPlanes", planes);

        profile = ScriptableObject.CreateInstance<GrassVariantProfile>();
        profile.ResetToMeadow();
        var data = new GrassVariantGpuData();
        data.Update(profile, false);
        data.Apply(shader);
        shader.SetInt("_GrassHeadQueues", data.HeadQueues);
        shader.SetInts("_GrassHeadOffsets", HeadOffset, HeadOffset + LodCapacity);
        shader.SetInts("_GrassHeadCapacities", LodCapacity, 0);
    }

    [TearDown]
    public void TearDown()
    {
        positions?.Dispose();
        counts?.Dispose();
        arguments?.Dispose();
        if (shader) Object.DestroyImmediate(shader);
        if (height) Object.DestroyImmediate(height);
        if (white) Object.DestroyImmediate(white);
        if (black) Object.DestroyImmediate(black);
        if (profile) Object.DestroyImmediate(profile);
    }

    [Test]
    public void TheComputeGrowsTheKindsTheCpuTwinPredictsAndHeadsOnlyOnFlowers()
    {
        uint[] counted = Generate();
        Assert.That(counted[0], Is.EqualTo((uint)(Cells * Cells)), "Every cell grows exactly one root.");
        Assert.That(counted[3], Is.Zero, "Nothing overflows.");
        Vector4[] roots = new Vector4[positions.count];
        positions.GetData(roots);

        var mix = new GrassVariantSelection.KindMix[8];
        var shares = new float[8];
        profile.GetMix(mix);
        GrassVariantSelection.Field field = profile.Field;
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
            int predicted = GrassVariantSelection.SelectKind(cellX, cellZ, Spacing, field, mix, profile.KindCount, shares);
            if (predicted == kind)
                agree++;
            if (profile.kinds[kind].HasHead && ((Vector3)root).magnitude < profile.headDrawDistance)
                expectedHeads++;
        }
        // A root exactly on a noise-lattice line or a share boundary may round apart between CPU and GPU.
        Assert.That(agree, Is.GreaterThanOrEqualTo((int)(0.995f * counted[0])), "CPU and GPU kinds agree.");
        // Every kind with a weight of at least 1 % grows in a 96 m meadow; the sparse flowers are checked through the heads.
        for (int k = 0; k < profile.KindCount; k++)
            if (profile.kinds[k].weight >= 0.01f)
                Assert.That(kinds[k], Is.GreaterThan(0), "Kind " + k + " grows in a 96 m meadow.");
        Assert.That(expectedHeads, Is.GreaterThan(0), "Flowers and clover grow heads in a 96 m meadow.");

        Assert.That(counted[4], Is.EqualTo((uint)expectedHeads), "Each flower or clover root within the head distance queues one head.");
        Assert.That(counted[5], Is.Zero, "No mesh heads without a profile mesh.");
        for (int i = 0; i < counted[4]; i++)
        {
            Vector4 head = roots[HeadOffset + i];
            Assert.That(profile.kinds[GrassVariantSelection.UnpackKind(head.w)].HasHead, Is.True);
        }
        var args = new GraphicsBuffer.IndirectDrawIndexedArgs[5];
        arguments.GetData(args);
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
            shader.SetTexture(generateKernel, "_GrassVariantWeightsRT0", weights0);
            shader.SetTexture(generateKernel, "_GrassVariantWeightsRT1", weights1);
            uint[] counted = Generate();
            var roots = new Vector4[positions.count];
            positions.GetData(roots);
            for (int i = 0; i < counted[0]; i++)
                Assert.That(GrassVariantSelection.UnpackKind(roots[i].w), Is.Not.EqualTo(4));
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
        profile.headDrawDistance = 1f;
        profile.MarkDirty();
        var data = new GrassVariantGpuData();
        data.Update(profile, false);
        data.Apply(shader);
        uint[] counted = Generate();
        Assert.That(counted[4], Is.LessThan(counted[0] / 200u));
    }

    private uint[] Generate()
    {
        int start = -Cells / 2;
        shader.SetInts("_GridStartIndex", start, start);
        shader.SetInts("_GridSize", Cells, Cells);
        shader.Dispatch(resetKernel, 1, 1, 1);
        shader.Dispatch(generateKernel, Cells / 8, Cells / 8, 1);
        shader.Dispatch(finalizeKernel, 1, 1, 1);
        var values = new uint[6];
        counts.GetData(values);
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
            pixels[i] = value;
        texture.SetPixels(pixels);
        texture.Apply(false, false);
        return texture;
    }

    private static int FindInstanceCountOffset()
    {
        const uint marker = 0x13579bdf;
        int size = Marshal.SizeOf<GraphicsBuffer.IndirectDrawIndexedArgs>();
        IntPtr memory = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(new GraphicsBuffer.IndirectDrawIndexedArgs { instanceCount = marker }, memory, false);
            var bytes = new byte[size];
            Marshal.Copy(memory, bytes, 0, size);
            for (int offset = 0; offset <= size - sizeof(uint); offset += sizeof(uint))
                if (BitConverter.ToUInt32(bytes, offset) == marker)
                    return offset;
            Assert.Fail("The indirect argument layout did not expose the instance-count field.");
            return 0;
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }
}
