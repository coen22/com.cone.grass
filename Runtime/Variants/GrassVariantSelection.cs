using System;
using UnityEngine;

/// <summary>
/// The CPU twin of GrassVariants.hlsl: which kind a root grows, from its cell seed, its position and the
/// mixing weights. The generation compute evaluates the same functions in the same float32 order, so tests
/// and tools can predict the GPU population; a root on an exact noise-lattice line may still round apart.
/// </summary>
/// <remarks>
/// A clumped kind keeps part of its weight everywhere and puts the rest into drifts: the places where its own smooth
/// noise lies in the top <c>driftArea</c> of its values. The thresholds come from the noise's measured distribution,
/// so the drifts cover that fraction of the ground and the kind's share stays its weight however strong the clumping.
/// </remarks>
public static class GrassVariantSelection
{
    /// <summary>At most this many kinds share one renderer.</summary>
    public const int MaximumKinds = 8;
    /// <summary>Fraction of the drift band, in quantiles, over which a drift's edge softens.</summary>
    public const float DriftEdge = 0.5f;

    // Streams of the per-root random numbers. They differ from the generation's jitter, density and LOD streams.
    public const uint KindStream = 0x3c6ef372u;
    public const uint PaletteStream = 0x1b873593u;
    public const uint DriftSalt = 0x27d4eb2fu;
    public const uint ColourSalt = 0x165667b1u;
    public const uint ClusterSalt = 0x61c88647u;
    public const uint MoistureSalt = 0x7feb352du;

    private const int DistributionSamples = 1 << 18;
    private static readonly Lazy<Distribution> noiseDistribution = new Lazy<Distribution>(() => new Distribution());

    public static uint Hash(uint value)
    {
        unchecked
        {
            value ^= value >> 16;
            value *= 0x85ebca6bu;
            value ^= value >> 13;
            value *= 0xc2b2ae35u;
            value ^= value >> 16;
            return value;
        }
    }

    /// <summary>The generation compute's cell seed: signed coordinate bits are preserved.</summary>
    public static uint CellSeed(int x, int z) => Hash(Hash(unchecked((uint)x) ^ 0x9e3779b9u) ^ unchecked((uint)z));

    /// <summary>The generation compute's stream random in [0, 1): the upper 24 bits are exact in float32.</summary>
    public static float Random(uint seed, uint stream) => (Hash(seed ^ stream) >> 8) * (1f / 16777216f);

    /// <summary>The root a cell grows before density and culling, in the compute's float32 order.</summary>
    public static Vector2 RootXZ(int x, int z, float spacing)
    {
        uint seed = CellSeed(x, z);
        float jitterX = Random(seed, 0xa511e9b3u) - 0.5f;
        float jitterZ = Random(seed, 0x63d83595u) - 0.5f;
        return new Vector2((x + jitterX) * spacing, (z + jitterZ) * spacing);
    }

    /// <summary>The profile seed mixed into every variant stream.</summary>
    public static uint ProfileSeed(int seed) => Hash(unchecked((uint)seed) ^ 0x68bc21ebu);

    /// <summary>Smooth value noise in [0, 1) on a unit lattice, with its own seed.</summary>
    public static float Noise(Vector2 position, uint seed)
    {
        float fx = Mathf.Floor(position.x), fz = Mathf.Floor(position.y);
        int ix = (int)fx, iz = (int)fz;
        float tx = position.x - fx, tz = position.y - fz;
        tx = tx * tx * (3f - 2f * tx);
        tz = tz * tz * (3f - 2f * tz);
        float a = Random(CellSeed(ix, iz), seed);
        float b = Random(CellSeed(ix + 1, iz), seed);
        float c = Random(CellSeed(ix, iz + 1), seed);
        float d = Random(CellSeed(ix + 1, iz + 1), seed);
        float lower = a + (b - a) * tx;
        float upper = c + (d - c) * tx;
        return lower + (upper - lower) * tz;
    }

    /// <summary>A kind's drift noise at a root, on the profile's clump scale.</summary>
    public static float DriftNoise(Vector2 positionXZ, float clumpSize, uint profileSeed, int kind) =>
        Noise(positionXZ / Mathf.Max(clumpSize, 0.01f), profileSeed ^ unchecked(DriftSalt * (uint)(kind + 1)));

