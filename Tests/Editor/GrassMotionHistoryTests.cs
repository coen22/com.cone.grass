using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class GrassMotionHistoryTests
{
    [TestCase(3)]
    [TestCase(10000)]
    [TestCase(2000000)]
    [TestCase(16000000)]
    public void HistoryTableHasSpaceForEveryConfiguredRootAtHalfLoad(int capacity)
    {
        int size = GrassMotionVectors.HistoryTableSize(capacity);
        Assert.That(size, Is.GreaterThanOrEqualTo(capacity * 2));
        Assert.That(size & (size - 1), Is.Zero);
    }

    [Test]
    [Category("GrassGPU")]
    public void GpuHistorySurvivesQueuePermutationAndHeightChangesAndClampsOverflow()
    {
        RequireComputeDevice();
        // Collide both the first slot and fingerprint; exact XZ must still
        // distinguish the roots while open addressing crosses occupied slots.
        var candidates = new List<Vector4>();
        for (int x = -20000; candidates.Count < 6; x++)
        {
            float z = x * 0.375f + 2f;
            if ((Seed(x, z) & 0xff00000fu) == 0u)
                candidates.Add(new Vector4(x, candidates.Count + 0.25f, z, 0.6f));
        }
        Vector4[] roots = candidates.ToArray();
        Vector4[] queries = { roots[4], roots[2], roots[0], roots[3], roots[1], roots[5] };
        for (int i = 0; i < queries.Length; i++)
            queries[i].y += 17f; // A moving surface retains its old world height.

        RunGpuHistory(roots, new uint[] { 99, 2, 1, 97 },
            new[] { 0, 2, 4, 0 }, new[] { 2, 2, 2, 0 }, new[] { 6, 3, 1, 0 }, queries,
            out Vector4[] previous, out uint[] found, out uint[] dispatch);
        int[] expected = { 4, 2, 0, 3, 1 };
        for (int i = 0; i < expected.Length; i++)
        {
            int index = expected[i];
            Assert.That(found[i], Is.EqualTo(1));
            Assert.That(previous[i].x, Is.EqualTo(roots[index].x));
            Assert.That(previous[i].y, Is.EqualTo(roots[index].y));
            Assert.That(previous[i].z, Is.EqualTo(roots[index].z));
            int rows = index < 2 ? 6 : (index < 4 ? 3 : 1);
            Assert.That(Mathf.FloorToInt(previous[i].w), Is.EqualTo(rows));
            Assert.That((previous[i].w - rows) * 2f, Is.EqualTo(0.6f).Within(0.000002f));
        }
        Assert.That(found[5], Is.Zero, "A buffer slot beyond the actual clamped count must not enter history.");
        Assert.That(dispatch, Is.EqualTo(new uint[] { 1, 1, 1, 1, 1, 1, 1, 1, 1 }));
    }

    [Test]
    [Category("GrassGPU")]
    public void GpuHistoryDeclinesAmbiguousStackedRootsWithoutHidingUniqueRoots()
    {
        RequireComputeDevice();
        Vector4[] roots =
        {
            new Vector4(4.5f, 1f, -7f, 1f),
            new Vector4(4.5f, 9f, -7f, 1f),
            new Vector4(-1f, 3f, 2f, 1f)
        };
        RunGpuHistory(roots, new uint[] { 1, 1, 1, 0 },
            new[] { 0, 1, 2, 0 }, new[] { 1, 1, 1, 0 }, new[] { 6, 3, 1, 0 }, roots,
            out Vector4[] previous, out uint[] found, out _);
        Assert.That(found[0], Is.Zero);
        Assert.That(found[1], Is.Zero);
        Assert.That(found[2], Is.EqualTo(1));
        Assert.That(previous[2].y, Is.EqualTo(3f));
    }

    [Test]
    [Category("GrassGPU")]
    public void GpuHistoryDeclinesDuplicatesEvenWhenTheirProbeWindowIsFull()
    {
        RequireComputeDevice();
        var candidates = new List<Vector4>();
        for (int x = -100000; candidates.Count < 129; x++)
        {
            float z = x * 0.25f + 1f;
            if ((Seed(x, z) & 511u) == 0u)
                candidates.Add(new Vector4(x, candidates.Count, z, 1f));
        }
        Vector4 duplicate = candidates[0];
        duplicate.y += 1000f;
        candidates.Add(duplicate);
        int count = candidates.Count;
        RunGpuHistory(candidates.ToArray(), new uint[] { (uint)count, 0, 0, 0 },
            new[] { 0, count, count, 0 }, new[] { count, 0, 0, 0 }, new[] { 6, 3, 1, 0 },
            new[] { candidates[0] }, out _, out uint[] found, out _);
        Assert.That(found[0], Is.Zero,
            "A duplicate must be marked ambiguous or absent, including when one insertion reaches the probe limit.");
    }

    [TestCase(0)]
    [TestCase(2)]
    [TestCase(5)]
    [TestCase(8)]
    [Category("GrassGPU")]
    public void GpuPreviousSurfaceMatchesActualMeshTrianglesAcrossLodChanges(int subdivisions)
    {
        RequireComputeDevice();
        ComputeShader shader = Resources.Load<ComputeShader>("GrassMotionHistoryQuery");
        Assert.That(shader, Is.Not.Null);
        Mesh mesh = InfiniteGrassRenderer.CreateBladeMesh(subdivisions);
        var buffers = new List<GraphicsBuffer>();
        try
        {
            Vector2[] uv = mesh.uv;
            var materialCoordinates = new Vector2[uv.Length];
            var worldPositions = new Vector3[uv.Length];
            for (int i = 0; i < uv.Length; i++)
            {
                materialCoordinates[i] = new Vector2((uv[i].x - 0.5f) * (1f - uv[i].y), uv[i].y);
                worldPositions[i] = TestRowCenter(uv[i].y) + (uv[i].x * 2f - 1f) * TestRowSpan(uv[i].y);
            }
            var queries = new List<Vector4>();
            var edges = new List<Vector4>();
            var expected = new List<Vector3>();
            int rows = subdivisions + 1;
            for (int heightIndex = 1; heightIndex < 20; heightIndex++)
            {
                float height = heightIndex / 20f;
                int row = Mathf.Min(Mathf.FloorToInt(height * rows), rows - 1);
                float lower = row / (float)rows;
                float upper = (row + 1) / (float)rows;
                for (int across = 1; across < 10; across++)
                {
                    var point = new Vector2((across / 10f - 0.5f) * (1f - height), height);
                    queries.Add(new Vector4(point.x, point.y, lower, upper));
                    edges.Add(TestRowCenter(lower));
                    edges.Add(TestRowSpan(lower));
                    edges.Add(TestRowCenter(upper));
                    edges.Add(TestRowSpan(upper));
                    expected.Add(SampleActualTriangles(point, materialCoordinates, worldPositions, mesh.triangles));
                }
            }
            GraphicsBuffer queryBuffer = Allocate(buffers, queries.Count, 16);
            GraphicsBuffer edgeBuffer = Allocate(buffers, edges.Count, 16);
            GraphicsBuffer resultBuffer = Allocate(buffers, queries.Count, 16);
            queryBuffer.SetData(queries);
            edgeBuffer.SetData(edges);
            int kernel = shader.FindKernel("QueryTriangles");
            shader.SetInt("_QueryCount", queries.Count);
            shader.SetBuffer(kernel, "_Queries", queryBuffer);
            shader.SetBuffer(kernel, "_Edges", edgeBuffer);
            shader.SetBuffer(kernel, "_Results", resultBuffer);
            shader.Dispatch(kernel, (queries.Count + 63) / 64, 1, 1);
            var actual = new Vector4[queries.Count];
            resultBuffer.GetData(actual);
            for (int i = 0; i < actual.Length; i++)
                Assert.That(Vector3.Distance(actual[i], expected[i]), Is.LessThan(0.00001f),
                    "Motion must follow the previous triangulated surface at a fragment, even when the current LOD has fewer vertices.");
        }
        finally
        {
            foreach (GraphicsBuffer buffer in buffers)
                buffer.Dispose();
            UnityEngine.Object.DestroyImmediate(mesh);
        }
    }

    private static Vector3 TestRowCenter(float height)
    {
        return new Vector3(height * height + 0.1f * Mathf.Sin(height * 9f), height * 2f, height * height * height);
    }

    private static Vector3 TestRowSpan(float height)
    {
        // Turning row spans make quads non-planar, so bilinear interpolation is
        // insufficient; the actual mesh's diagonal must be respected.
        return new Vector3(Mathf.Cos(height * 2f), 0.1f * height, Mathf.Sin(height * 2f)) * (0.5f * (1f - height));
    }

    private static Vector3 SampleActualTriangles(Vector2 point, Vector2[] uv, Vector3[] positions, int[] triangles)
    {
        for (int i = 0; i < triangles.Length; i += 3)
        {
            int ia = triangles[i], ib = triangles[i + 1], ic = triangles[i + 2];
            Vector2 a = uv[ia], b = uv[ib], c = uv[ic];
            float denominator = Cross(b - a, c - a);
            float wb = Cross(point - a, c - a) / denominator;
            float wc = Cross(b - a, point - a) / denominator;
            float wa = 1f - wb - wc;
            if (Mathf.Min(wa, Mathf.Min(wb, wc)) >= -0.00001f)
                return positions[ia] * wa + positions[ib] * wb + positions[ic] * wc;
        }
        throw new InvalidOperationException("Test point was outside the generated blade mesh.");
    }

    private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

    private static void RunGpuHistory(Vector4[] roots, uint[] counts, int[] offsets, int[] capacities,
        int[] rows, Vector4[] queries, out Vector4[] previous, out uint[] found, out uint[] dispatch)
    {
        ComputeShader history = Resources.Load<ComputeShader>("InfiniteGrassMotionHistory");
        ComputeShader query = Resources.Load<ComputeShader>("GrassMotionHistoryQuery");
        Assert.That(history, Is.Not.Null);
        Assert.That(query, Is.Not.Null);
        int tableSize = GrassMotionVectors.HistoryTableSize(roots.Length);
        var buffers = new List<GraphicsBuffer>();
        try
        {
            GraphicsBuffer positions = Allocate(buffers, roots.Length, 16);
            GraphicsBuffer countBuffer = Allocate(buffers, 4, 4);
            GraphicsBuffer keys = Allocate(buffers, tableSize, 4);
            GraphicsBuffer oldRoots = Allocate(buffers, roots.Length, 16);
            GraphicsBuffer snapshotCounts = Allocate(buffers, 3, 4);
            GraphicsBuffer arguments = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.IndirectArguments, 9, 4);
            buffers.Add(arguments);
            positions.SetData(roots);
            countBuffer.SetData(counts);
            int clearKernel = history.FindKernel("ClearHistory");
            int buildKernel = history.FindKernel("BuildHistory");
            int captureKernel = history.FindKernel("CaptureCounts");
            int storeKernel = history.FindKernel("StoreRoots");
            history.SetInts("_GrassHistoryOffsets", offsets);
            history.SetInts("_GrassHistoryCapacities", capacities);
            history.SetInts("_GrassHistoryRows", rows);
            history.SetInt("_GrassHistoryMask", tableSize - 1);
            history.SetInt("_GrassHistoryClearWidth", ((tableSize + 127) / 128) * 128);
            history.SetBuffer(captureKernel, "_GrassHistoryCounts", countBuffer);
            history.SetBuffer(captureKernel, "_GrassHistorySnapshotCounts", snapshotCounts);
            history.SetBuffer(captureKernel, "_GrassHistoryDispatch", arguments);
            history.Dispatch(captureKernel, 1, 1, 1);
            history.SetBuffer(storeKernel, "_GrassHistoryPositions", positions);
            history.SetBuffer(storeKernel, "_GrassHistoryCounts", countBuffer);
            history.SetBuffer(storeKernel, "_GrassHistoryRoots", oldRoots);
            for (int lod = 0; lod < 3; lod++)
            {
                history.SetInt("_GrassHistoryLod", lod);
                int groups = Mathf.Max(1, (int)((Math.Min(counts[lod], (uint)capacities[lod]) + 63u) / 64u));
                history.Dispatch(storeKernel, groups, 1, 1);
            }
            history.SetBuffer(clearKernel, "_GrassHistoryKeys", keys);
            history.SetBuffer(clearKernel, "_GrassHistoryCounts", snapshotCounts);
            history.SetBuffer(clearKernel, "_GrassHistoryDispatch", arguments);
            history.Dispatch(clearKernel, (tableSize + 127) / 128, 1, 1);
            history.SetBuffer(buildKernel, "_GrassHistoryPositions", oldRoots);
            history.SetBuffer(buildKernel, "_GrassHistoryCounts", snapshotCounts);
            history.SetBuffer(buildKernel, "_GrassHistoryKeys", keys);
            for (int lod = 0; lod < 3; lod++)
            {
                history.SetInt("_GrassHistoryLod", lod);
                int groups = Mathf.Max(1, (int)((Math.Min(counts[lod], (uint)capacities[lod]) + 63u) / 64u));
                history.Dispatch(buildKernel, groups, 1, 1);
            }

            // Compaction overwrites the live buffer on the next render. The
            // immutable snapshot must remain valid independently of that buffer.
            var nextRoots = (Vector4[])roots.Clone();
            for (int i = 0; i < nextRoots.Length; i++)
                nextRoots[i].y += 100f;
            positions.SetData(nextRoots);

            GraphicsBuffer queryBuffer = Allocate(buffers, queries.Length, 16);
            GraphicsBuffer results = Allocate(buffers, queries.Length, 16);
            GraphicsBuffer flags = Allocate(buffers, queries.Length, 4);
            queryBuffer.SetData(queries);
            int kernel = query.FindKernel("Query");
            query.SetInt("_QueryCount", queries.Length);
            query.SetInt("_HashMask", tableSize - 1);
            query.SetBuffer(kernel, "_Queries", queryBuffer);
            query.SetBuffer(kernel, "_GrassPreviousRootKeys", keys);
            query.SetBuffer(kernel, "_GrassPreviousRoots", oldRoots);
            query.SetBuffer(kernel, "_Results", results);
            query.SetBuffer(kernel, "_Found", flags);
            query.Dispatch(kernel, (queries.Length + 63) / 64, 1, 1);
            previous = new Vector4[queries.Length];
            found = new uint[queries.Length];
            dispatch = new uint[9];
            // Test-only synchronization: production history never reads positions back.
            results.GetData(previous);
            flags.GetData(found);
            arguments.GetData(dispatch);
        }
        finally
        {
            foreach (GraphicsBuffer buffer in buffers)
                buffer.Dispose();
        }
    }

    private static GraphicsBuffer Allocate(List<GraphicsBuffer> buffers, int count, int stride)
    {
        var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, stride);
        buffers.Add(buffer);
        return buffer;
    }

    private static uint Seed(float x, float z)
    {
        return Hash(unchecked((uint)BitConverter.SingleToInt32Bits(x))) ^
            Hash(unchecked((uint)BitConverter.SingleToInt32Bits(z) + 0x9e3779b9u));
    }

    private static uint Hash(uint value)
    {
        unchecked
        {
            value ^= value >> 16;
            value *= 0x85ebca6b;
            value ^= value >> 13;
            value *= 0xc2b2ae35;
            return value ^ (value >> 16);
        }
    }

    private static void RequireComputeDevice()
    {
        if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsIndirectArgumentsBuffer ||
            SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            Assert.Ignore("This GPU integration test requires a compute-capable graphics device; it is not a CPU simulation.");
    }
}
