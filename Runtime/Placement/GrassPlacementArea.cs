using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;

public enum GrassPlacementShape
{
    Box = 0,
    Circle = 1,
    Texture = 2
}

[Flags]
public enum GrassPlacementChange
{
    None = 0,
    Density = 1,
    GroundColor = 2,
    Surface = 4,
    All = Density | GroundColor | Surface
}

/// <summary>A snapshot consumed by the grass density and ground-color capture passes.</summary>
public struct GrassPlacementDrawData
{
    public Matrix4x4 LocalToWorld;
    public Matrix4x4 WorldToMask;
    /// <summary>Independent color mapping; a small density spot can use a whole-terrain albedo bake.</summary>
    public Matrix4x4 GroundWorldToMask;
    public Bounds WorldBounds;
    public Terrain Terrain;
    public Vector4 TerrainRect;
    /// <summary>CPU occupancy for painted maps; querying it never requests a texture upload.</summary>
    public GrassDensityAsset DensityAsset;
    public Texture DensityTexture;
    public GrassPlacementShape Shape;
    public float Density;
    public float EdgeFalloff;
    public Texture GroundColorTexture;
    public bool GroundColorUsesTerrainBounds;
    public Color GroundTint;
    public float GroundColorStrength;
    public Texture GroundLayerTexture;
    /// <summary>worldXZ * xy + zw gives the terrain layer's repeated diffuse UV.</summary>
    public Vector4 GroundLayerUV;
    public Vector4 GroundLayerRemapMin;
    public Vector4 GroundLayerRemapMax;

    /// <summary>
    /// Conservative XZ occupancy using this capture's bounds and mapping. Painted pixel data
    /// remains owned by DensityAsset; discard the snapshot when its source revision changes.
    /// </summary>
    public readonly bool IntersectsCoverage(Bounds worldBounds)
    {
        return Density > 0f && IntersectsCoverage(worldBounds, WorldBounds, WorldToMask,
            Shape, DensityAsset, DensityTexture);
    }

    internal static bool IntersectsCoverage(Bounds worldBounds, Bounds sourceBounds,
        Matrix4x4 worldToMask, GrassPlacementShape shape, GrassDensityAsset densityAsset, Texture densityTexture)
    {
        Vector3 queryMin = worldBounds.min, queryMax = worldBounds.max;
        Vector3 sourceMin = sourceBounds.min, sourceMax = sourceBounds.max;
        if (sourceMax.x < queryMin.x || sourceMin.x > queryMax.x ||
            sourceMax.z < queryMin.z || sourceMin.z > queryMax.z)
            return false;
        if (shape == GrassPlacementShape.Texture && !densityAsset &&
            (!densityTexture || densityTexture.dimension != TextureDimension.Tex2D))
            return false;

        Vector3 a = worldToMask.MultiplyPoint3x4(new Vector3(queryMin.x, 0f, queryMin.z));
        Vector3 b = worldToMask.MultiplyPoint3x4(new Vector3(queryMax.x, 0f, queryMin.z));
        Vector3 c = worldToMask.MultiplyPoint3x4(new Vector3(queryMin.x, 0f, queryMax.z));
        Vector3 d = worldToMask.MultiplyPoint3x4(new Vector3(queryMax.x, 0f, queryMax.z));
        float xMin = Mathf.Min(Mathf.Min(a.x, b.x), Mathf.Min(c.x, d.x));
        float xMax = Mathf.Max(Mathf.Max(a.x, b.x), Mathf.Max(c.x, d.x));
        float yMin = Mathf.Min(Mathf.Min(a.z, b.z), Mathf.Min(c.z, d.z));
        float yMax = Mathf.Max(Mathf.Max(a.z, b.z), Mathf.Max(c.z, d.z));
        if (shape == GrassPlacementShape.Texture && densityAsset)
            return densityAsset.HasCoverageIn(Rect.MinMaxRect(xMin, yMin, xMax, yMax));
        if (shape == GrassPlacementShape.Circle)
        {
            // The transformed query's bounding rectangle contains its full footprint.
            // Reject only when even that rectangle misses the unit circle; keeping
            // equality and the full radius preserves the caller's filter margin.
            float nearestX = Mathf.Clamp(0.5f, xMin, xMax) - 0.5f;
            float nearestY = Mathf.Clamp(0.5f, yMin, yMax) - 0.5f;
            return nearestX * nearestX + nearestY * nearestY <= 0.25f;
        }
        // World X/Z overlap was checked above. These local axes complete the
        // conservative rectangle overlap check for a rotated or narrow box/texture.
        return xMax >= 0f && xMin <= 1f && yMax >= 0f && yMin <= 1f;
    }
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
    [Tooltip("Optional mesh surface and Scene brush collider. Its Renderer, or a parent Renderer, supports this area.")]
    [SerializeField] private Collider paintSurface;

