using System;
using NUnit.Framework;
using UnityEngine;

public sealed class GrassInteractionFieldTests
{
    [TestCase(0.5f, 0f, 1f, 0.25f, 0.25f, true, 0.75)]
    [TestCase(0.5f, 0.25f, 1f, 0.25f, 0.25f, true, 0.5)]
    [TestCase(0.5f, 0.26f, 1f, 0.25f, 0.25f, false, 0.0)]
    [TestCase(0.9f, 0.1f, 1f, 0.25f, 0.25f, true, 1.0)]
    [TestCase(-0.25f, 0f, 1f, 0.25f, 0.25f, true, 0.0)]
    [TestCase(1.3f, 0f, 1f, 0.25f, 0.25f, false, 0.0)]
    [TestCase(0.3f, 0f, 0f, 0.5f, 0.1f, true, 0.5)]
    [TestCase(0.3f, 0f, 0f, 0.1f, 0.5f, true, 1.0)]
    [TestCase(0.1f, 0f, 0f, 0.25f, 0.25f, true, 1.0)]
    [TestCase(0.3f, 0f, 0f, 0.25f, 0.25f, false, 0.0)]
    [TestCase(-0.5f, 0f, 1f, 1.25f, 0.25f, true, 0.375)]
    [TestCase(0.5f, 0f, 1f, 1.25f, 0.25f, true, 0.875)]
    public void LatestContactIncludesTangencyDepartureAndChangingFootprints(
        float x, float y, float travel, float r0, float r1, bool expectedHit, double expectedFraction)
    {
        bool hit = GrassInteractionField.TryGetLatestContact(new Vector2(x, y), Vector2.zero, r0,
            new Vector2(travel, 0f), r1, out double fraction);
        Assert.That(hit, Is.EqualTo(expectedHit));
        Assert.That(fraction, Is.EqualTo(expectedFraction).Within(0.0000001));
    }

    [Test]
    public void LongTangentialSweepDoesNotLoseContactToAlongPathCancellation()
    {
        Assert.That(GrassInteractionField.TryGetLatestContact(new Vector2(4096f, 0.125f),
            Vector2.zero, 0.125f, new Vector2(8192f, 0f), 0.125f, out double fraction), Is.True);
        Assert.That(fraction, Is.EqualTo(0.5));
        Assert.That(GrassInteractionField.TryGetLatestContact(new Vector2(4096f, 0.12501f),
            Vector2.zero, 0.125f, new Vector2(8192f, 0f), 0.125f, out _), Is.False);
    }

    [Test]
    public void RecoveryStartsWhenTheFootprintLeavesRatherThanWhenItsCenterPasses()
    {
        Assert.That(GrassInteractionField.TryGetLatestContact(Vector2.zero, Vector2.zero, 0.3f,
            new Vector2(0.4f, 0f), 0.3f, out double fraction), Is.True);
        double departure = fraction * 4.0;
        Assert.That(departure, Is.EqualTo(3.0).Within(0.000002));
        Assert.That(GrassInteractionField.RecoveryWeight(3.01 - departure, 2f), Is.GreaterThan(0.9999f));
        Assert.That(GrassInteractionField.RecoveryWeight(3.01, 2f), Is.Zero,
            "The center-passage timestamp would already have erased this slow actor's newly vacated footprint.");
    }

    [TestCase(0f, 1f, 0.0, 1f, 0f)]
    [TestCase(0f, 1f, 1.0, 1f, 0.63212056f)]
    [TestCase(1f, 0f, 1.0, 1f, 0.36787944f)]
    [TestCase(0.25f, 0.75f, 0.0, 0f, 0.75f)]
    [TestCase(-1f, 2f, 0.0, 0f, 1f)]
    [TestCase(-1f, 2f, 0.0, 1f, 0f)]
    [TestCase(2f, -1f, 0.0, 1f, 1f)]
    [TestCase(0.5f, 0.5f, 100.0, 0.06f, 0.5f)]
    public void AttackResponseIsAnalyticAndClamped(float start, float target, double elapsed, float attack, float expected)
    {
        Assert.That(GrassInteractionField.EvaluateAttack(start, target, elapsed, attack),
            Is.EqualTo(expected).Within(0.0000001f));
    }

