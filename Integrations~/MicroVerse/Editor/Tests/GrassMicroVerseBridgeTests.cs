using System;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;

public sealed class GrassMicroVerseBridgeTests
{
    private string folderPath;
    private GrassMicroVerseTestMaskTarget maskTarget;
    private GameObject terrainObject;
    private TerrainData terrainData;
    private GrassMicroVerseBridge bridge;
    private TerrainLayer layer;

    [SetUp]
    public void SetUp()
    {
        string folderName = "GrassMaskBridgeTests_" + Guid.NewGuid().ToString("N");
        AssetDatabase.CreateFolder("Assets", folderName);
        folderPath = "Assets/" + folderName;
        maskTarget = ScriptableObject.CreateInstance<GrassMicroVerseTestMaskTarget>();
        AssetDatabase.CreateAsset(maskTarget, folderPath + "/Density.asset");

        terrainData = new TerrainData
        {
            heightmapResolution = 33,
            size = new Vector3(100f, 20f, 80f)
        };
        terrainObject = Terrain.CreateTerrainGameObject(terrainData);
        terrainObject.name = "Tile 1";
        bridge = terrainObject.AddComponent<GrassMicroVerseBridge>();

        var serialized = new SerializedObject(bridge);
        serialized.FindProperty("terrain").objectReferenceValue = terrainObject.GetComponent<Terrain>();
        serialized.FindProperty("maskTarget").objectReferenceValue = maskTarget;
        serialized.FindProperty("autoRefresh").boolValue = false;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    [TearDown]
    public void TearDown()
    {
        if (terrainObject)
            Object.DestroyImmediate(terrainObject);
        if (terrainData)
            Object.DestroyImmediate(terrainData);
        if (layer)
            Object.DestroyImmediate(layer);
        if (!string.IsNullOrEmpty(folderPath))
            AssetDatabase.DeleteAsset(folderPath);
    }

    [Test]
    public void ExplicitFullNameSelectsCorrectTileAmongSimilarNames()
    {
        AddTexture("Tile 10 Grass", 8);
        Texture2D expected = AddTexture("Tile 1 Grass", 8);
        SelectTexture("Tile 1 Grass");

        Assert.That(GrassMicroVerseBridgeUtility.Refresh(bridge, false, false), Is.True);
        Assert.That(bridge.PlacementArea.DensityTexture, Is.SameAs(expected));
        Assert.That(bridge.PlacementArea.Terrain, Is.SameAs(terrainObject.GetComponent<Terrain>()));
        Assert.That(bridge.PlacementArea.UsesTerrainBounds, Is.True);
        Assert.That(bridge.PlacementArea.EdgeFalloff, Is.Zero);
    }

    [Test]
    public void AnUnselectedSingleTextureDoesNotImplicitlyEnableCoverage()
    {
        AddTexture("Tile 1 Grass", 8);

        Assert.That(GrassMicroVerseBridgeUtility.Refresh(bridge, false, false), Is.False);
        Assert.That(bridge.PlacementArea.Shape, Is.EqualTo(GrassPlacementShape.Texture));
        Assert.That(bridge.PlacementArea.DensityTexture, Is.Null);
    }

    [Test]
    public void RegeneratedSubassetReplacesDestroyedReferenceByUniqueFullName()
    {
        Texture2D original = AddTexture("Tile 1 Grass", 8);
        SelectTexture(original.name);
        Assert.That(GrassMicroVerseBridgeUtility.Refresh(bridge, false, false), Is.True);

        Object.DestroyImmediate(original, true);
        Texture2D replacement = AddTexture("Tile 1 Grass", 16);

        Assert.That(GrassMicroVerseBridgeUtility.Refresh(bridge, false, false), Is.True);
        Assert.That(bridge.PlacementArea.DensityTexture, Is.SameAs(replacement));
        Assert.That(bridge.PlacementArea.DensityTexture.width, Is.EqualTo(16));
    }

    [Test]
    public void DuplicateSubassetNamesClearPreviouslyValidCoverage()
    {
        AddTexture("Tile 1 Grass", 8);
        SelectTexture("Tile 1 Grass");
        Assert.That(GrassMicroVerseBridgeUtility.Refresh(bridge, false, false), Is.True);
        AddTexture("Tile 1 Grass", 16);

        Assert.That(GrassMicroVerseBridgeUtility.Refresh(bridge, false, false), Is.False);
        Assert.That(bridge.PlacementArea.DensityTexture, Is.Null);
        Assert.That(bridge.LastRefreshMessage, Does.Contain("Several textures"));
    }

    [Test]
    public void SrgbDensityIsRejectedAndClearsOldCoverage()
    {
        AddTexture("Linear density", 8);
        SelectTexture("Linear density");
        Assert.That(GrassMicroVerseBridgeUtility.Refresh(bridge, false, false), Is.True);

        var colorTexture = new Texture2D(8, 8, TextureFormat.RGBA32, false, false)
        {
            name = "Color density"
        };
        AssetDatabase.AddObjectToAsset(colorTexture, maskTarget);
        AssetDatabase.SaveAssets();
        Assert.That(GraphicsFormatUtility.IsSRGBFormat(colorTexture.graphicsFormat), Is.True);
        SelectTexture(colorTexture.name);

        Assert.That(GrassMicroVerseBridgeUtility.Refresh(bridge, false, false), Is.False);
        Assert.That(bridge.PlacementArea.DensityTexture, Is.Null);
    }

    [Test]
    public void MatchingGroundLayerMustBelongToSupportingTerrain()
    {
        AddTexture("Tile 1 Grass", 8);
        SelectTexture("Tile 1 Grass");
        layer = new TerrainLayer();
        var serialized = new SerializedObject(bridge);
        serialized.FindProperty("groundLayer").objectReferenceValue = layer;
        serialized.ApplyModifiedPropertiesWithoutUndo();

        Assert.That(GrassMicroVerseBridgeUtility.Refresh(bridge, false, false), Is.False);
        Assert.That(bridge.PlacementArea.DensityTexture, Is.Null);

        terrainData.terrainLayers = new[] { layer };
        Assert.That(GrassMicroVerseBridgeUtility.Refresh(bridge, false, false), Is.True);
        Assert.That(bridge.PlacementArea.GroundLayer, Is.SameAs(layer));
        Assert.That(bridge.PlacementArea.GroundColorStrength, Is.EqualTo(1f));
    }

    private Texture2D AddTexture(string textureName, int resolution)
    {
        var texture = new Texture2D(resolution, resolution, TextureFormat.R8, false, true)
        {
            name = textureName
        };
        AssetDatabase.AddObjectToAsset(texture, maskTarget);
        AssetDatabase.SaveAssets();
        return texture;
    }

    private void SelectTexture(string textureName)
    {
        var serialized = new SerializedObject(bridge);
        serialized.FindProperty("textureSubAssetName").stringValue = textureName;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }
}
