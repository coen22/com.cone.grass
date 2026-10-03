using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>Only copied into the disposable validation project, never installed with the package.</summary>
[InitializeOnLoad]
public static class GrassValidationProjectBootstrap
{
    static GrassValidationProjectBootstrap()
    {
        EditorApplication.delayCall += Configure;
    }

    public static void Configure()
    {
        string project = Directory.GetParent(Application.dataPath).FullName;
        if (!File.Exists(Path.Combine(project, ".grass-validation-project")))
            return;
        const string folder = "Assets/ValidationSettings";
        if (!AssetDatabase.IsValidFolder(folder))
            AssetDatabase.CreateFolder("Assets", "ValidationSettings");
        const string pipelinePath = folder + "/GrassValidationPipeline.asset";
        UniversalRenderPipelineAsset pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(pipelinePath);
        if (!pipeline)
        {
            const string rendererPath = folder + "/GrassValidationRenderer.asset";
            UniversalRendererData renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
            if (!renderer)
            {
                renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                renderer.name = "Grass validation renderer";
                AssetDatabase.CreateAsset(renderer, rendererPath);
            }
            pipeline = UniversalRenderPipelineAsset.Create(renderer);
            pipeline.name = "Grass validation pipeline";
            AssetDatabase.CreateAsset(pipeline, pipelinePath);
        }
        // Regeneration can reuse ProjectSettings. An arbitrary existing URP
        // asset must not bypass the validation renderer or Linear color space.
        if (GraphicsSettings.defaultRenderPipeline != pipeline)
            GraphicsSettings.defaultRenderPipeline = pipeline;
        if (QualitySettings.renderPipeline != pipeline)
            QualitySettings.renderPipeline = pipeline;
        if (PlayerSettings.colorSpace != ColorSpace.Linear)
            PlayerSettings.colorSpace = ColorSpace.Linear;
        AssetDatabase.SaveAssets();
        Debug.Log("Grass validation project configured with native URP. GPU tests still require a graphics device.");
    }
}