    [TestCase(-1.0)]
    [TestCase(double.NaN)]
    [TestCase(double.PositiveInfinity)]
    public void InvalidAttackElapsedTimeDoesNotInventPressure(double elapsed)
    {
        Assert.That(GrassInteractionField.EvaluateAttack(0.5f, 1f, elapsed, 0.06f), Is.Zero);
    }

    [TestCase(30, 0f, 0.9f)]
    [TestCase(60, 0f, 0.9f)]
    [TestCase(120, 0f, 0.9f)]
    [TestCase(30, 0.9f, 0.2f)]
    [TestCase(60, 0.9f, 0.2f)]
    [TestCase(120, 0.9f, 0.2f)]
    public void DeparturePressureAgreesAcrossSplitCadencesForAConstantTarget(int cadence, float startResponse, float target)
    {
        const float radius = 0.25f, speed = 5f, attack = 0.06f, recovery = 1f;
        const double duration = 0.2, now = 0.4;
        var reference = new GrassInteractionField(0.125f, 128);
        Assert.That(reference.AdvanceSweptCircle(Vector2.zero, radius, 0.0,
            Vector2.right, radius, duration, Vector2.right, startResponse, target, attack), Is.True);

        var field = new GrassInteractionField(reference.CellSize, reference.Capacity);
        float response = startResponse;
        for (int i = 0; i < duration * cadence; i++)
        {
            double start = i / (double)cadence;
            double end = (i + 1) / (double)cadence;
            Assert.That(field.AdvanceSweptCircle(new Vector2((float)(speed * start), 0f), radius, start,
                new Vector2((float)(speed * end), 0f), radius, end, Vector2.right, response, target, attack), Is.True);
            response = GrassInteractionField.EvaluateAttack(response, target, end - start, attack);
        }
        Assert.That(field.Count, Is.EqualTo(reference.Count));
        for (int i = 0; i < reference.Count; i++)
        {
            Vector2Int key = reference.GetKey(i);
            Assert.That(reference.TryGetNode(key, now, recovery, out Vector2 expectedDirection, out float expected), Is.True);
            Assert.That(field.TryGetNode(key, now, recovery, out Vector2 direction, out float actual), Is.True);
            Assert.That(actual, Is.EqualTo(expected).Within(0.000001f),
                "Pressure must be evaluated when this node leaves the footprint, not at the frame's end.");
            Assert.That(Vector2.Distance(direction, expectedDirection), Is.LessThan(0.00001f));
        }
        double departure = (double)radius / speed;
        float departureResponse = (float)(target + ((double)startResponse - target) * Math.Exp(-departure / attack));
        Assert.That(field.TryGetNode(Vector2Int.zero, now, recovery, out _, out float originStrength), Is.True);
        Assert.That(originStrength,
            Is.EqualTo(departureResponse * GrassInteractionField.RecoveryWeight(now - departure, recovery)).Within(0.000001f));
    }

    [Test]
    public void LaterActorPressureDoesNotChangeAnOlderNodesStoredAmplitude()
    {
        var field = new GrassInteractionField(1f, 2);
        Assert.That(field.AdvanceSweptCircle(Vector2.zero, 0.1f, 0.0,
            Vector2.zero, 0.1f, 0.0, Vector2.right, 0f, 0.8f, 0f), Is.True);
        Vector2 later = new Vector2(2f, 0f);
        Assert.That(field.AdvanceSweptCircle(later, 0.1f, 1.0,
            later, 0.1f, 1.0, Vector2.up, 0f, 0.2f, 0f), Is.True);
        Assert.That(field.TryGetNode(Vector2Int.zero, 1.0, 4f, out Vector2 oldDirection, out float oldStrength), Is.True);
        Assert.That(oldDirection, Is.EqualTo(Vector2.right));
        Assert.That(oldStrength, Is.EqualTo(0.8f * 0.84375f));
        Assert.That(field.TryGetNode(new Vector2Int(2, 0), 1.0, 4f, out _, out float laterStrength), Is.True);
        Assert.That(laterStrength, Is.EqualTo(0.2f));

        Assert.That(field.AdvanceSweptCircle(later, 0.1f, 1.0,
            later, 0.1f, 2.0, Vector2.up, 0.2f, 1f, 0.06f), Is.True);
        Assert.That(field.TryGetNode(Vector2Int.zero, 2.0, 4f, out oldDirection, out oldStrength), Is.True);
        Assert.That(oldDirection, Is.EqualTo(Vector2.right));
        Assert.That(oldStrength, Is.EqualTo(0.4f));
        Assert.That(field.TryGetNode(new Vector2Int(2, 0), 2.0, 4f, out _, out laterStrength), Is.True);
        Assert.That(laterStrength, Is.GreaterThan(0.99f));
    }

