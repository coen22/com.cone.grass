using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Kind = GrassVariantSelection.KindMix;

/// <summary>Deterministic kind placement and mixing weights (#88), on the CPU twin of the generation compute.</summary>
public sealed class GrassVariantSelectionTests
{
    private readonly float[] shares = new float[GrassVariantSelection.MaximumKinds];

    private static GrassVariantSelection.Field Field(int seed, float drift = 4f, float cluster = 0.7f) =>
        new GrassVariantSelection.Field
        {
            DriftSize = drift, ClusterSize = cluster, HabitatSize = 40f, Seed = GrassVariantSelection.ProfileSeed(seed)
        };

    private static Kind Mix(float weight, float clumping = 0f, float driftArea = 0.25f, float clusterArea = 1f,
        float moisture = 0f, float slope = 0f) =>
        new Kind { Weight = weight, Clumping = clumping, DriftArea = driftArea, ClusterArea = clusterArea, Moisture = moisture, Slope = slope };

    [Test]
    public void TheSameSeedPlacesTheSameKindsAndAnotherSeedMovesOnlyTheKinds()
    {
        Kind[] kinds = { Mix(0.5f), Mix(0.3f, 0.5f), Mix(0.1f, 1f, 0.25f, 0.3f), Mix(0.1f, 1f) };
        int changed = 0;
        for (int z = -40; z < 40; z++)
        for (int x = -40; x < 40; x++)
        {
            int first = GrassVariantSelection.SelectKind(x, z, 0.1f, Field(7), kinds, 4, shares);
            int again = GrassVariantSelection.SelectKind(x, z, 0.1f, Field(7), kinds, 4, shares);
            Assert.That(again, Is.EqualTo(first), "A seed must reproduce every root's kind.");
            if (GrassVariantSelection.SelectKind(x, z, 0.1f, Field(8), kinds, 4, shares) != first)
                changed++;
        }
        // Another seed re-deals the kinds; the roots themselves never depend on it.
        Assert.That(changed, Is.GreaterThan(80 * 80 / 5));
        Assert.That(GrassVariantSelection.RootXZ(3, -5, 0.1f), Is.EqualTo(GrassVariantSelection.RootXZ(3, -5, 0.1f)));
    }

    [Test]
    public void UnclumpedKindsGrowInProportionToTheirWeights()
    {
        int[] counts = Count(new[] { Mix(6f), Mix(3f), Mix(1f) }, 0.1f, 200);
        AssertShares(counts, new[] { 0.6f, 0.3f, 0.1f }, 0.01f);
    }

    [Test]
    public void ClumpingGathersAKindIntoDriftsWithoutChangingItsShare()
    {
        Kind[] even = { Mix(0.9f), Mix(0.05f), Mix(0.05f) };
        Kind[] drifts = { Mix(0.9f), Mix(0.05f, 1f), Mix(0.05f, 1f) };
        // 400 m square at 0.5 m: 10,000 drift cells of 4 m, so the drifts' area is sampled closely.
        int[] counts = Count(drifts, 0.5f, 800);
        AssertShares(counts, new[] { 0.9f, 0.05f, 0.05f }, 0.004f);

        // A neighbour of a flower is far more often the same flower inside a drift than when sprinkled evenly.
        float sprinkled = NeighbourAgreement(even, 1);
        float gathered = NeighbourAgreement(drifts, 1);
        Assert.That(sprinkled, Is.EqualTo(0.05f).Within(0.02f));
        Assert.That(gathered, Is.GreaterThan(2.5f * sprinkled));
    }

