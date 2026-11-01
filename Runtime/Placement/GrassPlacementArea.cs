using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace Cone.Grass
{
    /// <summary>The coverage shape of a <see cref="GrassPlacementArea"/>.</summary>
    public enum GrassPlacementShape
    {
        /// <summary>A rectangle of the area's size, rotated with the object's yaw.</summary>
        Box = 0,
        /// <summary>An ellipse inscribed in the area's size.</summary>
        Circle = 1,
        /// <summary>
        /// A density map: a painted <see cref="GrassDensityAsset"/> or an external texture's red channel.
        /// </summary>
        Texture = 2
    }

    /// <summary>Which captured inputs of a placement area changed; a combination of flags.</summary>
    [Flags]
    public enum GrassPlacementChange
    {
        /// <summary>Nothing changed.</summary>
        None = 0,
        /// <summary>Coverage or density changed; ground alpha changes with it.</summary>
        Density = 1,
        /// <summary>The matched ground colour or the plant-kind weights changed.</summary>
        GroundColor = 2,
        /// <summary>The supporting terrain or mesh surface changed.</summary>
        Surface = 4,
        /// <summary>Every input changed.</summary>
        All = Density | GroundColor | Surface
    }

    /// <summary>A snapshot consumed by the grass density and ground-color capture passes.</summary>
    public struct GrassPlacementDrawData
    {
        /// <summary>Maps the cropped unit capture quad to world space.</summary>
        public Matrix4x4 LocalToWorld;
        /// <summary>Maps world positions to the density mask's UV in x and z.</summary>
        public Matrix4x4 WorldToMask;
        /// <summary>Independent color mapping; a small density spot can use a whole-terrain albedo bake.</summary>
        public Matrix4x4 GroundWorldToMask;
        /// <summary>World bounds of the captured coverage.</summary>
        public Bounds WorldBounds;
        /// <summary>The supporting terrain, or null.</summary>
        public Terrain Terrain;
        /// <summary>The terrain's world XZ origin in xy and its XZ size in zw; zero without a terrain.</summary>
        public Vector4 TerrainRect;
        /// <summary>CPU occupancy for painted maps; querying it never requests a texture upload.</summary>
        public GrassDensityAsset DensityAsset;
        /// <summary>The density texture sampled for <see cref="GrassPlacementShape.Texture"/> coverage.</summary>
        public Texture DensityTexture;
        /// <summary>Captured draw extent in original mask UVs. Default preserves a full mask for manually created snapshots.</summary>
        public Rect CoverageUV;
        /// <summary>The coverage shape.</summary>
        public GrassPlacementShape Shape;
        /// <summary>Coverage density, from 0 to 1.</summary>
        public float Density;
        /// <summary>Fraction of the shape radius used to soften the edge.</summary>
        public float EdgeFalloff;
        /// <summary>Optional ground colour map, or null.</summary>
        public Texture GroundColorTexture;
        /// <summary>Whether the ground colour map spans the terrain rather than the area.</summary>
        public bool GroundColorUsesTerrainBounds;
        /// <summary>Tint applied to the matched ground colour.</summary>
        public Color GroundTint;
        /// <summary>How strongly the matched ground colour applies, from 0 to 1.</summary>
        public float GroundColorStrength;
        /// <summary>The ground terrain layer's diffuse texture, or null.</summary>
        public Texture GroundLayerTexture;
        /// <summary>Native terrain group-first sampler source. Manual snapshots may leave this null to use GroundLayerTexture.</summary>
        public Texture GroundLayerSamplerTexture;
        /// <summary>worldXZ * xy + zw gives the terrain layer's repeated diffuse UV.</summary>
        public Vector4 GroundLayerUV;
        /// <summary>The ground terrain layer's diffuse remap minimum.</summary>
        public Vector4 GroundLayerRemapMin;
        /// <summary>The ground terrain layer's diffuse remap maximum.</summary>
        public Vector4 GroundLayerRemapMax;
        /// <summary>Per-kind weight multipliers for kinds 0-3 and 4-7; all zero (a manual snapshot) keeps the profile's mix.</summary>
        /// <summary>Whether the area reweights the plant kinds; areas without an override keep the profile's mix.</summary>
        public bool OverridesVariants;
        /// <summary>Plant-kind weight multipliers for kinds 0 to 3.</summary>
        public Vector4 VariantWeights0;
        /// <summary>Plant-kind weight multipliers for kinds 4 to 7.</summary>
        public Vector4 VariantWeights1;

        /// <summary>
        /// Conservative XZ occupancy using this capture's bounds and mapping. Painted pixel data
        /// remains owned by DensityAsset; discard the snapshot when its source revision changes.
        /// </summary>
        public readonly bool IntersectsCoverage(Bounds worldBounds)
        {
            return Density > 0f && IntersectsCoverage(worldBounds, WorldBounds, WorldToMask,
                Shape, DensityAsset, DensityTexture, CoverageUV);
        }

        internal static bool IntersectsCoverage(Bounds worldBounds, Bounds sourceBounds,
            Matrix4x4 worldToMask, GrassPlacementShape shape, GrassDensityAsset densityAsset, Texture densityTexture,
            Rect coverageUV = default)
        {
            Vector3 queryMin = worldBounds.min, queryMax = worldBounds.max;
            Vector3 sourceMin = sourceBounds.min, sourceMax = sourceBounds.max;
            if (sourceMax.x < queryMin.x || sourceMin.x > queryMax.x ||
                sourceMax.z < queryMin.z || sourceMin.z > queryMax.z)
            {
                return false;
            }

            if (shape == GrassPlacementShape.Texture)
            {
                if (GrassPlacementArea.HasAssignedReference(densityAsset) && !densityAsset)
                {
                    return false;
                }

                if (!densityAsset && !IsReadyDensityTexture(densityTexture))
                {
                    return false;
                }
            }

            Vector3 a = worldToMask.MultiplyPoint3x4(new Vector3(queryMin.x, 0f, queryMin.z));
            Vector3 b = worldToMask.MultiplyPoint3x4(new Vector3(queryMax.x, 0f, queryMin.z));
            Vector3 c = worldToMask.MultiplyPoint3x4(new Vector3(queryMin.x, 0f, queryMax.z));
            Vector3 d = worldToMask.MultiplyPoint3x4(new Vector3(queryMax.x, 0f, queryMax.z));
            float xMin = Mathf.Min(Mathf.Min(a.x, b.x), Mathf.Min(c.x, d.x));
            float xMax = Mathf.Max(Mathf.Max(a.x, b.x), Mathf.Max(c.x, d.x));
            float yMin = Mathf.Min(Mathf.Min(a.z, b.z), Mathf.Min(c.z, d.z));
            float yMax = Mathf.Max(Mathf.Max(a.z, b.z), Mathf.Max(c.z, d.z));
            if (shape == GrassPlacementShape.Texture && densityAsset)
            {
                return densityAsset.HasCoverageIn(Rect.MinMaxRect(xMin, yMin, xMax, yMax));
            }

            if (shape == GrassPlacementShape.Circle)
            {
                // The transformed query's bounding rectangle contains its full footprint.
                // Reject only when even that rectangle misses the unit circle; keeping
                // equality and the full radius preserves the caller's filter margin.
                float nearestX = Mathf.Clamp(0.5f, xMin, xMax) - 0.5f;
                float nearestY = Mathf.Clamp(0.5f, yMin, yMax) - 0.5f;
                return nearestX * nearestX + nearestY * nearestY <= 0.25f;
            }
            // The world AABB of a rotated crop still includes empty corners. Test its
            // actual local UV extent while retaining the original sampling coordinates.
            Rect rectangle = shape == GrassPlacementShape.Texture && coverageUV != default
                ? coverageUV : new Rect(0f, 0f, 1f, 1f);
            return xMax >= rectangle.xMin && xMin <= rectangle.xMax &&
                yMax >= rectangle.yMin && yMin <= rectangle.yMax;
        }

        // Both capture passes use ordinary Texture2D sampling. Explicit multisampled
        // bindings have no automatic resolve; the producer must supply a usable view.
        internal static bool IsReadyTexture(Texture texture) => texture &&
            texture.dimension == TextureDimension.Tex2D &&
            (!(texture is RenderTexture target) || (target.IsCreated() &&
                (target.antiAliasing <= 1 || !target.bindTextureMS)));

        /// <summary>
        /// Whether the format supplies the normalized or floating-point red channel
        /// used by density sampling. Dimension, GPU storage readiness and color-space
        /// requirements are separate; this format-only check never creates storage.
        /// </summary>
        /// <param name="texture">The candidate density texture.</param>
        public static bool SupportsDensityFormat(Texture texture)
        {
            if (!texture || (texture is Texture2D image && image.format == TextureFormat.Alpha8))
            {
                return false;
            }

            GraphicsFormat format = texture.graphicsFormat;
            return format != GraphicsFormat.None && !GraphicsFormatUtility.IsAlphaOnlyFormat(format) &&
                !GraphicsFormatUtility.IsIntegerFormat(format) && !GraphicsFormatUtility.IsDepthStencilFormat(format);
        }

        internal static bool IsReadyDensityTexture(Texture texture) => IsReadyTexture(texture) && SupportsDensityFormat(texture);
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
        [SerializeField, FormerlySerializedAs("shape")] private GrassPlacementShape _shape = GrassPlacementShape.Circle;
        [SerializeField, Range(0f, 1f), FormerlySerializedAs("density")] private float _density = 1f;
        [Tooltip("Fraction of the shape radius used to soften the edge. Use zero for a generated mask with its own falloff.")]
        [SerializeField, Range(0f, 1f), FormerlySerializedAs("edgeFalloff")] private float _edgeFalloff = 0.15f;
        [Tooltip("World-space width and length before the object's X/Z scale. Rotation around Y is supported.")]
        [SerializeField, FormerlySerializedAs("size")] private Vector2 _size = new Vector2(10f, 10f);

        [Header("Surface and mapping")]
        [Tooltip("Explicit supporting terrain. Assigning this does not enable grass outside the area.")]
        [SerializeField, FormerlySerializedAs("terrain")] private Terrain _terrain;
        [Tooltip("Map the texture over this terrain's complete XZ extent. Object position, scale and rotation are then ignored.")]
        [SerializeField, FormerlySerializedAs("useTerrainBounds")] private bool _useTerrainBounds;
        [Tooltip("Optional mesh surface and Scene brush collider. Its visible Renderer, or a parent Renderer, needs geometry and at least one material slot.")]
        [SerializeField, FormerlySerializedAs("paintSurface")] private Collider _paintSurface;

        [Header("Texture coverage")]
        [Tooltip("Optional paintable asset. Takes precedence over Density Texture. Black contains no grass.")]
        [SerializeField, FormerlySerializedAs("densityAsset")] private GrassDensityAsset _densityAsset;
        [Tooltip("Linear positive density in red: black is empty, white is full. No CPU readability is required.")]
        [SerializeField, FormerlySerializedAs("densityTexture")] private Texture _densityTexture;
        [Tooltip("Optional bounds of a generated texture's coverage, in its original UVs. This crops the draw without stretching the mask.")]
        [SerializeField, FormerlySerializedAs("textureCoverageBounds")] private Rect _textureCoverageBounds = new Rect(0f, 0f, 1f, 1f);

        [Header("Ground color matching")]
        [Tooltip("The grass TerrainLayer painted beneath this area. Its texture tiling is preserved.")]
        [SerializeField, FormerlySerializedAs("groundLayer")] private TerrainLayer _groundLayer;
        [Tooltip("Optional color map. Overrides the repeated TerrainLayer color; its mapping can follow the whole Terrain independently of the density spot.")]
        [SerializeField, FormerlySerializedAs("groundColorTexture")] private Texture _groundColorTexture;
        [SerializeField, FormerlySerializedAs("groundColorUsesTerrainBounds")] private bool _groundColorUsesTerrainBounds;
        [SerializeField, FormerlySerializedAs("groundTint")] private Color _groundTint = Color.white;
        [SerializeField, Range(0f, 1f), FormerlySerializedAs("groundColorStrength")] private float _groundColorStrength;

        [Header("Plant kinds")]
        [Tooltip("Reweight the renderer's Variant Profile here. Off grows the profile's own mix.")]
        [SerializeField, FormerlySerializedAs("overrideVariantWeights")] private bool _overrideVariantWeights;
        [Tooltip("Per kind, in the profile's order: a multiplier of its weight from 0 (none here) to 1. Overlapping areas take each kind's largest.")]
        [SerializeField, FormerlySerializedAs("variantWeights")] private float[] _variantWeights = DefaultVariantWeights();

        private static readonly List<GrassPlacementArea> s_activeAreas = new List<GrassPlacementArea>();
        private static int s_revision = 1;
        private static int s_registryGeneration = 1;
        private int _registeredGeneration;
        private bool _registered;
        private int _sourceRevision = 1, _densityRevision = 1, _groundColorRevision = 1, _surfaceRevision = 1;
        private readonly object _pendingLock = new object();
        private GrassDensityAsset _observedAsset;
        private readonly List<Material> _supportingMaterials = new List<Material>();
        private TerrainData _groundSamplerTerrainData;
        private TerrainLayer _groundSamplerSelectedLayer, _groundSamplerFirstLayer;
        private TerrainLayer[] _groundSamplerLayers;
        private bool _groundSamplerHasGroup;
        private int _groundSamplerLayerCount, _groundSamplerTopologyHash;
        private int _groundSamplerRefreshRequested = 1;
#if UNITY_EDITOR
        private int _groundSamplerEditorDirtyCount;
#endif
        private Matrix4x4 _previousFrame;
        private Bounds _previousFrameBounds, _previousBounds;
        private int _previousDensityHash, _previousDensityConfigurationHash, _previousGroundHash, _previousSurfaceHash, _previousMappingHash;
        private bool _hadFrame, _hadCoverage, _observedState;
        private GrassPlacementChange _pendingChanges;
        private bool _pendingFullRegion;
        private RectInt _pendingPixels;

        /// <summary>The enabled areas in loaded scenes.</summary>
        public static IReadOnlyList<GrassPlacementArea> ActiveAreas => s_activeAreas;
        /// <summary>Advances whenever any area registers, unregisters or changes a source.</summary>
        public static uint Revision => unchecked((uint)Volatile.Read(ref s_revision));
        /// <summary>Raised with the world region whose coverage, colour or surface changed.</summary>
        public static event Action<Bounds> CoverageChanged;
        /// <summary>A source changed in a world-space region; density changes also affect ground alpha.</summary>
        public static event Action<GrassPlacementArea, Bounds, GrassPlacementChange> SourceChanged;

        /// <summary>Advances on every change to this area's sources.</summary>
        public uint SourceRevision => unchecked((uint)Volatile.Read(ref _sourceRevision));
        /// <summary>Advances when this area's coverage or density changes.</summary>
        public uint DensityRevision => unchecked((uint)Volatile.Read(ref _densityRevision));
        /// <summary>Advances when this area's ground colour or plant-kind weights change.</summary>
        public uint GroundColorRevision => unchecked((uint)Volatile.Read(ref _groundColorRevision));
        /// <summary>Advances when this area's supporting surface changes.</summary>
        public uint SurfaceRevision => unchecked((uint)Volatile.Read(ref _surfaceRevision));

        /// <summary>The coverage shape.</summary>
        public GrassPlacementShape Shape => _shape;
        /// <summary>The explicit supporting terrain, or null.</summary>
        public Terrain Terrain => _terrain;
        /// <summary>Whether a terrain binding exists, including an assigned object that has been destroyed.</summary>
        public bool HasAssignedTerrain => HasAssignedReference(_terrain);
        /// <summary>Whether a mesh binding exists, including an assigned object that has been destroyed.</summary>
        public bool HasAssignedPaintSurface => HasAssignedReference(_paintSurface);
        private bool HasAssignedDensityAsset => HasAssignedReference(_densityAsset);

        internal static bool HasAssignedReference(UnityEngine.Object value)
        {
            if (ReferenceEquals(value, null))
            {
                return false;
            }
#if UNITY_EDITOR
            // Unset Editor fields can have zero-ID placeholders. Destroyed
            // assigned objects retain their IDs here and still require validation.
            return value.GetEntityId() != EntityId.None;
#else
            // Destroyed player objects can also have zero IDs. Keep their managed
            // references assigned so validity checks reject them instead of falling back.
            return true;
#endif
        }
        /// <summary>
        /// Whether texture coverage spans the terrain's complete XZ extent instead of the object's frame.
        /// </summary>
        public bool UsesTerrainBounds => _useTerrainBounds;
        /// <summary>The optional mesh surface and Scene brush collider, or null.</summary>
        public Collider PaintSurface => _paintSurface;
        /// <summary>The paintable density asset, or null; it takes precedence over the density texture.</summary>
        public GrassDensityAsset DensityAsset => _densityAsset;
        /// <summary>
        /// The density texture used for texture coverage: the density asset's texture when one is assigned, otherwise
        /// the external texture.
        /// </summary>
        public Texture DensityTexture
        {
            get
            {
                if (HasAssignedDensityAsset)
                {
                    return _densityAsset ? _densityAsset.Texture : null;
                }

                return _densityTexture;
            }
        }
        /// <summary>The grass terrain layer painted beneath this area, or null.</summary>
        public TerrainLayer GroundLayer => _groundLayer;
        /// <summary>The optional ground colour map, or null.</summary>
        public Texture GroundColorTexture => _groundColorTexture;
        /// <summary>Whether the ground colour map spans the terrain rather than the area.</summary>
        public bool GroundColorUsesTerrainBounds => _groundColorUsesTerrainBounds;
        /// <summary>The cropped coverage of an external density texture, in its original UVs.</summary>
        public Rect TextureCoverageBounds => _textureCoverageBounds;
        /// <summary>Tint applied to the matched ground colour.</summary>
        public Color GroundTint => _groundTint;
        /// <summary>How strongly the matched ground colour applies, from 0 to 1.</summary>
        public float GroundColorStrength => _groundColorStrength;
        /// <summary>Whether this area reweights the renderer's plant kinds.</summary>
        public bool OverridesVariantWeights => _overrideVariantWeights;

        /// <summary>The multiplier this area applies to a kind's weight; one without an override.</summary>
        /// <param name="kind">The kind's index in the profile.</param>
        public float GetVariantWeight(int kind) => _overrideVariantWeights && _variantWeights != null &&
            kind >= 0 && kind < _variantWeights.Length ? Mathf.Clamp01(FiniteOr(_variantWeights[kind], 1f)) : 1f;

        /// <summary>Reweights the renderer's kinds inside this area, in the profile's order; missing entries keep one.</summary>
        /// <param name="weights">Multipliers from 0 to 1, per kind in the profile's order.</param>
        public void SetVariantWeights(params float[] weights)
        {
            var next = DefaultVariantWeights();
            for (int k = 0; weights != null && k < Mathf.Min(weights.Length, next.Length); k++)
            {
                next[k] = Mathf.Clamp01(FiniteOr(weights[k], 1f));
            }

            bool changed = !_overrideVariantWeights;
            for (int k = 0; !changed && k < next.Length; k++)
            {
                changed = _variantWeights == null || k >= _variantWeights.Length || _variantWeights[k] != next[k];
            }

            _overrideVariantWeights = true;
            _variantWeights = next;
            if (changed)
            {
                Invalidate(GrassPlacementChange.GroundColor);
            }
        }

        /// <summary>Grows the profile's own mix here again.</summary>
        public void ClearVariantWeights()
        {
            if (!_overrideVariantWeights)
            {
                return;
            }

            _overrideVariantWeights = false;
            Invalidate(GrassPlacementChange.GroundColor);
        }

        private static float[] DefaultVariantWeights() => new[] { 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f };

        private void GetVariantWeights(out Vector4 first, out Vector4 second)
        {
            first = new Vector4(GetVariantWeight(0), GetVariantWeight(1), GetVariantWeight(2), GetVariantWeight(3));
            second = new Vector4(GetVariantWeight(4), GetVariantWeight(5), GetVariantWeight(6), GetVariantWeight(7));
        }
        /// <summary>Coverage density, from 0 to 1.</summary>
        public float Density => _density;
        /// <summary>Fraction of the shape radius used to soften the edge.</summary>
        public float EdgeFalloff => _edgeFalloff;
        /// <summary>World-space width and length before the object's X/Z scale.</summary>
        public Vector2 Size => _size;
        /// <summary>World bounds of the area's frame; an empty box at the object when the frame is unusable.</summary>
        public Bounds WorldBounds => TryGetFrame(out _, out _, out Bounds bounds) ? bounds : new Bounds(transform.position, Vector3.zero);
        /// <summary>Maps world positions to mask UV in x and z; identity when the frame is unusable.</summary>
        public Matrix4x4 WorldToMask => TryGetFrame(out _, out Matrix4x4 matrix, out _) ? matrix : Matrix4x4.identity;

        /// <summary>Replace a generated terrain-mask binding, including clearing missing output.</summary>
        /// <remarks>Sets texture coverage over the whole terrain, with no edge falloff and full ground colour strength when a layer or colour map is given.</remarks>
        /// <param name="supportingTerrain">The terrain the mask covers.</param>
        /// <param name="coverage">The density mask; its red channel is the density.</param>
        /// <param name="matchingGroundLayer">Optional terrain layer whose colour the blade roots match.</param>
        /// <param name="matchingGroundColor">Optional terrain-aligned ground colour map.</param>
        public void ConfigureTexture(Terrain supportingTerrain, Texture coverage,
            TerrainLayer matchingGroundLayer = null, Texture matchingGroundColor = null)
        {
            GrassPlacementChange changes = GrassPlacementChange.None;
            if (_terrain != supportingTerrain || !_useTerrainBounds)
            {
                changes |= GrassPlacementChange.All;
            }

            if (_shape != GrassPlacementShape.Texture || HasAssignedDensityAsset || _densityTexture != coverage || _edgeFalloff != 0f)
            {
                changes |= GrassPlacementChange.Density;
            }

            float strength = matchingGroundLayer || matchingGroundColor ? 1f : 0f;
            if (_groundLayer != matchingGroundLayer || _groundColorTexture != matchingGroundColor ||
                _groundColorStrength != strength || !_groundColorUsesTerrainBounds)
            {
                changes |= GrassPlacementChange.GroundColor;
            }

            _terrain = supportingTerrain;
            _useTerrainBounds = true;
            _shape = GrassPlacementShape.Texture;
            _densityAsset = null;
            _densityTexture = coverage;
            _groundLayer = matchingGroundLayer;
            _groundColorTexture = matchingGroundColor;
            _groundColorUsesTerrainBounds = true;
            _groundColorStrength = strength;
            _edgeFalloff = 0f;
            SynchronizeAssetSubscription();
            Invalidate(changes);
        }

        /// <summary>Sets the terrain layer whose colour the blade roots match, and its strength.</summary>
        /// <param name="layer">The grass terrain layer painted beneath this area.</param>
        /// <param name="strength">How strongly the layer colour applies, clamped to 0 to 1.</param>
        public void ConfigureGroundLayer(TerrainLayer layer, float strength = 1f)
        {
            strength = Mathf.Clamp01(FiniteOr(strength, 0f));
            if (_groundLayer == layer && _groundColorStrength == strength)
            {
                return;
            }

            _groundLayer = layer;
            _groundColorStrength = strength;
            Invalidate(GrassPlacementChange.GroundColor);
        }

        /// <summary>Sets the ground tint and the strength of the matched ground colour.</summary>
        /// <param name="tint">Tint applied to the matched ground colour.</param>
        /// <param name="strength">How strongly the matched ground colour applies, clamped to 0 to 1.</param>
        public void SetGroundColor(Color tint, float strength)
        {
            tint = SanitizeColor(tint);
            strength = Mathf.Clamp01(FiniteOr(strength, 0f));
            if (_groundTint == tint && _groundColorStrength == strength)
            {
                return;
            }

            _groundTint = tint;
            _groundColorStrength = strength;
            Invalidate(GrassPlacementChange.GroundColor);
        }

        /// <summary>Sets the ground colour map, its mapping and its strength.</summary>
        /// <param name="texture">The ground colour map, or null.</param>
        /// <param name="terrainAligned">Whether the map spans the terrain rather than the area.</param>
        /// <param name="strength">How strongly the map applies, clamped to 0 to 1.</param>
        public void SetGroundColorTexture(Texture texture, bool terrainAligned, float strength = 1f)
        {
            strength = Mathf.Clamp01(FiniteOr(strength, 0f));
            if (_groundColorTexture == texture && _groundColorUsesTerrainBounds == terrainAligned && _groundColorStrength == strength)
            {
                return;
            }

            _groundColorTexture = texture;
            _groundColorUsesTerrainBounds = terrainAligned;
            _groundColorStrength = strength;
            Invalidate(GrassPlacementChange.GroundColor);
        }

        /// <summary>Crop an external mask's draw in its original UV frame. Empty bounds disable coverage.</summary>
        public void SetTextureCoverageBounds(Rect uvBounds)
        {
            uvBounds = SanitizeRect(uvBounds);
            if (_textureCoverageBounds == uvBounds)
            {
                return;
            }

            _textureCoverageBounds = uvBounds;
            MarkCoverageDirty();
        }

        /// <summary>
        /// Uses a paintable density asset as texture coverage, replacing an external density texture, with no edge
        /// falloff.
        /// </summary>
        /// <param name="asset">The density asset.</param>
        /// <param name="fitTerrain">Whether the asset spans the terrain's complete XZ extent.</param>
        public void SetDensityAsset(GrassDensityAsset asset, bool fitTerrain)
        {
            bool mappingChanged = _useTerrainBounds != fitTerrain;
            if (ReferenceEquals(_densityAsset, asset) && ReferenceEquals(_densityTexture, null) &&
                _shape == GrassPlacementShape.Texture && !mappingChanged && _edgeFalloff == 0f)
            {
                return;
            }

            _densityAsset = asset;
            _densityTexture = null;
            _shape = GrassPlacementShape.Texture;
            _useTerrainBounds = fitTerrain;
            _edgeFalloff = 0f;
            SynchronizeAssetSubscription();
            Invalidate(mappingChanged ? GrassPlacementChange.All : GrassPlacementChange.Density);
        }

        /// <summary>A snapshot of this area for the capture passes.</summary>
        /// <param name="data">The snapshot, or default when the area contributes nothing.</param>
        /// <returns>False when the area is inactive, has no density or no usable frame, or its density texture is not ready.</returns>
        public bool TryGetCaptureData(out GrassPlacementDrawData data)
        {
            RefreshState();
            data = default;
            if (!IsActiveSource() || _density <= 0f ||
                !TryGetFrame(out Matrix4x4 localToWorld, out Matrix4x4 worldToMask, out Bounds frameBounds) ||
                !TryGetCoverageExtent(localToWorld, frameBounds, out Rect coverageUV, out Bounds bounds))
            {
                return false;
            }

            Texture coverage = null;
            if (_shape == GrassPlacementShape.Texture)
            {
                coverage = DensityTexture;
                if (!GrassPlacementDrawData.IsReadyDensityTexture(coverage))
                {
                    return false;
                }
            }

            Vector4 terrainRect = Vector4.zero;
            if (_terrain && _terrain.terrainData)
            {
                Vector3 position = _terrain.transform.position;
                Vector3 terrainSize = _terrain.terrainData.size;
                terrainRect = new Vector4(position.x, position.z, terrainSize.x, terrainSize.z);
            }

            GetVariantWeights(out Vector4 variantWeights0, out Vector4 variantWeights1);
            data = new GrassPlacementDrawData
            {
                OverridesVariants = _overrideVariantWeights,
                VariantWeights0 = variantWeights0,
                VariantWeights1 = variantWeights1,
                LocalToWorld = CropFrame(localToWorld, coverageUV),
                WorldToMask = worldToMask,
                GroundWorldToMask = GetGroundWorldToMask(worldToMask),
                WorldBounds = bounds,
                Terrain = _terrain,
                TerrainRect = terrainRect,
                DensityAsset = _densityAsset,
                DensityTexture = coverage,
                CoverageUV = coverageUV,
                Shape = _shape,
                Density = _density,
                EdgeFalloff = _edgeFalloff,
                GroundColorTexture = HasReadyGroundColorTexture() ? _groundColorTexture : null,
                GroundColorUsesTerrainBounds = _groundColorUsesTerrainBounds,
                GroundTint = _groundTint,
                GroundColorStrength = _groundColorStrength,
                GroundLayerTexture = _groundLayer ? _groundLayer.diffuseTexture : null,
                GroundLayerSamplerTexture = GetGroundLayerSamplerTexture(),
                GroundLayerUV = GetGroundLayerUV(),
                GroundLayerRemapMin = _groundLayer ? _groundLayer.diffuseRemapMin : Vector4.zero,
                GroundLayerRemapMax = _groundLayer ? _groundLayer.diffuseRemapMax : Vector4.one
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
            if (!IsActiveSource() || _density <= 0f ||
                !TryGetFrame(out Matrix4x4 localToWorld, out Matrix4x4 worldToMask, out Bounds frameBounds) ||
                frameBounds.max.x < worldBounds.min.x || frameBounds.min.x > worldBounds.max.x ||
                frameBounds.max.z < worldBounds.min.z || frameBounds.min.z > worldBounds.max.z ||
                !TryGetCoverageExtent(localToWorld, frameBounds, out Rect coverageUV, out Bounds ownBounds))
            {
                return false;
            }

            return GrassPlacementDrawData.IntersectsCoverage(worldBounds, ownBounds, worldToMask,
                _shape, _densityAsset, _densityTexture, coverageUV);
        }

        // Interaction ownership needs the same cropped extent as capture without
        // requesting a painted texture upload or reading an external texture back.
        internal bool TryGetCoverageWorldBounds(out Bounds bounds)
        {
            bounds = default;
            return IsActiveSource() && _density > 0f &&
                TryGetFrame(out Matrix4x4 frame, out _, out Bounds frameBounds) &&
                TryGetCoverageExtent(frame, frameBounds, out _, out bounds);
        }

        /// <summary>
        /// Paints or erases the density asset with a round world-space brush. Callers record Undo first.
        /// </summary>
        /// <param name="worldPosition">The brush centre in world space.</param>
        /// <param name="radius">The brush radius in metres.</param>
        /// <param name="strength">How much density one dab adds or removes, from 0 to 1.</param>
        /// <param name="hardness">The fraction of the radius painted at full strength.</param>
        /// <param name="erase">Whether to remove density instead of adding it.</param>
        /// <returns>Whether any texel changed; false without a density asset or texture coverage.</returns>
        public bool Paint(Vector3 worldPosition, float radius, float strength, float hardness, bool erase)
        {
            if (!_densityAsset || _shape != GrassPlacementShape.Texture || radius <= 0f ||
                !TryGetFrame(out Matrix4x4 localToWorld, out Matrix4x4 worldToMask, out _))
            {
                return false;
            }

            Vector3 uv = worldToMask.MultiplyPoint3x4(worldPosition);
            float worldWidth = localToWorld.MultiplyVector(Vector3.right).magnitude;
            float worldDepth = localToWorld.MultiplyVector(Vector3.forward).magnitude;
            return _densityAsset.Paint(new Vector2(uv.x, uv.z),
                new Vector2(radius / worldWidth, radius / worldDepth), strength, hardness, erase);
        }

        /// <summary>Invalidate both old and new extents when a source moves or its GPU output changes.</summary>
        public void MarkDirty()
        {
            Interlocked.Exchange(ref _groundSamplerRefreshRequested, 1);
            Invalidate(GrassPlacementChange.All);
        }

        /// <summary>Notify a changed external density texture. Ground alpha changes with its coverage.</summary>
        public void MarkCoverageDirty() => Invalidate(GrassPlacementChange.Density);

        /// <summary>Notify an albedo or terrain-layer order change without recapturing placement or supporting geometry.</summary>
        public void MarkGroundColorDirty()
        {
            Interlocked.Exchange(ref _groundSamplerRefreshRequested, 1);
            Invalidate(GrassPlacementChange.GroundColor);
        }

        /// <summary>Notify a change to the supporting terrain or mesh surface.</summary>
        public void MarkSurfaceDirty() => Invalidate(GrassPlacementChange.Surface);

        private void Invalidate(GrassPlacementChange changes)
        {
            if (changes == GrassPlacementChange.None)
            {
                return;
            }

            QueueChanges(changes);
            RefreshState();
        }

        private void QueueChanges(GrassPlacementChange changes, RectInt? pixels = null)
        {
            changes = IncludeDependentChanges(changes);
            BumpRevisions(changes);
            lock (_pendingLock)
            {
                _pendingChanges |= changes;
                if (!pixels.HasValue)
                {
                    _pendingFullRegion = true;
                }
                else
                {
                    _pendingPixels = Union(_pendingPixels, pixels.Value);
                }
            }
        }

        private void OnAssetRegionChanged(RectInt pixels) => QueueChanges(GrassPlacementChange.Density, pixels);

        private void BumpRevisions(GrassPlacementChange changes)
        {
            if (changes == GrassPlacementChange.None)
            {
                return;
            }

            Interlocked.Increment(ref s_revision);
            Interlocked.Increment(ref _sourceRevision);
            if ((changes & GrassPlacementChange.Density) != 0)
            {
                Interlocked.Increment(ref _densityRevision);
            }

            if ((changes & GrassPlacementChange.GroundColor) != 0)
            {
                Interlocked.Increment(ref _groundColorRevision);
            }

            if ((changes & GrassPlacementChange.Surface) != 0)
            {
                Interlocked.Increment(ref _surfaceRevision);
            }
        }

        private void OnEnable()
        {
            // During scene loading Unity enables objects before setting Scene.isLoaded.
            // Register here; actual captures wait for the scene to finish loading.
            if (!IsRegistryEligible())
            {
                return;
            }

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
            s_activeAreas.Remove(this);
            _registered = false;
            if (!ReferenceEquals(_observedAsset, null))
            {
                _observedAsset.RegionChanged -= OnAssetRegionChanged;
            }

            _observedAsset = null;
            _supportingMaterials.Clear();
            _groundSamplerTerrainData = null;
            _groundSamplerSelectedLayer = _groundSamplerFirstLayer = null;
            _groundSamplerLayers = null;
            Interlocked.Exchange(ref _groundSamplerRefreshRequested, 1);
            TerrainCallbacks.heightmapChanged -= OnTerrainHeightChanged;
            TerrainCallbacks.textureChanged -= OnTerrainTextureChanged;
            BumpRevisions(GrassPlacementChange.All);
            if (_hadCoverage)
            {
                CoverageChanged?.Invoke(_previousBounds);
                SourceChanged?.Invoke(this, _previousBounds, GrassPlacementChange.All);
            }
            _hadFrame = _hadCoverage = _observedState = false;
            lock (_pendingLock)
            {
                _pendingChanges = GrassPlacementChange.None;
                _pendingFullRegion = false;
                _pendingPixels = default;
            }
        }

        private void OnValidate()
        {
            _size.x = Mathf.Max(0.01f, FiniteOr(_size.x, 10f));
            _size.y = Mathf.Max(0.01f, FiniteOr(_size.y, 10f));
            _density = Mathf.Clamp01(FiniteOr(_density, 0f));
            _edgeFalloff = Mathf.Clamp01(FiniteOr(_edgeFalloff, 0f));
            _groundColorStrength = Mathf.Clamp01(FiniteOr(_groundColorStrength, 0f));
            _groundTint = SanitizeColor(_groundTint);
            _textureCoverageBounds = SanitizeRect(_textureCoverageBounds);
            if (_variantWeights == null || _variantWeights.Length != GrassVariantSelection.MaximumKinds)
            {
                var weights = DefaultVariantWeights();
                for (int k = 0; _variantWeights != null && k < Mathf.Min(_variantWeights.Length, weights.Length); k++)
                {
                    weights[k] = _variantWeights[k];
                }

                _variantWeights = weights;
            }
            for (int k = 0; k < _variantWeights.Length; k++)
            {
                _variantWeights[k] = Mathf.Clamp01(FiniteOr(_variantWeights[k], 1f));
            }

            Interlocked.Exchange(ref _groundSamplerRefreshRequested, 1);
            // OnValidate can run during loading. Defer object and transform access to the main loop.
            // State comparison there distinguishes a color edit from a geometry or density edit.
            Interlocked.Increment(ref s_revision);
        }

        private void Update()
        {
            if (!IsRegistryEligible())
            {
                // Moving an enabled object to a preview scene need not invoke OnDisable.
                if (_registered)
                {
                    OnDisable();
                }

                return;
            }
            if (!_registered || _registeredGeneration != s_registryGeneration)
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
            {
                return;
            }

            SynchronizeAssetSubscription();
            GrassPlacementChange changes;
            RectInt changedPixels;
            bool fullRegion;
            lock (_pendingLock)
            {
                changes = _pendingChanges;
                changedPixels = _pendingPixels;
                fullRegion = _pendingFullRegion;
                _pendingChanges = GrassPlacementChange.None;
                _pendingPixels = default;
                _pendingFullRegion = false;
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
            if (densityConfigurationHash != _previousDensityConfigurationHash || groundHash != _previousGroundHash)
            {
                fullRegion = true;
            }

            bool mappingChanged = !_observedState || validFrame != _hadFrame || mappingHash != _previousMappingHash ||
                (validFrame && (frame != _previousFrame || !SameXZ(frameBounds, _previousFrameBounds)));
            GrassPlacementChange detected = mappingChanged ? GrassPlacementChange.All : GrassPlacementChange.None;
            if (densityHash != _previousDensityHash)
            {
                detected |= GrassPlacementChange.Density;
            }

            if (groundHash != _previousGroundHash)
            {
                detected |= GrassPlacementChange.GroundColor;
            }

            if (surfaceHash != _previousSurfaceHash || (validFrame && frameBounds != _previousFrameBounds))
            {
                detected |= GrassPlacementChange.Surface;
            }

            detected = IncludeDependentChanges(detected);
            BumpRevisions(detected & ~changes);
            changes |= detected;
            if (changes == GrassPlacementChange.None)
            {
                return;
            }

            Bounds bounds = default;
            bool hasCoverage = validFrame && _density > 0f &&
                TryGetCoverageExtent(frame, frameBounds, out _, out bounds);
            Bounds dirty = default;
            bool hasDirtyBounds = false;
            if (!fullRegion && !mappingChanged && _densityAsset && validFrame &&
                changedPixels.width > 0 && changedPixels.height > 0 &&
                (changes & GrassPlacementChange.Surface) == 0)
            {
                Rect uv = Rect.MinMaxRect(Mathf.Max(0f, (changedPixels.xMin - 0.5f) / _densityAsset.Width),
                    Mathf.Max(0f, (changedPixels.yMin - 0.5f) / _densityAsset.Height),
                    Mathf.Min(1f, (changedPixels.xMax + 0.5f) / _densityAsset.Width),
                    Mathf.Min(1f, (changedPixels.yMax + 0.5f) / _densityAsset.Height));
                hasDirtyBounds = TryGetBoundsFromUV(frame, uv, frameBounds, out dirty);
            }
            else
            {
                if (_hadCoverage)
                {
                    dirty = _previousBounds;
                    hasDirtyBounds = true;
                }
                if (hasCoverage)
                {
                    if (hasDirtyBounds)
                    {
                        dirty.Encapsulate(bounds);
                    }
                    else
                    {
                        dirty = bounds;
                    }

                    hasDirtyBounds = true;
                }
                if (!hasDirtyBounds && validFrame && (changes & GrassPlacementChange.Surface) != 0)
                {
                    dirty = frameBounds;
                    hasDirtyBounds = true;
                }
            }

            _previousFrame = frame;
            _previousFrameBounds = frameBounds;
            _previousBounds = bounds;
            _previousDensityHash = densityHash;
            _previousDensityConfigurationHash = densityConfigurationHash;
            _previousGroundHash = groundHash;
            _previousSurfaceHash = surfaceHash;
            _previousMappingHash = mappingHash;
            _hadFrame = validFrame;
            _hadCoverage = hasCoverage;
            _observedState = true;

            if (hasDirtyBounds)
            {
                CoverageChanged?.Invoke(dirty);
                SourceChanged?.Invoke(this, dirty, changes);
            }
        }

        private void Register()
        {
            if (!s_activeAreas.Contains(this))
            {
                s_activeAreas.Add(this);
            }

            _registered = true;
            _registeredGeneration = s_registryGeneration;
        }

        private bool IsActiveSource() => IsRegistryEligible() && gameObject.scene.isLoaded;

        private bool IsRegistryEligible()
        {
            if (!isActiveAndEnabled || !gameObject.scene.IsValid())
            {
                return false;
            }
#if UNITY_EDITOR
            // Prefab Stage and other preview scenes must not contribute coverage to the main scene.
            if (UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(gameObject.scene))
            {
                return false;
            }
#endif
            return true;
        }

        private void SynchronizeAssetSubscription()
        {
            if (ReferenceEquals(_observedAsset, _densityAsset))
            {
                return;
            }
            // RegionChanged lives on the managed wrapper, which can outlive the
            // native asset. Detaching it must also work after that asset is destroyed.
            if (!ReferenceEquals(_observedAsset, null))
            {
                _observedAsset.RegionChanged -= OnAssetRegionChanged;
            }

            _observedAsset = IsRegistryEligible() ? _densityAsset : null;
            if (_observedAsset)
            {
                _observedAsset.RegionChanged += OnAssetRegionChanged;
            }
        }

        private bool TryGetFrame(out Matrix4x4 localToWorld, out Matrix4x4 worldToMask, out Bounds bounds)
        {
            localToWorld = worldToMask = Matrix4x4.identity;
            bounds = default;
            Vector3 center = transform.position;
            float yaw = transform.eulerAngles.y;
            if (float.IsNaN(yaw) || float.IsInfinity(yaw))
            {
                return false;
            }

            Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
            Vector3 frameSize = new Vector3(Mathf.Abs(transform.lossyScale.x) * _size.x, 1f,
                Mathf.Abs(transform.lossyScale.z) * _size.y);

            if (HasAssignedTerrain)
            {
                if (!_terrain || !_terrain.terrainData || !_terrain.isActiveAndEnabled)
                {
                    return false;
                }

                var supportingScene = _terrain.gameObject.scene;
                if (!supportingScene.IsValid() || !supportingScene.isLoaded)
                {
                    return false;
                }
#if UNITY_EDITOR
                // The area may live in a normal scene while an assigned terrain is moved
                // into a preview scene. Its geometry must not leak into the main scene.
                if (UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(supportingScene))
                {
                    return false;
                }
#endif
            }
            else if (HasAssignedPaintSurface)
            {
                // An explicitly assigned mesh surface must not turn into generic coverage
                // when its collider is destroyed, unloaded or moved into a preview scene.
                if (!_paintSurface || !_paintSurface.gameObject.activeInHierarchy)
                {
                    return false;
                }

                Renderer supportingRenderer = GetSupportingRenderer(_paintSurface);
                if (!HasSupportingGeometry(supportingRenderer))
                {
                    return false;
                }

                var supportingScene = _paintSurface.gameObject.scene;
                if (!supportingScene.IsValid() || !supportingScene.isLoaded)
                {
                    return false;
                }
#if UNITY_EDITOR
                if (UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(supportingScene))
                {
                    return false;
                }
#endif
            }
            if (_useTerrainBounds)
            {
                if (!_terrain || !_terrain.terrainData)
                {
                    return false;
                }

                Vector3 terrainSize = _terrain.terrainData.size;
                center = _terrain.transform.position + new Vector3(terrainSize.x * 0.5f, 0f, terrainSize.z * 0.5f);
                frameSize = new Vector3(terrainSize.x, 1f, terrainSize.z);
                rotation = Quaternion.identity;
            }

            if (!IsFinite(center) || !IsFinite(frameSize) || frameSize.x < 0.001f || frameSize.z < 0.001f)
            {
                return false;
            }

            localToWorld = Matrix4x4.TRS(center, rotation, frameSize);
            worldToMask = Matrix4x4.Translate(new Vector3(0.5f, 0f, 0.5f)) * localToWorld.inverse;
            if (!IsFinite(localToWorld) || !IsFinite(worldToMask) ||
                (worldToMask.m00 == 0f && worldToMask.m02 == 0f) ||
                (worldToMask.m20 == 0f && worldToMask.m22 == 0f))
            {
                return false;
            }

            bounds = new Bounds(center, Vector3.zero);
            bounds.Encapsulate(localToWorld.MultiplyPoint3x4(new Vector3(-0.5f, 0f, -0.5f)));
            bounds.Encapsulate(localToWorld.MultiplyPoint3x4(new Vector3(0.5f, 0f, -0.5f)));
            bounds.Encapsulate(localToWorld.MultiplyPoint3x4(new Vector3(-0.5f, 0f, 0.5f)));
            bounds.Encapsulate(localToWorld.MultiplyPoint3x4(new Vector3(0.5f, 0f, 0.5f)));
            if (_terrain && _terrain.terrainData)
            {
                Vector3 position = _terrain.transform.position;
                Vector3 terrainSize = _terrain.terrainData.size;
                Vector3 minimum = bounds.min;
                Vector3 maximum = bounds.max;
                minimum.x = Mathf.Max(minimum.x, position.x);
                minimum.z = Mathf.Max(minimum.z, position.z);
                maximum.x = Mathf.Min(maximum.x, position.x + terrainSize.x);
                maximum.z = Mathf.Min(maximum.z, position.z + terrainSize.z);
                if (minimum.x >= maximum.x || minimum.z >= maximum.z)
                {
                    return false;
                }

                minimum.y = position.y;
                maximum.y = position.y + terrainSize.y;
                bounds.SetMinMax(minimum, maximum);
            }
            else if (_paintSurface)
            {
                Vector3 minimum = bounds.min;
                Vector3 maximum = bounds.max;
                minimum.y = _paintSurface.bounds.min.y;
                maximum.y = _paintSurface.bounds.max.y;
                bounds.SetMinMax(minimum, maximum);
            }
            // Finite inputs can still overflow during corner/bounds construction,
            // or collapse to a line at large world coordinates.
            return HasFiniteFootprint(bounds);
        }

        private bool HasSupportingGeometry(Renderer renderer)
        {
            if (!renderer || !renderer.enabled || renderer.forceRenderingOff || !renderer.gameObject.activeInHierarchy)
            {
                return false;
            }

            _supportingMaterials.Clear();
            renderer.GetSharedMaterials(_supportingMaterials);
            // The height pass supplies its own material, so null slots are allowed.
            // Only slot indices actually recorded by that pass can supply geometry.
            if (_supportingMaterials.Count == 0)
            {
                return false;
            }

            Mesh mesh = null;
            if (renderer is SkinnedMeshRenderer skinned)
            {
                mesh = skinned.sharedMesh;
            }
            else if (renderer is MeshRenderer)
            {
                mesh = renderer.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null;
            }

            if (renderer is MeshRenderer || renderer is SkinnedMeshRenderer)
            {
                if (!mesh || mesh.vertexCount == 0)
                {
                    return false;
                }

                int drawCount = Mathf.Min(mesh.subMeshCount, _supportingMaterials.Count);
                for (int submesh = 0; submesh < drawCount; submesh++)
                {
                    if (mesh.GetIndexCount(submesh) != 0)
                    {
                        return true;
                    }
                }

                return false;
            }
            return true;
        }

        private static Renderer GetSupportingRenderer(Collider surface)
        {
            Renderer renderer = surface.GetComponent<Renderer>();
            return renderer ? renderer : surface.GetComponentInParent<Renderer>();
        }

        private bool TryGetCoverageExtent(Matrix4x4 frame, Bounds frameBounds, out Rect uvBounds, out Bounds bounds)
        {
            uvBounds = new Rect(0f, 0f, 1f, 1f);
            bounds = frameBounds;
            if (_shape == GrassPlacementShape.Texture)
            {
                if (HasAssignedDensityAsset)
                {
                    if (!_densityAsset || !_densityAsset.TryGetCoverageBounds(out uvBounds))
                    {
                        return false;
                    }
                }
                else
                {
                    if (!GrassPlacementDrawData.IsReadyDensityTexture(_densityTexture))
                    {
                        return false;
                    }

                    uvBounds = SanitizeRect(_textureCoverageBounds);
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
            {
                return false;
            }

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
            if (!IsFinite(minimum) || !IsFinite(maximum) || minimum.x >= maximum.x || minimum.z >= maximum.z)
            {
                return false;
            }

            bounds.SetMinMax(minimum, maximum);
            return HasFiniteFootprint(bounds);
        }

        private Matrix4x4 GetGroundWorldToMask(Matrix4x4 sourceWorldToMask)
        {
            if (!_groundColorUsesTerrainBounds || !_terrain || !_terrain.terrainData)
            {
                return sourceWorldToMask;
            }

            Vector3 origin = _terrain.transform.position;
            Vector3 extent = _terrain.terrainData.size;
            return Matrix4x4.Scale(new Vector3(1f / extent.x, 1f, 1f / extent.z)) *
                Matrix4x4.Translate(new Vector3(-origin.x, 0f, -origin.z));
        }

        private Vector4 GetGroundLayerUV()
        {
            if (!_groundLayer)
            {
                return new Vector4(1f, 1f, 0f, 0f);
            }

            Vector2 tileSize = _groundLayer.tileSize;
            float scaleX = 1f / SignedTileSize(tileSize.x);
            float scaleY = 1f / SignedTileSize(tileSize.y);
            Vector3 origin = _terrain ? _terrain.transform.position : Vector3.zero;
            Vector2 offset = _groundLayer.tileOffset;
            return new Vector4(scaleX, scaleY, (FiniteOr(offset.x, 0f) - origin.x) * scaleX,
                (FiniteOr(offset.y, 0f) - origin.z) * scaleY);
        }

        private int GetDensityConfigurationHash()
        {
            unchecked
            {
                int hash = (int)_shape;
                hash = hash * 397 ^ _density.GetHashCode();
                hash = hash * 397 ^ _edgeFalloff.GetHashCode();
                hash = hash * 397 ^ HasAssignedDensityAsset.GetHashCode();
                hash = hash * 397 ^ (_densityAsset ? _densityAsset.GetEntityId().GetHashCode() : 0);
                hash = hash * 397 ^ (_densityAsset ? _densityAsset.Width : 0);
                hash = hash * 397 ^ (_densityAsset ? _densityAsset.Height : 0);
                hash = hash * 397 ^ (!_densityAsset && _densityTexture ? _densityTexture.GetEntityId().GetHashCode() : 0);
                hash = hash * 397 ^ (_densityAsset ? 0 : _textureCoverageBounds.GetHashCode());
                return hash;
            }
        }

        private int GetDensityStateHash(int configurationHash)
        {
            unchecked
            {
                return configurationHash * 397 ^ (_densityAsset ? (int)_densityAsset.Revision : GetTextureStateHash(_densityTexture));
            }
        }

        private int GetGroundStateHash()
        {
            unchecked
            {
                bool useLayerSampler = !HasReadyGroundColorTexture();
                Texture sampler = useLayerSampler ? GetGroundLayerSamplerTexture() : null;
                int hash = GetTextureStateHash(_groundColorTexture);
                hash = hash * 397 ^ _groundColorUsesTerrainBounds.GetHashCode();
                hash = hash * 397 ^ _groundTint.GetHashCode();
                hash = hash * 397 ^ _groundColorStrength.GetHashCode();
                hash = hash * 397 ^ (_groundLayer ? _groundLayer.GetEntityId().GetHashCode() : 0);
                if (_groundLayer)
                {
                    hash = hash * 397 ^ _groundLayer.tileSize.GetHashCode();
                    hash = hash * 397 ^ _groundLayer.tileOffset.GetHashCode();
                    hash = hash * 397 ^ _groundLayer.diffuseRemapMin.GetHashCode();
                    hash = hash * 397 ^ _groundLayer.diffuseRemapMax.GetHashCode();
                    hash = hash * 397 ^ GetTextureStateHash(_groundLayer.diffuseTexture);
                }
                hash = hash * 397 ^ (useLayerSampler ? _groundSamplerTopologyHash : 0);
                // Kind weights are captured with the ground colour, from the same sources.
                for (int kind = 0; kind < GrassVariantSelection.MaximumKinds; kind++)
                {
                    hash = hash * 397 ^ GetVariantWeight(kind).GetHashCode();
                }

                hash = hash * 397 ^ (sampler ? sampler.GetEntityId().GetHashCode() : 0);
                if (sampler)
                {
                    hash = hash * 397 ^ (int)sampler.filterMode;
                    hash = hash * 397 ^ (int)sampler.wrapModeU;
                    hash = hash * 397 ^ (int)sampler.wrapModeV;
                    hash = hash * 397 ^ sampler.mipMapBias.GetHashCode();
                    hash = hash * 397 ^ sampler.anisoLevel;
                }
                return hash;
            }
        }

        private bool HasReadyGroundColorTexture() => GrassPlacementDrawData.IsReadyTexture(_groundColorTexture) &&
            (!_groundColorUsesTerrainBounds || _terrain);

        private Texture GetGroundLayerSamplerTexture()
        {
            TerrainData data = _terrain ? _terrain.terrainData : null;
            int layerCount = data ? data.alphamapLayers : 0;
#if UNITY_EDITOR
            int editorDirtyCount = data ? UnityEditor.EditorUtility.GetDirtyCount(data) : 0;
#endif
            bool refresh = Interlocked.Exchange(ref _groundSamplerRefreshRequested, 0) != 0 ||
                _groundSamplerTerrainData != data || _groundSamplerSelectedLayer != _groundLayer ||
                _groundSamplerLayerCount != layerCount;
#if UNITY_EDITOR
            refresh |= _groundSamplerEditorDirtyCount != editorDirtyCount;
#endif
            if (refresh)
            {
                _groundSamplerTerrainData = data;
                _groundSamplerSelectedLayer = _groundLayer;
                _groundSamplerLayerCount = layerCount;
#if UNITY_EDITOR
                _groundSamplerEditorDirtyCount = editorDirtyCount;
#endif
                // TerrainData.terrainLayers allocates an array. Only refresh it for
                // source/configuration notifications; steady-state captures reuse it.
                _groundSamplerLayers = data && _groundLayer ? data.terrainLayers : null;
                _groundSamplerFirstLayer = null;
                _groundSamplerHasGroup = false;
                _groundSamplerTopologyHash = 0;
                if (_groundSamplerLayers != null)
                {
                    for (int index = 0; index < _groundSamplerLayers.Length; index++)
                    {
                        if (_groundSamplerLayers[index] != _groundLayer)
                        {
                            continue;
                        }

                        int first = index / 4 * 4;
                        _groundSamplerHasGroup = true;
                        _groundSamplerFirstLayer = _groundSamplerLayers[first];
                        unchecked
                        {
                            _groundSamplerTopologyHash = (index + 1) * 397 ^
                                (_groundSamplerFirstLayer ? _groundSamplerFirstLayer.GetEntityId().GetHashCode() : 0);
                        }
                        break;
                    }
                }
            }
            if (!_groundLayer)
            {
                return null;
            }
            // Native TerrainLit shares each four-layer group's first sampler. The
            // gray default also matches the albedo baker's missing-diffuse binding.
            if (_groundSamplerHasGroup)
            {
                return _groundSamplerFirstLayer && _groundSamplerFirstLayer.diffuseTexture
                    ? _groundSamplerFirstLayer.diffuseTexture : Texture2D.grayTexture;
            }

            return _groundLayer.diffuseTexture;
        }

        private int GetSurfaceStateHash()
        {
            unchecked
            {
                int hash = _paintSurface ? _paintSurface.GetEntityId().GetHashCode() : 0;
                if (_paintSurface)
                {
                    hash = hash * 397 ^ _paintSurface.bounds.GetHashCode();
                    // A separate area's frame and a support's AABB can stay unchanged
                    // when its geometry changes. Publish the same surface observation
                    // to capture caches and interaction history left on this surface.
                    hash = hash * 397 ^ _paintSurface.transform.localToWorldMatrix.GetHashCode();
                    hash = hash * 397 ^ _paintSurface.gameObject.scene.handle.GetRawData().GetHashCode();
                    hash = hash * 397 ^ _paintSurface.enabled.GetHashCode();
                    hash = hash * 397 ^ _paintSurface.isTrigger.GetHashCode();
                    hash = hash * 397 ^ _paintSurface.gameObject.layer;
                    if (_paintSurface is MeshCollider meshCollider)
                    {
                        hash = hash * 397 ^ GetMeshStateHash(meshCollider.sharedMesh);
                        hash = hash * 397 ^ meshCollider.convex.GetHashCode();
                        hash = hash * 397 ^ (int)meshCollider.cookingOptions;
                    }
                    Renderer renderer = GetSupportingRenderer(_paintSurface);
                    hash = hash * 397 ^ (renderer ? renderer.GetEntityId().GetHashCode() : 0);
                    if (renderer)
                    {
                        hash = hash * 397 ^ renderer.localToWorldMatrix.GetHashCode();
                        hash = hash * 397 ^ renderer.bounds.GetHashCode();
                        hash = hash * 397 ^ renderer.gameObject.scene.handle.GetRawData().GetHashCode();
                        Mesh mesh = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh :
                            renderer.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null;
                        hash = hash * 397 ^ GetMeshStateHash(mesh);
                        // The height override records one draw per slot. Reuse the
                        // readiness buffer instead of allocating sharedMaterials.
                        _supportingMaterials.Clear();
                        renderer.GetSharedMaterials(_supportingMaterials);
                        hash = hash * 397 ^ _supportingMaterials.Count;
                    }
                }
                if (_terrain && _terrain.terrainData)
                {
                    TerrainData data = _terrain.terrainData;
                    hash = hash * 397 ^ data.GetEntityId().GetHashCode();
                    hash = hash * 397 ^ _terrain.gameObject.scene.handle.GetRawData().GetHashCode();
                    hash = hash * 397 ^ data.size.y.GetHashCode();
                    hash = hash * 397 ^ _terrain.transform.position.y.GetHashCode();
                    hash = hash * 397 ^ GetTextureStateHash(data.heightmapTexture);
                    hash = hash * 397 ^ GetTextureStateHash(data.holesTexture);
                }
                return hash;
            }
        }

        private static int GetMeshStateHash(Mesh mesh)
        {
            if (!mesh)
            {
                return 0;
            }

            unchecked
            {
                int hash = mesh.GetEntityId().GetHashCode();
                hash = hash * 397 ^ mesh.bounds.GetHashCode();
                hash = hash * 397 ^ mesh.vertexCount;
                return hash * 397 ^ mesh.subMeshCount;
            }
        }

        private int GetMappingStateHash()
        {
            unchecked
            {
                int hash = _terrain ? _terrain.GetEntityId().GetHashCode() : 0;
                hash = hash * 397 ^ _useTerrainBounds.GetHashCode();
                hash = hash * 397 ^ gameObject.scene.handle.GetRawData().GetHashCode();
                if (_terrain && _terrain.terrainData)
                {
                    Vector3 origin = _terrain.transform.position;
                    Vector3 extent = _terrain.terrainData.size;
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
            {
                return 0;
            }

            unchecked
            {
                int hash = texture.GetEntityId().GetHashCode();
                hash = hash * 397 ^ (int)texture.updateCount;
                hash = hash * 397 ^ texture.width;
                hash = hash * 397 ^ texture.height;
                hash = hash * 397 ^ (int)texture.dimension;
                hash = hash * 397 ^ (int)texture.graphicsFormat;
                hash = hash * 397 ^ (texture is Texture2D image && image.format == TextureFormat.Alpha8).GetHashCode();
                if (texture is RenderTexture target)
                {
                    hash = hash * 397 ^ target.IsCreated().GetHashCode();
                    // A producer can release/recreate between observations, so the
                    // allocated flag alone cannot detect a different sampling view.
                    hash = hash * 397 ^ target.antiAliasing;
                    hash = hash * 397 ^ target.bindTextureMS.GetHashCode();
                }
                return hash;
            }
        }

        private void OnTerrainHeightChanged(Terrain changedTerrain, RectInt region, bool synched)
        {
            if (changedTerrain == _terrain)
            {
                MarkSurfaceDirty();
            }
        }

        private void OnTerrainTextureChanged(Terrain changedTerrain, string textureName, RectInt region, bool synched)
        {
            if (changedTerrain == _terrain)
            {
                if (textureName == TerrainData.HolesTextureName)
                {
                    MarkSurfaceDirty();
                }
                else
                {
                    MarkGroundColorDirty();
                }
            }
        }

        private void OnDrawGizmosSelected()
        {
            if (!TryGetFrame(out Matrix4x4 localToWorld, out _, out _))
            {
                return;
            }

            Matrix4x4 previousMatrix = Gizmos.matrix;
            Color previousColor = Gizmos.color;
            Gizmos.matrix = localToWorld;
            Gizmos.color = new Color(0.3f, 0.85f, 0.35f, 0.9f);
            if (_shape == GrassPlacementShape.Circle)
            {
                const int Segments = 64;
                Vector3 previous = new Vector3(0.5f, 0f, 0f);
                for (int i = 1; i <= Segments; i++)
                {
                    float angle = i * (Mathf.PI * 2f / Segments);
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
            s_activeAreas.Clear();
            unchecked { s_registryGeneration++; }
            Interlocked.Increment(ref s_revision);
            CoverageChanged = null;
            SourceChanged = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RestoreRegistry()
        {
            // Also handles Enter Play Mode with both scene reload and domain reload disabled.
            GrassPlacementArea[] areas = FindObjectsByType<GrassPlacementArea>(FindObjectsInactive.Exclude);
            for (int i = 0; i < areas.Length; i++)
            {
                if (areas[i].IsActiveSource())
                {
                    areas[i].OnEnable();
                }
            }
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

        private static bool IsFinite(Matrix4x4 value)
        {
            for (int index = 0; index < 16; index++)
            {
                if (float.IsNaN(value[index]) || float.IsInfinity(value[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HasFiniteFootprint(Bounds bounds)
        {
            Vector3 minimum = bounds.min, maximum = bounds.max;
            return IsFinite(minimum) && IsFinite(maximum) &&
                minimum.x < maximum.x && minimum.z < maximum.z && minimum.y <= maximum.y;
        }

        private static Rect SanitizeRect(Rect value)
        {
            if (!IsFinite(new Vector3(value.x, value.y, value.width)) ||
                float.IsNaN(value.height) || float.IsInfinity(value.height) || value.width <= 0f || value.height <= 0f)
            {
                return default;
            }

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
            {
                return right;
            }

            if (right.width <= 0 || right.height <= 0)
            {
                return left;
            }

            int x = Mathf.Min(left.xMin, right.xMin), y = Mathf.Min(left.yMin, right.yMin);
            return new RectInt(x, y, Mathf.Max(left.xMax, right.xMax) - x, Mathf.Max(left.yMax, right.yMax) - y);
        }
    }
}
