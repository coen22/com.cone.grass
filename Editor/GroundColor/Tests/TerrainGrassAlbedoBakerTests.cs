using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

/// <summary>Run in a URP Editor test project with a graphics device; these exercise the actual GPU bake and saved assets.</summary>
[Category("GrassGPU")]
public sealed class TerrainGrassAlbedoBakerTests
{
    private readonly List<Object> owned = new List<Object>();
    private Terrain terrain;
    private string folder;
    private string outputPath;

    [SetUp]
    public void SetUp()
    {
        if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset) ||
            SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            Assert.Ignore("Ground-albedo GPU tests require a URP Editor project with a graphics device.");
        Shader shader = Shader.Find(TerrainGrassAlbedoBaker.NativeTerrainShaderName);
        Assert.That(shader, Is.Not.Null, "Native URP Terrain/Lit shader must be installed.");
        string name = "ConeGrassAlbedoTests_" + Guid.NewGuid().ToString("N");
        folder = "Assets/" + name;
        AssetDatabase.CreateFolder("Assets", name);
        outputPath = folder + "/Albedo.asset";
        TerrainData data = Own(new TerrainData { heightmapResolution = 33, alphamapResolution = 16, size = new Vector3(16, 8, 16) });
        GameObject gameObject = Own(Terrain.CreateTerrainGameObject(data));
        terrain = gameObject.GetComponent<Terrain>();
        terrain.transform.position = new Vector3(1024, 17, -256);
        terrain.materialTemplate = Own(new Material(shader));
        terrain.materialTemplate.DisableKeyword("_TERRAIN_BLEND_HEIGHT");
        terrain.materialTemplate.SetFloat("_EnableHeightBlend", 0f);
    }

    [TearDown]
    public void TearDown()
    {
        for (int i = owned.Count - 1; i >= 0; i--)
            if (owned[i])
                Object.DestroyImmediate(owned[i]);
        owned.Clear();
        if (!string.IsNullOrEmpty(folder))
            AssetDatabase.DeleteAsset(folder);
        folder = null;
        terrain = null;
    }

    [Test]
    public void NonReadableSourcesBlendAcrossControlGroupsAndUpdateAssetInPlace()
    {
        TerrainLayer[] layers = new TerrainLayer[5];
        for (int i = 0; i < layers.Length; i++)
            layers[i] = Layer(i == 0 ? Color.red : Color.blue, true);
        SetLayers(layers, new[] { 0.25f, 0f, 0f, 0f, 0.75f });
        Texture2D first = Bake();
        AssertColor(first.GetPixel(8, 8), new Color(0.25f, 0f, 0.75f), 0.015f);
        string guid = AssetDatabase.AssetPathToGUID(outputPath);
        EntityId id = first.GetEntityId();
        SetLayers(layers, new[] { 0.75f, 0f, 0f, 0f, 0.25f });
        Texture2D second = Bake();
        Assert.That(second.GetEntityId(), Is.EqualTo(id));
        Assert.That(AssetDatabase.AssetPathToGUID(outputPath), Is.EqualTo(guid));
        Assert.That(second.isDataSRGB, Is.False);
        Assert.That(second.mipmapCount, Is.GreaterThan(1));
        AssertColor(second.GetPixel(8, 8), new Color(0.75f, 0f, 0.25f), 0.015f);
    }

    [Test]
    public void OpacityAsDensityNormalizesTheFinalAlbedo()
    {
        TerrainLayer red = Layer(new Color(1f, 0f, 0f, 0.5f), true);
        red.diffuseRemapMin = new Vector4(0f, 0f, 0f, 1f);
        red.diffuseRemapMax = Vector4.one;
        SetLayers(new[] { red, Layer(Color.blue, true) }, new[] { 0.5f, 0.5f });
        AssertColor(Bake().GetPixel(8, 8), Color.blue, 0.015f);
    }

    [Test]
    public void HeightBlendUsesRemappedMaskBlueAndDisablesAboveFourLayers()
    {
        TerrainLayer red = Layer(Color.red, true);
        TerrainLayer blue = Layer(Color.blue, true);
        red.maskMapTexture = Solid(new Color(0f, 0f, 0.2f, 1f), true);
        blue.maskMapTexture = Solid(new Color(0f, 0f, 0.2f, 1f), true);
        blue.maskMapRemapMin = new Vector4(0f, 0f, 0.8f, 0f);
        blue.maskMapRemapMax = Vector4.one;
        terrain.materialTemplate.SetFloat("_EnableHeightBlend", 1f);
        terrain.materialTemplate.SetFloat("_HeightTransition", 0.01f);
        terrain.materialTemplate.EnableKeyword("_TERRAIN_BLEND_HEIGHT");
        SetLayers(new[] { red, blue }, new[] { 0.5f, 0.5f });
        AssertColor(Bake().GetPixel(8, 8), Color.blue, 0.015f);
        SetLayers(new[] { red, blue, Layer(Color.green, true), Layer(Color.white, true), Layer(Color.black, true) },
            new[] { 0.5f, 0.5f, 0f, 0f, 0f });
        AssertColor(Bake().GetPixel(8, 8), new Color(0.5f, 0f, 0.5f), 0.015f);
    }

    [Test]
    public void TextureOrientationTilingAndOffsetAreTerrainRelative()
    {
        Texture2D checker = Own(new Texture2D(2, 2, TextureFormat.RGBA32, false, true));
        checker.filterMode = FilterMode.Point;
        checker.wrapMode = TextureWrapMode.Repeat;
        checker.SetPixels(new[] { Color.red, Color.green, Color.blue, Color.white });
        checker.Apply(false, true);
        TerrainLayer layer = Layer(Color.white, true);
        layer.diffuseTexture = checker;
        layer.tileSize = new Vector2(16f, 16f);
        SetLayers(new[] { layer }, new[] { 1f });
        Texture2D first = Bake();
        AssertColor(first.GetPixel(2, 2), Color.red, 0.01f);
        AssertColor(first.GetPixel(2, 13), Color.blue, 0.01f);
        layer.tileOffset = new Vector2(8f, 0f);
        Texture2D shifted = Bake();
        AssertColor(shifted.GetPixel(2, 2), Color.green, 0.01f);
        layer.tileSize = new Vector2(8f, 16f);
        layer.tileOffset = Vector2.zero;
        Texture2D tiled = Bake();
        AssertColor(tiled.GetPixel(2, 2), Color.red, 0.01f);
        AssertColor(tiled.GetPixel(6, 2), Color.green, 0.01f);
    }

    [Test]
    public void DiffuseRemapUsesScaleWithoutAddingTheMinimum()
    {
        TerrainLayer layer = Layer(Color.white, true);
        layer.diffuseRemapMin = new Vector4(0.2f, 0.1f, 0f, 0f);
        layer.diffuseRemapMax = new Vector4(0.6f, 0.7f, 0.8f, 1f);
        SetLayers(new[] { layer }, new[] { 1f });
        AssertColor(Bake().GetPixel(8, 8), new Color(0.4f, 0.6f, 0.8f), 0.015f);
    }

    [Test]
    public void SourceSignatureTracksEditsButDoesNotChangeWhenSavingOutput()
    {
        TerrainLayer layer = Layer(Color.green, true);
        SetLayers(new[] { layer }, new[] { 1f });
        Assert.That(TerrainGrassAlbedoBaker.TryGetSourceSignature(terrain, 16, out Hash128 first, out string error), Is.True, error);
        Bake();
        Assert.That(TerrainGrassAlbedoBaker.TryGetSourceSignature(terrain, 16, out Hash128 afterBake, out error), Is.True, error);
        Assert.That(afterBake, Is.EqualTo(first));
        layer.tileOffset = Vector2.one;
        Assert.That(TerrainGrassAlbedoBaker.TryGetSourceSignature(terrain, 16, out Hash128 afterEdit, out error), Is.True, error);
        Assert.That(afterEdit, Is.Not.EqualTo(first));
        TerrainGrassAlbedoBaker.Invalidate(terrain);
        Assert.That(TerrainGrassAlbedoBaker.TryGetSourceSignature(terrain, 16, out Hash128 afterInvalidation, out error), Is.True, error);
        Assert.That(afterInvalidation, Is.Not.EqualTo(afterEdit));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SourceSignatureTracksVOnlyWrappingForDiffuseAndMaskTextures(bool maskTexture)
    {
        TerrainLayer layer = Layer(Color.red, true);
        Texture2D source = Own(new Texture2D(2, 2, TextureFormat.RGBA32, false, true));
        source.wrapMode = TextureWrapMode.Repeat;
        source.filterMode = FilterMode.Point;
        source.SetPixels(new[] { Color.red, Color.red, Color.blue, Color.blue });
        source.Apply(false, true);
        layer.tileSize = new Vector2(16f, 8f);
        if (maskTexture)
        {
            layer.maskMapTexture = source;
            terrain.materialTemplate.EnableKeyword("_TERRAIN_BLEND_HEIGHT");
            terrain.materialTemplate.SetFloat("_HeightTransition", 0.01f);
            SetLayers(new[] { layer, Layer(Color.blue, true) }, new[] { 0.5f, 0.5f });
        }
        else
        {
            layer.diffuseTexture = source;
            SetLayers(new[] { layer }, new[] { 1f });
        }

        // Sampler metadata must be part of the signature even when a producer
        // keeps temporary sampling edits outside the asset's dirty state.
        EditorUtility.ClearDirty(source);
        uint updateCount = source.updateCount;
        int dirtyCount = EditorUtility.GetDirtyCount(source);
        Assert.That(TerrainGrassAlbedoBaker.TryGetSourceSignature(terrain, 16,
            out Hash128 repeated, out string error), Is.True, error);
        AssertColor(Bake().GetPixel(2, 10), maskTexture ? Color.blue : Color.red, 0.015f);
        source.wrapModeV = TextureWrapMode.Clamp;
        EditorUtility.ClearDirty(source);
        Assert.That(source.wrapMode, Is.EqualTo(TextureWrapMode.Repeat), "The combined getter still exposes the unchanged U axis.");
        Assert.That(source.updateCount, Is.EqualTo(updateCount), "Changing addressing must not require a pixel upload.");
        Assert.That(EditorUtility.GetDirtyCount(source), Is.EqualTo(dirtyCount), "The sampler edit must be detected independently of dirty counters.");
        Assert.That(TerrainGrassAlbedoBaker.TryGetSourceSignature(terrain, 16,
            out Hash128 clamped, out error), Is.True, error);
        Assert.That(clamped, Is.Not.EqualTo(repeated));
        Assert.That(TerrainGrassAlbedoBaker.TryGetSourceSignature(terrain, 16,
            out Hash128 unchanged, out error), Is.True, error);
        Assert.That(unchanged, Is.EqualTo(clamped), "Unchanged sampler settings must settle after the edit.");
        AssertColor(Bake().GetPixel(2, 10), maskTexture ? Color.red : Color.blue, 0.015f);
    }

    [Test]
    public void SourceSignatureTracksAnisotropyWithoutPixelEdits()
    {
        TerrainLayer layer = Layer(Color.green, true);
        Texture2D source = Own(new Texture2D(8, 8, TextureFormat.RGBA32, true, true));
        source.filterMode = FilterMode.Trilinear;
        source.anisoLevel = 1;
        source.Apply(true, true);
        layer.diffuseTexture = source;
        SetLayers(new[] { layer }, new[] { 1f });
        EditorUtility.ClearDirty(source);
        uint updateCount = source.updateCount;
        int dirtyCount = EditorUtility.GetDirtyCount(source);
        Assert.That(TerrainGrassAlbedoBaker.TryGetSourceSignature(terrain, 16,
            out Hash128 original, out string error), Is.True, error);

        source.anisoLevel = 8;
        EditorUtility.ClearDirty(source);
        Assert.That(source.anisoLevel, Is.EqualTo(8));
        Assert.That(source.updateCount, Is.EqualTo(updateCount));
        Assert.That(EditorUtility.GetDirtyCount(source), Is.EqualTo(dirtyCount));
        Assert.That(TerrainGrassAlbedoBaker.TryGetSourceSignature(terrain, 16,
            out Hash128 changed, out error), Is.True, error);
        Assert.That(changed, Is.Not.EqualTo(original));
        Assert.That(TerrainGrassAlbedoBaker.TryGetSourceSignature(terrain, 16,
            out Hash128 unchanged, out error), Is.True, error);
        Assert.That(unchanged, Is.EqualTo(changed), "An unchanged sampler must not keep invalidating the bake.");
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(5)]
    public void SelectedLayerCaptureMatchesNativeGroupSampler(int selectedIndex)
    {
        TerrainLayer[] layers = new TerrainLayer[selectedIndex + 1];
        float[] weights = new float[layers.Length];
        for (int i = 0; i < layers.Length; i++)
            layers[i] = Layer(Color.white, true);
        weights[selectedIndex] = 1f;
        Texture2D selectedTexture = Own(new Texture2D(2, 2, TextureFormat.RGBA32, false, true));
        selectedTexture.SetPixels(new[] { Color.red, Color.green, Color.blue, Color.white });
        selectedTexture.filterMode = FilterMode.Bilinear;
        selectedTexture.wrapMode = TextureWrapMode.Repeat;
        selectedTexture.Apply(false, true);
        TerrainLayer selectedLayer = layers[selectedIndex];
        selectedLayer.diffuseTexture = selectedTexture;
        selectedLayer.tileSize = new Vector2(16f, 8f);
        SetLayers(layers, weights);

        Texture2D samplerSource = layers[(selectedIndex / 4) * 4].diffuseTexture;
        var areaObject = Own(new GameObject("Native selected-layer comparison"));
        GrassPlacementArea area = areaObject.AddComponent<GrassPlacementArea>();
        area.ConfigureTexture(terrain, Texture2D.whiteTexture, selectedLayer);
        for (int variant = 0; variant < 2; variant++)
        {
            // In later-layer cases the selected texture keeps Bilinear/Repeat.
            // Native TerrainLit uses the first diffuse sampler in its group,
            // even when that first layer contributes no color at this point.
            samplerSource.filterMode = variant == 0 ? FilterMode.Point : FilterMode.Bilinear;
            samplerSource.wrapModeU = TextureWrapMode.Repeat;
            samplerSource.wrapModeV = variant == 0 ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
            Assert.That(area.TryGetCaptureData(out GrassPlacementDrawData draw), Is.True);
            Assert.That(draw.GroundLayerTexture, Is.SameAs(selectedTexture));
            Assert.That(draw.GroundLayerSamplerTexture, Is.SameAs(samplerSource));
            Color[] expected = Bake().GetPixels();
            Color[] actual = CaptureSelectedLayer(draw);
            Assert.That(actual.Length, Is.EqualTo(expected.Length));
            for (int pixel = 0; pixel < actual.Length; pixel++)
            {
                AssertColor(actual[pixel], expected[pixel], 0.015f);
                Assert.That(actual[pixel].a, Is.EqualTo(1f).Within(0.015f));
            }
        }
    }

    [Test]
    public void CameraMipBiasCannotChangeTheSavedAlbedo()
    {
        Texture2D source = Own(new Texture2D(64, 64, TextureFormat.RGBAHalf, true, true));
        source.filterMode = FilterMode.Point;
        source.wrapMode = TextureWrapMode.Repeat;
        for (int mip = 0; mip < source.mipmapCount; mip++)
        {
            int size = Mathf.Max(1, 64 >> mip);
            Color color = mip == 0 ? Color.red : mip == 1 ? Color.green : Color.blue;
            Color[] pixels = new Color[size * size];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = color;
            source.SetPixels(pixels, mip);
        }
        source.Apply(false, true);
        TerrainLayer layer = Layer(Color.white, true);
        layer.diffuseTexture = source;
        SetLayers(new[] { layer }, new[] { 1f });
        int property = Shader.PropertyToID("_GlobalMipBias");
        Vector4 previous = Shader.GetGlobalVector(property);
        try
        {
            Shader.SetGlobalVector(property, new Vector4(0f, 1f, 0f, 0f));
            Color[] expected = Bake().GetPixels();
            Shader.SetGlobalVector(property, new Vector4(-6f, 1f / 64f, 0f, 0f));
            Color[] actual = Bake().GetPixels();
            Assert.That(actual.Length, Is.EqualTo(expected.Length));
            for (int i = 0; i < actual.Length; i++)
                AssertColor(actual[i], expected[i], 0.0001f);
        }
        finally
        {
            Shader.SetGlobalVector(property, previous);
        }
    }

    [Test]
    public void RebakingSharedOutputInvalidatesOnlyEachAreasGroundColor()
    {
        SetLayers(new[] { Layer(Color.green, true) }, new[] { 1f });
        Texture2D output = Bake();
        GrassPlacementArea[] areas = new GrassPlacementArea[2];
        uint[] groundRevisions = new uint[2], densityRevisions = new uint[2], surfaceRevisions = new uint[2];
        for (int i = 0; i < areas.Length; i++)
        {
            GameObject gameObject = Own(new GameObject("Baked ground consumer " + i));
            GrassPlacementArea area = gameObject.AddComponent<GrassPlacementArea>();
            area.ConfigureTexture(terrain, Texture2D.whiteTexture);
            area.SetGroundColorTexture(output, true);
            Assert.That(area.TryGetCaptureData(out _), Is.True);
            areas[i] = area;
            groundRevisions[i] = area.GroundColorRevision;
            densityRevisions[i] = area.DensityRevision;
            surfaceRevisions[i] = area.SurfaceRevision;
        }
        Assert.That(Bake(), Is.SameAs(output));
        for (int i = 0; i < areas.Length; i++)
        {
            Assert.That(areas[i].GroundColorRevision, Is.GreaterThan(groundRevisions[i]));
            Assert.That(areas[i].DensityRevision, Is.EqualTo(densityRevisions[i]));
            Assert.That(areas[i].SurfaceRevision, Is.EqualTo(surfaceRevisions[i]));
        }
    }

    [Test]
    public void FailingBakeObserverDoesNotPreventLaterObserversReceivingSavedOutput()
    {
        SetLayers(new[] { Layer(Color.green, true) }, new[] { 1f });
        Terrain notifiedTerrain = null;
        Texture2D notifiedTexture = null;
        Action<Terrain, Texture2D> failingObserver = (source, output) =>
            throw new InvalidOperationException("Injected ground-albedo observer failure.");
        Action<Terrain, Texture2D> laterObserver = (source, output) =>
        {
            notifiedTerrain = source;
            notifiedTexture = output;
        };
        TerrainGrassAlbedoBaker.Baked += failingObserver;
        TerrainGrassAlbedoBaker.Baked += laterObserver;
        try
        {
            LogAssert.Expect(LogType.Exception, new Regex("Injected ground-albedo observer failure"));
            Texture2D output = Bake();
            Assert.That(notifiedTerrain, Is.SameAs(terrain));
            Assert.That(notifiedTexture, Is.SameAs(output),
                "All observers must receive the successfully saved output even when an earlier observer fails.");
            Assert.That(AssetDatabase.LoadAssetAtPath<Texture2D>(outputPath), Is.SameAs(output));
            Assert.That(EditorUtility.IsDirty(output), Is.False);
            AssertColor(output.GetPixel(8, 8), Color.green, 0.015f);
        }
        finally
        {
            TerrainGrassAlbedoBaker.Baked -= failingObserver;
            TerrainGrassAlbedoBaker.Baked -= laterObserver;
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ReentrantBakeCannotOverwriteAnAliasedOrMovedOutput(bool moveOutput)
    {
        SetLayers(new[] { Layer(Color.green, true) }, new[] { 1f });
        string requestedPath = moveOutput ? folder + "/Moved.asset" :
            outputPath.Replace("Albedo.asset", "ALBEDO.ASSET").Replace('/', '\\');
        bool attempted = false, nestedSucceeded = true;
        string nestedError = null, moveError = null, originalGuid = null;
        Texture2D nestedTexture = null;
        Action<Terrain, Texture2D> observer = (source, output) =>
        {
            // Keep the regression bounded even without the reentrancy guard.
            if (source != terrain || attempted)
                return;
            attempted = true;
            originalGuid = AssetDatabase.AssetPathToGUID(outputPath);
            if (moveOutput)
                moveError = AssetDatabase.MoveAsset(outputPath, requestedPath);
            nestedSucceeded = TerrainGrassAlbedoBaker.TryBake(terrain, 32, requestedPath,
                out nestedTexture, out nestedError);
        };
        TerrainGrassAlbedoBaker.Baked += observer;
        Texture2D first;
        try
        {
            first = Bake();
        }
        finally
        {
            TerrainGrassAlbedoBaker.Baked -= observer;
        }

        Assert.That(attempted, Is.True);
        if (moveOutput)
            Assert.That(moveError, Is.Empty);
        Assert.That(nestedSucceeded, Is.False);
        Assert.That(nestedTexture, Is.Null);
        StringAssert.Contains("already owns this output", nestedError);
        Assert.That(first.width, Is.EqualTo(16), "A callback cannot resize the output before the original caller receives it.");
        AssertColor(first.GetPixel(8, 8), Color.green, 0.015f);
        string actualPath = AssetDatabase.GetAssetPath(first);
        Assert.That(AssetDatabase.AssetPathToGUID(actualPath), Is.EqualTo(originalGuid));

        Assert.That(TerrainGrassAlbedoBaker.TryBake(terrain, 32, actualPath,
            out Texture2D later, out string error), Is.True, error);
        Assert.That(later, Is.SameAs(first));
        Assert.That(later.width, Is.EqualTo(32), "Output ownership must be released after notifications finish.");
    }

    [Test]
    public void UnsupportedMaterialsAndSourceOverwriteAreRejected()
    {
        TerrainLayer layer = Layer(Color.green, false);
        SetLayers(new[] { layer }, new[] { 1f });
        Shader custom = Shader.Find("Universal Render Pipeline/Unlit");
        Assert.That(custom, Is.Not.Null);
        terrain.materialTemplate = Own(new Material(custom));
        Assert.That(TerrainGrassAlbedoBaker.TryBake(terrain, 16, outputPath, out _, out string error), Is.False);
        StringAssert.Contains("native", error);
        Assert.That(AssetDatabase.LoadMainAssetAtPath(outputPath), Is.Null);
        terrain.materialTemplate = Own(new Material(Shader.Find(TerrainGrassAlbedoBaker.NativeTerrainShaderName)));
        owned.Remove(layer.diffuseTexture);
        AssetDatabase.CreateAsset(layer.diffuseTexture, outputPath);
        Assert.That(TerrainGrassAlbedoBaker.TryBake(terrain, 16, outputPath, out _, out error), Is.False);
        StringAssert.Contains("source textures", error);
    }

    private Texture2D Bake()
    {
        bool result = TerrainGrassAlbedoBaker.TryBake(terrain, 16, outputPath, out Texture2D texture, out string error);
        Assert.That(result, Is.True, error);
        return texture;
    }

    private Color[] CaptureSelectedLayer(GrassPlacementDrawData draw)
    {
        Shader shader = Shader.Find("Hidden/InfiniteGrass/Placement");
        Assert.That(shader, Is.Not.Null);
        Material material = Own(new Material(shader));
        int pass = material.FindPass("PlacementGroundColor");
        Assert.That(pass, Is.GreaterThanOrEqualTo(0));
        ShaderUtil.CompilePass(material, pass, true);
        Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
        Mesh quad = Own(new Mesh
        {
            vertices = new[] { new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, -0.5f),
                new Vector3(-0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, 0.5f) },
            triangles = new[] { 0, 2, 1, 1, 2, 3 }
        });
        Vector3 origin = terrain.transform.position;
        Vector3 size = terrain.terrainData.size;
        Vector3 cameraPosition = origin + new Vector3(size.x * 0.5f, size.y + 1f, size.z * 0.5f);
        Matrix4x4 view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) *
            Matrix4x4.TRS(cameraPosition, Quaternion.LookRotation(Vector3.down, Vector3.forward), Vector3.one).inverse;
        Matrix4x4 projection = Matrix4x4.Ortho(-size.x * 0.5f, size.x * 0.5f,
            -size.z * 0.5f, size.z * 0.5f, 0.01f, size.y + 2f);
        material.SetMatrix("_GrassCaptureVP", GL.GetGPUProjectionMatrix(projection, true) * view);
        // Match production's MPB sampler binding and selected-layer branch.
        var properties = new MaterialPropertyBlock();
        properties.SetMatrix("_PlacementWorldToMask", draw.WorldToMask);
        properties.SetMatrix("_PlacementGroundWorldToMask", draw.GroundWorldToMask);
        properties.SetVector("_PlacementTerrainRect", draw.TerrainRect);
        properties.SetInteger("_PlacementHasTerrain", 1);
        properties.SetInteger("_PlacementShape", (int)draw.Shape);
        properties.SetFloat("_PlacementDensity", draw.Density);
        properties.SetFloat("_PlacementEdgeFalloff", draw.EdgeFalloff);
        properties.SetTexture("_PlacementDensityTexture", draw.DensityTexture);
        properties.SetInteger("_PlacementHasGroundColor", 0);
        properties.SetInteger("_PlacementHasGroundLayer", 1);
        properties.SetTexture("_PlacementGroundLayerTexture", draw.GroundLayerTexture);
        Texture layerSampler = draw.GroundLayerSamplerTexture ? draw.GroundLayerSamplerTexture :
            draw.GroundLayerTexture ? draw.GroundLayerTexture : Texture2D.whiteTexture;
        properties.SetTexture("_PlacementGroundLayerSamplerTexture", layerSampler);
        properties.SetInteger("_PlacementGroundLayerIsSampler", draw.GroundLayerTexture == layerSampler ? 1 : 0);
        properties.SetVector("_PlacementGroundLayerUV", draw.GroundLayerUV);
        properties.SetVector("_PlacementGroundRemapMin", draw.GroundLayerRemapMin);
        properties.SetVector("_PlacementGroundRemapMax", draw.GroundLayerRemapMax);
        properties.SetColor("_PlacementGroundTint", draw.GroundTint);
        properties.SetFloat("_PlacementGroundStrength", draw.GroundColorStrength);
        RenderTexture capture = RenderTexture.GetTemporary(16, 16, 0,
            RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        Texture2D readback = Own(new Texture2D(16, 16, TextureFormat.RGBAHalf, false, true));
        RenderTexture previous = RenderTexture.active;
        bool previousSRGBWrite = GL.sRGBWrite;
        var commands = new CommandBuffer();
        try
        {
            GL.sRGBWrite = false;
            commands.SetRenderTarget(capture);
            commands.SetViewport(new Rect(0, 0, 16, 16));
            commands.ClearRenderTarget(false, true, Color.clear);
            commands.DrawMesh(quad, draw.LocalToWorld, material, 0, pass, properties);
            Graphics.ExecuteCommandBuffer(commands);
            RenderTexture.active = capture;
            readback.ReadPixels(new Rect(0, 0, 16, 16), 0, 0, false);
            readback.Apply(false, false);
            return readback.GetPixels();
        }
        finally
        {
            RenderTexture.active = previous;
            GL.sRGBWrite = previousSRGBWrite;
            commands.Release();
            RenderTexture.ReleaseTemporary(capture);
        }
    }
    private TerrainLayer Layer(Color color, bool nonReadable)
    {
        TerrainLayer layer = Own(new TerrainLayer());
        layer.diffuseTexture = Solid(color, nonReadable);
        layer.tileSize = new Vector2(16, 16);
        layer.diffuseRemapMin = Vector4.zero;
        layer.diffuseRemapMax = Vector4.one;
        layer.maskMapRemapMin = Vector4.zero;
        layer.maskMapRemapMax = Vector4.one;
        return layer;
    }
    private Texture2D Solid(Color color, bool nonReadable)
    {
        Texture2D texture = Own(new Texture2D(2, 2, TextureFormat.RGBAHalf, false, true));
        texture.SetPixels(new[] { color, color, color, color });
        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Repeat;
        texture.Apply(false, nonReadable);
        return texture;
    }
    private void SetLayers(TerrainLayer[] layers, float[] weights)
    {
        terrain.terrainData.terrainLayers = layers;
        float[,,] controls = new float[16, 16, layers.Length];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                for (int layer = 0; layer < layers.Length; layer++)
                    controls[y, x, layer] = weights[layer];
        terrain.terrainData.SetAlphamaps(0, 0, controls);
    }
    private T Own<T>(T value) where T : Object { owned.Add(value); return value; }
    private static void AssertColor(Color actual, Color expected, float tolerance)
    {
        Assert.That(actual.r, Is.EqualTo(expected.r).Within(tolerance));
        Assert.That(actual.g, Is.EqualTo(expected.g).Within(tolerance));
        Assert.That(actual.b, Is.EqualTo(expected.b).Within(tolerance));
    }
}
