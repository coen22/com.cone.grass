using System;
using System.Collections.Generic;
using UnityEngine;

namespace Cone.Grass
{
    /// <summary>A terrain whose detail density layers a <see cref="GrassMeshDetails"/> scatters, and which kind each layer is.</summary>
    [Serializable]
    public sealed class GrassMeshDetailTerrain
    {
        [SerializeField] private Terrain _terrain;
        [Tooltip("For each of the terrain's detail layers, the index of the kind it scatters, or -1 for none."), SerializeField]
        private int[] _layerKinds = Array.Empty<int>();
        [Tooltip("The terrain's Detail Distance before the converter turned Unity's own detail drawing off; negative when it did not."), SerializeField]
        private float _originalDetailDistance = -1f;

        /// <summary>The terrain.</summary>
        public Terrain Terrain { get => _terrain; set => _terrain = value; }

        /// <summary>For each detail layer of the terrain, the kind index it scatters, or -1 for none.</summary>
        public int[] LayerKinds { get => _layerKinds ??= Array.Empty<int>(); set => _layerKinds = value ?? Array.Empty<int>(); }

        /// <summary>
        /// The terrain's Detail Distance before Unity's own detail drawing was turned off, so it can be restored; negative
        /// when it was left on.
        /// </summary>
        public float OriginalDetailDistance { get => _originalDetailDistance; set => _originalDetailDistance = value; }
    }

    /// <summary>
    /// Mesh detail kinds: draws terrain detail meshes (ferns, heather, bushes) from the terrains' own detail density layers
    /// with GPU scatter, frustum and Hi-Z occlusion culling, distance LODs with a TAA-free cross-fade, and per-instance
    /// scale, rotation, colour and wind, through a BatchRendererGroup so URP Lit, Shader Graph and Cone/Foliage materials
    /// render unchanged in every URP pass (depth, normals, shadows, forward, deferred). Needs the
    /// <c>Grass Mesh Detail Renderer Feature</c> on the URP renderer.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Cone/Grass/Mesh Details")]
    public sealed class GrassMeshDetails : MonoBehaviour
    {
        private static readonly List<GrassMeshDetails> s_active = new List<GrassMeshDetails>();
        private static uint s_globalRevision = 1;

        [SerializeField] private List<GrassMeshKind> _kinds = new List<GrassMeshKind>();
        [SerializeField] private List<GrassMeshDetailTerrain> _terrains = new List<GrassMeshDetailTerrain>();

        [Header("Population")]
        [Tooltip("Multiplies every kind's density."), Min(0f), SerializeField] private float _densityScale = 1f;
        [Tooltip("Multiplies every kind's LOD and shadow distances."), Min(0.01f), SerializeField] private float _distanceScale = 1f;

        [Header("Culling")]
        [Tooltip("Skip instances hidden behind last frame's depth (a Hi-Z pyramid of the camera depth). Needs the camera depth texture."), SerializeField]
        private bool _occlusionCulling = true;
        [SerializeField] private bool _castShadows = true;
        [Tooltip("Draw in reflection cameras (Crest planar reflections, realtime reflection probes) at a shorter reach."), SerializeField]
        private bool _drawInReflections = true;
        [Range(0.05f, 1f), SerializeField] private float _reflectionDistanceScale = 0.5f;

        [Header("Wind")]
        [SerializeField] private GrassMeshWind _wind = new GrassMeshWind();

        [Header("Water Line")]
        [Tooltip("Use the scene's Infinite Grass Renderer water line when it has one, so blades and meshes share one shoreline."), SerializeField]
        private bool _useGrassWaterLine = true;
        [SerializeField] private GrassWaterLine _waterLine = new GrassWaterLine();

        [Header("GPU Capacity")]
        [Tooltip("Visible instances each draw list starts with; lists grow on their own when a frame overflows them."), Min(64), SerializeField]
        private int _initialListCapacity = 8192;
        [Tooltip("The most instance slots per camera, in millions."), Range(0.01f, 16f), SerializeField]
        private float _maxInstances = 4f;

        [NonSerialized] private uint _revision = 1;

        /// <summary>Every enabled component, in enable order.</summary>
        public static IReadOnlyList<GrassMeshDetails> Active => s_active;

        /// <summary>Changes whenever an enabled component is added, removed or edited.</summary>
        internal static uint GlobalRevision => s_globalRevision;

