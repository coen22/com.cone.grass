using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

[Category("GrassGPU")]
public sealed class GrassMotionLifecycleTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void UnavailableMotionMaterialReleasesExistingCameraHistory(bool missingMaterial)
    {
        using (var fixture = new HistoryFixture())
        {
            Material material = null;
            try
            {
                if (!missingMaterial)
                {
                    Shader shader = Shader.Find("InfiniteGrass/Modifiers/GrassMaskShader");
                    Assert.That(shader, Is.Not.Null);
                    material = new Material(shader);
                    Assert.That(material.FindPass("GrassMotionVectors"), Is.LessThan(0));
                }
                GraphicsBuffer[] buffers = fixture.Buffers;
                RTHandle[] textures = fixture.Textures;

                Assert.That(fixture.Renderer.PrepareCamera(fixture.Camera, material), Is.False);

                Assert.That(fixture.Histories.Count, Is.Zero);
                AssertReleased(buffers, textures);
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }
    }

    [Test]
    public void MissingMotionTargetsReleaseExistingCameraHistory()
    {
        using (var fixture = new HistoryFixture())
        using (var frame = new ContextContainer())
        {
            frame.Create<UniversalCameraData>().camera = fixture.Camera;
            UniversalResourceData resources = frame.Create<UniversalResourceData>();
            // Reproduce URP's recording window without constructing a renderer.
            // Resource handles intentionally remain invalid in this failure case.
            typeof(UniversalResourceData).BaseType.GetMethod("InitFrame", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(resources, null);
            GraphicsBuffer[] buffers = fixture.Buffers;
            RTHandle[] textures = fixture.Textures;

            fixture.Renderer.Record(null, frame, default, default, default,
                null, null, null, null, null, null, null, null, null, 1f);

            Assert.That(fixture.Histories.Count, Is.Zero);
            AssertReleased(buffers, textures);
        }
    }

    [Test]
    public void LostSnapshotTextureIsRecreatedAndInvalidatesBothFrameSnapshots()
    {
        using (var fixture = new HistoryFixture())
        {
            SetField(fixture.History, "RenderedFrame", 28);
            SetField(fixture.History, "LatestIndex", 1);
            SetField(fixture.History, "PreviousValid", true);
            foreach (object snapshot in fixture.Snapshots)
                SetField(snapshot, "FrameNumber", 28);

            // Release only GPU storage: the RTHandle, dimensions and format stay
            // unchanged, so a descriptor-only reallocation check misses this.
            RTHandle lost = (RTHandle)GetField(fixture.Snapshots.GetValue(0), "Slope");
            Assert.That(lost.rt.IsCreated(), Is.True);
            lost.rt.Release();
            Assert.That(lost.rt.IsCreated(), Is.False);

            Assert.That(fixture.EnsureAllocation(), Is.True);

            Assert.That(GetField(fixture.History, "RenderedFrame"), Is.EqualTo(-1));
            Assert.That(GetField(fixture.History, "PreviousValid"), Is.False);
            foreach (object snapshot in fixture.Snapshots)
                Assert.That(GetField(snapshot, "FrameNumber"), Is.EqualTo(-1));
            foreach (RTHandle texture in fixture.Textures)
                Assert.That(texture.rt && texture.rt.IsCreated(), Is.True);
        }
    }

    [TestCase("Keys", -1)]
    [TestCase("DispatchArguments", -1)]
    [TestCase("Roots", 0)]
    [TestCase("Counts", 0)]
    [TestCase("Roots", 1)]
    [TestCase("Counts", 1)]
    public void LostHistoryBufferIsRecreatedAndInvalidatesBothFrameSnapshots(string field, int snapshotIndex)
    {
        using (var fixture = new HistoryFixture())
        {
            SetField(fixture.History, "RenderedFrame", 28);
            SetField(fixture.History, "LatestIndex", 1);
            SetField(fixture.History, "PreviousValid", true);
            foreach (object snapshot in fixture.Snapshots)
                SetField(snapshot, "FrameNumber", 28);

            object owner = snapshotIndex < 0 ? fixture.History : fixture.Snapshots.GetValue(snapshotIndex);
            GraphicsBuffer lost = (GraphicsBuffer)GetField(owner, field);
            lost.Dispose();
            Assert.That(lost.IsValid(), Is.False);

            Assert.That(fixture.EnsureAllocation(), Is.True);

            Assert.That(GetField(owner, field), Is.Not.SameAs(lost));
            Assert.That(GetField(fixture.History, "RenderedFrame"), Is.EqualTo(-1));
            Assert.That(GetField(fixture.History, "PreviousValid"), Is.False);
            foreach (object snapshot in fixture.Snapshots)
                Assert.That(GetField(snapshot, "FrameNumber"), Is.EqualTo(-1));
            foreach (GraphicsBuffer buffer in fixture.Buffers)
                Assert.That(buffer.IsValid(), Is.True);
            Assert.That(fixture.EnsureAllocation(), Is.False,
                "Recovering one lost allocation must return to a stable allocation on the next frame.");
        }
    }

    [Test]
    public void WindSnapshotMatchesGpuMipZeroWhenTextureQualityChanges()
    {
        using (var fixture = new HistoryFixture())
        {
            Texture2D wind = AssetDatabase.LoadAssetAtPath<Texture2D>(
                "Packages/com.cone.grass/Runtime/Textures/Wind Texture.png");
            Assert.That(wind, Is.Not.Null);
            Assert.That(wind.mipmapCount, Is.GreaterThan(1));
            Assert.That(wind.ignoreMipmapLimit, Is.False);
            int originalLimit = QualitySettings.globalTextureMipmapLimit;
            try
            {
                QualitySettings.globalTextureMipmapLimit = 0;
                fixture.EnsureAllocation(wind);
                foreach (object snapshot in fixture.Snapshots)
                    SetField(snapshot, "FrameNumber", 28);
                SetField(fixture.History, "RenderedFrame", 28);

                // This imported, non-streaming asset is synchronously re-uploaded.
                // The Texture2D still reports its original asset dimensions.
                QualitySettings.globalTextureMipmapLimit = 1;
                Assert.That(wind.activeMipmapLimit, Is.GreaterThan(0));
                Assert.That(fixture.EnsureAllocation(wind), Is.True);

                foreach (object snapshot in fixture.Snapshots)
                {
                    RTHandle texture = (RTHandle)GetField(snapshot, "Wind");
                    Assert.That(texture.rt.width, Is.EqualTo(Mathf.Max(1, wind.width >> wind.activeMipmapLimit)));
                    Assert.That(texture.rt.height, Is.EqualTo(Mathf.Max(1, wind.height >> wind.activeMipmapLimit)));
                    Assert.That(GetField(snapshot, "FrameNumber"), Is.EqualTo(-1));
                }
            }
            finally
            {
                QualitySettings.globalTextureMipmapLimit = originalLimit;
            }
        }
    }

    [TestCase(true, TextureFormat.RGBAFloat, false)]
    [TestCase(true, TextureFormat.RGBA32, false)]
    [TestCase(true, TextureFormat.RGBA32, true)]
    [TestCase(false, TextureFormat.RGBA32, false)]
    [TestCase(false, TextureFormat.RGBAFloat, false)]
    public void DeformationSnapshotPreservesSampledValuesWithoutHalfPrecisionLoss(
        bool wind, TextureFormat sourceFormat, bool srgb)
    {
        if (!SystemInfo.SupportsTextureFormat(sourceFormat))
            Assert.Ignore("The deformation precision regression requires the requested source texture format.");
        using (var fixture = new HistoryFixture())
        {
            var source = new Texture2D(3, 2, sourceFormat, false, !srgb)
            {
                filterMode = FilterMode.Bilinear,
                wrapModeU = TextureWrapMode.Repeat,
                wrapModeV = TextureWrapMode.Clamp
            };
            try
            {
                if (!SystemInfo.IsFormatSupported(source.graphicsFormat, GraphicsFormatUsage.Sample) ||
                    !SystemInfo.IsFormatSupported(source.graphicsFormat, GraphicsFormatUsage.Linear))
                    Assert.Ignore("The source texture must support the same linear sampling used by grass deformation.");
                // Distinct texels expose shifted/flipped pixel-center copies.
                // .5002 rounds to .5 in half storage; ordinary UNorm values also
                // change when unnecessarily stored as half floats.
                source.SetPixels(new[]
                {
                    new Color(0.5002f, 0.8f, 0.1f, 0.7f),
                    new Color(0.37f, 0.6123f, 0.3f, 0.45f),
                    new Color(0.8123f, 0.21f, 0.2f, 0.33f),
                    new Color(0.65f, 0.5002f, 0.5f, 0.8f),
                    new Color(0.1345f, 0.89f, 0.7f, 0.61f),
                    new Color(0.78f, 0.3456f, 0.9f, 0.5002f)
                });
                source.Apply(false, false);
                fixture.EnsureAllocation(wind ? source : null, wind ? null : source);
                RTHandle snapshot = (RTHandle)GetField(fixture.Snapshots.GetValue(0), wind ? "Wind" : "Slope");
                Assert.That(snapshot.rt.width, Is.EqualTo(source.width));
                Assert.That(snapshot.rt.height, Is.EqualTo(source.height));
                Assert.That(snapshot.rt.wrapModeU, Is.EqualTo(source.wrapModeU));
                Assert.That(snapshot.rt.wrapModeV, Is.EqualTo(source.wrapModeV));
                Assert.That(snapshot.rt.filterMode, Is.EqualTo(source.filterMode));
                if (source.graphicsFormat == GraphicsFormat.R8G8B8A8_SRGB)
                {
                    Assert.That(snapshot.rt.graphicsFormat, Is.EqualTo(source.graphicsFormat),
                        "Matching only point-sampled values in float storage does not preserve sRGB filtering.");
                    Assert.That(snapshot.rt.sRGB, Is.True);
                }
                CopyDeformation(source, snapshot.rt);

                Vector4[] samples = SampleDeformation(source, snapshot.rt);
                int channels = wind ? 2 : 4;
                for (int pair = 0; pair < samples.Length / 2; pair++)
                    for (int channel = 0; channel < channels; channel++)
                        Assert.That(samples[pair * 2 + 1][channel],
                            Is.EqualTo(samples[pair * 2][channel]).Within(pair < 6 ? 0.000001f : 0.00001f),
                            "Snapshot sample " + pair + " channel " + channel +
                            " must retain the current texture's sampled-space value.");

                // A changed source must diverge from the already captured history.
                // This rejects aliased inputs, zero results and a blank copy pass.
                source.SetPixel(0, 0, new Color(0.125f, 0.875f, 0.5f, 0.5f));
                source.Apply(false, false);
                Vector4[] changed = SampleDeformation(source, snapshot.rt);
                Assert.That(Mathf.Abs(changed[0].x - changed[1].x), Is.GreaterThan(0.05f));
                Assert.That(changed[1].x, Is.EqualTo(samples[1].x).Within(0.000001f));
            }
            finally
            {
                Object.DestroyImmediate(source);
            }
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SameTextureFormatChangeInvalidatesLosslessSnapshots(bool wind)
    {
        if (!SupportsSnapshotFormat(GraphicsFormat.R8G8B8A8_UNorm) ||
            !SupportsSnapshotFormat(GraphicsFormat.R32G32B32A32_SFloat))
            Assert.Ignore("The format transition regression requires exact UNorm and float snapshot storage.");
        using (var fixture = new HistoryFixture())
        {
            var source = new RenderTexture(new RenderTextureDescriptor(4, 4)
            {
                graphicsFormat = GraphicsFormat.R8G8B8A8_UNorm,
                depthBufferBits = 0,
                msaaSamples = 1,
                sRGB = false
            });
            try
            {
                Assert.That(source.Create(), Is.True);
                fixture.EnsureAllocation(wind ? source : null, wind ? null : source);
                string field = wind ? "Wind" : "Slope";
                foreach (object snapshot in fixture.Snapshots)
                {
                    Assert.That(((RTHandle)GetField(snapshot, field)).rt.graphicsFormat,
                        Is.EqualTo(GraphicsFormat.R8G8B8A8_UNorm));
                    SetField(snapshot, "FrameNumber", 28);
                }
                SetField(fixture.History, "RenderedFrame", 28);
                SetField(fixture.History, "PreviousValid", true);

                source.Release();
                source.graphicsFormat = GraphicsFormat.R32G32B32A32_SFloat;
                Assert.That(source.Create(), Is.True);
                Assert.That(fixture.EnsureAllocation(wind ? source : null, wind ? null : source), Is.True);
                foreach (object snapshot in fixture.Snapshots)
                {
                    Assert.That(((RTHandle)GetField(snapshot, field)).rt.graphicsFormat,
                        Is.EqualTo(SelectSnapshotFormat(source, wind)));
                    Assert.That(GetField(snapshot, "FrameNumber"), Is.EqualTo(-1));
                }
                Assert.That(GetField(fixture.History, "RenderedFrame"), Is.EqualTo(-1));
                Assert.That(GetField(fixture.History, "PreviousValid"), Is.False);
                Assert.That(fixture.EnsureAllocation(wind ? source : null, wind ? null : source), Is.False);
            }
            finally
            {
                source.Release();
                Object.DestroyImmediate(source);
            }
        }
    }

    [Test]
    public void EncodedSnapshotCopiesTheActiveMipWithoutChangingItsFilteredValues()
    {
        using (var fixture = new HistoryFixture())
        {
            int originalLimit = QualitySettings.globalTextureMipmapLimit;
            var source = new Texture2D(6, 4, TextureFormat.RGBA32, true, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapModeU = TextureWrapMode.Repeat,
                wrapModeV = TextureWrapMode.Clamp,
                ignoreMipmapLimit = false
            };
            try
            {
                Assert.That(source.graphicsFormat, Is.EqualTo(GraphicsFormat.R8G8B8A8_SRGB));
                QualitySettings.globalTextureMipmapLimit = 0;
                source.SetPixels(new Color[24], 0);
                source.SetPixels(new[]
                {
                    new Color(0.5f, 0.8f, 0f, 1f), new Color(0.37f, 0.61f, 0f, 1f), new Color(0.81f, 0.21f, 0f, 1f),
                    new Color(0.65f, 0.5f, 0f, 1f), new Color(0.13f, 0.89f, 0f, 1f), new Color(0.78f, 0.35f, 0f, 1f)
                }, 1);
                source.SetPixels(new[] { Color.white }, 2);
                source.Apply(false, false);
                fixture.EnsureAllocation(source);

                QualitySettings.globalTextureMipmapLimit = 1;
                Assert.That(source.activeMipmapLimit, Is.EqualTo(1));
                Assert.That(fixture.EnsureAllocation(source), Is.True);
                RTHandle snapshot = (RTHandle)GetField(fixture.Snapshots.GetValue(0), "Wind");
                Assert.That(snapshot.rt.width, Is.EqualTo(3));
                Assert.That(snapshot.rt.height, Is.EqualTo(2));
                Assert.That(snapshot.rt.graphicsFormat, Is.EqualTo(source.graphicsFormat));
                CopyDeformation(source, snapshot.rt);
                Vector4[] samples = SampleDeformation(source, snapshot.rt);
                Assert.That(samples[0].x, Is.InRange(0.1f, 0.4f),
                    "The active mip is neither the black full-resolution mip nor the white last mip.");
                for (int pair = 0; pair < samples.Length / 2; pair++)
                    for (int channel = 0; channel < 2; channel++)
                        Assert.That(samples[pair * 2 + 1][channel],
                            Is.EqualTo(samples[pair * 2][channel]).Within(pair < 6 ? 0.000001f : 0.00001f));
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                Object.DestroyImmediate(source);
                QualitySettings.globalTextureMipmapLimit = originalLimit;
            }
        }
    }

    [TestCase(1)]
    [TestCase(4)]
    public void EncodedRenderTextureCopyRequiresCreatedSingleSampleStorage(int samples)
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ||
            !SupportsSnapshotFormat(GraphicsFormat.R8G8B8A8_SRGB))
            Assert.Ignore("The encoded history storage check requires renderable sRGB textures.");
        var descriptor = new RenderTextureDescriptor(4, 4)
        {
            graphicsFormat = GraphicsFormat.R8G8B8A8_SRGB,
            depthBufferBits = 0,
            msaaSamples = samples,
            bindMS = false
        };
        if (SystemInfo.GetRenderTextureSupportedMSAASampleCount(descriptor) != samples)
            Assert.Ignore("The active graphics device cannot create the requested sample count.");
        var source = new RenderTexture(descriptor);
        try
        {
            Assert.That(CanCopyEncodedSnapshot(source, CopyTextureSupport.Basic), Is.False);
            Assert.That(source.Create(), Is.True);
            Assert.That(source.antiAliasing, Is.EqualTo(samples));
            Assert.That(source.graphicsFormat, Is.EqualTo(GraphicsFormat.R8G8B8A8_SRGB));
            Assert.That(source.sRGB, Is.True);
            Assert.That(CanCopyEncodedSnapshot(source, CopyTextureSupport.Basic), Is.EqualTo(samples == 1),
                "CopyTexture requires matching sample counts even when the producer exposes a resolved sampling view.");
            Assert.That(CanCopyEncodedSnapshot(source, CopyTextureSupport.None), Is.False);
            Assert.That(source.IsCreated(), Is.True);
        }
        finally
        {
            source.Release();
            Object.DestroyImmediate(source);
        }
    }

    [Test]
    public void UnsupportedSnapshotFormatsReleaseExistingCameraHistory()
    {
        using (var fixture = new HistoryFixture())
        {
            SetField(fixture.Renderer, "unormSnapshotsSupported", false);
            SetField(fixture.Renderer, "srgbSnapshotsSupported", false);
            SetField(fixture.Renderer, "rg32SnapshotsSupported", false);
            SetField(fixture.Renderer, "rgba32SnapshotsSupported", false);
            GraphicsBuffer[] buffers = fixture.Buffers;
            RTHandle[] textures = fixture.Textures;
            LogAssert.Expect(LogType.Warning,
                "Grass motion history cannot preserve these deformation textures on this device. It requires matching RGBA8 UNorm or sRGB, or 32-bit floating-point snapshot formats supporting rendering and linear sampling. RGBA8 sRGB additionally requires an exact texture copy from a single-sample source.");
            var arguments = new object[] { fixture.Camera, Texture2D.whiteTexture, Texture2D.whiteTexture, null, null };
            MethodInfo resolve = typeof(GrassMotionVectors).GetMethod("TryGetSnapshotFormats", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(resolve.Invoke(fixture.Renderer, arguments), Is.False);
            Assert.That(arguments[3], Is.EqualTo(GraphicsFormat.None));
            Assert.That(arguments[4], Is.EqualTo(GraphicsFormat.None));
            Assert.That(fixture.Histories.Count, Is.Zero);
            AssertReleased(buffers, textures);
            // An unsupported source should not repeat its warning every frame.
            Assert.That(resolve.Invoke(fixture.Renderer, arguments), Is.False);
            LogAssert.NoUnexpectedReceived();
        }
    }

    private static void CopyDeformation(Texture source, RenderTexture destination)
    {
        Shader shader = Resources.Load<Shader>("InfiniteGrassMotionCopy");
        Assert.That(shader, Is.Not.Null);
        Assert.That(shader.isSupported, Is.True);
        var material = new Material(shader);
        var commands = new CommandBuffer { name = "Grass Deformation Snapshot Regression" };
        bool previousSRGBWrite = GL.sRGBWrite;
        RenderTexture previousTarget = RenderTexture.active;
        try
        {
            if (destination.graphicsFormat == GraphicsFormat.R8G8B8A8_SRGB)
            {
                Assert.That(CanCopyEncodedSnapshot(source, SystemInfo.copyTextureSupport), Is.True);
                Assert.That(destination.graphicsFormat, Is.EqualTo(source.graphicsFormat));
                // Match the production copy: mip0 is the active GPU mip; the
                // region API adjusts these logical dimensions for its mip limit.
                commands.CopyTexture(source, 0, 0, 0, 0, source.width, source.height,
                    destination, 0, 0, 0, 0);
            }
            else
            {
                Assert.That(material.FindPass("CopyDeformation"), Is.EqualTo(0));
                ShaderUtil.CompilePass(material, 0, true);
                Assert.That(ShaderUtil.IsPassCompiled(material, 0), Is.True);
                var properties = new MaterialPropertyBlock();
                properties.SetTexture("_GrassHistorySource", source);
                properties.SetVector("_BlitScaleBias", new Vector4(1f, 1f, 0f, 0f));
                commands.SetRenderTarget(destination);
                commands.SetViewport(new Rect(0f, 0f, destination.width, destination.height));
                commands.ClearRenderTarget(false, true, Color.clear);
                commands.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3, 1, properties);
            }
            GL.sRGBWrite = false;
            Graphics.ExecuteCommandBuffer(commands);
        }
        finally
        {
            GL.sRGBWrite = previousSRGBWrite;
            RenderTexture.active = previousTarget;
            commands.Release();
            Object.DestroyImmediate(material);
        }
    }

    private static Vector4[] SampleDeformation(Texture source, Texture history)
    {
        ComputeShader resource = Resources.Load<ComputeShader>("GrassMotionHistoryQuery");
        Assert.That(resource, Is.Not.Null);
        ComputeShader shader = Object.Instantiate(resource);
        GraphicsBuffer queries = null, results = null;
        try
        {
            Vector4[] coordinates =
            {
                new Vector4(0.5f / 3f, 0.25f, 0f, 0f), new Vector4(1.5f / 3f, 0.25f, 0f, 0f), new Vector4(2.5f / 3f, 0.25f, 0f, 0f),
                new Vector4(0.5f / 3f, 0.75f, 0f, 0f), new Vector4(1.5f / 3f, 0.75f, 0f, 0f), new Vector4(2.5f / 3f, 0.75f, 0f, 0f),
                new Vector4(0.35f, 0.4f, 0f, 0f), new Vector4(-0.1f, 1.2f, 0f, 0f), new Vector4(1.1f, -0.2f, 0f, 0f)
            };
            queries = new GraphicsBuffer(GraphicsBuffer.Target.Structured, coordinates.Length, sizeof(float) * 4);
            results = new GraphicsBuffer(GraphicsBuffer.Target.Structured, coordinates.Length * 2, sizeof(float) * 4);
            queries.SetData(coordinates);
            int kernel = shader.FindKernel("QueryDeformationSamples");
            shader.SetInt("_QueryCount", coordinates.Length);
            shader.SetBuffer(kernel, "_Queries", queries);
            shader.SetBuffer(kernel, "_Results", results);
            shader.SetTexture(kernel, "_DeformationSource", source);
            shader.SetTexture(kernel, "_DeformationHistory", history);
            shader.Dispatch(kernel, 1, 1, 1);
            var sampled = new Vector4[coordinates.Length * 2];
            results.GetData(sampled);
            return sampled;
        }
        finally
        {
            queries?.Dispose();
            results?.Dispose();
            Object.DestroyImmediate(shader);
        }
    }

    private static bool SupportsSnapshotFormat(GraphicsFormat format) =>
        SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Render) &&
        SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Sample) &&
        SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Linear);

    private static GraphicsFormat SelectSnapshotFormat(Texture source, bool wind)
    {
        var select = (Func<GraphicsFormat, bool, bool, bool, bool, bool, GraphicsFormat>)typeof(GrassMotionVectors)
            .GetMethod("SelectSnapshotFormat", BindingFlags.Static | BindingFlags.NonPublic)
            .CreateDelegate(typeof(Func<GraphicsFormat, bool, bool, bool, bool, bool, GraphicsFormat>));
        return select(source.graphicsFormat, wind,
            SupportsSnapshotFormat(GraphicsFormat.R8G8B8A8_UNorm),
            SupportsSnapshotFormat(GraphicsFormat.R32G32_SFloat),
            SupportsSnapshotFormat(GraphicsFormat.R32G32B32A32_SFloat),
            SupportsSnapshotFormat(GraphicsFormat.R8G8B8A8_SRGB));
    }

    private static bool CanCopyEncodedSnapshot(Texture source, CopyTextureSupport support) =>
        (bool)typeof(GrassMotionVectors).GetMethod("CanCopyEncodedSnapshot", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { source, support });

    private static void AssertReleased(GraphicsBuffer[] buffers, RTHandle[] textures)
    {
        foreach (GraphicsBuffer buffer in buffers)
            Assert.That(buffer.IsValid(), Is.False, "No root/count/dispatch allocation should outlive unavailable motion.");
        foreach (RTHandle texture in textures)
            Assert.That(texture.rt == null, Is.True, "Interaction and wind history should be released too.");
    }

    private static object GetField(object owner, string name) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(owner);

    private static void SetField(object owner, string name, object value) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(owner, value);

    private sealed class HistoryFixture : IDisposable
    {
        public readonly GrassMotionVectors Renderer = new GrassMotionVectors();
        public readonly Camera Camera;
        public readonly IDictionary Histories;
        public readonly object History;
        public readonly Array Snapshots;

        public HistoryFixture()
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsIndirectArgumentsBuffer ||
                SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("Motion lifetime regression requires actual graphics buffers and render textures.");
            if (!(RenderPipelineManager.currentPipeline is UniversalRenderPipeline))
                Assert.Ignore("Motion lifetime regression requires an initialized URP instance and its RTHandle pool.");
            Camera = new GameObject("Grass motion lifetime test").AddComponent<Camera>();
            try
            {
                Histories = (IDictionary)GetField(Renderer, "cameras");
                Type historyType = typeof(GrassMotionVectors).GetNestedType("CameraHistory", BindingFlags.NonPublic);
                History = Activator.CreateInstance(historyType, true);
                Snapshots = (Array)GetField(History, "Snapshots");
                Histories.Add(Camera, History);
                EnsureAllocation();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public bool EnsureAllocation(Texture wind = null, Texture slope = null)
        {
            wind = wind ? wind : Texture2D.whiteTexture;
            slope = slope ? slope : Texture2D.whiteTexture;
            GraphicsFormat windFormat = SelectSnapshotFormat(wind, true);
            GraphicsFormat slopeFormat = SelectSnapshotFormat(slope, false);
            if (windFormat == GraphicsFormat.None || slopeFormat == GraphicsFormat.None)
                Assert.Ignore("Motion lifetime regression requires compatible lossless snapshot texture formats.");
            if ((windFormat == GraphicsFormat.R8G8B8A8_SRGB && !CanCopyEncodedSnapshot(wind, SystemInfo.copyTextureSupport)) ||
                (slopeFormat == GraphicsFormat.R8G8B8A8_SRGB && !CanCopyEncodedSnapshot(slope, SystemInfo.copyTextureSupport)))
                Assert.Ignore("sRGB motion history requires exact copying from this source texture type.");
            return (bool)History.GetType().GetMethod("EnsureAllocation")
                .Invoke(History, new object[] { 3, slope, wind, slopeFormat, windFormat });
        }

        public GraphicsBuffer[] Buffers
        {
            get
            {
                var buffers = new List<GraphicsBuffer>
                {
                    (GraphicsBuffer)GetField(History, "Keys"),
                    (GraphicsBuffer)GetField(History, "DispatchArguments")
                };
                foreach (object snapshot in Snapshots)
                {
                    buffers.Add((GraphicsBuffer)GetField(snapshot, "Roots"));
                    buffers.Add((GraphicsBuffer)GetField(snapshot, "Counts"));
                }
                return buffers.ToArray();
            }
        }

        public RTHandle[] Textures
        {
            get
            {
                var textures = new List<RTHandle>();
                foreach (object snapshot in Snapshots)
                {
                    textures.Add((RTHandle)GetField(snapshot, "Slope"));
                    textures.Add((RTHandle)GetField(snapshot, "Wind"));
                }
                return textures.ToArray();
            }
        }

        public void Dispose()
        {
            Renderer.Dispose();
            if (Camera)
                Object.DestroyImmediate(Camera.gameObject);
        }
    }
}

