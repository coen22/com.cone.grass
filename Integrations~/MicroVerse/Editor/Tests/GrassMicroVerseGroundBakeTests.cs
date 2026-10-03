using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

/// <summary>Exercises saved native TerrainLit bakes through the optional bridge's public editor API.</summary>
[Category("GrassGPU"), NonParallelizable]
public sealed class GrassMicroVerseGroundBakeTests
{
    private readonly List<Object> owned = new List<Object>();
    private string folder, diffusePath, outputPath;
    private Terrain terrain;
    private TerrainData data;
    private TerrainLayer layer;
    private Texture2D diffuse, density;
    private GrassMicroVerseTestMaskTarget target;
    private GrassMicroVerseBridge bridge;
    private int bakeCount;

    [SetUp]
    public void SetUp()
    {
        if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset) ||
            SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            Assert.Ignore("Ground-bake bridge tests require a URP Editor project with a graphics device.");
        Shader shader = Shader.Find(TerrainGrassAlbedoBaker.NativeTerrainShaderName);
        Assert.That(shader, Is.Not.Null, "The native URP Terrain/Lit shader must be installed.");
        GrassMicroVerseBridgeUtility.ClearCaches();
        string name = "GrassGroundBridgeTests_" + Guid.NewGuid().ToString("N");
        folder = "Assets/" + name;
        AssetDatabase.CreateFolder("Assets", name);
        diffusePath = folder + "/Diffuse.asset";
        outputPath = folder + "/Ground.asset";

        diffuse = Own(new Texture2D(2, 2, TextureFormat.RGBA32, false, true));
        diffuse.name = "Terrain diffuse";
        diffuse.wrapMode = TextureWrapMode.Repeat;
        SetDiffuse(Color.red);
        AssetDatabase.CreateAsset(diffuse, diffusePath);
        layer = Own(new TerrainLayer
        {
            diffuseTexture = diffuse,
            tileSize = new Vector2(64f, 64f),
            diffuseRemapMin = Vector4.zero,
            diffuseRemapMax = Vector4.one,
            maskMapRemapMin = Vector4.zero,
            maskMapRemapMax = Vector4.one
        });
        AssetDatabase.CreateAsset(layer, folder + "/Grass.terrainlayer");
        data = Own(new TerrainData
        {
            heightmapResolution = 33,
            alphamapResolution = 16,
            size = new Vector3(64f, 8f, 64f)
        });
        AssetDatabase.CreateAsset(data, folder + "/Terrain.asset");
        data.terrainLayers = new[] { layer };
        float[,,] controls = new float[16, 16, 1];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                controls[y, x, 0] = 1f;
        data.SetAlphamaps(0, 0, controls);

        GameObject terrainObject = Own(Terrain.CreateTerrainGameObject(data));
        terrain = terrainObject.GetComponent<Terrain>();
        Material material = Own(new Material(shader));
        material.DisableKeyword("_TERRAIN_BLEND_HEIGHT");
        material.SetFloat("_EnableHeightBlend", 0f);
        AssetDatabase.CreateAsset(material, folder + "/Terrain.mat");
        terrain.materialTemplate = material;

        target = Own(ScriptableObject.CreateInstance<GrassMicroVerseTestMaskTarget>());
        AssetDatabase.CreateAsset(target, folder + "/Mask.asset");
        density = Own(new Texture2D(8, 8, TextureFormat.R8, false, true) { name = "Tile grass density" });
        byte[] pixels = new byte[64];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = 255;
        density.LoadRawTextureData(pixels);
        density.Apply(false, false);
        AssetDatabase.AddObjectToAsset(density, target);
        foreach (Object source in new Object[] { diffuse, layer, material, density, target })
        {
            EditorUtility.SetDirty(source);
            AssetDatabase.SaveAssetIfDirty(source);
        }
        for (int i = 0; i < data.alphamapTextureCount; i++)
            EditorUtility.SetDirty(data.GetAlphamapTexture(i));
        EditorUtility.SetDirty(data);
        AssetDatabase.SaveAssetIfDirty(data);

