using System;
using System.IO;
using UnityEditor;
using UnityEditor.Rendering;
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
        EnsureGlobalSettings(folder);
        // Checked code still honors this independent project setting. Query
        // the configured pipeline explicitly, including before its first frame.
        if (!EditorGraphicsSettings.TryGetRenderPipelineSettingsForPipeline<RenderGraphGlobalSettings, UniversalRenderPipeline>(out var renderGraph))
            throw new InvalidOperationException("The validation pipeline has no RenderGraph Graphics settings.");
        renderGraph.enableValidityChecks = true;
        AssetDatabase.SaveAssets();
        Debug.Log("Grass validation project configured with native URP. GPU tests still require a graphics device.");
    }

    private static void EnsureGlobalSettings(string folder)
    {
        if (EditorGraphicsSettings.GetRenderPipelineGlobalSettingsAsset<UniversalRenderPipeline>() != null)
            return;
        // A fresh headless Editor has not necessarily created a pipeline yet.
        // Use Core's public asset factory to populate and initialize settings.
        // URP 17.6 keeps its concrete global-settings type internal, so discover
        // the asset type through the public Editor TypeCache without calling
        // internal constructors or initialization methods.
        string path = folder + "/GrassValidationGlobalSettings.asset";
        RenderPipelineGlobalSettings globalSettings = AssetDatabase.LoadAssetAtPath<RenderPipelineGlobalSettings>(path);
        if (!globalSettings)
        {
            foreach (Type candidate in TypeCache.GetTypesDerivedFrom<RenderPipelineGlobalSettings>())
            {
                if (candidate.FullName != "UnityEngine.Rendering.Universal.UniversalRenderPipelineGlobalSettings")
                    continue;
                globalSettings = RenderPipelineGlobalSettingsUtils.Create(candidate, path);
                break;
            }
        }
        if (!globalSettings)
            throw new InvalidOperationException("Could not create the validation pipeline's global settings asset.");
        EditorGraphicsSettings.SetRenderPipelineGlobalSettingsAsset<UniversalRenderPipeline>(globalSettings);
    }
}