    [Test]
    public void ClustersInsideDriftsHoldAFewToAFewDozenPlantsWithBareGroundBetween()
    {
        // The meadow's daisy: all of it in clusters of 0.7 m covering 0.3 of drifts that cover 0.25 of a 12 m-scale field.
        Kind[] kinds = { Mix(0.97f), Mix(0.03f, 1f, 0.25f, 0.3f) };
        var field = Field(1, 12f, 0.7f);
        const int Cells = 1000;
        const float Spacing = 0.08f;
        var flower = new bool[Cells, Cells];
        int flowers = 0;
        for (int z = 0; z < Cells; z++)
        for (int x = 0; x < Cells; x++)
            if (GrassVariantSelection.SelectKind(x, z, Spacing, field, kinds, 2, shares) == 1)
            {
                flower[x, z] = true;
                flowers++;
            }
        // An 80 m square holds only a few dozen 12 m drifts, so the share is checked loosely here and closely above.
        Assert.That(flowers / (float)(Cells * Cells), Is.InRange(0.015f, 0.045f), "Clusters keep roughly the kind's share.");

        // Group flowers within 0.25 m of each other: the clusters of an 80 m square.
        List<int> sizes = Clusters(flower, Cells, 3);
        sizes.Sort();
        int median = sizes[sizes.Count / 2];
        Assert.That(median, Is.InRange(5, 40), "A typical cluster holds a few to a few dozen heads.");
        int sprinkled = 0;
        foreach (int size in sizes)
            if (size <= 2)
                sprinkled += size;
        Assert.That(sprinkled, Is.LessThan(flowers / 10), "Hardly any flower stands alone between the clusters.");
    }

    [Test]
    public void HeavyClumpedKindsKeepTheirSharesWhereTheirDriftsDoNotOverlap()
    {
        // Heavy clumped kinds: where both kinds' drifts overlap they would hold more than every root, so they share it,
        // which trims their shares slightly (documented); elsewhere each keeps its weight.
        int[] counts = Count(new[] { Mix(0.6f), Mix(0.3f, 0.6f), Mix(0.1f, 1f) }, 0.5f, 600);
        AssertShares(counts, new[] { 0.6f, 0.3f, 0.1f }, 0.03f);
    }

    [Test]
    public void AnAreaWeightOfZeroRemovesAKindAndTheOthersFillItsShare()
    {
        Kind[] kinds = { Mix(0.6f), Mix(0.3f, 0.6f), Mix(0.1f, 1f) };
        var areaWeights = new[] { 1f, 1f, 0f };
        var counts = new int[3];
        for (int z = 0; z < 300; z++)
        for (int x = 0; x < 300; x++)
            counts[GrassVariantSelection.SelectKind(x, z, 0.5f, Field(3), kinds, 3, shares, 0f, areaWeights)]++;
        Assert.That(counts[2], Is.Zero);
        AssertShares(counts, new[] { 2f / 3f, 1f / 3f, 0f }, 0.025f);
    }

    [Test]
    public void HabitatSortsKindsByMoistureAndSlope()
    {
        // A wet-loving and a dry-loving flower at equal weight: the wet one dominates where the moisture field is high.
        Kind[] kinds = { Mix(0.8f), Mix(0.1f, 0f, 0.25f, 1f, 0.8f), Mix(0.1f, 0f, 0.25f, 1f, -0.8f) };
        var field = Field(5);
        int wetInWet = 0, dryInWet = 0, wetInDry = 0, dryInDry = 0;
        for (int z = 0; z < 600; z++)
        for (int x = 0; x < 600; x++)
        {
            float moisture = GrassVariantSelection.Moisture(GrassVariantSelection.RootXZ(x, z, 0.5f), field);
            if (moisture > 0.25f && moisture < 0.75f)
                continue;
            int kind = GrassVariantSelection.SelectKind(x, z, 0.5f, field, kinds, 3, shares);
            if (moisture >= 0.75f) { wetInWet += kind == 1 ? 1 : 0; dryInWet += kind == 2 ? 1 : 0; }
            else { wetInDry += kind == 1 ? 1 : 0; dryInDry += kind == 2 ? 1 : 0; }
        }
        Assert.That(wetInWet, Is.GreaterThan(3 * dryInWet), "Wet ground grows the wet-loving kind.");
        Assert.That(dryInDry, Is.GreaterThan(3 * wetInDry), "Dry ground grows the dry-loving kind.");

        // A slope-loving kind's share rises with the slope; flat ground keeps less of it.
        Kind[] slopes = { Mix(0.9f), Mix(0.1f, 0f, 0.25f, 1f, 0f, 1f) };
        GrassVariantSelection.Shares(new Vector2(3f, 4f), 0f, field, slopes, 2, shares);
        float flat = shares[1];
        GrassVariantSelection.Shares(new Vector2(3f, 4f), 1f, field, slopes, 2, shares);
        Assert.That(shares[1], Is.GreaterThan(1.5f * flat));
        Assert.That(flat, Is.EqualTo(0f).Within(1e-6f), "Full slope preference leaves flat ground to the others.");
    }

