using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
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
    private Scene savedScene;

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
        if (savedScene.IsValid() && savedScene.isLoaded)
            EditorSceneManager.CloseScene(savedScene, true);
        savedScene = default;
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
    public void LegacyBakeKeyWithoutCaptureShaderFailsPreflightAndRebuildsAfterCacheReset()
    {
        Texture2D output = Refresh();
        string currentSourceKey = bridge.GroundBakeSourceKey;
        string legacySourceKey = LegacySourceKeyWithoutBakeShader();
        string outputKey = bridge.GroundBakeOutputKey;
        string guid = AssetDatabase.AssetPathToGUID(outputPath);
        Hash128 sourceHash = AssetDatabase.GetAssetDependencyHash(diffusePath);
        Color[] sourcePixels = diffuse.GetPixels();
        bridge.SetBakedGroundColor(output, outputPath, legacySourceKey, outputKey);
        GrassMicroVerseBridgeUtility.ClearCaches();
        uint revision = bridge.PlacementArea.SourceRevision;

        Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(bridge, out string message), Is.False);
        StringAssert.Contains("stale", message);
        Assert.That(bridge.GroundBakeSourceKey, Is.EqualTo(legacySourceKey));
        Assert.That(bridge.GroundBakeOutputKey, Is.EqualTo(outputKey));
        Assert.That(bridge.PlacementArea.DensityTexture, Is.SameAs(density));
        Assert.That(bridge.PlacementArea.GroundColorTexture, Is.SameAs(output));
        Assert.That(bridge.PlacementArea.SourceRevision, Is.EqualTo(revision), "Build preflight must not repair the old record.");
        Assert.That(bakeCount, Is.EqualTo(1));

        Assert.That(Refresh(), Is.SameAs(output));
        Assert.That(bakeCount, Is.EqualTo(2), "A new session must not reuse an output whose key omitted the capture shader.");
        Assert.That(bridge.GroundBakeSourceKey, Is.EqualTo(currentSourceKey).And.Not.EqualTo(legacySourceKey));
        Assert.That(AssetDatabase.AssetPathToGUID(outputPath), Is.EqualTo(guid));
        Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(bridge, out message), Is.True, message);
        Assert.That(AssetDatabase.GetAssetDependencyHash(diffusePath), Is.EqualTo(sourceHash));
        Assert.That(diffuse.GetPixels(), Is.EqualTo(sourcePixels));
        Assert.That(EditorUtility.IsDirty(diffuse), Is.False);
        AssertColor(output.GetPixel(32, 32), Color.red);
        Refresh();
        Assert.That(bakeCount, Is.EqualTo(2), "The corrected saved key must settle after one replacement bake.");
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

    [TestCase(false)]
    [TestCase(true)]
    public void SourceEditsDuringBakeNotificationRequireAnotherBake(bool saveSource)
    {
        bool changed = false;
        Action<Terrain, Texture2D> changeSource = (source, output) =>
        {
            if (source != terrain || changed)
                return;
            changed = true;
            SetDiffuse(Color.blue);
            if (saveSource)
                AssetDatabase.SaveAssetIfDirty(diffuse);
        };
        TerrainGrassAlbedoBaker.Baked += changeSource;
        try
        {
            Texture2D first = Refresh();
            Assert.That(changed, Is.True);
            Assert.That(bakeCount, Is.EqualTo(1));
            AssertColor(first.GetPixel(32, 32), Color.red);
            Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(bridge, out _), Is.False,
                "The notification changed the source after the saved red image was rendered.");

            Assert.That(Refresh(), Is.SameAs(first));
            Assert.That(bakeCount, Is.EqualTo(2),
                "A post-bake source edit must not be mistaken for the source of the earlier image.");
            AssertColor(first.GetPixel(32, 32), Color.blue);
            Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(bridge, out string message),
                Is.EqualTo(saveSource), message);
            for (int i = 0; i < 3; i++)
                Refresh();
            Assert.That(bakeCount, Is.EqualTo(2), "Once the changed source is baked, polling must settle.");
        }
        finally
        {
            TerrainGrassAlbedoBaker.Baked -= changeSource;
        }
    }

    [Test]
    public void ReentrantRefreshDoesNotClearOrRebindTheCommittedPlacement()
    {
        Texture2D original = Refresh();
        bool attempted = false, nestedSucceeded = true, keptBinding = false, keptRevision = false;
        string nestedMessage = null;
        Action<Terrain, Texture2D> observer = (source, output) =>
        {
            // An integration can observe a bake and request a bridge refresh.
            // Keep this regression bounded on versions without the guard.
            if (source != terrain || attempted)
                return;
            attempted = true;
            uint revision = bridge.PlacementArea.SourceRevision;
            nestedSucceeded = GrassMicroVerseBridgeUtility.Refresh(bridge, false, false, true, true);
            nestedMessage = bridge.LastRefreshMessage;
            keptBinding = bridge.PlacementArea.DensityTexture == density &&
                bridge.PlacementArea.GroundColorTexture == original;
            keptRevision = bridge.PlacementArea.SourceRevision == revision;
        };
        TerrainGrassAlbedoBaker.Baked += observer;
        try
        {
            Assert.That(GrassMicroVerseBridgeUtility.Refresh(bridge, false, false, false, true),
                Is.True, bridge.LastRefreshMessage);
        }
        finally
        {
            TerrainGrassAlbedoBaker.Baked -= observer;
        }

        Assert.That(attempted, Is.True);
        Assert.That(nestedSucceeded, Is.False);
        StringAssert.Contains("already refreshing", nestedMessage);
        Assert.That(keptBinding, Is.True);
        Assert.That(keptRevision, Is.True, "A rejected nested refresh must not invalidate or clear the original placement.");
        Assert.That(bridge.LastRefreshSucceeded, Is.True);
        Assert.That(bakeCount, Is.EqualTo(2));
        Assert.That(Refresh(), Is.SameAs(original));
        Assert.That(bakeCount, Is.EqualTo(2), "Ordinary refresh must resume after the callback returns.");
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void RefreshIterationSurvivesBakeObserversDeletingCurrentAndLaterOwners(bool saveScene, bool childBridges)
    {
        // Keep the shared Terrain alive when a callback removes a bridge owner.
        Object.DestroyImmediate(bridge);
        var candidates = new GrassMicroVerseBridge[3];
        GameObject group = childBridges ? Own(new GameObject("Bridge group")) : null;
        for (int i = 0; i < candidates.Length; i++)
        {
            GameObject owner = Own(new GameObject("Bake consumer " + i));
            if (group)
                owner.transform.SetParent(group.transform);
            candidates[i] = AddBridge(owner, 64, true);
            var serialized = new SerializedObject(candidates[i]);
            serialized.FindProperty("groundBakeAssetPath").stringValue = folder + "/Consumer" + i + ".asset";
            serialized.FindProperty("autoRefresh").boolValue = !saveScene;
            serialized.FindProperty("pollWhileEditing").boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
        if (saveScene)
        {
            savedScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.MoveGameObjectToScene(terrain.gameObject, savedScene);
            if (group)
                SceneManager.MoveGameObjectToScene(group, savedScene);
            else
                foreach (GrassMicroVerseBridge candidate in candidates)
                    SceneManager.MoveGameObjectToScene(candidate.gameObject, savedScene);
        }

        bool removed = false;
        GrassMicroVerseBridge survivor = null;
        Action<Terrain, Texture2D> removeOwners = (source, output) =>
        {
            if (source != terrain || removed)
                return;
            removed = true;
            GrassMicroVerseBridge current = null, later = null;
            string path = AssetDatabase.GetAssetPath(output);
            foreach (GrassMicroVerseBridge candidate in candidates)
            {
                if (candidate.GroundBakeAssetPath == path)
                    current = candidate;
                else if (!later)
                    later = candidate;
                else
                    survivor = candidate;
            }
            Assert.That(current, Is.Not.Null);
            Assert.That(later, Is.Not.Null);
            Assert.That(survivor, Is.Not.Null);
            Object.DestroyImmediate(current.gameObject);
            Object.DestroyImmediate(later.gameObject);
        };
        TerrainGrassAlbedoBaker.Baked += removeOwners;
        try
        {
            if (saveScene)
                Assert.That(EditorSceneManager.SaveScene(savedScene, folder + "/Consumers.unity"), Is.True);
            else
            {
                // Invoke the real update handler once, without relying on a
                // wall-clock debounce or an unrelated editor repaint sequence.
                Type watcher = typeof(GrassMicroVerseBridgeWatcher);
                FieldInfo debounce = watcher.GetField("refreshAfter", BindingFlags.Static | BindingFlags.NonPublic);
                double previousDebounce = (double)debounce.GetValue(null);
                try
                {
                    debounce.SetValue(null, -1d);
                    MethodInfo update = watcher.GetMethod("Update", BindingFlags.Static | BindingFlags.NonPublic);
                    Assert.DoesNotThrow(() => update.Invoke(null, null));
                }
                finally
                {
                    debounce.SetValue(null, previousDebounce);
                }
            }
        }
        finally
        {
            TerrainGrassAlbedoBaker.Baked -= removeOwners;
        }

        Assert.That(removed, Is.True, "The regression must cross a real synchronous Baked callback.");
        Assert.That(bakeCount, Is.EqualTo(2), "The deleted queued owner must be skipped, and the surviving owner must finish.");
        Assert.That(survivor, Is.Not.Null);
        Assert.That(survivor.LastRefreshSucceeded, Is.True, survivor.LastRefreshMessage);
        Assert.That(survivor.PlacementArea.DensityTexture, Is.SameAs(density));
        Assert.That(survivor.PlacementArea.GroundColorTexture, Is.SameAs(survivor.BakedGroundColor));
        Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(survivor, out string message), Is.True, message);
    }

    [Test]
    public void RefreshingAnotherBridgeDuringASharedBakePreservesItsCommittedPlacement()
    {
        Texture2D original = Refresh();
        GameObject otherObject = Own(new GameObject("Shared ground-bake consumer"));
        GrassMicroVerseBridge other = AddBridge(otherObject, 64, true);
        Assert.That(GrassMicroVerseBridgeUtility.Refresh(other, false, false, false), Is.True, other.LastRefreshMessage);
        Assert.That(other.AutoRefresh, Is.False);
        int previousBakes = bakeCount;
        bool attempted = false, nestedSucceeded = true, keptBinding = false, keptRevision = false;
        string nestedMessage = null;
        Action<Terrain, Texture2D> observer = (source, output) =>
        {
            if (source != terrain || attempted)
                return;
            attempted = true;
            uint revision = other.PlacementArea.SourceRevision;
            nestedSucceeded = GrassMicroVerseBridgeUtility.Refresh(other, false, false, true, true);
            nestedMessage = other.LastRefreshMessage;
            keptBinding = other.PlacementArea.DensityTexture == density &&
                other.PlacementArea.GroundColorTexture == original;
            keptRevision = other.PlacementArea.SourceRevision == revision;
        };
        TerrainGrassAlbedoBaker.Baked += observer;
        try
        {
            Assert.That(GrassMicroVerseBridgeUtility.Refresh(bridge, false, false, false, true),
                Is.True, bridge.LastRefreshMessage);
        }
        finally
        {
            TerrainGrassAlbedoBaker.Baked -= observer;
        }

        Assert.That(attempted, Is.True);
        Assert.That(nestedSucceeded, Is.False);
        StringAssert.Contains("still being baked", nestedMessage);
        Assert.That(keptBinding, Is.True);
        Assert.That(keptRevision, Is.True);
        Assert.That(other.PlacementArea.DensityTexture, Is.SameAs(density),
            "A shared-output observer with Auto Refresh disabled must not be left with cleared coverage.");
        Assert.That(other.PlacementArea.GroundColorTexture, Is.SameAs(original));
        Assert.That(bakeCount, Is.EqualTo(previousBakes + 1));
        Assert.That(GrassMicroVerseBridgeUtility.Refresh(other, false, false, false), Is.True, other.LastRefreshMessage);
        Assert.That(other.LastRefreshSucceeded, Is.True);
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

    // Reconstruct the shipped v1 record format before the capture shader was
    // included. This models an upgraded project without modifying source assets
    // or relying on a new shader import changing unrelated Unity dirty counters.
    private string LegacySourceKeyWithoutBakeShader()
    {
        var state = new StringBuilder();
        state.Append("1|").Append(bridge.GroundBakeResolution.ToString(CultureInfo.InvariantCulture)).Append('|');
        state.Append(((int)QualitySettings.activeColorSpace).ToString(CultureInfo.InvariantCulture)).Append('|');
        Value(data.size.x); Value(data.size.z);
        Asset(data); Asset(terrain.materialTemplate); Asset(terrain.materialTemplate.shader);
        state.Append(terrain.materialTemplate.IsKeywordEnabled("_TERRAIN_BLEND_HEIGHT") ? '1' : '0').Append('|');
        Value(terrain.materialTemplate.GetFloat("_HeightTransition"));
        foreach (TerrainLayer source in data.terrainLayers)
        {
            Asset(source); Asset(source.diffuseTexture); Asset(source.maskMapTexture);
            Value(source.tileSize.x); Value(source.tileSize.y);
            Value(source.tileOffset.x); Value(source.tileOffset.y);
            Vector(source.diffuseRemapMin); Vector(source.diffuseRemapMax);
            Vector(source.maskMapRemapMin); Vector(source.maskMapRemapMax);
        }
        return Hash128.Compute(state.ToString()).ToString();

        void Value(float value) => state.Append(value.ToString("R", CultureInfo.InvariantCulture)).Append('|');
        void Vector(Vector4 value) { Value(value.x); Value(value.y); Value(value.z); Value(value.w); }
        void Asset(Object asset)
        {
            if (!asset)
            {
                state.Append("null|");
                return;
            }
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string assetGuid, out long localId), Is.True);
            state.Append(assetGuid).Append(':').Append(localId.ToString(CultureInfo.InvariantCulture)).Append(':');
            state.Append(AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(asset))).Append('|');
        }
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
