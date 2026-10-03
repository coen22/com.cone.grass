using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>
/// Checks source scenes before every player build, including incremental builds.
/// The scene processor only validates and strips; it never silently rebinds a
/// scene differently depending on whether Unity happens to rebuild that scene.
/// </summary>
[BuildCallbackVersion(2)]
public sealed class GrassMicroVerseBridgeBuildProcessor : BuildPlayerProcessor, IProcessSceneWithReport
{
    public override int callbackOrder => -1000;

    public override void PrepareForBuild(BuildPlayerContext buildPlayerContext)
    {
        ValidateSavedScenes(buildPlayerContext.BuildPlayerOptions.scenes ?? Array.Empty<string>());
    }

    /// <summary>
    /// Custom content build scripts can run the same preflight with their scene
    /// paths. Preview scenes leave the author's open scenes and unsaved edits alone.
    /// </summary>
    public static void ValidateSavedScenes(IEnumerable<string> scenePaths)
    {
        var checkedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in scenePaths)
        {
            if (string.IsNullOrEmpty(path) || !checkedPaths.Add(path))
                continue;
            Scene preview = default;
            try
            {
                preview = EditorSceneManager.OpenPreviewScene(path);
                if (!preview.IsValid() || !preview.isLoaded)
                    throw new BuildFailedException("Could not inspect grass bindings in saved scene '" + path + "'.");
                ValidateScene(preview);
            }
            finally
            {
                if (preview.IsValid())
                    EditorSceneManager.ClosePreviewScene(preview);
                GrassMicroVerseBridgeUtility.PruneUnusedMaskCaches();
            }
        }
    }

    public void OnProcessScene(Scene scene, BuildReport report)
    {
        // Unity also processes scenes when entering Play mode.
        if (report == null)
            return;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (GrassMicroVerseBridge bridge in root.GetComponentsInChildren<GrassMicroVerseBridge>(true))
            {
                if (bridge.enabled)
                {
                    // Unity 6.6 tracks these dependencies for BuildContentDirectory.
                    // BuildPlayer does not honor them, which is why the unconditional
                    // saved-scene preflight above is required for player builds.
                    DependOn(bridge.MaskTarget);
                    DependOn(bridge.Terrain ? bridge.Terrain.terrainData : null);
                    DependOn(bridge.GroundLayer);
                    DependOn(bridge.GroundColorOverride);
                    DependOn(bridge.BakedGroundColor);
                    ValidateBridge(bridge, scene.path);
                }
                Object.DestroyImmediate(bridge);
            }
        }
    }

    private static void ValidateScene(Scene scene)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
            foreach (GrassMicroVerseBridge bridge in root.GetComponentsInChildren<GrassMicroVerseBridge>(true))
                if (bridge.enabled) ValidateBridge(bridge, scene.path);
    }

    private static void ValidateBridge(GrassMicroVerseBridge bridge, string scenePath)
    {
        if (!GrassMicroVerseBridgeUtility.TryValidateForBuild(bridge, out string message))
            throw new BuildFailedException(
                $"Grass output in saved scene '{scenePath}' on '{bridge.name}' is not ready: " + message);
    }

    private static void DependOn(Object asset)
    {
        if (asset && !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(asset)))
            BuildPipelineContext.DependOnAsset(asset);
    }
}
