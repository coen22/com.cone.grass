using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

public sealed class GrassPlacementAreaTests
{
    private readonly List<GameObject> sceneObjects = new List<GameObject>();
    private readonly List<Object> assets = new List<Object>();

    [TearDown]
    public void TearDown()
    {
        for (int i = sceneObjects.Count - 1; i >= 0; i--)
            if (sceneObjects[i])
                Object.DestroyImmediate(sceneObjects[i]);
        for (int i = assets.Count - 1; i >= 0; i--)
            if (assets[i])
                Object.DestroyImmediate(assets[i]);
        sceneObjects.Clear();
        assets.Clear();
    }

    [Test]
    public void LocalMappingPreservesYawAndNonuniformScaleAtKnownWorldCorners()
    {
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(4f, 6f));
        area.transform.position = new Vector3(100f, 5f, -30f);
        area.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
        area.transform.localScale = new Vector3(2f, 1f, 3f);

        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData data), Is.True);
        AssertUv(data.WorldToMask, new Vector3(91f, 25f, -26f), 0f, 0f);
        AssertUv(data.WorldToMask, new Vector3(109f, -10f, -34f), 1f, 1f);
        AssertUv(data.WorldToMask, new Vector3(100f, 0f, -30f), 0.5f, 0.5f);
        Assert.That(data.WorldBounds.size.x, Is.EqualTo(18f).Within(0.0001f));
        Assert.That(data.WorldBounds.size.z, Is.EqualTo(8f).Within(0.0001f));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void UnrepresentableFiniteFootprintsStopCoverageWithoutPublishingInvalidBoundsAndRecover(bool overflow)
    {
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(10f, 10f));
        GrassDensityAsset density = NewDensity();
        density.Fill(1f);
        area.SetDensityAsset(density, false);
        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData original), Is.True);
        var regions = new List<Bounds>();
        Action<GrassPlacementArea, Bounds, GrassPlacementChange> handler = (source, region, changes) =>
        {
            if (source == area)
                regions.Add(region);
        };
        GrassPlacementArea.SourceChanged += handler;
        try
        {
            uint revision = area.SourceRevision, paintRevision = density.Revision;
            // All assigned values are finite. The first case overflows a corner;
            // the second loses the complete ten-metre width to float precision.
            area.transform.position = new Vector3(overflow ? 2e38f : 1e20f, 0f, 0f);
            var serialized = new SerializedObject(area);
            serialized.FindProperty("size").vector2Value = new Vector2(overflow ? 3e38f : 10f, 10f);
            serialized.ApplyModifiedPropertiesWithoutUndo();

            Assert.That(area.TryGetCaptureData(out _), Is.False);
            Assert.That(area.IntersectsCoverage(original.WorldBounds), Is.False);
            Assert.That(area.Paint(area.transform.position, 1f, 1f, 1f, true), Is.False);
            Assert.That(density.Revision, Is.EqualTo(paintRevision));
            Assert.That(area.SourceRevision, Is.GreaterThan(revision));
            Assert.That(regions.Count, Is.EqualTo(1));
            Assert.That(regions[0], Is.EqualTo(original.WorldBounds),
                "Only the old valid footprint should be invalidated when computed geometry becomes unusable.");
            revision = area.SourceRevision;
            Assert.That(area.TryGetCaptureData(out _), Is.False);
            Assert.That(area.SourceRevision, Is.EqualTo(revision),
                "An unchanged invalid frame must not invalidate captures every observation.");

            area.transform.position = Vector3.zero;
            serialized.Update();
            serialized.FindProperty("size").vector2Value = new Vector2(10f, 10f);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData restored), Is.True);
            Assert.That(restored.WorldBounds, Is.EqualTo(original.WorldBounds));
            Assert.That(restored.DensityTexture, Is.SameAs(original.DensityTexture));
            Assert.That(area.SourceRevision, Is.GreaterThan(revision));
            Assert.That(regions.Count, Is.EqualTo(2));
            Assert.That(regions[1], Is.EqualTo(original.WorldBounds));
        }
        finally
        {
            GrassPlacementArea.SourceChanged -= handler;
        }
    }

    [TestCase(1f, 1f)]
    [TestCase(-1f, 1f)]
    [TestCase(1f, -1f)]
    public void TerrainMappingUsesTerrainOriginAndRetainsItsLayerTexturePhase(float xSign, float ySign)
    {
        Terrain terrain = NewTerrain(new Vector3(-50f, 7f, 120f), new Vector3(200f, 35f, 100f));
        var layer = new TerrainLayer { tileSize = new Vector2(10f * xSign, 20f * ySign), tileOffset = new Vector2(2f, -4f) };
        assets.Add(layer);
        terrain.terrainData.terrainLayers = new[] { layer };
        GrassPlacementArea area = NewArea();
        area.transform.position = new Vector3(900f, 40f, -1000f);
        area.transform.rotation = Quaternion.Euler(0f, 37f, 0f);
        area.transform.localScale = new Vector3(7f, 1f, 2f);
        area.ConfigureTexture(terrain, NewTexture(), layer);

        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData data), Is.True);
        AssertUv(data.WorldToMask, new Vector3(-50f, 20f, 120f), 0f, 0f);
        AssertUv(data.WorldToMask, new Vector3(150f, 0f, 220f), 1f, 1f);
        Assert.That(data.TerrainRect, Is.EqualTo(new Vector4(-50f, 120f, 200f, 100f)));
        Assert.That(data.WorldBounds.min.y, Is.EqualTo(7f));
        Assert.That(data.WorldBounds.max.y, Is.EqualTo(42f));

        Vector4 mapping = data.GroundLayerUV;
        Assert.That(-50f * mapping.x + mapping.z, Is.EqualTo(0.2f * xSign).Within(0.0001f));
        Assert.That(120f * mapping.y + mapping.w, Is.EqualTo(-0.2f * ySign).Within(0.0001f));
        Assert.That(-40f * mapping.x + mapping.z, Is.EqualTo(1.2f * xSign).Within(0.0001f));
        Assert.That(140f * mapping.y + mapping.w, Is.EqualTo(0.8f * ySign).Within(0.0001f));
    }

    [Test]
    public void ClippingASpotToItsTerrainDoesNotStretchTheOriginalMaskCoordinates()
    {
        Terrain terrain = NewTerrain(new Vector3(10f, 3f, 20f), new Vector3(100f, 30f, 80f));
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(20f, 20f), terrain);
        area.transform.position = new Vector3(8f, 3f, 24f);

        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData data), Is.True);
        Assert.That(data.WorldBounds.min.x, Is.EqualTo(10f));
        Assert.That(data.WorldBounds.max.x, Is.EqualTo(18f));
        Assert.That(data.WorldBounds.min.z, Is.EqualTo(20f));
        Assert.That(data.WorldBounds.max.z, Is.EqualTo(34f));
        AssertUv(data.WorldToMask, new Vector3(10f, 3f, 20f), 0.6f, 0.3f);

        area.transform.position = new Vector3(-20f, 3f, 24f);
        Assert.That(area.TryGetCaptureData(out _), Is.False);
        Assert.That(area.IntersectsCoverage(new Bounds(terrain.transform.position, Vector3.one * 1000f)), Is.False);
    }

    [Test]
    public void MissingAndNon2DMasksFailClosedWhileUnreadable2DInputRemainsUsable()
    {
        Terrain terrain = NewTerrain(Vector3.zero, new Vector3(100f, 10f, 100f));
        GrassPlacementArea area = NewArea();
        area.ConfigureTexture(terrain, null);
        Assert.That(area.TryGetCaptureData(out _), Is.False);
        Assert.That(area.IntersectsCoverage(new Bounds(new Vector3(50f, 0f, 50f), Vector3.one)), Is.False);

        Texture2D texture = NewTexture();
        texture.Apply(false, true);
        Assert.That(texture.isReadable, Is.False);
        area.ConfigureTexture(terrain, texture);
        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData data), Is.True);
        Assert.That(data.DensityTexture, Is.SameAs(texture));

        var cubemap = new Cubemap(16, TextureFormat.RGBA32, false);
        assets.Add(cubemap);
        area.ConfigureTexture(terrain, cubemap);
        Assert.That(area.TryGetCaptureData(out _), Is.False);

        area.ConfigureTexture(null, texture);
        Assert.That(area.TryGetCaptureData(out _), Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AlphaOnlyExternalDensityStaysEmptyUntilAUsableRedFormatReplacesIt(bool reinitialize)
    {
        if (!SystemInfo.SupportsTextureFormat(TextureFormat.Alpha8) || !SystemInfo.SupportsTextureFormat(TextureFormat.R8))
            Assert.Ignore("This Editor must support constructing both Alpha8 and R8 textures for the format transition.");
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(10f, 10f));
        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData snapshot), Is.True);
        var texture = new Texture2D(16, 16, TextureFormat.Alpha8, false, true);
        assets.Add(texture);
        texture.LoadRawTextureData(new byte[256]);
        texture.Apply(false, false);
        BindLocalTexture(area, texture);

        Assert.That(GrassPlacementDrawData.SupportsDensityFormat(texture), Is.False);
        Assert.That(area.TryGetCaptureData(out _), Is.False);
        Assert.That(area.IntersectsCoverage(snapshot.WorldBounds), Is.False);
        Assert.That(area.DensityTexture, Is.SameAs(texture), "The producer retains the explicitly assigned texture.");
        snapshot.Shape = GrassPlacementShape.Texture;
        snapshot.DensityTexture = texture;
        Assert.That(snapshot.IntersectsCoverage(snapshot.WorldBounds), Is.False);
        uint densityRevision = area.DensityRevision, groundRevision = area.GroundColorRevision, surfaceRevision = area.SurfaceRevision;

        Texture2D replacement;
        if (reinitialize)
        {
            Assert.That(texture.Reinitialize(16, 16, TextureFormat.R8, false), Is.True);
            byte[] red = new byte[256];
            Array.Fill(red, byte.MaxValue);
            texture.LoadRawTextureData(red);
            texture.Apply(false, false);
            replacement = texture;
        }
        else
        {
            replacement = NewTexture();
            BindLocalTexture(area, replacement);
        }
        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData restored), Is.True);
        Assert.That(restored.DensityTexture, Is.SameAs(replacement));
        Assert.That(area.DensityRevision, Is.GreaterThan(densityRevision));
        Assert.That(area.GroundColorRevision, Is.GreaterThan(groundRevision));
        Assert.That(area.SurfaceRevision, Is.EqualTo(surfaceRevision));

        if (!reinitialize)
        {
            area.SetGroundColorTexture(texture, false);
            Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData color), Is.True);
            Assert.That(color.GroundColorTexture, Is.SameAs(texture),
                "The density format contract must not change color-texture readiness.");
        }
    }

    [TestCase(GraphicsFormat.R8_UNorm, true)]
    [TestCase(GraphicsFormat.R8G8B8A8_SRGB, true)]
    [TestCase(GraphicsFormat.R16_SFloat, true)]
    [TestCase(GraphicsFormat.R8_UInt, false)]
    [TestCase(GraphicsFormat.R8_SInt, false)]
    public void DensityFormatClassificationDoesNotAllocateProducerTextureStorage(GraphicsFormat format, bool supported)
    {
        // Unity validates descriptor support even when GPU storage is not created.
        if (!SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Render))
            Assert.Ignore("This Editor cannot construct a RenderTexture descriptor for " + format + ".");
        var descriptor = new RenderTextureDescriptor(16, 16)
        {
            graphicsFormat = format,
            depthStencilFormat = GraphicsFormat.None,
            msaaSamples = 1
        };
        var texture = new RenderTexture(descriptor);
        assets.Add(texture);
        Assert.That(texture.graphicsFormat, Is.EqualTo(format));
        Assert.That(texture.IsCreated(), Is.False);
        Assert.That(GrassPlacementDrawData.SupportsDensityFormat(texture), Is.EqualTo(supported));
        Assert.That(texture.IsCreated(), Is.False);
    }

    [Test]
    public void DepthOnlyTextureCannotSupplyDensity()
    {
        if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.Depth))
            Assert.Ignore("This Editor must support constructing depth RenderTextures.");
        var texture = new RenderTexture(16, 16, 16, RenderTextureFormat.Depth);
        assets.Add(texture);
        Assert.That(GrassPlacementDrawData.SupportsDensityFormat(texture), Is.False);
        Assert.That(texture.IsCreated(), Is.False);
    }

    [Test]
    public void UncreatedRenderTextureDoesNotContributeLiveOrCapturedCoverage()
    {
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(10f, 10f));
        RenderTexture texture = NewRenderTexture();
        BindLocalTexture(area, texture);
        var query = new Bounds(Vector3.zero, Vector3.one);

        Assert.That(texture.IsCreated(), Is.False);
        Assert.That(area.TryGetCaptureData(out _), Is.False);
        Assert.That(area.IntersectsCoverage(query), Is.False);
        var snapshot = new GrassPlacementDrawData
        {
            Shape = GrassPlacementShape.Texture,
            Density = 1f,
            DensityTexture = texture,
            WorldBounds = new Bounds(Vector3.zero, Vector3.one * 10f),
            WorldToMask = Matrix4x4.identity
        };
        Assert.That(snapshot.IntersectsCoverage(query), Is.False);
        Assert.That(texture.IsCreated(), Is.False, "Coverage queries must not allocate a borrowed producer texture.");
    }

    [Test, Category("GrassGPU")]
    public void ReleasedDensityTextureStopsCoverageAndInvalidatesAgainWhenItsProducerRecreatesIt()
    {
        RequireRenderTextureDevice();
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(10f, 10f));
        RenderTexture texture = NewRenderTexture();
        Assert.That(texture.Create(), Is.True);
        BindLocalTexture(area, texture);
        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData original), Is.True);
        uint densityRevision = area.DensityRevision, groundRevision = area.GroundColorRevision, surfaceRevision = area.SurfaceRevision;

        texture.Release();
        Assert.That(area.TryGetCaptureData(out _), Is.False);
        Assert.That(area.IntersectsCoverage(original.WorldBounds), Is.False);
        Assert.That(original.IntersectsCoverage(original.WorldBounds), Is.False);
        Assert.That(texture.IsCreated(), Is.False);
        Assert.That(area.DensityRevision, Is.GreaterThan(densityRevision));
        Assert.That(area.GroundColorRevision, Is.GreaterThan(groundRevision));
        Assert.That(area.SurfaceRevision, Is.EqualTo(surfaceRevision));
        densityRevision = area.DensityRevision;
        groundRevision = area.GroundColorRevision;

        Assert.That(texture.Create(), Is.True);
        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData restored), Is.True);
        Assert.That(restored.DensityTexture, Is.SameAs(texture));
        Assert.That(area.DensityRevision, Is.GreaterThan(densityRevision));
        Assert.That(area.GroundColorRevision, Is.GreaterThan(groundRevision));
        Assert.That(area.SurfaceRevision, Is.EqualTo(surfaceRevision));
    }

    [Test, Category("GrassGPU")]
    public void ReleasedGroundColorTextureUsesFallbackWithoutInvalidatingDensityOrSurface()
    {
        RequireRenderTextureDevice();
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(10f, 10f));
        RenderTexture texture = NewRenderTexture();
        Assert.That(texture.Create(), Is.True);
        area.SetGroundColorTexture(texture, false);
        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData original), Is.True);
        Assert.That(original.GroundColorTexture, Is.SameAs(texture));
        uint densityRevision = area.DensityRevision, groundRevision = area.GroundColorRevision, surfaceRevision = area.SurfaceRevision;

        texture.Release();
        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData fallback), Is.True);
        Assert.That(fallback.GroundColorTexture, Is.Null);
        Assert.That(area.GroundColorTexture, Is.SameAs(texture), "The producer retains its assigned object.");
        Assert.That(texture.IsCreated(), Is.False);
        Assert.That(area.GroundColorRevision, Is.GreaterThan(groundRevision));
        Assert.That(area.DensityRevision, Is.EqualTo(densityRevision));
        Assert.That(area.SurfaceRevision, Is.EqualTo(surfaceRevision));
        groundRevision = area.GroundColorRevision;

        Assert.That(texture.Create(), Is.True);
        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData restored), Is.True);
        Assert.That(restored.GroundColorTexture, Is.SameAs(texture));
        Assert.That(area.GroundColorRevision, Is.GreaterThan(groundRevision));
        Assert.That(area.DensityRevision, Is.EqualTo(densityRevision));
        Assert.That(area.SurfaceRevision, Is.EqualTo(surfaceRevision));
    }

    [TestCase(true)]
    [TestCase(false)]
    [Category("GrassGPU")]
    public void RecreatedTextureSamplingViewsInvalidateOnlyTheirDependentCaptures(bool densitySource)
    {
        RequireRenderTextureDevice();
        if (SystemInfo.supportsMultisampledTextures == 0)
            Assert.Ignore("Sampling-view transitions require multisampled texture storage.");
        var descriptor = new RenderTextureDescriptor(16, 16)
        {
            graphicsFormat = GraphicsFormat.R8G8B8A8_UNorm,
            depthBufferBits = 0,
            msaaSamples = 4,
            bindMS = true,
            sRGB = false
        };
        if (SystemInfo.GetRenderTextureSupportedMSAASampleCount(descriptor) != 4)
            Assert.Ignore("The active graphics device must support the requested 4x MSAA texture.");

        descriptor.bindMS = false;
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(10f, 10f));
        var texture = new RenderTexture(descriptor);
        assets.Add(texture);
        Assert.That(texture.Create(), Is.True);
        Assert.That(texture.antiAliasing, Is.EqualTo(4));
        Assert.That(texture.bindTextureMS, Is.False);
        if (densitySource)
            BindLocalTexture(area, texture);
        else
            area.SetGroundColorTexture(texture, false);
        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData original), Is.True);
        Bounds query = original.WorldBounds;
        var changes = new List<GrassPlacementChange>();
        Action<GrassPlacementArea, Bounds, GrassPlacementChange> handler = (source, region, change) =>
        {
            if (source == area)
                changes.Add(change);
        };
        GrassPlacementArea.SourceChanged += handler;
        try
        {
            var configurations = new[]
            {
                (Samples: 4, BindMS: true, Reject: true),
                (Samples: 4, BindMS: false, Reject: false),
                (Samples: 1, BindMS: true, Reject: false),
                (Samples: 4, BindMS: true, Reject: true),
                (Samples: 1, BindMS: true, Reject: false),
                (Samples: 1, BindMS: false, Reject: false)
            };
            foreach (var configuration in configurations)
            {
                uint densityRevision = area.DensityRevision;
                uint groundRevision = area.GroundColorRevision;
                uint surfaceRevision = area.SurfaceRevision;
                changes.Clear();
                // The area never observes the released interval. Reconfiguration
                // must be detected on this same, created producer object.
                texture.Release();
                texture.antiAliasing = configuration.Samples;
                texture.bindTextureMS = configuration.BindMS;
                Assert.That(texture.Create(), Is.True);
                Assert.That(texture.antiAliasing, Is.EqualTo(configuration.Samples));
                Assert.That(texture.bindTextureMS, Is.EqualTo(configuration.BindMS));

                bool hasCapture = !densitySource || !configuration.Reject;
                Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData captured), Is.EqualTo(hasCapture));
                Assert.That(area.IntersectsCoverage(query), Is.EqualTo(hasCapture));
                Assert.That(original.IntersectsCoverage(query), Is.EqualTo(hasCapture));
                if (densitySource)
                {
                    Assert.That(area.DensityTexture, Is.SameAs(texture));
                    if (hasCapture)
                        Assert.That(captured.DensityTexture, Is.SameAs(texture));
                    Assert.That(area.DensityRevision, Is.GreaterThan(densityRevision));
                }
                else
                {
                    Assert.That(area.GroundColorTexture, Is.SameAs(texture));
                    if (configuration.Reject)
                        Assert.That(captured.GroundColorTexture, Is.Null);
                    else
                        Assert.That(captured.GroundColorTexture, Is.SameAs(texture));
                    Assert.That(area.DensityRevision, Is.EqualTo(densityRevision));
                }
                Assert.That(area.GroundColorRevision, Is.GreaterThan(groundRevision));
                Assert.That(area.SurfaceRevision, Is.EqualTo(surfaceRevision));
                Assert.That(changes, Is.EqualTo(new[] { densitySource
                    ? GrassPlacementChange.Density | GrassPlacementChange.GroundColor
                    : GrassPlacementChange.GroundColor }));
                Assert.That(texture.IsCreated(), Is.True, "Capture queries must preserve producer-owned storage.");

                uint settledRevision = area.SourceRevision;
                Assert.That(area.TryGetCaptureData(out _), Is.EqualTo(hasCapture));
                Assert.That(area.SourceRevision, Is.EqualTo(settledRevision));
                Assert.That(changes.Count, Is.EqualTo(1), "An unchanged sampling view must not repeatedly invalidate captures.");
            }
        }
        finally
        {
            GrassPlacementArea.SourceChanged -= handler;
            texture.Release();
        }
    }

    [Test]
    public void DestroyedPaintedAssetDoesNotActivateAnExternalTextureUntilTheBindingIsCleared()
    {
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(10f, 10f));
        GrassDensityAsset density = NewDensity();
        density.Fill(1f);
        Texture2D external = NewTexture();
        BindLocalTexture(area, external);
        var serialized = new SerializedObject(area);
        serialized.FindProperty("densityAsset").objectReferenceValue = density;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData original), Is.True);
        Assert.That(original.DensityTexture, Is.SameAs(density.Texture));

        Object.DestroyImmediate(density);
        Assert.That(area.DensityTexture, Is.Null);
        Assert.That(area.TryGetCaptureData(out _), Is.False);
        Assert.That(area.IntersectsCoverage(original.WorldBounds), Is.False);
        uint revision = area.DensityRevision;

        serialized.Update();
        serialized.FindProperty("densityAsset").objectReferenceValue = null;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData restored), Is.True);
        Assert.That(restored.DensityTexture, Is.SameAs(external));
        Assert.That(area.DensityRevision, Is.GreaterThan(revision),
            "Deliberately clearing a missing asset binding must invalidate the newly restored external coverage.");
    }

    [Test]
    public void ClearingDestroyedDensityBindingsAllowsExplicitExternalRecovery()
    {
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(10f, 10f));
        GrassDensityAsset density = NewDensity();
        density.Fill(1f);
        area.SetDensityAsset(density, false);
        Assert.That(area.TryGetCaptureData(out _), Is.True);

        Object.DestroyImmediate(density);
        Assert.That(area.TryGetCaptureData(out _), Is.False);
        Assert.That(area.DensityTexture, Is.Null);
        Assert.That(ReferenceEquals(area.DensityAsset, null), Is.False,
            "The missing assigned asset must remain a binding until explicitly cleared.");
        uint revision = area.SourceRevision;
        area.SetDensityAsset(null, false);
        Assert.That(ReferenceEquals(area.DensityAsset, null), Is.True);
        Assert.That(area.SourceRevision, Is.GreaterThan(revision));
        revision = area.SourceRevision;
        area.SetDensityAsset(null, false);
        Assert.That(area.SourceRevision, Is.EqualTo(revision));

        Texture2D external = NewTexture();
        BindLocalTexture(area, external);
        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData restored), Is.True);
        Assert.That(restored.DensityTexture, Is.SameAs(external));

        Object.DestroyImmediate(external);
        Assert.That(area.TryGetCaptureData(out _), Is.False);
        Assert.That(ReferenceEquals(area.DensityTexture, null), Is.False);
        area.SetDensityAsset(null, false);
        Assert.That(ReferenceEquals(area.DensityTexture, null), Is.True,
            "Clearing density bindings must also remove a destroyed external texture wrapper.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DestroyedDensityWrappersStopInvalidatingAfterRebindingOrDisabling(bool disable)
    {
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(10f, 10f));
        GrassDensityAsset destroyed = NewDensity();
        destroyed.Fill(1f);
        area.SetDensityAsset(destroyed, false);
        Assert.That(area.TryGetCaptureData(out _), Is.True);
        Object.DestroyImmediate(destroyed);

        GrassDensityAsset replacement = NewDensity();
        if (disable)
            area.enabled = false;
        else
            area.SetDensityAsset(replacement, false);
        uint revision = area.SourceRevision;
        // NotifyChanged only touches managed fields/events. Its wrapper can outlive
        // the native asset, but no detached area should still receive this event.
        destroyed.NotifyChanged();
        Assert.That(area.SourceRevision, Is.EqualTo(revision));

        if (disable)
        {
            area.SetDensityAsset(replacement, false);
            area.enabled = true;
        }
        Assert.That(area.TryGetCaptureData(out _), Is.False);
        revision = area.SourceRevision;
        replacement.Fill(1f);
        Assert.That(area.SourceRevision, Is.EqualTo(revision + 1u),
            "The new live asset must retain exactly one active subscription.");
        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData restored), Is.True);
        Assert.That(restored.DensityAsset, Is.SameAs(replacement));
    }

    [Test]
    public void ReconfiguringExternalCoverageAfterADestroyedOverrideInvalidatesImmediately()
    {
        Terrain terrain = NewTerrain(Vector3.zero, new Vector3(100f, 10f, 100f));
        GrassPlacementArea area = NewArea();
        Texture2D external = NewTexture();
        area.ConfigureTexture(terrain, external);
        GrassDensityAsset density = NewDensity();
        density.Fill(1f);
        var serialized = new SerializedObject(area);
        serialized.FindProperty("densityAsset").objectReferenceValue = density;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData original), Is.True);
        Assert.That(original.DensityAsset, Is.SameAs(density));
        Object.DestroyImmediate(density);
        Assert.That(area.TryGetCaptureData(out _), Is.False);

        uint revision = area.DensityRevision, surfaceRevision = area.SurfaceRevision;
        int notifications = 0;
        GrassPlacementChange reportedChanges = GrassPlacementChange.None;
        Action<GrassPlacementArea, Bounds, GrassPlacementChange> handler = (source, region, changes) =>
        {
            if (source == area)
            {
                notifications++;
                reportedChanges = changes;
            }
        };
        GrassPlacementArea.SourceChanged += handler;
        try
        {
            area.ConfigureTexture(terrain, external);
            Assert.That(ReferenceEquals(area.DensityAsset, null), Is.True);
            Assert.That(area.DensityRevision, Is.GreaterThan(revision),
                "Clearing the destroyed override must invalidate before a later capture polls the new binding.");
            Assert.That(area.SurfaceRevision, Is.EqualTo(surfaceRevision));
            Assert.That(notifications, Is.EqualTo(1));
            Assert.That(reportedChanges, Is.EqualTo(GrassPlacementChange.Density | GrassPlacementChange.GroundColor));
            Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData restored), Is.True);
            Assert.That(restored.DensityTexture, Is.SameAs(external));

            uint sourceRevision = area.SourceRevision;
            area.ConfigureTexture(terrain, external);
            Assert.That(area.SourceRevision, Is.EqualTo(sourceRevision));
            Assert.That(notifications, Is.EqualTo(1));
        }
        finally
        {
            GrassPlacementArea.SourceChanged -= handler;
        }
    }

    [Test]
    public void RebindingTheSameLiveDensityAssetIsANoopAndKeepsOneSubscription()
    {
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(10f, 10f));
        GrassDensityAsset density = NewDensity();
        density.Fill(1f);
        area.SetDensityAsset(density, false);
        Assert.That(area.TryGetCaptureData(out _), Is.True);
        uint revision = area.SourceRevision, densityRevision = area.DensityRevision, surfaceRevision = area.SurfaceRevision;

        area.SetDensityAsset(density, false);
        area.SetDensityAsset(density, false);
        Assert.That(area.SourceRevision, Is.EqualTo(revision));
        density.Fill(0f);
        Assert.That(area.SourceRevision, Is.EqualTo(revision + 1u));
        Assert.That(area.DensityRevision, Is.EqualTo(densityRevision + 1u));
        Assert.That(area.SurfaceRevision, Is.EqualTo(surfaceRevision));
        Assert.That(area.TryGetCaptureData(out _), Is.False);
    }

    [Test]
    public void PaintedOccupancySkipsEmptyRegionsAndRespondsToErase()
    {
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(20f, 40f));
        var density = ScriptableObject.CreateInstance<GrassDensityAsset>();
        assets.Add(density);
        density.Resize(64, 64, false);
        area.SetDensityAsset(density, false);
        Assert.That(area.TryGetCaptureData(out _), Is.False);

        var region = new Bounds(new Vector3(-7.5f, 0f, 15f), Vector3.one * 0.1f);
        Assert.That(area.IntersectsCoverage(region), Is.False);
        density.Paint(new Vector2(0.125f, 0.875f), new Vector2(0.04f, 0.04f), 1f, 1f, false);
        Assert.That(area.TryGetCaptureData(out _), Is.True);
        Assert.That(area.IntersectsCoverage(region), Is.True);
        Assert.That(area.IntersectsCoverage(new Bounds(new Vector3(7.5f, 0f, -15f), Vector3.one)), Is.False);

        density.Fill(0f);
        Assert.That(area.IntersectsCoverage(region), Is.False);
        Assert.That(area.TryGetCaptureData(out _), Is.False);
    }

    [Test]
    public void CapturedOccupancyUsesItsOriginalFrameAndDoesNotUploadPendingPaint()
    {
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(64f, 64f));
        area.transform.position = new Vector3(100f, 5f, -30f);
        area.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
        area.transform.localScale = new Vector3(-2f, 1f, 3f);
        GrassDensityAsset density = NewDensity();
        Vector2 radius = Vector2.one * (0.25f / 64f);
        density.Paint(new Vector2(8.5f / 64f, 48.5f / 64f), radius, 1f, 1f, false);
        density.Paint(new Vector2(48.5f / 64f, 8.5f / 64f), radius, 1f, 1f, false);
        area.SetDensityAsset(density, false);
        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData snapshot), Is.True);
        Assert.That(snapshot.DensityAsset, Is.SameAs(density));

        var first = new Bounds(new Vector3(149.5f, 0f, 17f), Vector3.one * 0.01f);
        var second = new Bounds(new Vector3(29.5f, 0f, -63f), Vector3.one * 0.01f);
        var empty = new Bounds(new Vector3(101.5f, 0f, -31f), Vector3.one * 0.01f);
        Assert.That(snapshot.IntersectsCoverage(first), Is.True);
        Assert.That(snapshot.IntersectsCoverage(second), Is.True);
        Assert.That(snapshot.IntersectsCoverage(empty), Is.False,
            "The two painted islands enclose this point in their combined bounds, but its pixels are empty.");
        Assert.That(snapshot.IntersectsCoverage(first), Is.EqualTo(area.IntersectsCoverage(first)));
        Assert.That(snapshot.IntersectsCoverage(empty), Is.EqualTo(area.IntersectsCoverage(empty)));

        uint uploads = density.TextureUploadCount;
        density.Paint(new Vector2(32.5f / 64f, 32.5f / 64f), radius, 1f, 1f, false);
        RectInt pending = density.PendingUploadRegion;
        Assert.That(pending.width, Is.GreaterThan(0));
        Assert.That(snapshot.IntersectsCoverage(empty), Is.True,
            "The snapshot retains the mapping, while the asset still owns the CPU pixel data.");
        Assert.That(density.TextureUploadCount, Is.EqualTo(uploads));
        Assert.That(density.PendingUploadRegion, Is.EqualTo(pending));

        area.transform.position += Vector3.right * 1000f;
        Assert.That(area.IntersectsCoverage(first), Is.False);
        Assert.That(snapshot.IntersectsCoverage(first), Is.True,
            "Tile queries must use the same frame as the captured draw, without rereading the scene transform.");
        density.Fill(0f);
        Assert.That(snapshot.IntersectsCoverage(first), Is.False);
        Assert.That(density.TextureUploadCount, Is.EqualTo(uploads));
    }

    [Test]
    public void CapturedOccupancyRejectsMissingOrUnsupportedTexturesAndDefaultData()
    {
        var query = new Bounds(Vector3.zero, Vector3.one);
        Assert.That(default(GrassPlacementDrawData).IntersectsCoverage(query), Is.False);
        var snapshot = new GrassPlacementDrawData
        {
            Shape = GrassPlacementShape.Texture,
            Density = 1f,
            WorldBounds = new Bounds(Vector3.zero, Vector3.one * 10f),
            WorldToMask = Matrix4x4.identity
        };
        Assert.That(snapshot.IntersectsCoverage(query), Is.False);
        var cubemap = new Cubemap(16, TextureFormat.RGBA32, false);
        assets.Add(cubemap);
        snapshot.DensityTexture = cubemap;
        Assert.That(snapshot.IntersectsCoverage(query), Is.False);
        Texture2D unreadable = NewTexture();
        unreadable.Apply(false, true);
        snapshot.DensityTexture = unreadable;
        Assert.That(snapshot.IntersectsCoverage(query), Is.True);
        Assert.That(snapshot.IntersectsCoverage(new Bounds(Vector3.right * 20f, Vector3.one)), Is.False);
    }

    [TestCase(GrassPlacementShape.Box)]
    [TestCase(GrassPlacementShape.Circle)]
    [TestCase(GrassPlacementShape.Texture)]
    public void RotatedShapeOccupancyRejectsEmptyOuterBoundsWithoutDroppingItsBoundary(GrassPlacementShape shape)
    {
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(4f, 80f));
        var serialized = new SerializedObject(area);
        serialized.FindProperty("shape").enumValueIndex = (int)shape;
        if (shape == GrassPlacementShape.Texture)
        {
            Texture2D texture = NewTexture();
            texture.Apply(false, true);
            serialized.FindProperty("densityTexture").objectReferenceValue = texture;
        }
        serialized.ApplyModifiedPropertiesWithoutUndo();
        area.transform.rotation = Quaternion.Euler(0f, 45f, 0f);
        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData snapshot), Is.True);

        var offStrip = new Bounds(new Vector3(20f, 0f, -20f), Vector3.one * 0.1f);
        var onStrip = new Bounds(new Vector3(25f, 0f, 25f), Vector3.one * 0.1f);
        Assert.That(snapshot.WorldBounds.Contains(offStrip.center), Is.True,
            "The empty tile is inside the loose world bounds of the long rotated area.");
        Assert.That(snapshot.IntersectsCoverage(offStrip), Is.False);
        Assert.That(area.IntersectsCoverage(offStrip), Is.False);
        Assert.That(snapshot.IntersectsCoverage(onStrip), Is.True);

        Matrix4x4 maskToWorld = snapshot.WorldToMask.inverse;
        Vector3 nearCorner = maskToWorld.MultiplyPoint3x4(new Vector3(0.95f, 0f, 0.95f));
        Assert.That(snapshot.IntersectsCoverage(new Bounds(nearCorner, Vector3.one * 0.01f)),
            Is.EqualTo(shape != GrassPlacementShape.Circle));
        Vector3 justOutsideEdge = maskToWorld.MultiplyPoint3x4(new Vector3(1.001f, 0f, 0.5f));
        Assert.That(snapshot.IntersectsCoverage(new Bounds(justOutsideEdge, Vector3.one * 0.02f)), Is.True,
            "A query straddling the geometric edge must remain eligible after the caller adds its filter margin.");
    }

    [Test]
    public void MovingAnAreaInvalidatesBothItsPreviousAndCurrentExtent()
    {
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(20f, 40f));
        area.MarkDirty();
        Bounds oldBounds = area.WorldBounds;
        Bounds? reported = null;
        Action<Bounds> handler = value => reported = value;
        GrassPlacementArea.CoverageChanged += handler;
        try
        {
            area.transform.position = new Vector3(50f, 0f, 20f);
            area.MarkDirty();
            Bounds newBounds = area.WorldBounds;

            Assert.That(reported.HasValue, Is.True);
            Assert.That(reported.Value.Contains(oldBounds.min), Is.True);
            Assert.That(reported.Value.Contains(oldBounds.max), Is.True);
            Assert.That(reported.Value.Contains(newBounds.min), Is.True);
            Assert.That(reported.Value.Contains(newBounds.max), Is.True);
        }
        finally
        {
            GrassPlacementArea.CoverageChanged -= handler;
        }
    }

    [Test]
    public void DensityAndGroundEditsAdvanceOnlyTheirRequiredCaptureRevisions()
    {
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(64f, 128f));
        GrassDensityAsset density = NewDensity();
        area.SetDensityAsset(density, false);
        uint oldSurface = area.SurfaceRevision;
        uint oldDensity = area.DensityRevision;
        uint oldGround = area.GroundColorRevision;
        Bounds reported = default;
        GrassPlacementChange reportedChanges = GrassPlacementChange.None;
        Action<GrassPlacementArea, Bounds, GrassPlacementChange> handler = (source, region, changes) =>
        {
            if (source == area)
            {
                reported = region;
                reportedChanges = changes;
            }
        };
        GrassPlacementArea.SourceChanged += handler;
        try
        {
            density.Paint(new Vector2(8.5f / 64f, 48.5f / 64f), Vector2.one * (0.25f / 64f), 1f, 1f, false);
            Assert.That(area.DensityRevision, Is.GreaterThan(oldDensity), "Queued changes must invalidate caches before their bounds are consumed.");
            Assert.That(area.GroundColorRevision, Is.GreaterThan(oldGround));
            Assert.That(area.SurfaceRevision, Is.EqualTo(oldSurface));
            Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData data), Is.True);
            Assert.That(area.SurfaceRevision, Is.EqualTo(oldSurface));
            Assert.That(reportedChanges, Is.EqualTo(GrassPlacementChange.Density | GrassPlacementChange.GroundColor));
            Assert.That(reported.size.x, Is.EqualTo(2f).Within(0.0001f));
            Assert.That(reported.size.z, Is.EqualTo(4f).Within(0.0001f));
            Assert.That(data.WorldBounds, Is.EqualTo(reported));
            AssertUv(data.WorldToMask, new Vector3(-23.5f, 0f, 33f), 8.5f / 64f, 48.5f / 64f);

            oldDensity = area.DensityRevision;
            oldGround = area.GroundColorRevision;
            area.SetGroundColor(new Color(0.2f, 0.6f, 0.3f), 0.8f);
            Assert.That(area.DensityRevision, Is.EqualTo(oldDensity));
            Assert.That(area.GroundColorRevision, Is.GreaterThan(oldGround));
            Assert.That(area.SurfaceRevision, Is.EqualTo(oldSurface));
            oldGround = area.GroundColorRevision;
            area.MarkSurfaceDirty();
            Assert.That(area.SurfaceRevision, Is.GreaterThan(oldSurface));
            Assert.That(area.DensityRevision, Is.EqualTo(oldDensity));
            Assert.That(area.GroundColorRevision, Is.EqualTo(oldGround));
        }
        finally
        {
            GrassPlacementArea.SourceChanged -= handler;
        }
    }

    [Test]
    public void NativeGroupSamplerChangesInvalidateGroundColorWithoutRecapturingDensityOrSurface()
    {
        GrassPlacementArea area = NewGroundSamplerArea(out _, out TerrainLayer[] layers);
        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData original), Is.True);
        Assert.That(original.GroundLayerTexture, Is.SameAs(layers[5].diffuseTexture));
        Assert.That(original.GroundLayerSamplerTexture, Is.SameAs(layers[4].diffuseTexture));
        uint density = area.DensityRevision, surface = area.SurfaceRevision;
        Texture2D sampler = layers[4].diffuseTexture;
        Action[] edits =
        {
            () => sampler.filterMode = FilterMode.Point,
            () => sampler.wrapModeU = TextureWrapMode.Clamp,
            () => sampler.wrapModeV = TextureWrapMode.Mirror,
            () => sampler.mipMapBias = 0.5f,
            () => sampler.anisoLevel = 3,
            () => layers[4].diffuseTexture = NewTexture()
        };
        string[] names = { "filter", "U wrap", "V wrap", "mip bias", "anisotropy", "sampler source" };
        for (int index = 0; index < edits.Length; index++)
        {
            uint ground = area.GroundColorRevision;
            edits[index]();
            Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData changed), Is.True);
            Assert.That(changed.GroundLayerSamplerTexture, Is.SameAs(layers[4].diffuseTexture), names[index]);
            Assert.That(area.GroundColorRevision, Is.GreaterThan(ground), names[index]);
            Assert.That(area.DensityRevision, Is.EqualTo(density), names[index]);
            Assert.That(area.SurfaceRevision, Is.EqualTo(surface), names[index]);
        }
    }

    [Test]
    public void NotifiedTerrainLayerReorderingRefreshesTheGroupSamplerAndPreservesExplicitFallbacks()
    {
        GrassPlacementArea area = NewGroundSamplerArea(out Terrain terrain, out TerrainLayer[] layers);
        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData original), Is.True);
        uint density = area.DensityRevision, surface = area.SurfaceRevision, ground = area.GroundColorRevision;
        TerrainLayer first = layers[0];
        layers[0] = layers[4];
        layers[4] = first;
        terrain.terrainData.terrainLayers = layers;
        area.MarkGroundColorDirty();

        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData reordered), Is.True);
        Assert.That(reordered.GroundLayerSamplerTexture, Is.SameAs(first.diffuseTexture));
        Assert.That(reordered.GroundLayerSamplerTexture, Is.Not.SameAs(original.GroundLayerSamplerTexture));
        Assert.That(area.GroundColorRevision, Is.GreaterThan(ground));
        Assert.That(area.DensityRevision, Is.EqualTo(density));
        Assert.That(area.SurfaceRevision, Is.EqualTo(surface));

        first.diffuseTexture = null;
        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData missingFirstDiffuse), Is.True);
        Assert.That(missingFirstDiffuse.GroundLayerSamplerTexture, Is.SameAs(Texture2D.grayTexture),
            "An associated group keeps the native default sampler when its first diffuse is missing.");

        GrassPlacementArea unassociated = NewArea();
        ConfigureLocal(unassociated, new Vector2(10f, 10f));
        unassociated.ConfigureGroundLayer(layers[5]);
        Assert.That(unassociated.TryGetCaptureData(out GrassPlacementDrawData local), Is.True);
        Assert.That(local.GroundLayerSamplerTexture, Is.SameAs(layers[5].diffuseTexture));
    }

    [Test]
    public void WarmedGroundSamplerCapturesDoNotAllocateTerrainLayerArrays()
    {
        GrassPlacementArea area = NewGroundSamplerArea(out _, out _);
        bool captured = true;
        for (int index = 0; index < 8; index++)
            captured &= area.TryGetCaptureData(out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 32; index++)
            captured &= area.TryGetCaptureData(out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(captured, Is.True);
        Assert.That(allocated, Is.Zero,
            "Repeated capture snapshots must reuse terrain-layer topology until it changes or a producer marks it dirty.");
    }

    [Test]
    public void ReadyGroundColorOverrideIgnoresChangesToItsUnusedTerrainGroupSampler()
    {
        GrassPlacementArea area = NewGroundSamplerArea(out _, out TerrainLayer[] layers);
        area.SetGroundColorTexture(NewTexture(), false);
        Assert.That(area.TryGetCaptureData(out _), Is.True);
        uint density = area.DensityRevision, surface = area.SurfaceRevision, ground = area.GroundColorRevision;
        Texture2D sampler = layers[4].diffuseTexture;
        sampler.filterMode = FilterMode.Point;
        sampler.wrapModeU = TextureWrapMode.Clamp;
        sampler.wrapModeV = TextureWrapMode.Mirror;

        Assert.That(area.TryGetCaptureData(out _), Is.True);
        Assert.That(area.GroundColorRevision, Is.EqualTo(ground),
            "The color override uses its fixed capture sampler, so the unused TerrainLayer sampler cannot change its pixels.");
        Assert.That(area.DensityRevision, Is.EqualTo(density));
        Assert.That(area.SurfaceRevision, Is.EqualTo(surface));
    }

    [Test]
    public void RebindingIdenticalInputsDoesNotInvalidateAndMaskReplacementDoesNotRebuildTheSurface()
    {
        Terrain terrain = NewTerrain(Vector3.zero, new Vector3(100f, 10f, 100f));
        GrassPlacementArea area = NewArea();
        Texture2D first = NewTexture();
        area.ConfigureTexture(terrain, first);
        uint source = area.SourceRevision;
        uint surface = area.SurfaceRevision;
        area.ConfigureTexture(terrain, first);
        Assert.That(area.SourceRevision, Is.EqualTo(source));
        area.TryGetCaptureData(out _);
        Assert.That(area.SourceRevision, Is.EqualTo(source));

        uint oldDensity = area.DensityRevision;
        area.ConfigureTexture(terrain, NewTexture());
        Assert.That(area.DensityRevision, Is.GreaterThan(oldDensity));
        Assert.That(area.SurfaceRevision, Is.EqualTo(surface));
        uint beforeWrite = area.DensityRevision;
        area.MarkCoverageDirty();
        Assert.That(area.DensityRevision, Is.GreaterThan(beforeWrite));
        Assert.That(area.SurfaceRevision, Is.EqualTo(surface));
    }

    [Test]
    public void ASimultaneousSerializedColorEditIsNotRestrictedToAPendingBrushRectangle()
    {
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(64f, 64f));
        GrassDensityAsset density = NewDensity();
        density.Fill(1f);
        area.SetDensityAsset(density, false);
        Assert.That(area.TryGetCaptureData(out _), Is.True);
        uint surface = area.SurfaceRevision;
        Bounds reported = default;
        Action<GrassPlacementArea, Bounds, GrassPlacementChange> handler = (source, bounds, changes) =>
        {
            if (source == area)
                reported = bounds;
        };
        GrassPlacementArea.SourceChanged += handler;
        try
        {
            density.Paint(new Vector2(8.5f / 64f, 48.5f / 64f), Vector2.one * (0.25f / 64f), 1f, 1f, true);
            var serialized = new SerializedObject(area);
            serialized.FindProperty("groundTint").colorValue = Color.green;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            Assert.That(area.TryGetCaptureData(out _), Is.True);
            Assert.That(reported.size.x, Is.EqualTo(64f));
            Assert.That(reported.size.z, Is.EqualTo(64f));
            Assert.That(area.SurfaceRevision, Is.EqualTo(surface));
        }
        finally
        {
            GrassPlacementArea.SourceChanged -= handler;
        }
    }

    [Test]
    public void AnExternalMaskCanCropItsDrawWithoutStretchingItsOriginalUvFrame()
    {
        Terrain terrain = NewTerrain(new Vector3(10f, 7f, -30f), new Vector3(100f, 20f, 200f));
        GrassPlacementArea area = NewArea();
        area.ConfigureTexture(terrain, NewTexture());
        area.SetTextureCoverageBounds(new Rect(0.2f, 0.6f, 0.1f, 0.2f));

        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData data), Is.True);
        Assert.That(data.WorldBounds.min.x, Is.EqualTo(30f).Within(0.0001f));
        Assert.That(data.WorldBounds.max.x, Is.EqualTo(40f).Within(0.0001f));
        Assert.That(data.WorldBounds.min.z, Is.EqualTo(90f).Within(0.0001f));
        Assert.That(data.WorldBounds.max.z, Is.EqualTo(130f).Within(0.0001f));
        Vector3 firstCorner = data.LocalToWorld.MultiplyPoint3x4(new Vector3(-0.5f, 0f, -0.5f));
        AssertUv(data.WorldToMask, firstCorner, 0.2f, 0.6f);
        Assert.That(area.IntersectsCoverage(new Bounds(new Vector3(15f, 10f, -20f), Vector3.one)), Is.False);
        Assert.That(area.IntersectsCoverage(new Bounds(new Vector3(35f, 10f, 100f), Vector3.one)), Is.True);
        Assert.That(data.IntersectsCoverage(new Bounds(new Vector3(15f, 10f, -20f), Vector3.one)), Is.False);
        Assert.That(data.IntersectsCoverage(new Bounds(new Vector3(35f, 10f, 100f), Vector3.one)), Is.True);

        area.SetTextureCoverageBounds(new Rect(float.NaN, 0f, 1f, 1f));
        Assert.That(area.TryGetCaptureData(out _), Is.False);
        Assert.That(area.IntersectsCoverage(data.WorldBounds), Is.False);
    }

    [Test]
    public void RotatedCroppedTextureRejectsEmptyCropCornersAndKeepsItsCapturedExtent()
    {
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(80f, 80f));
        Texture2D texture = NewTexture();
        texture.Apply(false, true);
        var serialized = new SerializedObject(area);
        serialized.FindProperty("shape").enumValueIndex = (int)GrassPlacementShape.Texture;
        serialized.FindProperty("densityTexture").objectReferenceValue = texture;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        area.transform.rotation = Quaternion.Euler(0f, 45f, 0f);
        Rect originalCrop = new Rect(0.48f, 0.05f, 0.04f, 0.9f);
        area.SetTextureCoverageBounds(originalCrop);
        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData snapshot), Is.True);
        Assert.That(snapshot.CoverageUV, Is.EqualTo(originalCrop));

        var emptyCorner = new Bounds(new Vector3(20f, 0f, -20f), Vector3.one * 0.1f);
        var covered = new Bounds(new Vector3(20f, 0f, 20f), Vector3.one * 0.1f);
        Assert.That(snapshot.WorldBounds.Contains(emptyCorner.center), Is.True);
        Assert.That(snapshot.IntersectsCoverage(emptyCorner), Is.False,
            "The crop's loose world bounds must not dispatch empty tiles inside the uncropped mask.");
        Assert.That(area.IntersectsCoverage(emptyCorner), Is.False);
        Assert.That(snapshot.IntersectsCoverage(covered), Is.True);
        Assert.That(area.IntersectsCoverage(covered), Is.True);

        Vector3 edge = snapshot.WorldToMask.inverse.MultiplyPoint3x4(new Vector3(0.52005f, 0f, 0.5f));
        var crossingEdge = new Bounds(edge, Vector3.one * 0.02f);
        Assert.That(snapshot.IntersectsCoverage(crossingEdge), Is.True,
            "A tile footprint reaching the cropped edge remains eligible.");

        area.SetTextureCoverageBounds(new Rect(0.1f, 0.05f, 0.04f, 0.9f));
        Assert.That(area.IntersectsCoverage(covered), Is.False);
        Assert.That(snapshot.IntersectsCoverage(covered), Is.True,
            "A pending capture keeps the crop it recorded when the live source changes.");
    }

    [Test]
    public void AFullTerrainAlbedoUsesIndependentUvCoordinatesAndKeepsItsAuthoredStrengthForSparseGrass()
    {
        Terrain terrain = NewTerrain(new Vector3(-50f, 5f, 120f), new Vector3(200f, 30f, 100f));
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(10f, 20f), terrain);
        area.transform.position = new Vector3(-20f, 5f, 135f);
        var serialized = new SerializedObject(area);
        serialized.FindProperty("density").floatValue = 0.1f;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        Texture2D albedo = NewTexture();
        area.SetGroundColorTexture(albedo, true, 0.75f);

        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData data), Is.True);
        Assert.That(data.GroundColorTexture, Is.SameAs(albedo));
        Assert.That(data.GroundColorUsesTerrainBounds, Is.True);
        Assert.That(data.Density, Is.EqualTo(0.1f));
        Assert.That(data.GroundColorStrength, Is.EqualTo(0.75f));
        AssertUv(data.WorldToMask, area.transform.position, 0.5f, 0.5f);
        AssertUv(data.GroundWorldToMask, area.transform.position, 0.15f, 0.15f);
        AssertUv(data.GroundWorldToMask, terrain.transform.position, 0f, 0f);
        AssertUv(data.GroundWorldToMask, new Vector3(150f, 200f, 220f), 1f, 1f);

        uint densityRevision = area.DensityRevision;
        uint surfaceRevision = area.SurfaceRevision;
        area.SetGroundColorTexture(albedo, false, 0.75f);
        Assert.That(area.TryGetCaptureData(out data), Is.True);
        Assert.That(data.GroundColorUsesTerrainBounds, Is.False);
        AssertUv(data.GroundWorldToMask, area.transform.position, 0.5f, 0.5f);
        Assert.That(area.DensityRevision, Is.EqualTo(densityRevision));
        Assert.That(area.SurfaceRevision, Is.EqualTo(surfaceRevision));
    }

    [Test]
    public void DestroyingAnExplicitTerrainDoesNotRebindALocalSpotToAnUnrelatedSurface()
    {
        Terrain terrain = NewTerrain(Vector3.zero, new Vector3(100f, 10f, 100f));
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(10f, 10f), terrain);
        area.transform.position = new Vector3(50f, 0f, 50f);
        Assert.That(area.TryGetCaptureData(out _), Is.True);
        Object.DestroyImmediate(terrain.gameObject);
        Assert.That(area.TryGetCaptureData(out _), Is.False);
        Assert.That(area.IntersectsCoverage(new Bounds(area.transform.position, Vector3.one)), Is.False);
    }

    [Test]
    public void DestroyingAnExplicitMeshSurfaceDisablesCoverageAndPaintingUntilItsBindingIsCleared()
    {
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(10f, 10f));
        Collider surface = NewMeshSurface(area);
        GrassDensityAsset density = NewDensity();
        density.Fill(1f);
        area.SetDensityAsset(density, false);
        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData original), Is.True);

        Object.DestroyImmediate(surface);
        Assert.That(area.TryGetCaptureData(out _), Is.False);
        Assert.That(area.IntersectsCoverage(original.WorldBounds), Is.False);
        uint revision = density.Revision;
        Assert.That(area.Paint(area.transform.position, 2f, 1f, 1f, true), Is.False);
        Assert.That(density.Revision, Is.EqualTo(revision));

        var serialized = new SerializedObject(area);
        serialized.FindProperty("paintSurface").objectReferenceValue = null;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        Assert.That(area.TryGetCaptureData(out _), Is.True,
            "Explicitly clearing the binding restores the supported generic-surface mode.");
        Assert.That(area.IntersectsCoverage(original.WorldBounds), Is.True);
        Assert.That(area.Paint(area.transform.position, 2f, 1f, 1f, true), Is.True);
    }

    [Test]
    public void ADeactivatedExplicitMeshSurfaceStopsCoverageAndRestoresItWhenReactivated()
    {
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(10f, 10f));
        Collider surface = NewMeshSurface(area);
        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData original), Is.True);

        surface.gameObject.SetActive(false);
        Assert.That(area.TryGetCaptureData(out _), Is.False);
        Assert.That(area.IntersectsCoverage(original.WorldBounds), Is.False);

        surface.gameObject.SetActive(true);
        Assert.That(area.TryGetCaptureData(out _), Is.True);
        Assert.That(area.IntersectsCoverage(original.WorldBounds), Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void HiddenExplicitParentRendererStopsCoverageAndPaintingUntilRestored(bool forceOff)
    {
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(10f, 10f));
        Collider parent = NewMeshSurface(area);
        var child = new GameObject("Explicit child collider");
        sceneObjects.Add(child);
        child.transform.SetParent(parent.transform, false);
        BoxCollider surface = child.AddComponent<BoxCollider>();
        var serialized = new SerializedObject(area);
        serialized.FindProperty("paintSurface").objectReferenceValue = surface;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        GrassDensityAsset density = NewDensity();
        density.Fill(1f);
        area.SetDensityAsset(density, false);
        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData original), Is.True);
        Renderer renderer = parent.GetComponent<Renderer>();
        uint sourceRevision = area.SourceRevision, densityRevision = density.Revision;

        if (forceOff)
            renderer.forceRenderingOff = true;
        else
            renderer.enabled = false;
        Assert.That(area.TryGetCaptureData(out _), Is.False);
        Assert.That(area.IntersectsCoverage(original.WorldBounds), Is.False);
        Assert.That(area.Paint(Vector3.zero, 2f, 1f, 1f, true), Is.False);
        Assert.That(density.Revision, Is.EqualTo(densityRevision));
        Assert.That(area.SourceRevision, Is.GreaterThan(sourceRevision));
        sourceRevision = area.SourceRevision;

        renderer.forceRenderingOff = false;
        renderer.enabled = true;
        Assert.That(area.TryGetCaptureData(out _), Is.True);
        Assert.That(area.IntersectsCoverage(original.WorldBounds), Is.True);
        Assert.That(area.SourceRevision, Is.GreaterThan(sourceRevision));
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void MissingOrIndexlessExplicitMeshCannotSupplyCoverage(bool skinned, bool indexless)
    {
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(10f, 10f));
        Collider surface = NewMeshSurface(area);
        MeshFilter filter = surface.GetComponent<MeshFilter>();
        Mesh originalMesh = filter.sharedMesh;
        Material[] originalMaterials = surface.GetComponent<MeshRenderer>().sharedMaterials;
        SkinnedMeshRenderer skinnedRenderer = null;
        if (skinned)
        {
            Object.DestroyImmediate(surface.GetComponent<MeshRenderer>());
            skinnedRenderer = surface.gameObject.AddComponent<SkinnedMeshRenderer>();
            skinnedRenderer.sharedMesh = originalMesh;
            skinnedRenderer.sharedMaterials = originalMaterials;
        }
        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData original), Is.True);
        Mesh unavailable = null;
        if (indexless)
        {
            unavailable = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.forward } };
            assets.Add(unavailable);
        }
        if (skinned)
            skinnedRenderer.sharedMesh = unavailable;
        else
            filter.sharedMesh = unavailable;
        Assert.That(area.TryGetCaptureData(out _), Is.False);
        Assert.That(area.IntersectsCoverage(original.WorldBounds), Is.False);

        if (skinned)
            skinnedRenderer.sharedMesh = originalMesh;
        else
            filter.sharedMesh = originalMesh;
        Assert.That(area.TryGetCaptureData(out _), Is.True);
        Assert.That(area.IntersectsCoverage(original.WorldBounds), Is.True);
    }

    [Test]
    public void ExplicitMeshWithoutMaterialSlotsStaysEmptyWhileNullOverrideSlotsRemainUsable()
    {
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(10f, 10f));
        Renderer renderer = NewMeshSurface(area).GetComponent<Renderer>();
        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData original), Is.True);

        renderer.sharedMaterials = Array.Empty<Material>();
        Assert.That(area.TryGetCaptureData(out _), Is.False);
        Assert.That(area.IntersectsCoverage(original.WorldBounds), Is.False);

        renderer.sharedMaterials = new Material[1];
        Assert.That(area.TryGetCaptureData(out _), Is.True,
            "A material slot is enough because the height capture provides its own override material.");
        Assert.That(area.IntersectsCoverage(original.WorldBounds), Is.True);
    }

    [Test]
    public void ExplicitMeshNeedsGeometryInASubmeshThatTheHeightCaptureActuallyRecords()
    {
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(10f, 10f));
        Collider surface = NewMeshSurface(area);
        var mesh = new Mesh
        {
            vertices = new[] { Vector3.zero, Vector3.right, Vector3.forward },
            subMeshCount = 2
        };
        assets.Add(mesh);
        mesh.SetIndices(Array.Empty<int>(), MeshTopology.Triangles, 0);
        mesh.SetIndices(new[] { 0, 1, 2 }, MeshTopology.Triangles, 1);
        surface.GetComponent<MeshFilter>().sharedMesh = mesh;
        Renderer renderer = surface.GetComponent<Renderer>();
        renderer.sharedMaterials = new Material[1];
        Assert.That(area.TryGetCaptureData(out _), Is.False,
            "One material slot records only the empty first submesh, not geometry in the second submesh.");
        Assert.That(area.IntersectsCoverage(new Bounds(Vector3.zero, Vector3.one)), Is.False);

        renderer.sharedMaterials = new Material[2];
        Assert.That(area.TryGetCaptureData(out _), Is.True);
        Assert.That(area.IntersectsCoverage(new Bounds(Vector3.zero, Vector3.one)), Is.True);
    }

    [Test]
    public void RemovingTheExplicitMeshRendererDisablesCoverageUntilItsRendererIsRestored()
    {
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(10f, 10f));
        Collider surface = NewMeshSurface(area);
        GrassDensityAsset density = NewDensity();
        density.Fill(1f);
        area.SetDensityAsset(density, false);
        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData original), Is.True);

        Material[] materials = surface.GetComponent<MeshRenderer>().sharedMaterials;
        Object.DestroyImmediate(surface.GetComponent<MeshRenderer>());
        Assert.That(area.TryGetCaptureData(out _), Is.False);
        Assert.That(area.IntersectsCoverage(original.WorldBounds), Is.False);
        uint revision = density.Revision;
        Assert.That(area.Paint(area.transform.position, 2f, 1f, 1f, true), Is.False);
        Assert.That(density.Revision, Is.EqualTo(revision));

        surface.gameObject.AddComponent<MeshRenderer>().sharedMaterials = materials;
        Assert.That(area.TryGetCaptureData(out _), Is.True);
        Assert.That(area.IntersectsCoverage(original.WorldBounds), Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AnExplicitMeshSurfaceAcceptsAParentRendererEvenWithItsColliderDisabled(bool colliderEnabled)
    {
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(10f, 10f));
        Collider parentSurface = NewMeshSurface(area);
        var child = new GameObject("Grass child surface collider");
        sceneObjects.Add(child);
        child.transform.SetParent(parentSurface.transform, false);
        BoxCollider surface = child.AddComponent<BoxCollider>();
        surface.enabled = colliderEnabled;
        var serialized = new SerializedObject(area);
        serialized.FindProperty("paintSurface").objectReferenceValue = surface;
        serialized.ApplyModifiedPropertiesWithoutUndo();

        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData data), Is.True,
            "The visible parent renderer can support grass independently of physics participation.");
        Assert.That(area.IntersectsCoverage(data.WorldBounds), Is.True);
    }

    [TestCase("collider")]
    [TestCase("mesh renderer")]
    [TestCase("skinned renderer")]
    public void SameBoundsSupportingMeshReplacementPublishesOneSurfaceChange(string changedSource)
    {
        GrassPlacementArea area = NewArea();
        NewObservedMeshSupport(area, out MeshCollider collider, out MeshFilter filter, out MeshRenderer renderer);
        Mesh replacement = NewSteppedSupportMesh(true);
        SkinnedMeshRenderer skinned = null;
        if (changedSource == "skinned renderer")
        {
            GameObject owner = renderer.gameObject;
            Object.DestroyImmediate(renderer);
            skinned = owner.AddComponent<SkinnedMeshRenderer>();
            skinned.sharedMesh = filter.sharedMesh;
            skinned.localBounds = filter.sharedMesh.bounds;
            skinned.sharedMaterials = new Material[1];
        }
        Assert.That(replacement.bounds, Is.EqualTo(filter.sharedMesh.bounds));
        Assert.That(replacement.vertices[8].y, Is.Not.EqualTo(filter.sharedMesh.vertices[8].y),
            "The middle strip changes height even though the outer bounds remain equal.");
        Physics.SyncTransforms();
        Bounds colliderBounds = collider.bounds;
        Renderer observedRenderer = skinned ? (Renderer)skinned : renderer;
        Bounds rendererBounds = observedRenderer.bounds;

        AssertSurfaceObservation(area, () =>
        {
            if (changedSource == "collider")
                collider.sharedMesh = replacement;
            else if (skinned)
                skinned.sharedMesh = replacement;
            else
                filter.sharedMesh = replacement;
            Physics.SyncTransforms();
            Assert.That(collider.bounds, Is.EqualTo(colliderBounds));
            Assert.That(observedRenderer.bounds, Is.EqualTo(rendererBounds));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SameBoundsSupportingTransformsPublishOneSurfaceChange(bool changeParentRenderer)
    {
        GrassPlacementArea area = NewArea();
        NewObservedMeshSupport(area, out MeshCollider collider, out _, out MeshRenderer renderer);
        Physics.SyncTransforms();
        Bounds colliderBounds = collider.bounds, rendererBounds = renderer.bounds;
        Matrix4x4 colliderMatrix = collider.transform.localToWorldMatrix;
        Matrix4x4 rendererMatrix = renderer.localToWorldMatrix;

        AssertSurfaceObservation(area, () =>
        {
            // An exact half turn preserves this square mesh's AABB without
            // introducing trigonometric rounding into the old bounds-only oracle.
            Quaternion halfTurn = new Quaternion(0f, 1f, 0f, 0f);
            if (changeParentRenderer)
            {
                renderer.transform.rotation = halfTurn;
                collider.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                Assert.That(collider.transform.localToWorldMatrix, Is.EqualTo(colliderMatrix));
                Assert.That(renderer.localToWorldMatrix, Is.Not.EqualTo(rendererMatrix));
            }
            else
            {
                collider.transform.rotation = halfTurn;
                Assert.That(renderer.localToWorldMatrix, Is.EqualTo(rendererMatrix));
                Assert.That(collider.transform.localToWorldMatrix, Is.Not.EqualTo(colliderMatrix));
            }
            Physics.SyncTransforms();
            Assert.That(collider.bounds, Is.EqualTo(colliderBounds));
            Assert.That(renderer.bounds, Is.EqualTo(rendererBounds));
        });
    }

    [Test]
    public void ChangingRecordedMaterialSlotsPublishesASurfaceChangeWhileTheSourceStaysUsable()
    {
        GrassPlacementArea area = NewArea();
        NewObservedMeshSupport(area, out _, out MeshFilter filter, out MeshRenderer renderer);
        Mesh mesh = filter.sharedMesh;
        int[] indices = mesh.triangles;
        var firstStrip = new int[6];
        var remainingStrips = new int[indices.Length - firstStrip.Length];
        Array.Copy(indices, firstStrip, firstStrip.Length);
        Array.Copy(indices, firstStrip.Length, remainingStrips, 0, remainingStrips.Length);
        mesh.subMeshCount = 2;
        mesh.SetTriangles(firstStrip, 0);
        mesh.SetTriangles(remainingStrips, 1);

        AssertSurfaceObservation(area, () => renderer.sharedMaterials = new Material[2]);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SupportingSceneChangesPublishSurfaceEventsWithoutMovingTheSeparateArea(bool terrainSurface)
    {
        // Public saved-scene loading permits this Edit control to retain the
        // owner's untitled scene, without creating or saving another untitled one.
        var fixture = new SupportingSceneFixture();
        GameObject support = null;
        Scene originalScene = fixture.Source;
        Exception bodyFailure = null;
        try
        {
            GrassPlacementArea area = NewArea();
            if (terrainSurface)
            {
                Terrain terrain = NewTerrain(new Vector3(-2f, 0f, -2f), new Vector3(4f, 0.5f, 4f));
                ConfigureLocal(area, new Vector2(4f, 4f), terrain);
                support = terrain.gameObject;
            }
            else
            {
                NewObservedMeshSupport(area, out _, out _, out MeshRenderer renderer);
                support = renderer.gameObject;
            }
            Assert.That(support.scene, Is.EqualTo(originalScene));
            fixture.RestorePriorActive();
            AssertSurfaceObservation(area, () => SceneManager.MoveGameObjectToScene(support, fixture.Target));
            Assert.That(area.gameObject.scene, Is.EqualTo(originalScene));
            AssertSurfaceObservation(area, () => SceneManager.MoveGameObjectToScene(support, originalScene));
        }
        catch (Exception error)
        {
            bodyFailure = error;
            throw;
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            try
            {
                if (support && originalScene.IsValid() && originalScene.isLoaded && support.scene != originalScene)
                    SceneManager.MoveGameObjectToScene(support, originalScene);
            }
            catch (Exception error) { cleanupFailures.Add(error); }
            try { fixture.Dispose(); }
            catch (Exception error) { cleanupFailures.Add(error); }
            if (cleanupFailures.Count > 0)
            {
                if (bodyFailure != null)
                    cleanupFailures.Insert(0, bodyFailure);
                throw new AggregateException("Supporting-scene body/owned cleanup failures retained.", cleanupFailures);
            }
        }
    }

    private sealed class SupportingSceneFixture : IDisposable
    {
        private const string TemplateGuid = "c0293ca4b83f495b84033719ef793631";
        private readonly Scene priorActive;
        private readonly PriorScene[] priorScenes;
        private readonly SceneSetup[] priorSetup;
        private readonly string templatePath;
        private readonly Hash128 templateHash;
        private readonly string copyPath;
        private bool copied;
        public Scene Source { get; private set; }
        public Scene Target { get; private set; }

        private readonly struct PriorScene
        {
            public readonly Scene Scene;
            public readonly string Path, Name;
            public readonly bool Loaded, Dirty;
            public readonly GameObject[] Roots;

            public PriorScene(Scene scene)
            {
                Scene = scene; Path = scene.path; Name = scene.name;
                Loaded = scene.isLoaded; Dirty = scene.isDirty;
                Roots = scene.isLoaded ? scene.GetRootGameObjects() : Array.Empty<GameObject>();
            }
        }

        public SupportingSceneFixture()
        {
            Assert.That(EditorApplication.isPlaying, Is.False, "This scene control retains its Edit-only domain.");
            priorActive = SceneManager.GetActiveScene();
            priorSetup = EditorSceneManager.GetSceneManagerSetup();
            priorScenes = new PriorScene[SceneManager.sceneCount];
            for (int i = 0; i < priorScenes.Length; i++)
                priorScenes[i] = new PriorScene(SceneManager.GetSceneAt(i));
            templatePath = AssetDatabase.GUIDToAssetPath(TemplateGuid);
            Assert.That(AssetDatabase.LoadAssetAtPath<SceneAsset>(templatePath), Is.Not.Null);
            Assert.That(SceneManager.GetSceneByPath(templatePath).IsValid(), Is.False,
                "Never take ownership of a fixture scene already present in the owner's hierarchy.");
            templateHash = AssetDatabase.GetAssetDependencyHash(templatePath);
            copyPath = AssetDatabase.GenerateUniqueAssetPath("Assets/GrassSupportingScene60_" + Guid.NewGuid().ToString("N") + ".unity");
            Assert.That(AssetDatabase.AssetPathToGUID(copyPath), Is.Empty);
            try
            {
                copied = AssetDatabase.CopyAsset(templatePath, copyPath);
                Assert.That(copied, Is.True, "Only a successfully created unique asset copy is owned for cleanup.");
                Source = OpenOwnedScene(copyPath);
                Target = OpenOwnedScene(templatePath);
                AssertEmptyNormalScene(Source, copyPath);
                AssertEmptyNormalScene(Target, templatePath);
                Assert.That(Source, Is.Not.EqualTo(Target));
                Assert.That(SceneManager.sceneCount, Is.EqualTo(priorScenes.Length + 2));
                Assert.That(SceneManager.SetActiveScene(Source), Is.True);
            }
            catch (Exception bodyError)
            {
                try { Dispose(); }
                catch (Exception cleanupError)
                { throw new AggregateException("Scene acquisition/owned cleanup failures retained.", bodyError, cleanupError); }
                throw;
            }
        }

        private Scene OpenOwnedScene(string path)
        {
            Scene opened = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            // A failed/ambiguous return must never grant ownership of an old
            // scene. Leave any unproven partial acquisition for diagnosis.
            Assert.That(opened.IsValid(), Is.True);
            Assert.That(opened.path, Is.EqualTo(path));
            foreach (PriorScene previous in priorScenes)
                Assert.That(opened, Is.Not.EqualTo(previous.Scene));
            return opened;
        }

        private static void AssertEmptyNormalScene(Scene scene, string path)
        {
            Assert.That(scene.IsValid() && scene.isLoaded, Is.True);
            Assert.That(scene.path, Is.EqualTo(path));
            Assert.That(EditorSceneManager.IsPreviewScene(scene), Is.False,
                "The surface-event control requires normal loaded supporting scenes.");
            Assert.That(scene.rootCount, Is.Zero, "The readonly fixture contains no GameObjects or scripts.");
        }

        public void RestorePriorActive()
        {
            Assert.That(priorActive.IsValid() && priorActive.isLoaded, Is.True);
            Assert.That(SceneManager.SetActiveScene(priorActive), Is.True);
        }

        public void Dispose()
        {
            var failures = new List<Exception>();
            try { RestorePriorActive(); }
            catch (Exception error) { failures.Add(error); }
            foreach (Scene owned in new[] { Target, Source })
            {
                try
                {
                    if (owned.IsValid())
                        Assert.That(EditorSceneManager.CloseScene(owned, true), Is.True);
                }
                catch (Exception error) { failures.Add(error); }
            }
            try
            {
                bool remainingCopyScene = SceneManager.GetSceneByPath(copyPath).IsValid();
                if (copied && !remainingCopyScene)
                {
                    Assert.That(AssetDatabase.DeleteAsset(copyPath), Is.True);
                    copied = false;
                }
                Assert.That(remainingCopyScene, Is.False,
                    "An uncertain or still loaded copy is retained for diagnosis, not deleted.");
                Assert.That(SceneManager.GetSceneByPath(templatePath).IsValid(), Is.False);
                Assert.That(AssetDatabase.GetAssetDependencyHash(templatePath), Is.EqualTo(templateHash),
                    "Opening the readonly template must never save or rewrite its package asset.");
                Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(priorActive));
                Assert.That(SceneManager.sceneCount, Is.EqualTo(priorScenes.Length));
                for (int i = 0; i < priorScenes.Length; i++)
                {
                    PriorScene previous = priorScenes[i];
                    Scene current = SceneManager.GetSceneAt(i);
                    Assert.That(current, Is.EqualTo(previous.Scene));
                    Assert.That(current.path, Is.EqualTo(previous.Path));
                    Assert.That(current.name, Is.EqualTo(previous.Name));
                    Assert.That(current.isLoaded, Is.EqualTo(previous.Loaded));
                    Assert.That(current.isDirty, Is.EqualTo(previous.Dirty));
                    if (current.isLoaded)
                        Assert.That(current.GetRootGameObjects(), Is.EqualTo(previous.Roots));
                }
                SceneSetup[] after = EditorSceneManager.GetSceneManagerSetup();
                Assert.That(after.Length, Is.EqualTo(priorSetup.Length));
                for (int i = 0; i < after.Length; i++)
                {
                    Assert.That(after[i].path, Is.EqualTo(priorSetup[i].path));
                    Assert.That(after[i].isLoaded, Is.EqualTo(priorSetup[i].isLoaded));
                    Assert.That(after[i].isActive, Is.EqualTo(priorSetup[i].isActive));
                }
            }
            catch (Exception error) { failures.Add(error); }
            if (failures.Count > 0)
                throw new AggregateException("Owned-scene cleanup/state checks failed; uncertain artifacts retained.", failures);
        }
    }

    [Test]
    public void WarmedSupportingGeometryObservationsReuseTheirMaterialBuffer()
    {
        GrassPlacementArea area = NewArea();
        NewObservedMeshSupport(area, out _, out _, out _);
        bool captured = true;
        for (int index = 0; index < 8; index++)
            captured &= area.TryGetCaptureData(out _);
        uint revision = area.SourceRevision;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 32; index++)
            captured &= area.TryGetCaptureData(out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(captured, Is.True);
        Assert.That(area.SourceRevision, Is.EqualTo(revision));
        Assert.That(allocated, Is.Zero,
            "Support observation must reuse the warmed material list without allocating mesh or material arrays.");
    }

    [Test]
    public void MovingAnExplicitMeshSurfaceIntoAPreviewSceneDisablesCoverageAndPaintingUntilItReturns()
    {
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(10f, 10f));
        Collider surface = NewMeshSurface(area);
        GrassDensityAsset density = NewDensity();
        density.Fill(1f);
        area.SetDensityAsset(density, false);
        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData original), Is.True);
        Scene originalScene = surface.gameObject.scene;
        Scene preview = EditorSceneManager.NewPreviewScene();
        try
        {
            SceneManager.MoveGameObjectToScene(surface.gameObject, preview);
            Assert.That(area.gameObject.scene, Is.EqualTo(originalScene));
            Assert.That(area.TryGetCaptureData(out _), Is.False);
            Assert.That(area.IntersectsCoverage(original.WorldBounds), Is.False);
            uint revision = density.Revision;
            Assert.That(area.Paint(area.transform.position, 2f, 1f, 1f, true), Is.False);
            Assert.That(density.Revision, Is.EqualTo(revision));

            SceneManager.MoveGameObjectToScene(surface.gameObject, originalScene);
            Assert.That(area.TryGetCaptureData(out _), Is.True);
            Assert.That(area.IntersectsCoverage(original.WorldBounds), Is.True);
        }
        finally
        {
            if (surface && surface.gameObject.scene == preview)
                SceneManager.MoveGameObjectToScene(surface.gameObject, originalScene);
            EditorSceneManager.ClosePreviewScene(preview);
        }
    }

    [Test]
    public void MovingAnExplicitTerrainIntoAPreviewSceneDisablesCoverageAndPaintingUntilItReturns()
    {
        Terrain terrain = NewTerrain(Vector3.zero, new Vector3(100f, 10f, 100f));
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(10f, 10f), terrain);
        area.transform.position = new Vector3(50f, 0f, 50f);
        GrassDensityAsset density = NewDensity();
        density.Fill(1f);
        area.SetDensityAsset(density, false);
        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData original), Is.True);
        Scene originalScene = terrain.gameObject.scene;
        Scene preview = EditorSceneManager.NewPreviewScene();
        try
        {
            SceneManager.MoveGameObjectToScene(terrain.gameObject, preview);
            Assert.That(area.gameObject.scene, Is.EqualTo(originalScene));
            Assert.That(area.TryGetCaptureData(out _), Is.False);
            Assert.That(area.IntersectsCoverage(original.WorldBounds), Is.False);
            uint revision = density.Revision;
            Assert.That(area.Paint(area.transform.position, 2f, 1f, 1f, true), Is.False);
            Assert.That(density.Revision, Is.EqualTo(revision));

            SceneManager.MoveGameObjectToScene(terrain.gameObject, originalScene);
            Assert.That(area.TryGetCaptureData(out _), Is.True);
            Assert.That(area.IntersectsCoverage(original.WorldBounds), Is.True);
        }
        finally
        {
            if (terrain && terrain.gameObject.scene == preview)
                SceneManager.MoveGameObjectToScene(terrain.gameObject, originalScene);
            EditorSceneManager.ClosePreviewScene(preview);
        }
    }

    [Test]
    public void SceneBrushClipsLongOutsideTravelBeforeBudgetingDabsInARotatedScaledMap()
    {
        GrassPlacementArea area = NewStrokeArea(out GrassDensityAsset density, out Editor editor);
        GrassPlacementArea reference = NewStrokeArea(out GrassDensityAsset expected, out Editor referenceEditor);
        Matrix4x4 maskToWorld = area.WorldToMask.inverse;
        Vector3 first = maskToWorld.MultiplyPoint3x4(new Vector3(-50f, 0f, 0.5f));
        Vector3 last = maskToWorld.MultiplyPoint3x4(new Vector3(51f, 0f, 0.5f));
        InvokeEditor(editor, "PaintAlongStroke", area, first, false);
        InvokeEditor(editor, "PaintAlongStroke", area, last, false);

        // The reference contains the same relevant path, with only the half-metre
        // brush overlap beyond each map edge. It needs fewer than 512 normal dabs.
        float radiusU = 0.5f / maskToWorld.MultiplyVector(Vector3.right).magnitude;
        InvokeEditor(referenceEditor, "PaintAlongStroke", reference,
            maskToWorld.MultiplyPoint3x4(new Vector3(-radiusU, 0f, 0.5f)), false);
        InvokeEditor(referenceEditor, "PaintAlongStroke", reference,
            maskToWorld.MultiplyPoint3x4(new Vector3(1f + radiusU, 0f, 0.5f)), false);

        Assert.That(ReadDensitySamples(density), Is.EqualTo(ReadDensitySamples(expected)),
            "Dabs outside the map must not turn a continuous crossing into widely separated painted islands.");
        for (int x = 0; x < density.Width; x++)
            Assert.That(density.Sample(new Vector2((x + 0.5f) / density.Width, 32.5f / density.Height)),
                Is.EqualTo(1f), "The complete crossing must retain its painted center strip.");
        Assert.That(GetEditorField(editor, "lastDab"), Is.EqualTo(last),
            "Continuation must start at the actual cursor endpoint outside the map.");
    }

    [Test]
    public void SceneBrushOutsideOnlyTravelDoesNotPaintOrReconnectFromAnOlderCursorPoint()
    {
        GrassPlacementArea area = NewStrokeArea(out GrassDensityAsset density, out Editor editor);
        GrassPlacementArea reference = NewStrokeArea(out GrassDensityAsset expected, out Editor referenceEditor);
        Matrix4x4 maskToWorld = area.WorldToMask.inverse;
        uint revision = density.Revision;
        InvokeEditor(editor, "PaintAlongStroke", area,
            maskToWorld.MultiplyPoint3x4(new Vector3(-50f, 0f, 0.5f)), false);
        InvokeEditor(editor, "PaintAlongStroke", area,
            maskToWorld.MultiplyPoint3x4(new Vector3(0.5f, 0f, 50f)), false);
        Assert.That(density.HasCoverage, Is.False);
        Assert.That(density.Revision, Is.EqualTo(revision));

        Vector3 center = maskToWorld.MultiplyPoint3x4(new Vector3(0.5f, 0f, 0.5f));
        InvokeEditor(editor, "PaintAlongStroke", area, center, false);
        float radiusV = 0.5f / maskToWorld.MultiplyVector(Vector3.forward).magnitude;
        InvokeEditor(referenceEditor, "PaintAlongStroke", reference,
            maskToWorld.MultiplyPoint3x4(new Vector3(0.5f, 0f, 1f + radiusV)), false);
        InvokeEditor(referenceEditor, "PaintAlongStroke", reference, center, false);

        Assert.That(density.HasCoverage, Is.True);
        Assert.That(ReadDensitySamples(density), Is.EqualTo(ReadDensitySamples(expected)),
            "Re-entry must follow the cursor's latest outside point, preserving the vertical path into the map.");
        Assert.That(density.Sample(new Vector2(0.25f, 0.5f)), Is.Zero,
            "The earlier left-side cursor point must not create a horizontal stripe on re-entry.");
    }

    [Test]
    public void UndoDuringASceneStrokeReleasesItsContinuationAndPreservesRedo()
    {
        GrassPlacementArea area = NewStrokeArea(out GrassDensityAsset density, out Editor editor);
        Undo.IncrementCurrentGroup();
        SetEditorField(editor, "strokeUndoGroup", Undo.GetCurrentGroup());
        Undo.RegisterCompleteObjectUndo(density, "Paint active grass stroke");
        Vector3 center = area.transform.position;
        InvokeEditor(editor, "PaintAlongStroke", area, center, false);
        Assert.That(density.HasCoverage, Is.True);
        byte[] painted = ReadDensitySamples(density);
        Undo.FlushUndoRecordObjects();
        Undo.IncrementCurrentGroup();
        try
        {
            Undo.PerformUndo();
            Assert.That(density.HasCoverage, Is.False);
            Assert.That(GetEditorField(editor, "strokeAsset"), Is.Null);
            Assert.That(GetEditorField(editor, "hasLastDab"), Is.False);
            Assert.That(GetEditorField(editor, "strokeUndoGroup"), Is.EqualTo(-1));
            uint revision = density.Revision;

            InvokeEditor(editor, "PaintAlongStroke", area, center + Vector3.right, false);
            Assert.That(density.HasCoverage, Is.False,
                "A stale drag after Undo must wait for a new MouseDown and its complete-object Undo record.");
            Assert.That(density.Revision, Is.EqualTo(revision));

            Undo.PerformRedo();
            Assert.That(ReadDensitySamples(density), Is.EqualTo(painted),
                "Stroke teardown during Undo must not collapse or replace the already-replayed history.");
            Assert.That(GetEditorField(editor, "strokeAsset"), Is.Null);
        }
        finally
        {
            Undo.ClearUndo(density);
        }
    }

    [Test]
    public void SceneBrushDoesNotConnectDabsAcrossAMissedSurfaceHit()
    {
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(40f, 10f));
        NewMeshSurface(area);
        GrassDensityAsset density = NewDensity();
        area.SetDensityAsset(density, false);
        Editor editor = NewPlacementEditor(area);
        SetEditorField(editor, "strokeAsset", density);
        SetEditorField(editor, "brushRadius", 1f);
        SetEditorField(editor, "brushStrength", 1f);
        SetEditorField(editor, "brushHardness", 1f);
        Physics.SyncTransforms();

        Assert.That(BrushPoint(editor, area, new Ray(new Vector3(-8f, 5f, 0f), Vector3.down), out Vector3 first), Is.True);
        InvokeEditor(editor, "PaintAlongStroke", area, first, false);
        Assert.That(BrushPoint(editor, area, new Ray(new Vector3(30f, 5f, 0f), Vector3.down), out _), Is.False);
        Assert.That(BrushPoint(editor, area, new Ray(new Vector3(8f, 5f, 0f), Vector3.down), out Vector3 second), Is.True);
        InvokeEditor(editor, "PaintAlongStroke", area, second, false);

        Assert.That(density.Sample(new Vector2(0.3f, 0.5f)), Is.GreaterThan(0.9f));
        Assert.That(density.Sample(new Vector2(0.7f, 0.5f)), Is.GreaterThan(0.9f));
        Assert.That(density.Sample(new Vector2(0.5f, 0.5f)), Is.Zero,
            "Returning to the surface must not join separated hits with an unrequested painted stripe.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void InvalidatingSceneBrushInputFinishesItsPendingStroke(bool clearAsset)
    {
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(10f, 10f));
        GrassDensityAsset density = NewDensity();
        area.SetDensityAsset(density, false);
        Editor editor = NewPlacementEditor(area);
        SetEditorField(editor, "paintEnabled", true);
        SetEditorField(editor, "strokeAsset", density);
        SetEditorField(editor, "hasLastDab", true);

        if (clearAsset)
            area.SetDensityAsset(null, false);
        else
            ConfigureLocal(area, new Vector2(10f, 10f));
        InvokeEditor(editor, "OnSceneGUI");

        Assert.That(GetEditorField(editor, "strokeAsset"), Is.Null);
        Assert.That(GetEditorField(editor, "hasLastDab"), Is.False);
        Assert.That(GetEditorField(editor, "strokeUndoGroup"), Is.EqualTo(-1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SceneBrushDoesNotFallBackAfterItsExplicitSupportIsDestroyed(bool terrainSupport)
    {
        Terrain terrain = terrainSupport
            ? NewTerrain(new Vector3(-50f, 0f, -50f), new Vector3(100f, 10f, 100f)) : null;
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(10f, 10f), terrain);
        Collider surface = NewMeshSurface(area);
        Editor editor = NewPlacementEditor(area);
        Physics.SyncTransforms();
        var ray = new Ray(Vector3.up * 5f, Vector3.down);
        Assert.That(BrushPoint(editor, area, ray, out _), Is.True);

        Object.DestroyImmediate(terrainSupport ? (Object)terrain.gameObject : surface);
        Assert.That(BrushPoint(editor, area, ray, out _), Is.False);

        var serialized = new SerializedObject(area);
        serialized.FindProperty("terrain").objectReferenceValue = null;
        serialized.FindProperty("paintSurface").objectReferenceValue = null;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        Assert.That(BrushPoint(editor, area, ray, out Vector3 planePoint), Is.True);
        Assert.That(planePoint.y, Is.EqualTo(area.transform.position.y).Within(0.00001f));
    }

    [Test]
    public void AnAreaRegistersWhenASavedSceneIsOpenedAdditively()
    {
        Scene originalActive = SceneManager.GetActiveScene();
        Scene testScene = default;
        string path = "Assets/GrassPlacementSceneTest_" + Guid.NewGuid().ToString("N") + ".unity";
        try
        {
            testScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            GrassPlacementArea area = NewArea();
            SceneManager.MoveGameObjectToScene(area.gameObject, testScene);
            Assert.That(EditorSceneManager.SaveScene(testScene, path), Is.True);
            Assert.That(EditorSceneManager.CloseScene(testScene, true), Is.True);
            testScene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            GrassPlacementArea loaded = testScene.GetRootGameObjects()[0].GetComponent<GrassPlacementArea>();

            Assert.That(loaded, Is.Not.Null);
            Assert.That(GrassPlacementArea.ActiveAreas, Does.Contain(loaded),
                "OnEnable runs before Scene.isLoaded becomes true; registration must still occur during loading.");
            Assert.That(loaded.TryGetCaptureData(out _), Is.True);
        }
        finally
        {
            if (originalActive.IsValid() && originalActive.isLoaded)
                SceneManager.SetActiveScene(originalActive);
            if (testScene.IsValid() && testScene.isLoaded)
                EditorSceneManager.CloseScene(testScene, true);
            AssetDatabase.DeleteAsset(path);
        }
    }

    [Test, Category("GrassGPU")]
    public void GroundCaptureMatchesFinalTerrainAlbedoAtFullStrengthForSurvivingSparseRoots()
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ||
            !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf) ||
            !SystemInfo.SupportsTextureFormat(TextureFormat.RGBAHalf))
            Assert.Ignore("Ground-capture validation requires a graphics device with half-float texture support.");
        Shader shader = Shader.Find("Hidden/InfiniteGrass/Placement");
        Assert.That(shader, Is.Not.Null);
        var material = new Material(shader);
        assets.Add(material);
        int pass = material.FindPass("PlacementGroundColor");
        Assert.That(pass, Is.GreaterThanOrEqualTo(0));
        ShaderUtil.CompilePass(material, pass, true);
        Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
        var albedo = new Texture2D(2, 2, TextureFormat.RGBAHalf, false, true);
        assets.Add(albedo);
        var color = new Color(0.2f, 0.4f, 0.6f, 1f);
        albedo.SetPixels(new[] { color, color, color, color });
        albedo.Apply(false, false);
        var quad = new Mesh
        {
            vertices = new[] { new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, -0.5f),
                new Vector3(-0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, 0.5f) },
            triangles = new[] { 0, 2, 1, 2, 3, 1 }
        };
        assets.Add(quad);
        Matrix4x4 projection = Matrix4x4.zero;
        projection.SetRow(0, new Vector4(2f, 0f, 0f, 0f));
        projection.SetRow(1, new Vector4(0f, 0f, 2f, 0f));
        projection.SetRow(2, new Vector4(0f, 0f, 0f, 0.5f));
        projection.SetRow(3, new Vector4(0f, 0f, 0f, 1f));
        material.SetMatrix("_GrassCaptureVP", projection);
        material.SetMatrix("_PlacementWorldToMask", Matrix4x4.Translate(new Vector3(0.5f, 0f, 0.5f)));
        material.SetMatrix("_PlacementGroundWorldToMask", Matrix4x4.Translate(new Vector3(0.5f, 0f, 0.5f)));
        material.SetInteger("_PlacementHasTerrain", 0);
        material.SetInteger("_PlacementShape", (int)GrassPlacementShape.Box);
        material.SetInteger("_PlacementHasGroundColor", 1);
        material.SetInteger("_PlacementHasGroundLayer", 0);
        material.SetTexture("_PlacementGroundColorTexture", albedo);
        material.SetColor("_PlacementGroundTint", Color.white);
        material.SetFloat("_PlacementGroundStrength", 0.75f);
        material.SetFloat("_PlacementEdgeFalloff", 0f);
        material.SetFloat("_PlacementDensity", 0.1f);

        RenderTexture capture = RenderTexture.GetTemporary(16, 16, 0,
            RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        Texture2D readback = new Texture2D(16, 16, TextureFormat.RGBAHalf, false, true);
        RenderTexture previous = RenderTexture.active;
        bool previousSRGBWrite = GL.sRGBWrite;
        var commands = new CommandBuffer();
        try
        {
            GL.sRGBWrite = false;
            for (int variant = 0; variant < 3; variant++)
            {
                material.SetInteger("_PlacementGroundColorUsesTerrainBounds", variant == 0 ? 0 : 1);
                material.SetFloat("_PlacementDensity", variant == 2 ? 0f : 0.1f);
                commands.Clear();
                commands.SetRenderTarget(capture);
                commands.SetViewport(new Rect(0, 0, 16, 16));
                commands.ClearRenderTarget(false, true, Color.clear);
                commands.DrawMesh(quad, Matrix4x4.identity, material, 0, pass);
                Graphics.ExecuteCommandBuffer(commands);
                RenderTexture.active = capture;
                readback.ReadPixels(new Rect(0, 0, 16, 16), 0, 0, false);
                readback.Apply(false, false);
                Color captured = readback.GetPixel(8, 8);
                float expectedAlpha = variant == 0 ? 0.075f : variant == 1 ? 0.75f : 0f;
                Assert.That(captured.a, Is.EqualTo(expectedAlpha).Within(0.001f));
                Assert.That(captured.r, Is.EqualTo(color.r * expectedAlpha).Within(0.001f));
                Assert.That(captured.b, Is.EqualTo(color.b * expectedAlpha).Within(0.001f));
            }
        }
        finally
        {
            RenderTexture.active = previous;
            GL.sRGBWrite = previousSRGBWrite;
            commands.Release();
            Object.DestroyImmediate(readback);
            RenderTexture.ReleaseTemporary(capture);
        }
    }

    private GrassDensityAsset NewDensity()
    {
        GrassDensityAsset value = ScriptableObject.CreateInstance<GrassDensityAsset>();
        value.Resize(64, 64, false);
        assets.Add(value);
        return value;
    }

    private GrassPlacementArea NewArea()
    {
        var gameObject = new GameObject("Grass placement test");
        sceneObjects.Add(gameObject);
        return gameObject.AddComponent<GrassPlacementArea>();
    }

    private Terrain NewTerrain(Vector3 origin, Vector3 size)
    {
        var data = new TerrainData { heightmapResolution = 33, size = size };
        assets.Add(data);
        GameObject gameObject = Terrain.CreateTerrainGameObject(data);
        sceneObjects.Add(gameObject);
        gameObject.transform.position = origin;
        return gameObject.GetComponent<Terrain>();
    }

    private GrassPlacementArea NewGroundSamplerArea(out Terrain terrain, out TerrainLayer[] layers)
    {
        terrain = NewTerrain(new Vector3(-10f, 0f, -10f), new Vector3(20f, 10f, 20f));
        layers = new TerrainLayer[6];
        for (int index = 0; index < layers.Length; index++)
        {
            Texture2D diffuse = NewTexture();
            diffuse.filterMode = FilterMode.Bilinear;
            diffuse.wrapMode = TextureWrapMode.Repeat;
            diffuse.mipMapBias = 0f;
            diffuse.anisoLevel = 1;
            layers[index] = new TerrainLayer { diffuseTexture = diffuse, tileSize = Vector2.one };
            assets.Add(layers[index]);
        }
        terrain.terrainData.terrainLayers = layers;
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(10f, 10f), terrain);
        area.ConfigureGroundLayer(layers[5]);
        return area;
    }

    private Texture2D NewTexture()
    {
        var texture = new Texture2D(16, 16, TextureFormat.R8, false, true);
        assets.Add(texture);
        return texture;
    }

    private RenderTexture NewRenderTexture()
    {
        var texture = new RenderTexture(16, 16, 0, RenderTextureFormat.ARGB32);
        assets.Add(texture);
        return texture;
    }

    private static void BindLocalTexture(GrassPlacementArea area, Texture texture)
    {
        var serialized = new SerializedObject(area);
        serialized.FindProperty("shape").enumValueIndex = (int)GrassPlacementShape.Texture;
        serialized.FindProperty("densityTexture").objectReferenceValue = texture;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void RequireRenderTextureDevice()
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ||
            !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGB32))
            Assert.Ignore("RenderTexture loss/recreation checks require an Editor graphics device.");
    }

    private Collider NewMeshSurface(GrassPlacementArea area)
    {
        GameObject gameObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
        sceneObjects.Add(gameObject);
        gameObject.transform.localScale = new Vector3(20f, 1f, 20f);
        Collider surface = gameObject.GetComponent<Collider>();
        var serialized = new SerializedObject(area);
        serialized.FindProperty("paintSurface").objectReferenceValue = surface;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        return surface;
    }

    private void NewObservedMeshSupport(GrassPlacementArea area, out MeshCollider collider,
        out MeshFilter filter, out MeshRenderer renderer)
    {
        ConfigureLocal(area, new Vector2(4f, 4f));
        var parent = new GameObject("Separate observed grass renderer");
        sceneObjects.Add(parent);
        filter = parent.AddComponent<MeshFilter>();
        filter.sharedMesh = NewSteppedSupportMesh(false);
        renderer = parent.AddComponent<MeshRenderer>();
        renderer.sharedMaterials = new Material[1];
        var child = new GameObject("Explicit observed child collider");
        sceneObjects.Add(child);
        child.transform.SetParent(parent.transform, false);
        collider = child.AddComponent<MeshCollider>();
        collider.sharedMesh = filter.sharedMesh;
        var serialized = new SerializedObject(area);
        serialized.FindProperty("paintSurface").objectReferenceValue = collider;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    private Mesh NewSteppedSupportMesh(bool raiseMiddle)
    {
        float[] edges = { -2f, -1f, -0.5f, 0.5f, 2f };
        var vertices = new Vector3[16];
        var indices = new int[24];
        for (int strip = 0; strip < 4; strip++)
        {
            float height = strip == 0 || (raiseMiddle && strip == 2) ? 0.5f : 0f;
            int vertex = strip * 4, index = strip * 6;
            vertices[vertex] = new Vector3(edges[strip], height, -2f);
            vertices[vertex + 1] = new Vector3(edges[strip + 1], height, -2f);
            vertices[vertex + 2] = new Vector3(edges[strip], height, 2f);
            vertices[vertex + 3] = new Vector3(edges[strip + 1], height, 2f);
            indices[index] = vertex;
            indices[index + 1] = vertex + 2;
            indices[index + 2] = vertex + 1;
            indices[index + 3] = vertex + 1;
            indices[index + 4] = vertex + 2;
            indices[index + 5] = vertex + 3;
        }
        var mesh = new Mesh { name = "Same-bounds stepped grass support", vertices = vertices, triangles = indices };
        assets.Add(mesh);
        mesh.RecalculateBounds();
        return mesh;
    }

    private static void AssertSurfaceObservation(GrassPlacementArea area, Action mutate)
    {
        Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData original), Is.True);
        uint sourceRevision = area.SourceRevision, surfaceRevision = area.SurfaceRevision;
        uint densityRevision = area.DensityRevision, groundRevision = area.GroundColorRevision;
        var regions = new List<Bounds>();
        var changes = new List<GrassPlacementChange>();
        Action<GrassPlacementArea, Bounds, GrassPlacementChange> handler = (source, region, change) =>
        {
            if (source == area)
            {
                regions.Add(region);
                changes.Add(change);
            }
        };
        GrassPlacementArea.SourceChanged += handler;
        try
        {
            mutate();
            Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData changed), Is.True);
            Assert.That(changed.WorldBounds, Is.EqualTo(original.WorldBounds));
            Assert.That(regions.Count, Is.EqualTo(1));
            Assert.That(regions[0], Is.EqualTo(original.WorldBounds));
            Assert.That(changes[0], Is.EqualTo(GrassPlacementChange.Surface));
            Assert.That(area.SourceRevision, Is.EqualTo(sourceRevision + 1));
            Assert.That(area.SurfaceRevision, Is.EqualTo(surfaceRevision + 1));
            Assert.That(area.DensityRevision, Is.EqualTo(densityRevision));
            Assert.That(area.GroundColorRevision, Is.EqualTo(groundRevision));
            Assert.That(area.TryGetCaptureData(out _), Is.True);
            Assert.That(regions.Count, Is.EqualTo(1), "An unchanged repoll must not emit a second invalidation.");
            Assert.That(area.SourceRevision, Is.EqualTo(sourceRevision + 1));
            Assert.That(area.SurfaceRevision, Is.EqualTo(surfaceRevision + 1));
        }
        finally
        {
            GrassPlacementArea.SourceChanged -= handler;
        }
    }

    private static void ConfigureLocal(GrassPlacementArea area, Vector2 size, Terrain terrain = null)
    {
        var serialized = new SerializedObject(area);
        serialized.FindProperty("shape").enumValueIndex = (int)GrassPlacementShape.Box;
        serialized.FindProperty("size").vector2Value = size;
        serialized.FindProperty("terrain").objectReferenceValue = terrain;
        serialized.FindProperty("useTerrainBounds").boolValue = false;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    private Editor NewPlacementEditor(GrassPlacementArea area)
    {
        Editor editor = Editor.CreateEditor(area);
        assets.Add(editor);
        Assert.That(editor.GetType().Name, Is.EqualTo("GrassPlacementAreaEditor"));
        return editor;
    }

    private GrassPlacementArea NewStrokeArea(out GrassDensityAsset density, out Editor editor)
    {
        GrassPlacementArea area = NewArea();
        ConfigureLocal(area, new Vector2(15f, 20f));
        area.transform.position = new Vector3(100f, 4f, -80f);
        area.transform.rotation = Quaternion.Euler(0f, 37f, 0f);
        area.transform.localScale = new Vector3(2f, 1f, 0.5f);
        density = NewDensity();
        area.SetDensityAsset(density, false);
        editor = NewPlacementEditor(area);
        SetEditorField(editor, "strokeAsset", density);
        SetEditorField(editor, "brushRadius", 0.5f);
        SetEditorField(editor, "brushStrength", 1f);
        SetEditorField(editor, "brushHardness", 1f);
        return area;
    }

    private static byte[] ReadDensitySamples(GrassDensityAsset density)
    {
        var samples = new byte[density.Width * density.Height];
        for (int y = 0; y < density.Height; y++)
            for (int x = 0; x < density.Width; x++)
                samples[y * density.Width + x] = (byte)Mathf.RoundToInt(density.Sample(
                    new Vector2((x + 0.5f) / density.Width, (y + 0.5f) / density.Height)) * 255f);
        return samples;
    }

    private static object InvokeEditor(Editor editor, string method, params object[] arguments) =>
        editor.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(editor, arguments);

    private static void SetEditorField(Editor editor, string field, object value) =>
        editor.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(editor, value);

    private static object GetEditorField(Editor editor, string field) =>
        editor.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(editor);

    private static bool BrushPoint(Editor editor, GrassPlacementArea area, Ray ray, out Vector3 point)
    {
        object[] arguments = { area, ray, Vector3.zero, Vector3.zero };
        bool hit = (bool)InvokeEditor(editor, "TryGetBrushPoint", arguments);
        point = (Vector3)arguments[2];
        return hit;
    }

    private static void AssertUv(Matrix4x4 worldToMask, Vector3 world, float expectedU, float expectedV)
    {
        Vector3 uv = worldToMask.MultiplyPoint3x4(world);
        Assert.That(uv.x, Is.EqualTo(expectedU).Within(0.0001f));
        Assert.That(uv.z, Is.EqualTo(expectedV).Within(0.0001f));
    }
}
