#ifndef CONE_GRASS_VARIANTS_INCLUDED
#define CONE_GRASS_VARIANTS_INCLUDED

// Kinds of plant (GrassVariantProfile). GrassVariantSelection.cs evaluates the same functions on the CPU in the same
// float32 order; keep the two in step.
//
// Every per-kind value is stored relative to the neutral blade, so arrays nobody set (no profile, or a custom draw that
// binds none) grow today's single material blade unchanged.
#define GRASS_MAXIMUM_KINDS 8
#define GRASS_PALETTE_COLOURS 4

// x, y, z: blade height, width and curvature scale minus one; w: head queue (0 none, 1 quad-cross, 2 profile mesh).
float4 _GrassKindShape[GRASS_MAXIMUM_KINDS];
// rgb: one minus the tint; w: per-plant colour variation.
float4 _GrassKindTint[GRASS_MAXIMUM_KINDS];
// rgb: one minus the variation tint; w: per-clump colour variation.
float4 _GrassKindVariation[GRASS_MAXIMUM_KINDS];
// x: head diameter (m); y: its randomness; z: GrassHeadShape; w: palette clumping.
float4 _GrassKindHead[GRASS_MAXIMUM_KINDS];
// rgb: the disc or floret centre; w: palette colour count.
float4 _GrassKindCentre[GRASS_MAXIMUM_KINDS];
// rgb: the mean petal colour a distant tip takes; w: how strongly.
float4 _GrassKindFar[GRASS_MAXIMUM_KINDS];
float4 _GrassKindPalette[GRASS_MAXIMUM_KINDS * GRASS_PALETTE_COLOURS];
// GrassVariantSelection.PackMix: x normalized weight; y the share kept everywhere; z the share a drift cluster's core
// adds (zero when unclumped). Read by the generation compute only.
float4 _GrassKindSelect[GRASS_MAXIMUM_KINDS];
// xy: the drift noise's soft threshold; zw: the cluster noise's (GrassVariantSelection.DriftThresholds). Compute only.
float4 _GrassKindDrift[GRASS_MAXIMUM_KINDS];
// x: moisture preference; y: slope preference (-1 to 1). Compute only.
float4 _GrassKindHabitat[GRASS_MAXIMUM_KINDS];
// x: the blade's width kept at its tip (0 a pointed blade, 1 a straight stem).
float4 _GrassKindStem[GRASS_MAXIMUM_KINDS];
// x: drift size (m); y: cluster size (m); z: head draw distance; w: where heads start to shrink.
float4 _GrassVariantParams;
// x: habitat (moisture) size (m); y: 1 when a kind prefers flat ground or slopes; z: bud fraction; w: seed-head fraction.
float4 _GrassVariantParams2;
int _GrassVariantKinds;
int _GrassVariantSeed;
// The mix's mean blade area relative to the material blade, minus one (canopy occlusion).
float _GrassVariantCanopy;

#define GRASS_KIND_STREAM 0x3c6ef372u
#define GRASS_PALETTE_STREAM 0x1b873593u
#define GRASS_DRIFT_SALT 0x27d4eb2fu
#define GRASS_COLOUR_SALT 0x165667b1u
#define GRASS_CLUSTER_SALT 0x61c88647u
#define GRASS_MOISTURE_SALT 0x7feb352du

uint GrassVariantHash(uint value)
{
    value ^= value >> 16;
    value *= 0x85ebca6bu;
    value ^= value >> 13;
    value *= 0xc2b2ae35u;
    value ^= value >> 16;
    return value;
}

uint GrassVariantCellSeed(int2 cell)
{
    return GrassVariantHash(GrassVariantHash(asuint(cell.x) ^ 0x9e3779b9u) ^ asuint(cell.y));
}

float GrassVariantRandom(uint seed, uint stream)
{
    return (GrassVariantHash(seed ^ stream) >> 8) * (1.0 / 16777216.0);
}

// Smooth value noise in [0, 1) on a unit lattice.
float GrassVariantNoise(float2 position, uint seed)
{
    precise float2 lattice = floor(position);
    int2 cell = int2(lattice);
    precise float2 t = position - lattice;
    t = t * t * (3.0 - 2.0 * t);
    float a = GrassVariantRandom(GrassVariantCellSeed(cell), seed);
    float b = GrassVariantRandom(GrassVariantCellSeed(cell + int2(1, 0)), seed);
    float c = GrassVariantRandom(GrassVariantCellSeed(cell + int2(0, 1)), seed);
    float d = GrassVariantRandom(GrassVariantCellSeed(cell + int2(1, 1)), seed);
    precise float lower = a + (b - a) * t.x;
    precise float upper = c + (d - c) * t.x;
    return lower + (upper - lower) * t.y;
}

// How far a root is inside a kind's drifts and their clusters: zero outside, one in a cluster's core. Each threshold
// is a quantile of its own noise, so drifts and clusters cover fixed fractions of the ground and clumping moves a kind
// without changing its share.
float GrassVariantInDrift(float2 positionXZ, uint kind)
{
    float4 thresholds = _GrassKindDrift[kind];
    float drift = GrassVariantNoise(positionXZ / max(_GrassVariantParams.x, 0.01),
        (uint)_GrassVariantSeed ^ (GRASS_DRIFT_SALT * (kind + 1u)));
    float cluster = GrassVariantNoise(positionXZ / max(_GrassVariantParams.y, 0.01),
        (uint)_GrassVariantSeed ^ (GRASS_CLUSTER_SALT * (kind + 1u)));
    return saturate((drift - thresholds.x) / max(thresholds.y - thresholds.x, 1e-6)) *
        saturate((cluster - thresholds.z) / max(thresholds.w - thresholds.z, 1e-6));
}

