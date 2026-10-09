using System;
using UnityEngine;
using UnityEngine.Rendering;

public enum GrassPlacementMode
{
    AuthoredAreas = 0,
    LegacySurfaceLayer = 1
}

/// <summary>Scene settings and shared blade meshes. The renderer feature owns camera GPU resources.</summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public class InfiniteGrassRenderer : MonoBehaviour
{
    public static InfiniteGrassRenderer Instance { get; private set; }

    [Header("Rendering")]
    public Material grassMaterial;
    [Tooltip("Authored Areas requires explicit Grass Placement Area components. Legacy mode uses the feature's height layer.")]
    public GrassPlacementMode placementMode = GrassPlacementMode.AuthoredAreas;
    public bool renderInSceneView = true;

    [Header("Population")]
    [Min(0.01f)] public float spacing = 0.1f;
    [Min(1f)] public float drawDistance = 300f;
    [Min(0f)] public float fullDensityDistance = 30f;
    [Min(0.1f)] public float densityFalloffExponent = 4f;
    [Range(0f, 0.25f)] public float densityTransition = 0.05f;

    [Header("Geometry LOD")]
    [Range(0, 8)] public int grassMeshSubdivision = 5;
    [Min(0.01f)] public float nearLodDistance = 30f;
    [Tooltip("Start of the far, single-triangle LOD.")]
    public float subdivisionDistance = 100f;
    [Min(0f)] public float lodTransitionWidth = 10f;
    public float subdivisionHeightBoost;
    [Min(0.01f)] public float subdivisionBumpWidth = 20f;
    [Tooltip("Conservative world-space radius around a root for frustum culling. Increase for tall/wide, strongly bent grass.")]
    [Min(0.1f)] public float cullingPadding = 2f;

    [Header("Data Capture")]
    [Tooltip("Power-of-two capture resolution. Higher values improve small boundaries at greater memory and capture cost.")]
    public int captureResolution = 1024;
    [Min(0.1f)] public float textureUpdateThreshold = 10f;
    [Tooltip("Stable world-space minimum/maximum Y used by the top-down mesh capture.")]
    public Vector2 captureHeightRange = new Vector2(-100f, 1000f);
    public bool cacheSurfaceData = true;
    [Tooltip("Keep enabled for moving mask/color/slope modifiers. Static scenes can disable it and call RefreshGrassData after edits.")]
    public bool updateModifiersEveryFrame = true;

    [Header("GPU Capacity")]
    [Tooltip("Total capacity across all three LOD queues, in millions. Overflow is dropped safely and exposed in diagnostics.")]
    [Range(0.01f, 16f)] public float maxBufferCount = 2f;
    public Vector3 lodCapacityWeights = new Vector3(0.35f, 0.45f, 0.2f);

    [Header("Ground Blending")]
    [Range(0f, 1f)] public float groundBlendStrength = 0.8f;
    [Tooltip("Fraction of the blade height that blends toward the authored ground albedo.")]
    [Range(0.001f, 1f)] public float groundBlendHeight = 0.25f;

    [Header("Aliasing")]
    [Tooltip("Uses alpha-to-coverage only when the camera's actual color target is multisampled.")]
    public bool alphaToCoverage = true;
    [Range(0f, 3f)] public float minimumPixelWidth = 1f;
    public Vector2 specularFadeRange = new Vector2(30f, 100f);
    public GrassMotionVectors.Settings motionVectors = new GrassMotionVectors.Settings();

    [Header("Optional Contact Shadows")]
    public GrassContactShadows.Settings contactShadows = new GrassContactShadows.Settings();

    [Header("Diagnostics")]
    [Tooltip("Periodically reads counts asynchronously. Counts may lag behind the rendered frame.")]
    public bool previewVisibleGrassCount;

    [NonSerialized] public Bounds cameraBounds;
    public uint VisibleGrassCount { get; internal set; }
    public uint OverflowGrassCount { get; internal set; }
    public uint Revision { get; private set; }
    public uint InteractionRevision { get; private set; }
    public int Capacity => Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp(Finite(maxBufferCount, 2f), 0f, 16f) * 1000000f), 3, 16000000);
    public bool IsReadyForRendering => Instance == this && isActiveAndEnabled && IsSceneInstance();

    private Mesh[] meshes;
    private int cachedSubdivision = -1;
    private GUIStyle diagnosticStyle;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Instance = null;
    }

    private void OnEnable()
    {
        // Scene objects receive OnEnable before an additive scene reports
        // isLoaded. Register now; rendering starts after scene activation.
        if (!IsSceneInstance(false))
            return;
        // Moving the current owner into a preview scene need not invoke
        // OnDisable. A replacement can enable before that owner's next Update.
        if (Instance && Instance != this && !Instance.IsSceneInstance(false))
        {
            Instance.ReleaseMeshes();
            Instance = null;
        }
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("Only one enabled Infinite Grass Renderer can own the scene settings.", this);
            enabled = false;
            return;
        }

        Instance = this;
        ValidateSettings();
        RefreshGrassData();
    }

    private void OnValidate()
    {
        ValidateSettings();
        RefreshGrassData();
    }

    private void OnDisable()
    {
        if (Instance == this)
            Instance = null;
        ReleaseMeshes();
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
        ReleaseMeshes();
    }

    // Scene moves can change eligibility without an OnDisable callback. Also
    // restore ownership when Enter Play Mode skips scene and domain reload.
    private void Update()
    {
        bool sceneEligible = isActiveAndEnabled && IsSceneInstance();
        if (Instance == this && !sceneEligible)
        {
            Instance = null;
            ReleaseMeshes();
        }
        if (Instance == null && sceneEligible)
        {
            Instance = this;
            ValidateSettings();
            RefreshGrassData();
        }
    }

    private bool IsSceneInstance(bool requireLoaded = true)
    {
        if (!gameObject.scene.IsValid() || (requireLoaded && !gameObject.scene.isLoaded))
            return false;
#if UNITY_EDITOR
        if (UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(gameObject.scene))
            return false;
#endif
        return true;
    }

    /// <summary>
    /// One-sided blade area per square metre of ground at full density, as the blade shader's
    /// GrassCanopyAreaIndex computes it: blades per square metre at this spacing times the mean
    /// blade's triangle of physical base width and height. Ground shaders can darken under the
    /// grass with the same canopy.
    /// </summary>
    public float CanopyAreaIndex()
    {
        if (!grassMaterial || !grassMaterial.HasProperty("_GrassWidth") || !grassMaterial.HasProperty("_GrassHeight"))
            return 0f;
        float width = 0.5f * Mathf.Max(grassMaterial.GetFloat("_GrassWidth"), 0f) *
            (1f - 0.5f * Mathf.Clamp01(grassMaterial.GetFloat("_GrassWidthRandomness")));
        float height = Mathf.Max(grassMaterial.GetFloat("_GrassHeight"), 0f) *
            (1f - 0.5f * Mathf.Clamp01(grassMaterial.GetFloat("_GrassHeightRandomness")));
        float cell = Mathf.Max(spacing, 0.001f);
        return 0.5f * width * height / (cell * cell);
    }

    [ContextMenu("Refresh Grass Data")]
    public void RefreshGrassData()
    {
        unchecked { Revision++; }
    }

    public void MarkDirty()
    {
        RefreshGrassData();
    }

    /// <summary>Refresh only interaction capture, including with a stationary camera.</summary>
    public void RefreshGrassInteraction()
    {
        unchecked { InteractionRevision++; }
    }

    public void GetLodCapacity(int[] capacities, int[] offsets)
    {
        if (capacities == null || capacities.Length < 4 || offsets == null || offsets.Length < 4)
            throw new ArgumentException("LOD capacity and offset arrays must contain at least four entries.");

        // Public fields can change after OnValidate/OnEnable. Float arithmetic
        // can overflow even when every supplied weight is finite. Widen before
        // adding or multiplying, then convert only the bounded final shares.
        double near = Math.Max(0.01f, Finite(lodCapacityWeights.x, 0.35f));
        double middle = Math.Max(0.01f, Finite(lodCapacityWeights.y, 0.45f));
        double far = Math.Max(0.01f, Finite(lodCapacityWeights.z, 0.2f));
        double total = near + middle + far;
        int capacity = Capacity;
        capacities[0] = Mathf.Clamp(Mathf.FloorToInt((float)(capacity * near / total)), 1, capacity - 2);
        capacities[1] = Mathf.Clamp(Mathf.FloorToInt((float)(capacity * middle / total)), 1, capacity - capacities[0] - 1);
        capacities[2] = capacity - capacities[0] - capacities[1];
        capacities[3] = 0;
        offsets[0] = 0;
        offsets[1] = capacities[0];
        offsets[2] = capacities[0] + capacities[1];
        offsets[3] = 0;
    }

    /// <summary>Compatibility accessor for the highest detail mesh.</summary>
    public Mesh GetGrassMeshCache()
    {
        return GetLodMeshes()[0];
    }

    public Mesh[] GetLodMeshes()
    {
        int subdivisions = Mathf.Clamp(grassMeshSubdivision, 0, 8);
        if (meshes != null && cachedSubdivision == subdivisions && meshes[0] && meshes[1] && meshes[2])
            return meshes;

        ReleaseMeshes();
        meshes = new[]
        {
            CreateBladeMesh(subdivisions),
            CreateBladeMesh(Mathf.Min(subdivisions, 2)),
            CreateBladeMesh(0)
        };
        cachedSubdivision = subdivisions;
        return meshes;
    }

    /// <summary>Shares row vertices, including a two-vertex tip for opaque footprint caps.
    /// The shader collapses that tip for pointed/A2C blades; UV.y remains true normalized height.</summary>
    public static Mesh CreateBladeMesh(int subdivisions)
    {
        subdivisions = Mathf.Clamp(subdivisions, 0, 8);
        int rows = subdivisions + 1;
        var vertices = new Vector3[rows * 2 + 2];
        var uv = new Vector2[vertices.Length];
        var indices = new int[(subdivisions + 1) * 6];

        for (int row = 0; row < rows; row++)
        {
            float height = row / (float)rows;
            vertices[row * 2] = new Vector3(-0.25f, height, 0f);
            vertices[row * 2 + 1] = new Vector3(0.25f, height, 0f);
            uv[row * 2] = new Vector2(0f, height);
            uv[row * 2 + 1] = new Vector2(1f, height);
        }

        for (int row = 0; row < subdivisions; row++)
        {
            int lowerLeft = row * 2;
            int upperLeft = lowerLeft + 2;
            int index = row * 6;
            indices[index] = lowerLeft;
            indices[index + 1] = upperLeft + 1;
            indices[index + 2] = lowerLeft + 1;
            indices[index + 3] = lowerLeft;
            indices[index + 4] = upperLeft;
            indices[index + 5] = upperLeft + 1;
        }

        int tip = rows * 2;
        vertices[tip] = new Vector3(-0.25f, 1f, 0f);
        vertices[tip + 1] = new Vector3(0.25f, 1f, 0f);
        uv[tip] = new Vector2(0f, 1f);
        uv[tip + 1] = new Vector2(1f, 1f);
        int last = indices.Length - 6;
        indices[last] = (rows - 1) * 2;
        indices[last + 1] = tip + 1;
        indices[last + 2] = (rows - 1) * 2 + 1;
        indices[last + 3] = (rows - 1) * 2;
        indices[last + 4] = tip;
        indices[last + 5] = tip + 1;

        var mesh = new Mesh
        {
            name = "Grass blade (" + subdivisions + " subdivisions)",
            hideFlags = HideFlags.HideAndDontSave,
            vertices = vertices,
            uv = uv,
            triangles = indices
        };
        mesh.RecalculateBounds();
        mesh.UploadMeshData(false);
        return mesh;
    }

    private void ReleaseMeshes()
    {
        if (meshes != null)
        {
            foreach (Mesh mesh in meshes)
                CoreUtils.Destroy(mesh);
            meshes = null;
        }
        cachedSubdivision = -1;
    }

    private void ValidateSettings()
    {
        spacing = Mathf.Clamp(Finite(spacing, 0.1f), 0.01f, 10f);
        drawDistance = Mathf.Clamp(Finite(drawDistance, 300f), 1f, 10000f);
        fullDensityDistance = Mathf.Clamp(Finite(fullDensityDistance, 30f), 0f, drawDistance - 0.001f);
        densityFalloffExponent = Mathf.Clamp(Finite(densityFalloffExponent, 4f), 0.1f, 16f);
        densityTransition = Mathf.Clamp(Finite(densityTransition, 0.05f), 0f, 0.25f);
        grassMeshSubdivision = Mathf.Clamp(grassMeshSubdivision, 0, 8);
        nearLodDistance = Mathf.Clamp(Finite(nearLodDistance, 30f), 0.01f, drawDistance);
        subdivisionDistance = Mathf.Clamp(Finite(subdivisionDistance, 100f), nearLodDistance, drawDistance);
        lodTransitionWidth = Mathf.Clamp(Finite(lodTransitionWidth, 10f), 0f, subdivisionDistance);
        subdivisionHeightBoost = Mathf.Clamp(Finite(subdivisionHeightBoost, 0f), -0.95f, 10f);
        subdivisionBumpWidth = Mathf.Max(0.01f, Finite(subdivisionBumpWidth, 20f));
        cullingPadding = Mathf.Max(0.1f, Finite(cullingPadding, 2f));
        textureUpdateThreshold = Mathf.Clamp(Finite(textureUpdateThreshold, 10f), 0.1f, drawDistance);
        captureResolution = Mathf.ClosestPowerOfTwo(Mathf.Clamp(captureResolution, 128, 2048));
        captureHeightRange.x = Finite(captureHeightRange.x, -100f);
        captureHeightRange.y = Mathf.Max(captureHeightRange.x + 1f, Finite(captureHeightRange.y, 1000f));
        maxBufferCount = Mathf.Clamp(Finite(maxBufferCount, 2f), 0.01f, 16f);
        lodCapacityWeights.x = Mathf.Clamp(Finite(lodCapacityWeights.x, 0.35f), 0.01f, 100f);
        lodCapacityWeights.y = Mathf.Clamp(Finite(lodCapacityWeights.y, 0.45f), 0.01f, 100f);
        lodCapacityWeights.z = Mathf.Clamp(Finite(lodCapacityWeights.z, 0.2f), 0.01f, 100f);
        groundBlendStrength = Mathf.Clamp01(Finite(groundBlendStrength, 0.8f));
        groundBlendHeight = Mathf.Clamp(Finite(groundBlendHeight, 0.25f), 0.001f, 1f);
        minimumPixelWidth = Mathf.Clamp(Finite(minimumPixelWidth, 1f), 0f, 3f);
        specularFadeRange.x = Mathf.Max(0f, Finite(specularFadeRange.x, 30f));
        specularFadeRange.y = Mathf.Max(specularFadeRange.x + 0.01f, Finite(specularFadeRange.y, 100f));
        if (motionVectors == null)
            motionVectors = new GrassMotionVectors.Settings();
        if (contactShadows == null)
            contactShadows = new GrassContactShadows.Settings();
    }

    private static float Finite(float value, float fallback)
    {
        return float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;
    }

    private void OnGUI()
    {
        if (!previewVisibleGrassCount || Instance != this)
            return;
        if (diagnosticStyle == null)
            diagnosticStyle = new GUIStyle(GUI.skin.label) { fontSize = 18 };
        GUI.Label(new Rect(24f, 24f, 600f, 60f),
            "Grass: " + VisibleGrassCount.ToString("N0") + " drawn; " +
            OverflowGrassCount.ToString("N0") + " over capacity (async)", diagnosticStyle);
    }
}
