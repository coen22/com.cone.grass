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

    [TestCase(0, 0, 7)]
    [TestCase(-1000, -2, 5)]
    public void NarrowCameraWindowsCountClippedCellsInsteadOfWholeTiles(int minX, int minZ, int height)
    {
        var cameraGrid = new GrassGridRange(minX, minZ, 300000, height);
        int firstTileX = GrassDispatchMath.FloorDivide(cameraGrid.MinX, 256);
        int lastTileX = GrassDispatchMath.FloorDivide(cameraGrid.MaxX - 1, 256);
        int firstTileZ = GrassDispatchMath.FloorDivide(cameraGrid.MinZ, 256);
        int lastTileZ = GrassDispatchMath.FloorDivide(cameraGrid.MaxZ - 1, 256);
        long oldWholeTileEstimate = (long)(lastTileX - firstTileX + 1) * (lastTileZ - firstTileZ + 1) * 256 * 256;
        Assert.That(oldWholeTileEstimate, Is.GreaterThan(GrassDispatchMath.MaximumCandidates),
            "The regression must exercise a window the former estimate rejected.");
        long candidates = 0;
        for (int tileZ = firstTileZ; tileZ <= lastTileZ; tileZ++)
        for (int tileX = firstTileX; tileX <= lastTileX; tileX++)
        {
            Assert.That(GrassDispatchMath.TryClipTile(cameraGrid, tileX, tileZ, 256, out GrassGridRange clipped), Is.True);
            Assert.That(clipped.MinX, Is.GreaterThanOrEqualTo(cameraGrid.MinX));
            Assert.That(clipped.MinZ, Is.GreaterThanOrEqualTo(cameraGrid.MinZ));
            Assert.That(clipped.MaxX, Is.LessThanOrEqualTo(cameraGrid.MaxX));
            Assert.That(clipped.MaxZ, Is.LessThanOrEqualTo(cameraGrid.MaxZ));
            Assert.That(GrassDispatchMath.TryAddCandidateBudget(candidates, clipped.Width, clipped.Height,
                GrassDispatchMath.MaximumCandidates, out candidates), Is.True);
        }
        Assert.That(candidates, Is.EqualTo(300000L * height));
    }

    [Test]
    public void ClippingOutsideAndExtremeTilesDoesNotWrapCoordinates()
    {
        var grid = new GrassGridRange(-2, -3, 5, 7);
        Assert.That(GrassDispatchMath.TryClipTile(grid, int.MaxValue, 0, int.MaxValue, out _), Is.False);
        Assert.That(GrassDispatchMath.TryClipTile(grid, int.MinValue, 0, int.MaxValue, out _), Is.False);
        Assert.That(GrassDispatchMath.TryClipTile(grid, 2, 0, 256, out _), Is.False);
        Assert.That(GrassDispatchMath.TryClipTile(grid, -1, -1, 256, out GrassGridRange clipped), Is.True);
        Assert.That(clipped.MinX, Is.EqualTo(-2));
        Assert.That(clipped.MinZ, Is.EqualTo(-3));
        Assert.That(clipped.Width, Is.EqualTo(2));
        Assert.That(clipped.Height, Is.EqualTo(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => GrassDispatchMath.TryClipTile(grid, 0, 0, 0, out _));
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
