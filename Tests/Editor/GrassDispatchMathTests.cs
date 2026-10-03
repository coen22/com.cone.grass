using System;
using NUnit.Framework;
using UnityEngine;

public class GrassDispatchMathTests
{
    [TestCase(0f)]
    [TestCase(-1f)]
    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    public void InvalidSpacingDoesNotProduceDispatch(float spacing)
    {
        Assert.That(GrassDispatchMath.TryGetGridRange(new Bounds(Vector3.zero, Vector3.one),
            spacing, out _), Is.False);
    }

    [Test]
    public void InvalidBoundsDoNotReachIntegerConversion()
    {
        Assert.That(GrassDispatchMath.TryGetGridRange(new Bounds(Vector3.zero, Vector3.zero), 0.1f, out _), Is.False);
        Assert.That(GrassDispatchMath.TryGetGridRange(
            new Bounds(new Vector3(float.PositiveInfinity, 0f, 0f), Vector3.one), 0.1f, out _), Is.False);
        Assert.That(GrassDispatchMath.TryGetGridRange(
            new Bounds(new Vector3(float.NaN, 0f, 0f), Vector3.one), 0.1f, out _), Is.False);
        Assert.That(GrassDispatchMath.TryGetGridRange(
            new Bounds(new Vector3(1e20f, 0f, 0f), Vector3.one), 0.001f, out _), Is.False);
    }

    [TestCase(-10.2f, -7.1f, -3.9f, 2.2f, 1f)]
    [TestCase(-0.02f, -0.03f, 0.07f, 0.08f, 0.01f)]
    [TestCase(0.5f, 0.5f, 1.5f, 1.5f, 1f)]
    public void EveryJitteredRootInsideBoundsHasAVisitedCell(
        float minX, float minZ, float maxX, float maxZ, float spacing)
    {
        Bounds bounds = default;
        bounds.SetMinMax(new Vector3(minX, 0f, minZ), new Vector3(maxX, 0f, maxZ));
        Assert.That(GrassDispatchMath.TryGetGridRange(bounds, spacing, out GrassGridRange range), Is.True);
        float[] offsets = { -0.5f, -0.499f, -0.25f, 0f, 0.25f, 0.499f };
        // Evaluate world points independently of the range-construction formula.
        for (int z = -20; z <= 20; z++)
        for (int x = -20; x <= 20; x++)
        foreach (float dz in offsets)
        foreach (float dx in offsets)
        {
            double worldX = ((double)x + dx) * spacing;
            double worldZ = ((double)z + dz) * spacing;
            if (worldX < minX || worldX >= maxX || worldZ < minZ || worldZ >= maxZ)
                continue;
            Assert.That(x, Is.GreaterThanOrEqualTo(range.MinX).And.LessThan(range.MaxX));
            Assert.That(z, Is.GreaterThanOrEqualTo(range.MinZ).And.LessThan(range.MaxZ));
        }
    }

    [Test]
    public void GridOverUnitSquareOmitsCellsWhoseJitterCannotReachIt()
    {
        Bounds bounds = new Bounds(new Vector3(0.5f, 0f, 0.5f), new Vector3(1f, 0f, 1f));
        Assert.That(GrassDispatchMath.TryGetGridRange(bounds, 1f, out GrassGridRange range), Is.True);
        Assert.That(range.MinX, Is.EqualTo(0));
        Assert.That(range.MinZ, Is.EqualTo(0));
        Assert.That(range.MaxX, Is.EqualTo(2));
        Assert.That(range.MaxZ, Is.EqualTo(2));
        Assert.That(range.CandidateCount, Is.EqualTo(4));
    }

