using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

/// <summary>Output guards execute before rendering and can run without a graphics device.</summary>
[NonParallelizable]
public sealed class TerrainGrassAlbedoOutputTests
{
    private readonly List<Object> owned = new List<Object>();
    private string folder;

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
    }

    [TestCase("diffuse")]
    [TestCase("mask")]
    [TestCase("normal")]
    public void SourceTextureCaseAliasesCannotBeUsedAsTheBakeOutput(string sourceKind)
    {
        if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset))
            Assert.Ignore("Ground-albedo output checks require a URP Editor test project.");
        Shader shader = Shader.Find(TerrainGrassAlbedoBaker.NativeTerrainShaderName);
        Assert.That(shader, Is.Not.Null);
        string folderName = "ConeGrassAlbedoOutputTests_" + Guid.NewGuid().ToString("N");
        folder = "Assets/" + folderName;
        AssetDatabase.CreateFolder("Assets", folderName);
        TerrainData data = Own(new TerrainData
        {
            heightmapResolution = 33, alphamapResolution = 16, size = new Vector3(16f, 8f, 16f)
        });
        Terrain terrain = Own(Terrain.CreateTerrainGameObject(data)).GetComponent<Terrain>();
        terrain.materialTemplate = Own(new Material(shader));
        Texture2D source = Own(new Texture2D(2, 2, TextureFormat.RGBA32, false, true));
        source.SetPixels(new[] { Color.red, Color.green, Color.blue, Color.white });
        source.Apply(false, false);
        TerrainLayer layer = Own(new TerrainLayer
        {
            tileSize = new Vector2(16f, 16f),
            diffuseTexture = sourceKind == "diffuse" ? source : Texture2D.grayTexture,
            maskMapTexture = sourceKind == "mask" ? source : null,
            normalMapTexture = sourceKind == "normal" ? source : null
        });
        data.terrainLayers = new[] { layer };
        float[,,] weights = new float[16, 16, 1];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                weights[y, x, 0] = 1f;
        data.SetAlphamaps(0, 0, weights);

        string sourcePath = folder + "/TerrainSource.asset";
        string aliasPath = folder + "/TERRAINSOURCE.ASSET";
        AssetDatabase.CreateAsset(source, sourcePath);
        owned.Remove(source);
        AssetDatabase.SaveAssetIfDirty(source);
        Assert.That(AssetDatabase.LoadAssetAtPath<Texture2D>(aliasPath), Is.SameAs(source),
            "Unity resolves an existing asset path case-insensitively.");
        string guid = AssetDatabase.AssetPathToGUID(sourcePath);
        Color[] before = source.GetPixels();

        Assert.That(TerrainGrassAlbedoBaker.TryBake(terrain, 16, aliasPath,
            out Texture2D result, out string error), Is.False);
        Assert.That(result, Is.Null);
        StringAssert.Contains("source textures", error);
        Assert.That(source.width, Is.EqualTo(2));
        Assert.That(source.GetPixels(), Is.EqualTo(before));
        Assert.That(AssetDatabase.GetAssetPath(source), Is.EqualTo(sourcePath));
        Assert.That(AssetDatabase.AssetPathToGUID(sourcePath), Is.EqualTo(guid));
    }

    private T Own<T>(T value) where T : Object { owned.Add(value); return value; }
}
