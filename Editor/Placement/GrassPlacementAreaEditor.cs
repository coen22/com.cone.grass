using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(GrassPlacementArea))]
public sealed class GrassPlacementAreaEditor : Editor
{
    private bool paintEnabled;
    private bool erase;
    private float brushRadius = 3f;
    private float brushStrength = 0.25f;
    private float brushHardness = 0.5f;
    private bool hasLastDab;
    private Vector3 lastDab;
    private int strokeUndoGroup = -1;
    private int brushControl;
    private GrassDensityAsset strokeAsset;

    private GrassPlacementArea Area => (GrassPlacementArea)target;

    private void OnEnable() => Undo.undoRedoPerformed += OnUndoRedo;

    private void OnDisable()
    {
        FinishStroke();
        Undo.undoRedoPerformed -= OnUndoRedo;
    }

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        GrassPlacementArea area = Area;

        if (area.Shape == GrassPlacementShape.Texture && !area.DensityAsset && !area.DensityTexture)
            EditorGUILayout.HelpBox("This area is empty until a density map is assigned or painted.", MessageType.Info);
        if (area.UsesTerrainBounds && !area.Terrain)
            EditorGUILayout.HelpBox("Assign a Terrain to use its bounds, or turn off Use Terrain Bounds for a world-space area.", MessageType.Info);
        if (!area.Terrain && area.PaintSurface && !area.PaintSurface.GetComponent<Renderer>() &&
            !area.PaintSurface.GetComponentInParent<Renderer>())
            EditorGUILayout.HelpBox("The assigned Paint Surface needs a Renderer on the same object or a parent. Grass coverage stays disabled until one is available.", MessageType.Warning);

