using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

/// <summary>
/// Resolves documented saved texture subassets without a dependency on a
/// particular MicroVerse assembly, private field, getter, or event name.
/// </summary>
public static class GrassMicroVerseBridgeUtility
{
    public static Texture2D[] GetTextureSubAssets(GrassMicroVerseBridge bridge)
    {
        if (!bridge || !bridge.MaskTarget)
            return Array.Empty<Texture2D>();

        string path = AssetDatabase.GetAssetPath(bridge.MaskTarget);
        if (string.IsNullOrEmpty(path))
            return Array.Empty<Texture2D>();

        var textures = new List<Texture2D>();
        foreach (UnityEngine.Object asset in AssetDatabase.LoadAllAssetsAtPath(path))
        {
            if (asset is Texture2D texture && AssetDatabase.IsSubAsset(texture))
                textures.Add(texture);
        }

        textures.Sort((left, right) => string.CompareOrdinal(left.name, right.name));
        return textures.ToArray();
    }

    public static bool TryResolve(GrassMicroVerseBridge bridge, out Texture2D texture,
        out string message)
    {
        texture = null;
        if (!bridge || !bridge.Terrain || !bridge.Terrain.terrainData)
        {
            message = "Assign the Terrain that owns this generated mask.";
            return false;
        }

        if (!bridge.MaskTarget || string.IsNullOrEmpty(AssetDatabase.GetAssetPath(bridge.MaskTarget)))
        {
            message = "Assign a saved MicroVerse MaskTarget asset.";
            return false;
        }

        if (string.IsNullOrEmpty(bridge.TextureSubAssetName))
        {
            message = "Choose this terrain's generated density texture. Save the MicroVerse scene first if the list is empty.";
            return false;
        }

        int matches = 0;
        foreach (Texture2D candidate in GetTextureSubAssets(bridge))
        {
            // The name is chosen explicitly in the inspector. Never infer a
            // terrain relationship from name fragments or subasset ordering.
            if (!string.Equals(candidate.name, bridge.TextureSubAssetName, StringComparison.Ordinal))
                continue;

            texture = candidate;
            matches++;
        }

        if (matches != 1)
        {
            message = matches == 0
                ? "The selected texture is missing. Finish MicroVerse generation, save, then select its replacement."
                : "Several textures have this name. Give the terrain and mask unique names, regenerate, then select the correct texture.";
            texture = null;
            return false;
        }

        if (GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat))
        {
            message = "Density is linear data. Set the MaskTarget format to R8 or linear RGBA, regenerate, then refresh.";
            texture = null;
            return false;
        }

        if (bridge.GroundLayer && !HasTerrainLayer(bridge.Terrain, bridge.GroundLayer))
        {
            message = "The selected ground TerrainLayer is absent from this terrain. Apply the matching Texture Stamp and save first.";
            texture = null;
            return false;
        }

        message = $"Bound {texture.name} ({texture.width} x {texture.height}) to {bridge.Terrain.name}.";
        return true;
    }

    /// <summary>
    /// Binds current saved output, or clears coverage if it cannot be resolved.
    /// This does not invoke MicroVerse generation or establish its completion state.
    /// </summary>
    public static bool Refresh(GrassMicroVerseBridge bridge, bool recordUndo = false,
        bool markSceneDirty = true)
    {
        if (!bridge || !bridge.PlacementArea)
            return false;

        bool valid = TryResolve(bridge, out Texture2D density, out string message);
        GrassPlacementArea area = bridge.PlacementArea;
        bool hasGroundColor = valid && (bridge.GroundLayer || bridge.GroundColorOverride);
        float strength = hasGroundColor && !float.IsNaN(bridge.GroundColorStrength) &&
            !float.IsInfinity(bridge.GroundColorStrength) ? Mathf.Clamp01(bridge.GroundColorStrength) : 0f;
        Texture desiredDensity = valid ? density : null;
        TerrainLayer desiredLayer = valid ? bridge.GroundLayer : null;
        Texture desiredColor = valid ? bridge.GroundColorOverride : null;
        // Compare actual public inputs so a steady-state poll only invalidates
        // coverage. It does not dirty the scene or create an Undo entry.
        bool changed = area.Shape != GrassPlacementShape.Texture || !area.UsesTerrainBounds ||
            area.Terrain != bridge.Terrain || area.DensityAsset || area.DensityTexture != desiredDensity ||
            area.GroundLayer != desiredLayer || area.GroundColorTexture != desiredColor ||
            area.GroundTint != bridge.GroundTint || area.GroundColorStrength != strength || area.EdgeFalloff != 0f;
        if (changed)
        {
            if (recordUndo)
                Undo.RecordObject(area, "Refresh MicroVerse grass mask");

            area.ConfigureTexture(bridge.Terrain, desiredDensity, desiredLayer, desiredColor);
            area.ConfigureGroundLayer(desiredLayer, strength);
            area.SetGroundColor(bridge.GroundTint, strength);
        }
        // Also invalidate when the same Texture2D has been updated in place.
        // GPU writes are not required to advance Texture.updateCount.
        area.MarkDirty();

        if (markSceneDirty && changed)
        {
            EditorUtility.SetDirty(area);
            PrefabUtility.RecordPrefabInstancePropertyModifications(area);
            if (area.gameObject.scene.IsValid() && area.gameObject.scene.isLoaded)
                EditorSceneManager.MarkSceneDirty(area.gameObject.scene);
        }

        bridge.SetRefreshResult(valid, message);
        return valid;
    }

    private static bool HasTerrainLayer(Terrain terrain, TerrainLayer layer)
    {
        foreach (TerrainLayer candidate in terrain.terrainData.terrainLayers)
        {
            if (candidate == layer)
                return true;
        }

        return false;
    }
}

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
        DrawTextureSelection(bridge);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Matching terrain color", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("groundLayer"),
            new GUIContent("Grass Terrain Layer", "Use the same TerrainLayer as the MicroVerse Texture Stamp."));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("groundColorOverride"),
            new GUIContent("Ground Color Override", "Optional saved terrain-aligned color map. Takes precedence over the layer's diffuse texture."));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("groundTint"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("groundColorStrength"));
        EditorGUILayout.HelpBox(
            "The placement area samples this layer with its terrain tiling and offset. " +
            "Match both stamps' falloff and filters. Use a baked terrain-aligned ground color override when several ground textures blend at an edge.",
            MessageType.None);

        EditorGUILayout.Space();
        EditorGUILayout.PropertyField(serializedObject.FindProperty("autoRefresh"));
        using (new EditorGUI.DisabledScope(!serializedObject.FindProperty("autoRefresh").boolValue))
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("pollWhileEditing"),
                new GUIContent("Poll Saved Outputs", "Periodically rebinds output textures and invalidates grass while editing. No player cost."));
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
        }

        if (!string.IsNullOrEmpty(bridge.LastRefreshMessage))
            EditorGUILayout.HelpBox(bridge.LastRefreshMessage,
                bridge.LastRefreshSucceeded ? MessageType.Info : MessageType.Warning);

        EditorGUILayout.HelpBox(
            "Automatic refresh reads saved output subassets. Polling catches in-place updates, " +
            "but does not know whether MicroVerse has completed or canceled a generation. " +
            "Finish generation and refresh before saving or building.", MessageType.Info);
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