    [Test]
    public void ZeroPressureDoesNotReserveCapacityOrEraseAnEarlierContact()
    {
        var field = new GrassInteractionField(1f, 1);
        Assert.That(field.AdvanceSweptCircle(Vector2.zero, 0.1f, 0.0,
            new Vector2(2f, 0f), 0.1f, 1.0, Vector2.up, 0f, 0f, 0.06f), Is.True);
        Assert.That(field.Count, Is.Zero);
        Assert.That(field.AdvanceSweptCircle(Vector2.zero, 0.1f, 0.0,
            Vector2.zero, 0.1f, 0.0, Vector2.right, 0f, 0.75f, 0f), Is.True);
        Assert.That(field.AdvanceSweptCircle(Vector2.zero, 0.1f, 1.0,
            new Vector2(2f, 0f), 0.1f, 2.0, Vector2.up, 0.75f, 0f, 0f), Is.True);
        Assert.That(field.Count, Is.EqualTo(1));
        Assert.That(field.TryGetNode(Vector2Int.zero, 2.0, 4f, out Vector2 direction, out float strength), Is.True);
        Assert.That(direction, Is.EqualTo(Vector2.right));
        Assert.That(strength, Is.EqualTo(0.375f));
        Assert.That(field.TryGetNode(Vector2Int.right, 2.0, 4f, out _, out _), Is.False);
    }

    [Test]
    public void InstantDepartureAtTheStartOfAttackDoesNotConsumeANodeSlot()
    {
        var field = new GrassInteractionField(1f, 1);
        Assert.That(field.AdvanceSweptCircle(new Vector2(0.25f, 0f), 0.25f, 0.0,
            new Vector2(0.75f, 0f), 0.25f, 1.0, Vector2.right, 0f, 1f, 1f), Is.True);
        Assert.That(field.Count, Is.EqualTo(1));
        Assert.That(field.TryGetNode(Vector2Int.zero, 1.0, 2f, out _, out _), Is.False);
        Assert.That(field.TryGetNode(Vector2Int.right, 1.0, 2f, out _, out float strength), Is.True);
        Assert.That(strength, Is.EqualTo(0.63212056f).Within(0.0000001f));
    }

    [Test]
    public void WeightedNodesRecoverThenReleaseTheirCapacity()
    {
        var field = new GrassInteractionField(1f, 1);
        Assert.That(field.AdvanceSweptCircle(Vector2.zero, 0.1f, 0.0,
            Vector2.zero, 0.1f, 0.0, Vector2.right, 0f, 0.25f, 0f), Is.True);
        Assert.That(field.TryGetNode(Vector2Int.zero, 0.5, 1f, out _, out float strength), Is.True);
        Assert.That(strength, Is.EqualTo(0.125f));
        Assert.That(field.Prune(0.5, 1f), Is.Zero);
        Assert.That(field.Prune(1.0 - 1.0 / (1L << 30), 1f), Is.Zero);
        Assert.That(field.Prune(1.0, 1f), Is.EqualTo(1));
        Assert.That(field.Count, Is.Zero);
        Assert.That(field.AdvanceSweptCircle(Vector2.right, 0.1f, 2.0,
            Vector2.right, 0.1f, 2.0, Vector2.up, 0f, 0.75f, 0f), Is.True);
    }

