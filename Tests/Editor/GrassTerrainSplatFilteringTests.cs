using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Descriptor/binding tests only; no scene, asset, camera or pixel capture is changed.</summary>
public sealed class GrassTerrainSplatFilteringTests
{
    private static MethodInfo SplatMethod(string name, BindingFlags visibility)
    {
        Type type = typeof(InfiniteGrassRenderer).Assembly.GetType("GrassTerrainSplat");
        Assert.That(type, Is.Not.Null, "Resolve the production terrain binding type.");
        MethodInfo method = type.GetMethod(name, BindingFlags.Static | visibility);
        Assert.That(method, Is.Not.Null, "Resolve production method " + name);
        return method;
    }

    private static float LodBias(Texture2D texture, Vector2 tileSize)
    {
        return (float)SplatMethod("TextureLodBias", BindingFlags.NonPublic)
            .Invoke(null, new object[] { texture, tileSize });
    }

    [TestCase(64, 64, 8f, 8f, 3f)]
    [TestCase(16, 256, 8f, 4f, 6f)]
    [TestCase(256, 16, 4f, 8f, 6f)]
    [TestCase(64, 64, 32f, 2f, 5f)]
    [TestCase(16, 16, 64f, 64f, -2f)]
    public void TextureMipScaleUsesBothDimensions(int width, int height, float tileX, float tileY, float expected)
    {
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, true, true);
        try
        {
            Assert.That(LodBias(texture, new Vector2(tileX, tileY)), Is.EqualTo(expected).Within(0.00001f));
        }
        finally
        {
            Object.DestroyImmediate(texture);
        }
    }

    [Test]
    public void DifferentDiffuseAndNormalResolutionsHaveIndependentScales()
    {
        Texture2D diffuse = null, normal = null;
        try
        {
            diffuse = new Texture2D(64, 64, TextureFormat.RGBA32, true, true);
            normal = new Texture2D(256, 32, TextureFormat.RGBA32, true, true);
            var tileSize = new Vector2(4f, 8f);
            Assert.That(LodBias(diffuse, tileSize), Is.EqualTo(4f).Within(0.00001f));
            Assert.That(LodBias(normal, tileSize), Is.EqualTo(6f).Within(0.00001f));
        }
        finally
        {
            if (normal) Object.DestroyImmediate(normal);
            if (diffuse) Object.DestroyImmediate(diffuse);
        }
    }

    [Test]
    public void MipScaleDoesNotRequireReadableTexturePixels()
    {
        var texture = new Texture2D(16, 64, TextureFormat.RGBA32, true, true);
        try
        {
            texture.Apply(false, true);
            Assert.That(texture.isReadable, Is.False);
            Assert.That(LodBias(texture, new Vector2(4f, 4f)), Is.EqualTo(4f).Within(0.00001f));
        }
        finally
        {
            Object.DestroyImmediate(texture);
        }
    }

    [Test]
    public void MissingTerrainResetsEveryLayerBinding()
    {
        var properties = new MaterialPropertyBlock();
        int rect = Shader.PropertyToID("_GrassSplatTerrain");
        int lod = Shader.PropertyToID("_GrassSplatLodBias");
        properties.SetVector(rect, Vector4.one);
        properties.SetVectorArray(lod, new[] { Vector4.one * 99f, Vector4.one * 99f,
            Vector4.one * 99f, Vector4.one * 99f });

        SplatMethod("Bind", BindingFlags.Public).Invoke(null, new object[] { properties, null });

        Assert.That(properties.GetVector(rect), Is.EqualTo(Vector4.zero));
        Vector4[] biases = properties.GetVectorArray(lod);
        Assert.That(biases.Length, Is.EqualTo(4));
        for (int i = 0; i < biases.Length; i++)
        {
            Texture diffuse = properties.GetTexture(Shader.PropertyToID("_GrassSplatDiffuse" + i));
            Texture normal = properties.GetTexture(Shader.PropertyToID("_GrassSplatNormal" + i));
            Assert.That(diffuse, Is.EqualTo(Texture2D.whiteTexture));
            Assert.That(normal, Is.EqualTo(Texture2D.normalTexture));
            Assert.That(biases[i].x, Is.EqualTo(Mathf.Log(Mathf.Max(diffuse.width, diffuse.height), 2f)).Within(0.00001f));
            Assert.That(biases[i].y, Is.EqualTo(Mathf.Log(Mathf.Max(normal.width, normal.height), 2f)).Within(0.00001f));
            Assert.That(biases[i].z, Is.Zero);
            Assert.That(biases[i].w, Is.Zero);
        }
    }
}