        /// <summary>The kinds, indexed by <see cref="GrassMeshDetailTerrain.LayerKinds"/>.</summary>
        public List<GrassMeshKind> Kinds { get => _kinds ??= new List<GrassMeshKind>(); set { _kinds = value ?? new List<GrassMeshKind>(); MarkChanged(); } }

        /// <summary>The terrains to scatter on and their layer-to-kind maps.</summary>
        public List<GrassMeshDetailTerrain> Terrains { get => _terrains ??= new List<GrassMeshDetailTerrain>(); set { _terrains = value ?? new List<GrassMeshDetailTerrain>(); MarkChanged(); } }

        /// <summary>Multiplies every kind's density.</summary>
        public float DensityScale { get => _densityScale; set { _densityScale = value; MarkChanged(); } }

        /// <summary>Multiplies every kind's LOD and shadow distances.</summary>
        public float DistanceScale { get => _distanceScale; set { _distanceScale = value; MarkChanged(); } }

        /// <summary>Whether instances hidden behind last frame's depth are skipped.</summary>
        public bool OcclusionCulling { get => _occlusionCulling; set => _occlusionCulling = value; }

        /// <summary>Whether kinds that cast shadows draw into the sun's shadow cascades.</summary>
        public bool CastShadows { get => _castShadows; set { _castShadows = value; MarkChanged(); } }

        /// <summary>Whether reflection cameras draw the kinds, at <see cref="ReflectionDistanceScale"/>.</summary>
        public bool DrawInReflections { get => _drawInReflections; set => _drawInReflections = value; }

        /// <summary>Distance scale for reflection cameras.</summary>
        public float ReflectionDistanceScale { get => _reflectionDistanceScale; set => _reflectionDistanceScale = value; }

        /// <summary>The wind.</summary>
        public GrassMeshWind Wind { get => _wind ??= new GrassMeshWind(); set => _wind = value ?? new GrassMeshWind(); }

        /// <summary>Use the scene's <see cref="InfiniteGrassRenderer"/> water line when it is active.</summary>
        public bool UseGrassWaterLine { get => _useGrassWaterLine; set => _useGrassWaterLine = value; }

        /// <summary>This component's own water line, used when the grass renderer's is not.</summary>
        public GrassWaterLine WaterLine { get => _waterLine ??= new GrassWaterLine(); set => _waterLine = value ?? new GrassWaterLine(); }

        /// <summary>Visible instances each draw list starts with.</summary>
        public int InitialListCapacity { get => _initialListCapacity; set { _initialListCapacity = value; MarkChanged(); } }

        /// <summary>The most instance slots per camera, in millions.</summary>
        public float MaxInstances { get => _maxInstances; set { _maxInstances = value; MarkChanged(); } }

        /// <summary>Changes whenever the kinds, terrains or capacities change, or <see cref="Refresh"/> is called.</summary>
        public uint Revision => _revision;

        /// <summary>
        /// The water line in force: the scene grass renderer's when <see cref="UseGrassWaterLine"/> is on and it applies,
        /// otherwise this component's own. Returns false when no water line applies.
        /// </summary>
        public bool TryResolveWaterLine(out float level, out float shoreFade)
        {
            InfiniteGrassRenderer grass = InfiniteGrassRenderer.Instance;
            if (_useGrassWaterLine && grass && grass.isActiveAndEnabled && grass.WaterLine.TryResolve(out level, out shoreFade))
            {
                return true;
            }

            return WaterLine.TryResolve(out level, out shoreFade);
        }

        /// <summary>Re-reads the terrains' detail layers (after painting them by script) and rebuilds the draw lists.</summary>
        public void Refresh() => MarkChanged();

        /// <summary>
        /// Reads the most recent per-kind counts the GPU reported for <paramref name="camera"/>: instances drawn by the camera
        /// (all LODs) and in the shadow cascades. They arrive a few frames late. Returns false before the first report.
        /// </summary>
        public bool TryGetCounts(Camera camera, out int[] cameraInstances, out int[] shadowInstances)
        {
            return GrassMeshDetailRenderer.TryGetCounts(this, camera, out cameraInstances, out shadowInstances);
        }

        private void MarkChanged()
        {
            _revision++;
            s_globalRevision++;
        }

        private void OnEnable()
        {
            if (!s_active.Contains(this))
            {
                s_active.Add(this);
            }

            MarkChanged();
        }

        private void OnDisable()
        {
            s_active.Remove(this);
            GrassMeshDetailDensity.Release(this);
            MarkChanged();
        }

        private void OnValidate() => MarkChanged();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => s_active.Clear();
    }
}
