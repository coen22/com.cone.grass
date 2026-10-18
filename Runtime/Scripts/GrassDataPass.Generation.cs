using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

public partial class GrassDataRendererFeature
{
    // Dispatch tiles, blade generation, readback and the capture math.
    private sealed partial class GrassDataPass
    {
        private void BuildDispatches(RenderGraph graph, CameraState state, InfiniteGrassRenderer owner,
            Bounds bounds, float spacing, bool authored, TextureHandle fallbackDensity)
        {
            state.DispatchCount = 0;
            state.CandidateCount = 0;
            try
            {
                BuildDispatchData(graph, state, owner, bounds, spacing, authored, fallbackDensity);
            }
            finally
            {
                // Execution uses the resolved values below. Pooled records must
                // not keep an evicted group's terrain and sparse tile storage alive.
                state.ReleaseDispatchGroups();
            }
        }

        private void BuildDispatchData(RenderGraph graph, CameraState state, InfiniteGrassRenderer owner,
            Bounds bounds, float spacing, bool authored, TextureHandle fallbackDensity)
        {
            float densityFootprint = (2f * state.CaptureExtent) / state.Density.rt.width;
            bool valid = TryGridBounds(bounds, spacing, out int minX, out int minZ, out int maxX, out int maxZ);
            if (valid && !authored)
                valid = AddDispatch(state, null, minX, minZ, maxX - minX, maxZ - minZ);

            if (valid && authored)
            {
                var cameraGrid = new GrassGridRange(minX, minZ, maxX - minX, maxZ - minZ);
                for (int g = 0; g < state.ActiveGroups.Count && valid; g++)
                {
                    TerrainGroup group = state.ActiveGroups[g];
                    group.Tiles.Clear();
                    long projectedCandidates = state.CandidateCount;
                    for (int a = 0; a < group.Sources.Count && valid; a++)
                    {
                        PlacementSource source = group.Sources[a];
                        // The cached density map is sampled bilinearly. Its
                        // positive footprint can reach one capture texel beyond
                        // the authored quad, including into an adjacent cell tile.
                        Bounds filteredBounds = GrassDispatchMath.ExpandXZ(source.Data.WorldBounds, densityFootprint);
                        if (!IntersectXZ(filteredBounds, bounds, out Bounds intersection))
                            continue;
                        if (!TryGridBounds(intersection, spacing, out int x0, out int z0, out int x1, out int z1))
                        {
                            valid = false;
                            break;
                        }
                        int tx0 = FloorDivide(x0, TileCells);
                        int tz0 = FloorDivide(z0, TileCells);
                        int tx1 = FloorDivide(x1 - 1, TileCells);
                        int tz1 = FloorDivide(z1 - 1, TileCells);
                        if ((long)(tx1 - tx0 + 1) * (tz1 - tz0 + 1) > MaximumTileSearch)
                        {
                            valid = false;
                            break;
                        }
                        for (int tz = tz0; tz <= tz1 && valid; tz++)
                        for (int tx = tx0; tx <= tx1; tx++)
                        {
                            Vector2Int tile = new Vector2Int(tx, tz);
                            if (group.Tiles.Contains(tile))
                                continue;
                            if (!GrassDispatchMath.TryClipTile(cameraGrid, tx, tz, TileCells, out GrassGridRange clipped))
                                continue;
                            if (!GrassDispatchMath.TryGetCandidateBounds(clipped, spacing, densityFootprint, out Bounds tileBounds))
                            {
                                valid = false;
                                break;
                            }
                            if (!source.Data.IntersectsCoverage(tileBounds))
                                continue;
                            if (!GrassDispatchMath.TryAddCandidateBudget(projectedCandidates, clipped.Width, clipped.Height,
                                    MaximumCandidates, out projectedCandidates))
                            {
                                valid = false;
                                break;
                            }
                            group.Tiles.Add(tile);
                        }
                    }
                    group.SortedTiles.Clear();
                    group.SortedTiles.AddRange(group.Tiles);
                    group.SortedTiles.Sort(TileComparer.Instance);
                    for (int i = 0; i < group.SortedTiles.Count && valid; i++)
                    {
                        Vector2Int first = group.SortedTiles[i];
                        int lastX = first.x;
                        while (i + 1 < group.SortedTiles.Count && group.SortedTiles[i + 1].y == first.y &&
                            group.SortedTiles[i + 1].x == lastX + 1)
                            lastX = group.SortedTiles[++i].x;
                        int x = Mathf.Max(minX, first.x * TileCells);
                        int z = Mathf.Max(minZ, first.y * TileCells);
                        int width = Mathf.Min(maxX, (lastX + 1) * TileCells) - x;
                        int height = Mathf.Min(maxZ, (first.y + 1) * TileCells) - z;
                        valid = AddDispatch(state, group, x, z, width, height);
                    }
                }
            }

            if (!valid)
            {
                // A complete, visible failure is safer than silently selecting a
                // moving subset or allowing uint counters/dispatch dimensions to wrap.
                state.RejectDispatches();
                if (!state.WarnedBudget)
                    Debug.LogWarning("Grass generation was skipped: the camera/source grid exceeds the safety budget of " +
                        MaximumCandidates + " candidate cells. Increase spacing, reduce draw distance, or use smaller authored areas.", owner);
                state.WarnedBudget = true;
            }
            else
                state.WarnedBudget = false;

            TextureHandle black = ImportTexture(graph, state, Texture2D.blackTexture);
            TextureHandle white = ImportTexture(graph, state, Texture2D.whiteTexture);
            for (int i = 0; i < state.DispatchCount; i++)
            {
                DispatchData dispatch = state.Dispatches[i];
                Terrain terrain = dispatch.Group?.Terrain;
                TerrainData terrainData = terrain ? terrain.terrainData : null;
                dispatch.UseTerrain = terrainData != null;
                dispatch.Origin = terrain ? terrain.transform.position : Vector3.zero;
                dispatch.Size = terrainData ? terrainData.size : Vector3.one;
                dispatch.TerrainHeight = terrainData ? ImportTexture(graph, state, terrainData.heightmapTexture) : black;
                dispatch.Density = dispatch.Group != null ? dispatch.Group.DensityTexture : fallbackDensity;
                Texture holes = terrainData ? terrainData.holesTexture : null;
                dispatch.HasHoles = holes != null;
                dispatch.TerrainHoles = holes ? ImportTexture(graph, state, holes) : white;
            }
        }