    [Test]
    public void RejectedWeightedSweepsLeavePressureDirectionAndTimestampUnchanged()
    {
        var field = new GrassInteractionField(1f, 2);
        Assert.That(field.AdvanceSweptCircle(Vector2.zero, 0.1f, 0.0,
            Vector2.zero, 0.1f, 0.0, Vector2.right, 0f, 0.4f, 0f), Is.True);
        Assert.That(field.AdvanceSweptCircle(Vector2.zero, 0.1f, 1.0,
            new Vector2(2f, 0f), 0.1f, 3.0, Vector2.up, 0.2f, 0.8f, 0.06f), Is.False);
        Assert.That(field.AdvanceSweptCircle(Vector2.zero, 0.1f, 4.0,
            new Vector2(64f, 64f), 0.1f, 5.0, Vector2.up, 0.2f, 0.8f, 0.06f), Is.False);
        Assert.That(field.Count, Is.EqualTo(1));
        Assert.That(field.TryGetNode(Vector2Int.zero, 1.0, 2f, out Vector2 direction, out float strength), Is.True);
        Assert.That(direction, Is.EqualTo(Vector2.right));
        Assert.That(strength, Is.EqualTo(0.2f));
    }

    [TestCase(float.NaN, 0.5f, 0.06f)]
    [TestCase(0.5f, float.NaN, 0.06f)]
    [TestCase(0.5f, 0.5f, float.NaN)]
    [TestCase(float.PositiveInfinity, 0.5f, 0.06f)]
    [TestCase(0.5f, float.NegativeInfinity, 0.06f)]
    [TestCase(0.5f, 0.5f, float.PositiveInfinity)]
    [TestCase(0.5f, 0.5f, -1f)]
    public void InvalidResponseInputsRejectTheEntireSweep(float start, float target, float attack)
    {
        Assert.That(GrassInteractionField.EvaluateAttack(start, target, 1.0, attack), Is.Zero);
        var field = new GrassInteractionField(1f, 2);
        Assert.That(field.AdvanceSweptCircle(Vector2.zero, 0.1f, 0.0,
            Vector2.zero, 0.1f, 0.0, Vector2.right, 0f, 0.4f, 0f), Is.True);
        Assert.That(field.AdvanceSweptCircle(Vector2.zero, 0.1f, 0.5,
            Vector2.right, 0.1f, 1.5, Vector2.up, start, target, attack), Is.False);
        Assert.That(field.Count, Is.EqualTo(1));
        Assert.That(field.TryGetNode(Vector2Int.zero, 1.0, 2f, out Vector2 direction, out float strength), Is.True);
        Assert.That(direction, Is.EqualTo(Vector2.right));
        Assert.That(strength, Is.EqualTo(0.2f));
    }

    [TestCase(0.0, 2f, 1f)]
    [TestCase(0.5, 2f, 0.84375f)]
    [TestCase(1.0, 2f, 0.5f)]
    [TestCase(2.0, 2f, 0f)]
    [TestCase(3.0, 2f, 0f)]
    [TestCase(0.0, 0f, 1f)]
    [TestCase(0.000001, 0f, 0f)]
    [TestCase(-1.0, 2f, 0f)]
    [TestCase(double.NaN, 2f, 0f)]
    [TestCase(double.PositiveInfinity, 2f, 0f)]
    [TestCase(1.0, float.NaN, 0f)]
    [TestCase(1.0, -1f, 0f)]
    public void RecoveryHasFiniteDurationAndDefinedZeroDurationBehavior(double age, float duration, float expected)
    {
        Assert.That(GrassInteractionField.RecoveryWeight(age, duration), Is.EqualTo(expected));
    }

    [Test]
    public void FinalRecoveryWeightsStayPositiveUntilTheirActualExpiry()
    {
        double almostExpired = 1.0 - 1.0 / (1L << 30);
        Assert.That(GrassInteractionField.RecoveryWeight(almostExpired, 1f), Is.GreaterThan(0f));
        var field = new GrassInteractionField(1f, 1);
        Assert.That(field.AdvanceSweptCircle(Vector2.zero, 0.1f, 0.0,
            Vector2.zero, 0.1f, 0.0, Vector2.right), Is.True);
        Assert.That(field.Prune(almostExpired, 1f), Is.Zero);
        Assert.That(field.Prune(1.0, 1f), Is.EqualTo(1));
    }