    [Header("Texture coverage")]
    [Tooltip("Optional paintable asset. Takes precedence over Density Texture. Black contains no grass.")]
    [SerializeField] private GrassDensityAsset densityAsset;
    [Tooltip("Linear positive density in red: black is empty, white is full. No CPU readability is required.")]
    [SerializeField] private Texture densityTexture;
    [Tooltip("Optional bounds of a generated texture's coverage, in its original UVs. This crops the draw without stretching the mask.")]
    [SerializeField] private Rect textureCoverageBounds = new Rect(0f, 0f, 1f, 1f);

    [Header("Ground color matching")]
    [Tooltip("The grass TerrainLayer painted beneath this area. Its texture tiling is preserved.")]
    [SerializeField] private TerrainLayer groundLayer;
    [Tooltip("Optional color map. Overrides the repeated TerrainLayer color; its mapping can follow the whole Terrain independently of the density spot.")]
    [SerializeField] private Texture groundColorTexture;
    [SerializeField] private bool groundColorUsesTerrainBounds;
    [SerializeField] private Color groundTint = Color.white;
    [SerializeField, Range(0f, 1f)] private float groundColorStrength;

    private static readonly List<GrassPlacementArea> activeAreas = new List<GrassPlacementArea>();
    private static int revision = 1;
    private static int registryGeneration = 1;
    private int registeredGeneration;
    private bool registered;
    private int sourceRevision = 1, densityRevision = 1, groundColorRevision = 1, surfaceRevision = 1;
    private readonly object pendingLock = new object();
    private GrassDensityAsset observedAsset;
    private Matrix4x4 previousFrame;
    private Bounds previousFrameBounds, previousBounds;
    private int previousDensityHash, previousDensityConfigurationHash, previousGroundHash, previousSurfaceHash, previousMappingHash;
    private bool hadFrame, hadCoverage, observedState;
    private GrassPlacementChange pendingChanges;
    private bool pendingFullRegion;
    private RectInt pendingPixels;

    public static IReadOnlyList<GrassPlacementArea> ActiveAreas => activeAreas;
    public static uint Revision => unchecked((uint)Volatile.Read(ref revision));
    public static event Action<Bounds> CoverageChanged;
    /// <summary>A source changed in a world-space region; density changes also affect ground alpha.</summary>
    public static event Action<GrassPlacementArea, Bounds, GrassPlacementChange> SourceChanged;

    public uint SourceRevision => unchecked((uint)Volatile.Read(ref sourceRevision));
    public uint DensityRevision => unchecked((uint)Volatile.Read(ref densityRevision));
    public uint GroundColorRevision => unchecked((uint)Volatile.Read(ref groundColorRevision));
    public uint SurfaceRevision => unchecked((uint)Volatile.Read(ref surfaceRevision));