    [Test]
    public void SharesSumToOneEverywhereAndNoWeightsGrowTheFirstKind()
    {
        Kind[] kinds = { Mix(0.2f, 0f, 0.25f, 1f, 0.5f), Mix(0.2f, 1f, 0.25f, 0.3f), Mix(0.3f, 1f, 0.2f, 0.5f, -0.6f, 0.4f), Mix(0.3f, 1f) };
        for (int i = 0; i < 400; i++)
        {
            var position = new Vector2(i * 0.37f - 70f, i * -0.53f + 11f);
            GrassVariantSelection.Shares(position, (i % 7) / 6f, Field(99), kinds, 4, shares);
            float sum = shares[0] + shares[1] + shares[2] + shares[3];
            Assert.That(sum, Is.EqualTo(1f).Within(1e-4f));
            for (int k = 0; k < 4; k++)
                Assert.That(shares[k], Is.GreaterThanOrEqualTo(0f));
        }
        GrassVariantSelection.Shares(Vector2.zero, 0f, Field(99), new[] { Mix(0f), Mix(0f), Mix(0f) }, 3, shares);
        Assert.That(GrassVariantSelection.SelectKind(shares, 3, 0.99f), Is.Zero);
    }

    [TestCase(0.05f)]
    [TestCase(0.25f)]
    [TestCase(0.6f)]
    public void DriftsCoverTheirAreaOfTheGroundForAnySeed(float area)
    {
        // The threshold comes from the noise's distribution; another seed's ground agrees within its sampling.
        Vector2 thresholds = GrassVariantSelection.DriftThresholds(area);
        Assert.That(thresholds.y, Is.GreaterThan(thresholds.x));
        double sum = 0;
        const int Samples = 400;
        for (int z = 0; z < Samples; z++)
        for (int x = 0; x < Samples; x++)
            sum += GrassVariantSelection.InDrift(GrassVariantSelection.DriftNoise(
                new Vector2(x * 0.53f, z * 0.53f), 1f, 12345u, 2), thresholds);
        Assert.That(sum / (Samples * Samples), Is.EqualTo(area).Within(0.015f));
    }

    [TestCase(0.37f, 0)]
    [TestCase(1f, 0)]
    [TestCase(0.001f, 5)]
    [TestCase(1f, 7)]
    public void TheKindTravelsBesideCoverageAndKindZeroKeepsTheEarlierLayout(float coverage, int kind)
    {
        float w = GrassVariantSelection.PackKind(coverage, kind);
        Assert.That(GrassVariantSelection.UnpackKind(w), Is.EqualTo(kind));
        Assert.That(GrassVariantSelection.UnpackCoverage(w), Is.EqualTo(coverage).Within(1e-6f));
        if (kind == 0)
            Assert.That(w, Is.EqualTo(coverage));
    }