        private static bool AddDispatch(CameraState state, TerrainGroup group, int x, int z, int width, int height)
        {
            if (width <= 0 || height <= 0)
                return true;
            if (!GrassDispatchMath.TryAddCandidateBudget(state.CandidateCount, width, height,
                    MaximumCandidates, out long updatedBudget))
                return false;
            state.CandidateCount = updatedBudget;
            for (int dz = 0; dz < height; dz += MaximumDispatchCells)
            for (int dx = 0; dx < width; dx += MaximumDispatchCells)
            {
                if (state.DispatchCount == state.Dispatches.Count)
                    state.Dispatches.Add(new DispatchData());
                DispatchData dispatch = state.Dispatches[state.DispatchCount++];
                dispatch.Group = group;
                dispatch.Start[0] = x + dx;
                dispatch.Start[1] = z + dz;
                dispatch.SizeInCells[0] = Mathf.Min(MaximumDispatchCells, width - dx);
                dispatch.SizeInCells[1] = Mathf.Min(MaximumDispatchCells, height - dz);
            }
            return true;
        }

        private void BuildGeneration(RenderGraph graph, CameraState state, InfiniteGrassRenderer owner,
            TextureHandle height, TextureHandle density, TextureHandle mask,
            BufferHandle positions, BufferHandle counts, BufferHandle arguments,
            Vector2 center, Vector3 cameraPosition, float spacing, float distanceLimit, float capturePadding,
            float radius, bool authored)
        {
            using (IComputeRenderGraphBuilder builder = graph.AddComputePass<GenerationPass>(
                       "Grass Cull And Generate", out GenerationPass pass))
            {
                pass.Shader = generationShader;
                pass.ResetKernel = resetKernel;
                pass.GenerateKernel = generateKernel;
                pass.FinalizeKernel = finalizeKernel;
                pass.Positions = state.Positions;
                pass.Counts = state.Counts;
                pass.Arguments = state.Arguments;
                pass.Height = height;
                pass.Density = density;
                pass.Mask = mask;
                pass.Dispatches = state.Dispatches;
                pass.DispatchCount = state.DispatchCount;
                pass.LodOffsets = state.LodOffsets;
                pass.LodCapacities = state.LodCapacities;
                pass.Frustum = state.Frustum;
                pass.Center = center;
                pass.CameraPosition = cameraPosition;
                pass.Spacing = spacing;
                pass.DrawDistance = distanceLimit;
                pass.CapturePadding = capturePadding;
                pass.FullDensityDistance = Mathf.Clamp(FiniteOr(owner.fullDensityDistance, 30f), 0f, distanceLimit);
                pass.DensityExponent = Mathf.Max(0f, FiniteOr(owner.densityFalloffExponent, 4f));
                pass.DensityTransition = Mathf.Clamp01(FiniteOr(owner.densityTransition, 0.05f));
                float nearLod = Mathf.Max(0f, FiniteOr(owner.nearLodDistance, 30f));
                float farLod = Mathf.Max(nearLod, FiniteOr(owner.subdivisionDistance, 100f));
                pass.LodDistances = new Vector4(nearLod, farLod, 0f, 0f);
                pass.LodTransition = Mathf.Max(0f, FiniteOr(owner.lodTransitionWidth, 10f));
                pass.BoundsRadius = radius;
                pass.Authored = authored;
                pass.DensityShortens = state.DensityShortens;
                pass.ArgumentStride = argumentStride;
                pass.ArgumentCountOffset = argumentCountOffset;
                builder.UseTexture(height, AccessFlags.Read);
                builder.UseTexture(density, AccessFlags.Read);
                builder.UseTexture(mask, AccessFlags.Read);
                builder.UseBuffer(positions, AccessFlags.Write);
                builder.UseBuffer(counts, AccessFlags.ReadWrite);
                builder.UseBuffer(arguments, AccessFlags.ReadWrite);
                for (int i = 0; i < state.DispatchCount; i++)
                {
                    builder.UseTexture(state.Dispatches[i].TerrainHeight, AccessFlags.Read);
                    builder.UseTexture(state.Dispatches[i].TerrainHoles, AccessFlags.Read);
                    builder.UseTexture(state.Dispatches[i].Density, AccessFlags.Read);
                }
                builder.SetRenderFunc(static (GenerationPass data, ComputeGraphContext context) =>
                {
                    ComputeCommandBuffer cmd = context.cmd;
                    ComputeShader shader = data.Shader;
                    cmd.SetComputeBufferParam(shader, data.ResetKernel, Id.Counts, data.Counts);
                    cmd.DispatchCompute(shader, data.ResetKernel, 1, 1, 1);
                    cmd.SetComputeFloatParam(shader, Id.Spacing, data.Spacing);
                    cmd.SetComputeFloatParam(shader, Id.DrawDistance, data.DrawDistance);
                    cmd.SetComputeFloatParam(shader, Id.CapturePadding, data.CapturePadding);
                    cmd.SetComputeFloatParam(shader, Id.FullDensity, data.FullDensityDistance);
                    cmd.SetComputeFloatParam(shader, Id.DensityExponent, data.DensityExponent);
                    cmd.SetComputeFloatParam(shader, Id.DensityTransition, data.DensityTransition);
                    cmd.SetComputeFloatParam(shader, Id.BoundsRadius, data.BoundsRadius);
                    cmd.SetComputeFloatParam(shader, Id.LodTransition, data.LodTransition);
                    cmd.SetComputeVectorParam(shader, Id.LodDistances, data.LodDistances);
                    cmd.SetComputeVectorParam(shader, Id.Center, new Vector4(data.Center.x, data.Center.y, 0f, 0f));
                    cmd.SetComputeVectorParam(shader, Id.CameraPosition, data.CameraPosition);
                    cmd.SetComputeVectorArrayParam(shader, Id.Frustum, data.Frustum);
                    cmd.SetComputeIntParams(shader, Id.LodOffsets, data.LodOffsets);
                    cmd.SetComputeIntParams(shader, Id.LodCapacities, data.LodCapacities);
                    cmd.SetComputeIntParam(shader, Id.Authored, data.Authored ? 1 : 0);
                    cmd.SetComputeIntParam(shader, Id.DensityShortens, data.DensityShortens ? 1 : 0);
                    cmd.SetComputeIntParam(shader, Id.ArgsStride, data.ArgumentStride);
                    cmd.SetComputeIntParam(shader, Id.ArgsCountOffset, data.ArgumentCountOffset);
                    cmd.SetComputeBufferParam(shader, data.GenerateKernel, Id.Positions, data.Positions);
                    cmd.SetComputeBufferParam(shader, data.GenerateKernel, Id.Counts, data.Counts);
                    cmd.SetComputeTextureParam(shader, data.GenerateKernel, Id.Height, data.Height);
                    cmd.SetComputeTextureParam(shader, data.GenerateKernel, Id.Density, data.Density);
                    cmd.SetComputeTextureParam(shader, data.GenerateKernel, Id.Mask, data.Mask);
                    for (int i = 0; i < data.DispatchCount; i++)
                    {
                        DispatchData dispatch = data.Dispatches[i];
                        cmd.SetComputeIntParams(shader, Id.GridStart, dispatch.Start);
                        cmd.SetComputeIntParams(shader, Id.GridSize, dispatch.SizeInCells);
                        cmd.SetComputeIntParam(shader, Id.UseTerrain, dispatch.UseTerrain ? 1 : 0);
                        cmd.SetComputeIntParam(shader, Id.TerrainHasHoles, dispatch.HasHoles ? 1 : 0);
                        cmd.SetComputeVectorParam(shader, Id.TerrainOrigin, dispatch.Origin);
                        cmd.SetComputeVectorParam(shader, Id.TerrainSize, dispatch.Size);
                        cmd.SetComputeTextureParam(shader, data.GenerateKernel, Id.TerrainHeight, dispatch.TerrainHeight);
                        cmd.SetComputeTextureParam(shader, data.GenerateKernel, Id.TerrainHoles, dispatch.TerrainHoles);
                        cmd.SetComputeTextureParam(shader, data.GenerateKernel, Id.Density, dispatch.Density);
                        cmd.DispatchCompute(shader, data.GenerateKernel,
                            GrassDispatchMath.ThreadGroupCount(dispatch.SizeInCells[0]),
                            GrassDispatchMath.ThreadGroupCount(dispatch.SizeInCells[1]), 1);
                    }
                    cmd.SetComputeBufferParam(shader, data.FinalizeKernel, Id.Counts, data.Counts);
                    cmd.SetComputeBufferParam(shader, data.FinalizeKernel, Id.Arguments, data.Arguments);
                    cmd.DispatchCompute(shader, data.FinalizeKernel, 1, 1, 1);
                });
            }
        }

