using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine.Rendering.Universal;

namespace Cone.Grass.Editor
{
    /// <summary>
    /// Before a player build, fills in the package's compute shader and height material on every Grass Data Renderer
    /// Feature in the project that has none. URP only initializes the features of the active pipeline in the Editor, so
    /// a renderer for another quality level could otherwise build without them and draw no grass.
    /// </summary>
    internal sealed class GrassRendererFeatureBuildDefaults : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            foreach (string guid in AssetDatabase.FindAssets("t:" + nameof(UniversalRendererData)))
            {
                var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(AssetDatabase.GUIDToAssetPath(guid));
                GrassDataRendererFeature feature = GrassRendererSetup.FindFeature(renderer);
                if (!feature)
                {
                    continue;
                }

                feature.AssignPackageDefaults();
                AssetDatabase.SaveAssetIfDirty(feature);
            }
        }
    }
}
