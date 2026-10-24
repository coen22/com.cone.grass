using NUnit.Framework;
using UnityEngine;

/// <summary>Deterministic kind placement and mixing weights (#88), on the CPU twin of the generation compute.</summary>
public sealed class GrassVariantSelectionTests
{
    private readonly float[] shares = new float[GrassVariantSelection.MaximumKinds];
    private static readonly float[] Areas = { 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f };

    [Test]
    public void TheSameSeedPlacesTheSameKindsAndAnotherSeedMovesOnlyTheKinds()
    {
        float[] weights = { 0.5f, 0.3f, 0.1f, 0.1f };
        float[] clumping = { 0f, 0.5f, 1f, 1f };
        int changed = 0;
        for (int z = -40; z < 40; z++)
        for (int x = -40; x < 40; x++)
        {
            int first = GrassVariantSelection.SelectKind(x, z, 0.1f, 4f, 7, 4, weights, clumping, Areas, shares);
            int again = GrassVariantSelection.SelectKind(x, z, 0.1f, 4f, 7, 4, weights, clumping, Areas, shares);
            Assert.That(again, Is.EqualTo(first), "A seed must reproduce every root's kind.");
            if (GrassVariantSelection.SelectKind(x, z, 0.1f, 4f, 8, 4, weights, clumping, Areas, shares) != first)
                changed++;
        }
        // Another seed re-deals the kinds; the roots themselves never depend on it.
        Assert.That(changed, Is.GreaterThan(80 * 80 / 5));
        Assert.That(GrassVariantSelection.RootXZ(3, -5, 0.1f), Is.EqualTo(GrassVariantSelection.RootXZ(3, -5, 0.1f)));
    }

    [Test]
    public void UnclumpedKindsGrowInProportionToTheirWeights()
    {
        float[] weights = { 6f, 3f, 1f };
        float[] clumping = { 0f, 0f, 0f };
        int[] counts = Count(weights, clumping, 3, 0.1f, 200);
        AssertShares(counts, new[] { 0.6f, 0.3f, 0.1f }, 0.01f);
    }

    [Test]
    public void ClumpingGathersAKindIntoDriftsWithoutChangingItsShare()
    {
        float[] weights = { 0.9f, 0.05f, 0.05f };
        float[] even = { 0f, 0f, 0f };
        float[] drifts = { 0f, 1f, 1f };
        // 400 m square at 0.5 m: 10,000 drift cells of 4 m, so the drifts' area is sampled closely.
        int[] counts = Count(weights, drifts, 3, 0.5f, 800);
        AssertShares(counts, new[] { 0.9f, 0.05f, 0.05f }, 0.004f);

        // A neighbour of a flower is far more often the same flower inside a drift than when sprinkled evenly.
        float sprinkled = NeighbourAgreement(weights, even, 1);
        float gathered = NeighbourAgreement(weights, drifts, 1);
        Assert.That(sprinkled, Is.EqualTo(0.05f).Within(0.02f));
        Assert.That(gathered, Is.GreaterThan(2.5f * sprinkled));
    }

    [Test]
    public void HeavyClumpedKindsKeepTheirSharesWhereTheirDriftsDoNotOverlap()
    {
        // Heavy clumped kinds: where both kinds' drifts overlap they would hold more than every root, so they share it,
        // which trims their shares slightly (documented); elsewhere each keeps its weight.
        int[] counts = Count(new[] { 0.6f, 0.3f, 0.1f }, new[] { 0f, 0.6f, 1f }, 3, 0.5f, 600);
        AssertShares(counts, new[] { 0.6f, 0.3f, 0.1f }, 0.03f);
    }