    /// <summary>How far a drift noise value is inside the drift, from zero outside to one in its core.</summary>
    public static float InDrift(float noise, Vector2 thresholds) =>
        Mathf.Clamp01((noise - thresholds.x) / Mathf.Max(thresholds.y - thresholds.x, 1e-6f));

    /// <summary>
    /// The drift noise's soft threshold for drifts covering <paramref name="area"/> of the ground: the mean of
    /// <see cref="InDrift"/> over the noise's distribution is exactly that fraction, within its sampling.
    /// </summary>
    public static Vector2 DriftThresholds(float area)
    {
        area = Mathf.Clamp(area, 0.01f, 0.99f);
        lock (thresholdCache)
        {
            if (!thresholdCache.TryGetValue(area, out Vector2 thresholds))
            {
                thresholds = noiseDistribution.Value.Thresholds(area);
                thresholdCache.Add(area, thresholds);
            }
            return thresholds;
        }
    }

    private static readonly System.Collections.Generic.Dictionary<float, Vector2> thresholdCache =
        new System.Collections.Generic.Dictionary<float, Vector2>();

    /// <summary>The smallest drift area a kind can use: inside a drift its share may not exceed one.</summary>
    public static float MinimumDriftArea(float weight, float clumping) =>
        Mathf.Clamp(clumping * weight / Mathf.Max(1f - weight * (1f - clumping), 1e-6f), 0.01f, 0.99f);

    /// <summary>A kind's mixing settings: the profile weight, how much of it gathers into drifts, the drifts' share of the
    /// ground, the clusters' share of a drift, and its habitat preferences (-1 to 1; 0 is indifferent).</summary>
    public struct KindMix
    {
        public float Weight, Clumping, DriftArea, ClusterArea, Moisture, Slope;
    }

    /// <summary>The profile's spatial scales (metres) and its hashed seed (<see cref="ProfileSeed"/>).</summary>
    public struct Field
    {
        public float DriftSize, ClusterSize, HabitatSize;
        public uint Seed;
    }

    /// <summary>
    /// A kind's packed mix, as the generation compute reads it: x its normalized weight, y the share it keeps everywhere,
    /// z the share a drift cluster's core adds (zero when unclumped); w unused. Areas are raised so z stays under one.
    /// </summary>
    public static Vector4 PackMix(KindMix kind, float total, out float driftArea, out float clusterArea)
    {
        float normalized = total > 0f ? Mathf.Max(kind.Weight, 0f) / total : 0f;
        float amount = Mathf.Clamp01(kind.Clumping);
        driftArea = Mathf.Clamp(kind.DriftArea, 0.01f, 0.99f);
        clusterArea = Mathf.Clamp(kind.ClusterArea, 0.01f, 1f);
        if (amount <= 0f || normalized <= 0f)
            return new Vector4(normalized, normalized, 0f, 0f);
        // Inside a cluster in a drift the kind may hold at most every root: widen the clusters first, then the drifts.
        float needed = MinimumDriftArea(normalized, amount);
        if (driftArea * clusterArea < needed)
            clusterArea = Mathf.Min(1f, needed / driftArea);
        if (driftArea * clusterArea < needed)
            driftArea = Mathf.Min(0.99f, needed / clusterArea);
        return new Vector4(normalized, normalized * (1f - amount), amount * normalized / (driftArea * clusterArea), 0f);
    }

    /// <summary>The cluster noise's threshold; a cluster area of one keeps the whole drift.</summary>
    public static Vector2 ClusterThresholds(float clusterArea) =>
        clusterArea >= 0.99f ? new Vector2(-1f, 0f) : DriftThresholds(clusterArea);

    /// <summary>How far a root is inside a kind's drifts and their clusters, from zero to one.</summary>
    public static float InDrift(Vector2 positionXZ, in Field field, int kind, Vector2 drift, Vector2 cluster)
    {
        float low = InDrift(DriftNoise(positionXZ, field.DriftSize, field.Seed, kind), drift);
        float high = InDrift(Noise(positionXZ / Mathf.Max(field.ClusterSize, 0.01f), field.Seed ^ unchecked(ClusterSalt * (uint)(kind + 1))), cluster);
        return low * high;
    }

