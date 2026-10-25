using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// A profile's kinds packed as GrassVariants.hlsl reads them: every value relative to the neutral blade, so the zero
/// arrays of no profile grow the material's own blade. Colours are converted to the active colour space.
/// </summary>
public sealed class GrassVariantGpuData
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

    private GrassVariantProfile packedProfile;
    private uint packedRevision;
    private bool packedMesh, packedLinear, packed;

    private static readonly int ShapeId = Shader.PropertyToID("_GrassKindShape");
    private static readonly int TintId = Shader.PropertyToID("_GrassKindTint");
    private static readonly int VariationId = Shader.PropertyToID("_GrassKindVariation");
    private static readonly int HeadId = Shader.PropertyToID("_GrassKindHead");
    private static readonly int CentreId = Shader.PropertyToID("_GrassKindCentre");
    private static readonly int FarId = Shader.PropertyToID("_GrassKindFar");
    private static readonly int PaletteId = Shader.PropertyToID("_GrassKindPalette");
    private static readonly int SelectId = Shader.PropertyToID("_GrassKindSelect");
    private static readonly int DriftId = Shader.PropertyToID("_GrassKindDrift");
    private static readonly int HabitatId = Shader.PropertyToID("_GrassKindHabitat");
    private static readonly int StemId = Shader.PropertyToID("_GrassKindStem");
    private static readonly int ParametersId = Shader.PropertyToID("_GrassVariantParams");
    private static readonly int Parameters2Id = Shader.PropertyToID("_GrassVariantParams2");
    private static readonly int KindsId = Shader.PropertyToID("_GrassVariantKinds");
    private static readonly int SeedId = Shader.PropertyToID("_GrassVariantSeed");
    private static readonly int CanopyId = Shader.PropertyToID("_GrassVariantCanopy");

    /// <summary>Repacks when the profile, its revision, the mesh head's availability or the colour space changed.</summary>
    public bool Update(GrassVariantProfile profile, bool meshHeads)
    {
        bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
        uint revision = profile ? profile.Revision : 0u;
        if (packed && packedProfile == profile && packedRevision == revision && packedMesh == meshHeads && packedLinear == linear)
            return false;
        packed = true;
        packedProfile = profile;
        packedRevision = revision;
        packedMesh = meshHeads;
        packedLinear = linear;
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
            return;

        int count = profile.KindCount;
        var mixes = new GrassVariantSelection.KindMix[Kinds];
        profile.GetMix(mixes);
        float total = 0f;
        for (int k = 0; k < count; k++)
            total += mixes[k].Weight;
        bool quadHeads = false, anyMesh = false;
        for (int k = 0; k < count; k++)
        {
            GrassVariantKind kind = profile.kinds[k];
            if (kind == null)
                continue;
            int queue = !kind.HasHead || kind.weight <= 0f ? 0 : kind.head == GrassHeadShape.Mesh ? (meshHeads ? 2 : 0) : 1;
            quadHeads |= queue == 1;
            anyMesh |= queue == 2;
            Shape[k] = new Vector4(kind.heightScale - 1f, kind.widthScale - 1f, kind.curvingScale - 1f, queue);
            Color tint = Convert(kind.tint, linear), variation = Convert(kind.variationTint, linear);
            Tint[k] = new Vector4(1f - tint.r, 1f - tint.g, 1f - tint.b, kind.instanceVariation);
            Variation[k] = new Vector4(1f - variation.r, 1f - variation.g, 1f - variation.b, kind.clumpVariation);
            Head[k] = new Vector4(kind.headSize, kind.headSizeRandomness, (float)kind.head, kind.paletteClumping);
            int colours = Mathf.Clamp(kind.palette != null ? kind.palette.Length : 0, 1, GrassVariantProfile.MaximumPaletteColours);
            Color centre = Convert(kind.centre, linear);
            Centre[k] = new Vector4(centre.r, centre.g, centre.b, colours);
            Vector3 mean = Vector3.zero;
            for (int c = 0; c < colours; c++)
            {
                Color colour = kind.palette != null && c < kind.palette.Length ? Convert(kind.palette[c], linear) : Color.white;
                Palette[k * GrassVariantProfile.MaximumPaletteColours + c] = new Vector4(colour.r, colour.g, colour.b, 1f);
                mean += new Vector3(colour.r, colour.g, colour.b) / colours;
            }
            // Clover is a leaf: its distant tip keeps the blade colour.
            float far = queue > 0 && kind.head != GrassHeadShape.Clover ? profile.distantTipColour : 0f;
            Far[k] = new Vector4(mean.x, mean.y, mean.z, far);
            Vector4 mix = GrassVariantSelection.PackMix(mixes[k], total, out float driftArea, out float clusterArea);
            Select[k] = new Vector4(mix.x, mix.y, mix.z, 0f);
            if (mix.z > 0f)
            {
                Vector2 drift = GrassVariantSelection.DriftThresholds(driftArea);
                Vector2 cluster = GrassVariantSelection.ClusterThresholds(clusterArea);
                Drift[k] = new Vector4(drift.x, drift.y, cluster.x, cluster.y);
            }
            Habitat[k] = new Vector4(kind.moisture, kind.slope, 0f, 0f);
            Stem[k] = new Vector4(kind.stemWidth, 0f, 0f, 0f);
        }
        KindCount = count;
        HeadQueues = anyMesh ? 2 : quadHeads ? 1 : 0;
        Seed = unchecked((int)GrassVariantSelection.ProfileSeed(profile.seed));
        float distance = profile.headDrawDistance;
        Parameters = new Vector4(profile.clumpSize, profile.clusterSize, distance, distance * (1f - profile.headFade));
        Parameters2 = new Vector4(profile.habitatSize, profile.UsesSlope() ? 1f : 0f, profile.budFraction, profile.seedFraction);
        Canopy = profile.MeanBladeAreaScale() - 1f;
    }

    private static Color Convert(Color colour, bool linear) => linear ? colour.linear : colour;

    public void Apply(MaterialPropertyBlock properties)
    {
        properties.SetVectorArray(ShapeId, Shape);
        properties.SetVectorArray(TintId, Tint);
        properties.SetVectorArray(VariationId, Variation);
        properties.SetVectorArray(HeadId, Head);
        properties.SetVectorArray(CentreId, Centre);
        properties.SetVectorArray(FarId, Far);
        properties.SetVectorArray(PaletteId, Palette);
        properties.SetVectorArray(StemId, Stem);
        properties.SetVector(ParametersId, Parameters);
        properties.SetVector(Parameters2Id, Parameters2);
        properties.SetInteger(KindsId, KindCount);
        properties.SetInteger(SeedId, Seed);
        properties.SetFloat(CanopyId, Canopy);
    }

    public void Apply(ComputeCommandBuffer cmd, ComputeShader shader)
    {
        cmd.SetComputeVectorArrayParam(shader, ShapeId, Shape);
        cmd.SetComputeVectorArrayParam(shader, SelectId, Select);
        cmd.SetComputeVectorArrayParam(shader, DriftId, Drift);
        cmd.SetComputeVectorArrayParam(shader, HabitatId, Habitat);
        cmd.SetComputeVectorParam(shader, ParametersId, Parameters);
        cmd.SetComputeVectorParam(shader, Parameters2Id, Parameters2);
        cmd.SetComputeIntParam(shader, KindsId, KindCount);
        cmd.SetComputeIntParam(shader, SeedId, Seed);
    }

    /// <summary>The same values on a compute shader directly, for tests and tools.</summary>
    public void Apply(ComputeShader shader)
    {
        shader.SetVectorArray(ShapeId, Shape);
        shader.SetVectorArray(SelectId, Select);
        shader.SetVectorArray(DriftId, Drift);
        shader.SetVectorArray(HabitatId, Habitat);
        shader.SetVector(ParametersId, Parameters);
        shader.SetVector(Parameters2Id, Parameters2);
        shader.SetInt(KindsId, KindCount);
        shader.SetInt(SeedId, Seed);
    }
}
