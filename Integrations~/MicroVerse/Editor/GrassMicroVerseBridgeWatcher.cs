using System.Collections.Generic;
using Cone.Grass.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Cone.Grass.MicroVerse
{
    /// <summary>
    /// Deferred editor refresh and optional polling for saved producer outputs.
    /// Asset events are deliberately not treated as MicroVerse completion events.
    /// </summary>
    [InitializeOnLoad]
    public static class GrassMicroVerseBridgeWatcher
    {
        private const double DebounceSeconds = 0.35;
        private const double CacheCleanupIntervalSeconds = 1d;
        private static readonly Dictionary<GrassMicroVerseBridge, double> s_nextPoll =
            new Dictionary<GrassMicroVerseBridge, double>();
        private static readonly List<GrassMicroVerseBridge> s_staleEntries =
            new List<GrassMicroVerseBridge>();
        private static readonly List<GrassMicroVerseBridge> s_activeSnapshot =
            new List<GrassMicroVerseBridge>();
        private static double s_refreshAfter = -1;
        private static double s_nextCacheCleanup;

        static GrassMicroVerseBridgeWatcher()
        {
            EditorApplication.update += Update;
            EditorApplication.projectChanged += QueueRefresh;
            EditorApplication.hierarchyChanged += QueueRefresh;
            Undo.undoRedoPerformed += QueueRefresh;
            EditorSceneManager.sceneOpened += SceneOpened;
            EditorSceneManager.sceneSaving += SceneSaving;
            EditorSceneManager.sceneSaved += SceneSaved;
            TerrainGrassAlbedoBaker.SourceChanged += GroundSourceChanged;
            TerrainGrassAlbedoBaker.Baked += GroundBaked;
        }

        public static void QueueRefresh()
        {
            s_refreshAfter = EditorApplication.timeSinceStartup + DebounceSeconds;
        }

        private static void SceneOpened(Scene scene, OpenSceneMode mode) => QueueRefresh();
        private static void SceneSaved(Scene scene) => QueueRefresh();
        private static void GroundSourceChanged(Terrain terrain) => QueueRefresh();
        private static void GroundBaked(Terrain terrain, Texture2D texture) => QueueRefresh();

        private static void SceneSaving(Scene scene, string path)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || BuildPipeline.isBuildingPlayer ||
                EditorSceneManager.IsPreviewScene(scene))
            {
                return;
            }

            // Resolve before serialization, so saved runtime references agree with
            // cached and clean builds. Never invoke MicroVerse generation here.
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (!root)
                {
                    continue;
                }

                foreach (GrassMicroVerseBridge bridge in root.GetComponentsInChildren<GrassMicroVerseBridge>(true))
                {
                    // An earlier Baked callback may delete a later root/component
                    // from these snapshots, or the bridge currently refreshing.
                    if (!bridge || !bridge.enabled)
                    {
                        continue;
                    }

                    bool resolved = GrassMicroVerseBridgeUtility.Refresh(bridge);
                    if (bridge && !resolved)
                    {
                        Debug.LogWarning("Unresolved grass output while saving: " + bridge.LastRefreshMessage, bridge);
                    }
                }
            }
        }

        private static void Update()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling ||
                EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer)
            {
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            if (now >= s_nextCacheCleanup)
            {
                // Manual refresh with Auto Refresh disabled never enters nextPoll.
                // Clean all observations so closing those scenes does not retain
                // their bridge objects and generated texture metadata indefinitely.
                GrassMicroVerseBridgeUtility.PruneInactiveCaches();
                s_nextCacheCleanup = now + CacheCleanupIntervalSeconds;
            }
            if (s_refreshAfter >= 0 && now < s_refreshAfter)
            {
                return;
            }

            bool refreshAll = s_refreshAfter >= 0;
            s_refreshAfter = -1;
            bool refreshed = false;

            s_activeSnapshot.Clear();
            s_activeSnapshot.AddRange(GrassMicroVerseBridge.ActiveBridges);
            foreach (GrassMicroVerseBridge bridge in s_activeSnapshot)
            {
                if (!bridge || !bridge.AutoRefresh || !bridge.gameObject.scene.isLoaded ||
                    EditorSceneManager.IsPreviewScene(bridge.gameObject.scene))
                {
                    continue;
                }

                bool changed = bridge.ConsumeRefreshRequest();
                bool poll = bridge.PollWhileEditing &&
                    (!s_nextPoll.TryGetValue(bridge, out double due) || now >= due);
                if (!changed && !refreshAll && !poll)
                {
                    continue;
                }

                uint previousRevision = bridge.PlacementArea.SourceRevision;
                string previousMessage = bridge.LastRefreshMessage;
                bool previousSuccess = bridge.LastRefreshSucceeded;
                GrassMicroVerseBridgeUtility.Refresh(bridge, false, true, false);
                if (!bridge || !bridge.PlacementArea)
                {
                    continue;
                }

                s_nextPoll[bridge] = now + bridge.PollInterval;
                refreshed |= previousRevision != bridge.PlacementArea.SourceRevision ||
                    previousMessage != bridge.LastRefreshMessage || previousSuccess != bridge.LastRefreshSucceeded;
            }

            s_staleEntries.Clear();
            foreach (GrassMicroVerseBridge bridge in s_nextPoll.Keys)
            {
                if (!bridge || !bridge.isActiveAndEnabled)
                {
                    s_staleEntries.Add(bridge);
                }
            }

            foreach (GrassMicroVerseBridge bridge in s_staleEntries)
            {
                s_nextPoll.Remove(bridge);
                GrassMicroVerseBridgeUtility.Forget(bridge);
            }

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
            // Invalidate only metadata caches. Do not bake, mutate or import assets
            // from an asset import callback.
            Invalidate(importedAssets);
            Invalidate(deletedAssets);
            Invalidate(movedAssets);
            Invalidate(movedFromAssetPaths);
            GrassMicroVerseBridgeWatcher.QueueRefresh();
        }

        private static void Invalidate(string[] paths)
        {
            foreach (string path in paths)
            {
                GrassMicroVerseBridgeUtility.InvalidateAssetPath(path);
            }
        }
    }

    public sealed class GrassMicroVerseMaskSaveProcessor : AssetModificationProcessor
    {
        private static string[] OnWillSaveAssets(string[] paths)
        {
            // ScriptableObject subasset creation can be saved without an import or
            // projectChanged notification. Re-discover only when this path is used.
            foreach (string path in paths)
            {
                GrassMicroVerseBridgeUtility.InvalidateAssetPath(path);
            }

            GrassMicroVerseBridgeWatcher.QueueRefresh();
            return paths;
        }
    }
}