    /// <summary>The shared moisture field in [0, 1]: low ground-water patches on the habitat scale.</summary>
    public static float Moisture(Vector2 positionXZ, in Field field)
    {
        float noise = Noise(positionXZ / Mathf.Max(field.HabitatSize, 0.01f), field.Seed ^ MoistureSalt);
        float t = Mathf.Clamp01((noise - 0.3f) / 0.4f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>A kind's habitat factor from the moisture (0 dry to 1 wet) and the slope (0 flat to 1 steep) at a root.</summary>
    public static float Habitat(in KindMix kind, float moisture, float slope) =>
        Mathf.Max(0f, 1f + kind.Moisture * (2f * moisture - 1f)) * Mathf.Max(0f, 1f + kind.Slope * (2f * slope - 1f));

    /// <summary>
    /// The share of each kind at a root. Weights are normalized; a clumped kind keeps (1 - clumping) of its share
    /// everywhere and puts the rest into clusters inside drifts, which cover DriftArea * ClusterArea of the ground, and the
    /// unclumped kinds fill what the drifts leave, in proportion to their weights. Habitat and the Placement Areas'
    /// multipliers (<paramref name="areaWeights"/>, null for none) rescale the kinds locally; the kinds kept share the root.
    /// <paramref name="slope"/> is 0 on flat ground and 1 at about 35 degrees.
    /// </summary>
    public static void Shares(Vector2 positionXZ, float slope, in Field field, KindMix[] kinds, int kindCount,
        float[] shares, float[] areaWeights = null)
    {
        kindCount = Mathf.Clamp(kindCount, 0, MaximumKinds);
        float profileTotal = 0f;
        for (int k = 0; k < kindCount; k++)
            profileTotal += Mathf.Max(kinds[k].Weight, 0f);
        float moisture = Moisture(positionXZ, field);
        float total = 0f;
        for (int k = 0; k < kindCount; k++)
        {
            shares[k] = AreaWeight(areaWeights, k) * Habitat(kinds[k], moisture, slope);
            total += profileTotal > 0f ? Mathf.Max(kinds[k].Weight, 0f) / profileTotal * shares[k] : 0f;
        }
        if (total <= 0f)
        {
            for (int k = 0; k < kindCount; k++)
                shares[k] = k == 0 ? 1f : 0f;
            return;
        }
        float clumpedWeight = 0f, clumpedShare = 0f;
        for (int k = 0; k < kindCount; k++)
        {
            Vector4 mix = PackMix(kinds[k], profileTotal, out float driftArea, out float clusterArea);
            float scale = shares[k] / total;
            if (mix.z > 0f && scale > 0f)
            {
                float inside = InDrift(positionXZ, field, k, DriftThresholds(driftArea), ClusterThresholds(clusterArea));
                shares[k] = (mix.y + mix.z * inside) * scale;
                clumpedWeight += mix.x * scale;
                clumpedShare += shares[k];
            }
            else
                shares[k] = -mix.x * scale;
        }
        // Unclumped kinds are marked negative above until the drifts' total is known.
        bool fillsAll = clumpedWeight >= 1f - 1e-6f || clumpedShare >= 1f;
        float clumpedScale = fillsAll ? 1f / Mathf.Max(clumpedShare, 1e-6f) : 1f;
        float fillScale = fillsAll ? 0f : (1f - clumpedShare) / Mathf.Max(1f - clumpedWeight, 1e-6f);
        for (int k = 0; k < kindCount; k++)
            shares[k] = shares[k] < 0f ? -shares[k] * fillScale : shares[k] * clumpedScale;
    }

    private static float AreaWeight(float[] areaWeights, int kind) =>
        areaWeights == null ? 1f : Mathf.Clamp01(kind < areaWeights.Length ? areaWeights[kind] : 1f);

    /// <summary>The kind a root grows: the cumulative shares against the root's own random number.</summary>
    public static int SelectKind(float[] shares, int kindCount, float random)
    {
        float cumulative = 0f;
        int last = 0;
        for (int k = 0; k < kindCount; k++)
        {
            if (shares[k] <= 0f)
                continue;
            last = k;
            cumulative += shares[k];
            if (random < cumulative)
                return k;
        }
        // Rounding can leave the sum a hair under one.
        return last;
    }

    /// <summary>The kind the root of a generation cell grows; <paramref name="shares"/> is scratch of at least the kind count.</summary>
    public static int SelectKind(int cellX, int cellZ, float spacing, in Field field, KindMix[] kinds, int kindCount,
        float[] shares, float slope = 0f, float[] areaWeights = null)
    {
        Shares(RootXZ(cellX, cellZ, spacing), slope, field, kinds, kindCount, shares, areaWeights);
        return SelectKind(shares, kindCount, Random(CellSeed(cellX, cellZ) ^ field.Seed, KindStream));
    }

    /// <summary>Packs a kind beside coverage in a root's w: coverage + 2 kind, so kind zero keeps today's layout.</summary>
    public static float PackKind(float coverage, int kind) => Mathf.Clamp01(coverage) + 2f * Mathf.Clamp(kind, 0, MaximumKinds - 1);

    public static int UnpackKind(float w) => Mathf.Clamp(Mathf.FloorToInt(w * 0.5f), 0, MaximumKinds - 1);

    public static float UnpackCoverage(float w) => Mathf.Clamp01(w - 2f * UnpackKind(w));

    // The noise's distribution over the ground: one value from each of 262,144 lattice cells, sorted, with prefix sums
    // so the mean of a soft threshold is exact over the sample. The seed and cells are fixed, so every run agrees.
    private sealed class Distribution
    {
        private readonly float[] sorted = new float[DistributionSamples];
        private readonly double[] prefix = new double[DistributionSamples + 1];

        public Distribution()
        {
            uint state = 0x2545f491u;
            for (int i = 0; i < DistributionSamples; i++)
            {
                state = Hash(state + 0x9e3779b9u);
                float u = (state >> 8) * (1f / 16777216f);
                state = Hash(state + 0x9e3779b9u);
                float v = (state >> 8) * (1f / 16777216f);
                // A distinct lattice cell per sample, at a random point inside it.
                sorted[i] = Noise(new Vector2((i & 511) + u, (i >> 9) + v), 0x5bd1e995u);
            }
            Array.Sort(sorted);
            for (int i = 0; i < DistributionSamples; i++)
                prefix[i + 1] = prefix[i] + sorted[i];
        }

        private float Quantile(float probability) =>
            sorted[Mathf.Clamp(Mathf.RoundToInt(probability * (DistributionSamples - 1)), 0, DistributionSamples - 1)];

        // The sample mean of saturate((v - lo) / (hi - lo)).
        private double Mean(float lo, float hi)
        {
            int below = LowerBound(lo);
            int above = LowerBound(hi);
            double inside = (prefix[above] - prefix[below] - (above - below) * (double)lo) / Math.Max(hi - lo, 1e-9);
            return (DistributionSamples - above + inside) / DistributionSamples;
        }

        private int LowerBound(float value)
        {
            int first = 0, count = DistributionSamples;
            while (count > 0)
            {
                int step = count / 2;
                if (sorted[first + step] < value)
                {
                    first += step + 1;
                    count -= step + 1;
                }
                else
                    count = step;
            }
            return first;
        }

        public Vector2 Thresholds(float area)
        {
            // A band of quantiles round 1 - area softens the edge; then shift it until its mean is the area.
            float band = DriftEdge * Mathf.Min(area, 1f - area);
            float lo = Quantile(1f - area - band * 0.5f), hi = Quantile(1f - area + band * 0.5f);
            float width = Mathf.Max(hi - lo, 1e-4f);
            float low = -1f, high = 1f;
            for (int i = 0; i < 48; i++)
            {
                float shift = 0.5f * (low + high);
                if (Mean(lo + shift, lo + shift + width) > area)
                    low = shift;
                else
                    high = shift;
            }
            float centre = lo + 0.5f * (low + high);
            return new Vector2(centre, centre + width);
        }
    }
}
