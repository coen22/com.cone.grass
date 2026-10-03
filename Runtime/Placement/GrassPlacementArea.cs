using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public enum GrassPlacementShape
{
    Box = 0,
    Circle = 1,
    Texture = 2
}

/// <summary>A snapshot consumed by the grass density and ground-color capture passes.</summary>
public struct GrassPlacementDrawData
{
    public Matrix4x4 LocalToWorld;
    public Matrix4x4 WorldToMask;
    public Bounds WorldBounds;
    public Terrain Terrain;
    public Vector4 TerrainRect;
    public Texture DensityTexture;
    public GrassPlacementShape Shape;
    public float Density;
    public float EdgeFalloff;
    public Texture GroundColorTexture;
    public Color GroundTint;
    public float GroundColorStrength;
    public Texture GroundLayerTexture;
    /// <summary>worldXZ * xy + zw gives the terrain layer's repeated diffuse UV.</summary>
    public Vector4 GroundLayerUV;
    public Vector4 GroundLayerRemapMin;
    public Vector4 GroundLayerRemapMax;
}

/// <summary>
/// Explicit grass coverage projected along world Y. An empty texture never enables grass.
/// Texture sources can follow a whole Terrain; small spots can use the object's yaw and size.
/// </summary>
[ExecuteAlways, DisallowMultipleComponent]
[AddComponentMenu("Cone/Grass/Placement Area")]
public sealed class GrassPlacementArea : MonoBehaviour
{
    [Header("Coverage")]
    [SerializeField] private GrassPlacementShape shape = GrassPlacementShape.Circle;
    [SerializeField, Range(0f, 1f)] private float density = 1f;
    [Tooltip("Fraction of the shape radius used to soften the edge. Use zero for a generated mask with its own falloff.")]
    [SerializeField, Range(0f, 1f)] private float edgeFalloff = 0.15f;
    [Tooltip("World-space width and length before the object's X/Z scale. Rotation around Y is supported.")]
    [SerializeField] private Vector2 size = new Vector2(10f, 10f);

    [Header("Surface and mapping")]
    [Tooltip("Explicit supporting terrain. Assigning this does not enable grass outside the area.")]
    [SerializeField] private Terrain terrain;
    [Tooltip("Map the texture over this terrain's complete XZ extent. Object position, scale and rotation are then ignored.")]
    [SerializeField] private bool useTerrainBounds;
    [Tooltip("Optional collider used by the Scene brush when the surface is not a Terrain.")]
    [SerializeField] private Collider paintSurface;

    [Header("Texture coverage")]
    [Tooltip("Optional paintable asset. Takes precedence over Density Texture. Black contains no grass.")]
    [SerializeField] private GrassDensityAsset densityAsset;
    [Tooltip("Linear positive density in red: black is empty, white is full. No CPU readability is required.")]
    [SerializeField] private Texture densityTexture;

    [Header("Ground color matching")]
    [Tooltip("The grass TerrainLayer painted beneath this area. Its texture tiling is preserved.")]
    [SerializeField] private TerrainLayer groundLayer;
    [Tooltip("Optional color map aligned with the density UVs. Overrides the repeated TerrainLayer color.")]
    [SerializeField] private Texture groundColorTexture;
    [SerializeField] private Color groundTint = Color.white;
    [SerializeField, Range(0f, 1f)] private float groundColorStrength;

    private static readonly List<GrassPlacementArea> activeAreas = new List<GrassPlacementArea>();
    private static uint revision = 1;
    private GrassDensityAsset observedAsset;
    private Matrix4x4 previousFrame;
    private Bounds previousBounds;
    private int previousSourceHash;
    private bool hadFrame;
    private bool pendingChanges;

    public static IReadOnlyList<GrassPlacementArea> ActiveAreas => activeAreas;
    public static uint Revision => revision;
    public static event Action<Bounds> CoverageChanged;

    public GrassPlacementShape Shape => shape;
    public Terrain Terrain => terrain;
    public bool UsesTerrainBounds => useTerrainBounds;
    public Collider PaintSurface => paintSurface;
    public GrassDensityAsset DensityAsset => densityAsset;
    public Texture DensityTexture => densityAsset ? densityAsset.Texture : densityTexture;
    public TerrainLayer GroundLayer => groundLayer;
    public Texture GroundColorTexture => groundColorTexture;
    public Color GroundTint => groundTint;
    public float GroundColorStrength => groundColorStrength;
    public float Density => density;
    public float EdgeFalloff => edgeFalloff;
    public Vector2 Size => size;
    public Bounds WorldBounds => TryGetFrame(out _, out _, out Bounds bounds) ? bounds : new Bounds(transform.position, Vector3.zero);
    public Matrix4x4 WorldToMask => TryGetFrame(out _, out Matrix4x4 matrix, out _) ? matrix : Matrix4x4.identity;

