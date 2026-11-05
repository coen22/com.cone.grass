using UnityEditor;
using UnityEngine;

namespace Cone.Grass.Editor
{
    /// <summary>Inspector for <see cref="GrassMeshDetails"/>: the settings, plus convert, restore and refresh buttons.</summary>
    [CustomEditor(typeof(GrassMeshDetails))]
    internal sealed class GrassMeshDetailsEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var details = (GrassMeshDetails)target;
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Convert Active Terrains", "Add every active terrain's detail mesh prototypes as kinds.")))
                {
                    GrassMeshDetailConverter.Convert(Terrain.activeTerrains, details);
                    GrassMeshDetailConverter.InstallFeature();
                }

                if (GUILayout.Button(new GUIContent("Restore Unity Details", "Put back each terrain's Detail Distance, so Unity draws its details again.")))
                {
                    GrassMeshDetailConverter.RestoreUnityDetails(details);
                }

                if (GUILayout.Button(new GUIContent("Refresh", "Re-read the terrains' detail layers.")))
                {
                    details.Refresh();
                }
            }

            Camera camera = SceneView.lastActiveSceneView ? SceneView.lastActiveSceneView.camera : null;
            if (camera && details.TryGetCounts(camera, out int[] drawn, out int[] shadows))
            {
                long total = 0, shadowTotal = 0;
                for (int i = 0; i < drawn.Length; i++)
                {
                    total += drawn[i];
                    shadowTotal += shadows[i];
                }

                EditorGUILayout.HelpBox($"Scene view: {total:N0} instance draws, {shadowTotal:N0} shadow-cascade draws.", MessageType.None);
            }
        }
    }
}
