using UnityEditor;
using UnityEngine;
using UnityEngine.Serialization;

namespace Cone.Grass.Editor
{
    /// <summary>The Bake Terrain Ground Albedo window: bakes a terrain's albedo and can assign it to a placement area.</summary>
    internal sealed class TerrainGrassAlbedoBakerWindow : EditorWindow
    {
        [SerializeField, FormerlySerializedAs("terrain")] private Terrain _terrain;
        [SerializeField, FormerlySerializedAs("area")] private GrassPlacementArea _area;
        [SerializeField, FormerlySerializedAs("resolution")] private int _resolution = 1024;
        [SerializeField, FormerlySerializedAs("assetPath")] private string _assetPath = "Assets/TerrainGrassAlbedo.asset";
        private string _status;
        private bool _failed;

        [MenuItem("Tools/Cone/Grass/Bake Terrain Ground Albedo")]
        private static void Open()
        {
            TerrainGrassAlbedoBakerWindow window = GetWindow<TerrainGrassAlbedoBakerWindow>("Grass Ground Albedo");
            GameObject selected = Selection.activeGameObject;
            if (!selected)
            {
                return;
            }

            window._area = selected.GetComponent<GrassPlacementArea>();
            window._terrain = window._area ? window._area.Terrain : selected.GetComponent<Terrain>();
        }

        [MenuItem("CONTEXT/GrassPlacementArea/Bake Terrain Ground Albedo")]
        private static void OpenForArea(MenuCommand command)
        {
            TerrainGrassAlbedoBakerWindow window = GetWindow<TerrainGrassAlbedoBakerWindow>("Grass Ground Albedo");
            window._area = command.context as GrassPlacementArea;
            window._terrain = window._area ? window._area.Terrain : null;
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox("Bake the native URP Terrain/Lit layer blend into a ground-color map. " +
                "The map contains unlit albedo, including texture alpha density and remapped mask-height blending. " +
                "Source textures can remain non-readable.", MessageType.Info);
            _terrain = (Terrain)EditorGUILayout.ObjectField("Terrain", _terrain, typeof(Terrain), true);
            _area = (GrassPlacementArea)EditorGUILayout.ObjectField("Assign to area", _area, typeof(GrassPlacementArea), true);
            _resolution = Mathf.Clamp(EditorGUILayout.IntField("Resolution", _resolution),
                TerrainGrassAlbedoBaker.MinimumResolution, TerrainGrassAlbedoBaker.MaximumResolution);
            EditorGUILayout.BeginHorizontal();
            _assetPath = EditorGUILayout.TextField("Texture asset", _assetPath);
            if (GUILayout.Button("Choose…", GUILayout.Width(80)))
            {
                string selected = EditorUtility.SaveFilePanelInProject("Save terrain grass albedo", "TerrainGrassAlbedo",
                    "asset", "Choose the generated Texture2D asset. Existing Texture2D assets are updated in place.");
                if (!string.IsNullOrEmpty(selected))
                {
                    _assetPath = selected;
                }
            }
            EditorGUILayout.EndHorizontal();

            bool wrongArea = _area && _area.Terrain != _terrain;
            if (wrongArea)
            {
                EditorGUILayout.HelpBox("The Placement Area must reference the same Terrain before this map can be assigned.", MessageType.Warning);
            }

            EditorGUILayout.HelpBox("Only the native URP Terrain/Lit shader is supported. " +
                "Lighting, decals, terrain normal maps and custom shader effects are outside this albedo capture. " +
                "Rebake after changing terrain textures or layer weights.", MessageType.None);
            using (new EditorGUI.DisabledScope(!_terrain || wrongArea))
            {
                if (GUILayout.Button("Bake ground albedo"))
                {
                    _failed = !TerrainGrassAlbedoBaker.TryBake(_terrain, _resolution, _assetPath, out Texture2D texture, out _status);
                    if (!_failed)
                    {
                        if (_area)
                        {
                            Undo.RecordObject(_area, "Assign terrain grass ground albedo");
                            _area.SetGroundColorTexture(texture, true);
                            PrefabUtility.RecordPrefabInstancePropertyModifications(_area);
                            EditorUtility.SetDirty(_area);
                        }
                        _status = "Saved " + _assetPath + (_area ? " and assigned terrain-aligned ground color." : ".");
                        EditorGUIUtility.PingObject(texture);
                        EditorApplication.QueuePlayerLoopUpdate();
                        SceneView.RepaintAll();
                    }
                }
            }
            if (!string.IsNullOrEmpty(_status))
            {
                EditorGUILayout.HelpBox(_status, _failed ? MessageType.Error : MessageType.Info);
            }
        }
    }
}
