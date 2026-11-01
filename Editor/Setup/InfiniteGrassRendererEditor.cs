using UnityEditor;
using UnityEngine;

namespace Cone.Grass.Editor
{
    /// <summary>Inspector for <see cref="InfiniteGrassRenderer"/> that offers to install the renderer feature it needs.</summary>
    [CustomEditor(typeof(InfiniteGrassRenderer))]
    [CanEditMultipleObjects]
    internal sealed class InfiniteGrassRendererEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            if (!GrassRendererSetup.IsUrpActive())
            {
                EditorGUILayout.HelpBox("Infinite Grass needs the Universal Render Pipeline. Assign a URP asset in " +
                    "Project Settings > Graphics.", MessageType.Error);
            }
            else if (!GrassRendererSetup.IsInstalled())
            {
                if (GrassRendererSetup.ActiveRendererData() == null)
                {
                    EditorGUILayout.HelpBox("No renderer of the active URP asset has a Grass Data Renderer Feature, and " +
                        "its default renderer is not a Universal Renderer. Add the feature to the Universal Renderer " +
                        "your cameras use.", MessageType.Warning);
                }
                else
                {
                    EditorGUILayout.HelpBox("No renderer of the active URP asset has a Grass Data Renderer Feature, so " +
                        "no grass is drawn. The button adds it to the default renderer.", MessageType.Warning);
                    if (GUILayout.Button("Add Grass Data Renderer Feature"))
                    {
                        GrassRendererSetup.Install();
                    }
                }
            }

            if (targets.Length == 1 && target is InfiniteGrassRenderer renderer && !renderer.GrassMaterial)
            {
                EditorGUILayout.HelpBox("Assign a grass material, for example the package's Grass Blade material.",
                    MessageType.Warning);
            }

            DrawDefaultInspector();
        }
    }
}
