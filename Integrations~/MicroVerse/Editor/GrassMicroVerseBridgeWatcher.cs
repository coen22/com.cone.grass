using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

/// <summary>
/// Deferred editor refresh and optional polling for saved producer outputs.
/// Asset events are deliberately not treated as MicroVerse completion events.
/// </summary>
[InitializeOnLoad]
public static class GrassMicroVerseBridgeWatcher
{
    private const double DebounceSeconds = 0.35;
    private static readonly Dictionary<GrassMicroVerseBridge, double> nextPoll =
        new Dictionary<GrassMicroVerseBridge, double>();
    private static readonly List<GrassMicroVerseBridge> staleEntries =
        new List<GrassMicroVerseBridge>();
    private static double refreshAfter = -1;

    static GrassMicroVerseBridgeWatcher()
    {
        EditorApplication.update += Update;
        EditorApplication.projectChanged += QueueRefresh;
        EditorApplication.hierarchyChanged += QueueRefresh;
        Undo.undoRedoPerformed += QueueRefresh;
        EditorSceneManager.sceneOpened += SceneOpened;
        EditorSceneManager.sceneSaved += SceneSaved;
    }

    public static void QueueRefresh()
    {
        refreshAfter = EditorApplication.timeSinceStartup + DebounceSeconds;
    }

    private static void SceneOpened(Scene scene, OpenSceneMode mode) => QueueRefresh();
    private static void SceneSaved(Scene scene) => QueueRefresh();

    private static void Update()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling ||
            EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer)
            return;

        double now = EditorApplication.timeSinceStartup;
        if (refreshAfter >= 0 && now < refreshAfter)
            return;

        bool refreshAll = refreshAfter >= 0;
        refreshAfter = -1;
        bool refreshed = false;

        foreach (GrassMicroVerseBridge bridge in GrassMicroVerseBridge.ActiveBridges)
        {
            if (!bridge || !bridge.AutoRefresh)
                continue;

            bool changed = bridge.ConsumeRefreshRequest();
            bool poll = bridge.PollWhileEditing &&
                (!nextPoll.TryGetValue(bridge, out double due) || now >= due);
            if (!changed && !refreshAll && !poll)
                continue;

            GrassMicroVerseBridgeUtility.Refresh(bridge);
            nextPoll[bridge] = now + bridge.PollInterval;
            refreshed = true;
        }

        staleEntries.Clear();
        foreach (GrassMicroVerseBridge bridge in nextPoll.Keys)
        {
            if (!bridge || !bridge.isActiveAndEnabled)
                staleEntries.Add(bridge);
        }

        foreach (GrassMicroVerseBridge bridge in staleEntries)
            nextPoll.Remove(bridge);

        if (refreshed)
        {
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
        }
    }
}

public sealed class GrassMicroVerseMaskPostprocessor : AssetPostprocessor
{
    private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets,
        string[] movedAssets, string[] movedFromAssetPaths, bool didDomainReload)
    {
        // Queue only. Do not mutate or import assets from the import callback.
        GrassMicroVerseBridgeWatcher.QueueRefresh();
    }
}