    /// <summary>Replace a generated terrain-mask binding, including clearing missing output.</summary>
    public void ConfigureTexture(Terrain supportingTerrain, Texture coverage,
        TerrainLayer matchingGroundLayer = null, Texture matchingGroundColor = null)
    {
        terrain = supportingTerrain;
        useTerrainBounds = true;
        shape = GrassPlacementShape.Texture;
        densityAsset = null;
        densityTexture = coverage;
        groundLayer = matchingGroundLayer;
        groundColorTexture = matchingGroundColor;
        groundColorStrength = matchingGroundLayer || matchingGroundColor ? 1f : 0f;
        edgeFalloff = 0f;
        SynchronizeAssetSubscription();
        MarkDirty();
    }

    public void ConfigureGroundLayer(TerrainLayer layer, float strength = 1f)
    {
        groundLayer = layer;
        groundColorStrength = Mathf.Clamp01(FiniteOr(strength, 0f));
        MarkDirty();
    }

    public void SetGroundColor(Color tint, float strength)
    {
        groundTint = SanitizeColor(tint);
        groundColorStrength = Mathf.Clamp01(FiniteOr(strength, 0f));
        MarkDirty();
    }

    public void SetDensityAsset(GrassDensityAsset asset, bool fitTerrain)
    {
        densityAsset = asset;
        densityTexture = null;
        shape = GrassPlacementShape.Texture;
        useTerrainBounds = fitTerrain;
        edgeFalloff = 0f;
        SynchronizeAssetSubscription();
        MarkDirty();
    }

    public bool TryGetCaptureData(out GrassPlacementDrawData data)
    {
        if (pendingChanges)
        {
            SynchronizeAssetSubscription();
            MarkDirty();
        }
        data = default;
        if (!IsActiveSource() || density <= 0f ||
            !TryGetFrame(out Matrix4x4 localToWorld, out Matrix4x4 worldToMask, out Bounds bounds))
            return false;

        Texture coverage = null;
        if (shape == GrassPlacementShape.Texture)
        {
            if (densityAsset && !densityAsset.HasCoverage)
                return false;
            coverage = DensityTexture;
            if (!coverage || coverage.dimension != TextureDimension.Tex2D)
                return false;
        }

        Vector4 terrainRect = Vector4.zero;
        if (terrain && terrain.terrainData)
        {
            Vector3 position = terrain.transform.position;
            Vector3 terrainSize = terrain.terrainData.size;
            terrainRect = new Vector4(position.x, position.z, terrainSize.x, terrainSize.z);
        }

        data = new GrassPlacementDrawData
        {
            LocalToWorld = localToWorld,
            WorldToMask = worldToMask,
            WorldBounds = bounds,
            Terrain = terrain,
            TerrainRect = terrainRect,
            DensityTexture = coverage,
            Shape = shape,
            Density = density,
            EdgeFalloff = edgeFalloff,
            GroundColorTexture = groundColorTexture && groundColorTexture.dimension == TextureDimension.Tex2D
                ? groundColorTexture : null,
            GroundTint = groundTint,
            GroundColorStrength = groundColorStrength,
            GroundLayerTexture = groundLayer ? groundLayer.diffuseTexture : null,
            GroundLayerUV = GetGroundLayerUV(),
            GroundLayerRemapMin = groundLayer ? groundLayer.diffuseRemapMin : Vector4.zero,
            GroundLayerRemapMax = groundLayer ? groundLayer.diffuseRemapMax : Vector4.one
        };
        return true;
    }

