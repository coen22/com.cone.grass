using UnityEditor;
using UnityEngine;

public sealed class TerrainGrassAlbedoBakerWindow : EditorWindow
{
    [SerializeField] private Terrain terrain;
    [SerializeField] private GrassPlacementArea area;
    [SerializeField] private int resolution = 1024;
    [SerializeField] private string assetPath = "Assets/TerrainGrassAlbedo.asset";
    private string status;
    private bool failed;

    [MenuItem("Tools/Cone/Grass/Bake Terrain Ground Albedo")]
    private static void Open()
    {
        TerrainGrassAlbedoBakerWindow window = GetWindow<TerrainGrassAlbedoBakerWindow>("Grass Ground Albedo");
        GameObject selected = Selection.activeGameObject;
        if (!selected)
            return;
        window.area = selected.GetComponent<GrassPlacementArea>();
        window.terrain = window.area ? window.area.Terrain : selected.GetComponent<Terrain>();
    }

    [MenuItem("CONTEXT/GrassPlacementArea/Bake Terrain Ground Albedo")]
    private static void OpenForArea(MenuCommand command)
    {
        TerrainGrassAlbedoBakerWindow window = GetWindow<TerrainGrassAlbedoBakerWindow>("Grass Ground Albedo");
        window.area = command.context as GrassPlacementArea;
        window.terrain = window.area ? window.area.Terrain : null;
    }

    private void OnGUI()
    {
        EditorGUILayout.HelpBox("Bake the native URP Terrain/Lit layer blend into a ground-color map. " +
            "The map contains unlit albedo, including texture alpha density and remapped mask-height blending. " +
            "Source textures can remain non-readable.", MessageType.Info);
        terrain = (Terrain)EditorGUILayout.ObjectField("Terrain", terrain, typeof(Terrain), true);
        area = (GrassPlacementArea)EditorGUILayout.ObjectField("Assign to area", area, typeof(GrassPlacementArea), true);
        resolution = Mathf.Clamp(EditorGUILayout.IntField("Resolution", resolution),
            TerrainGrassAlbedoBaker.MinimumResolution, TerrainGrassAlbedoBaker.MaximumResolution);
        EditorGUILayout.BeginHorizontal();
        assetPath = EditorGUILayout.TextField("Texture asset", assetPath);
        if (GUILayout.Button("Choose…", GUILayout.Width(80)))
        {
            string selected = EditorUtility.SaveFilePanelInProject("Save terrain grass albedo", "TerrainGrassAlbedo",
                "asset", "Choose the generated Texture2D asset. Existing Texture2D assets are updated in place.");
            if (!string.IsNullOrEmpty(selected))
                assetPath = selected;
        }
        EditorGUILayout.EndHorizontal();

        bool wrongArea = area && area.Terrain != terrain;
        if (wrongArea)
            EditorGUILayout.HelpBox("The Placement Area must reference the same Terrain before this map can be assigned.", MessageType.Warning);
        EditorGUILayout.HelpBox("Only the native URP Terrain/Lit shader is supported. " +
            "Lighting, decals, terrain normal maps and custom shader effects are outside this albedo capture. " +
            "Rebake after changing terrain textures or layer weights.", MessageType.None);
        using (new EditorGUI.DisabledScope(!terrain || wrongArea))
        {
            if (GUILayout.Button("Bake ground albedo"))
            {
                failed = !TerrainGrassAlbedoBaker.TryBake(terrain, resolution, assetPath, out Texture2D texture, out status);
                if (!failed)
                {
                    if (area)
                    {
                        Undo.RecordObject(area, "Assign terrain grass ground albedo");
                        area.SetGroundColorTexture(texture, true);
                        PrefabUtility.RecordPrefabInstancePropertyModifications(area);
                        EditorUtility.SetDirty(area);
                    }
                    status = "Saved " + assetPath + (area ? " and assigned terrain-aligned ground color." : ".");
                    EditorGUIUtility.PingObject(texture);
                    EditorApplication.QueuePlayerLoopUpdate();
                    SceneView.RepaintAll();
                }
            }
        }
        if (!string.IsNullOrEmpty(status))
            EditorGUILayout.HelpBox(status, failed ? MessageType.Error : MessageType.Info);
    }
}