    public GrassPlacementShape Shape => shape;
    public Terrain Terrain => terrain;
    public bool UsesTerrainBounds => useTerrainBounds;
    public Collider PaintSurface => paintSurface;
    public GrassDensityAsset DensityAsset => densityAsset;
    public Texture DensityTexture => densityAsset ? densityAsset.Texture : densityTexture;
    public TerrainLayer GroundLayer => groundLayer;
    public Texture GroundColorTexture => groundColorTexture;
    public bool GroundColorUsesTerrainBounds => groundColorUsesTerrainBounds;
    public Rect TextureCoverageBounds => textureCoverageBounds;
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
        GrassPlacementChange changes = GrassPlacementChange.None;
        if (terrain != supportingTerrain || !useTerrainBounds)
            changes |= GrassPlacementChange.All;
        if (shape != GrassPlacementShape.Texture || densityAsset || densityTexture != coverage || edgeFalloff != 0f)
            changes |= GrassPlacementChange.Density;
        float strength = matchingGroundLayer || matchingGroundColor ? 1f : 0f;
        if (groundLayer != matchingGroundLayer || groundColorTexture != matchingGroundColor ||
            groundColorStrength != strength || !groundColorUsesTerrainBounds)
            changes |= GrassPlacementChange.GroundColor;
        terrain = supportingTerrain;
        useTerrainBounds = true;
        shape = GrassPlacementShape.Texture;
        densityAsset = null;
        densityTexture = coverage;
        groundLayer = matchingGroundLayer;
        groundColorTexture = matchingGroundColor;
        groundColorUsesTerrainBounds = true;
        groundColorStrength = strength;
        edgeFalloff = 0f;
        SynchronizeAssetSubscription();
        Invalidate(changes);
    }

    public void ConfigureGroundLayer(TerrainLayer layer, float strength = 1f)
    {
        strength = Mathf.Clamp01(FiniteOr(strength, 0f));
        if (groundLayer == layer && groundColorStrength == strength)
            return;
        groundLayer = layer;
        groundColorStrength = strength;
        MarkGroundColorDirty();
    }

    public void SetGroundColor(Color tint, float strength)
    {
        tint = SanitizeColor(tint);
        strength = Mathf.Clamp01(FiniteOr(strength, 0f));
        if (groundTint == tint && groundColorStrength == strength)
            return;
        groundTint = tint;
        groundColorStrength = strength;
        MarkGroundColorDirty();
    }

    public void SetGroundColorTexture(Texture texture, bool terrainAligned, float strength = 1f)
    {
        strength = Mathf.Clamp01(FiniteOr(strength, 0f));
        if (groundColorTexture == texture && groundColorUsesTerrainBounds == terrainAligned && groundColorStrength == strength)
            return;
        groundColorTexture = texture;
        groundColorUsesTerrainBounds = terrainAligned;
        groundColorStrength = strength;
        MarkGroundColorDirty();
    }

    /// <summary>Crop an external mask's draw in its original UV frame. Empty bounds disable coverage.</summary>
    public void SetTextureCoverageBounds(Rect uvBounds)
    {
        uvBounds = SanitizeRect(uvBounds);
        if (textureCoverageBounds == uvBounds)
            return;
        textureCoverageBounds = uvBounds;
        MarkCoverageDirty();
    }

    public void SetDensityAsset(GrassDensityAsset asset, bool fitTerrain)
    {
        bool mappingChanged = useTerrainBounds != fitTerrain;
        if (densityAsset == asset && !densityTexture && shape == GrassPlacementShape.Texture && !mappingChanged && edgeFalloff == 0f)
            return;
        densityAsset = asset;
        densityTexture = null;
        shape = GrassPlacementShape.Texture;
        useTerrainBounds = fitTerrain;
        edgeFalloff = 0f;
        SynchronizeAssetSubscription();
        Invalidate(mappingChanged ? GrassPlacementChange.All : GrassPlacementChange.Density);
    }

    public bool TryGetCaptureData(out GrassPlacementDrawData data)
    {
        RefreshState();
        data = default;
        if (!IsActiveSource() || density <= 0f ||
            !TryGetFrame(out Matrix4x4 localToWorld, out Matrix4x4 worldToMask, out Bounds frameBounds) ||
            !TryGetCoverageExtent(localToWorld, frameBounds, out Rect coverageUV, out Bounds bounds))
            return false;

        Texture coverage = null;
        if (shape == GrassPlacementShape.Texture)
        {
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
            LocalToWorld = CropFrame(localToWorld, coverageUV),
            WorldToMask = worldToMask,
            GroundWorldToMask = GetGroundWorldToMask(worldToMask),
            WorldBounds = bounds,
            Terrain = terrain,
            TerrainRect = terrainRect,
            DensityAsset = densityAsset,
            DensityTexture = coverage,
            Shape = shape,
            Density = density,
            EdgeFalloff = edgeFalloff,
            GroundColorTexture = groundColorTexture && groundColorTexture.dimension == TextureDimension.Tex2D &&
                (!groundColorUsesTerrainBounds || terrain)
                ? groundColorTexture : null,
            GroundColorUsesTerrainBounds = groundColorUsesTerrainBounds,
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
    /// Conservative XZ occupancy for a live source. External GPU textures use their authored
    /// coverage bounds; painted assets omit empty regions without synchronous texture readback.
    /// Use the captured draw data for repeated tile queries in the same capture.
    /// </summary>
    public bool IntersectsCoverage(Bounds worldBounds)
    {
        if (!IsActiveSource() || density <= 0f ||
            !TryGetFrame(out Matrix4x4 localToWorld, out Matrix4x4 worldToMask, out Bounds frameBounds) ||
            frameBounds.max.x < worldBounds.min.x || frameBounds.min.x > worldBounds.max.x ||
            frameBounds.max.z < worldBounds.min.z || frameBounds.min.z > worldBounds.max.z ||
            !TryGetCoverageExtent(localToWorld, frameBounds, out _, out Bounds ownBounds))
            return false;
        return GrassPlacementDrawData.IntersectsCoverage(worldBounds, ownBounds, worldToMask,
            shape, densityAsset, densityTexture);
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
    public void MarkDirty() => Invalidate(GrassPlacementChange.All);

    /// <summary>Notify a changed external density texture. Ground alpha changes with its coverage.</summary>
    public void MarkCoverageDirty() => Invalidate(GrassPlacementChange.Density);

    /// <summary>Notify an albedo change without recapturing placement or supporting geometry.</summary>
    public void MarkGroundColorDirty() => Invalidate(GrassPlacementChange.GroundColor);

    public void MarkSurfaceDirty() => Invalidate(GrassPlacementChange.Surface);

    private void Invalidate(GrassPlacementChange changes)
    {
        if (changes == GrassPlacementChange.None)
            return;
        QueueChanges(changes);
        RefreshState();
    }

    private void QueueChanges(GrassPlacementChange changes, RectInt? pixels = null)
    {
        changes = IncludeDependentChanges(changes);
        BumpRevisions(changes);
        lock (pendingLock)
        {
            pendingChanges |= changes;
            if (!pixels.HasValue)
                pendingFullRegion = true;
            else
                pendingPixels = Union(pendingPixels, pixels.Value);
        }
    }

    private void OnAssetRegionChanged(RectInt pixels) => QueueChanges(GrassPlacementChange.Density, pixels);

    private void BumpRevisions(GrassPlacementChange changes)
    {
        if (changes == GrassPlacementChange.None)
            return;
        Interlocked.Increment(ref revision);
        Interlocked.Increment(ref sourceRevision);
        if ((changes & GrassPlacementChange.Density) != 0)
            Interlocked.Increment(ref densityRevision);
        if ((changes & GrassPlacementChange.GroundColor) != 0)
            Interlocked.Increment(ref groundColorRevision);
        if ((changes & GrassPlacementChange.Surface) != 0)
            Interlocked.Increment(ref surfaceRevision);
    }

    private void OnEnable()
    {
        // During scene loading Unity enables objects before setting Scene.isLoaded.
        // Register here; actual captures wait for the scene to finish loading.
        if (!IsRegistryEligible())
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
        registered = false;
        if (observedAsset)
            observedAsset.RegionChanged -= OnAssetRegionChanged;
        observedAsset = null;
        TerrainCallbacks.heightmapChanged -= OnTerrainHeightChanged;
        TerrainCallbacks.textureChanged -= OnTerrainTextureChanged;
        BumpRevisions(GrassPlacementChange.All);
        if (hadCoverage)
        {
            CoverageChanged?.Invoke(previousBounds);
            SourceChanged?.Invoke(this, previousBounds, GrassPlacementChange.All);
        }
        hadFrame = hadCoverage = observedState = false;
        lock (pendingLock)
        {
            pendingChanges = GrassPlacementChange.None;
            pendingFullRegion = false;
            pendingPixels = default;
        }
    }

    private void OnValidate()
    {
        size.x = Mathf.Max(0.01f, FiniteOr(size.x, 10f));
        size.y = Mathf.Max(0.01f, FiniteOr(size.y, 10f));
        density = Mathf.Clamp01(FiniteOr(density, 0f));
        edgeFalloff = Mathf.Clamp01(FiniteOr(edgeFalloff, 0f));
        groundColorStrength = Mathf.Clamp01(FiniteOr(groundColorStrength, 0f));
        groundTint = SanitizeColor(groundTint);
        textureCoverageBounds = SanitizeRect(textureCoverageBounds);
        // OnValidate can run during loading. Defer object and transform access to the main loop.
        // State comparison there distinguishes a color edit from a geometry or density edit.
        Interlocked.Increment(ref revision);
    }

    private void Update()
    {
        if (!IsRegistryEligible())
        {
            // Moving an enabled object to a preview scene need not invoke OnDisable.
            if (registered)
                OnDisable();
            return;
        }
        if (!registered || registeredGeneration != registryGeneration)
        {
            OnEnable();
            return;
        }
        RefreshState();
    }

    private void RefreshState()
    {
        // Do not mutate the active-source list while a renderer may be iterating it.
        if (!IsActiveSource())
            return;
        SynchronizeAssetSubscription();
        GrassPlacementChange changes;
        RectInt changedPixels;
        bool fullRegion;
        lock (pendingLock)
        {
            changes = pendingChanges;
            changedPixels = pendingPixels;
            fullRegion = pendingFullRegion;
            pendingChanges = GrassPlacementChange.None;
            pendingPixels = default;
            pendingFullRegion = false;
        }

        // Do not clear transform.hasChanged: other systems can use that flag too.
        bool validFrame = TryGetFrame(out Matrix4x4 frame, out _, out Bounds frameBounds);
        int densityConfigurationHash = GetDensityConfigurationHash();
        int densityHash = GetDensityStateHash(densityConfigurationHash);
        int groundHash = GetGroundStateHash();
        int surfaceHash = GetSurfaceStateHash();
        int mappingHash = GetMappingStateHash();
        // A pending brush rectangle does not limit a simultaneous inspector/Undo edit to
        // density settings, source binding or ground color: those affect the complete source.
        if (densityConfigurationHash != previousDensityConfigurationHash || groundHash != previousGroundHash)
            fullRegion = true;
        bool mappingChanged = !observedState || validFrame != hadFrame || mappingHash != previousMappingHash ||
            (validFrame && (frame != previousFrame || !SameXZ(frameBounds, previousFrameBounds)));
        GrassPlacementChange detected = mappingChanged ? GrassPlacementChange.All : GrassPlacementChange.None;
        if (densityHash != previousDensityHash)
            detected |= GrassPlacementChange.Density;
        if (groundHash != previousGroundHash)
            detected |= GrassPlacementChange.GroundColor;
        if (surfaceHash != previousSurfaceHash || (validFrame && frameBounds != previousFrameBounds))
            detected |= GrassPlacementChange.Surface;
        detected = IncludeDependentChanges(detected);
        BumpRevisions(detected & ~changes);
        changes |= detected;
        if (changes == GrassPlacementChange.None)
            return;

        Bounds bounds = default;
        bool hasCoverage = validFrame && density > 0f &&
            TryGetCoverageExtent(frame, frameBounds, out _, out bounds);
        Bounds dirty = default;
        bool hasDirtyBounds = false;
        if (!fullRegion && !mappingChanged && densityAsset && validFrame &&
            changedPixels.width > 0 && changedPixels.height > 0 &&
            (changes & GrassPlacementChange.Surface) == 0)
        {
            Rect uv = Rect.MinMaxRect(Mathf.Max(0f, (changedPixels.xMin - 0.5f) / densityAsset.Width),
                Mathf.Max(0f, (changedPixels.yMin - 0.5f) / densityAsset.Height),
                Mathf.Min(1f, (changedPixels.xMax + 0.5f) / densityAsset.Width),
                Mathf.Min(1f, (changedPixels.yMax + 0.5f) / densityAsset.Height));
            hasDirtyBounds = TryGetBoundsFromUV(frame, uv, frameBounds, out dirty);
        }
        else
        {
            if (hadCoverage)
            {
                dirty = previousBounds;
                hasDirtyBounds = true;
            }
            if (hasCoverage)
            {
                if (hasDirtyBounds)
                    dirty.Encapsulate(bounds);
                else
                    dirty = bounds;
                hasDirtyBounds = true;
            }
            if (!hasDirtyBounds && validFrame && (changes & GrassPlacementChange.Surface) != 0)
            {
                dirty = frameBounds;
                hasDirtyBounds = true;
            }
        }

        previousFrame = frame;
        previousFrameBounds = frameBounds;
        previousBounds = bounds;
        previousDensityHash = densityHash;
        previousDensityConfigurationHash = densityConfigurationHash;
        previousGroundHash = groundHash;
        previousSurfaceHash = surfaceHash;
        previousMappingHash = mappingHash;
        hadFrame = validFrame;
        hadCoverage = hasCoverage;
        observedState = true;

        if (hasDirtyBounds)
        {
            CoverageChanged?.Invoke(dirty);
            SourceChanged?.Invoke(this, dirty, changes);
        }
    }

    private void Register()
    {
        if (!activeAreas.Contains(this))
            activeAreas.Add(this);
        registered = true;
        registeredGeneration = registryGeneration;
    }

    private bool IsActiveSource() => IsRegistryEligible() && gameObject.scene.isLoaded;

    private bool IsRegistryEligible()
    {
        if (!isActiveAndEnabled || !gameObject.scene.IsValid())
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
            observedAsset.RegionChanged -= OnAssetRegionChanged;
        observedAsset = IsRegistryEligible() ? densityAsset : null;
        if (observedAsset)
            observedAsset.RegionChanged += OnAssetRegionChanged;
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

        if (!ReferenceEquals(terrain, null))
        {
            if (!terrain || !terrain.terrainData || !terrain.isActiveAndEnabled)
                return false;
            var supportingScene = terrain.gameObject.scene;
            if (!supportingScene.IsValid() || !supportingScene.isLoaded)
                return false;
#if UNITY_EDITOR
            // The area may live in a normal scene while an assigned terrain is moved
            // into a preview scene. Its geometry must not leak into the main scene.
            if (UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(supportingScene))
                return false;
#endif
        }
        else if (!ReferenceEquals(paintSurface, null))
        {
            // An explicitly assigned mesh surface must not turn into generic coverage
            // when its collider is destroyed, unloaded or moved into a preview scene.
            if (!paintSurface || !paintSurface.gameObject.activeInHierarchy)
                return false;
            if (!paintSurface.GetComponent<Renderer>() && !paintSurface.GetComponentInParent<Renderer>())
                return false;
            var supportingScene = paintSurface.gameObject.scene;
            if (!supportingScene.IsValid() || !supportingScene.isLoaded)
                return false;
#if UNITY_EDITOR
            if (UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(supportingScene))
                return false;
#endif
        }
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

    private bool TryGetCoverageExtent(Matrix4x4 frame, Bounds frameBounds, out Rect uvBounds, out Bounds bounds)
    {
        uvBounds = new Rect(0f, 0f, 1f, 1f);
        bounds = frameBounds;
        if (shape == GrassPlacementShape.Texture)
        {
            if (densityAsset)
            {
                if (!densityAsset.TryGetCoverageBounds(out uvBounds))
                    return false;
            }
            else
            {
                if (!densityTexture || densityTexture.dimension != TextureDimension.Tex2D)
                    return false;
                uvBounds = SanitizeRect(textureCoverageBounds);
            }
        }
        return TryGetBoundsFromUV(frame, uvBounds, frameBounds, out bounds);
    }

    private static Matrix4x4 CropFrame(Matrix4x4 frame, Rect uvBounds) => frame * Matrix4x4.TRS(
        new Vector3(uvBounds.center.x - 0.5f, 0f, uvBounds.center.y - 0.5f), Quaternion.identity,
        new Vector3(uvBounds.width, 1f, uvBounds.height));

    private static bool TryGetBoundsFromUV(Matrix4x4 frame, Rect uvBounds, Bounds frameBounds, out Bounds bounds)
    {
        bounds = default;
        if (uvBounds.width <= 0f || uvBounds.height <= 0f)
            return false;
        Matrix4x4 cropped = CropFrame(frame, uvBounds);
        bounds = new Bounds(cropped.MultiplyPoint3x4(Vector3.zero), Vector3.zero);
        bounds.Encapsulate(cropped.MultiplyPoint3x4(new Vector3(-0.5f, 0f, -0.5f)));
        bounds.Encapsulate(cropped.MultiplyPoint3x4(new Vector3(0.5f, 0f, -0.5f)));
        bounds.Encapsulate(cropped.MultiplyPoint3x4(new Vector3(-0.5f, 0f, 0.5f)));
        bounds.Encapsulate(cropped.MultiplyPoint3x4(new Vector3(0.5f, 0f, 0.5f)));
        Vector3 minimum = bounds.min;
        Vector3 maximum = bounds.max;
        minimum.x = Mathf.Max(minimum.x, frameBounds.min.x);
        minimum.z = Mathf.Max(minimum.z, frameBounds.min.z);
        maximum.x = Mathf.Min(maximum.x, frameBounds.max.x);
        maximum.z = Mathf.Min(maximum.z, frameBounds.max.z);
        minimum.y = frameBounds.min.y;
        maximum.y = frameBounds.max.y;
        if (minimum.x >= maximum.x || minimum.z >= maximum.z)
            return false;
        bounds.SetMinMax(minimum, maximum);
        return true;
    }

    private Matrix4x4 GetGroundWorldToMask(Matrix4x4 sourceWorldToMask)
    {
        if (!groundColorUsesTerrainBounds || !terrain || !terrain.terrainData)
            return sourceWorldToMask;
        Vector3 origin = terrain.transform.position;
        Vector3 extent = terrain.terrainData.size;
        return Matrix4x4.Scale(new Vector3(1f / extent.x, 1f, 1f / extent.z)) *
            Matrix4x4.Translate(new Vector3(-origin.x, 0f, -origin.z));
    }

    private Vector4 GetGroundLayerUV()
    {
        if (!groundLayer)
            return new Vector4(1f, 1f, 0f, 0f);
        Vector2 tileSize = groundLayer.tileSize;
        float scaleX = 1f / SignedTileSize(tileSize.x);
        float scaleY = 1f / SignedTileSize(tileSize.y);
        Vector3 origin = terrain ? terrain.transform.position : Vector3.zero;
        Vector2 offset = groundLayer.tileOffset;
        return new Vector4(scaleX, scaleY, (FiniteOr(offset.x, 0f) - origin.x) * scaleX,
            (FiniteOr(offset.y, 0f) - origin.z) * scaleY);
    }

    private int GetDensityConfigurationHash()
    {
        unchecked
        {
            int hash = (int)shape;
            hash = hash * 397 ^ density.GetHashCode();
            hash = hash * 397 ^ edgeFalloff.GetHashCode();
            hash = hash * 397 ^ (densityAsset ? densityAsset.GetEntityId().GetHashCode() : 0);
            hash = hash * 397 ^ (densityAsset ? densityAsset.Width : 0);
            hash = hash * 397 ^ (densityAsset ? densityAsset.Height : 0);
            hash = hash * 397 ^ (!densityAsset && densityTexture ? densityTexture.GetEntityId().GetHashCode() : 0);
            hash = hash * 397 ^ (densityAsset ? 0 : textureCoverageBounds.GetHashCode());
            return hash;
        }
    }

    private int GetDensityStateHash(int configurationHash)
    {
        unchecked
        {
            return configurationHash * 397 ^ (densityAsset ? (int)densityAsset.Revision : GetTextureStateHash(densityTexture));
        }
    }

    private int GetGroundStateHash()
    {
        unchecked
        {
            int hash = GetTextureStateHash(groundColorTexture);
            hash = hash * 397 ^ groundColorUsesTerrainBounds.GetHashCode();
            hash = hash * 397 ^ groundTint.GetHashCode();
            hash = hash * 397 ^ groundColorStrength.GetHashCode();
            hash = hash * 397 ^ (groundLayer ? groundLayer.GetEntityId().GetHashCode() : 0);
            if (groundLayer)
            {
                hash = hash * 397 ^ groundLayer.tileSize.GetHashCode();
                hash = hash * 397 ^ groundLayer.tileOffset.GetHashCode();
                hash = hash * 397 ^ groundLayer.diffuseRemapMin.GetHashCode();
                hash = hash * 397 ^ groundLayer.diffuseRemapMax.GetHashCode();
                hash = hash * 397 ^ GetTextureStateHash(groundLayer.diffuseTexture);
            }
            return hash;
        }
    }

    private int GetSurfaceStateHash()
    {
        unchecked
        {
            int hash = paintSurface ? paintSurface.GetEntityId().GetHashCode() : 0;
            if (paintSurface)
                hash = hash * 397 ^ paintSurface.bounds.GetHashCode();
            if (terrain && terrain.terrainData)
            {
                TerrainData data = terrain.terrainData;
                hash = hash * 397 ^ data.GetEntityId().GetHashCode();
                hash = hash * 397 ^ data.size.y.GetHashCode();
                hash = hash * 397 ^ terrain.transform.position.y.GetHashCode();
                hash = hash * 397 ^ GetTextureStateHash(data.heightmapTexture);
                hash = hash * 397 ^ GetTextureStateHash(data.holesTexture);
            }
            return hash;
        }
    }

    private int GetMappingStateHash()
    {
        unchecked
        {
            int hash = terrain ? terrain.GetEntityId().GetHashCode() : 0;
            hash = hash * 397 ^ useTerrainBounds.GetHashCode();
            hash = hash * 397 ^ gameObject.scene.handle;
            if (terrain && terrain.terrainData)
            {
                Vector3 origin = terrain.transform.position;
                Vector3 extent = terrain.terrainData.size;
                hash = hash * 397 ^ origin.x.GetHashCode();
                hash = hash * 397 ^ origin.z.GetHashCode();
                hash = hash * 397 ^ extent.x.GetHashCode();
                hash = hash * 397 ^ extent.z.GetHashCode();
            }
            return hash;
        }
    }

    private static int GetTextureStateHash(Texture texture)
    {
        if (!texture)
            return 0;
        unchecked
        {
            int hash = texture.GetEntityId().GetHashCode();
            hash = hash * 397 ^ (int)texture.updateCount;
            hash = hash * 397 ^ texture.width;
            hash = hash * 397 ^ texture.height;
            hash = hash * 397 ^ (int)texture.dimension;
            return hash;
        }
    }

    private void OnTerrainHeightChanged(Terrain changedTerrain, RectInt region, bool synched)
    {
        if (changedTerrain == terrain)
            MarkSurfaceDirty();
    }

    private void OnTerrainTextureChanged(Terrain changedTerrain, string textureName, RectInt region, bool synched)
    {
        if (changedTerrain == terrain)
        {
            if (textureName == TerrainData.HolesTextureName)
                MarkSurfaceDirty();
            else
                MarkGroundColorDirty();
        }
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
        unchecked { registryGeneration++; }
        Interlocked.Increment(ref revision);
        CoverageChanged = null;
        SourceChanged = null;
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

    private static float SignedTileSize(float value)
    {
        value = FiniteOr(value, 1f);
        return Mathf.Abs(value) >= 0.00001f ? value : value < 0f ? -0.00001f : 0.00001f;
    }

    private static Color SanitizeColor(Color color) => new Color(
        Mathf.Max(0f, FiniteOr(color.r, 1f)), Mathf.Max(0f, FiniteOr(color.g, 1f)),
        Mathf.Max(0f, FiniteOr(color.b, 1f)), Mathf.Clamp01(FiniteOr(color.a, 1f)));

    private static bool IsFinite(Vector3 value) =>
        !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
        !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
        !float.IsNaN(value.z) && !float.IsInfinity(value.z);

    private static Rect SanitizeRect(Rect value)
    {
        if (!IsFinite(new Vector3(value.x, value.y, value.width)) ||
            float.IsNaN(value.height) || float.IsInfinity(value.height) || value.width <= 0f || value.height <= 0f)
            return default;
        float xMin = Mathf.Clamp01(value.xMin), yMin = Mathf.Clamp01(value.yMin);
        float xMax = Mathf.Clamp01(value.xMax), yMax = Mathf.Clamp01(value.yMax);
        return xMax <= xMin || yMax <= yMin ? default : Rect.MinMaxRect(xMin, yMin, xMax, yMax);
    }

    private static bool SameXZ(Bounds left, Bounds right) => left.min.x == right.min.x &&
        left.min.z == right.min.z && left.max.x == right.max.x && left.max.z == right.max.z;

    private static GrassPlacementChange IncludeDependentChanges(GrassPlacementChange changes) =>
        (changes & GrassPlacementChange.Density) != 0 ? changes | GrassPlacementChange.GroundColor : changes;

    private static RectInt Union(RectInt left, RectInt right)
    {
        if (left.width <= 0 || left.height <= 0)
            return right;
        if (right.width <= 0 || right.height <= 0)
            return left;
        int x = Mathf.Min(left.xMin, right.xMin), y = Mathf.Min(left.yMin, right.yMin);
        return new RectInt(x, y, Mathf.Max(left.xMax, right.xMax) - x, Mathf.Max(left.yMax, right.yMax) - y);
    }
}