public sealed class GrassMotionSnapshotFormatTests
{
    [TestCase(GraphicsFormat.R8G8B8A8_UNorm, true, true, false, false, false, GraphicsFormat.R8G8B8A8_UNorm)]
    [TestCase(GraphicsFormat.R8G8B8A8_UNorm, false, true, false, false, false, GraphicsFormat.R8G8B8A8_UNorm)]
    [TestCase(GraphicsFormat.R8G8B8A8_UNorm, true, false, true, false, false, GraphicsFormat.R32G32_SFloat)]
    [TestCase(GraphicsFormat.R8G8B8A8_UNorm, false, false, true, true, false, GraphicsFormat.R32G32B32A32_SFloat)]
    [TestCase(GraphicsFormat.R8G8B8A8_SRGB, true, true, true, true, true, GraphicsFormat.R8G8B8A8_SRGB)]
    [TestCase(GraphicsFormat.R8G8B8A8_SRGB, false, true, true, true, true, GraphicsFormat.R8G8B8A8_SRGB)]
    [TestCase(GraphicsFormat.R8G8B8A8_SRGB, true, true, true, true, false, GraphicsFormat.None)]
    [TestCase(GraphicsFormat.R8G8B8A8_SRGB, false, true, true, true, false, GraphicsFormat.None)]
    [TestCase(GraphicsFormat.R32G32B32A32_SFloat, true, true, true, true, false, GraphicsFormat.R32G32_SFloat)]
    [TestCase(GraphicsFormat.R16G16B16A16_SFloat, false, true, true, true, false, GraphicsFormat.R32G32B32A32_SFloat)]
    [TestCase(GraphicsFormat.R32G32B32A32_SFloat, true, true, false, true, false, GraphicsFormat.R32G32B32A32_SFloat)]
    [TestCase(GraphicsFormat.R32G32B32A32_SFloat, true, true, false, false, false, GraphicsFormat.None)]
    [TestCase(GraphicsFormat.R32G32B32A32_SFloat, false, true, true, false, false, GraphicsFormat.None)]
    public void FormatSelectionPreservesEncodingAndRequiredChannelsOrDeclinesHistory(GraphicsFormat source,
        bool wind, bool unormSupported, bool rg32Supported, bool rgba32Supported, bool srgbSupported, GraphicsFormat expected)
    {
        var select = (Func<GraphicsFormat, bool, bool, bool, bool, bool, GraphicsFormat>)typeof(GrassMotionVectors)
            .GetMethod("SelectSnapshotFormat", BindingFlags.Static | BindingFlags.NonPublic)
            .CreateDelegate(typeof(Func<GraphicsFormat, bool, bool, bool, bool, bool, GraphicsFormat>));
        Assert.That(select(source, wind, unormSupported, rg32Supported, rgba32Supported, srgbSupported), Is.EqualTo(expected));
    }

