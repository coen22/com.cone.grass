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
        if (layer && !AssetDatabase.Contains(layer))
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
    public void AlphaOnlyDensityFailsReadOnlyBuildValidationAndClearsCoverageOnRefresh()
    {
        Texture2D original = AddTexture("Red density", 8);
        SelectTexture(original.name);
        Assert.That(GrassMicroVerseBridgeUtility.Refresh(bridge, false, false), Is.True);
        var alpha = new Texture2D(8, 8, TextureFormat.Alpha8, false, true) { name = "Alpha-only density" };
        alpha.LoadRawTextureData(new byte[64]);
        alpha.Apply(false, false);
        AssetDatabase.AddObjectToAsset(alpha, maskTarget);
        AssetDatabase.SaveAssets();
        SelectTexture(alpha.name);
        uint revision = bridge.PlacementArea.SourceRevision;

        Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(bridge, out string message), Is.False);
        StringAssert.Contains("red channel", message);
        Assert.That(bridge.PlacementArea.DensityTexture, Is.SameAs(original));
        Assert.That(bridge.PlacementArea.SourceRevision, Is.EqualTo(revision), "Preflight must not repair a rejected mask binding.");
        Assert.That(GrassMicroVerseBridgeUtility.Refresh(bridge, false, false), Is.False);
        Assert.That(bridge.PlacementArea.DensityTexture, Is.Null);
        StringAssert.Contains("red channel", bridge.LastRefreshMessage);
        Assert.That(AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(maskTarget)), Does.Contain(alpha));
    }

    [TestCase(TextureFormat.R8)]
    [TestCase(TextureFormat.RGBA32)]
    [TestCase(TextureFormat.RHalf)]
    [TestCase(TextureFormat.RFloat)]
    [TestCase(TextureFormat.DXT5)]
    public void LinearColorFormatsRetainTheirRedDensityChannel(TextureFormat format)
    {
        if (!SystemInfo.SupportsTextureFormat(format))
            Assert.Ignore("This Editor cannot construct the requested source texture format: " + format);
        var texture = new Texture2D(8, 8, format, false, true) { name = "Linear red density" };
        AssetDatabase.AddObjectToAsset(texture, maskTarget);
        AssetDatabase.SaveAssets();
        SelectTexture(texture.name);

        Assert.That(GrassMicroVerseBridgeUtility.TryResolve(bridge, out Texture2D resolved, out string message), Is.True, message);
        Assert.That(resolved, Is.SameAs(texture));
        Assert.That(GrassMicroVerseBridgeUtility.Refresh(bridge, false, false), Is.True);
        Assert.That(bridge.PlacementArea.DensityTexture, Is.SameAs(texture));
        Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(bridge, out message), Is.True, message);
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
    public void ManualRefreshCacheIsReleasedWhenBridgeIsDisabled()
    {
        AddTexture("Tile 1 Grass", 8);
        SelectTexture("Tile 1 Grass");
        Assert.That(bridge.AutoRefresh, Is.False);
        Assert.That(GrassMicroVerseBridgeUtility.Refresh(bridge, false, false, false), Is.True);
        Texture2D[] cached = GrassMicroVerseBridgeUtility.GetTextureSubAssets(bridge);
        bridge.enabled = false;

        GrassMicroVerseBridgeUtility.PruneInactiveCaches();

        bridge.enabled = true;
        Assert.That(GrassMicroVerseBridgeUtility.GetTextureSubAssets(bridge), Is.Not.SameAs(cached),
            "A manually refreshed bridge must not retain its subasset cache after deactivation.");
        uint revision = bridge.PlacementArea.DensityRevision;
        Assert.That(GrassMicroVerseBridgeUtility.Refresh(bridge, false, false, false), Is.True);
        Assert.That(bridge.PlacementArea.DensityRevision, Is.Not.EqualTo(revision),
            "Reactivation must establish a new observation instead of reusing a disabled bridge's cache.");
    }

    [Test]
    public void CacheCleanupPreservesLiveManualObservationsWithoutRecapture()
    {
        AddTexture("Tile 1 Grass", 8);
        SelectTexture("Tile 1 Grass");
        Assert.That(GrassMicroVerseBridgeUtility.Refresh(bridge, false, false, false), Is.True);
        Texture2D[] cached = GrassMicroVerseBridgeUtility.GetTextureSubAssets(bridge);
        uint revision = bridge.PlacementArea.SourceRevision;

        GrassMicroVerseBridgeUtility.PruneInactiveCaches();

        Assert.That(GrassMicroVerseBridgeUtility.GetTextureSubAssets(bridge), Is.SameAs(cached));
        Assert.That(GrassMicroVerseBridgeUtility.Refresh(bridge, false, false, false), Is.True);
        Assert.That(bridge.PlacementArea.SourceRevision, Is.EqualTo(revision),
            "Housekeeping must not turn a live manual binding into new capture work.");
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

    [TestCase(false)]
    [TestCase(true)]
    public void BuildValidationChecksTheGroundTextureThatActuallySuppliesColor(bool useOverride)
    {
        Texture2D density = AddTexture("Tile 1 Grass", 8);
        SelectTexture(density.name);
        var diffuse = new Texture2D(8, 8, TextureFormat.RGBA32, false, true) { name = "Ground layer diffuse" };
        AssetDatabase.CreateAsset(diffuse, folderPath + "/LayerDiffuse.asset");
        layer = new TerrainLayer { diffuseTexture = diffuse };
        AssetDatabase.CreateAsset(layer, folderPath + "/Ground.terrainlayer");
        terrainData.terrainLayers = new[] { layer };
        var serialized = new SerializedObject(bridge);
        serialized.FindProperty("groundLayer").objectReferenceValue = layer;
        Texture2D colorOverride = null;
        if (useOverride)
        {
            colorOverride = new Texture2D(8, 8, TextureFormat.RGBA32, false, true);
            AssetDatabase.CreateAsset(colorOverride, folderPath + "/GroundOverride.asset");
            serialized.FindProperty("groundColorOverride").objectReferenceValue = colorOverride;
        }
        serialized.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();
        Assert.That(GrassMicroVerseBridgeUtility.Refresh(bridge, false, false), Is.True);
        Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(bridge, out string message), Is.True, message);

        diffuse.SetPixel(2, 3, Color.green);
        diffuse.Apply(false, false);
        EditorUtility.SetDirty(diffuse);
        Assert.That(EditorUtility.IsDirty(layer), Is.False, "Editing texture pixels does not save or dirty its TerrainLayer asset.");
        uint revision = bridge.PlacementArea.SourceRevision;
        Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(bridge, out message), Is.EqualTo(useOverride), message);
        if (!useOverride)
            StringAssert.Contains(diffuse.name, message);
        Assert.That(bridge.PlacementArea.DensityTexture, Is.SameAs(density));
        Assert.That(bridge.PlacementArea.GroundLayer, Is.SameAs(layer));
        Assert.That(bridge.PlacementArea.GroundColorTexture, Is.SameAs(colorOverride));
        Assert.That(bridge.PlacementArea.SourceRevision, Is.EqualTo(revision), "Build validation must remain read-only.");

        AssetDatabase.SaveAssetIfDirty(diffuse);
        Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(bridge, out message), Is.True, message);
    }

    [TestCase(1, false)]
    [TestCase(1, true)]
    [TestCase(5, false)]
    [TestCase(5, true)]
    public void BuildValidationChecksOnlyTheConsumedNativeGroupSampler(int selectedIndex, bool useOverride)
    {
        Texture2D density = AddTexture("Tile grass density", 8);
        SelectTexture(density.name);
        var layers = new TerrainLayer[selectedIndex + 1];
        for (int index = 0; index < layers.Length; index++)
        {
            var diffuse = new Texture2D(8, 8, TextureFormat.RGBA32, false, true) { name = "Diffuse " + index };
            AssetDatabase.CreateAsset(diffuse, folderPath + "/Diffuse" + index + ".asset");
            layers[index] = new TerrainLayer { name = "Layer " + index, diffuseTexture = diffuse };
            AssetDatabase.CreateAsset(layers[index], folderPath + "/Layer" + index + ".terrainlayer");
        }
        terrainData.terrainLayers = layers;
        layer = layers[selectedIndex];
        TerrainLayer samplerLayer = layers[selectedIndex / 4 * 4];
        Texture2D samplerTexture = samplerLayer.diffuseTexture;
        var replacement = new Texture2D(8, 8, TextureFormat.RGBA32, false, true) { name = "Replacement sampler" };
        AssetDatabase.CreateAsset(replacement, folderPath + "/ReplacementSampler.asset");
        var serialized = new SerializedObject(bridge);
        serialized.FindProperty("groundLayer").objectReferenceValue = layer;
        Texture2D colorOverride = null;
        if (useOverride)
        {
            colorOverride = new Texture2D(8, 8, TextureFormat.RGBA32, false, true);
            AssetDatabase.CreateAsset(colorOverride, folderPath + "/ColorOverride.asset");
            serialized.FindProperty("groundColorOverride").objectReferenceValue = colorOverride;
        }
        serialized.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();
        Assert.That(GrassMicroVerseBridgeUtility.Refresh(bridge, false, false), Is.True);
        Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(bridge, out string message), Is.True, message);

        for (int edit = 0; edit < 2; edit++)
        {
            Object unsaved;
            if (edit == 0)
            {
                samplerTexture.wrapModeV = TextureWrapMode.Clamp;
                unsaved = samplerTexture;
            }
            else
            {
                samplerLayer.diffuseTexture = replacement;
                unsaved = samplerLayer;
            }
            EditorUtility.SetDirty(unsaved);
            Assert.That(EditorUtility.IsDirty(layer), Is.False, "The selected layer is separate from its group's sampler dependency.");
            Assert.That(EditorUtility.IsDirty(layer.diffuseTexture), Is.False);
            uint revision = bridge.PlacementArea.SourceRevision;

            Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(bridge, out message), Is.EqualTo(useOverride), message);
            if (!useOverride)
                StringAssert.Contains(unsaved.name, message);
            Assert.That(bridge.PlacementArea.DensityTexture, Is.SameAs(density));
            Assert.That(bridge.PlacementArea.GroundLayer, Is.SameAs(layer));
            Assert.That(bridge.PlacementArea.GroundColorTexture, Is.SameAs(colorOverride));
            Assert.That(bridge.PlacementArea.SourceRevision, Is.EqualTo(revision), "Validation must not mutate placement observations.");
            Assert.That(EditorUtility.IsDirty(unsaved), Is.True, "Preflight must leave unsaved source assets untouched.");

            AssetDatabase.SaveAssetIfDirty(unsaved);
            Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(bridge, out message), Is.True, message);
        }
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
