using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

/// <summary>Executes the shipped kernels, including group barriers and native indirect argument writes.</summary>
[Category("GrassGPU")]
public sealed class GrassGpuGenerationTests
{
    private const string ComputePath = "Packages/com.cone.grass/Runtime/Compute/GrassPositionsCompute.compute";
    private static readonly Vector4 Sentinel = new Vector4(-901f, -902f, -903f, -904f);
    private ComputeShader shader;
    private GraphicsBuffer positions;
    private GraphicsBuffer counts;
    private GraphicsBuffer arguments;
    private Texture2D height;
    private Texture2D density;
    private Texture2D exclusion;
    private Texture2D white;
    private int resetKernel;
    private int generateKernel;
    private int finalizeKernel;
    private readonly int[] capacities = { 200, 200, 200, 0 };
    private readonly int[] offsets = { 2, 204, 406, 0 };
    private GraphicsBuffer.IndirectDrawIndexedArgs[] initialArguments;

    [SetUp]
    public void SetUp()
    {
        if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsIndirectArgumentsBuffer ||
            SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ||
            !SystemInfo.SupportsTextureFormat(TextureFormat.RGBAFloat))
            Assert.Ignore("Grass GPU tests require compute, indirect arguments, and RGBAFloat texture support.");

        ComputeShader source = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputePath);
        Assert.That(source, Is.Not.Null, "The installed package compute shader must import successfully.");
        shader = Object.Instantiate(source);
        resetKernel = shader.FindKernel("ResetCounts");
        generateKernel = shader.FindKernel("CSMain");
        finalizeKernel = shader.FindKernel("FinalizeArgs");
        positions = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 608, sizeof(float) * 4);
        counts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 4, sizeof(uint));
        arguments = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments,
            3, GraphicsBuffer.IndirectDrawIndexedArgs.size);
        var untouched = new Vector4[positions.count];
        for (int index = 0; index < untouched.Length; index++)
            untouched[index] = Sentinel;
        positions.SetData(untouched);
        initialArguments = new GraphicsBuffer.IndirectDrawIndexedArgs[3];
        for (int lod = 0; lod < 3; lod++)
        {
            initialArguments[lod] = new GraphicsBuffer.IndirectDrawIndexedArgs
            {
                indexCountPerInstance = (uint)(33 - lod * 6),
                instanceCount = 777,
                startIndex = (uint)(lod + 2),
                baseVertexIndex = (uint)(lod + 5),
                startInstance = (uint)(lod + 9)
            };
        }
        arguments.SetData(initialArguments);
        counts.SetData(new uint[] { 123, 456, 789, 99 });

        height = CreateTexture(new Color(3f, 1f, 0f, 1f));
        density = CreateTexture(Color.white);
        exclusion = CreateTexture(Color.black);
        white = CreateTexture(Color.white);
        shader.SetTexture(generateKernel, "_GrassHeightMapRT", height);
        shader.SetTexture(generateKernel, "_GrassDensityRT", density);
        shader.SetTexture(generateKernel, "_GrassMaskMapRT", exclusion);
        shader.SetTexture(generateKernel, "_TerrainHeightmap", white);
        shader.SetTexture(generateKernel, "_TerrainHoles", white);
        shader.SetBuffer(resetKernel, "_GrassCounts", counts);
        shader.SetBuffer(generateKernel, "_GrassCounts", counts);
        shader.SetBuffer(generateKernel, "_GrassPositions", positions);
        shader.SetBuffer(finalizeKernel, "_GrassCounts", counts);
        shader.SetBuffer(finalizeKernel, "_GrassIndirectArgs", arguments);

        shader.SetFloat("_Spacing", 1f);
        shader.SetFloat("_DrawDistance", 1000f);
        shader.SetFloat("_TextureUpdateThreshold", 1f);
        shader.SetFloat("_FullDensityDistance", 900f);
        shader.SetFloat("_DensityFalloffExponent", 4f);
        shader.SetFloat("_DensityTransition", 0f);
        shader.SetFloat("_BladeBoundsRadius", 1f);
        shader.SetFloat("_LodTransitionWidth", 0f);
        shader.SetVector("_CameraPosition", Vector4.zero);
        shader.SetVector("_CenterPos", Vector4.zero);
        shader.SetVector("_LodDistances", new Vector4(100f, 200f, 0f, 0f));
        shader.SetInt("_AuthoredAreas", 1);
        shader.SetInt("_UseTerrain", 0);
        shader.SetInt("_TerrainHasHoles", 0);
        shader.SetVector("_TerrainOrigin", Vector4.zero);
        shader.SetVector("_TerrainSize", new Vector4(16f, 20f, 16f, 0f));
        shader.SetInts("_LodCapacities", capacities);
        shader.SetInts("_LodOffsets", offsets);
        shader.SetInt("_ArgsStride", GraphicsBuffer.IndirectDrawIndexedArgs.size);
        shader.SetInt("_ArgsInstanceCountOffset", FindInstanceCountOffset());
        var planes = new Vector4[6];
        for (int index = 0; index < planes.Length; index++)
            planes[index] = new Vector4(0f, 0f, 0f, 1f);
        shader.SetVectorArray("_FrustumPlanes", planes);
    }

    [TearDown]
    public void TearDown()
    {
        positions?.Dispose();
        counts?.Dispose();
        arguments?.Dispose();
        if (shader) Object.DestroyImmediate(shader);
        if (height) Object.DestroyImmediate(height);
        if (density) Object.DestroyImmediate(density);
        if (exclusion) Object.DestroyImmediate(exclusion);
        if (white) Object.DestroyImmediate(white);
    }

    [Test]
    public void RoundedEdgeGroupsGenerateEachSignedGridCellExactlyOnce()
    {
        Generate(-7, -5, 13, 9);
        Assert.That(ReadCounts(), Is.EqualTo(new uint[] { 117, 0, 0, 0 }));
        AssertArguments(117, 0, 0);
        Vector4[] roots = ReadPositions();
        var cells = new HashSet<Vector2Int>();
        for (int index = offsets[0]; index < offsets[0] + 117; index++)
        {
            Vector4 root = roots[index];
            Assert.That(root.y, Is.EqualTo(3f).Within(0.0001f));
            Assert.That(root.w, Is.EqualTo(1f).Within(0.0001f));
            var cell = new Vector2Int(Mathf.FloorToInt(root.x + 0.5f), Mathf.FloorToInt(root.z + 0.5f));
            Assert.That(cell.x, Is.InRange(-7, 5));
            Assert.That(cell.y, Is.InRange(-5, 3));
            Assert.That(cells.Add(cell), Is.True, "Each candidate must reserve a unique slot even across workgroups.");
        }
        Assert.That(cells.Count, Is.EqualTo(117));
        AssertGuards(roots, 0, 117);
    }

    [TestCase(290, 6, true, 29.05000114440918f)]
    [TestCase(-95, 602, true, -9.449999809265137f)]
    [TestCase(163, 483, false, 16.25f)]
    [TestCase(-44, -253, false, -4.450000286102295f)]
    [TestCase(8388610, 3, true, 838861f)]
    public void PreciseGpuRootOnAWorldBoundRetainsItsGridCell(
        int cellX, int cellZ, bool minimum, float expectedX)
    {
        const float spacing = 0.1f;
        shader.SetFloat("_Spacing", spacing);
        shader.SetVector("_CameraPosition", new Vector4(cellX * spacing, 0f, cellZ * spacing, 0f));
        shader.SetVector("_CenterPos", new Vector4(cellX * spacing, cellZ * spacing, 0f, 0f));
        Generate(cellX, cellZ, 1, 1);
        Assert.That(ReadCounts(), Is.EqualTo(new uint[] { 1, 0, 0, 0 }));
        Vector4 root = ReadPositions()[offsets[0]];
        Assert.That(root.x, Is.EqualTo(expectedX),
            "Root arithmetic must retain the written jitter-add then spacing-product order. " +
            "The former optimized order moved cell (8388610,3) to X=838861.125.");

        Bounds bounds = new Bounds(new Vector3(root.x + (minimum ? 0.5f : -0.5f), 0f, root.z),
            new Vector3(1f, 0f, 1f));
        Assert.That(minimum ? bounds.min.x : bounds.max.x, Is.EqualTo(root.x));
        Assert.That(GrassDispatchMath.TryGetGridRange(bounds, spacing, out GrassGridRange range), Is.True);
        Assert.That(cellX, Is.GreaterThanOrEqualTo(range.MinX).And.LessThan(range.MaxX));
        Assert.That(cellZ, Is.GreaterThanOrEqualTo(range.MinZ).And.LessThan(range.MaxZ));
        AssertArguments(1, 0, 0);
        AssertGuards(ReadPositions(), 0, 1);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void CapacityOverflowCannotWriteOutsideItsLodPartition(int lod)
    {
        shader.SetInts("_LodCapacities", 3, 3, 3, 0);
        shader.SetVector("_LodDistances", lod == 0 ? new Vector4(100f, 200f, 0f, 0f) :
            lod == 1 ? new Vector4(0f, 100f, 0f, 0f) : Vector4.zero);
        Generate(-7, -5, 13, 9);
        var expected = new uint[4];
        expected[lod] = 117;
        expected[3] = 114;
        Assert.That(ReadCounts(), Is.EqualTo(expected), "Attempted counts and rejected overflow retain separate meanings.");
        AssertArguments(lod == 0 ? 3u : 0u, lod == 1 ? 3u : 0u, lod == 2 ? 3u : 0u);
        AssertGuards(ReadPositions(), lod, 3);
    }

    [Test]
    public void NoDispatchAfterAPopulatedFrameClearsAllIndirectInstanceCounts()
    {
        Generate(-7, -5, 13, 9);
        AssertArguments(117, 0, 0);
        shader.Dispatch(resetKernel, 1, 1, 1);
        shader.Dispatch(finalizeKernel, 1, 1, 1);
        Assert.That(ReadCounts(), Is.EqualTo(new uint[4]));
        AssertArguments(0, 0, 0);
    }

    [TestCase(0f, 0f, 1f)]
    [TestCase(1f, 1f, 1f)]
    [TestCase(1f, 0f, 0f)]
    public void EmptyDensityExclusionAndMissingSurfaceRejectAllCandidates(float authored, float mask, float valid)
    {
        FillTexture(density, new Color(authored, 0f, 0f, 1f));
        FillTexture(exclusion, new Color(mask, 0f, 0f, 1f));
        FillTexture(height, new Color(3f, valid, 0f, 1f));
        Generate(-7, -5, 13, 9);
        Assert.That(ReadCounts(), Is.EqualTo(new uint[4]));
        AssertArguments(0, 0, 0);
        AssertGuards(ReadPositions(), 0, 0);
    }

    [Test]
    public void NativeTerrainHeightUsesWorldOriginAndAllHolesRemoveGrass()
    {
        var data = new TerrainData { heightmapResolution = 33, size = new Vector3(16f, 20f, 16f) };
        try
        {
            var heights = new float[33, 33];
            for (int y = 0; y < 33; y++)
                for (int x = 0; x < 33; x++)
                    heights[y, x] = 0.35f;
            data.SetHeights(0, 0, heights);
            var holes = new bool[data.holesResolution, data.holesResolution];
            for (int y = 0; y < data.holesResolution; y++)
                for (int x = 0; x < data.holesResolution; x++)
                    holes[y, x] = true;
            data.SetHoles(0, 0, holes);
            shader.SetInt("_UseTerrain", 1);
            shader.SetInt("_TerrainHasHoles", 1);
            shader.SetVector("_TerrainOrigin", new Vector4(0f, 11f, 0f, 0f));
            shader.SetTexture(generateKernel, "_TerrainHeightmap", data.heightmapTexture);
            shader.SetTexture(generateKernel, "_TerrainHoles", data.holesTexture);
            Generate(4, 4, 8, 8);
            Assert.That(ReadCounts(), Is.EqualTo(new uint[] { 64, 0, 0, 0 }));
            Vector4[] roots = ReadPositions();
            for (int index = offsets[0]; index < offsets[0] + 64; index++)
                Assert.That(roots[index].y, Is.EqualTo(18f).Within(0.005f),
                    "Native Terrain height encoding must decode on this graphics API, including the world Y offset.");

            data.SetHoles(0, 0, new bool[data.holesResolution, data.holesResolution]);
            shader.SetTexture(generateKernel, "_TerrainHoles", data.holesTexture);
            Generate(4, 4, 8, 8);
            Assert.That(ReadCounts(), Is.EqualTo(new uint[4]));
            AssertArguments(0, 0, 0);
        }
        finally
        {
            Object.DestroyImmediate(data);
        }
    }

    [Test]
    public void AdjacentFractionalOriginTerrainsGenerateASharedEdgeRootOnlyOnce()
    {
        shader.SetFloat("_Spacing", 0.1f);
        shader.SetVector("_CameraPosition", new Vector4(512.1f, 0f, -214f, 0f));
        Generate(5121, -2141, 1, 1);
        Assert.That(ReadCounts(), Is.EqualTo(new uint[] { 1, 0, 0, 0 }));
        Vector4 baseline = ReadPositions()[offsets[0]];

        // This seeded root exposed duplicate ownership at X = 0.1f + 512f.
        // Derive the shared edge from the actual GPU result so multiplication
        // contraction cannot move the candidate out of the regression case.
        float leftOrigin = (baseline.x - 512f) + 0.00002f;
        float sharedEdge = leftOrigin + 512f;
        Assert.That(sharedEdge, Is.EqualTo(baseline.x));
        Assert.That((baseline.x - leftOrigin) / 512f, Is.LessThan(1f),
            "The old normalized-UV test must also claim this edge for the left Terrain.");

        shader.SetInt("_UseTerrain", 1);
        shader.SetVector("_TerrainSize", new Vector4(512f, 20f, 512f, 0f));
        // Zero decodes to native height zero on every supported channel packing.
        shader.SetTexture(generateKernel, "_TerrainHeightmap", exclusion);
        shader.SetVector("_TerrainOrigin", new Vector4(leftOrigin, 11f, -256f, 0f));
        Generate(5121, -2141, 1, 1);
        Assert.That(ReadCounts(), Is.EqualTo(new uint[4]), "The left Terrain excludes its upper edge.");

        shader.SetVector("_TerrainOrigin", new Vector4(sharedEdge, 11f, -256f, 0f));
        // Keep the same counts and grid to exercise consecutive Terrain dispatches.
        shader.Dispatch(generateKernel, 1, 1, 1);
        shader.Dispatch(finalizeKernel, 1, 1, 1);
        Assert.That(ReadCounts(), Is.EqualTo(new uint[] { 1, 0, 0, 0 }));
        AssertArguments(1, 0, 0);
        Vector4[] roots = ReadPositions();
        Assert.That(roots[offsets[0]].x, Is.EqualTo(baseline.x));
        Assert.That(roots[offsets[0]].z, Is.EqualTo(baseline.z));
        Assert.That(roots[offsets[0]].y, Is.EqualTo(11f).Within(0.0001f));
        AssertGuards(roots, 0, 1);
    }

    [Test]
    public void TerrainInteriorRootSurvivesAUVThatRoundsToTheUpperEdge()
    {
        shader.SetFloat("_Spacing", 0.1f);
        shader.SetVector("_CameraPosition", new Vector4(0f, 0f, -370.5f, 0f));
        Generate(0, -3705, 1, 1);
        Assert.That(ReadCounts(), Is.EqualTo(new uint[] { 1, 0, 0, 0 }));
        Vector4 baseline = ReadPositions()[offsets[0]];
        Assert.That(baseline.x, Is.LessThan(0f).And.GreaterThan(-0.00001f));
        Assert.That((baseline.x + 512f) / 512f, Is.EqualTo(1f),
            "The old normalized-UV test must reject a representable point inside this Terrain.");

        shader.SetInt("_UseTerrain", 1);
        shader.SetVector("_TerrainOrigin", new Vector4(-512f, 11f, -512f, 0f));
        shader.SetVector("_TerrainSize", new Vector4(512f, 20f, 512f, 0f));
        shader.SetTexture(generateKernel, "_TerrainHeightmap", exclusion);
        Generate(0, -3705, 1, 1);
        Assert.That(ReadCounts(), Is.EqualTo(new uint[] { 1, 0, 0, 0 }));
        AssertArguments(1, 0, 0);
        Vector4[] roots = ReadPositions();
        Assert.That(roots[offsets[0]].x, Is.EqualTo(baseline.x));
        Assert.That(roots[offsets[0]].z, Is.EqualTo(baseline.z));
        Assert.That(roots[offsets[0]].y, Is.EqualTo(11f).Within(0.0001f));
        AssertGuards(roots, 0, 1);
    }

    [TestCase(1f)]
    [TestCase(0.25f)]
    public void FilteringAValidMeshEdgeCannotPullRootsTowardClearedWorldHeight(float valid)
    {
        var pixels = new Color[16];
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                pixels[y * 4 + x] = x < 2 ? new Color(10f, valid, 0f, 1f) : Color.clear;
        height.SetPixels(pixels);
        height.Apply(false, false);
        // These roots straddle the two center texels. Their bilinear validity
        // remains fractional while every contributing valid surface is at Y=10.
        Generate(-7, -5, 13, 9);
        uint[] generated = ReadCounts();
        Assert.That(generated[0], Is.InRange(1u, 116u));
        Assert.That(generated[1], Is.Zero);
        Assert.That(generated[2], Is.Zero);
        Assert.That(generated[3], Is.Zero);
        Vector4[] roots = ReadPositions();
        for (int index = offsets[0]; index < offsets[0] + generated[0]; index++)
            Assert.That(roots[index].y, Is.EqualTo(10f).Within(0.0001f),
                "Invalid clear texels must not lower height; partial vertex validity must not scale world height either.");
        AssertArguments(generated[0], 0, 0);
        AssertGuards(roots, 0, (int)generated[0]);
    }

    [TestCase(1f)]
    [TestCase(3f)]
    [TestCase(5f)]
    public void RealPerspectiveFrustumRejectsRootsOutsideTheCameraVolume(float cameraHeight)
    {
        Generate(-7, -5, 13, 9);
        Dictionary<Vector2Int, GeneratedRoot> baseline = ReadGeneratedRoots();
        Assert.That(baseline.Count, Is.EqualTo(117));
        var cameraObject = new GameObject("Grass GPU perspective frustum", typeof(Camera));
        try
        {
            Camera camera = cameraObject.GetComponent<Camera>();
            camera.enabled = false;
            camera.transform.SetPositionAndRotation(new Vector3(0f, cameraHeight, -4f), Quaternion.Euler(0f, 17f, 0f));
            camera.fieldOfView = 65f;
            camera.aspect = 1.3f;
            camera.nearClipPlane = 1.5f;
            camera.farClipPlane = 7.5f;
            Plane[] planes = GeometryUtility.CalculateFrustumPlanes(camera);
            var expected = new HashSet<Vector2Int>();
            foreach (KeyValuePair<Vector2Int, GeneratedRoot> item in baseline)
                if (GeometryUtility.TestPlanesAABB(planes, new Bounds(item.Value.Position, Vector3.zero)))
                    expected.Add(item.Key);
            Assert.That(expected.Count, Is.InRange(1, baseline.Count - 1),
                "The camera must have both visible and rejected candidates for this regression.");

            SetFrustum(planes);
            shader.SetVector("_CameraPosition", camera.transform.position);
            shader.SetFloat("_BladeBoundsRadius", 0f);
            Generate(-7, -5, 13, 9);
            Dictionary<Vector2Int, GeneratedRoot> visible = ReadGeneratedRoots();
            Assert.That(visible.Keys, Is.EquivalentTo(expected),
                "The GPU point test must agree with Unity's real camera frustum, including its near and far planes.");
            AssertSameRoots(baseline, visible);
        }
        finally
        {
            Object.DestroyImmediate(cameraObject);
        }
    }

    [Test]
    public void ConservativeBladeRadiusRetainsRootsBeyondOrthographicEdges()
    {
        Generate(-7, -5, 13, 9);
        Dictionary<Vector2Int, GeneratedRoot> baseline = ReadGeneratedRoots();
        var cameraObject = new GameObject("Grass GPU blade bounds", typeof(Camera));
        try
        {
            Camera camera = cameraObject.GetComponent<Camera>();
            camera.enabled = false;
            camera.transform.SetPositionAndRotation(new Vector3(0f, 3f, -2f), Quaternion.identity);
            camera.orthographic = true;
            camera.orthographicSize = 2.25f;
            camera.aspect = 1.2f;
            camera.nearClipPlane = 0.5f;
            camera.farClipPlane = 5.5f;
            Plane[] planes = GeometryUtility.CalculateFrustumPlanes(camera);
            SetFrustum(planes);
            shader.SetVector("_CameraPosition", camera.transform.position);
            var pointExpected = new HashSet<Vector2Int>();
            var expandedExpected = new HashSet<Vector2Int>();
            const float radius = 1.25f;
            foreach (KeyValuePair<Vector2Int, GeneratedRoot> item in baseline)
            {
                if (GeometryUtility.TestPlanesAABB(planes, new Bounds(item.Value.Position, Vector3.zero)))
                    pointExpected.Add(item.Key);
                // For an axis-aligned orthographic frustum, a sphere and this
                // AABB have identical support on every plane. Unity supplies
                // the independent intersection implementation used as oracle.
                if (GeometryUtility.TestPlanesAABB(planes, new Bounds(item.Value.Position, Vector3.one * (2f * radius))))
                    expandedExpected.Add(item.Key);
            }
            Assert.That(pointExpected.Count, Is.GreaterThan(0));
            Assert.That(expandedExpected.Count, Is.InRange(pointExpected.Count + 1, baseline.Count - 1));

            shader.SetFloat("_BladeBoundsRadius", 0f);
            Generate(-7, -5, 13, 9);
            Assert.That(ReadGeneratedRoots().Keys, Is.EquivalentTo(pointExpected));
            shader.SetFloat("_BladeBoundsRadius", radius);
            Generate(-7, -5, 13, 9);
            Dictionary<Vector2Int, GeneratedRoot> expanded = ReadGeneratedRoots();
            Assert.That(expanded.Keys, Is.EquivalentTo(expandedExpected),
                "Culling must preserve blades whose roots are offscreen while their conservative bounds remain visible.");
            AssertSameRoots(baseline, expanded);
        }
        finally
        {
            Object.DestroyImmediate(cameraObject);
        }
    }

    [Test]
    public void MixedLodTransitionsAssignOneStableMeshPerRootDuringDensityThinning()
    {
        shader.SetVector("_CameraPosition", new Vector4(0f, 3f, 0f, 0f));
        shader.SetVector("_LodDistances", new Vector4(4f, 6.5f, 0f, 0f));
        shader.SetFloat("_LodTransitionWidth", 2f);
        Generate(-7, -7, 15, 15);
        Dictionary<Vector2Int, GeneratedRoot> baseline = ReadGeneratedRoots();
        Assert.That(baseline.Count, Is.EqualTo(225), "Mesh transitions must neither drop nor duplicate full-density roots.");
        var population = new int[3];
        var nearTransitionPopulation = new int[2];
        var farTransitionPopulation = new int[2];
        foreach (GeneratedRoot root in baseline.Values)
        {
            population[root.Lod]++;
            float distance = Vector3.Distance(new Vector3(0f, 3f, 0f), root.Position);
            if (distance < 3f)
                Assert.That(root.Lod, Is.EqualTo(0));
            else if (distance < 5f)
            {
                Assert.That(root.Lod, Is.InRange(0, 1));
                nearTransitionPopulation[root.Lod]++;
            }
            else if (distance < 5.5f)
                Assert.That(root.Lod, Is.EqualTo(1));
            else if (distance < 7.5f)
            {
                Assert.That(root.Lod, Is.InRange(1, 2));
                farTransitionPopulation[root.Lod - 1]++;
            }
            else
                Assert.That(root.Lod, Is.EqualTo(2));
        }
        Assert.That(population, Has.All.GreaterThan(0));
        Assert.That(nearTransitionPopulation, Has.All.GreaterThan(0),
            "The near transition must exercise both adjacent meshes in this fixed world grid.");
        Assert.That(farTransitionPopulation, Has.All.GreaterThan(0),
            "The far transition must exercise both adjacent meshes in this fixed world grid.");

        Generate(-7, -7, 15, 15);
        Dictionary<Vector2Int, GeneratedRoot> repeated = ReadGeneratedRoots();
        Assert.That(repeated.Keys, Is.EquivalentTo(baseline.Keys));
        AssertSameRoots(baseline, repeated);

        FillTexture(density, new Color(0.5f, 0f, 0f, 1f));
        Generate(-7, -7, 15, 15);
        Dictionary<Vector2Int, GeneratedRoot> thinned = ReadGeneratedRoots();
        Assert.That(thinned.Count, Is.InRange(1, baseline.Count - 1));
        AssertSameRoots(baseline, thinned);
    }

    [Test]
    public void DistanceThinningPreservesTheFullDensityPlateauAndStablePartialCoverage()
    {
        shader.SetVector("_CameraPosition", new Vector4(0f, 3f, 0f, 0f));
        shader.SetVector("_LodDistances", new Vector4(4f, 6.5f, 0f, 0f));
        shader.SetFloat("_LodTransitionWidth", 2f);
        shader.SetFloat("_DrawDistance", 10f);
        shader.SetFloat("_FullDensityDistance", 10f);
        shader.SetFloat("_DensityFalloffExponent", 2f);
        shader.SetFloat("_DensityTransition", 0.1f);
        Generate(-6, -6, 13, 13);
        Dictionary<Vector2Int, GeneratedRoot> baseline = ReadGeneratedRoots();
        Assert.That(baseline.Count, Is.EqualTo(169));
        foreach (GeneratedRoot root in baseline.Values)
            Assert.That(root.Position.w, Is.EqualTo(1f));

        shader.SetFloat("_FullDensityDistance", 4f);
        Generate(-6, -6, 13, 13);
        Dictionary<Vector2Int, GeneratedRoot> faded = ReadGeneratedRoots();
        int plateauPopulation = 0;
        int partialCoveragePopulation = 0;
        foreach (KeyValuePair<Vector2Int, GeneratedRoot> item in baseline)
        {
            if (Vector3.Distance(new Vector3(0f, 3f, 0f), item.Value.Position) > 4f)
                continue;
            plateauPopulation++;
            Assert.That(faded.TryGetValue(item.Key, out GeneratedRoot root), Is.True,
                "Distance thinning must begin at Full Density Distance, never inside its plateau.");
            Assert.That(root.Position.w, Is.EqualTo(1f));
        }
        Assert.That(plateauPopulation, Is.GreaterThan(0));
        foreach (GeneratedRoot root in faded.Values)
            if (root.Position.w < 1f)
                partialCoveragePopulation++;
        Assert.That(faded.Count, Is.InRange(plateauPopulation + 1, baseline.Count - 1),
            "The outer range must retain some grass while thinning the original population.");
        Assert.That(partialCoveragePopulation, Is.GreaterThan(0),
            "The fractional transition band must exercise partial blade coverage, not only binary rejection.");
        AssertSameRoots(baseline, faded, compareCoverage: false);

        Generate(-6, -6, 13, 13);
        Dictionary<Vector2Int, GeneratedRoot> repeated = ReadGeneratedRoots();
        Assert.That(repeated.Keys, Is.EquivalentTo(faded.Keys));
        AssertSameRoots(faded, repeated);
    }

    private void Generate(int startX, int startZ, int width, int heightCount)
    {
        shader.SetInts("_GridStartIndex", startX, startZ);
        shader.SetInts("_GridSize", width, heightCount);
        shader.Dispatch(resetKernel, 1, 1, 1);
        shader.Dispatch(generateKernel, (width + 7) / 8, (heightCount + 7) / 8, 1);
        shader.Dispatch(finalizeKernel, 1, 1, 1);
    }

    private uint[] ReadCounts()
    {
        var values = new uint[4];
        counts.GetData(values); // Synchronous readback is intentional in small correctness tests only.
        return values;
    }

    private Vector4[] ReadPositions()
    {
        var values = new Vector4[positions.count];
        positions.GetData(values);
        return values;
    }

    private Dictionary<Vector2Int, GeneratedRoot> ReadGeneratedRoots()
    {
        uint[] generated = ReadCounts();
        Assert.That(generated[3], Is.Zero, "These bounded visibility tests must not overflow a LOD partition.");
        AssertArguments(generated[0], generated[1], generated[2]);
        Vector4[] roots = ReadPositions();
        var result = new Dictionary<Vector2Int, GeneratedRoot>();
        for (int lod = 0; lod < 3; lod++)
        {
            Assert.That(generated[lod], Is.LessThanOrEqualTo((uint)capacities[lod]));
            for (int index = 0; index < generated[lod]; index++)
            {
                Vector4 root = roots[offsets[lod] + index];
                Assert.That(root.w, Is.GreaterThan(0f).And.LessThanOrEqualTo(1f));
                var cell = new Vector2Int(Mathf.FloorToInt(root.x + 0.5f), Mathf.FloorToInt(root.z + 0.5f));
                Assert.That(result.ContainsKey(cell), Is.False, "A root may appear only once across all LOD partitions.");
                result.Add(cell, new GeneratedRoot(root, lod));
            }
        }
        for (int index = 0; index < roots.Length; index++)
        {
            bool insidePartition = false;
            for (int lod = 0; lod < 3; lod++)
                insidePartition |= index >= offsets[lod] && index < offsets[lod] + capacities[lod];
            if (!insidePartition)
                Assert.That(roots[index], Is.EqualTo(Sentinel), "A generated root crossed a reserved guard slot.");
        }
        return result;
    }

    private static void AssertSameRoots(Dictionary<Vector2Int, GeneratedRoot> reference,
        Dictionary<Vector2Int, GeneratedRoot> actual, bool compareCoverage = true)
    {
        foreach (KeyValuePair<Vector2Int, GeneratedRoot> item in actual)
        {
            Assert.That(reference.TryGetValue(item.Key, out GeneratedRoot previous), Is.True);
            Assert.That(item.Value.Lod, Is.EqualTo(previous.Lod), "Root identity must not depend on the GPU append order or density acceptance.");
            Assert.That((Vector3)item.Value.Position, Is.EqualTo((Vector3)previous.Position));
            if (compareCoverage)
                Assert.That(item.Value.Position.w, Is.EqualTo(previous.Position.w));
        }
    }

    private void SetFrustum(Plane[] planes)
    {
        var packed = new Vector4[planes.Length];
        for (int index = 0; index < planes.Length; index++)
            packed[index] = new Vector4(planes[index].normal.x, planes[index].normal.y,
                planes[index].normal.z, planes[index].distance);
        shader.SetVectorArray("_FrustumPlanes", packed);
    }

    private readonly struct GeneratedRoot
    {
        public readonly Vector4 Position;
        public readonly int Lod;

        public GeneratedRoot(Vector4 position, int lod)
        {
            Position = position;
            Lod = lod;
        }
    }

    private void AssertArguments(uint near, uint middle, uint far)
    {
        var actual = new GraphicsBuffer.IndirectDrawIndexedArgs[3];
        arguments.GetData(actual);
        uint[] expectedCounts = { near, middle, far };
        for (int lod = 0; lod < 3; lod++)
        {
            Assert.That(actual[lod].instanceCount, Is.EqualTo(expectedCounts[lod]));
            Assert.That(actual[lod].indexCountPerInstance, Is.EqualTo(initialArguments[lod].indexCountPerInstance));
            Assert.That(actual[lod].startIndex, Is.EqualTo(initialArguments[lod].startIndex));
            Assert.That(actual[lod].baseVertexIndex, Is.EqualTo(initialArguments[lod].baseVertexIndex));
            Assert.That(actual[lod].startInstance, Is.EqualTo(initialArguments[lod].startInstance));
        }
    }

    private void AssertGuards(Vector4[] roots, int populatedLod, int populatedCount)
    {
        int start = offsets[populatedLod];
        for (int index = 0; index < roots.Length; index++)
        {
            if (index >= start && index < start + populatedCount)
                Assert.That(roots[index].w, Is.InRange(0f, 1f));
            else
                Assert.That(roots[index], Is.EqualTo(Sentinel), "A write crossed a partition boundary or came from an invalid candidate.");
        }
    }

    private static Texture2D CreateTexture(Color value)
    {
        var texture = new Texture2D(4, 4, TextureFormat.RGBAFloat, false, true)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        FillTexture(texture, value);
        return texture;
    }

    private static void FillTexture(Texture2D texture, Color value)
    {
        var pixels = new Color[texture.width * texture.height];
        for (int index = 0; index < pixels.Length; index++)
            pixels[index] = value;
        texture.SetPixels(pixels);
        texture.Apply(false, false);
    }

    private static int FindInstanceCountOffset()
    {
        const uint marker = 0x13579bdf;
        int size = Marshal.SizeOf<GraphicsBuffer.IndirectDrawIndexedArgs>();
        Assert.That(size, Is.EqualTo(GraphicsBuffer.IndirectDrawIndexedArgs.size));
        IntPtr memory = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(new GraphicsBuffer.IndirectDrawIndexedArgs { instanceCount = marker }, memory, false);
            var bytes = new byte[size];
            Marshal.Copy(memory, bytes, 0, size);
            for (int offset = 0; offset <= size - sizeof(uint); offset += sizeof(uint))
                if (BitConverter.ToUInt32(bytes, offset) == marker)
                    return offset;
            Assert.Fail("The platform's typed indirect argument layout did not expose the instance-count field.");
            return 0;
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }
}
