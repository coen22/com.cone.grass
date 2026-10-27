using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

/// <summary>
/// Renders the shipped head shader's contact-depth and motion passes (#90) beside its forward pass and the blade
/// shader's motion pass, with the same per-root inputs the renderer binds.
/// </summary>
[Category("GrassGPU")]
public sealed class GrassHeadPassTests
{
    private const int Size = 128;
    private const string HeadShaderPath = "Packages/com.cone.grass/Runtime/Resources/InfiniteGrassHeads.shader";
    private const string BladeShaderPath = "Packages/com.cone.grass/Runtime/Shaders/GrassBladeShader.shader";
    private const float HeadSize = 0.3f;
    private const int Subdivisions = 2;
    private static readonly string[] HeadPasses = { "GrassHeadForward", "GrassHeadContactDepth", "GrassHeadMotionVectors" };

    private Material blade, head;
    private GrassVariantProfile profile;
    private GrassVariantGpuData variants;
    private Mesh headMesh, bladeMesh;
    private Texture2D empty, calm, windy;
    private readonly List<GraphicsBuffer> buffers = new List<GraphicsBuffer>();

    [SetUp]
    public void SetUp()
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || !SystemInfo.supportsComputeShaders ||
            !SystemInfo.IsFormatSupported(GraphicsFormat.R32G32_SFloat, GraphicsFormatUsage.Render) ||
            !SystemInfo.IsFormatSupported(GraphicsFormat.R8G8B8A8_UNorm, GraphicsFormatUsage.Render))
            Assert.Ignore("Head pass tests require a graphics device, structured buffers and RG32F and RGBA8 targets.");

        Shader headShader = AssetDatabase.LoadAssetAtPath<Shader>(HeadShaderPath);
        Shader bladeShader = AssetDatabase.LoadAssetAtPath<Shader>(BladeShaderPath);
        Assert.That(headShader, Is.Not.Null);
        Assert.That(bladeShader, Is.Not.Null);
        Assert.That(headShader.isSupported, Is.True, "The shipped head shader must be supported on the tested graphics API.");
        blade = new Material(bladeShader) { enableInstancing = true };
        blade.SetColor("_Color", Color.white);
        blade.SetColor("_AOColor", Color.white);
        blade.SetFloat("_GrassHeight", 1f);
        blade.SetFloat("_GrassWidth", 0.08f);
        blade.SetFloat("_GrassWidthRandomness", 0f);
        blade.SetFloat("_GrassHeightRandomness", 0f);
        blade.SetFloat("_GrassCurving", 0f);
        blade.SetFloat("_ExpandDistantGrassWidth", 0f);
        blade.SetFloat("_SubdivisionHeightBoost", 0f);
        blade.SetFloat("_MinimumPixelWidth", 0f);
        blade.SetFloat("_WindStrength", 0.5f);
        blade.SetVector("_WindScroll", Vector4.zero);
        blade.SetTextureScale("_WindTexture", Vector2.one);
        blade.SetTextureOffset("_WindTexture", Vector2.zero);
        blade.SetFloat("_GroundBlendStrength", 0f);
        blade.SetFloat("_GrassAlphaToCoverage", 0f);
        head = new Material(headShader) { enableInstancing = true };
        head.CopyPropertiesFromMaterial(blade);
        foreach (string name in HeadPasses)
        {
            int pass = head.FindPass(name);
            Assert.That(pass, Is.GreaterThanOrEqualTo(0), name + " exists.");
            ShaderUtil.CompilePass(head, pass, true);
            Assert.That(ShaderUtil.IsPassCompiled(head, pass), Is.True, name + " compiles natively.");
        }
        int bladeMotion = blade.FindPass("GrassMotionVectors");
        ShaderUtil.CompilePass(blade, bladeMotion, true);
        Assert.That(ShaderUtil.IsPassCompiled(blade, bladeMotion), Is.True);

        // Kind 1 carries a large open puff that faces the sun (straight up here): no buds, seeds or nodding heads.
        profile = ScriptableObject.CreateInstance<GrassVariantProfile>();
        profile.budFraction = 0f;
        profile.seedFraction = 0f;
        profile.headDrawDistance = 60f;
        profile.kinds = new List<GrassVariantKind>
        {
            new GrassVariantKind { name = "Blade" },
            new GrassVariantKind { name = "Puff", head = GrassHeadShape.Puff, headSize = HeadSize, headSizeRandomness = 0f,
                sideways = 0f, stemWidth = 0.5f, palette = new[] { Color.white } }
        };
        variants = new GrassVariantGpuData();
        variants.Update(profile, false);
        headMesh = InfiniteGrassRenderer.CreateHeadMesh();
        bladeMesh = InfiniteGrassRenderer.CreateBladeMesh(Subdivisions);
        empty = Solid(Color.clear);
        calm = Solid(new Color(0.5f, 0.5f, 0f, 0f));
        windy = Solid(new Color(0.9f, 0.5f, 0f, 0f));
    }

    [TearDown]
    public void TearDown()
    {
        foreach (GraphicsBuffer buffer in buffers)
            buffer.Dispose();
        buffers.Clear();
        foreach (Object item in new Object[] { blade, head, profile, headMesh, bladeMesh, empty, calm, windy })
            if (item) Object.DestroyImmediate(item);
    }

    [Test]
    public void HeadsArePresentInTheContactDepthWithTheForwardSilhouette()
    {
        Vector4[] roots = { Root(0f) };
        Color[] forward = RenderForward(roots, calm);
        Vector2[] depth = RenderContactDepth(roots, calm, 100f, Size, out int drawn);
        float far = SystemInfo.usesReversedZBuffer ? 0f : 1f;
        int covered = 0, present = 0, stray = 0;
        for (int i = 0; i < forward.Length; i++)
        {
            bool inForward = forward[i].a > 0.5f;
            bool inContact = depth[i].y > 0.5f;
            covered += inForward ? 1 : 0;
            if (inForward && inContact)
            {
                present++;
                Assert.That(depth[i].x, Is.Not.EqualTo(far), "A covered texel records the head's depth.");
            }
            else if (inContact)
                stray++;
        }
        Assert.That(drawn, Is.EqualTo(1));
        Assert.That(covered, Is.GreaterThan(60), "The head covers a multi-pixel area in colour.");
        // The same vertex code rebuilds the same head: the outlines agree up to their anti-aliased edge.
        Assert.That(present, Is.GreaterThanOrEqualTo((int)(covered * 0.95f)), "The head is in the contact depth.");
        Assert.That(stray, Is.LessThanOrEqualTo((int)(covered * 0.05f) + 2), "No contact coverage outside the head.");

        Vector2[] tooFar = RenderContactDepth(roots, calm, 1f, Size, out _);
        foreach (Vector2 texel in tooFar)
            Assert.That(texel.y, Is.Zero, "A head beyond the caster distance is not a contact caster.");
    }

    [Test]
    public void SwayingHeadWritesMotionWithAStaticCamera()
    {
        Vector4[] roots = { Root(0f) };
        Color[] forward = RenderForward(roots, windy);
        Vector2[] still = RenderMotion(roots, windy, windy, true, false);
        Vector2[] swaying = RenderMotion(roots, windy, calm, true, false);
        int covered = 0, moving = 0;
        float stillMax = 0f, swayMean = 0f;
        for (int i = 0; i < forward.Length; i++)
        {
            if (forward[i].a <= 0.5f)
                continue;
            covered++;
            stillMax = Mathf.Max(stillMax, still[i].magnitude);
            swayMean += swaying[i].magnitude;
            if (swaying[i].x > 0.002f)
                moving++;
        }
        Assert.That(covered, Is.GreaterThan(60));
        swayMean /= covered;
        TestContext.Out.WriteLine($"Head motion: {covered} px, still max {stillMax:E2}, swaying mean {swayMean:F4} UV");
        // An unchanged wind rebuilds the previous head exactly where the current one is.
        Assert.That(stillMax, Is.LessThan(1e-4f), "A still head with a still camera writes no motion.");
        // The wind turned from calm to +x: the head moved right since the previous frame.
        Assert.That(swayMean, Is.GreaterThan(0.005f), "A swaying head writes motion.");
        Assert.That(moving, Is.GreaterThanOrEqualTo((int)(covered * 0.9f)), "The head moves with the wind, to +x.");
        SaveMotionEvidence(windy, calm);
    }

    [Test]
    public void ContactDepthAndMotionPassCostsForTenThousandHeads()
    {
        const int Count = 10000, Draws = 20, Rounds = 7;
        var roots = new Vector4[Count];
        for (int i = 0; i < Count; i++)
        {
            int x = i % 100, z = i / 100;
            roots[i] = new Vector4(-2f + x * 0.04f, -0.5f, 2f + z * 0.04f, GrassVariantSelectionTestsPack(1f, 1));
        }
        const int Width = 1920, Height = 1080;
        double contact = MeasurePass(roots, "GrassHeadContactDepth", Width, Height, Draws, Rounds);
        double motion = MeasurePass(roots, "GrassHeadMotionVectors", Width, Height, Draws, Rounds);
        double bladeContact = MeasureBladePass(roots, "GrassContactDepth", Width, Height, Draws, Rounds);
        TestContext.Out.WriteLine($"GPU-synced wall clock per draw of {Count} heads at {Width}x{Height} " +
            $"({SystemInfo.graphicsDeviceName}, {SystemInfo.graphicsDeviceType}): head contact depth {contact:F3} ms, " +
            $"head motion {motion:F3} ms; blade contact depth for as many blades {bladeContact:F3} ms.");
        UnityEngine.Debug.Log($"GrassHeadPassCost contact={contact:F3}ms motion={motion:F3}ms bladeContact={bladeContact:F3}ms heads={Count}");
        Assert.That(double.IsNaN(contact) || double.IsNaN(motion), Is.False);
    }

    // A root on the camera axis, two metres ahead, its blade one metre tall: the head sits at y = 0.5.
    private static Vector4 Root(float x) => new Vector4(x, -0.5f, 2f, GrassVariantSelectionTestsPack(1f, 1));

    private static float GrassVariantSelectionTestsPack(float coverage, int kind) => Mathf.Clamp01(coverage) + 2f * kind;

    private Color[] RenderForward(Vector4[] roots, Texture wind)
    {
        RenderTexture target = Target(GraphicsFormat.R8G8B8A8_UNorm, Size, Size);
        try
        {
            Draw(target, Color.clear, cmd =>
            {
                MaterialPropertyBlock properties = Properties(roots, wind);
                cmd.DrawMeshInstancedProcedural(headMesh, 0, head, head.FindPass("GrassHeadForward"), roots.Length, properties);
            }, Size, Size);
            var readback = new Texture2D(Size, Size, TextureFormat.RGBA32, false, true);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            readback.ReadPixels(new Rect(0f, 0f, Size, Size), 0, 0, false);
            readback.Apply(false, false);
            RenderTexture.active = previous;
            Color[] pixels = readback.GetPixels();
            Object.DestroyImmediate(readback);
            return pixels;
        }
        finally
        {
            Object.DestroyImmediate(target);
        }
    }

    private Vector2[] RenderContactDepth(Vector4[] roots, Texture wind, float casterDistance, int size, out int drawn)
    {
        RenderTexture target = Target(GraphicsFormat.R32G32_SFloat, size, size);
        try
        {
            float far = SystemInfo.usesReversedZBuffer ? 0f : 1f;
            Draw(target, new Color(far, 0f, 0f, 0f), cmd =>
            {
                MaterialPropertyBlock properties = Properties(roots, wind);
                properties.SetFloat("_GrassContactCasterDistance", casterDistance);
                properties.SetMatrix("_GrassContactViewProjection", GpuProjection() * View());
                cmd.DrawMeshInstancedProcedural(headMesh, 0, head, head.FindPass("GrassHeadContactDepth"), roots.Length,
                    properties);
            }, size, size);
            drawn = roots.Length;
            return ReadRG(target);
        }
        finally
        {
            Object.DestroyImmediate(target);
        }
    }

    // The head motion pass, and optionally the blades' motion pass first, as the renderer draws them.
    private Vector2[] RenderMotion(Vector4[] roots, Texture wind, Texture previousWind, bool heads, bool blades)
    {
        RenderTexture target = Target(GraphicsFormat.R32G32_SFloat, Size, Size);
        try
        {
            Draw(target, Color.clear, cmd =>
            {
                Matrix4x4 viewProjection = GpuProjection() * View();
                cmd.SetGlobalMatrix("_NonJitteredViewProjMatrix", viewProjection);
                cmd.SetGlobalMatrix("_PrevViewProjMatrix", viewProjection);
                MaterialPropertyBlock properties = Properties(roots, wind);
                BindHistory(properties, roots, previousWind);
                if (blades)
                    cmd.DrawMeshInstancedProcedural(bladeMesh, 0, blade, blade.FindPass("GrassMotionVectors"), roots.Length,
                        properties);
                if (heads)
                    cmd.DrawMeshInstancedProcedural(headMesh, 0, head, head.FindPass("GrassHeadMotionVectors"), roots.Length,
                        properties);
            }, Size, Size);
            return ReadRG(target);
        }
        finally
        {
            Object.DestroyImmediate(target);
        }
    }

    // The previous frame's roots in a hash the motion passes look up, and the previous frame's shape, view and wind:
    // the same as the current frame except for the previous wind texture.
    private void BindHistory(MaterialPropertyBlock properties, Vector4[] roots, Texture previousWind)
    {
        int tableSize = GrassMotionVectors.HistoryTableSize(roots.Length);
        var keys = new uint[tableSize];
        var previous = new Vector4[roots.Length];
        int rows = (bladeMesh.vertexCount - 1) / 2;
        for (int i = 0; i < roots.Length; i++)
        {
            Vector4 root = roots[i];
            int kind = Mathf.FloorToInt(root.w * 0.5f);
            float coverage = root.w - 2f * kind;
            // The history stores 16 * kind + rows + coverage / 2.
            previous[i] = new Vector4(root.x, root.y, root.z, 16f * kind + rows + coverage * 0.5f);
            uint seed = Seed(root.x, root.z);
            uint slot = seed & (uint)(tableSize - 1);
            while (keys[slot] != 0u)
                slot = (slot + 1u) & (uint)(tableSize - 1);
            keys[slot] = (seed & 0x7f000000u) | (uint)(i + 1);
        }
        GraphicsBuffer keyBuffer = Buffer(tableSize, sizeof(uint));
        keyBuffer.SetData(keys);
        GraphicsBuffer rootBuffer = Buffer(roots.Length, 16);
        rootBuffer.SetData(previous);
        properties.SetInteger("_GrassMotionHistoryValid", 1);
        properties.SetInteger("_GrassMotionHashMask", tableSize - 1);
        properties.SetBuffer("_GrassPreviousRootKeys", keyBuffer);
        properties.SetBuffer("_GrassPreviousRoots", rootBuffer);
        properties.SetTexture("_GrassPreviousSlope", empty);
        properties.SetTexture("_GrassPreviousWind", previousWind);
        properties.SetVector("_GrassPreviousDimensions", new Vector4(blade.GetFloat("_GrassWidth"),
            blade.GetFloat("_GrassHeight"), blade.GetFloat("_GrassWidthRandomness"), blade.GetFloat("_GrassHeightRandomness")));
        properties.SetVector("_GrassPreviousShaping", new Vector4(blade.GetFloat("_GrassCurving"),
            blade.GetFloat("_ExpandDistantGrassWidth"), blade.GetFloat("_SubdivisionHeightBoost"),
            blade.GetFloat("_SubdivisionBumpWidth")));
        Vector4 widthRange = blade.GetVector("_ExpandDistantGrassRange");
        properties.SetVector("_GrassPreviousRanges", new Vector4(widthRange.x, widthRange.y,
            blade.GetFloat("_SubdivisionDistance"), blade.GetFloat("_MinimumPixelWidth")));
        properties.SetVector("_GrassPreviousWindST", new Vector4(1f, 1f, 0f, 0f));
        properties.SetVector("_GrassPreviousWindMotion", new Vector4(0f, 0f, blade.GetFloat("_WindStrength"), 0f));
        properties.SetVector("_GrassPreviousCameraPosition", Vector4.zero);
        properties.SetVector("_GrassPreviousCameraForward", new Vector4(0f, 0f, 1f, 0f));
        properties.SetVector("_GrassPreviousCameraRight", new Vector4(1f, 0f, 0f, 0f));
        properties.SetVector("_GrassPreviousProjection", new Vector4(Size, Mathf.Abs(Projection().m11), 1f, 0f));
        properties.SetVector("_GrassPreviousMap", new Vector4(0f, 0f, 1f / (2f * 101f), 0f));
        properties.SetVector("_GrassPreviousMainLight", new Vector4(0f, 1f, 0f, 0f));
    }

    private MaterialPropertyBlock Properties(Vector4[] roots, Texture wind)
    {
        GraphicsBuffer positions = Buffer(roots.Length, 16);
        positions.SetData(roots);
        var properties = new MaterialPropertyBlock();
        properties.SetBuffer("_GrassPositions", positions);
        properties.SetInteger("_GrassInstanceOffset", 0);
        properties.SetInteger("_GrassUseExplicitTime", 1);
        properties.SetFloat("_GrassTime", 0f);
        properties.SetFloat("_DrawDistance", 100f);
        properties.SetFloat("_TextureUpdateThreshold", 1f);
        properties.SetVector("_CenterPos", Vector4.zero);
        properties.SetTexture("_GrassColorRT", empty);
        properties.SetTexture("_GrassGroundColorRT", empty);
        properties.SetTexture("_GrassSlopeRT", empty);
        properties.SetTexture("_GrassHeightMapRT", empty);
        properties.SetTexture("_GrassDensityRT", Texture2D.whiteTexture);
        properties.SetTexture("_WindTexture", wind);
        properties.SetVector("_GrassSHAr", new Vector4(0f, 0f, 0f, 1f));
        properties.SetVector("_GrassSHAg", new Vector4(0f, 0f, 0f, 1f));
        properties.SetVector("_GrassSHAb", new Vector4(0f, 0f, 0f, 1f));
        properties.SetVector("_GrassSHBr", Vector4.zero);
        properties.SetVector("_GrassSHBg", Vector4.zero);
        properties.SetVector("_GrassSHBb", Vector4.zero);
        properties.SetVector("_GrassSHC", Vector4.zero);
        variants.Apply(properties);
        return properties;
    }

    // An orthographic camera at the origin looking along +z, two metres across: Size / 2 pixels per metre.
    private static Matrix4x4 View() => Matrix4x4.Scale(new Vector3(1f, 1f, -1f));
    private static Matrix4x4 Projection() => Matrix4x4.Ortho(-1f, 1f, -1f, 1f, 0.1f, 10f);
    private static Matrix4x4 GpuProjection() => GL.GetGPUProjectionMatrix(Projection(), true);

    private static void Draw(RenderTexture target, Color clear, Action<CommandBuffer> record, int width, int height)
    {
        var commands = new CommandBuffer { name = "Grass Head Pass Test" };
        string[] matrixNames = { "unity_MatrixV", "glstate_matrix_projection", "unity_MatrixVP", "_NonJitteredViewProjMatrix",
            "_PrevViewProjMatrix" };
        string[] vectorNames = { "_WorldSpaceCameraPos", "_ScaledScreenParams", "unity_OrthoParams", "_ProjectionParams",
            "_MainLightColor", "_MainLightPosition", "unity_FogColor", "unity_FogParams", "_Time", "_TimeParameters" };
        var matrices = new Matrix4x4[matrixNames.Length];
        var vectors = new Vector4[vectorNames.Length];
        for (int i = 0; i < matrixNames.Length; i++)
            matrices[i] = Shader.GetGlobalMatrix(matrixNames[i]);
        for (int i = 0; i < vectorNames.Length; i++)
            vectors[i] = Shader.GetGlobalVector(vectorNames[i]);
        bool sRGBWrite = GL.sRGBWrite;
        try
        {
            Vector4[] values =
            {
                Vector4.zero, new Vector4(width, height, 1f + 1f / width, 1f + 1f / height), new Vector4(2f, 2f, 0f, 1f),
                new Vector4(1f, 0.1f, 10f, 0.1f), Vector4.zero, new Vector4(0f, 1f, 0f, 0f), Vector4.one, Vector4.zero,
                Vector4.zero, new Vector4(0f, 0f, 1f, 0f)
            };
            for (int i = 0; i < vectorNames.Length; i++)
                commands.SetGlobalVector(vectorNames[i], values[i]);
            commands.SetRenderTarget(target);
            commands.SetViewport(new Rect(0f, 0f, width, height));
            commands.ClearRenderTarget(true, true, clear);
            RenderingUtils.SetViewAndProjectionMatrices(commands, View(), GpuProjection(), false);
            record(commands);
            for (int i = 0; i < matrixNames.Length; i++)
                commands.SetGlobalMatrix(matrixNames[i], matrices[i]);
            for (int i = 0; i < vectorNames.Length; i++)
                commands.SetGlobalVector(vectorNames[i], vectors[i]);
            GL.sRGBWrite = false;
            Graphics.ExecuteCommandBuffer(commands);
        }
        finally
        {
            GL.sRGBWrite = sRGBWrite;
            commands.Release();
        }
    }

    // GPU-synced wall clock of Draws draws of every root with one head pass, less an empty submission, per draw.
    private double MeasurePass(Vector4[] roots, string pass, int width, int height, int draws, int rounds) =>
        Measure(width, height, draws, rounds, roots, (cmd, properties) =>
            cmd.DrawMeshInstancedProcedural(headMesh, 0, head, head.FindPass(pass), roots.Length, properties));

    private double MeasureBladePass(Vector4[] roots, string pass, int width, int height, int draws, int rounds) =>
        Measure(width, height, draws, rounds, roots, (cmd, properties) =>
            cmd.DrawMeshInstancedProcedural(bladeMesh, 0, blade, blade.FindPass(pass), roots.Length, properties));

    private double Measure(int width, int height, int draws, int rounds, Vector4[] roots,
        Action<CommandBuffer, MaterialPropertyBlock> draw)
    {
        RenderTexture target = Target(GraphicsFormat.R32G32_SFloat, width, height, true);
        try
        {
            MaterialPropertyBlock properties = Properties(roots, calm);
            BindHistory(properties, roots, calm);
            properties.SetFloat("_GrassContactCasterDistance", 100f);
            properties.SetMatrix("_GrassContactViewProjection", GpuProjection() * View());
            double Sample(int count)
            {
                var samples = new List<double>();
                for (int round = 0; round < rounds + 1; round++)
                {
                    var watch = Stopwatch.StartNew();
                    Draw(target, Color.clear, cmd =>
                    {
                        for (int i = 0; i < count; i++)
                            draw(cmd, properties);
                    }, width, height);
                    AsyncGPUReadback.Request(target, 0, 0, 1, 0, 1, 0, 1).WaitForCompletion();
                    watch.Stop();
                    // The first round warms the pipeline state.
                    if (round > 0)
                        samples.Add(watch.Elapsed.TotalMilliseconds);
                }
                samples.Sort();
                return samples[samples.Count / 2];
            }
            double baseline = Sample(0);
            return Math.Max(0.0, Sample(draws) - baseline) / draws;
        }
        finally
        {
            Object.DestroyImmediate(target);
        }
    }

    // With GRASS_HEAD_EVIDENCE set to a folder: colour, then blade-only motion (before #90), then blades and heads.
    private void SaveMotionEvidence(Texture wind, Texture previousWind)
    {
        string folder = Environment.GetEnvironmentVariable("GRASS_HEAD_EVIDENCE");
        if (string.IsNullOrEmpty(folder))
            return;
        var roots = new Vector4[5];
        for (int i = 0; i < roots.Length; i++)
            roots[i] = new Vector4(-0.8f + 0.4f * i, -0.5f - 0.1f * (i % 2), 2f + 0.3f * (i % 2), GrassVariantSelectionTestsPack(1f, 1));
        Color[] colour = RenderForward(roots, wind);
        Vector2[] before = RenderMotion(roots, wind, previousWind, false, true);
        Vector2[] after = RenderMotion(roots, wind, previousWind, true, true);
        var image = new Texture2D(Size * 3, Size, TextureFormat.RGBA32, false, false);
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            int i = y * Size + x;
            image.SetPixel(x, y, colour[i].a > 0.5f ? colour[i] : new Color(0.1f, 0.1f, 0.1f, 1f));
            image.SetPixel(Size + x, y, Visualise(before[i]));
            image.SetPixel(2 * Size + x, y, Visualise(after[i]));
        }
        image.Apply();
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "head-motion-vectors.png"), image.EncodeToPNG());
        Object.DestroyImmediate(image);
    }

    // Motion as colour: red for +x, blue for -x, green for vertical, scaled so 0.05 UV is full brightness.
    private static Color Visualise(Vector2 v) =>
        new Color(Mathf.Clamp01(v.x * 20f), Mathf.Clamp01(Mathf.Abs(v.y) * 20f), Mathf.Clamp01(-v.x * 20f), 1f);

    private static Vector2[] ReadRG(RenderTexture target)
    {
        AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(target);
        request.WaitForCompletion();
        Assert.That(request.hasError, Is.False);
        NativeArray<Vector2> data = request.GetData<Vector2>();
        return data.ToArray();
    }

    private static RenderTexture Target(GraphicsFormat format, int width, int height, bool depth = false)
    {
        var target = new RenderTexture(new RenderTextureDescriptor(width, height)
        {
            graphicsFormat = format, depthStencilFormat = depth ? GraphicsFormat.D32_SFloat : GraphicsFormat.None,
            msaaSamples = 1, sRGB = false
        });
        Assert.That(target.Create(), Is.True);
        return target;
    }

    private GraphicsBuffer Buffer(int count, int stride)
    {
        var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, stride);
        buffers.Add(buffer);
        return buffer;
    }

    private static Texture2D Solid(Color colour)
    {
        var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false, true) { wrapMode = TextureWrapMode.Repeat };
        texture.SetPixel(0, 0, colour);
        texture.Apply(false, false);
        return texture;
    }

    private static uint Seed(float x, float z) =>
        Hash(unchecked((uint)BitConverter.SingleToInt32Bits(x))) ^
        Hash(unchecked((uint)BitConverter.SingleToInt32Bits(z) + 0x9e3779b9u));

    private static uint Hash(uint value)
    {
        unchecked
        {
            value ^= value >> 16;
            value *= 0x85ebca6b;
            value ^= value >> 13;
            value *= 0xc2b2ae35;
            return value ^ (value >> 16);
        }
    }
}
