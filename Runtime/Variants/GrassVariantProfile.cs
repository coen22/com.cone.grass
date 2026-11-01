using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace Cone.Grass
{
    /// <summary>The head a kind carries at its blade tip; None grows a plain blade.</summary>
    public enum GrassHeadShape
    {
        /// <summary>No head: a plain blade.</summary>
        None = 0,
        /// <summary>A ring of narrow petals round a disc, as a daisy or chamomile.</summary>
        Daisy = 1,
        /// <summary>Five broad petals in a shallow cup, as a buttercup or poppy.</summary>
        Cup = 2,
        /// <summary>A flat cluster of small florets, as yarrow or cow parsley.</summary>
        Umbel = 3,
        /// <summary>An upright spike of florets, as lavender or plantain.</summary>
        Spike = 4,
        /// <summary>A three-lobed leaf held flat, as clover; a herb rather than a flower.</summary>
        Clover = 5,
        /// <summary>The profile's own small mesh, tinted from the palette.</summary>
        Mesh = 6,
        /// <summary>A small soft round flower, as clover blossom or a pompom.</summary>
        Puff = 7
    }

    /// <summary>One kind of plant in a profile: its blade, its colour and its optional head.</summary>
    [Serializable]
    public sealed class GrassVariantKind
    {
        [SerializeField, FormerlySerializedAs("name")] private string _name = "Grass";
        /// <summary>A label for the kind in the inspector.</summary>
        public string Name { get => _name; set => _name = value; }

        [Header("Mix")]
        [Tooltip("Share of the roots that grow this kind, relative to the other kinds' weights.")]
        [Min(0f), SerializeField, FormerlySerializedAs("weight")] private float _weight = 1f;

        /// <summary>Share of the roots that grow this kind, relative to the other kinds' weights.</summary>
        public float Weight { get => _weight; set => _weight = value; }
        [Tooltip("Zero sprinkles this kind evenly; one gathers all of it into clusters inside drifts. Its share stays the same.")]
        [Range(0f, 1f), SerializeField, FormerlySerializedAs("clumping")] private float _clumping;
        /// <summary>Zero sprinkles this kind evenly; one gathers all of it into clusters inside drifts. Its share stays the same.</summary>
        public float Clumping { get => _clumping; set => _clumping = value; }
        [Tooltip("Fraction of the ground this kind's drifts cover, on the profile's Drift Size.")]
        [Range(0.01f, 0.99f), SerializeField, FormerlySerializedAs("driftArea")] private float _driftArea = 0.25f;
        /// <summary>Fraction of the ground this kind's drifts cover, on the profile's <see cref="GrassVariantProfile.ClumpSize"/>.</summary>
        public float DriftArea { get => _driftArea; set => _driftArea = value; }
        [Tooltip("Fraction of a drift its clusters cover, on the profile's Cluster Size: a few to a few dozen plants each with bare ground between. One keeps the whole drift.")]
        [Range(0.01f, 1f), SerializeField, FormerlySerializedAs("clusterArea")] private float _clusterArea = 1f;
        /// <summary>Fraction of a drift its clusters cover, on the profile's <see cref="GrassVariantProfile.ClusterSize"/>; one keeps the whole drift.</summary>
        public float ClusterArea { get => _clusterArea; set => _clusterArea = value; }

        [Header("Habitat")]
        [Tooltip("-1 grows on dry ground, 1 on wet ground (the profile's moisture field), 0 anywhere.")]
        [Range(-1f, 1f), SerializeField, FormerlySerializedAs("moisture")] private float _moisture;

        /// <summary>-1 grows on dry ground, 1 on wet ground (the profile's moisture field), 0 anywhere.</summary>
        public float Moisture { get => _moisture; set => _moisture = value; }
        [Tooltip("-1 grows on flat ground, 1 on slopes, 0 anywhere.")]
        [Range(-1f, 1f), SerializeField, FormerlySerializedAs("slope")] private float _slope;
        /// <summary>-1 grows on flat ground, 1 on slopes, 0 anywhere.</summary>
        public float Slope { get => _slope; set => _slope = value; }

        [Header("Blade")]
        [Tooltip("Blade height relative to the material's Grass Height.")]
        [Min(0f), SerializeField, FormerlySerializedAs("heightScale")] private float _heightScale = 1f;

        /// <summary>Blade height relative to the material's Grass Height.</summary>
        public float HeightScale { get => _heightScale; set => _heightScale = value; }
        [Tooltip("Blade width relative to the material's Grass Width.")]
        [Min(0f), SerializeField, FormerlySerializedAs("widthScale")] private float _widthScale = 1f;
        /// <summary>Blade width relative to the material's Grass Width.</summary>
        public float WidthScale { get => _widthScale; set => _widthScale = value; }
        [Tooltip("Curvature relative to the material's Grass Curving.")]
        [Min(0f), SerializeField, FormerlySerializedAs("curvingScale")] private float _curvingScale = 1f;
        /// <summary>Curvature relative to the material's Grass Curving.</summary>
        public float CurvingScale { get => _curvingScale; set => _curvingScale = value; }
        [Tooltip("0 tapers to a point like a grass blade; 1 keeps its width to the tip, a stem that holds a head.")]
        [Range(0f, 1f), SerializeField, FormerlySerializedAs("stemWidth")] private float _stemWidth;
        /// <summary>0 tapers to a point like a grass blade; 1 keeps its width to the tip, a stem that holds a head.</summary>
        public float StemWidth { get => _stemWidth; set => _stemWidth = value; }
        [Tooltip("Replaces the material's Grass Height Randomness for this kind; below zero keeps the material's. "
    + "A low value holds a flower's head at the height of the grass tips round it.")]
        [Range(-1f, 1f), SerializeField, FormerlySerializedAs("heightRandomness")] private float _heightRandomness = -1f;
        /// <summary>Replaces the material's Grass Height Randomness for this kind; below zero keeps the material's.</summary>
        public float HeightRandomness { get => _heightRandomness; set => _heightRandomness = value; }

        [Header("Blade colour")]
        [Tooltip("Multiplies the blade colour. The root still blends toward the matched ground colour."), SerializeField, FormerlySerializedAs("tint")]
        private Color _tint = Color.white;

        /// <summary>Multiplies the blade colour; the root still blends toward the matched ground colour.</summary>
        public Color Tint { get => _tint; set => _tint = value; }
        [Tooltip("The colour variation mixes the tint toward this one."), SerializeField, FormerlySerializedAs("variationTint")]
        private Color _variationTint = new Color(1.15f, 1.05f, 0.7f, 1f);
        /// <summary>The colour variation mixes the tint toward this one.</summary>
        public Color VariationTint { get => _variationTint; set => _variationTint = value; }
        [Tooltip("How far each plant's own random number moves its colour toward the variation tint.")]
        [Range(0f, 1f), SerializeField, FormerlySerializedAs("instanceVariation")] private float _instanceVariation = 0.25f;
        /// <summary>How far each plant's own random number moves its colour toward the variation tint.</summary>
        public float InstanceVariation { get => _instanceVariation; set => _instanceVariation = value; }
        [Tooltip("How far each clump's noise moves the colour toward the variation tint, so neighbouring plants drift together.")]
        [Range(0f, 1f), SerializeField, FormerlySerializedAs("clumpVariation")] private float _clumpVariation = 0.25f;
        /// <summary>How far each clump's noise moves the colour toward the variation tint, so neighbouring plants drift together.</summary>
        public float ClumpVariation { get => _clumpVariation; set => _clumpVariation = value; }

        [Header("Head"), SerializeField, FormerlySerializedAs("head")]
        private GrassHeadShape _head = GrassHeadShape.None;

        /// <summary>The head this kind carries at its blade tip.</summary>
        public GrassHeadShape Head { get => _head; set => _head = value; }
        [Tooltip("Head diameter in metres.")]
        [Min(0.001f), SerializeField, FormerlySerializedAs("headSize")] private float _headSize = 0.04f;
        /// <summary>Head diameter in metres.</summary>
        public float HeadSize { get => _headSize; set => _headSize = value; }
        [Tooltip("Each head's size varies by up to this fraction either way.")]
        [Range(0f, 1f), SerializeField, FormerlySerializedAs("headSizeRandomness")] private float _headSizeRandomness = 0.3f;
        /// <summary>Each head's size varies by up to this fraction either way.</summary>
        public float HeadSizeRandomness { get => _headSizeRandomness; set => _headSizeRandomness = value; }
        [Tooltip("Fraction of heads that face sideways rather than up toward the sun.")]
        [Range(0f, 1f), SerializeField, FormerlySerializedAs("sideways")] private float _sideways = 0.25f;
        /// <summary>Fraction of heads that face sideways rather than up toward the sun.</summary>
        public float Sideways { get => _sideways; set => _sideways = value; }
        [Tooltip("Petal colours; each head picks one."), SerializeField, FormerlySerializedAs("palette")]
        private Color[] _palette = { Color.white };
        /// <summary>Petal colours; each head picks one. The generation uses up to <see cref="GrassVariantProfile.MaximumPaletteColours"/>.</summary>
        public Color[] Palette { get => _palette; set => _palette = value; }
        [Tooltip("Zero picks each head's colour independently; one gives a whole drift the same colour.")]
        [Range(0f, 1f), SerializeField, FormerlySerializedAs("paletteClumping")] private float _paletteClumping = 0.6f;
        /// <summary>Zero picks each head's colour independently; one gives a whole drift the same colour.</summary>
        public float PaletteClumping { get => _paletteClumping; set => _paletteClumping = value; }
        [Tooltip("The disc or floret centre."), SerializeField, FormerlySerializedAs("centre")]
        private Color _centre = new Color(0.95f, 0.75f, 0.15f, 1f);
        /// <summary>The colour of the disc or floret centre.</summary>
        public Color Centre { get => _centre; set => _centre = value; }

        /// <summary>Whether this kind carries a head.</summary>
        public bool HasHead => _head != GrassHeadShape.None;

        internal void Sanitize()
        {
            _weight = Mathf.Max(0f, Finite(_weight, 1f));
            _clumping = Mathf.Clamp01(Finite(_clumping, 0f));
            _driftArea = Mathf.Clamp(Finite(_driftArea, 0.25f), 0.01f, 0.99f);
            _clusterArea = Mathf.Clamp(Finite(_clusterArea, 1f), 0.01f, 1f);
            _moisture = Mathf.Clamp(Finite(_moisture, 0f), -1f, 1f);
            _slope = Mathf.Clamp(Finite(_slope, 0f), -1f, 1f);
            _stemWidth = Mathf.Clamp01(Finite(_stemWidth, 0f));
            _heightRandomness = Mathf.Clamp(Finite(_heightRandomness, -1f), -1f, 1f);
            _sideways = Mathf.Clamp01(Finite(_sideways, 0.25f));
            _heightScale = Mathf.Clamp(Finite(_heightScale, 1f), 0f, 16f);
            _widthScale = Mathf.Clamp(Finite(_widthScale, 1f), 0f, 16f);
            _curvingScale = Mathf.Clamp(Finite(_curvingScale, 1f), 0f, 16f);
            _instanceVariation = Mathf.Clamp01(Finite(_instanceVariation, 0f));
            _clumpVariation = Mathf.Clamp01(Finite(_clumpVariation, 0f));
            _headSize = Mathf.Clamp(Finite(_headSize, 0.04f), 0.001f, 2f);
            _headSizeRandomness = Mathf.Clamp01(Finite(_headSizeRandomness, 0f));
            _paletteClumping = Mathf.Clamp01(Finite(_paletteClumping, 0f));
            if (_palette == null || _palette.Length == 0)
            {
                _palette = new[] { Color.white };
            }
        }

        private static float Finite(float value, float fallback) =>
            float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;
    }

    /// <summary>
    /// The kinds of plant an Infinite Grass Renderer grows: blades of several heights and widths, herbs and flowers with
    /// heads, mixed by weight, gathered into drifts and varied in colour per plant and per clump. Placement Areas can
    /// reweight the kinds locally. Without a profile the renderer grows its one material blade, as before.
    /// </summary>
    [CreateAssetMenu(menuName = "Cone/Grass/Variant Profile", fileName = "Grass Variant Profile")]
    public sealed class GrassVariantProfile : ScriptableObject
    {
        /// <summary>The most palette colours a kind's heads choose from.</summary>
        public const int MaximumPaletteColours = 4;

        [Tooltip("Changes every kind decision and drift without moving a root."), SerializeField, FormerlySerializedAs("seed")]
        private int _seed = 1;

        /// <summary>Changes every kind decision and drift without moving a root.</summary>
        public int Seed { get => _seed; set => _seed = value; }
        [Tooltip("Diameter of a drift or a colour clump, in metres.")]
        [Min(0.1f), SerializeField, FormerlySerializedAs("clumpSize")] private float _clumpSize = 12f;
        /// <summary>Diameter of a drift or a colour clump, in metres.</summary>
        public float ClumpSize { get => _clumpSize; set => _clumpSize = value; }
        [Tooltip("Diameter of a cluster of plants inside a drift, in metres.")]
        [Min(0.05f), SerializeField, FormerlySerializedAs("clusterSize")] private float _clusterSize = 0.7f;
        /// <summary>Diameter of a cluster of plants inside a drift, in metres.</summary>
        public float ClusterSize { get => _clusterSize; set => _clusterSize = value; }
        [Tooltip("Scale of the moisture field the kinds' habitat follows, in metres.")]
        [Min(1f), SerializeField, FormerlySerializedAs("habitatSize")] private float _habitatSize = 40f;
        /// <summary>Scale of the moisture field the kinds' habitat follows, in metres.</summary>
        public float HabitatSize { get => _habitatSize; set => _habitatSize = value; }
        [Tooltip("Fraction of heads that are closed buds.")]
        [Range(0f, 0.5f), SerializeField, FormerlySerializedAs("budFraction")] private float _budFraction = 0.12f;
        /// <summary>Fraction of heads that are closed buds.</summary>
        public float BudFraction { get => _budFraction; set => _budFraction = value; }
        [Tooltip("Fraction of heads gone to seed.")]
        [Range(0f, 0.5f), SerializeField, FormerlySerializedAs("seedFraction")] private float _seedFraction = 0.08f;
        /// <summary>Fraction of heads gone to seed.</summary>
        public float SeedFraction { get => _seedFraction; set => _seedFraction = value; }
        [Tooltip("Heads are drawn to this camera distance; beyond it a flower's blade tip carries its colour.")]
        [Min(1f), SerializeField, FormerlySerializedAs("headDrawDistance")] private float _headDrawDistance = 60f;
        /// <summary>Heads are drawn to this camera distance; beyond it a flower's blade tip carries its colour.</summary>
        public float HeadDrawDistance { get => _headDrawDistance; set => _headDrawDistance = value; }
        [Tooltip("Fraction of the head distance over which heads shrink away.")]
        [Range(0.01f, 1f), SerializeField, FormerlySerializedAs("headFade")] private float _headFade = 0.3f;
        /// <summary>Fraction of the head distance over which heads shrink away.</summary>
        public float HeadFade { get => _headFade; set => _headFade = value; }
        [Tooltip("How strongly a distant flower's tip takes its mean petal colour once its head has faded.")]
        [Range(0f, 1f), SerializeField, FormerlySerializedAs("distantTipColour")] private float _distantTipColour = 0.8f;
        /// <summary>How strongly a distant flower's tip takes its mean petal colour once its head has faded.</summary>
        public float DistantTipColour { get => _distantTipColour; set => _distantTipColour = value; }
        [Tooltip("The mesh used by kinds whose head is Mesh: about one unit across, pivot at the stem, +Y up."), SerializeField, FormerlySerializedAs("headMesh")]
        private Mesh _headMesh;
        /// <summary>The mesh used by kinds whose head is <see cref="GrassHeadShape.Mesh"/>: about one unit across, pivot at the stem, +Y up.</summary>
        public Mesh HeadMesh { get => _headMesh; set => _headMesh = value; }
        [SerializeField, FormerlySerializedAs("kinds")] private List<GrassVariantKind> _kinds = new List<GrassVariantKind>();
        /// <summary>The kinds of plant, in order; the renderer uses the first eight.</summary>
        public List<GrassVariantKind> Kinds { get => _kinds; set => _kinds = value; }

        [NonSerialized] private uint _revision = 1;
        /// <summary>Advances on every inspector or script change reported through <see cref="MarkDirty"/>.</summary>
        public uint Revision => _revision;

        /// <summary>The number of kinds the renderer uses: the list's length, at most eight.</summary>
        public int KindCount => _kinds == null ? 0 : Mathf.Min(_kinds.Count, GrassVariantSelection.MaximumKinds);

        /// <summary>
        /// Whether a used kind with a positive weight grows a mesh head (for <see cref="GrassHeadShape.Mesh"/>) or a
        /// procedural head (for any other shape, including <see cref="GrassHeadShape.None"/>).
        /// </summary>
        /// <param name="shape"><see cref="GrassHeadShape.Mesh"/> to ask about mesh heads; any other value asks about procedural heads.</param>
        public bool HasHeads(GrassHeadShape shape)
        {
            for (int k = 0; k < KindCount; k++)
            {
                if (_kinds[k] != null && _kinds[k].Weight > 0f && (shape == GrassHeadShape.Mesh ? _kinds[k].Head == GrassHeadShape.Mesh :
                        _kinds[k].HasHead && _kinds[k].Head != GrassHeadShape.Mesh))
                {
                    return true;
                }
            }

            return false;
        }

        private void OnValidate()
        {
            Sanitize();
            MarkDirty();
        }

        /// <summary>Advances <see cref="Revision"/> so renderers repack the profile; call after changing it from a script.</summary>
        public void MarkDirty()
        {
            unchecked { _revision++; }
        }

        internal void Sanitize()
        {
            _clumpSize = Mathf.Clamp(Finite(_clumpSize, 12f), 0.1f, 1000f);
            _clusterSize = Mathf.Clamp(Finite(_clusterSize, 0.7f), 0.05f, 100f);
            _habitatSize = Mathf.Clamp(Finite(_habitatSize, 40f), 1f, 10000f);
            _budFraction = Mathf.Clamp(Finite(_budFraction, 0.12f), 0f, 0.5f);
            _seedFraction = Mathf.Clamp(Finite(_seedFraction, 0.08f), 0f, 0.5f);
            _headDrawDistance = Mathf.Clamp(Finite(_headDrawDistance, 60f), 1f, 10000f);
            _headFade = Mathf.Clamp(Finite(_headFade, 0.3f), 0.01f, 1f);
            _distantTipColour = Mathf.Clamp01(Finite(_distantTipColour, 0.8f));
            if (_kinds == null)
            {
                _kinds = new List<GrassVariantKind>();
            }

            foreach (GrassVariantKind kind in _kinds)
            {
                kind?.Sanitize();
            }
        }

        /// <summary>Each kind's mix as the generation compute uses it; missing kinds have no weight.</summary>
        internal void GetMix(GrassVariantSelection.KindMix[] mix)
        {
            for (int k = 0; k < GrassVariantSelection.MaximumKinds; k++)
            {
                GrassVariantKind kind = k < KindCount ? _kinds[k] : null;
                mix[k] = kind == null ? new GrassVariantSelection.KindMix { DriftArea = 0.25f, ClusterArea = 1f } :
                    new GrassVariantSelection.KindMix
                    {
                        Weight = Mathf.Max(0f, kind.Weight),
                        Clumping = Mathf.Clamp01(kind.Clumping),
                        DriftArea = kind.DriftArea,
                        ClusterArea = kind.ClusterArea,
                        Moisture = kind.Moisture,
                        Slope = kind.Slope
                    };
            }
        }

        /// <summary>The profile's scales and hashed seed.</summary>
        internal GrassVariantSelection.Field Field => new GrassVariantSelection.Field
        {
            DriftSize = _clumpSize,
            ClusterSize = _clusterSize,
            HabitatSize = _habitatSize,
            Seed = GrassVariantSelection.ProfileSeed(_seed)
        };

        /// <summary>Whether any growing kind prefers flat ground or slopes, so the generation measures the slope.</summary>
        public bool UsesSlope()
        {
            for (int k = 0; k < KindCount; k++)
            {
                if (_kinds[k] != null && _kinds[k].Weight > 0f && _kinds[k].Slope != 0f)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The mean blade area over the mix relative to the material's blade, for the canopy's leaf area.</summary>
        /// <returns>The weighted mean of height scale times width scale, or one when no kind grows.</returns>
        public float MeanBladeAreaScale()
        {
            float total = 0f, area = 0f;
            for (int k = 0; k < KindCount; k++)
            {
                GrassVariantKind kind = _kinds[k];
                if (kind == null || kind.Weight <= 0f)
                {
                    continue;
                }

                total += kind.Weight;
                area += kind.Weight * kind.HeightScale * kind.WidthScale;
            }
            return total > 0f ? area / total : 1f;
        }

        /// <summary>The largest blade height, width and curvature scale, and the largest head, over kinds that grow.</summary>
        /// <returns>
        /// The maximum height, width and curving scales in xyz and the largest head extent in metres in w (zero without heads);
        /// (1, 1, 1, 0) when no kind grows.
        /// </returns>
        public Vector4 MaximumScales()
        {
            var result = new Vector4(1f, 1f, 1f, 0f);
            bool any = false;
            for (int k = 0; k < KindCount; k++)
            {
                GrassVariantKind kind = _kinds[k];
                if (kind == null || kind.Weight <= 0f)
                {
                    continue;
                }

                if (!any)
                {
                    result = new Vector4(0f, 0f, 0f, 0f);
                }

                any = true;
                result.x = Mathf.Max(result.x, kind.HeightScale);
                result.y = Mathf.Max(result.y, kind.WidthScale);
                result.z = Mathf.Max(result.z, kind.CurvingScale);
                if (kind.HasHead)
                {
                    result.w = Mathf.Max(result.w, kind.HeadSize * (1f + kind.HeadSizeRandomness) *
                        (kind.Head == GrassHeadShape.Spike ? 2f : 1f));
                }
            }
            return result;
        }

        /// <summary>
        /// A flowering lawn to start from: short, broad, bright blades over a darker undercoat, a broad herb and clover, and
        /// sparse single flowers (about one per one to one and a half square metres, unclumped) with heads of about 6 cm that
        /// sit at the lawn's tallest tips, in saturated pink, lilac, yellow and white.
        /// </summary>
        public void ResetToMeadow()
        {
            _seed = 1;
            _clumpSize = 12f;
            _clusterSize = 1.5f;
            _habitatSize = 40f;
            _budFraction = 0.05f;
            _seedFraction = 0.02f;
            _headDrawDistance = 50f;
            _headFade = 0.4f;
            // At range the flowers fade into the lawn, leaving faint dots.
            _distantTipColour = 0.35f;
            // The lawn's tips stand at 0.62 of the material height times 0.5 to 1 (its randomness); flowers at 0.62 times
            // 0.9 to 1 hold their heads level with the lawn's tallest tips, so a standing eye sees them over the grass round
            // them, never above the tallest blade.
            const float LawnHeight = 0.62f, FlowerRandomness = 0.1f, HeadRandomness = 0.1f;
            var stem = new Color(0.62f, 0.8f, 0.55f);
            _kinds = new List<GrassVariantKind>
            {
                new GrassVariantKind { Name = "Lawn grass", Weight = 0.62f, HeightScale = LawnHeight, WidthScale = 1.5f,
                    CurvingScale = 1.2f, Tint = new Color(0.95f, 1.15f, 0.75f), VariationTint = new Color(1.1f, 1.1f, 0.7f),
                    InstanceVariation = 0.2f, ClumpVariation = 0.25f },
                new GrassVariantKind { Name = "Undercoat", Weight = 0.3f, HeightScale = 0.35f, WidthScale = 1.8f,
                    Tint = new Color(0.55f, 0.68f, 0.45f), InstanceVariation = 0.2f, ClumpVariation = 0.2f },
                new GrassVariantKind { Name = "Broad herb", Weight = 0.06f, Clumping = 0.5f, DriftArea = 0.3f, ClusterArea = 0.5f,
                    HeightScale = 0.3f, WidthScale = 3f, CurvingScale = 2f, Tint = new Color(0.75f, 0.95f, 0.7f) },
                new GrassVariantKind { Name = "Clover", Weight = 0.015f, Clumping = 0.7f, DriftArea = 0.3f, ClusterArea = 0.5f,
                    Moisture = 0.3f, HeightScale = 0.25f, WidthScale = 0.5f, StemWidth = 0.6f,
                    Tint = new Color(0.8f, 0.95f, 0.8f), Head = GrassHeadShape.Clover, HeadSize = 0.04f,
                    Palette = new[] { new Color(0.28f, 0.5f, 0.2f), new Color(0.32f, 0.55f, 0.24f) },
                    Centre = new Color(0.55f, 0.7f, 0.45f) },
                // Flowers are unclumped singles: weight 0.0054 of the mix is about 0.84 heads per square metre at the
                // 0.08 m spacing the Alice grass uses.
                new GrassVariantKind { Name = "Meadow puff", Weight = 0.003f, Clumping = 0f,
                    HeightScale = LawnHeight, HeightRandomness = FlowerRandomness, WidthScale = 0.18f, StemWidth = 0.5f, Tint = stem,
                    Head = GrassHeadShape.Puff, HeadSize = 0.06f, HeadSizeRandomness = HeadRandomness, PaletteClumping = 0f,
                    Palette = new[]
                    {
                        new Color(1f, 0.38f, 0.68f), new Color(0.68f, 0.45f, 1f), new Color(1f, 0.84f, 0.08f),
                        new Color(1f, 1f, 1f)
                    },
                    Centre = new Color(0.95f, 0.85f, 0.9f) },
                new GrassVariantKind { Name = "Daisy", Weight = 0.0012f, Clumping = 0f, Slope = -0.3f,
                    HeightScale = LawnHeight, HeightRandomness = FlowerRandomness, WidthScale = 0.18f,
                    StemWidth = 0.5f, Tint = stem, Head = GrassHeadShape.Daisy, HeadSize = 0.062f, HeadSizeRandomness = HeadRandomness,
                    Palette = new[] { new Color(1f, 1f, 1f), new Color(1f, 0.85f, 0.92f) },
                    Centre = new Color(1f, 0.78f, 0.05f) },
                new GrassVariantKind { Name = "Buttercup", Weight = 0.0012f, Clumping = 0f, Moisture = 0.4f,
                    HeightScale = LawnHeight, HeightRandomness = FlowerRandomness, WidthScale = 0.18f,
                    StemWidth = 0.5f, Tint = stem, Head = GrassHeadShape.Cup, HeadSize = 0.056f, HeadSizeRandomness = HeadRandomness,
                    Palette = new[] { new Color(1f, 0.82f, 0f) }, Centre = new Color(0.95f, 0.6f, 0f) }
            };
            Sanitize();
            MarkDirty();
        }

        private void Reset() => ResetToMeadow();

        private static float Finite(float value, float fallback) =>
            float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;
    }
}
