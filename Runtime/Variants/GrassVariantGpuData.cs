using UnityEngine;
using UnityEngine.Rendering;

namespace Cone.Grass
{
    /// <summary>
    /// A profile's kinds packed as GrassVariants.hlsl reads them: every value relative to the neutral blade, so the zero
    /// arrays of no profile grow the material's own blade. Colours are converted to the active colour space.
    /// </summary>
    internal sealed class GrassVariantGpuData
    {
        public const int Kinds = GrassVariantSelection.MaximumKinds;
        public const int PaletteSize = Kinds * GrassVariantProfile.MaximumPaletteColours;

        public readonly Vector4[] Shape = new Vector4[Kinds];
        public readonly Vector4[] Tint = new Vector4[Kinds];
        public readonly Vector4[] Variation = new Vector4[Kinds];
        public readonly Vector4[] Head = new Vector4[Kinds];
        public readonly Vector4[] Centre = new Vector4[Kinds];
        public readonly Vector4[] Far = new Vector4[Kinds];
        public readonly Vector4[] Palette = new Vector4[PaletteSize];
        public readonly Vector4[] Select = new Vector4[Kinds];
        public readonly Vector4[] Drift = new Vector4[Kinds];
        public readonly Vector4[] Habitat = new Vector4[Kinds];
        public readonly Vector4[] Stem = new Vector4[Kinds];
        public Vector4 Parameters, Parameters2;
        public int KindCount;
        public int Seed;
        public float Canopy;
        /// <summary>Head queues the generation fills: 0, 1 (quad-cross) or 2 (quad-cross and mesh).</summary>
        public int HeadQueues;

        private GrassVariantProfile _packedProfile;
        private uint _packedRevision;
        private bool _packedMesh, _packedLinear, _packed;

        private static readonly int s_shapeId = Shader.PropertyToID("_GrassKindShape");
        private static readonly int s_tintId = Shader.PropertyToID("_GrassKindTint");
        private static readonly int s_variationId = Shader.PropertyToID("_GrassKindVariation");
        private static readonly int s_headId = Shader.PropertyToID("_GrassKindHead");
        private static readonly int s_centreId = Shader.PropertyToID("_GrassKindCentre");
        private static readonly int s_farId = Shader.PropertyToID("_GrassKindFar");
        private static readonly int s_paletteId = Shader.PropertyToID("_GrassKindPalette");
        private static readonly int s_selectId = Shader.PropertyToID("_GrassKindSelect");
        private static readonly int s_driftId = Shader.PropertyToID("_GrassKindDrift");
        private static readonly int s_habitatId = Shader.PropertyToID("_GrassKindHabitat");
        private static readonly int s_stemId = Shader.PropertyToID("_GrassKindStem");
        private static readonly int s_parametersId = Shader.PropertyToID("_GrassVariantParams");
        private static readonly int s_parameters2Id = Shader.PropertyToID("_GrassVariantParams2");
        private static readonly int s_kindsId = Shader.PropertyToID("_GrassVariantKinds");
        private static readonly int s_seedId = Shader.PropertyToID("_GrassVariantSeed");
        private static readonly int s_canopyId = Shader.PropertyToID("_GrassVariantCanopy");

        /// <summary>Repacks when the profile, its revision, the mesh head's availability or the colour space changed.</summary>
        public bool Update(GrassVariantProfile profile, bool meshHeads)
        {
            bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            uint revision = profile ? profile.Revision : 0u;
            if (_packed && _packedProfile == profile && _packedRevision == revision && _packedMesh == meshHeads && _packedLinear == linear)
            {
                return false;
            }

            _packed = true;
            _packedProfile = profile;
            _packedRevision = revision;
            _packedMesh = meshHeads;
            _packedLinear = linear;
            Pack(profile, meshHeads, linear);
            return true;
        }

