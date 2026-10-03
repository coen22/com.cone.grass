using System;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

public sealed class GrassMicroVerseBridgeTests
{
    private string folderPath;
    private GrassMicroVerseTestMaskTarget maskTarget;
    private GameObject terrainObject;
    private TerrainData terrainData;
    private GrassMicroVerseBridge bridge;
    private TerrainLayer layer;
    private Scene savedScene;

    [SetUp]
    public void SetUp()
    {
        GrassMicroVerseBridgeUtility.ClearCaches();
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
        AssetDatabase.CreateAsset(terrainData, folderPath + "/Terrain.asset");
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
        if (savedScene.IsValid() && savedScene.isLoaded)
            EditorSceneManager.CloseScene(savedScene, true);
        if (terrainObject)
            Object.DestroyImmediate(terrainObject);
        if (terrainData && !AssetDatabase.Contains(terrainData))
            Object.DestroyImmediate(terrainData);
        if (layer)
            Object.DestroyImmediate(layer);
        if (!string.IsNullOrEmpty(folderPath))
            AssetDatabase.DeleteAsset(folderPath);
        GrassMicroVerseBridgeUtility.ClearCaches();
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

    [Test]
    public void UnchangedPollingDoesNotInvalidateDensityColorOrHeight()
    {
        AddTexture("Tile 1 Grass", 8);
        SelectTexture("Tile 1 Grass");
        Assert.That(GrassMicroVerseBridgeUtility.Refresh(bridge, false, false), Is.True);
        bridge.PlacementArea.TryGetCaptureData(out _);
        uint revision = bridge.PlacementArea.SourceRevision;

        for (int i = 0; i < 4; i++)
            Assert.That(GrassMicroVerseBridgeUtility.Refresh(bridge, false, false, false), Is.True);

        Assert.That(bridge.PlacementArea.SourceRevision, Is.EqualTo(revision),
            "Steady polling must not schedule density, color, or height capture work.");
    }

    [Test]
    public void InPlaceDensityUpdateInvalidatesCoverageWithoutRecapturingHeight()
    {
        Texture2D texture = AddTexture("Tile 1 Grass", 8);
        SelectTexture(texture.name);
        GrassMicroVerseBridgeUtility.Refresh(bridge, false, false);
        bridge.PlacementArea.TryGetCaptureData(out _);
        uint surfaceRevision = bridge.PlacementArea.SurfaceRevision;
        uint densityRevision = bridge.PlacementArea.DensityRevision;

        var pixels = new byte[64];
        pixels[27] = 255;
        texture.LoadRawTextureData(pixels);
        texture.Apply(false, false);
        Assert.That(GrassMicroVerseBridgeUtility.Refresh(bridge, false, false, false), Is.True);

        Assert.That(bridge.PlacementArea.DensityTexture, Is.SameAs(texture));
        Assert.That(bridge.PlacementArea.DensityRevision, Is.Not.EqualTo(densityRevision));
        Assert.That(bridge.PlacementArea.SurfaceRevision, Is.EqualTo(surfaceRevision));
    }

    [Test]
    public void ForcedRefreshHandlesUnreportedWritesWithoutRecapturingHeight()
    {
        AddTexture("Tile 1 Grass", 8);
        SelectTexture("Tile 1 Grass");
        GrassMicroVerseBridgeUtility.Refresh(bridge, false, false);
        bridge.PlacementArea.TryGetCaptureData(out _);
        uint surfaceRevision = bridge.PlacementArea.SurfaceRevision;
        uint densityRevision = bridge.PlacementArea.DensityRevision;

        GrassMicroVerseBridgeUtility.Refresh(bridge, false, false, true);

        Assert.That(bridge.PlacementArea.DensityRevision, Is.Not.EqualTo(densityRevision));
        Assert.That(bridge.PlacementArea.SurfaceRevision, Is.EqualTo(surfaceRevision));
    }

    [Test]
    public void InPlaceGroundMapUpdateDoesNotInvalidateDensityOrHeight()
    {
        AddTexture("Tile 1 Grass", 8);
        SelectTexture("Tile 1 Grass");
        var color = new Texture2D(8, 8, TextureFormat.RGBA32, false, true);
        AssetDatabase.CreateAsset(color, folderPath + "/Ground.asset");
        var serialized = new SerializedObject(bridge);
        serialized.FindProperty("groundColorOverride").objectReferenceValue = color;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();
        GrassMicroVerseBridgeUtility.Refresh(bridge, false, false);
        bridge.PlacementArea.TryGetCaptureData(out _);
        uint surfaceRevision = bridge.PlacementArea.SurfaceRevision;
        uint densityRevision = bridge.PlacementArea.DensityRevision;
        uint groundRevision = bridge.PlacementArea.GroundColorRevision;

        color.SetPixel(2, 3, Color.green);
        color.Apply(false, false);
        GrassMicroVerseBridgeUtility.Refresh(bridge, false, false, false);

        Assert.That(bridge.PlacementArea.DensityRevision, Is.EqualTo(densityRevision));
        Assert.That(bridge.PlacementArea.GroundColorRevision, Is.Not.EqualTo(groundRevision));
        Assert.That(bridge.PlacementArea.SurfaceRevision, Is.EqualTo(surfaceRevision));
    }

    [Test]
    public void UnrelatedAssetInvalidationKeepsCachedMaskDiscovery()
    {
        AddTexture("Tile 1 Grass", 8);
        Texture2D[] original = GrassMicroVerseBridgeUtility.GetTextureSubAssets(bridge);
        GrassMicroVerseBridgeUtility.InvalidateAssetPath(folderPath + "/Unrelated.asset");

        Assert.That(GrassMicroVerseBridgeUtility.GetTextureSubAssets(bridge), Is.SameAs(original));
    }

    [Test]
    public void BuildValidationRejectsStaleBindingWithoutRepairingIt()
    {
        Texture2D original = AddTexture("Tile 1 Grass", 8);
        SelectTexture(original.name);
        GrassMicroVerseBridgeUtility.Refresh(bridge, false, false);
        original.name = "Retired Grass";
        EditorUtility.SetDirty(original);
        Texture2D replacement = AddTexture("Tile 1 Grass", 16);

        Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(bridge, out string message), Is.False);
        Assert.That(message, Does.Contain("Saved placement inputs differ"));
        Assert.That(bridge.PlacementArea.DensityTexture, Is.SameAs(original));

        GrassMicroVerseBridgeUtility.Refresh(bridge, false, false);
        Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(bridge, out message), Is.True, message);
        Assert.That(bridge.PlacementArea.DensityTexture, Is.SameAs(replacement));
    }

    [Test]
    public void PlayerPreflightReadsSavedSceneEvenWhenOpenSceneHasBeenRepaired()
    {
        Texture2D original = AddTexture("Tile 1 Grass", 8);
        SelectTexture(original.name);
        GrassMicroVerseBridgeUtility.Refresh(bridge, false, false);
        savedScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        SceneManager.MoveGameObjectToScene(terrainObject, savedScene);
        string scenePath = folderPath + "/Grass.unity";
        Assert.That(EditorSceneManager.SaveScene(savedScene, scenePath), Is.True);

        original.name = "Retired Grass";
        EditorUtility.SetDirty(original);
        Texture2D replacement = AddTexture("Tile 1 Grass", 16);
        GrassMicroVerseBridgeUtility.Refresh(bridge, false, false);
        Hash128 savedHash = AssetDatabase.GetAssetDependencyHash(scenePath);
        int activeCount = GrassMicroVerseBridge.ActiveBridges.Count;

        Assert.Throws<BuildFailedException>(() =>
            GrassMicroVerseBridgeBuildProcessor.ValidateSavedScenes(new[] { scenePath }));
        Assert.That(bridge.PlacementArea.DensityTexture, Is.SameAs(replacement));
        Assert.That(GrassMicroVerseBridge.ActiveBridges.Count, Is.EqualTo(activeCount),
            "Preview scene bridges must not enter the live polling registry.");
        Assert.That(AssetDatabase.GetAssetDependencyHash(scenePath), Is.EqualTo(savedHash),
            "Build preflight must not save or rewrite source scenes.");

        Assert.That(EditorSceneManager.SaveScene(savedScene, scenePath), Is.True);
        Assert.DoesNotThrow(() => GrassMicroVerseBridgeBuildProcessor.ValidateSavedScenes(new[] { scenePath }));
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