        if (area.Shape == GrassPlacementShape.Texture && !area.DensityAsset && area.DensityTexture)
        {
            string path = AssetDatabase.GetAssetPath(area.DensityTexture);
            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer && importer.sRGBTexture)
                EditorGUILayout.HelpBox("Density is linear data. Disable sRGB on this density texture to preserve brush strength and soft boundaries.", MessageType.Warning);
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Scene Brush", EditorStyles.boldLabel);
        if (!area.DensityAsset)
        {
            if (GUILayout.Button("Create paintable density map"))
                CreateDensityMap(area);
            EditorGUILayout.HelpBox("A new map starts empty. Painting adds grass only inside this area's bounds.", MessageType.Info);
            paintEnabled = false;
        }
        else
        {
            using (new EditorGUI.DisabledScope(area.Shape != GrassPlacementShape.Texture))
            {
                bool nextPaintEnabled = GUILayout.Toggle(paintEnabled, "Paint density in Scene view", "Button");
                if (paintEnabled != nextPaintEnabled)
                {
                    FinishStroke();
                    paintEnabled = nextPaintEnabled;
                    SceneView.RepaintAll();
                }
                erase = GUILayout.Toolbar(erase ? 1 : 0, new[] { "Add", "Erase" }) == 1;
                brushRadius = Mathf.Clamp(EditorGUILayout.FloatField("Radius (metres)", brushRadius), 0.01f, 10000f);
                brushStrength = EditorGUILayout.Slider("Strength", brushStrength, 0.01f, 1f);
                brushHardness = EditorGUILayout.Slider("Hardness", brushHardness, 0f, 1f);
            }

            EditorGUILayout.HelpBox("Drag with the left mouse button. Hold Shift to erase and Alt to orbit. Each stroke supports Undo/Redo.", MessageType.None);
            if (!area.Terrain && !area.PaintSurface)
                EditorGUILayout.HelpBox("Without an explicit Terrain or Paint Surface collider, the brush projects onto the area's horizontal plane.", MessageType.Info);
            if (area.Terrain && !area.Terrain.GetComponent<TerrainCollider>())
                EditorGUILayout.HelpBox("Add a TerrainCollider to the assigned Terrain so the Scene Brush can follow its surface.", MessageType.Info);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Select density asset"))
                Selection.activeObject = area.DensityAsset;
            if (GUILayout.Button("Clear coverage"))
            {
                FinishStroke();
                Undo.RegisterCompleteObjectUndo(area.DensityAsset, "Clear grass coverage");
                area.DensityAsset.Fill(0f);
                EditorUtility.SetDirty(area.DensityAsset);
                AssetDatabase.SaveAssetIfDirty(area.DensityAsset);
                RefreshViews();
            }
            EditorGUILayout.EndHorizontal();

            Rect preview = GUILayoutUtility.GetRect(32f, 180f, GUILayout.ExpandWidth(true));
            EditorGUI.DrawPreviewTexture(preview, area.DensityAsset.Texture, null, ScaleMode.ScaleToFit);
            EditorGUILayout.LabelField(area.DensityAsset.Width + " × " + area.DensityAsset.Height + " linear density — black is empty", EditorStyles.centeredGreyMiniLabel);
        }
    }

    private void OnSceneGUI()
    {
        GrassPlacementArea area = Area;
        if (!paintEnabled || !area || !area.DensityAsset || area.Shape != GrassPlacementShape.Texture)
        {
            FinishStroke();
            return;
        }

        Event current = Event.current;
        brushControl = GUIUtility.GetControlID("ConeGrassDensityBrush".GetHashCode(), FocusType.Passive);
        if (current.type == EventType.Layout && !current.alt)
            HandleUtility.AddDefaultControl(brushControl);

        if (current.type == EventType.MouseUp && current.button == 0 && GUIUtility.hotControl == brushControl)
        {
            FinishStroke();
            current.Use();
            return;
        }

        if (current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape)
        {
            FinishStroke();
            paintEnabled = false;
            current.Use();
            Repaint();
            return;
        }

        if (current.alt)
        {
            FinishStroke();
            return;
        }

        if (!TryGetBrushPoint(area, HandleUtility.GUIPointToWorldRay(current.mousePosition),
            out Vector3 point, out Vector3 normal))
            return;

        bool eraseThisDab = erase || current.shift;
        if (current.type == EventType.Repaint)
        {
            Color color = eraseThisDab ? new Color(1f, 0.3f, 0.2f, 0.9f) : new Color(0.3f, 0.9f, 0.35f, 0.9f);
            Handles.color = color;
            Handles.DrawWireDisc(point + normal * 0.025f, normal, brushRadius);
            Handles.color = new Color(color.r, color.g, color.b, 0.5f);
            Handles.DrawWireDisc(point + normal * 0.025f, normal, brushRadius * brushHardness);
        }

        if (current.button != 0)
            return;

        if (current.type == EventType.MouseDown)
        {
            GUIUtility.hotControl = brushControl;
            Undo.IncrementCurrentGroup();
            strokeUndoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(eraseThisDab ? "Erase grass density" : "Paint grass density");
            strokeAsset = area.DensityAsset;
            Undo.RegisterCompleteObjectUndo(strokeAsset, "Paint grass density");
            hasLastDab = false;
            PaintAlongStroke(area, point, eraseThisDab);
            current.Use();
        }
        else if (current.type == EventType.MouseDrag && GUIUtility.hotControl == brushControl)
        {
            PaintAlongStroke(area, point, eraseThisDab);
            current.Use();
        }

        if (current.type == EventType.MouseMove)
            SceneView.RepaintAll();
    }

    private void PaintAlongStroke(GrassPlacementArea area, Vector3 point, bool eraseThisDab)
    {
        if (strokeAsset != area.DensityAsset)
        {
            FinishStroke();
            return;
        }

        float spacing = Mathf.Max(0.01f, brushRadius * 0.15f);
        bool changed = false;
        if (!hasLastDab)
        {
            changed = area.Paint(point, brushRadius, brushStrength, brushHardness, eraseThisDab);
            lastDab = point;
            hasLastDab = true;
        }
        else
        {
            Vector2 offset = new Vector2(point.x - lastDab.x, point.z - lastDab.z);
            float distance = offset.magnitude;
            if (distance >= spacing)
            {
                int dabs = Mathf.Min(Mathf.FloorToInt(distance / spacing), 512);
                Vector3 start = lastDab;
                float step = distance / spacing > 512f ? distance / dabs : spacing;
                for (int i = 1; i <= dabs; i++)
                {
                    Vector3 dab = Vector3.Lerp(start, point, Mathf.Min(i * step / distance, 1f));
                    changed |= area.Paint(dab, brushRadius, brushStrength, brushHardness, eraseThisDab);
                    lastDab = dab;
                }
            }
        }

        if (changed)
        {
            EditorUtility.SetDirty(strokeAsset);
            RefreshViews();
        }
    }

    private void FinishStroke()
    {
        if (strokeAsset)
        {
            if (strokeUndoGroup >= 0)
                Undo.CollapseUndoOperations(strokeUndoGroup);
            AssetDatabase.SaveAssetIfDirty(strokeAsset);
        }
        strokeAsset = null;
        strokeUndoGroup = -1;
        hasLastDab = false;
        if (brushControl != 0 && GUIUtility.hotControl == brushControl)
            GUIUtility.hotControl = 0;
    }

    private bool TryGetBrushPoint(GrassPlacementArea area, Ray ray, out Vector3 point, out Vector3 normal)
    {
        Collider collider = area.PaintSurface;
        if (!ReferenceEquals(area.Terrain, null))
            collider = area.Terrain ? area.Terrain.GetComponent<TerrainCollider>() : null;

        if (collider)
        {
            if (collider.Raycast(ray, out RaycastHit hit, float.PositiveInfinity))
            {
                point = hit.point;
                normal = hit.normal;
                return true;
            }
        }
        else if (ReferenceEquals(area.Terrain, null) && ReferenceEquals(area.PaintSurface, null))
        {
            // Only an intentionally unbound area uses a horizontal brush plane.
            Plane plane = new Plane(Vector3.up, area.transform.position);
            if (plane.Raycast(ray, out float enter))
            {
                point = ray.GetPoint(enter);
                normal = Vector3.up;
                return true;
            }
        }

        // Re-entering a surface starts a new dab. Interpolating from the last hit
        // would paint a stripe through the region the cursor crossed without a hit.
        hasLastDab = false;
        point = normal = Vector3.zero;
        return false;
    }

    private static void CreateDensityMap(GrassPlacementArea area)
    {
        string path = EditorUtility.SaveFilePanelInProject("Create grass density map", "GrassDensity", "asset",
            "Choose where the paintable density map will be saved.");
        if (string.IsNullOrEmpty(path))
            return;
        GrassDensityAsset asset = CreateInstance<GrassDensityAsset>();
        asset.Fill(0f);
        AssetDatabase.CreateAsset(asset, path);
        Undo.RegisterCreatedObjectUndo(asset, "Create grass density map");
        Undo.RecordObject(area, "Assign grass density map");
        area.SetDensityAsset(asset, area.UsesTerrainBounds);
        PrefabUtility.RecordPrefabInstancePropertyModifications(area);
        EditorUtility.SetDirty(area);
        AssetDatabase.SaveAssetIfDirty(asset);
        RefreshViews();
    }

    private void OnUndoRedo()
    {
        if (!target)
            return;
        if (Area.DensityAsset)
            Area.DensityAsset.NotifyChanged();
        // Density Undo queues the affected coverage/ground maps through the asset. Other serialized
        // area edits are classified by its next state comparison, so a color Undo does not rebuild heights.
        RefreshViews();
        Repaint();
    }

    private static void RefreshViews()
    {
        EditorApplication.QueuePlayerLoopUpdate();
        SceneView.RepaintAll();
    }
}