    [Test]
    public void AnAreaWeightOfZeroRemovesAKindAndTheOthersFillItsShare()
    {
        var areaWeights = new[] { 1f, 1f, 0f };
        var counts = new int[3];
        for (int z = 0; z < 300; z++)
        for (int x = 0; x < 300; x++)
            counts[GrassVariantSelection.SelectKind(x, z, 0.5f, 4f, 3, 3, new[] { 0.6f, 0.3f, 0.1f },
                new[] { 0f, 0.6f, 1f }, Areas, shares, areaWeights)]++;
        Assert.That(counts[2], Is.Zero);
        AssertShares(counts, new[] { 2f / 3f, 1f / 3f, 0f }, 0.025f);
    }

    [Test]
    public void SharesSumToOneEverywhereAndNoWeightsGrowTheFirstKind()
    {
        float[] weights = { 0.2f, 0.2f, 0.3f, 0.3f };
        float[] clumping = { 0f, 1f, 1f, 1f };
        for (int i = 0; i < 400; i++)
        {
            var position = new Vector2(i * 0.37f - 70f, i * -0.53f + 11f);
            GrassVariantSelection.Shares(position, 3f, 99u, 4, weights, clumping, Areas, shares);
            float sum = shares[0] + shares[1] + shares[2] + shares[3];
            Assert.That(sum, Is.EqualTo(1f).Within(1e-4f));
            for (int k = 0; k < 4; k++)
                Assert.That(shares[k], Is.GreaterThanOrEqualTo(0f));
        }
        GrassVariantSelection.Shares(Vector2.zero, 3f, 99u, 3, new float[3], new float[3], Areas, shares);
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
        Assert.That(data.Update(null, false), Is.False, "An unchanged binding does not repack.");
    }

    [Test]
    public void TheMeadowPresetPacksItsKindsAndHeads()
    {
        var profile = ScriptableObject.CreateInstance<GrassVariantProfile>();
        try
        {
            profile.ResetToMeadow();
            Assert.That(profile.KindCount, Is.EqualTo(8));
            Assert.That(profile.HasHeads(GrassHeadShape.Daisy), Is.True);
            Assert.That(profile.HasHeads(GrassHeadShape.Mesh), Is.False);
            var data = new GrassVariantGpuData();
            data.Update(profile, false);
            Assert.That(data.KindCount, Is.EqualTo(8));
            Assert.That(data.HeadQueues, Is.EqualTo(1));
            float total = 0f;
            for (int k = 0; k < 8; k++)
                total += data.Select[k].x;
            Assert.That(total, Is.EqualTo(1f).Within(1e-5f));
            for (int k = 0; k < 8; k++)
            {
                GrassVariantKind kind = profile.kinds[k];
                Assert.That(data.Shape[k].x, Is.EqualTo(kind.heightScale - 1f).Within(1e-6f));
                Assert.That(data.Shape[k].w, Is.EqualTo(kind.HasHead ? 1f : 0f));
                // Clover is a leaf and keeps its blade colour at distance; flowers carry theirs.
                Assert.That(data.Far[k].w > 0f, Is.EqualTo(kind.HasHead && kind.head != GrassHeadShape.Clover));
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

    private int[] Count(float[] weights, float[] clumping, int kinds, float spacing, int cells)
    {
        var counts = new int[kinds];
        for (int z = 0; z < cells; z++)
        for (int x = 0; x < cells; x++)
            counts[GrassVariantSelection.SelectKind(x - cells / 2, z - cells / 2, spacing, 4f, 3, kinds,
                weights, clumping, Areas, shares)]++;
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
    private float NeighbourAgreement(float[] weights, float[] clumping, int kind)
    {
        const int Cells = 400;
        int same = 0, total = 0;
        for (int z = 0; z < Cells; z++)
        {
            int previous = GrassVariantSelection.SelectKind(-Cells / 2, z, 0.25f, 4f, 3, 3, weights, clumping, Areas, shares);
            for (int x = 1; x < Cells; x++)
            {
                int current = GrassVariantSelection.SelectKind(x - Cells / 2, z, 0.25f, 4f, 3, 3, weights, clumping, Areas, shares);
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
}