    /// <summary>
    /// Conservative XZ occupancy. External GPU textures use their whole area; painted assets
    /// can omit completely empty regions without synchronous texture readback.
    /// </summary>
    public bool IntersectsCoverage(Bounds worldBounds)
    {
        if (!IsActiveSource() || density <= 0f ||
            !TryGetFrame(out _, out Matrix4x4 worldToMask, out Bounds ownBounds) ||
            ownBounds.max.x < worldBounds.min.x || ownBounds.min.x > worldBounds.max.x ||
            ownBounds.max.z < worldBounds.min.z || ownBounds.min.z > worldBounds.max.z)
            return false;

        if (shape != GrassPlacementShape.Texture)
            return true;
        if (!densityAsset)
            return densityTexture && densityTexture.dimension == TextureDimension.Tex2D;

        Vector3 a = worldToMask.MultiplyPoint3x4(new Vector3(worldBounds.min.x, 0f, worldBounds.min.z));
        Vector3 b = worldToMask.MultiplyPoint3x4(new Vector3(worldBounds.max.x, 0f, worldBounds.min.z));
        Vector3 c = worldToMask.MultiplyPoint3x4(new Vector3(worldBounds.min.x, 0f, worldBounds.max.z));
        Vector3 d = worldToMask.MultiplyPoint3x4(new Vector3(worldBounds.max.x, 0f, worldBounds.max.z));
        float xMin = Mathf.Min(Mathf.Min(a.x, b.x), Mathf.Min(c.x, d.x));
        float xMax = Mathf.Max(Mathf.Max(a.x, b.x), Mathf.Max(c.x, d.x));
        float yMin = Mathf.Min(Mathf.Min(a.z, b.z), Mathf.Min(c.z, d.z));
        float yMax = Mathf.Max(Mathf.Max(a.z, b.z), Mathf.Max(c.z, d.z));
        return densityAsset.HasCoverageIn(Rect.MinMaxRect(xMin, yMin, xMax, yMax));
    }

    public bool Paint(Vector3 worldPosition, float radius, float strength, float hardness, bool erase)
    {
        if (!densityAsset || shape != GrassPlacementShape.Texture || radius <= 0f ||
            !TryGetFrame(out Matrix4x4 localToWorld, out Matrix4x4 worldToMask, out _))
            return false;
        Vector3 uv = worldToMask.MultiplyPoint3x4(worldPosition);
        float worldWidth = localToWorld.MultiplyVector(Vector3.right).magnitude;
        float worldDepth = localToWorld.MultiplyVector(Vector3.forward).magnitude;
        return densityAsset.Paint(new Vector2(uv.x, uv.z),
            new Vector2(radius / worldWidth, radius / worldDepth), strength, hardness, erase);
    }

    /// <summary>Invalidate both old and new extents when a source moves or its GPU output changes.</summary>
    public void MarkDirty()
    {
        pendingChanges = false;
        Bounds dirty = previousBounds;
        if (TryGetFrame(out Matrix4x4 frame, out _, out Bounds bounds))
        {
            if (hadFrame)
                dirty.Encapsulate(bounds);
            else
                dirty = bounds;
            previousBounds = bounds;
            previousFrame = frame;
            hadFrame = true;
        }
        previousSourceHash = GetSourceStateHash();
        unchecked { revision++; }
        CoverageChanged?.Invoke(dirty);
    }

    private void OnEnable()
    {
        if (!IsActiveSource())
            return;
        Register();
        SynchronizeAssetSubscription();
        TerrainCallbacks.heightmapChanged -= OnTerrainHeightChanged;
        TerrainCallbacks.textureChanged -= OnTerrainTextureChanged;
        TerrainCallbacks.heightmapChanged += OnTerrainHeightChanged;
        TerrainCallbacks.textureChanged += OnTerrainTextureChanged;
        MarkDirty();
    }

    private void OnDisable()
    {
        activeAreas.Remove(this);
        if (observedAsset)
            observedAsset.Changed -= QueueDirty;
        observedAsset = null;
        TerrainCallbacks.heightmapChanged -= OnTerrainHeightChanged;
        TerrainCallbacks.textureChanged -= OnTerrainTextureChanged;
        unchecked { revision++; }
        if (hadFrame)
            CoverageChanged?.Invoke(previousBounds);
    }

    private void OnValidate()
    {
        size.x = Mathf.Max(0.01f, FiniteOr(size.x, 10f));
        size.y = Mathf.Max(0.01f, FiniteOr(size.y, 10f));
        density = Mathf.Clamp01(FiniteOr(density, 0f));
        edgeFalloff = Mathf.Clamp01(FiniteOr(edgeFalloff, 0f));
        groundColorStrength = Mathf.Clamp01(FiniteOr(groundColorStrength, 0f));
        groundTint = SanitizeColor(groundTint);
        // OnValidate can run during loading. Defer object and transform access to the main loop.
        QueueDirty();
    }