    [Test]
    public void ShiftedNegativeGridAndZeroRadialDirectionUseTheDeclaredOriginAndFallback()
    {
        var field = new GrassInteractionField(0.5f, 16, new Vector2(0.125f, -0.125f));
        var key = new Vector2Int(-2, -3);
        Vector2 point = field.GetPosition(key);
        Assert.That(point, Is.EqualTo(new Vector2(-0.875f, -1.625f)));
        Assert.That(field.AdvanceSweptCircle(point, 0.24f, 4.0, point, 0.24f, 4.0, new Vector2(3f, 4f)), Is.True);
        Assert.That(field.Count, Is.EqualTo(1));
        Assert.That(field.GetKey(0), Is.EqualTo(key));
        Assert.That(field.TryGetNode(key, 4.0, 2f, out Vector2 direction, out float strength), Is.True);
        Assert.That(Vector2.Distance(direction, new Vector2(0.6f, 0.8f)), Is.LessThan(0.000001f));
        Assert.That(strength, Is.EqualTo(1f));
        Assert.That(field.TryGetNode(key + Vector2Int.right, 4.0, 2f, out direction, out strength), Is.False);
        Assert.That(direction, Is.EqualTo(Vector2.zero));
        Assert.That(strength, Is.Zero);
    }

    [Test]
    public void SmallNonzeroRadialDirectionsAreNormalizedInsteadOfUsingTheFallback()
    {
        var field = new GrassInteractionField(0.000001f, 16);
        Assert.That(field.AdvanceSweptCircle(Vector2.zero, 0.0000011f, 0.0,
            Vector2.zero, 0.0000011f, 0.0, Vector2.down), Is.True);
        Assert.That(field.TryGetNode(Vector2Int.right, 0.0, 1f, out Vector2 direction, out _), Is.True);
        Assert.That(direction, Is.EqualTo(Vector2.right));
        Assert.That(field.TryGetNode(Vector2Int.zero, 0.0, 1f, out direction, out _), Is.True);
        Assert.That(direction, Is.EqualTo(Vector2.down));
    }

    [Test]
    public void StationaryContactRefreshesExistingNodesAndThenRecoversWithoutAppendingHistory()
    {
        var field = new GrassInteractionField(0.25f, 64);
        Assert.That(field.AdvanceSweptCircle(Vector2.zero, 0.5f, 0.0, Vector2.zero, 0.5f, 0.0, Vector2.right), Is.True);
        int initialCount = field.Count;
        Assert.That(initialCount, Is.GreaterThan(1));
        for (int i = 0; i < 128; i++)
            Assert.That(field.AdvanceSweptCircle(Vector2.zero, 0.5f, i,
                Vector2.zero, 0.5f, i + 1.0, Vector2.right), Is.True);
        Assert.That(field.Count, Is.EqualTo(initialCount));
        Assert.That(field.TryGetNode(Vector2Int.zero, 128.0, 1f, out _, out float strength), Is.True);
        Assert.That(strength, Is.EqualTo(1f));
        Assert.That(field.TryGetNode(Vector2Int.zero, 128.5, 1f, out _, out strength), Is.True);
        Assert.That(strength, Is.EqualTo(0.5f));
        Assert.That(field.Prune(129.0, 1f), Is.EqualTo(initialCount));
        Assert.That(field.Count, Is.Zero);
    }

