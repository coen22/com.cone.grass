using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Cone.Grass
{
    /// <summary>How a mesh kind tints each instance.</summary>
    public enum GrassMeshColorVariation
    {
        /// <summary>Every instance keeps the material's own colour.</summary>
        None = 0,
        /// <summary>
        /// Each instance takes a colour between <see cref="GrassMeshKind.HealthyColor"/> and
        /// <see cref="GrassMeshKind.DryColor"/> from the kind's noise field, as Unity's detail Healthy and Dry colours.
        /// The tint multiplies the material's <c>_BaseColor</c> where the shader reads it per instance (URP Lit does).
        /// </summary>
        HealthyDry = 1
    }

    /// <summary>One level of detail of a <see cref="GrassMeshKind"/>: a mesh, its materials and how far it is drawn.</summary>
    [Serializable]
    public sealed class GrassMeshKindLod
    {
        [SerializeField] private Mesh _mesh;
        [Tooltip("One material per submesh. URP Lit, Shader Graph and Cone/Foliage materials work: the shader needs DOTS instancing (BatchRendererGroup)."), SerializeField]
        private Material[] _materials = Array.Empty<Material>();
        [Tooltip("Camera distance, in metres, at which this LOD ends. The next LOD (or nothing, after the last) takes over across the kind's cross-fade band."), Min(0.1f), SerializeField]
        private float _endDistance = 80f;

        /// <summary>The mesh drawn at this level.</summary>
        public Mesh Mesh { get => _mesh; set => _mesh = value; }

        /// <summary>One material per submesh; a shorter array repeats its last material.</summary>
        public Material[] Materials { get => _materials ??= Array.Empty<Material>(); set => _materials = value ?? Array.Empty<Material>(); }

        /// <summary>Camera distance, in metres, at which this LOD ends.</summary>
        public float EndDistance { get => _endDistance; set => _endDistance = value; }

        /// <summary>The material for <paramref name="submesh"/>, or null.</summary>
        public Material MaterialFor(int submesh)
        {
            Material[] materials = Materials;
            return materials.Length == 0 ? null : materials[Mathf.Clamp(submesh, 0, materials.Length - 1)];
        }
    }

    /// <summary>
    /// One kind of detail mesh (a fern, heather, a bush) scattered on terrains by <see cref="GrassMeshDetails"/> from the
    /// terrain's own detail density layers. The settings follow Unity's <see cref="DetailPrototype"/>, which
    /// <see cref="GrassMeshKind.FromDetailPrototype"/> copies.
    /// </summary>
    [Serializable]
    public sealed class GrassMeshKind
    {
        /// <summary>The most levels of detail a kind can have.</summary>
        public const int MaxLods = 4;

        [SerializeField] private string _name = "Mesh Kind";
        [SerializeField] private bool _enabled = true;
        [SerializeField] private List<GrassMeshKindLod> _lods = new List<GrassMeshKindLod>();
        [Tooltip("Width, in metres, of the band before each LOD's end distance over which it cross-fades with a fixed screen dither (no TAA)."), Min(0f), SerializeField]
        private float _crossFadeWidth = 6f;

        [Header("Density")]
        [Tooltip("Instances per square metre at full coverage, for terrains in Coverage scatter mode (DetailPrototype.density). Instance Count terrains place the painted count."), Min(0f), SerializeField]
        private float _density = 1f;
        [Tooltip("Follow the terrain's Detail Density setting (DetailPrototype.useDensityScaling)."), SerializeField]
        private bool _useDensityScaling = true;

        [Header("Shape")]
        [Min(0f), SerializeField] private float _minWidth = 1f;
        [Min(0f), SerializeField] private float _maxWidth = 1f;
        [Min(0f), SerializeField] private float _minHeight = 1f;
        [Min(0f), SerializeField] private float _maxHeight = 1f;
        [Tooltip("Frequency, per metre, of the noise that varies size and colour (DetailPrototype.noiseSpread)."), Min(0f), SerializeField]
        private float _noiseSpread = 0.1f;
        [SerializeField] private int _noiseSeed;
        [Tooltip("How far instances tilt to the terrain normal, 0 upright to 1 fully aligned (DetailPrototype.alignToGround / 100)."), Range(0f, 1f), SerializeField]
        private float _alignToGround;
        [Tooltip("How far instances move from their place in the cell's grid, 0 to 1 (DetailPrototype.positionJitter / 100)."), Range(0f, 1f), SerializeField]
        private float _positionJitter = 1f;

        [Header("Colour")]
        [SerializeField] private GrassMeshColorVariation _colorVariation;
        [SerializeField] private Color _healthyColor = Color.white;
        [SerializeField] private Color _dryColor = Color.white;

        [Header("Shadows")]
        [SerializeField] private ShadowCastingMode _shadowCasting = ShadowCastingMode.On;
        [Tooltip("Camera distance, in metres, up to which instances cast sun shadows. Shorter than the draw distance saves most of the shadow cost."), Min(0f), SerializeField]
        private float _shadowDistance = 40f;

        [Header("Wind")]
        [Tooltip("How far the top leans per unit of wind strength, as a share of the plant's height."), SerializeField]
        private float _windBend = 0.35f;
        [Tooltip("How much the wind's turbulence makes each plant flutter, 0 to 1."), Range(0f, 1f), SerializeField]
        private float _windFlutter = 0.5f;

        /// <summary>The kind's name, for the inspector and diagnostics.</summary>
        public string Name { get => _name; set => _name = value; }

        /// <summary>Whether the kind is scattered and drawn.</summary>
        public bool Enabled { get => _enabled; set => _enabled = value; }

        /// <summary>Levels of detail, nearest first; at most <see cref="MaxLods"/> are used.</summary>
        public List<GrassMeshKindLod> Lods { get => _lods ??= new List<GrassMeshKindLod>(); set => _lods = value ?? new List<GrassMeshKindLod>(); }

        /// <summary>Width, in metres, of the cross-fade band before each LOD's end distance.</summary>
        public float CrossFadeWidth { get => _crossFadeWidth; set => _crossFadeWidth = value; }

        /// <summary>Instances per square metre at full coverage, for terrains in Coverage scatter mode.</summary>
        public float Density { get => _density; set => _density = value; }

        /// <summary>Whether the terrain's Detail Density setting scales this kind.</summary>
        public bool UseDensityScaling { get => _useDensityScaling; set => _useDensityScaling = value; }

        /// <summary>Smallest width scale.</summary>
        public float MinWidth { get => _minWidth; set => _minWidth = value; }

        /// <summary>Largest width scale.</summary>
        public float MaxWidth { get => _maxWidth; set => _maxWidth = value; }

        /// <summary>Smallest height scale.</summary>
        public float MinHeight { get => _minHeight; set => _minHeight = value; }

        /// <summary>Largest height scale.</summary>
        public float MaxHeight { get => _maxHeight; set => _maxHeight = value; }

        /// <summary>Frequency, per metre, of the noise that varies size and colour.</summary>
        public float NoiseSpread { get => _noiseSpread; set => _noiseSpread = value; }

        /// <summary>Seed of the size and colour noise.</summary>
        public int NoiseSeed { get => _noiseSeed; set => _noiseSeed = value; }

        /// <summary>How far instances tilt to the terrain normal, 0 to 1.</summary>
        public float AlignToGround { get => _alignToGround; set => _alignToGround = value; }

        /// <summary>How far instances move from their place in the cell's grid, 0 to 1.</summary>
        public float PositionJitter { get => _positionJitter; set => _positionJitter = value; }

        /// <summary>How each instance is tinted.</summary>
        public GrassMeshColorVariation ColorVariation { get => _colorVariation; set => _colorVariation = value; }

        /// <summary>The tint at one end of the noise.</summary>
        public Color HealthyColor { get => _healthyColor; set => _healthyColor = value; }

        /// <summary>The tint at the other end of the noise.</summary>
        public Color DryColor { get => _dryColor; set => _dryColor = value; }

        /// <summary>Whether instances cast sun shadows. Only <see cref="ShadowCastingMode.Off"/> turns them off.</summary>
        public ShadowCastingMode ShadowCasting { get => _shadowCasting; set => _shadowCasting = value; }

        /// <summary>Camera distance, in metres, up to which instances cast sun shadows.</summary>
        public float ShadowDistance { get => _shadowDistance; set => _shadowDistance = value; }

        /// <summary>How far the top leans per unit of wind strength, as a share of the plant's height.</summary>
        public float WindBend { get => _windBend; set => _windBend = value; }

        /// <summary>How much the wind's turbulence makes each plant flutter, 0 to 1.</summary>
        public float WindFlutter { get => _windFlutter; set => _windFlutter = value; }

        /// <summary>The number of LODs that will be drawn: those with a mesh, up to <see cref="MaxLods"/>.</summary>
        public int UsableLodCount
        {
            get
            {
                int count = 0;
                foreach (GrassMeshKindLod lod in Lods)
                {
                    if (lod == null || !lod.Mesh || count == MaxLods)
                    {
                        break;
                    }

                    count++;
                }

                return count;
            }
        }

        /// <summary>The distance at which the last LOD ends: nothing of this kind is drawn beyond it.</summary>
        public float DrawDistance
        {
            get
            {
                int count = UsableLodCount;
                return count == 0 ? 0f : Mathf.Max(0f, Lods[count - 1].EndDistance);
            }
        }

        /// <summary>
        /// Copies Unity's settings from a terrain detail prototype: the mesh (and the LODs of a LOD Group on its prefab),
        /// sizes, noise, alignment, jitter, density and colours. Returns null when the prototype draws no mesh.
        /// </summary>
        /// <param name="prototype">The terrain's detail prototype.</param>
        /// <param name="drawDistance">The terrain's detail distance, for the last LOD's end.</param>
        public static GrassMeshKind FromDetailPrototype(DetailPrototype prototype, float drawDistance)
        {
            if (prototype == null || !prototype.usePrototypeMesh || !prototype.prototype)
            {
                return null;
            }

            var kind = new GrassMeshKind
            {
                Name = prototype.prototype.name,
                Density = prototype.density,
                UseDensityScaling = prototype.useDensityScaling,
                MinWidth = prototype.minWidth,
                MaxWidth = prototype.maxWidth,
                MinHeight = prototype.minHeight,
                MaxHeight = prototype.maxHeight,
                NoiseSpread = prototype.noiseSpread,
                NoiseSeed = prototype.noiseSeed,
                AlignToGround = Mathf.Clamp01(prototype.alignToGround / 100f),
                PositionJitter = Mathf.Clamp01(prototype.positionJitter / 100f),
                // Unity tints only the non-instanced detail shaders with Healthy/Dry; instanced prototypes draw the material.
                ColorVariation = prototype.useInstancing ? GrassMeshColorVariation.None : GrassMeshColorVariation.HealthyDry,
                HealthyColor = prototype.healthyColor,
                DryColor = prototype.dryColor,
                ShadowDistance = Mathf.Min(drawDistance, 40f)
            };

            float distance = Mathf.Max(1f, drawDistance);
            LODGroup group = prototype.prototype.GetComponentInChildren<LODGroup>();
            if (group)
            {
                LOD[] lods = group.GetLODs();
                // Screen height falls as 1 / distance, so LOD i ends at k / h_i. The last LOD ends at the detail distance.
                float last = lods.Length > 0 ? Mathf.Max(lods[lods.Length - 1].screenRelativeTransitionHeight, 1e-4f) : 1f;
                for (int i = 0; i < lods.Length && kind.Lods.Count < MaxLods; i++)
                {
                    if (!TryGetMesh(lods[i].renderers, out Mesh mesh, out Material[] materials))
                    {
                        continue;
                    }

                    float end = distance * last / Mathf.Max(lods[i].screenRelativeTransitionHeight, 1e-4f);
                    kind.Lods.Add(new GrassMeshKindLod { Mesh = mesh, Materials = materials, EndDistance = Mathf.Clamp(end, 1f, distance) });
                }
            }

            if (kind.Lods.Count == 0)
            {
                MeshFilter filter = prototype.prototype.GetComponentInChildren<MeshFilter>();
                MeshRenderer renderer = filter ? filter.GetComponent<MeshRenderer>() : null;
                if (!filter || !filter.sharedMesh)
                {
                    return null;
                }

                kind.Lods.Add(new GrassMeshKindLod
                {
                    Mesh = filter.sharedMesh,
                    Materials = renderer ? renderer.sharedMaterials : Array.Empty<Material>(),
                    EndDistance = distance
                });
                if (renderer && renderer.shadowCastingMode == ShadowCastingMode.Off)
                {
                    kind.ShadowCasting = ShadowCastingMode.Off;
                }
            }

            // LOD ends must rise, and the last one is the detail distance.
            for (int i = 0; i < kind.Lods.Count; i++)
            {
                float minimum = i == 0 ? 1f : kind.Lods[i - 1].EndDistance + 1f;
                kind.Lods[i].EndDistance = i == kind.Lods.Count - 1 ? Mathf.Max(distance, minimum) : Mathf.Max(kind.Lods[i].EndDistance, minimum);
            }

            kind.CrossFadeWidth = Mathf.Min(6f, distance * 0.1f);
            return kind;
        }

        private static bool TryGetMesh(Renderer[] renderers, out Mesh mesh, out Material[] materials)
        {
            mesh = null;
            materials = null;
            if (renderers == null)
            {
                return false;
            }

            foreach (Renderer renderer in renderers)
            {
                if (renderer is MeshRenderer meshRenderer && meshRenderer.TryGetComponent(out MeshFilter filter) && filter.sharedMesh)
                {
                    mesh = filter.sharedMesh;
                    materials = meshRenderer.sharedMaterials;
                    return true;
                }
            }

            return false;
        }
    }
}