    [TestCase(CopyTextureSupport.None, false)]
    [TestCase(CopyTextureSupport.Basic, false)]
    [TestCase(CopyTextureSupport.TextureToRT, false)]
    [TestCase(CopyTextureSupport.Basic | CopyTextureSupport.DifferentTypes, false)]
    [TestCase(CopyTextureSupport.Basic | CopyTextureSupport.TextureToRT, true)]
    public void EncodedTextureCopyRequiresItsActualStorageTransition(CopyTextureSupport support, bool expected)
    {
        var source = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
        try
        {
            MethodInfo check = typeof(GrassMotionVectors).GetMethod("CanCopyEncodedSnapshot", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(check.Invoke(null, new object[] { source, support }), Is.EqualTo(expected));
        }
        finally
        {
            Object.DestroyImmediate(source);
        }
    }
}

public sealed class GrassMotionPassLayoutTests
{
    [Test]
    public void PendingHistoryPassesKeepTheirLodLayoutsWithoutSteadyStateAllocations()
    {
        Type passType = typeof(GrassMotionVectors).GetNestedType("HistoryPass", BindingFlags.NonPublic);
        object previous = Activator.CreateInstance(passType, true);
        object current = Activator.CreateInstance(passType, true);
        var capturePrevious = (Action<int[], int[], Mesh[]>)passType.GetMethod("CaptureLayout")
            .CreateDelegate(typeof(Action<int[], int[], Mesh[]>), previous);
        var captureCurrent = (Action<int[], int[], Mesh[]>)passType.GetMethod("CaptureLayout")
            .CreateDelegate(typeof(Action<int[], int[], Mesh[]>), current);
        Mesh[] meshes =
        {
            InfiniteGrassRenderer.CreateBladeMesh(5),
            InfiniteGrassRenderer.CreateBladeMesh(2),
            InfiniteGrassRenderer.CreateBladeMesh(0)
        };
        try
        {
            int[] offsets = { 0, 2, 4, 0 };
            int[] capacities = { 2, 2, 2, 0 };
            capturePrevious(offsets, capacities, meshes);
            offsets[1] = 4;
            offsets[2] = 5;
            capacities[0] = 4;
            capacities[1] = capacities[2] = 1;
            Array.Reverse(meshes);
            captureCurrent(offsets, capacities, meshes);

            Assert.That(GetLayout(previous, "Offsets"), Is.EqualTo(new[] { 0, 2, 4, 0 }));
            Assert.That(GetLayout(previous, "Capacities"), Is.EqualTo(new[] { 2, 2, 2, 0 }));
            Assert.That(GetLayout(previous, "Rows"), Is.EqualTo(new[] { 6, 3, 1, 0 }));
            Assert.That(GetLayout(current, "Offsets"), Is.EqualTo(offsets));
            Assert.That(GetLayout(current, "Capacities"), Is.EqualTo(capacities));
            Assert.That(GetLayout(current, "Rows"), Is.EqualTo(new[] { 1, 3, 6, 0 }));

            // Warm the exact delegate outside the measurement. Reflection and
            // NUnit allocations are intentionally excluded from this hot path.
            for (int i = 0; i < 16; i++)
                captureCurrent(offsets, capacities, meshes);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 128; i++)
                captureCurrent(offsets, capacities, meshes);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.Zero, "Recording an unchanged motion layout must reuse its pooled pass storage.");

            // Later camera settings updates must not modify a recorded layout.
            offsets[1] = 999;
            capacities[0] = 999;
            Assert.That(GetLayout(current, "Offsets"), Is.EqualTo(new[] { 0, 4, 5, 0 }));
            Assert.That(GetLayout(current, "Capacities"), Is.EqualTo(new[] { 4, 1, 1, 0 }));
        }
        finally
        {
            foreach (Mesh mesh in meshes)
                Object.DestroyImmediate(mesh);
        }
    }

    private static int[] GetLayout(object pass, string name) =>
        (int[])pass.GetType().GetField(name).GetValue(pass);
}