    [TestCase(1.5f, 0.3f, 0.3f)]
    [TestCase(5f, 0.3f, 0.3f)]
    [TestCase(10f, 0.3f, 0.3f)]
    [TestCase(1.5f, 0.12f, 0.4f)]
    [TestCase(5f, 0.12f, 0.4f)]
    [TestCase(10f, 0.12f, 0.4f)]
    [TestCase(1.5f, 0.5f, 0.15f)]
    [TestCase(5f, 0.5f, 0.15f)]
    [TestCase(10f, 0.5f, 0.15f)]
    public void WalkRunAndSprintFieldsAgreeAcrossCadencesAndChangingRadii(float speed, float r0, float r1)
    {
        const double duration = 2.0;
        Vector2 origin = new Vector2(0.03125f, -0.03125f);
        var reference = new GrassInteractionField(0.125f, GrassInteractionField.MaximumCapacity, origin);
        Assert.That(reference.AdvanceSweptCircle(Path(speed, 0.0), r0, 0.0,
            Path(speed, duration), r1, duration, Vector2.right), Is.True);
        Assert.That(reference.Count, Is.GreaterThan(10));

        foreach (int cadence in new[] { 15, 30, 60, 120, 144 })
        {
            var field = new GrassInteractionField(reference.CellSize, reference.Capacity, origin);
            for (int i = 0; i < duration * cadence; i++)
            {
                double start = i / (double)cadence;
                double end = (i + 1) / (double)cadence;
                float radius0 = (float)(r0 + ((double)r1 - r0) * start / duration);
                float radius1 = (float)(r0 + ((double)r1 - r0) * end / duration);
                Assert.That(field.AdvanceSweptCircle(Path(speed, start), radius0, start,
                    Path(speed, end), radius1, end, Vector2.right), Is.True);
            }
            Assert.That(field.Count, Is.EqualTo(reference.Count), "The same continuous sweep must visit the same grid nodes.");
            for (int i = 0; i < reference.Count; i++)
            {
                Vector2Int key = reference.GetKey(i);
                bool expected = reference.TryGetNode(key, 2.4, 3f, out Vector2 expectedDirection, out float expectedStrength);
                Assert.That(field.TryGetNode(key, 2.4, 3f, out Vector2 direction, out float strength), Is.EqualTo(expected));
                // The observed centers/radii are float32. Their sampled linear
                // paths may differ slightly after rounding, unlike ideal doubles.
                Assert.That(strength, Is.EqualTo(expectedStrength).Within(0.000003f));
                Assert.That(Vector2.Distance(direction, expectedDirection), Is.LessThan(0.00001f));
            }
        }
    }

    [TestCase(30)]
    [TestCase(60)]
    [TestCase(120)]
    public void DefaultFieldKeepsASustainedSprintTrailWithinItsBudgets(int cadence)
    {
        const float cellSize = 0.15f, radius = 0.3f, speed = 10f, recovery = 2f;
        const double duration = 12.0;
        var field = new GrassInteractionField(cellSize);
        for (int i = 0; i < duration * cadence; i++)
        {
            double start = i / (double)cadence;
            double end = (i + 1) / (double)cadence;
            field.Prune(end, recovery);
            Assert.That(field.AdvanceSweptCircle(new Vector2((float)(speed * start), 0f), radius, start,
                new Vector2((float)(speed * end), 0f), radius, end, Vector2.right), Is.True,
                "Default field budgets must retain a normal continuous sprint for longer than its recovery duration.");
        }
        Assert.That(field.Count, Is.GreaterThan(128).And.LessThanOrEqualTo(field.Capacity));
        Assert.That(field.TryGetNode(Vector2Int.zero, duration, recovery, out _, out _), Is.False,
            "The trail's old beginning must have expired instead of consuming the fixed node budget.");
        var recentKey = new Vector2Int(Mathf.RoundToInt((float)(speed * (duration - 1.0)) / cellSize), 0);
        Assert.That(field.TryGetNode(recentKey, duration, recovery, out _, out float strength), Is.True);
        Assert.That(strength, Is.GreaterThan(0f).And.LessThan(1f));
    }

    [Test]
    public void CapacityFailureDoesNotPartiallyRefreshExistingNodesOrAddNewOnes()
    {
        var field = new GrassInteractionField(1f, 2);
        Assert.That(field.AdvanceSweptCircle(Vector2.zero, 0.2f, 0.0,
            Vector2.zero, 0.2f, 0.0, Vector2.right), Is.True);
        Assert.That(field.AdvanceSweptCircle(Vector2.zero, 0.2f, 1.0,
            new Vector2(2f, 0f), 0.2f, 3.0, Vector2.up), Is.False);
        Assert.That(field.Count, Is.EqualTo(1));
        Assert.That(field.TryGetNode(Vector2Int.zero, 1.0, 2f, out Vector2 direction, out float strength), Is.True);
        Assert.That(direction, Is.EqualTo(Vector2.right));
        Assert.That(strength, Is.EqualTo(0.5f));
        Assert.That(field.TryGetNode(Vector2Int.right, 1.0, 2f, out _, out _), Is.False);
    }