    private void Update()
    {
        if (!IsActiveSource())
            return;
        if (pendingChanges)
        {
            SynchronizeAssetSubscription();
            MarkDirty();
        }
        // Do not clear transform.hasChanged: other systems can use that flag too.
        if (!TryGetFrame(out Matrix4x4 frame, out _, out Bounds bounds))
        {
            if (hadFrame)
            {
                hadFrame = false;
                unchecked { revision++; }
                CoverageChanged?.Invoke(previousBounds);
            }
            return;
        }

        if (!hadFrame || frame != previousFrame || bounds != previousBounds || previousSourceHash != GetSourceStateHash())
            MarkDirty();
    }

    private void Register()
    {
        if (!activeAreas.Contains(this))
            activeAreas.Add(this);
    }

    private bool IsActiveSource()
    {
        if (!isActiveAndEnabled || !gameObject.scene.IsValid() || !gameObject.scene.isLoaded)
            return false;
#if UNITY_EDITOR
        // Prefab Stage and other preview scenes must not contribute coverage to the main scene.
        if (UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(gameObject.scene))
            return false;
#endif
        return true;
    }

    private void SynchronizeAssetSubscription()
    {
        if (observedAsset == densityAsset)
            return;
        if (observedAsset)
            observedAsset.Changed -= QueueDirty;
        observedAsset = isActiveAndEnabled ? densityAsset : null;
        if (observedAsset)
            observedAsset.Changed += QueueDirty;
    }

    private void QueueDirty()
    {
        pendingChanges = true;
        unchecked { revision++; }
    }

    private bool TryGetFrame(out Matrix4x4 localToWorld, out Matrix4x4 worldToMask, out Bounds bounds)
    {
        localToWorld = worldToMask = Matrix4x4.identity;
        bounds = default;
        Vector3 center = transform.position;
        float yaw = transform.eulerAngles.y;
        if (float.IsNaN(yaw) || float.IsInfinity(yaw))
            return false;
        Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
        Vector3 frameSize = new Vector3(Mathf.Abs(transform.lossyScale.x) * size.x, 1f,
            Mathf.Abs(transform.lossyScale.z) * size.y);

        if (terrain && (!terrain.terrainData || !terrain.isActiveAndEnabled))
            return false;
        if (useTerrainBounds)
        {
            if (!terrain || !terrain.terrainData)
                return false;
            Vector3 terrainSize = terrain.terrainData.size;
            center = terrain.transform.position + new Vector3(terrainSize.x * 0.5f, 0f, terrainSize.z * 0.5f);
            frameSize = new Vector3(terrainSize.x, 1f, terrainSize.z);
            rotation = Quaternion.identity;
        }

        if (!IsFinite(center) || !IsFinite(frameSize) || frameSize.x < 0.001f || frameSize.z < 0.001f)
            return false;

        localToWorld = Matrix4x4.TRS(center, rotation, frameSize);
        worldToMask = Matrix4x4.Translate(new Vector3(0.5f, 0f, 0.5f)) * localToWorld.inverse;
        bounds = new Bounds(center, Vector3.zero);
        bounds.Encapsulate(localToWorld.MultiplyPoint3x4(new Vector3(-0.5f, 0f, -0.5f)));
        bounds.Encapsulate(localToWorld.MultiplyPoint3x4(new Vector3(0.5f, 0f, -0.5f)));
        bounds.Encapsulate(localToWorld.MultiplyPoint3x4(new Vector3(-0.5f, 0f, 0.5f)));
        bounds.Encapsulate(localToWorld.MultiplyPoint3x4(new Vector3(0.5f, 0f, 0.5f)));
        if (terrain && terrain.terrainData)
        {
            Vector3 position = terrain.transform.position;
            Vector3 terrainSize = terrain.terrainData.size;
            Vector3 minimum = bounds.min;
            Vector3 maximum = bounds.max;
            minimum.x = Mathf.Max(minimum.x, position.x);
            minimum.z = Mathf.Max(minimum.z, position.z);
            maximum.x = Mathf.Min(maximum.x, position.x + terrainSize.x);
            maximum.z = Mathf.Min(maximum.z, position.z + terrainSize.z);
            if (minimum.x >= maximum.x || minimum.z >= maximum.z)
                return false;
            minimum.y = position.y;
            maximum.y = position.y + terrainSize.y;
            bounds.SetMinMax(minimum, maximum);
        }
        else if (paintSurface)
        {
            Vector3 minimum = bounds.min;
            Vector3 maximum = bounds.max;
            minimum.y = paintSurface.bounds.min.y;
            maximum.y = paintSurface.bounds.max.y;
            bounds.SetMinMax(minimum, maximum);
        }
        return true;
    }

