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
using Object = UnityEngine.Object;

[NonParallelizable]
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
        private readonly GraphicsFormat format;

        public HistoryFixture()
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsIndirectArgumentsBuffer ||
                SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("Motion lifetime regression requires actual graphics buffers and render textures.");
            if (!(RenderPipelineManager.currentPipeline is UniversalRenderPipeline))
                Assert.Ignore("Motion lifetime regression requires an initialized URP instance and its RTHandle pool.");
            format = GraphicsFormat.R16G16B16A16_SFloat;
            if (!SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Render) ||
                !SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Sample) ||
                !SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Linear))
                Assert.Ignore("Motion lifetime regression requires renderable, sampleable floating-point history textures.");

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

        public bool EnsureAllocation(Texture wind = null) => (bool)History.GetType().GetMethod("EnsureAllocation")
            .Invoke(History, new object[] { 3, Texture2D.whiteTexture, wind ? wind : Texture2D.whiteTexture, format });

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