    [Test]
    public void SweepCandidateLimitAcceptsItsExactBoundaryAndRejectsALargerDiagonalWithoutMutation()
    {
        int side = (int)Math.Sqrt(GrassInteractionField.MaximumSweepCandidates);
        Assert.That(side * side, Is.EqualTo(GrassInteractionField.MaximumSweepCandidates));
        var field = new GrassInteractionField(1f, 128);
        Vector2 end = new Vector2(side - 1, side - 1);
        Assert.That(field.AdvanceSweptCircle(Vector2.zero, 0.1f, 0.0, end, 0.1f, 1.0, Vector2.right), Is.True);
        Assert.That(field.Count, Is.EqualTo(side));
        Vector2Int key = new Vector2Int(side - 1, side - 1);
        Assert.That(field.TryGetNode(key, 1.5, 2f, out Vector2 direction, out float strength), Is.True);
        Assert.That(field.AdvanceSweptCircle(Vector2.zero, 0.1f, 2.0,
            new Vector2(side, side), 0.1f, 3.0, Vector2.up), Is.False);
        Assert.That(field.Count, Is.EqualTo(side));
        Assert.That(field.TryGetNode(key, 1.5, 2f, out Vector2 afterDirection, out float afterStrength), Is.True);
        Assert.That(afterDirection, Is.EqualTo(direction));
        Assert.That(afterStrength, Is.EqualTo(strength));
    }

    [Test]
    public void ReversedAndInstantaneouslyMovedIntervalsAreRejectedAndOlderContactsCannotRewindNodes()
    {
        var field = new GrassInteractionField(1f, 8);
        Assert.That(field.AdvanceSweptCircle(Vector2.zero, 0.2f, 10.0,
            Vector2.zero, 0.2f, 10.0, Vector2.right), Is.True);
        Assert.That(field.AdvanceSweptCircle(Vector2.zero, 0.2f, 10.0,
            Vector2.zero, 0.2f, 9.0, Vector2.up), Is.False);
        Assert.That(field.AdvanceSweptCircle(Vector2.zero, 0.2f, 10.0,
            Vector2.right, 0.2f, 10.0, Vector2.up), Is.False);
        Assert.That(field.AdvanceSweptCircle(Vector2.zero, 0.2f, 0.0,
            Vector2.zero, 0.2f, 1.0, Vector2.up), Is.True);
        Assert.That(field.Count, Is.EqualTo(1));
        Assert.That(field.TryGetNode(Vector2Int.zero, 11.0, 2f, out Vector2 direction, out float strength), Is.True);
        Assert.That(direction, Is.EqualTo(Vector2.right));
        Assert.That(strength, Is.EqualTo(0.5f));
    }

    [Test]
    public void PruningCompactsTheLookupAndClearAllowsAnIndependentNewSession()
    {
        var field = new GrassInteractionField(1f, 3);
        for (int i = 0; i < 3; i++)
        {
            Vector2 point = new Vector2(i, 0f);
            Assert.That(field.AdvanceSweptCircle(point, 0.1f, i, point, 0.1f, i, Vector2.right), Is.True);
        }
        Assert.That(field.Prune(2.0, 1.5f), Is.EqualTo(1));
        Assert.That(field.Count, Is.EqualTo(2));
        for (int i = 0; i < field.Count; i++)
            Assert.That(field.TryGetNode(field.GetKey(i), 2.0, 1.5f, out _, out _), Is.True);
        Assert.That(field.TryGetNode(Vector2Int.zero, 2.0, 1.5f, out _, out _), Is.False);
        field.Clear();
        Assert.That(field.Count, Is.Zero);
        Assert.That(field.TryGetNode(Vector2Int.right, 0.0, 1f, out _, out _), Is.False);
        Assert.Throws<ArgumentOutOfRangeException>(() => field.GetKey(0));
        Assert.That(field.AdvanceSweptCircle(Vector2.zero, 0.1f, 0.0,
            Vector2.zero, 0.1f, 0.0, Vector2.up), Is.True);
        Assert.That(field.TryGetNode(Vector2Int.zero, 0.0, 1f, out Vector2 direction, out _), Is.True);
        Assert.That(direction, Is.EqualTo(Vector2.up));
    }