    private Vector4 GetGroundLayerUV()
    {
        if (!groundLayer)
            return new Vector4(1f, 1f, 0f, 0f);
        Vector2 tileSize = groundLayer.tileSize;
        float scaleX = 1f / Mathf.Max(Mathf.Abs(FiniteOr(tileSize.x, 1f)), 0.001f);
        float scaleY = 1f / Mathf.Max(Mathf.Abs(FiniteOr(tileSize.y, 1f)), 0.001f);
        Vector3 origin = terrain ? terrain.transform.position : Vector3.zero;
        Vector2 offset = groundLayer.tileOffset;
        return new Vector4(scaleX, scaleY, (FiniteOr(offset.x, 0f) - origin.x) * scaleX,
            (FiniteOr(offset.y, 0f) - origin.z) * scaleY);
    }

    private int GetSourceStateHash()
    {
        unchecked
        {
            int hash = densityAsset ? densityAsset.GetInstanceID() : 0;
            hash = hash * 397 ^ (densityAsset ? (int)densityAsset.Revision : 0);
            hash = hash * 397 ^ (densityTexture ? densityTexture.GetInstanceID() : 0);
            hash = hash * 397 ^ (groundColorTexture ? groundColorTexture.GetInstanceID() : 0);
            hash = hash * 397 ^ (terrain && terrain.terrainData ? terrain.terrainData.GetInstanceID() : 0);
            hash = hash * 397 ^ (groundLayer ? groundLayer.GetInstanceID() : 0);
            if (groundLayer)
            {
                hash = hash * 397 ^ groundLayer.tileSize.GetHashCode();
                hash = hash * 397 ^ groundLayer.tileOffset.GetHashCode();
                hash = hash * 397 ^ groundLayer.diffuseRemapMin.GetHashCode();
                hash = hash * 397 ^ groundLayer.diffuseRemapMax.GetHashCode();
                hash = hash * 397 ^ (groundLayer.diffuseTexture ? groundLayer.diffuseTexture.GetInstanceID() : 0);
            }
            return hash;
        }
    }

    private void OnTerrainHeightChanged(Terrain changedTerrain, RectInt region, bool synched)
    {
        if (changedTerrain == terrain)
            MarkDirty();
    }

    private void OnTerrainTextureChanged(Terrain changedTerrain, string textureName, RectInt region, bool synched)
    {
        if (changedTerrain == terrain)
            MarkDirty();
    }

    private void OnDrawGizmosSelected()
    {
        if (!TryGetFrame(out Matrix4x4 localToWorld, out _, out _))
            return;
        Matrix4x4 previousMatrix = Gizmos.matrix;
        Color previousColor = Gizmos.color;
        Gizmos.matrix = localToWorld;
        Gizmos.color = new Color(0.3f, 0.85f, 0.35f, 0.9f);
        if (shape == GrassPlacementShape.Circle)
        {
            const int segments = 64;
            Vector3 previous = new Vector3(0.5f, 0f, 0f);
            for (int i = 1; i <= segments; i++)
            {
                float angle = i * (Mathf.PI * 2f / segments);
                Vector3 point = new Vector3(Mathf.Cos(angle) * 0.5f, 0f, Mathf.Sin(angle) * 0.5f);
                Gizmos.DrawLine(previous, point);
                previous = point;
            }
        }
        else
        {
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(1f, 0f, 1f));
        }
        Gizmos.matrix = previousMatrix;
        Gizmos.color = previousColor;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetRegistry()
    {
        activeAreas.Clear();
        unchecked { revision++; }
        CoverageChanged = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void RestoreRegistry()
    {
        // Also handles Enter Play Mode with both scene reload and domain reload disabled.
        GrassPlacementArea[] areas = FindObjectsByType<GrassPlacementArea>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < areas.Length; i++)
            if (areas[i].IsActiveSource())
                areas[i].OnEnable();
    }

    private static float FiniteOr(float value, float fallback) =>
        float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;

    private static Color SanitizeColor(Color color) => new Color(
        Mathf.Max(0f, FiniteOr(color.r, 1f)), Mathf.Max(0f, FiniteOr(color.g, 1f)),
        Mathf.Max(0f, FiniteOr(color.b, 1f)), Mathf.Clamp01(FiniteOr(color.a, 1f)));

    private static bool IsFinite(Vector3 value) =>
        !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
        !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
        !float.IsNaN(value.z) && !float.IsInfinity(value.z);
}