        private static void BuildReadback(RenderGraph graph, CameraState state, BufferHandle counts,
            InfiniteGrassRenderer owner)
        {
            if (!owner.previewVisibleGrassCount || !SystemInfo.supportsAsyncGPUReadback ||
                state.ReadbackPending || Time.realtimeSinceStartup < state.NextReadbackTime)
                return;
            state.ReadbackPending = true;
            state.NextReadbackTime = Time.realtimeSinceStartup + 0.25f;
            state.ReadbackOwner = owner;
            state.ReadbackCapacity0 = (uint)state.LodCapacities[0];
            state.ReadbackCapacity1 = (uint)state.LodCapacities[1];
            state.ReadbackCapacity2 = (uint)state.LodCapacities[2];
            using (IUnsafeRenderGraphBuilder builder = graph.AddUnsafePass<ReadbackPass>(
                       "Grass Diagnostic Readback", out ReadbackPass pass))
            {
                pass.State = state;
                pass.Counts = state.Counts;
                builder.UseBuffer(counts, AccessFlags.Read);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (ReadbackPass data, UnsafeGraphContext context) =>
                    context.cmd.RequestAsyncReadback(data.Counts, data.State.ReadbackCallback));
            }
        }

        private bool EnsurePlacementMaterial()
        {
            if (placementMaterial)
                return true;
            Shader shader = Resources.Load<Shader>("InfiniteGrassPlacement");
            if (!shader || !shader.isSupported)
            {
                if (!missingPlacementWarning)
                    Debug.LogWarning("Grass authored areas require the supported InfiniteGrassPlacement resource shader.");
                missingPlacementWarning = true;
                return false;
            }
            placementMaterial = CoreUtils.CreateEngineMaterial(shader);
            captureQuad = new Mesh { name = "Grass Capture Quad", hideFlags = HideFlags.HideAndDontSave };
            captureQuad.vertices = new[]
            {
                new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, -0.5f),
                new Vector3(-0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, 0.5f)
            };
            captureQuad.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
            captureQuad.triangles = new[] { 0, 2, 1, 1, 2, 3 };
            captureQuad.UploadMeshData(true);
            return true;
        }

        private static int FindInstanceCountOffset()
        {
            int size = Marshal.SizeOf<GraphicsBuffer.IndirectDrawIndexedArgs>();
            if (size != GraphicsBuffer.IndirectDrawIndexedArgs.size || size % sizeof(uint) != 0)
                throw new NotSupportedException("The managed and native indirect argument sizes differ.");
            var probe = new GraphicsBuffer.IndirectDrawIndexedArgs { instanceCount = 123456789u };
            IntPtr memory = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(probe, memory, false);
                for (int offset = 0; offset < size; offset += sizeof(uint))
                    if (Marshal.ReadInt32(memory, offset) == 123456789)
                        return offset;
                throw new NotSupportedException("The indirect instance-count field could not be located.");
            }
            finally
            {
                Marshal.FreeHGlobal(memory);
            }
        }

        private static Vector2 ResolveCaptureHeightRange(Vector2 range)
        {
            // Use the rendered range as the cache identity. Raw NaN/Infinity
            // endpoints never compare equal, despite resolving to finite bounds.
            float first = FiniteOr(range.x, -100f);
            float second = FiniteOr(range.y, 1000f);
            return new Vector2(Mathf.Min(first, second), Mathf.Max(first, second));
        }

        private static Matrix4x4 MakeCaptureMatrix(Vector2 center, float extent, float minY, float maxY)
        {
            Matrix4x4 view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) *
                Matrix4x4.TRS(new Vector3(center.x, maxY + 1f, center.y),
                    Quaternion.LookRotation(Vector3.down, Vector3.forward), Vector3.one).inverse;
            Matrix4x4 projection = Matrix4x4.Ortho(-extent, extent, -extent, extent, 0.01f, maxY - minY + 2f);
            return GL.GetGPUProjectionMatrix(projection, true) * view;
        }

        private static float CalculateBladeRadius(Material material, InfiniteGrassRenderer owner, Camera camera, float scaledHeight)
        {
            float height = Mathf.Max(0f, material.GetFloat("_GrassHeight")) * (1f + Mathf.Max(0f, owner.subdivisionHeightBoost));
            float width = Mathf.Max(0f, material.GetFloat("_GrassWidth")) + Mathf.Max(0f, material.GetFloat("_ExpandDistantGrassWidth"));
            float pixelWidth = owner.minimumPixelWidth * 2f *
                (camera.orthographic ? 1f : Mathf.Max(0.01f, FiniteOr(owner.drawDistance, 300f))) /
                (Mathf.Max(1f, scaledHeight) * Mathf.Max(0.001f, Mathf.Abs(camera.projectionMatrix.m11)));
            return height + Mathf.Max(width * 0.25f, pixelWidth * 0.5f) +
                Mathf.Abs(material.GetFloat("_GrassCurving")) * 1.415f + Mathf.Max(0f, owner.cullingPadding);
        }

        private static Bounds CalculateCameraBounds(Camera camera, float drawDistance, float padding)
        {
            float far = Mathf.Max(camera.nearClipPlane, Mathf.Min(drawDistance, camera.farClipPlane));
            Bounds bounds = new Bounds(camera.transform.position, Vector3.zero);
            for (int plane = 0; plane < 2; plane++)
            for (int y = 0; y < 2; y++)
            for (int x = 0; x < 2; x++)
                bounds.Encapsulate(camera.ViewportToWorldPoint(new Vector3(x, y, plane == 0 ? camera.nearClipPlane : far)));
            bounds.Expand(padding * 2f);
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            Vector3 origin = camera.transform.position;
            min.x = Mathf.Max(min.x, origin.x - drawDistance);
            min.z = Mathf.Max(min.z, origin.z - drawDistance);
            max.x = Mathf.Min(max.x, origin.x + drawDistance);
            max.z = Mathf.Min(max.z, origin.z + drawDistance);
            bounds.SetMinMax(min, max);
            return bounds;
        }

        private static bool TryGridBounds(Bounds bounds, float spacing,
            out int minX, out int minZ, out int maxX, out int maxZ)
        {
            minX = minZ = maxX = maxZ = 0;
            if (!GrassDispatchMath.TryGetGridRange(bounds, spacing, out GrassGridRange range))
                return false;
            minX = range.MinX;
            minZ = range.MinZ;
            maxX = range.MaxX;
            maxZ = range.MaxZ;
            return true;
        }

        private static int FloorDivide(int value, int divisor) => GrassDispatchMath.FloorDivide(value, divisor);

        private static float FiniteOr(float value, float fallback) =>
            float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;

        private static bool IsFinite(Vector3 value) =>
            !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
            !float.IsNaN(value.z) && !float.IsInfinity(value.z);

        private static bool IntersectsXZ(Bounds a, Bounds b) =>
            a.min.x <= b.max.x && a.max.x >= b.min.x && a.min.z <= b.max.z && a.max.z >= b.min.z;

        private static bool IntersectXZ(Bounds a, Bounds b, out Bounds intersection) =>
            GrassDispatchMath.TryIntersectXZ(a, b, out intersection);

        private static TextureHandle ImportTexture(RenderGraph graph, CameraState state, Texture texture)
        {
            if (state.ImportedTextures.TryGetValue(texture, out TextureHandle imported))
                return imported;
            if (!state.TextureWrappers.TryGetValue(texture, out TextureWrapper wrapper))
            {
                wrapper = new TextureWrapper
                {
                    Handle = texture is RenderTexture renderTexture ? RTHandles.Alloc(renderTexture) : RTHandles.Alloc(texture)
                };
                state.TextureWrappers.Add(texture, wrapper);
            }
            wrapper.LastUsedTime = state.LastUsedTime;
            imported = graph.ImportTexture(wrapper.Handle);
            state.ImportedTextures.Add(texture, imported);
            return imported;
        }
    }
}