        bridge = AddBridge(terrainObject, 64, true);
        bakeCount = 0;
        TerrainGrassAlbedoBaker.Baked += OnBaked;
    }

    [TearDown]
    public void TearDown()
    {
        TerrainGrassAlbedoBaker.Baked -= OnBaked;
        for (int i = owned.Count - 1; i >= 0; i--)
            if (owned[i] && !AssetDatabase.Contains(owned[i]))
                Object.DestroyImmediate(owned[i]);
        owned.Clear();
        if (!string.IsNullOrEmpty(folder))
            AssetDatabase.DeleteAsset(folder);
        folder = null;
        terrain = null;
        GrassMicroVerseBridgeUtility.ClearCaches();
    }

    [Test]
    public void UnchangedSavedPollsReuseOutputAndRemainBuildValid()
    {
        Texture2D output = Refresh();
        Assert.That(bakeCount, Is.EqualTo(1));
        Assert.That(bridge.GroundBakeSourceKey, Is.Not.Null.And.Not.Empty);
        Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(bridge, out string message), Is.True, message);
        string guid = AssetDatabase.AssetPathToGUID(outputPath);
        uint revision = bridge.PlacementArea.SourceRevision;

        for (int i = 0; i < 4; i++)
            Assert.That(Refresh(), Is.SameAs(output));

        Assert.That(bakeCount, Is.EqualTo(1), "Unchanged polls must not render or read back another terrain bake.");
        Assert.That(bridge.PlacementArea.SourceRevision, Is.EqualTo(revision));
        Assert.That(AssetDatabase.AssetPathToGUID(outputPath), Is.EqualTo(guid));
        AssertColor(output.GetPixel(32, 32), Color.red);
    }

    [Test]
    public void DirtyPreviewCannotCertifyOldDiskDataAfterCacheResetAndSourceRestore()
    {
        Texture2D output = Refresh();
        string savedSourceKey = bridge.GroundBakeSourceKey;
        Hash128 savedDiffuseHash = AssetDatabase.GetAssetDependencyHash(diffusePath);
        SetDiffuse(Color.blue);
        Assert.That(EditorUtility.IsDirty(diffuse), Is.True);
        GrassMicroVerseBridgeUtility.ClearCaches();

        Assert.That(Refresh(), Is.SameAs(output));
        Assert.That(bakeCount, Is.EqualTo(2), "An unknown session cache must still bake dirty GPU source content.");
        AssertColor(output.GetPixel(32, 32), Color.blue);
        Assert.That(bridge.GroundBakeSourceKey, Is.Empty,
            "An image from unsaved source pixels cannot be certified by that asset's previous disk hash.");
        Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(bridge, out _), Is.False);
        for (int i = 0; i < 3; i++)
            Refresh();
        Assert.That(bakeCount, Is.EqualTo(2), "An unchanged dirty preview must still use the session cache.");

        // Force a reload of the saved red texture, leaving the blue baked output
        // in place. Clearing bridge caches models the lost state after a reload.
        AssetDatabase.ImportAsset(diffusePath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
        diffuse = AssetDatabase.LoadAssetAtPath<Texture2D>(diffusePath);
        Assert.That(diffuse, Is.Not.Null);
        Assert.That(EditorUtility.IsDirty(diffuse), Is.False);
        AssertColor(diffuse.GetPixel(0, 0), Color.red);
        Assert.That(AssetDatabase.GetAssetDependencyHash(diffusePath), Is.EqualTo(savedDiffuseHash));
        GrassMicroVerseBridgeUtility.ClearCaches();

        Assert.That(Refresh(), Is.SameAs(output));
        Assert.That(bakeCount, Is.EqualTo(3), "The old image must be rebuilt even though the saved source key is unchanged.");
        AssertColor(output.GetPixel(32, 32), Color.red);
        Assert.That(bridge.GroundBakeSourceKey, Is.EqualTo(savedSourceKey));
        Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(bridge, out string message), Is.True, message);
    }

    [Test]
    public void SavingDirtySourcesCreatesOneDurableBakeThenPollingStops()
    {
        Refresh();
        SetDiffuse(Color.blue);
        Refresh();
        Assert.That(bridge.GroundBakeSourceKey, Is.Empty);
        Assert.That(bakeCount, Is.EqualTo(2));

        AssetDatabase.SaveAssetIfDirty(diffuse);
        Refresh();
        Assert.That(bakeCount, Is.EqualTo(3));
        Assert.That(bridge.GroundBakeSourceKey, Is.Not.Null.And.Not.Empty);
        Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(bridge, out string message), Is.True, message);
        for (int i = 0; i < 4; i++)
            Refresh();
        Assert.That(bakeCount, Is.EqualTo(3));
    }

    [Test]
    public void SharedTerrainOutputAtAnotherResolutionClearsConflictingCoverage()
    {
        Texture2D original = Refresh();
        GameObject otherObject = Own(new GameObject("Conflicting ground bake"));
        GrassMicroVerseBridge other = AddBridge(otherObject, 128, false);
        Assert.That(GrassMicroVerseBridgeUtility.Refresh(other, false, false, false), Is.True, other.LastRefreshMessage);
        Assert.That(other.PlacementArea.DensityTexture, Is.SameAs(density));
        var serialized = new SerializedObject(other);
        serialized.FindProperty("bakeTerrainGroundColor").boolValue = true;
        serialized.ApplyModifiedPropertiesWithoutUndo();

        Assert.That(GrassMicroVerseBridgeUtility.Refresh(other, false, false, false), Is.False);
        Assert.That(other.LastRefreshMessage, Does.Contain("Terrain and resolution"));
        Assert.That(other.PlacementArea.DensityTexture, Is.Null);
        Assert.That(original.width, Is.EqualTo(64));
        Assert.That(bridge.BakedGroundColor, Is.SameAs(original));
        Assert.That(bakeCount, Is.EqualTo(1), "Conflicting outputs must not alternate between resolutions on every poll.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SharedOutputAliasesCannotOverwriteAnotherResolution(bool changeCase)
    {
        Texture2D original = Refresh();
        string originalGuid = AssetDatabase.AssetPathToGUID(outputPath);
        GameObject otherObject = Own(new GameObject("Aliased ground output"));
        GrassMicroVerseBridge other = AddBridge(otherObject, 128, true);
        var serialized = new SerializedObject(other);
        serialized.FindProperty("groundBakeAssetPath").stringValue = changeCase
            ? outputPath.Replace("Ground.asset", "GROUND.asset")
            : outputPath.Replace('/', '\\');
        serialized.ApplyModifiedPropertiesWithoutUndo();

        Assert.That(GrassMicroVerseBridgeUtility.Refresh(other, false, false, false), Is.False);
        Assert.That(other.LastRefreshMessage, Does.Contain("Terrain and resolution"));
        Assert.That(other.PlacementArea.DensityTexture, Is.Null);
        Assert.That(original.width, Is.EqualTo(64));
        Assert.That(AssetDatabase.AssetPathToGUID(outputPath), Is.EqualTo(originalGuid));
        Assert.That(bakeCount, Is.EqualTo(1), "An alias must be rejected before overwriting the existing bake.");
    }

    [Test]
    public void WindowsOutputPathIsSavedCanonicallyAndReused()
    {
        var serialized = new SerializedObject(bridge);
        serialized.FindProperty("groundBakeAssetPath").stringValue = outputPath.Replace('/', '\\');
        serialized.ApplyModifiedPropertiesWithoutUndo();

        Texture2D output = Refresh();
        Assert.That(bridge.GroundBakeAssetPath, Is.EqualTo(AssetDatabase.GetAssetPath(output)));
        Assert.That(bridge.GroundBakeAssetPath, Is.EqualTo(outputPath));
        Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(bridge, out string message), Is.True, message);
        uint revision = bridge.PlacementArea.SourceRevision;
        for (int i = 0; i < 3; i++)
            Assert.That(Refresh(), Is.SameAs(output));
        Assert.That(bakeCount, Is.EqualTo(1));
        Assert.That(bridge.PlacementArea.SourceRevision, Is.EqualTo(revision));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DeactivatedOwnerKeepsItsSavedOutput(bool disableComponent)
    {
        Texture2D original = Refresh();
        if (disableComponent)
            bridge.enabled = false;
        else
            bridge.gameObject.SetActive(false);
        Assert.That(GrassMicroVerseBridge.ActiveBridges, Does.Not.Contain(bridge));
        GameObject otherObject = Own(new GameObject("Conflicting inactive bake owner"));
        GrassMicroVerseBridge other = AddBridge(otherObject, 128, true);

        Assert.That(GrassMicroVerseBridgeUtility.Refresh(other, false, false, false), Is.False);
        Assert.That(other.LastRefreshMessage, Does.Contain("Terrain and resolution"));
        Assert.That(other.PlacementArea.DensityTexture, Is.Null);
        Assert.That(original.width, Is.EqualTo(64));
        Assert.That(bridge.PlacementArea.GroundColorTexture, Is.SameAs(original));
        Assert.That(bakeCount, Is.EqualTo(1), "An inactive saved scene binding still owns its baked texture.");
    }

    private GrassMicroVerseBridge AddBridge(GameObject gameObject, int resolution, bool enableBaking)
    {
        GrassMicroVerseBridge added = gameObject.AddComponent<GrassMicroVerseBridge>();
        var serialized = new SerializedObject(added);
        serialized.FindProperty("terrain").objectReferenceValue = terrain;
        serialized.FindProperty("maskTarget").objectReferenceValue = target;
        serialized.FindProperty("textureSubAssetName").stringValue = density.name;
        serialized.FindProperty("autoRefresh").boolValue = false;
        serialized.FindProperty("bakeTerrainGroundColor").boolValue = enableBaking;
        serialized.FindProperty("groundBakeResolution").intValue = resolution;
        serialized.FindProperty("groundBakeAssetPath").stringValue = outputPath;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        return added;
    }

    private Texture2D Refresh()
    {
        Assert.That(GrassMicroVerseBridgeUtility.Refresh(bridge, false, false, false), Is.True, bridge.LastRefreshMessage);
        Assert.That(bridge.PlacementArea.TryGetCaptureData(out _), Is.True);
        Assert.That(bridge.BakedGroundColor, Is.Not.Null);
        return bridge.BakedGroundColor;
    }

    private void SetDiffuse(Color color)
    {
        diffuse.SetPixels(new[] { color, color, color, color });
        diffuse.Apply(false, false);
        EditorUtility.SetDirty(diffuse);
    }

    private void OnBaked(Terrain source, Texture2D output)
    {
        if (source == terrain)
            bakeCount++;
    }

    private T Own<T>(T value) where T : Object { owned.Add(value); return value; }

    private static void AssertColor(Color actual, Color expected)
    {
        Assert.That(actual.r, Is.EqualTo(expected.r).Within(0.02f));
        Assert.That(actual.g, Is.EqualTo(expected.g).Within(0.02f));
        Assert.That(actual.b, Is.EqualTo(expected.b).Within(0.02f));
    }
}