    [Test]
    public void WithoutAProfileTheGpuTableIsNeutral()
    {
        var data = new GrassVariantGpuData();
        Assert.That(data.Update(null, false), Is.True);
        Assert.That(data.KindCount, Is.Zero);
        Assert.That(data.HeadQueues, Is.Zero);
        foreach (Vector4 value in data.Shape) Assert.That(value, Is.EqualTo(Vector4.zero));
        foreach (Vector4 value in data.Tint) Assert.That(value, Is.EqualTo(Vector4.zero));
        foreach (Vector4 value in data.Far) Assert.That(value, Is.EqualTo(Vector4.zero));
        foreach (Vector4 value in data.Stem) Assert.That(value, Is.EqualTo(Vector4.zero));
        Assert.That(data.Update(null, false), Is.False, "An unchanged binding does not repack.");
    }

    [Test]
    public void TheMeadowPresetPacksItsKindsAndHeads()
    {
        var profile = ScriptableObject.CreateInstance<GrassVariantProfile>();
        try
        {
            profile.ResetToMeadow();
            int count = profile.KindCount;
            Assert.That(count, Is.EqualTo(7));
            Assert.That(profile.HasHeads(GrassHeadShape.Puff), Is.True);
            Assert.That(profile.HasHeads(GrassHeadShape.Mesh), Is.False);
            Assert.That(profile.UsesSlope(), Is.True);
            var data = new GrassVariantGpuData();
            data.Update(profile, false);
            Assert.That(data.KindCount, Is.EqualTo(count));
            Assert.That(data.HeadQueues, Is.EqualTo(1));
            float total = 0f, flowers = 0f;
            for (int k = 0; k < count; k++)
            {
                total += data.Select[k].x;
                if (profile.kinds[k].HasHead && profile.kinds[k].head != GrassHeadShape.Clover)
                    flowers += data.Select[k].x;
            }
            Assert.That(total, Is.EqualTo(1f).Within(1e-5f));
            // About one flower per one to one and a half square metres at the 0.08 m spacing the Alice grass uses
            // (156 roots per m²).
            Assert.That(1f / (flowers / (0.08f * 0.08f)), Is.InRange(1f, 1.5f));
            GrassVariantKind lawn = profile.kinds[0];
            for (int k = 0; k < count; k++)
            {
                GrassVariantKind kind = profile.kinds[k];
                Assert.That(data.Shape[k].x, Is.EqualTo(kind.heightScale - 1f).Within(1e-6f));
                Assert.That(data.Shape[k].w, Is.EqualTo(kind.HasHead ? 1f : 0f));
                // Clover is a leaf and keeps its blade colour at distance; flowers carry theirs.
                Assert.That(data.Far[k].w > 0f, Is.EqualTo(kind.HasHead && kind.head != GrassHeadShape.Clover));
                Assert.That(data.Select[k].z, Is.LessThanOrEqualTo(1f), "A cluster never holds more than every root.");
                if (!kind.HasHead || kind.head == GrassHeadShape.Clover)
                    continue;
                // Flowers grow as singles, not groups, with heads of about 6 cm that read from a standing eye.
                Assert.That(kind.clumping, Is.LessThanOrEqualTo(0.05f), kind.name + " grows in groups.");
                Assert.That(kind.headSize, Is.InRange(0.055f, 0.065f), kind.name + " has a head that does not read.");
                // A flower's head sits at the lawn's tallest tips: never above the tallest blade, never under 0.7 of it.
                float lowest = kind.heightScale * (1f - kind.heightRandomness), highest = kind.heightScale;
                Assert.That(data.Stem[k].y, Is.EqualTo(kind.heightRandomness + 1f).Within(1e-6f));
                Assert.That(highest, Is.LessThanOrEqualTo(lawn.heightScale), kind.name + " stands above the grass.");
                Assert.That(lowest, Is.GreaterThanOrEqualTo(lawn.heightScale * 0.7f), kind.name + " hides under the grass.");
            }
            Assert.That(data.Seed, Is.EqualTo(unchecked((int)GrassVariantSelection.ProfileSeed(profile.seed))));
            uint revision = profile.Revision;
            profile.kinds[0].weight = 2f;
            profile.MarkDirty();
            Assert.That(profile.Revision, Is.Not.EqualTo(revision));
            Assert.That(data.Update(profile, false), Is.True, "A changed profile repacks.");
        }
        finally
        {
            Object.DestroyImmediate(profile);
        }
    }

