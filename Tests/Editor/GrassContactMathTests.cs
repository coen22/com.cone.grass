using System;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public class GrassContactMathTests
{
    [TestCase(1)]
    [TestCase(4)]
    [NonParallelizable]
    [Category("GrassGPU")]
    public void RecordAcceptsImportedBackbufferWithoutTextureDescriptor(int samples)
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ||
            !SystemInfo.supportsComputeShaders || !SystemInfo.supportsIndirectArgumentsBuffer)
            Assert.Ignore("The backbuffer regression requires a graphics device with indirect buffers.");

        GraphicsFormat colorFormat = SystemInfo.GetGraphicsFormat(DefaultFormat.LDR);
        bool depthCoverageSupported = SupportsContactFormat(GraphicsFormat.R32G32_SFloat) ||
            SupportsContactFormat(GraphicsFormat.R32G32B32A32_SFloat);
        GraphicsFormat depthFormat = SystemInfo.IsFormatSupported(GraphicsFormat.D32_SFloat,
            GraphicsFormatUsage.Render)
            ? GraphicsFormat.D32_SFloat : SystemInfo.GetGraphicsFormat(DefaultFormat.DepthStencil);
        if (!depthCoverageSupported || !GraphicsFormatUtility.IsDepthFormat(depthFormat) ||
            !SystemInfo.IsFormatSupported(depthFormat, GraphicsFormatUsage.Render) ||
            !SystemInfo.IsFormatSupported(colorFormat, GraphicsFormatUsage.Blend))
            Assert.Ignore("The backbuffer regression requires contact-shadow attachment formats.");

        Shader shader = Shader.Find("Hidden/InternalErrorShader");
        Assert.That(shader, Is.Not.Null);
        var cameraObject = new GameObject("Grass Contact Backbuffer Test Camera");
        var mesh = new Mesh();
        var material = new Material(shader);
        var graph = new RenderGraph("Grass Contact Backbuffer Test");
        RTHandle backbuffer = null;
        try
        {
            // Stop after attachment validation, before creating or executing passes.
            // The old descriptor query throws before this missing-pass guard.
            Assert.That(material.FindPass("GrassContactDepth"), Is.EqualTo(-1));
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.stereoTargetEye = StereoTargetEyeMask.None;
            Assert.That(camera.stereoEnabled, Is.False);
            using var frameData = new ContextContainer();
            using var contacts = new GrassContactShadows();
            using var positions = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 3, 16);
            using var arguments = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments,
                3, GraphicsBuffer.IndirectDrawIndexedArgs.size);
            using var lights = new NativeArray<VisibleLight>(new[]
            {
                new VisibleLight { lightType = LightType.Directional, finalColor = Color.white }
            }, Allocator.Temp);

            // This is the same RTIdentifier + explicit metadata import used by URP
            // for the system backbuffer. It intentionally has no TextureDesc.
            backbuffer = RTHandles.Alloc(new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget));
            var targetInfo = new RenderTargetInfo
            {
                width = 64, height = 64, volumeDepth = 1,
                msaaSamples = samples, format = colorFormat
            };
            TextureHandle cameraColor = graph.ImportTexture(backbuffer, targetInfo);
            Assert.Throws<ArgumentException>(() => graph.GetTextureDesc(cameraColor));
            Assert.That(graph.GetRenderTargetInfo(cameraColor), Is.EqualTo(targetInfo));
            TextureHandle sceneDepth = graph.CreateTexture(new TextureDesc(64, 64)
            {
                name = "Grass Contact Test Scene Depth",
                format = GraphicsFormat.R32_SFloat,
                dimension = TextureDimension.Tex2D,
                msaaSamples = MSAASamples.None
            });
            BufferHandle positionsHandle = graph.ImportBuffer(positions);
            BufferHandle argumentsHandle = graph.ImportBuffer(arguments);
            Assert.That(positionsHandle.IsValid() && argumentsHandle.IsValid(), Is.True);
            Assert.That(cameraColor.IsValid() && sceneDepth.IsValid(), Is.True);

            UniversalCameraData cameraData = frameData.Create<UniversalCameraData>();
            cameraData.camera = camera;
            cameraData.cameraTargetDescriptor = new RenderTextureDescriptor(64, 64)
            {
                graphicsFormat = colorFormat,
                dimension = TextureDimension.Tex2D,
                msaaSamples = samples
            };
            UniversalResourceData resources = frameData.Create<UniversalResourceData>();
            // URP owns these frame setters. Reflection is confined to this fixture;
            // it opens the same accessibility window as URP's frame setup.
            MethodInfo initFrame = typeof(UniversalResourceDataBase).GetMethod("InitFrame",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(initFrame, Is.Not.Null);
            initFrame.Invoke(resources, null);
            SetFrameTexture(resources, "backBufferColor", cameraColor);
            SetFrameTexture(resources, "cameraDepthTexture", sceneDepth);
            resources.SwitchActiveTexturesToBackbuffer();
            Assert.That(resources.isActiveTargetBackBuffer, Is.True);
            Assert.That(resources.activeColorTexture, Is.EqualTo(cameraColor));
            Assert.That(resources.cameraDepthTexture, Is.EqualTo(sceneDepth));
            UniversalLightData lightData = frameData.Create<UniversalLightData>();
            lightData.mainLightIndex = 0;
            lightData.visibleLights = lights;

            var properties = new[]
            {
                new MaterialPropertyBlock(), new MaterialPropertyBlock(), new MaterialPropertyBlock()
            };
            Assert.DoesNotThrow(() => contacts.Record(graph, frameData,
                positionsHandle, argumentsHandle, positions, arguments,
                new[] { mesh, mesh, mesh }, material, properties,
                new GrassContactShadows.Settings { enabled = true }));
        }
        finally
        {
            graph.Cleanup();
            backbuffer?.Release();
            UnityEngine.Object.DestroyImmediate(material);
            UnityEngine.Object.DestroyImmediate(mesh);
            UnityEngine.Object.DestroyImmediate(cameraObject);
        }
    }

    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    [TestCase(float.NegativeInfinity)]
    public void NonfiniteSettingsDoNotReachGpuParameters(float invalid)
    {
        var variants = new[]
        {
            new GrassContactShadows.Settings { enabled = true, strength = invalid },
            new GrassContactShadows.Settings { enabled = true, rayLength = invalid },
            new GrassContactShadows.Settings { enabled = true, bias = invalid },
            new GrassContactShadows.Settings { enabled = true, thickness = invalid },
            new GrassContactShadows.Settings { enabled = true, maxDistance = invalid }
        };
        foreach (GrassContactShadows.Settings settings in variants)
        {
            Assert.That(GrassContactShadows.TryGetParameters(settings, out Vector4 parameters,
                out Vector4 limits, out float radius), Is.False);
            Assert.That(parameters, Is.EqualTo(Vector4.zero));
            Assert.That(limits, Is.EqualTo(Vector4.zero));
            Assert.That(radius, Is.Zero);
        }
    }

    [Test]
    public void CasterRadiusContainsEverySampleOfAnInRangeReceiver()
    {
        var settings = new GrassContactShadows.Settings
        {
            enabled = true, maxDistance = 50f, rayLength = 0.75f, bias = 0.03f
        };
        Assert.That(GrassContactShadows.TryGetParameters(settings, out Vector4 parameters,
            out Vector4 limits, out float radius), Is.True);
        Vector3[] directions =
        {
            Vector3.right, Vector3.left, Vector3.up, Vector3.down,
            Vector3.forward, Vector3.back, new Vector3(1f, 2f, 3f).normalized
        };
        float[] fractions = { 0f, 0.25f, 0.5f, 0.999f, 1f };
        foreach (Vector3 receiverDirection in directions)
        foreach (Vector3 lightDirection in directions)
        foreach (float receiverFraction in fractions)
        foreach (float rayFraction in fractions)
        {
            Vector3 receiver = receiverDirection * limits.x * receiverFraction;
            Vector3 sample = receiver + lightDirection * (parameters.z + parameters.y * rayFraction);
            Assert.That(sample.magnitude, Is.LessThanOrEqualTo(radius + 0.00001f));
        }
        // Omitting either ray length or bias would lose a reachable caster here.
        Assert.That(radius, Is.GreaterThan(settings.maxDistance + settings.rayLength));
    }

    [Test]
    public void InvalidOrDisabledRangesAndSquaredOverflowProduceNoContactPass()
    {
        var variants = new[]
        {
            new GrassContactShadows.Settings(),
            new GrassContactShadows.Settings { enabled = true, strength = 0f },
            new GrassContactShadows.Settings { enabled = true, rayLength = 0f },
            new GrassContactShadows.Settings { enabled = true, maxDistance = -1f },
            new GrassContactShadows.Settings { enabled = true, maxDistance = 1e30f }
        };
        foreach (GrassContactShadows.Settings settings in variants)
            Assert.That(GrassContactShadows.TryGetParameters(settings, out _, out _, out _), Is.False);
    }

    [TestCase(false, false, false, 0)]
    [TestCase(false, false, true, 1)]
    [TestCase(false, false, true, 2)]
    [TestCase(false, true, false, 0)]
    [TestCase(false, true, true, 1)]
    [TestCase(false, true, true, 2)]
    [TestCase(true, false, false, 0)]
    [TestCase(true, false, true, 1)]
    [TestCase(true, false, true, 2)]
    [TestCase(true, true, false, 0)]
    [TestCase(true, true, true, 2)]
    public void ProjectionAndRawDepthRecoverWorldPositionAndEyeDepth(
        bool orthographic, bool oblique, bool uvStartsAtTop, int depthRange)
    {
        Matrix4x4 projection = MakeProjection(orthographic, oblique, depthRange, uvStartsAtTop);
        Matrix4x4 view = Matrix4x4.TRS(new Vector3(3f, -2f, 4f),
            Quaternion.Euler(3f, 12f, -7f), Vector3.one).inverse;
        Assert.That(GrassContactShadows.TryGetProjectionMatrices(projection, view,
            out Matrix4x4 viewProjection, out Matrix4x4 inverseViewProjection,
            out Matrix4x4 inverseProjection), Is.True);

        Vector3[] viewPositions =
        {
            new Vector3(-0.05f, 0.03f, -0.4f),
            new Vector3(1f, -0.5f, -3f),
            new Vector3(-3f, 2f, -20f)
        };
        float clipMinimum = depthRange == 0 ? -1f : 0f;
        foreach (Vector3 expectedViewPosition in viewPositions)
        {
            Vector3 expectedWorld = view.inverse.MultiplyPoint(expectedViewPosition);
            Vector4 clip = viewProjection * new Vector4(expectedWorld.x, expectedWorld.y, expectedWorld.z, 1f);
            Vector4 directClip = projection * new Vector4(expectedViewPosition.x,
                expectedViewPosition.y, expectedViewPosition.z, 1f);
            Assert.That((clip - directClip).magnitude, Is.LessThan(0.0001f));

            Vector2 uv = ClipToUV(clip, uvStartsAtTop);
            float rawDepth = (clip.z / clip.w - clipMinimum) / (1f - clipMinimum);
            Assert.That(rawDepth, Is.InRange(0f, 1f));
            Vector4 reconstructionClip = new Vector4(uv.x * 2f - 1f,
                uvStartsAtTop ? 1f - uv.y * 2f : uv.y * 2f - 1f,
                rawDepth * (1f - clipMinimum) + clipMinimum, 1f);
            Vector4 world = inverseViewProjection * reconstructionClip;
            Vector3 reconstructedWorld = new Vector3(world.x, world.y, world.z) / world.w;
            float eyeDepth = -Vector4.Dot(inverseProjection.GetRow(2), reconstructionClip) /
                Vector4.Dot(inverseProjection.GetRow(3), reconstructionClip);

            Assert.That(Vector3.Distance(reconstructedWorld, expectedWorld), Is.LessThan(0.003f));
            Assert.That(eyeDepth, Is.EqualTo(-expectedViewPosition.z).Within(0.003f));
        }
    }

    [Test]
    public void OriginAndAllocationScaleKeepOneWorldPointAlignedAcrossAllTargets()
    {
        Vector4 viewPoint = new Vector4(0.7f, 1.1f, -4f, 1f);
        Vector2 sceneAllocationScale = new Vector2(0.75f, 0.6f);
        foreach (bool grassBottomLeft in new[] { false, true })
        foreach (bool colorBottomLeft in new[] { false, true })
        foreach (bool sceneBottomLeft in new[] { false, true })
        {
            Matrix4x4 grassProjection = MakeProjection(false, true, 2, grassBottomLeft);
            Matrix4x4 colorProjection = MakeProjection(false, true, 2, colorBottomLeft);
            Matrix4x4 sceneProjection = MakeProjection(false, true, 2, sceneBottomLeft);
            Vector2 grassUV = ClipToUV(grassProjection * viewPoint, true);
            Vector2 colorUV = ClipToUV(colorProjection * viewPoint, true);
            Vector2 sceneUV = ClipToUV(sceneProjection * viewPoint, true);

            Vector4 compositeTransform = GrassContactShadows.GetUVScaleBias(Vector2.one,
                grassBottomLeft != colorBottomLeft);
            Vector2 reconstructedGrassUV = ApplyScaleBias(colorUV, compositeTransform);
            Vector4 sceneTransform = GrassContactShadows.GetUVScaleBias(sceneAllocationScale,
                grassBottomLeft != sceneBottomLeft);
            Vector2 sampledSceneUV = ApplyScaleBias(reconstructedGrassUV, sceneTransform);

            Assert.That(Vector2.Distance(reconstructedGrassUV, grassUV), Is.LessThan(0.000001f));
            Assert.That(Vector2.Distance(sampledSceneUV, Vector2.Scale(sceneUV, sceneAllocationScale)),
                Is.LessThan(0.000001f));
        }
    }

    [Test]
    public void NoninvertibleOrNonfiniteProjectionDoesNotProduceReconstructionMatrices()
    {
        Assert.That(GrassContactShadows.TryGetProjectionMatrices(Matrix4x4.zero, Matrix4x4.identity,
            out _, out _, out _), Is.False);
        Matrix4x4 invalid = Matrix4x4.identity;
        invalid.m01 = float.NaN;
        Assert.That(GrassContactShadows.TryGetProjectionMatrices(invalid, Matrix4x4.identity,
            out _, out _, out _), Is.False);
    }

    private static Matrix4x4 MakeProjection(bool orthographic, bool oblique, int depthRange, bool flipY)
    {
        Matrix4x4 projection = orthographic
            ? Matrix4x4.Ortho(-4f, 6f, -3f, 5f, 0.1f, 300f)
            : Matrix4x4.Perspective(60f, 1.7f, 0.1f, 300f);
        if (!orthographic)
        {
            // An asymmetric lens must retain its horizontal and vertical offsets.
            projection.m02 = 0.18f;
            projection.m12 = -0.09f;
        }
        if (oblique)
        {
            projection.m20 += 0.008f;
            projection.m21 -= 0.006f;
        }
        if (depthRange != 0)
            projection.SetRow(2, (projection.GetRow(2) + projection.GetRow(3)) * 0.5f);
        if (depthRange == 2)
            projection.SetRow(2, projection.GetRow(3) - projection.GetRow(2));
        if (flipY)
            projection.SetRow(1, -projection.GetRow(1));
        return projection;
    }

    private static Vector2 ClipToUV(Vector4 clip, bool uvStartsAtTop)
    {
        Vector2 uv = new Vector2(clip.x / clip.w, clip.y / clip.w) * 0.5f + Vector2.one * 0.5f;
        if (uvStartsAtTop)
            uv.y = 1f - uv.y;
        return uv;
    }

    private static Vector2 ApplyScaleBias(Vector2 uv, Vector4 scaleBias) =>
        new Vector2(uv.x * scaleBias.x + scaleBias.z, uv.y * scaleBias.y + scaleBias.w);

    private static bool SupportsContactFormat(GraphicsFormat format) =>
        SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Render) &&
        SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Sample);

    private static void SetFrameTexture(UniversalResourceData resources, string propertyName,
        TextureHandle texture)
    {
        MethodInfo setter = typeof(UniversalResourceData).GetProperty(propertyName,
            BindingFlags.Instance | BindingFlags.Public)?.GetSetMethod(true);
        Assert.That(setter, Is.Not.Null, $"URP frame texture setter {propertyName} must exist.");
        setter.Invoke(resources, new object[] { texture });
    }
}