        private void Pack(GrassVariantProfile profile, bool meshHeads, bool linear)
        {
            System.Array.Clear(Shape, 0, Shape.Length);
            System.Array.Clear(Tint, 0, Tint.Length);
            System.Array.Clear(Variation, 0, Variation.Length);
            System.Array.Clear(Head, 0, Head.Length);
            System.Array.Clear(Centre, 0, Centre.Length);
            System.Array.Clear(Far, 0, Far.Length);
            System.Array.Clear(Palette, 0, Palette.Length);
            System.Array.Clear(Select, 0, Select.Length);
            System.Array.Clear(Drift, 0, Drift.Length);
            System.Array.Clear(Habitat, 0, Habitat.Length);
            System.Array.Clear(Stem, 0, Stem.Length);
            Parameters = Parameters2 = Vector4.zero;
            KindCount = 0;
            Seed = 0;
            Canopy = 0f;
            HeadQueues = 0;
            // One kind is the material blade scaled; the generation only selects between two or more.
            if (!profile || profile.KindCount < 1)
            {
                return;
            }

            int count = profile.KindCount;
            var mixes = new GrassVariantSelection.KindMix[Kinds];
            profile.GetMix(mixes);
            float total = 0f;
            for (int k = 0; k < count; k++)
            {
                total += mixes[k].Weight;
            }

            bool quadHeads = false, anyMesh = false;
            for (int k = 0; k < count; k++)
            {
                GrassVariantKind kind = profile.Kinds[k];
                if (kind == null)
                {
                    continue;
                }

                int queue = !kind.HasHead || kind.Weight <= 0f ? 0 : kind.Head == GrassHeadShape.Mesh ? (meshHeads ? 2 : 0) : 1;
                quadHeads |= queue == 1;
                anyMesh |= queue == 2;
                Shape[k] = new Vector4(kind.HeightScale - 1f, kind.WidthScale - 1f, kind.CurvingScale - 1f, queue);
                Color tint = Convert(kind.Tint, linear), variation = Convert(kind.VariationTint, linear);
                Tint[k] = new Vector4(1f - tint.r, 1f - tint.g, 1f - tint.b, kind.InstanceVariation);
                Variation[k] = new Vector4(1f - variation.r, 1f - variation.g, 1f - variation.b, kind.ClumpVariation);
                Head[k] = new Vector4(kind.HeadSize, kind.HeadSizeRandomness, (float)kind.Head, kind.PaletteClumping);
                int colours = Mathf.Clamp(kind.Palette != null ? kind.Palette.Length : 0, 1, GrassVariantProfile.MaximumPaletteColours);
                Color centre = Convert(kind.Centre, linear);
                Centre[k] = new Vector4(centre.r, centre.g, centre.b, colours);
                Vector3 mean = Vector3.zero;
                for (int c = 0; c < colours; c++)
                {
                    Color colour = kind.Palette != null && c < kind.Palette.Length ? Convert(kind.Palette[c], linear) : Color.white;
                    Palette[k * GrassVariantProfile.MaximumPaletteColours + c] = new Vector4(colour.r, colour.g, colour.b, 1f);
                    mean += new Vector3(colour.r, colour.g, colour.b) / colours;
                }
                // Clover is a leaf: its distant tip keeps the blade colour.
                float far = queue > 0 && kind.Head != GrassHeadShape.Clover ? profile.DistantTipColour : 0f;
                Far[k] = new Vector4(mean.x, mean.y, mean.z, far);
                Vector4 mix = GrassVariantSelection.PackMix(mixes[k], total, out float driftArea, out float clusterArea);
                Select[k] = new Vector4(mix.x, mix.y, mix.z, 0f);
                if (mix.z > 0f)
                {
                    Vector2 drift = GrassVariantSelection.DriftThresholds(driftArea);
                    Vector2 cluster = GrassVariantSelection.ClusterThresholds(clusterArea);
                    Drift[k] = new Vector4(drift.x, drift.y, cluster.x, cluster.y);
                }
                Habitat[k] = new Vector4(kind.Moisture, kind.Slope, 0f, 0f);
                // y: the kind's own height randomness plus one, zero for the material's; z: its sideways heads.
                Stem[k] = new Vector4(kind.StemWidth, kind.HeightRandomness >= 0f ? kind.HeightRandomness + 1f : 0f, kind.Sideways, 0f);
            }
            KindCount = count;
            HeadQueues = anyMesh ? 2 : quadHeads ? 1 : 0;
            Seed = unchecked((int)GrassVariantSelection.ProfileSeed(profile.Seed));
            float distance = profile.HeadDrawDistance;
            Parameters = new Vector4(profile.ClumpSize, profile.ClusterSize, distance, distance * (1f - profile.HeadFade));
            Parameters2 = new Vector4(profile.HabitatSize, profile.UsesSlope() ? 1f : 0f, profile.BudFraction, profile.SeedFraction);
            Canopy = profile.MeanBladeAreaScale() - 1f;
        }

        private static Color Convert(Color colour, bool linear) => linear ? colour.linear : colour;

        public void Apply(MaterialPropertyBlock properties)
        {
            properties.SetVectorArray(s_shapeId, Shape);
            properties.SetVectorArray(s_tintId, Tint);
            properties.SetVectorArray(s_variationId, Variation);
            properties.SetVectorArray(s_headId, Head);
            properties.SetVectorArray(s_centreId, Centre);
            properties.SetVectorArray(s_farId, Far);
            properties.SetVectorArray(s_paletteId, Palette);
            properties.SetVectorArray(s_stemId, Stem);
            properties.SetVector(s_parametersId, Parameters);
            properties.SetVector(s_parameters2Id, Parameters2);
            properties.SetInteger(s_kindsId, KindCount);
            properties.SetInteger(s_seedId, Seed);
            properties.SetFloat(s_canopyId, Canopy);
        }

        public void Apply(ComputeCommandBuffer cmd, ComputeShader shader)
        {
            cmd.SetComputeVectorArrayParam(shader, s_shapeId, Shape);
            cmd.SetComputeVectorArrayParam(shader, s_selectId, Select);
            cmd.SetComputeVectorArrayParam(shader, s_driftId, Drift);
            cmd.SetComputeVectorArrayParam(shader, s_habitatId, Habitat);
            cmd.SetComputeVectorParam(shader, s_parametersId, Parameters);
            cmd.SetComputeVectorParam(shader, s_parameters2Id, Parameters2);
            cmd.SetComputeIntParam(shader, s_kindsId, KindCount);
            cmd.SetComputeIntParam(shader, s_seedId, Seed);
        }

        /// <summary>The same values on a compute shader directly, for tests and tools.</summary>
        public void Apply(ComputeShader shader)
        {
            shader.SetVectorArray(s_shapeId, Shape);
            shader.SetVectorArray(s_selectId, Select);
            shader.SetVectorArray(s_driftId, Drift);
            shader.SetVectorArray(s_habitatId, Habitat);
            shader.SetVector(s_parametersId, Parameters);
            shader.SetVector(s_parameters2Id, Parameters2);
            shader.SetInt(s_kindsId, KindCount);
            shader.SetInt(s_seedId, Seed);
        }
    }
}
