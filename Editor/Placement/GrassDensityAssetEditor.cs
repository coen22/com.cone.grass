using UnityEditor;
using UnityEngine;

namespace Cone.Grass.Editor
{
    [CustomEditor(typeof(GrassDensityAsset))]
    internal sealed class GrassDensityAssetEditor : UnityEditor.Editor
    {
        private int _mapWidth;
        private int _mapHeight;

        private void OnEnable()
        {
            GrassDensityAsset asset = (GrassDensityAsset)target;
            _mapWidth = asset.Width;
            _mapHeight = asset.Height;
            Undo.undoRedoPerformed += OnUndoRedo;
        }

        private void OnDisable() => Undo.undoRedoPerformed -= OnUndoRedo;

        public override void OnInspectorGUI()
        {
            GrassDensityAsset asset = (GrassDensityAsset)target;
            EditorGUILayout.HelpBox("Linear positive density: black removes grass, full red-channel coverage allows the configured density. Use a Placement Area's Scene Brush to paint this asset.", MessageType.Info);
            _mapWidth = Mathf.Clamp(EditorGUILayout.IntField("Width", _mapWidth), GrassDensityAsset.MinimumResolution, GrassDensityAsset.MaximumResolution);
            _mapHeight = Mathf.Clamp(EditorGUILayout.IntField("Height", _mapHeight), GrassDensityAsset.MinimumResolution, GrassDensityAsset.MaximumResolution);
            using (new EditorGUI.DisabledScope(_mapWidth == asset.Width && _mapHeight == asset.Height))
            {
                if (GUILayout.Button("Resize and preserve coverage"))
                {
                    Undo.RegisterCompleteObjectUndo(asset, "Resize grass density map");
                    asset.Resize(_mapWidth, _mapHeight);
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
            {
                return;
            }

            GrassDensityAsset asset = (GrassDensityAsset)target;
            asset.NotifyChanged();
            _mapWidth = asset.Width;
            _mapHeight = asset.Height;
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
}
