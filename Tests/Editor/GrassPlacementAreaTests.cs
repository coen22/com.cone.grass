using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
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

    [Test]
    public void TerrainMappingUsesTerrainOriginAndRetainsItsLayerTexturePhase()
    {
        Terrain terrain = NewTerrain(new Vector3(-50f, 7f, 120f), new Vector3(200f, 35f, 100f));
        var layer = new TerrainLayer { tileSize = new Vector2(10f, 20f), tileOffset = new Vector2(2f, -4f) };
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
        Assert.That(-50f * mapping.x + mapping.z, Is.EqualTo(0.2f).Within(0.0001f));
        Assert.That(120f * mapping.y + mapping.w, Is.EqualTo(-0.2f).Within(0.0001f));
        Assert.That(-40f * mapping.x + mapping.z, Is.EqualTo(1.2f).Within(0.0001f));
        Assert.That(140f * mapping.y + mapping.w, Is.EqualTo(0.8f).Within(0.0001f));
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
