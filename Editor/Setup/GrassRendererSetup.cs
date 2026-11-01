using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Cone.Grass.Editor
{
    /// <summary>
    /// Finds the URP renderer that draws the project's cameras and adds the <see cref="GrassDataRendererFeature"/> to
    /// it. Grass draws only through that feature, so a scene with an <see cref="InfiniteGrassRenderer"/> shows nothing
    /// until it is installed.
    /// </summary>
    public static class GrassRendererSetup
    {
        private const string MenuPath = "Tools/Cone/Grass/Add Renderer Feature to Active Renderer";

        /// <summary>The default renderer of the active URP asset, or null when URP is not the active pipeline.</summary>
        public static UniversalRendererData ActiveRendererData()
        {
            if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset pipeline))
            {
                return null;
            }

            using var serialized = new SerializedObject(pipeline);
            SerializedProperty list = serialized.FindProperty("m_RendererDataList");
            int index = serialized.FindProperty("m_DefaultRendererIndex").intValue;
            if (list == null || index < 0 || index >= list.arraySize)
            {
                return null;
            }

            return list.GetArrayElementAtIndex(index).objectReferenceValue as UniversalRendererData;
        }

        /// <summary>The grass feature on <paramref name="renderer"/>, or null when it has none.</summary>
        public static GrassDataRendererFeature FindFeature(ScriptableRendererData renderer)
        {
            if (!renderer)
            {
                return null;
            }

            foreach (ScriptableRendererFeature feature in renderer.rendererFeatures)
            {
                if (feature is GrassDataRendererFeature grass)
                {
                    return grass;
                }
            }

            return null;
        }

        /// <summary>True when the active renderer has an enabled grass feature.</summary>
        public static bool IsInstalled()
        {
            GrassDataRendererFeature feature = FindFeature(ActiveRendererData());
            return feature && feature.isActive;
        }

        /// <summary>
        /// Adds the grass feature to the active renderer, or enables the one it already has, and saves the renderer
        /// asset. Returns null when URP is not the active pipeline.
        /// </summary>
        public static GrassDataRendererFeature Install()
        {
            UniversalRendererData renderer = ActiveRendererData();
            if (!renderer)
            {
                return null;
            }

            GrassDataRendererFeature feature = FindFeature(renderer);
            if (feature)
            {
                if (!feature.isActive)
                {
                    Undo.RecordObject(feature, "Enable Grass Data Renderer Feature");
                    feature.SetActive(true);
                    EditorUtility.SetDirty(renderer);
                }
            }
            else
            {
                feature = ScriptableObject.CreateInstance<GrassDataRendererFeature>();
                feature.name = "Grass Data";
                AssetDatabase.AddObjectToAsset(feature, renderer);
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);
                using (var serialized = new SerializedObject(renderer))
                {
                    SerializedProperty features = serialized.FindProperty("m_RendererFeatures");
                    SerializedProperty map = serialized.FindProperty("m_RendererFeatureMap");
                    features.arraySize++;
                    features.GetArrayElementAtIndex(features.arraySize - 1).objectReferenceValue = feature;
                    map.arraySize++;
                    map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
                    serialized.ApplyModifiedProperties();
                }
                feature.Create();
                EditorUtility.SetDirty(renderer);
            }
            AssetDatabase.SaveAssetIfDirty(renderer);
            return feature;
        }

        [MenuItem(MenuPath)]
        private static void InstallFromMenu()
        {
            GrassDataRendererFeature feature = Install();
            if (feature)
            {
                Debug.Log($"Infinite Grass: the Grass Data Renderer Feature is active on {AssetDatabase.GetAssetPath(ActiveRendererData())}.");
            }
            else
            {
                Debug.LogWarning("Infinite Grass: the active render pipeline is not URP; grass needs a URP renderer.");
            }
        }

        [MenuItem(MenuPath, true)]
        private static bool CanInstallFromMenu() => ActiveRendererData() && !IsInstalled();
    }
}