    [Test]
    public void AreaVariantWeightsDefaultToTheProfileMixAndClampAnOverride()
    {
        var area = new GameObject("Kind weights").AddComponent<GrassPlacementArea>();
        try
        {
            for (int k = 0; k < GrassVariantSelection.MaximumKinds; k++)
                Assert.That(area.GetVariantWeight(k), Is.EqualTo(1f));
            area.SetVariantWeights(0.5f, 2f, -1f, float.NaN);
            Assert.That(area.OverridesVariantWeights, Is.True);
            Assert.That(area.GetVariantWeight(0), Is.EqualTo(0.5f));
            Assert.That(area.GetVariantWeight(1), Is.EqualTo(1f));
            Assert.That(area.GetVariantWeight(2), Is.EqualTo(0f));
            Assert.That(area.GetVariantWeight(3), Is.EqualTo(1f));
            Assert.That(area.GetVariantWeight(7), Is.EqualTo(1f));
            area.ClearVariantWeights();
            Assert.That(area.GetVariantWeight(2), Is.EqualTo(1f));
        }
        finally
        {
            Object.DestroyImmediate(area.gameObject);
        }
    }

    private int[] Count(Kind[] kinds, float spacing, int cells)
    {
        var counts = new int[kinds.Length];
        for (int z = 0; z < cells; z++)
        for (int x = 0; x < cells; x++)
            counts[GrassVariantSelection.SelectKind(x - cells / 2, z - cells / 2, spacing, Field(3), kinds, kinds.Length, shares)]++;
        return counts;
    }

    private static void AssertShares(int[] counts, float[] expected, float tolerance)
    {
        float total = 0f;
        foreach (int count in counts)
            total += count;
        for (int k = 0; k < counts.Length; k++)
            Assert.That(counts[k] / total, Is.EqualTo(expected[k]).Within(tolerance), "Kind " + k);
    }

    // Among roots of the kind, the fraction whose right-hand neighbour is the same kind.
    private float NeighbourAgreement(Kind[] kinds, int kind)
    {
        const int Cells = 400;
        int same = 0, total = 0;
        for (int z = 0; z < Cells; z++)
        {
            int previous = GrassVariantSelection.SelectKind(-Cells / 2, z, 0.25f, Field(3), kinds, 3, shares);
            for (int x = 1; x < Cells; x++)
            {
                int current = GrassVariantSelection.SelectKind(x - Cells / 2, z, 0.25f, Field(3), kinds, 3, shares);
                if (previous == kind)
                {
                    total++;
                    if (current == kind)
                        same++;
                }
                previous = current;
            }
        }
        return total > 0 ? same / (float)total : 0f;
    }

    // Sizes of the groups of marked cells joined within a reach of cells (8-connected over that reach).
    private static List<int> Clusters(bool[,] marked, int cells, int reach)
    {
        var seen = new bool[cells, cells];
        var sizes = new List<int>();
        var stack = new Stack<Vector2Int>();
        for (int z = 0; z < cells; z++)
        for (int x = 0; x < cells; x++)
        {
            if (!marked[x, z] || seen[x, z])
                continue;
            int size = 0;
            seen[x, z] = true;
            stack.Push(new Vector2Int(x, z));
            while (stack.Count > 0)
            {
                Vector2Int cell = stack.Pop();
                size++;
                for (int dz = -reach; dz <= reach; dz++)
                for (int dx = -reach; dx <= reach; dx++)
                {
                    int nx = cell.x + dx, nz = cell.y + dz;
                    if (nx < 0 || nz < 0 || nx >= cells || nz >= cells || !marked[nx, nz] || seen[nx, nz])
                        continue;
                    seen[nx, nz] = true;
                    stack.Push(new Vector2Int(nx, nz));
                }
            }
            sizes.Add(size);
        }
        return sizes;
    }
}
