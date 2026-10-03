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

    [TestCase(290, 0.4999920725822449f, 0.1f, true, 1f)]
    [TestCase(-95, 0.4999975562095642f, 0.1f, true, 1f)]
    [TestCase(163, -0.4999997019767761f, 0.1f, false, 1f)]
    [TestCase(-44, -0.49999868869781494f, 0.1f, false, 1f)]
    [TestCase(16777219, 0f, 0.125f, true, 32f)]
    [TestCase(16777217, 0f, 0.125f, false, 32f)]
    [TestCase(-16777219, 0f, 0.125f, false, 32f)]
    [TestCase(-16777217, 0f, 0.125f, true, 32f)]
    [TestCase(1000000033, 0f, 0.125f, true, 32f)]
    [TestCase(1000000003, 0f, 0.125f, false, 32f)]
    [TestCase(-1000000033, 0f, 0.125f, false, 32f)]
    [TestCase(-1000000003, 0f, 0.125f, true, 32f)]
    [TestCase(1073741311, 0f, 0.125f, true, 16f)]
    [TestCase(-1073741311, 0f, 0.125f, false, 16f)]
    public void FloatRoundedCandidateOnEitherWorldBoundRetainsItsCell(
        int cell, float jitter, float spacing, bool minimum, float width)
    {
        // Evaluate a candidate independently of the inverse range calculation.
        // The first four jitters come from seeded cells (290,6), (-95,602),
        // (163,483), (-44,-253); later cases also exercise int-to-float rounding.
        float coordinate = (float)((double)(float)cell + jitter);
        float world = (float)((double)coordinate * spacing);
        float center = world + (minimum ? width * 0.5f : -width * 0.5f);
        Bounds bounds = new Bounds(new Vector3(center, 0f, 0f), new Vector3(width, 0f, width));
        Assert.That(minimum ? bounds.min.x : bounds.max.x, Is.EqualTo(world),
            "Bounds construction must preserve the endpoint that exposed the omitted cell.");

        Assert.That(GrassDispatchMath.TryGetGridRange(bounds, spacing, out GrassGridRange range), Is.True);
        Assert.That(cell, Is.GreaterThanOrEqualTo(range.MinX).And.LessThan(range.MaxX));
        Assert.That(range.Width, Is.GreaterThan(0));
        Assert.That(range.Height, Is.GreaterThan(0));
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

    [TestCase(-1, 37076, false, -0.050000209361314774f)]
    [TestCase(1, 446604, false, 0.05000074580311775f)]
    [TestCase(226327, -1, true, -0.05000084266066551f)]
    [TestCase(66162, 1, true, 0.050000738352537155f)]
    public void FilterExpansionRetainsSeededRootsAcrossEitherSignedAxis(
        int cellX, int cellZ, bool transpose, float expectedRoot)
    {
        const float spacing = 0.1f;
        const float margin = 0.050001f;
        Vector2 root = SeededRoot(cellX, cellZ, spacing);
        int boundaryCell = transpose ? cellZ : cellX;
        float boundaryRoot = transpose ? root.y : root.x;
        Assert.That(boundaryRoot, Is.EqualTo(expectedRoot));
        Bounds source = new Bounds(transpose
            ? new Vector3(root.x, 7f, -boundaryCell * 512f)
            : new Vector3(-boundaryCell * 512f, 7f, root.y),
            transpose ? new Vector3(2f, 6f, 1024f) : new Vector3(1024f, 6f, 2f));
        float sourceEdge = boundaryCell < 0
            ? (transpose ? source.min.z : source.min.x)
            : (transpose ? source.max.z : source.max.x);
        Assert.That(sourceEdge, Is.Zero);
        Assert.That(Math.Abs((double)boundaryRoot - sourceEdge), Is.LessThan(margin),
            "This actual shader root lies inside the source's requested filter support.");

        Bounds former = source;
        former.Expand(new Vector3(margin * 2f, 0f, margin * 2f));
        Assert.That(GrassDispatchMath.TryGetGridRange(former, spacing, out GrassGridRange omitted), Is.True);
        Assert.That(boundaryCell < (transpose ? omitted.MinZ : omitted.MinX) ||
            boundaryCell >= (transpose ? omitted.MaxZ : omitted.MaxX), Is.True,
            "The former extent addition must reproduce the missing cell.");
        if (boundaryCell < 0)
            Assert.That(GrassDispatchMath.FloorDivide(transpose ? omitted.MinZ : omitted.MinX, 256), Is.Zero,
                "Rounding the minimum inward formerly skipped the complete negative tile.");

        Bounds expanded = GrassDispatchMath.ExpandXZ(source, margin);
        AssertExpandedSupport(source, expanded, margin);
        Assert.That(GrassDispatchMath.TryGetGridRange(expanded, spacing, out GrassGridRange range), Is.True);
        Assert.That(cellX, Is.GreaterThanOrEqualTo(range.MinX).And.LessThan(range.MaxX));
        Assert.That(cellZ, Is.GreaterThanOrEqualTo(range.MinZ).And.LessThan(range.MaxZ));
    }

    [TestCase(6, 214015, false, 0.5500008463859558f)]
    [TestCase(167631, 6, true, 0.5500006079673767f)]
    public void CameraClippingRetainsSeededRootsInsideTheExpandedFilterEnvelope(
        int cellX, int cellZ, bool transpose, float expectedRoot)
    {
        const float spacing = 0.1f;
        const float captureExtent = 563.20105f;
        const int captureResolution = 2048;
        const float cameraExtent = 512.4873657226562f;
        float margin = (2f * captureExtent) / captureResolution;
        Vector2 root = SeededRoot(cellX, cellZ, spacing);
        float boundaryRoot = transpose ? root.y : root.x;
        Assert.That(boundaryRoot, Is.EqualTo(expectedRoot));
        Bounds source = new Bounds(transpose
            ? new Vector3(root.x, 7f, -500f) : new Vector3(-500f, 7f, root.y),
            transpose ? new Vector3(2f, 6f, 1000f) : new Vector3(1000f, 6f, 2f));
        Bounds camera = new Bounds(transpose
            ? new Vector3(root.x, 99f, 0f) : new Vector3(0f, 99f, root.y),
            new Vector3(cameraExtent * 2f, 8f, cameraExtent * 2f));
        Assert.That(boundaryRoot, Is.LessThan(margin),
            "The root is inside the source's one-capture-texel envelope, independent of rendered density acceptance.");
        Assert.That(root.x, Is.InRange(camera.min.x, camera.max.x));
        Assert.That(root.y, Is.InRange(camera.min.z, camera.max.z));

        Bounds expanded = GrassDispatchMath.ExpandXZ(source, margin);
        Assert.That(GrassDispatchMath.TryGetGridRange(expanded, spacing, out GrassGridRange beforeClipping), Is.True);
        Assert.That(cellX, Is.GreaterThanOrEqualTo(beforeClipping.MinX).And.LessThan(beforeClipping.MaxX));
        Assert.That(cellZ, Is.GreaterThanOrEqualTo(beforeClipping.MinZ).And.LessThan(beforeClipping.MaxZ));
        Vector3 minimum = new Vector3(Mathf.Max(expanded.min.x, camera.min.x), 0f,
            Mathf.Max(expanded.min.z, camera.min.z));
        Vector3 maximum = new Vector3(Mathf.Min(expanded.max.x, camera.max.x), 0f,
            Mathf.Min(expanded.max.z, camera.max.z));
        Bounds former = default;
        former.SetMinMax(minimum, maximum);
        Assert.That(GrassDispatchMath.TryGetGridRange(former, spacing, out GrassGridRange omitted), Is.True);
        Assert.That(transpose ? omitted.MaxZ : omitted.MaxX, Is.EqualTo(6),
            "The later native Bounds conversion must reproduce losing the cell even after outward expansion.");

        Assert.That(GrassDispatchMath.TryIntersectXZ(expanded, camera, out Bounds intersection), Is.True);
        Assert.That(intersection.min.x, Is.LessThanOrEqualTo(minimum.x));
        Assert.That(intersection.min.z, Is.LessThanOrEqualTo(minimum.z));
        Assert.That(intersection.max.x, Is.GreaterThanOrEqualTo(maximum.x));
        Assert.That(intersection.max.z, Is.GreaterThanOrEqualTo(maximum.z));
        Assert.That(intersection.center.y, Is.Zero);
        Assert.That(intersection.extents.y, Is.Zero);
        Assert.That(GrassDispatchMath.TryGetGridRange(intersection, spacing, out GrassGridRange retained), Is.True);
        Assert.That(cellX, Is.GreaterThanOrEqualTo(retained.MinX).And.LessThan(retained.MaxX));
        Assert.That(cellZ, Is.GreaterThanOrEqualTo(retained.MinZ).And.LessThan(retained.MaxZ));
    }

    [Test]
    public void SourceCameraIntersectionRetainsEmptyAndInvalidOverlapBehavior()
    {
        Bounds source = new Bounds(Vector3.zero, new Vector3(2f, 4f, 2f));
        Bounds[] emptyOverlaps =
        {
            new Bounds(Vector3.right * 3f, Vector3.one * 2f),
            new Bounds(Vector3.right * 2f, Vector3.one * 2f),
            new Bounds(Vector3.forward * 3f, Vector3.one * 2f),
            new Bounds(Vector3.forward * 2f, Vector3.one * 2f),
            new Bounds(Vector3.zero, new Vector3(0f, 2f, 1f)),
            new Bounds(Vector3.zero, new Vector3(1f, 2f, 0f))
        };
        foreach (Bounds camera in emptyOverlaps)
        {
            Assert.That(GrassDispatchMath.TryIntersectXZ(source, camera, out Bounds intersection), Is.False);
            AssertBoundsRepresentation(intersection, default);
        }

        Bounds invalid = new Bounds(new Vector3(float.NaN, 0f, 0f), Vector3.one);
        Vector3 minimum = new Vector3(Mathf.Max(source.min.x, invalid.min.x), 0f,
            Mathf.Max(source.min.z, invalid.min.z));
        Vector3 maximum = new Vector3(Mathf.Min(source.max.x, invalid.max.x), 0f,
            Mathf.Min(source.max.z, invalid.max.z));
        Bounds former = default;
        former.SetMinMax(minimum, maximum);
        Assert.That(GrassDispatchMath.TryIntersectXZ(source, invalid, out Bounds result), Is.True);
        AssertBoundsRepresentation(result, former);
        Assert.That(GrassDispatchMath.TryGetGridRange(result, 0.1f, out _), Is.False,
            "Unrepresentable overlap must still reach the existing downstream range rejection.");
    }

    [TestCase(1e8f, 0.0000001f)]
    [TestCase(1e30f, 1e-10f)]
    public void FilterExpansionKeepsTinyEndpointsAcrossWideCancellation(float extent, float margin)
    {
        Bounds source = new Bounds(new Vector3(extent, 9.25f, -extent), Vector3.zero);
        source.extents = new Vector3(extent, 0.125f, extent);
        Assert.That(source.min.x, Is.Zero);
        Assert.That(source.max.z, Is.Zero);
        Bounds expanded = GrassDispatchMath.ExpandXZ(source, margin);
        AssertExpandedSupport(source, expanded, margin);
        Assert.That(expanded.min.x, Is.LessThan(0f));
        Assert.That(expanded.max.z, Is.GreaterThan(0f));
    }

    [Test]
    public void ZeroFilterMarginPreservesTheOriginalCenterAndExtents()
    {
        // The X endpoints collapse at float precision. A zero-margin request
        // must not reconstruct and discard the still-authored nonzero extent.
        Bounds source = new Bounds(new Vector3(16777216f, 7.25f, -3.25f), new Vector3(0.5f, 3f, 1f));
        AssertBoundsRepresentation(GrassDispatchMath.ExpandXZ(source, 0f), source);
    }

    [Test]
    public void InvalidFilterExpansionRetainsItsFormerExceptionsAndRangeRejection()
    {
        foreach (float margin in new[] { -1f, float.NaN, float.PositiveInfinity, float.MaxValue })
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                GrassDispatchMath.ExpandXZ(new Bounds(Vector3.zero, Vector3.one), margin));

        Bounds inverted = new Bounds(Vector3.zero, Vector3.zero);
        inverted.extents = new Vector3(-2f, 3f, -2f);
        Bounds enormous = new Bounds(Vector3.zero, Vector3.zero);
        enormous.extents = new Vector3(float.MaxValue, 3f, float.MaxValue);
        Bounds[] inputs =
        {
            new Bounds(new Vector3(float.NaN, 7f, 0f), Vector3.one),
            new Bounds(new Vector3(float.PositiveInfinity, 7f, 0f), Vector3.one),
            inverted,
            enormous
        };
        for (int index = 0; index < inputs.Length; index++)
        {
            float margin = index == inputs.Length - 1 ? float.MaxValue * 0.5f : 0.25f;
            Bounds former = inputs[index];
            former.Expand(new Vector3(margin * 2f, 0f, margin * 2f));
            Bounds expanded = GrassDispatchMath.ExpandXZ(inputs[index], margin);
            AssertBoundsRepresentation(expanded, former);
            Assert.That(GrassDispatchMath.TryGetGridRange(expanded, 0.1f, out _), Is.False);
        }
    }

    [TestCase(84168, true)]
    [TestCase(140005, false)]
    [TestCase(-86916, true)]
    [TestCase(-144729, false)]
    public void SparseTileBoundsRetainRootsRoundedBeyondTheFormerWorldBounds(int tile, bool minimum)
    {
        const int cells = 256;
        const float spacing = 0.1f;
        const float captureFootprint = 20f / 2048f;
        int first = tile * cells;
        int cell = first + (minimum ? 0 : cells - 1);
        // At these cell magnitudes every supported jitter rounds back to the
        // same integer-to-float coordinate. This is an actual generated root.
        float root = (float)((double)(float)cell * spacing);
        Bounds former = new Bounds(new Vector3((tile + 0.5f) * cells * spacing, 0f, 0f),
            new Vector3((cells + 1) * spacing, 0f, (cells + 1) * spacing));
        former.Expand(new Vector3(captureFootprint * 2f, 0f, captureFootprint * 2f));
        Assert.That(minimum ? former.min.x > root : former.max.x < root, Is.True,
            "The existing capture margin must still leave the root outside the former tile query.");

        var range = new GrassGridRange(first, -128, cells, cells);
        Assert.That(GrassDispatchMath.TryGetCandidateBounds(range, spacing, captureFootprint, out Bounds bounds), Is.True);
        Assert.That((double)bounds.min.x, Is.LessThanOrEqualTo((double)root - captureFootprint));
        Assert.That((double)bounds.max.x, Is.GreaterThanOrEqualTo((double)root + captureFootprint));
        // Querying a source that ends/starts at this root used to reject the
        // entire tile even though grid enumeration already retained its cell.
        float sourceCenter = root + (minimum ? -0.5f : 0.5f);
        Bounds source = new Bounds(new Vector3(sourceCenter, 0f, 0f), new Vector3(1f, 0f, 1f));
        Assert.That(source.min.x <= bounds.max.x && source.max.x >= bounds.min.x, Is.True);

        var transposed = new GrassGridRange(-128, first, cells, cells);
        Assert.That(GrassDispatchMath.TryGetCandidateBounds(transposed, spacing, captureFootprint, out Bounds zBounds), Is.True);
        Assert.That(zBounds.min.z, Is.EqualTo(bounds.min.x));
        Assert.That(zBounds.max.z, Is.EqualTo(bounds.max.x));
    }

    [Test]
    public void CandidateBoundsUseClippedCellsAndIncludeTheFilteringMargin()
    {
        var range = new GrassGridRange(-2, -3, 5, 7);
        Assert.That(GrassDispatchMath.TryGetCandidateBounds(range, 2f, 0.25f, out Bounds bounds), Is.True);
        Assert.That(bounds.min, Is.EqualTo(new Vector3(-5.25f, 0f, -7.25f)));
        Assert.That(bounds.max, Is.EqualTo(new Vector3(5.25f, 0f, 7.25f)));
    }

    [TestCase(1073741311)]
    [TestCase(-1073741311)]
    public void CandidateBoundsKeepSubUlpMarginsAroundCollapsedRootCoordinates(int cell)
    {
        const float spacing = 0.125f;
        const float margin = 0.01f;
        var range = new GrassGridRange(cell, cell, 1, 1);
        Assert.That(GrassDispatchMath.TryGetCandidateBounds(range, spacing, margin, out Bounds bounds), Is.True);
        float root = (float)((double)(float)cell * spacing);
        Assert.That((double)bounds.min.x, Is.LessThanOrEqualTo((double)root - margin));
        Assert.That((double)bounds.max.x, Is.GreaterThanOrEqualTo((double)root + margin));
        Assert.That(bounds.min.z, Is.EqualTo(bounds.min.x));
        Assert.That(bounds.max.z, Is.EqualTo(bounds.max.x));
    }

    [Test]
    public void CandidateBoundsRetainTinyEndpointAcrossALargeRange()
    {
        const float spacing = 0.1f;
        float margin = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(0.05f) + 1);
        var range = new GrassGridRange(1, 0, 1073741000, 1);
        Assert.That(GrassDispatchMath.TryGetCandidateBounds(range, spacing, margin, out Bounds bounds), Is.True);
        double minimum = (double)(0.5f * spacing) - margin;
        Assert.That(minimum, Is.LessThan(0.0));
        Assert.That((double)bounds.min.x, Is.LessThanOrEqualTo(minimum),
            "Center/extents cancellation must not collapse the tiny negative endpoint to zero.");
    }

    [Test]
    public void CandidateBoundsRejectInvalidRangesAndUnrepresentableEndpoints()
    {
        var unit = new GrassGridRange(0, 0, 1, 1);
        Assert.That(GrassDispatchMath.TryGetCandidateBounds(unit, 0f, 0f, out _), Is.False);
        Assert.That(GrassDispatchMath.TryGetCandidateBounds(unit, float.NaN, 0f, out _), Is.False);
        Assert.That(GrassDispatchMath.TryGetCandidateBounds(unit, 1f, -1f, out _), Is.False);
        Assert.That(GrassDispatchMath.TryGetCandidateBounds(unit, 1f, float.PositiveInfinity, out _), Is.False);
        Assert.That(GrassDispatchMath.TryGetCandidateBounds(new GrassGridRange(0, 0, 0, 1), 1f, 0f, out _), Is.False);
        Assert.That(GrassDispatchMath.TryGetCandidateBounds(new GrassGridRange(int.MaxValue, 0, 2, 1), 1f, 0f, out _), Is.False);
        Assert.That(GrassDispatchMath.TryGetCandidateBounds(new GrassGridRange(int.MinValue, 0, 1, 1), 1f, 0f, out _), Is.False);
        Assert.That(GrassDispatchMath.TryGetCandidateBounds(new GrassGridRange(10, 0, 1, 1), float.MaxValue, 0f, out _), Is.False);
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

    private static void AssertExpandedSupport(Bounds source, Bounds expanded, float margin)
    {
        Assert.That((double)expanded.min.x, Is.LessThanOrEqualTo((double)source.min.x - margin));
        Assert.That((double)expanded.min.z, Is.LessThanOrEqualTo((double)source.min.z - margin));
        Assert.That((double)expanded.max.x, Is.GreaterThanOrEqualTo((double)source.max.x + margin));
        Assert.That((double)expanded.max.z, Is.GreaterThanOrEqualTo((double)source.max.z + margin));
        Assert.That(expanded.center.y, Is.EqualTo(source.center.y));
        Assert.That(expanded.extents.y, Is.EqualTo(source.extents.y));
    }

    private static void AssertBoundsRepresentation(Bounds actual, Bounds expected)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            Assert.That(BitConverter.SingleToInt32Bits(actual.center[axis]),
                Is.EqualTo(BitConverter.SingleToInt32Bits(expected.center[axis])));
            Assert.That(BitConverter.SingleToInt32Bits(actual.extents[axis]),
                Is.EqualTo(BitConverter.SingleToInt32Bits(expected.extents[axis])));
        }
    }

    private static Vector2 SeededRoot(int x, int z, float spacing)
    {
        // Forward root generation from the shipped compute hash; independent
        // of the inverse grid calculation and Bounds expansion under test.
        uint seed = Hash(Hash(unchecked((uint)x) ^ 0x9e3779b9u) ^ unchecked((uint)z));
        float jitterX = (Hash(seed ^ 0xa511e9b3u) >> 8) * (1f / 16777216f) - 0.5f;
        float jitterZ = (Hash(seed ^ 0x63d83595u) >> 8) * (1f / 16777216f) - 0.5f;
        float coordinateX = (float)((double)(float)x + jitterX);
        float coordinateZ = (float)((double)(float)z + jitterZ);
        return new Vector2((float)((double)coordinateX * spacing), (float)((double)coordinateZ * spacing));
    }

    private static uint Hash(uint value)
    {
        unchecked
        {
            value ^= value >> 16;
            value *= 0x85ebca6bu;
            value ^= value >> 13;
            value *= 0xc2b2ae35u;
            return value ^ (value >> 16);
        }
    }
}
