using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

[NonParallelizable]
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

        Object.DestroyImmediate(surface.GetComponent<MeshRenderer>());
        Assert.That(area.TryGetCaptureData(out _), Is.False);
        Assert.That(area.IntersectsCoverage(original.WorldBounds), Is.False);
        uint revision = density.Revision;
        Assert.That(area.Paint(area.transform.position, 2f, 1f, 1f, true), Is.False);
        Assert.That(density.Revision, Is.EqualTo(revision));

        surface.gameObject.AddComponent<MeshRenderer>();
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

    private Texture2D NewTexture()
    {
        var texture = new Texture2D(16, 16, TextureFormat.R8, false, true);
        assets.Add(texture);
        return texture;
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

    private static void ConfigureLocal(GrassPlacementArea area, Vector2 size, Terrain terrain = null)
    {
        var serialized = new SerializedObject(area);
        serialized.FindProperty("shape").enumValueIndex = (int)GrassPlacementShape.Box;
        serialized.FindProperty("size").vector2Value = size;
        serialized.FindProperty("terrain").objectReferenceValue = terrain;
        serialized.FindProperty("useTerrainBounds").boolValue = false;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void AssertUv(Matrix4x4 worldToMask, Vector3 world, float expectedU, float expectedV)
    {
        Vector3 uv = worldToMask.MultiplyPoint3x4(world);
        Assert.That(uv.x, Is.EqualTo(expectedU).Within(0.0001f));
        Assert.That(uv.z, Is.EqualTo(expectedV).Within(0.0001f));
    }
}
