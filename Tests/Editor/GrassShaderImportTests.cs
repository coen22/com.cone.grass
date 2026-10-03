using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

[NonParallelizable]
public sealed class GrassShaderImportTests
{
    private static readonly string[] SearchFolders =
    {
        "Packages/com.cone.grass/Runtime",
        "Packages/com.cone.grass/Editor/GroundColor"
    };

    [Test]
    public void PackageShaderImportsHaveNoReportedErrors()
    {
        List<string> errors = new List<string>();
        string[] shaderAssets = AssetDatabase.FindAssets("t:Shader", SearchFolders);
        Assert.That(shaderAssets.Length, Is.GreaterThan(0), "The installed package must contain imported shaders.");
        foreach (string guid in shaderAssets)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            Assert.That(shader, Is.Not.Null, path);
            AddErrors(errors, path, ShaderUtil.GetShaderMessages(shader));
        }
        string[] computeAssets = AssetDatabase.FindAssets("t:ComputeShader", SearchFolders);
        Assert.That(computeAssets.Length, Is.GreaterThan(0));
        foreach (string guid in computeAssets)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
            Assert.That(shader, Is.Not.Null, path);
            AddErrors(errors, path, ShaderUtil.GetComputeShaderMessages(shader));
        }
        Assert.That(errors, Is.Empty, string.Join("\n", errors));
    }

    [Test]
    [Category("GrassGPU")]
    public void DefaultPassesCompileOnTheActiveGraphicsApi()
    {
        RequireGraphics();
        List<string> errors = new List<string>();
        string[] shaders = AssetDatabase.FindAssets("t:Shader", SearchFolders);
        Assert.That(shaders.Length, Is.GreaterThan(0), "No shader passes were available to compile.");
        foreach (string guid in shaders)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            Assert.That(shader, Is.Not.Null, path);
            Material material = new Material(shader);
            try
            {
                Assert.That(material.passCount, Is.GreaterThan(0), path + " has no supported passes on this API.");
                for (int pass = 0; pass < material.passCount; pass++)
                {
                    ShaderUtil.CompilePass(material, pass, true);
                    Assert.That(ShaderUtil.IsPassCompiled(material, pass), Is.True,
                        path + " pass " + pass + " did not finish compilation.");
                }
                AddErrors(errors, path, ShaderUtil.GetShaderMessages(shader));
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }
        Assert.That(errors, Is.Empty, string.Join("\n", errors));
    }

    [TestCase("MainShadow")]
    [TestCase("CascadedSoft")]
    [TestCase("ScreenShadow")]
    [TestCase("ForwardPlus")]
    [TestCase("GroundNormals")]
    [TestCase("ForwardPlusGround")]
    [Category("GrassGPU")]
    public void RepresentativeBladeLightingVariantsCompile(string variant)
    {
        RequireGraphics();
        Shader shader = AssetDatabase.LoadAssetAtPath<Shader>("Packages/com.cone.grass/Runtime/Shaders/GrassBladeShader.shader");
        Assert.That(shader, Is.Not.Null);
        Material material = new Material(shader);
        try
        {
            if (variant == "MainShadow")
                material.EnableKeyword("_MAIN_LIGHT_SHADOWS");
            if (variant == "CascadedSoft" || variant.StartsWith("ForwardPlus"))
            {
                material.EnableKeyword("_MAIN_LIGHT_SHADOWS_CASCADE");
                material.EnableKeyword("_SHADOWS_SOFT_HIGH");
            }
            if (variant == "ScreenShadow")
                material.EnableKeyword("_MAIN_LIGHT_SHADOWS_SCREEN");
            if (variant.StartsWith("ForwardPlus"))
            {
                material.EnableKeyword("_GRASS_ADDITIONAL_LIGHTS");
                material.EnableKeyword("_CLUSTER_LIGHT_LOOP");
                material.EnableKeyword("_ADDITIONAL_LIGHTS");
                material.EnableKeyword("_ADDITIONAL_LIGHT_SHADOWS");
            }
            if (variant == "GroundNormals" || variant == "ForwardPlusGround")
                material.EnableKeyword("_GRASS_GROUND_NORMAL");
            Assert.That(material.passCount, Is.GreaterThan(0));
            for (int pass = 0; pass < material.passCount; pass++)
            {
                ShaderUtil.CompilePass(material, pass, true);
                Assert.That(ShaderUtil.IsPassCompiled(material, pass), Is.True,
                    variant + " pass " + pass + " did not finish compilation.");
            }
            var errors = new List<string>();
            AddErrors(errors, variant, ShaderUtil.GetShaderMessages(shader));
            Assert.That(errors, Is.Empty, string.Join("\n", errors));
        }
        finally
        {
            Object.DestroyImmediate(material);
        }
    }

    private static void RequireGraphics()
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            Assert.Ignore("Shader pass compilation needs an active graphics device. A headless import is a separate check.");
    }

    private static void AddErrors(List<string> errors, string source, IEnumerable<ShaderMessage> messages)
    {
        errors.AddRange(messages.Where(message => message.severity == ShaderCompilerMessageSeverity.Error)
            .Select(message => source + ": " + message.file + ":" + message.line + " " + message.message));
    }
}