    [TestCase(1f, 16f)]
    [TestCase(0.25f, 2f)]
    public void SparseTilesIncludePositiveBilinearDensityAcrossAnAuthoredBoundary(float spacing, float captureTexel)
    {
        const int nextTileCell = 256;
        float tileEdge = nextTileCell * spacing;
        float authoredEnd = tileEdge - 0.75f * spacing;
        Bounds source = default;
        source.SetMinMax(new Vector3(authoredEnd - 4f * captureTexel, 0f, -captureTexel),
            new Vector3(authoredEnd, 0f, captureTexel));
        Assert.That(GrassDispatchMath.TryGetGridRange(source, spacing, out GrassGridRange unfiltered), Is.True);
        Assert.That(unfiltered.MaxX, Is.EqualTo(nextTileCell));
        Assert.That(GrassDispatchMath.TryGetGridRange(GrassDispatchMath.ExpandXZ(source, captureTexel),
            spacing, out GrassGridRange filtered), Is.True);

        // An independent two-texel capture: the center before this tile edge is
        // inside the quad (density one), and the next center is outside (zero).
        // Bilinear density remains positive between these two sample centers.
        float occupiedCenter = tileEdge - captureTexel * 0.5f;
        float emptyCenter = tileEdge + captureTexel * 0.5f;
        int positiveRootsInNextTile = 0;
        for (int cell = nextTileCell - 32; cell < nextTileCell + 32; cell++)
        {
            float worldX = (cell + 0.25f) * spacing;
            if (worldX < occupiedCenter || worldX >= emptyCenter)
                continue;
            float sampledDensity = (emptyCenter - worldX) / captureTexel;
            Assert.That(sampledDensity, Is.GreaterThan(0f));
            Assert.That(cell, Is.GreaterThanOrEqualTo(filtered.MinX).And.LessThan(filtered.MaxX));
            if (cell >= nextTileCell)
                positiveRootsInNextTile++;
        }
        Assert.That(positiveRootsInNextTile, Is.GreaterThan(0), "The regression must exercise the formerly omitted tile.");

        Bounds nextTile = new Bounds(new Vector3((nextTileCell + 128f) * spacing, 0f, 0f),
            new Vector3(257f * spacing, 0f, 257f * spacing));
        Assert.That(source.max.x, Is.LessThan(nextTile.min.x));
        Assert.That(source.max.x, Is.GreaterThanOrEqualTo(GrassDispatchMath.ExpandXZ(nextTile, captureTexel).min.x),
            "The occupancy query needs the same filter support as dispatch enumeration.");
    }

    [TestCase(-257, 256, -2)]
    [TestCase(-256, 256, -1)]
    [TestCase(-1, 256, -1)]
    [TestCase(0, 256, 0)]
    [TestCase(255, 256, 0)]
    [TestCase(256, 256, 1)]
    [TestCase(int.MinValue, 256, -8388608)]
    [TestCase(int.MinValue, int.MaxValue, -2)]
    public void TileDivisionUsesMathematicalFloor(int value, int divisor, int expected)
    {
        Assert.That(GrassDispatchMath.FloorDivide(value, divisor), Is.EqualTo(expected));
    }

    [Test]
    public void BudgetAcceptsExactLimitAndRejectsOneAdditionalCellWithoutChangingIt()
    {
        const long limit = GrassDispatchMath.MaximumCandidates;
        Assert.That(GrassDispatchMath.TryAddCandidateBudget(0, 8192, 8192, limit, out long used), Is.True);
        Assert.That(used, Is.EqualTo(limit));
        Assert.That(GrassDispatchMath.TryAddCandidateBudget(used, 1, 1, limit, out long unchanged), Is.False);
        Assert.That(unchanged, Is.EqualTo(used));
    }

    [Test]
    public void BudgetRejectsHugeOrNegativeInputWithoutIntegerOverflow()
    {
        Assert.That(GrassDispatchMath.TryAddCandidateBudget(12, int.MaxValue, int.MaxValue,
            GrassDispatchMath.MaximumCandidates, out long unchanged), Is.False);
        Assert.That(unchanged, Is.EqualTo(12));
        Assert.That(GrassDispatchMath.TryAddCandidateBudget(12, -1, 5,
            GrassDispatchMath.MaximumCandidates, out unchanged), Is.False);
        Assert.That(unchanged, Is.EqualTo(12));
        Assert.That(GrassDispatchMath.TryAddCandidateBudget(-1, 1, 1,
            GrassDispatchMath.MaximumCandidates, out _), Is.False);
    }

    [TestCase(0, 0)]
    [TestCase(1, 1)]
    [TestCase(8, 1)]
    [TestCase(9, 2)]
    [TestCase(GrassDispatchMath.MaximumDispatchCells, 65535)]
    public void ThreadGroupsRespectHardwareDimensionLimit(int cells, int groups)
    {
        Assert.That(GrassDispatchMath.ThreadGroupCount(cells), Is.EqualTo(groups));
    }

    [Test]
    public void UnsupportedDispatchDimensionsAndDivisorsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GrassDispatchMath.ThreadGroupCount(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GrassDispatchMath.ThreadGroupCount(GrassDispatchMath.MaximumDispatchCells + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => GrassDispatchMath.FloorDivide(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => GrassDispatchMath.FloorDivide(1, -1));
    }
}
