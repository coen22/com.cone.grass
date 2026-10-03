using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(GrassDensityAsset))]
public sealed class GrassDensityAssetEditor : Editor
{
    private int mapWidth;
    private int mapHeight;

    private void OnEnable()
    {
        GrassDensityAsset asset = (GrassDensityAsset)target;
        mapWidth = asset.Width;
        mapHeight = asset.Height;
        Undo.undoRedoPerformed += OnUndoRedo;
    }

    private void OnDisable() => Undo.undoRedoPerformed -= OnUndoRedo;

    public override void OnInspectorGUI()
    {
        GrassDensityAsset asset = (GrassDensityAsset)target;
        EditorGUILayout.HelpBox("Linear positive density: black removes grass, full red-channel coverage allows the configured density. Use a Placement Area's Scene Brush to paint this asset.", MessageType.Info);
        mapWidth = Mathf.Clamp(EditorGUILayout.IntField("Width", mapWidth), GrassDensityAsset.MinimumResolution, GrassDensityAsset.MaximumResolution);
        mapHeight = Mathf.Clamp(EditorGUILayout.IntField("Height", mapHeight), GrassDensityAsset.MinimumResolution, GrassDensityAsset.MaximumResolution);
        using (new EditorGUI.DisabledScope(mapWidth == asset.Width && mapHeight == asset.Height))
        {
            if (GUILayout.Button("Resize and preserve coverage"))
            {
                Undo.RegisterCompleteObjectUndo(asset, "Resize grass density map");
                asset.Resize(mapWidth, mapHeight);
                SaveChange(asset);
            }
        }

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Clear"))
        {
            Undo.RegisterCompleteObjectUndo(asset, "Clear grass density map");
            asset.Fill(0f);
            SaveChange(asset);
        }
        if (GUILayout.Button("Fill"))
        {
            Undo.RegisterCompleteObjectUndo(asset, "Fill grass density map");
            asset.Fill(1f);
            SaveChange(asset);
        }
        EditorGUILayout.EndHorizontal();

        Rect preview = GUILayoutUtility.GetRect(64f, 300f, GUILayout.ExpandWidth(true));
        EditorGUI.DrawPreviewTexture(preview, asset.Texture, null, ScaleMode.ScaleToFit);
        EditorGUILayout.LabelField(asset.HasCoverage ? "Contains authored grass coverage" : "Empty — no grass", EditorStyles.centeredGreyMiniLabel);
    }

    private void OnUndoRedo()
    {
        if (!target)
            return;
        GrassDensityAsset asset = (GrassDensityAsset)target;
        asset.NotifyChanged();
        mapWidth = asset.Width;
        mapHeight = asset.Height;
        Repaint();
        EditorApplication.QueuePlayerLoopUpdate();
        SceneView.RepaintAll();
    }

    private static void SaveChange(GrassDensityAsset asset)
    {
        EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssetIfDirty(asset);
        EditorApplication.QueuePlayerLoopUpdate();
        SceneView.RepaintAll();
    }
}
