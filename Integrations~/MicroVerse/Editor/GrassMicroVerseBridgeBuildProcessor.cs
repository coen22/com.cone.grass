using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Resolves editor bindings into ordinary placement inputs in each built scene.
/// It consumes generated assets; it never runs terrain generation during a build.
/// </summary>
public sealed class GrassMicroVerseBridgeBuildProcessor : IProcessSceneWithReport
{
    public int callbackOrder => -1000;

    public void OnProcessScene(Scene scene, BuildReport report)
    {
        if (report == null)
            return;

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (GrassMicroVerseBridge bridge in root.GetComponentsInChildren<GrassMicroVerseBridge>(true))
            {
                if (bridge.enabled && !GrassMicroVerseBridgeUtility.Refresh(bridge, false, false))
                {
                    throw new BuildFailedException(
                        $"Grass mask binding in scene '{scene.path}' on '{bridge.name}' is unresolved: " +
                        bridge.LastRefreshMessage);
                }

                // This is the build's scene copy, not the author's open scene.
                // Only the resolved runtime GrassPlacementArea is needed.
                Object.DestroyImmediate(bridge);
            }
        }
    }
}