// The shared moisture field in [0, 1] (GrassVariantSelection.Moisture).
float GrassVariantMoisture(float2 positionXZ)
{
    float noise = GrassVariantNoise(positionXZ / max(_GrassVariantParams2.x, 0.01), (uint)_GrassVariantSeed ^ GRASS_MOISTURE_SALT);
    float t = saturate((noise - 0.3) / 0.4);
    return t * t * (3.0 - 2.0 * t);
}

// A smooth per-clump number in [0, 1): neighbouring plants share their colour drift and palette.
float GrassVariantClump(float2 positionXZ)
{
    return GrassVariantNoise(positionXZ / max(_GrassVariantParams.x * 0.7, 0.01), (uint)_GrassVariantSeed ^ GRASS_COLOUR_SALT);
}

// Which kind a root grows (GrassVariantSelection.Shares and SelectKind). areaWeights scales each kind's weight for the
// Placement Areas over the root, all ones keeping the profile's mix; slope is 0 on flat ground and 1 near 35 degrees.
uint GrassSelectKind(float2 positionXZ, float slope, uint cellSeed, float4 areaWeights0, float4 areaWeights1)
{
    uint count = (uint)clamp(_GrassVariantKinds, 0, GRASS_MAXIMUM_KINDS);
    if (count <= 1u)
        return 0u;
    float moisture = GrassVariantMoisture(positionXZ);
    // Habitat and the areas' multipliers rescale the kinds they keep, so those share the root in proportion.
    float scales[GRASS_MAXIMUM_KINDS];
    float total = 0.0;
    [unroll]
    for (uint k = 0u; k < GRASS_MAXIMUM_KINDS; ++k)
    {
        // Select the vector first: FXC unrolls both sides of a ternary and rejects an out-of-range index.
        float4 group = k < 4u ? areaWeights0 : areaWeights1;
        float area = group[k & 3u];
        float2 habitat = _GrassKindHabitat[k].xy;
        float fit = max(0.0, 1.0 + habitat.x * (2.0 * moisture - 1.0)) * max(0.0, 1.0 + habitat.y * (2.0 * slope - 1.0));
        scales[k] = k < count ? saturate(area) * fit : 0.0;
        total += max(_GrassKindSelect[k].x, 0.0) * scales[k];
    }
    if (total <= 0.0)
        return 0u;
    float weights[GRASS_MAXIMUM_KINDS];
    float clumpedWeight = 0.0;
    float clumpedShare = 0.0;
    [unroll]
    for (uint j = 0u; j < GRASS_MAXIMUM_KINDS; ++j)
    {
        float4 mix = _GrassKindSelect[j];
        float scale = scales[j] / total;
        if (mix.z > 0.0 && scale > 0.0)
        {
            weights[j] = (mix.y + mix.z * GrassVariantInDrift(positionXZ, j)) * scale;
            clumpedWeight += mix.x * scale;
            clumpedShare += weights[j];
        }
        else
            weights[j] = -mix.x * scale;
    }
    bool fillsAll = clumpedWeight >= 1.0 - 1e-6 || clumpedShare >= 1.0;
    float clumpedScale = fillsAll ? 1.0 / max(clumpedShare, 1e-6) : 1.0;
    float fillScale = fillsAll ? 0.0 : (1.0 - clumpedShare) / max(1.0 - clumpedWeight, 1e-6);

    float random = GrassVariantRandom(cellSeed ^ (uint)_GrassVariantSeed, GRASS_KIND_STREAM);
    float cumulative = 0.0;
    uint selected = 0u;
    bool found = false;
    [unroll]
    for (uint i = 0u; i < GRASS_MAXIMUM_KINDS; ++i)
    {
        float share = weights[i] < 0.0 ? -weights[i] * fillScale : weights[i] * clumpedScale;
        if (!found && share > 0.0)
        {
            selected = i;
            cumulative += share;
            found = random < cumulative;
        }
    }
    return selected;
}

// How much of a head shows: it shrinks away once it covers under about three pixels, so it never sparkles as a speck,
// and over the last stretch of the head draw distance. The blade tip takes over its colour as it goes.
float GrassHeadVisibility(float size, float cameraDistance, float worldUnitsPerPixel)
{
    float pixels = size / max(worldUnitsPerPixel, 1e-6);
    return smoothstep(1.5, 3.5, pixels) * (1.0 - smoothstep(_GrassVariantParams.w, _GrassVariantParams.z, cameraDistance));
}

// A root's w: coverage + 2 kind. Coverage lies in (0, 1], so kind zero is exactly the earlier layout.
float GrassPackKind(float coverage, uint kind)
{
    return saturate(coverage) + 2.0 * (float)min(kind, GRASS_MAXIMUM_KINDS - 1u);
}

uint GrassUnpackKind(float w)
{
    return (uint)clamp(floor(w * 0.5), 0.0, GRASS_MAXIMUM_KINDS - 1.0);
}

float GrassUnpackCoverage(float w)
{
    return saturate(w - 2.0 * (float)GrassUnpackKind(w));
}

#endif
