using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(GrassMicroVerseBridge))]
public sealed class GrassMicroVerseBridgeEditor : Editor
{
    public override void OnInspectorGUI()
    {
        var bridge = (GrassMicroVerseBridge)target;
        ScriptableObject previousTarget = bridge.MaskTarget;
        Terrain previousTerrain = bridge.Terrain;
        serializedObject.Update();

        EditorGUILayout.HelpBox(
            "Use one Spline Area for a Texture Stamp and a positive Mask Stamp. " +
            "Choose the generated density texture for this terrain explicitly; " +
            "black means no grass and the red channel supplies density.", MessageType.Info);

        EditorGUILayout.PropertyField(serializedObject.FindProperty("maskTarget"),
            new GUIContent("Mask Target"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("terrain"));
        // Apply the asset selector before inspecting that asset's subassets.
        bool changed = serializedObject.ApplyModifiedProperties();
        serializedObject.Update();
        if (bridge.MaskTarget != previousTarget || bridge.Terrain != previousTerrain)
        {
            // A different terrain or producer requires an explicit new mapping.
            serializedObject.FindProperty("textureSubAssetName").stringValue = string.Empty;
        }
        if (bridge.Terrain != previousTerrain)
        {
            serializedObject.FindProperty("groundBakeAssetPath").stringValue = string.Empty;
            ClearBakeRecord();
        }
        DrawTextureSelection(bridge);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Matching terrain color", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("groundLayer"),
            new GUIContent("Grass Terrain Layer", "Use the same TerrainLayer as the MicroVerse Texture Stamp."));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("bakeTerrainGroundColor"),
            new GUIContent("Bake Terrain Ground Albedo", "Bake native URP Terrain/Lit layer blending to a saved terrain-aligned map."));
        if (serializedObject.FindProperty("bakeTerrainGroundColor").boolValue)
            DrawGroundBakeOutput(bridge);
        else
            EditorGUILayout.PropertyField(serializedObject.FindProperty("groundColorOverride"),
                new GUIContent("Ground Color Override", "Optional saved terrain-aligned color map. Takes precedence over the layer's diffuse texture."));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("groundTint"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("groundColorStrength"));
        EditorGUILayout.HelpBox(
            "The placement area samples this layer with its terrain tiling and offset. " +
            "Match both stamps' falloff and filters. Bake Terrain Ground Albedo includes native URP height blending, opacity-as-density and mixed layers at the edge.",
            MessageType.None);

        EditorGUILayout.Space();
        EditorGUILayout.PropertyField(serializedObject.FindProperty("autoRefresh"));
        using (new EditorGUI.DisabledScope(!serializedObject.FindProperty("autoRefresh").boolValue))
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("pollWhileEditing"),
                new GUIContent("Poll Saved Outputs", "Checks cached references and change counters. Unchanged outputs do no grass capture work. No player cost."));
            if (serializedObject.FindProperty("pollWhileEditing").boolValue)
                EditorGUILayout.PropertyField(serializedObject.FindProperty("pollInterval"));
        }

        changed |= serializedObject.ApplyModifiedProperties();
        if (changed)
            GrassMicroVerseBridgeWatcher.QueueRefresh();

        using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling))
        {
            if (GUILayout.Button("Refresh Saved Mask"))
            {
                GrassMicroVerseBridgeUtility.Refresh(bridge, true);
                EditorApplication.QueuePlayerLoopUpdate();
                SceneView.RepaintAll();
            }
            if (bridge.BakeTerrainGroundColor && GUILayout.Button("Bake Terrain Albedo and Refresh"))
            {
                GrassMicroVerseBridgeUtility.Refresh(bridge, true, true, true, true);
                EditorApplication.QueuePlayerLoopUpdate();
                SceneView.RepaintAll();
            }
        }

        if (!string.IsNullOrEmpty(bridge.LastRefreshMessage))
            EditorGUILayout.HelpBox(bridge.LastRefreshMessage,
                bridge.LastRefreshSucceeded ? MessageType.Info : MessageType.Warning);

        EditorGUILayout.HelpBox(
            "Automatic refresh observes saved assets and reported texture changes. " +
            "It cannot identify MicroVerse completion or cancellation without its installed native API. " +
            "For GPU writes that do not report changes, finish generation and use Refresh Saved Mask; " +
            "use Bake Terrain Albedo and Refresh if the ground textures also changed.", MessageType.Info);
    }

    private void DrawGroundBakeOutput(GrassMicroVerseBridge bridge)
    {
        EditorGUILayout.PropertyField(serializedObject.FindProperty("groundBakeResolution"));
        SerializedProperty path = serializedObject.FindProperty("groundBakeAssetPath");
        string previousPath = path.stringValue;
        EditorGUILayout.PropertyField(path, new GUIContent("Ground Albedo Asset Path"));
        if (GUILayout.Button("Choose Ground Albedo Output"))
        {
            string selected = EditorUtility.SaveFilePanelInProject("Grass ground albedo output",
                "GrassGroundAlbedo", "asset", "Choose one output Texture2D asset per Terrain.");
            if (!string.IsNullOrEmpty(selected))
                path.stringValue = selected;
        }
        if (path.stringValue != previousPath)
            ClearBakeRecord();
        using (new EditorGUI.DisabledScope(true))
            EditorGUILayout.ObjectField("Saved Ground Albedo", bridge.BakedGroundColor, typeof(Texture2D), false);
        EditorGUILayout.HelpBox(
            "The bake uses the installed native URP Terrain/Lit blending functions. " +
            "Source textures can remain unreadable. Custom terrain materials need their own ground-color output. " +
            "Automatic baking runs only after source changes; a saved bake is used in the player.",
            MessageType.None);
    }

    private void ClearBakeRecord()
    {
        serializedObject.FindProperty("bakedGroundColor").objectReferenceValue = null;
        serializedObject.FindProperty("groundBakeSourceKey").stringValue = string.Empty;
        serializedObject.FindProperty("groundBakeOutputKey").stringValue = string.Empty;
    }

    private void DrawTextureSelection(GrassMicroVerseBridge bridge)
    {
        Texture2D[] textures = GrassMicroVerseBridgeUtility.GetTextureSubAssets(bridge);
        SerializedProperty selectedName = serializedObject.FindProperty("textureSubAssetName");
        var labels = new string[textures.Length + 1];
        labels[0] = "None (zero coverage)";
        int selected = 0;
        for (int i = 0; i < textures.Length; i++)
        {
            labels[i + 1] = $"{textures[i].name} ({textures[i].width} x {textures[i].height})";
            if (textures[i].name == selectedName.stringValue)
                selected = i + 1;
        }

        int chosen = EditorGUILayout.Popup("Generated Density Texture", selected, labels);
        if (chosen != selected)
            selectedName.stringValue = chosen > 0 ? textures[chosen - 1].name : string.Empty;

        if (selected == 0 && !string.IsNullOrEmpty(selectedName.stringValue))
            EditorGUILayout.HelpBox($"Missing generated texture: {selectedName.stringValue}", MessageType.Warning);

        if (selected > 0)
        {
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.ObjectField("Resolved Subasset", textures[selected - 1], typeof(Texture2D), false);
        }

        if (textures.Length == 0)
            EditorGUILayout.HelpBox("No saved Texture2D subassets are available in this target yet.", MessageType.None);
    }
}