    [TestCase(0f)]
    [TestCase(-1f)]
    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    public void InvalidCellSizesAndRadiiNeverProduceFieldWork(float invalid)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GrassInteractionField(invalid));
        var field = new GrassInteractionField(1f, 8);
        Assert.That(field.AdvanceSweptCircle(Vector2.zero, invalid, 0.0,
            Vector2.zero, 0.2f, 1.0, Vector2.right), Is.False);
        Assert.That(field.AdvanceSweptCircle(Vector2.zero, 0.2f, 0.0,
            Vector2.zero, invalid, 1.0, Vector2.right), Is.False);
        Assert.That(field.Count, Is.Zero);
    }

    [Test]
    public void NonfiniteCoordinatesTimesAndUnrepresentableGridHalosAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GrassInteractionField(1f, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GrassInteractionField(1f, GrassInteractionField.MaximumCapacity + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GrassInteractionField(1f, 8, new Vector2(float.NaN, 0f)));
        var field = new GrassInteractionField(1f, 8);
        Assert.That(field.AdvanceSweptCircle(new Vector2(float.NaN, 0f), 0.2f, 0.0,
            Vector2.zero, 0.2f, 1.0, Vector2.right), Is.False);
        Assert.That(field.AdvanceSweptCircle(Vector2.zero, 0.2f, 0.0,
            Vector2.zero, 0.2f, double.PositiveInfinity, Vector2.right), Is.False);
        Assert.That(field.AdvanceSweptCircle(Vector2.zero, 0.2f, 0.0,
            Vector2.zero, 0.2f, 1.0, new Vector2(float.NaN, 0f)), Is.False);
        foreach (float coordinate in new[] { (float)int.MinValue, (float)int.MaxValue, float.MaxValue })
        {
            var point = new Vector2(coordinate, 0f);
            Assert.That(field.AdvanceSweptCircle(point, 0.2f, 0.0, point, 0.2f, 0.0, Vector2.right), Is.False);
        }
        var tinyCells = new GrassInteractionField(float.Epsilon, 8);
        Assert.That(tinyCells.AdvanceSweptCircle(Vector2.one, 0.2f, 0.0,
            Vector2.one, 0.2f, 1.0, Vector2.right), Is.False);
        var overflowingHalo = new GrassInteractionField(float.MaxValue, 8, new Vector2(float.MaxValue, 0f));
        Assert.That(overflowingHalo.AdvanceSweptCircle(new Vector2(float.MaxValue, 0f), 1f, 0.0,
            new Vector2(float.MaxValue, 0f), 1f, 0.0, Vector2.right), Is.False);
        Assert.That(field.Count, Is.Zero);
    }

    [Test]
    public void WarmFieldUpdatesLookupsPruningAndReuseDoNotAllocateManagedMemory()
    {
        var field = new GrassInteractionField(0.25f, 128);
        bool success = true;
        for (int i = 0; i < 64; i++)
            success &= ExerciseField(field);
        Assert.That(success, Is.True);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 128; i++)
            success &= ExerciseField(field);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.That(success, Is.True);
        Assert.That(allocated, Is.Zero, "The fixed node pool and reserved lookup must be reused after warmup.");
    }

    private static bool ExerciseField(GrassInteractionField field)
    {
        field.Clear();
        bool success = field.AdvanceSweptCircle(Vector2.zero, 0.3f, 0.0,
            Vector2.right, 0.3f, 1.0, Vector2.right);
        success &= field.TryGetNode(Vector2Int.right, 1.1, 2f, out _, out _);
        field.Prune(1.5, 1f);
        success &= field.AdvanceSweptCircle(Vector2.right, 0.3f, 1.5,
            Vector2.right, 0.3f, 2.0, Vector2.right, 0.2f, 0.8f, 0.06f);
        return success;
    }

    private static Vector2 Path(float speed, double time) => new Vector2((float)(-1.0 + speed * time), 0.043f);
}
