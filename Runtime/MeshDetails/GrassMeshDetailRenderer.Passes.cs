using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Cone.Grass
{
    internal sealed partial class GrassMeshDetailRenderer
    {
        private static class Id
        {
            public static readonly int CameraPosition = UnityEngine.Shader.PropertyToID("_CameraPosition");
            public static readonly int FrustumPlanes = UnityEngine.Shader.PropertyToID("_FrustumPlanes");
            public static readonly int HiZViewProjection = UnityEngine.Shader.PropertyToID("_HiZViewProjection");
            public static readonly int HiZSize = UnityEngine.Shader.PropertyToID("_HiZSize");
            public static readonly int ShadowLight = UnityEngine.Shader.PropertyToID("_ShadowLight");
            public static readonly int ShadowSplits = UnityEngine.Shader.PropertyToID("_ShadowSplits");
            public static readonly int WindDirection = UnityEngine.Shader.PropertyToID("_WindDirection");
            public static readonly int WindParams = UnityEngine.Shader.PropertyToID("_WindParams");
            public static readonly int WindOffset = UnityEngine.Shader.PropertyToID("_WindOffset");
            public static readonly int Water = UnityEngine.Shader.PropertyToID("_Water");
            public static readonly int StatsRect = UnityEngine.Shader.PropertyToID("_StatsRect");
            public static readonly int TerrainOrigin = UnityEngine.Shader.PropertyToID("_TerrainOrigin");
            public static readonly int TerrainSize = UnityEngine.Shader.PropertyToID("_TerrainSize");
            public static readonly int CellStart = UnityEngine.Shader.PropertyToID("_CellStart");
            public static readonly int TerrainInfo = UnityEngine.Shader.PropertyToID("_TerrainInfo");
            public static readonly int SliceInfo = UnityEngine.Shader.PropertyToID("_SliceInfo");
            public static readonly int SliceKinds = UnityEngine.Shader.PropertyToID("_SliceKinds");
            public static readonly int SliceScale = UnityEngine.Shader.PropertyToID("_SliceScale");
            public static readonly int Capacity = UnityEngine.Shader.PropertyToID("_Capacity");
            public static readonly int LayoutOffsets = UnityEngine.Shader.PropertyToID("_Layout");
            public static readonly int Kinds = UnityEngine.Shader.PropertyToID("_Kinds");
            public static readonly int ListOffset = UnityEngine.Shader.PropertyToID("_ListOffset");
            public static readonly int ListCapacity = UnityEngine.Shader.PropertyToID("_ListCapacity");
            public static readonly int DrawList = UnityEngine.Shader.PropertyToID("_DrawList");
            public static readonly int ListCount = UnityEngine.Shader.PropertyToID("_ListCount");
            public static readonly int Counters = UnityEngine.Shader.PropertyToID("_Counters");
            public static readonly int Stats = UnityEngine.Shader.PropertyToID("_Stats");
            public static readonly int Visible = UnityEngine.Shader.PropertyToID("_Visible");
            public static readonly int InstanceData = UnityEngine.Shader.PropertyToID("_InstanceData");
            public static readonly int Args = UnityEngine.Shader.PropertyToID("_Args");
            public static readonly int TerrainHeightmap = UnityEngine.Shader.PropertyToID("_TerrainHeightmap");
            public static readonly int TerrainHoles = UnityEngine.Shader.PropertyToID("_TerrainHoles");
            public static readonly int Density = UnityEngine.Shader.PropertyToID("_Density");
            public static readonly int HiZ = UnityEngine.Shader.PropertyToID("_HiZ");
            public static readonly int SourceDepth = UnityEngine.Shader.PropertyToID("_SourceDepth");
            public static readonly int HiZOut = UnityEngine.Shader.PropertyToID("_HiZOut");
            public static readonly int HiZIn = UnityEngine.Shader.PropertyToID("_HiZIn");
            public static readonly int HiZSource = UnityEngine.Shader.PropertyToID("_HiZSource");
        }

        private static readonly ProfilingSampler s_cullSampler = new ProfilingSampler("Grass Mesh Details Cull");
        private static readonly ProfilingSampler s_hiZSampler = new ProfilingSampler("Grass Mesh Details Hi-Z");
        private static readonly Vector4[] s_planes = new Vector4[6];
        private static readonly Plane[] s_unityPlanes = new Plane[6];
        private static readonly int[] s_sliceKinds = new int[GrassMeshDetailDensity.MaxSlicesPerDispatch];
        private static readonly Vector4[] s_sliceScale = new Vector4[GrassMeshDetailDensity.MaxSlicesPerDispatch / 4];
        private static readonly int[] s_int4 = new int[4];
        private static Texture2D s_blackFloat;

        /// <summary>Tests: count instances whose root lies in this world rectangle (x, z min; x, z max).</summary>
        internal static Vector4 StatsRect { get; set; }

        /// <summary>Tests: a time to use for the wind instead of <see cref="Time.timeAsDouble"/>.</summary>
        internal static double? TimeOverride { get; set; }

        private sealed class CullData
        {
            public GrassMeshDetailRenderer Renderer;
            public CameraState State;
            public Vector3 CameraPosition;
            public bool Occlusion;
        }

        private sealed class HiZData
        {
            public GrassMeshDetailRenderer Renderer;
            public CameraState State;
            public TextureHandle Depth;
            public int Width, Height;
            public Matrix4x4 ViewProjection;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        /// <summary>The scatter and cull pass, before the shadow maps: it fills the lists every later pass draws.</summary>
        internal sealed class CullPass : ScriptableRenderPass
        {
            private readonly GrassMeshDetailRenderer _renderer;

            public CullPass(GrassMeshDetailRenderer renderer)
            {
                _renderer = renderer;
                renderPassEvent = RenderPassEvent.BeforeRenderingShadows;
                profilingSampler = s_cullSampler;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                if (!_renderer.TryGetPrepared(cameraData.camera, out CameraState state))
                {
                    return;
                }

                using (IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass("Grass Mesh Details Cull", out CullData data, s_cullSampler))
                {
                    data.Renderer = _renderer;
                    data.State = state;
                    data.CameraPosition = cameraData.camera.transform.position;
                    data.Occlusion = OcclusionWanted(_renderer._layout);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static (CullData d, UnsafeGraphContext context) =>
                        d.Renderer.ExecuteCull(CommandBufferHelpers.GetNativeCommandBuffer(context.cmd), d));
                }
            }
        }

        /// <summary>Builds this frame's Hi-Z from the camera depth after the opaques, for next frame's occlusion test.</summary>
        internal sealed class HiZPass : ScriptableRenderPass
        {
            private readonly GrassMeshDetailRenderer _renderer;

            public HiZPass(GrassMeshDetailRenderer renderer)
            {
                _renderer = renderer;
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
                profilingSampler = s_hiZSampler;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                if (!_renderer.TryGetPrepared(cameraData.camera, out CameraState state) || !resources.cameraDepthTexture.IsValid())
                {
                    return;
                }

                Camera camera = cameraData.camera;
                using (IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass("Grass Mesh Details Hi-Z", out HiZData data, s_hiZSampler))
                {
                    data.Renderer = _renderer;
                    data.State = state;
                    data.Depth = resources.cameraDepthTexture;
                    data.Width = cameraData.cameraTargetDescriptor.width;
                    data.Height = cameraData.cameraTargetDescriptor.height;
                    data.ViewProjection = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(), true) * cameraData.GetViewMatrix();
                    data.Position = camera.transform.position;
                    data.Rotation = camera.transform.rotation;
                    builder.UseTexture(data.Depth, AccessFlags.Read);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static (HiZData d, UnsafeGraphContext context) =>
                        d.Renderer.ExecuteHiZ(CommandBufferHelpers.GetNativeCommandBuffer(context.cmd), d));
                }
            }
        }

        internal static bool OcclusionWanted(Layout layout)
        {
            foreach (GrassMeshDetails owner in layout.Owners)
            {
                if (owner && owner.OcclusionCulling)
                {
                    return true;
                }
            }

            return false;
        }

        private static Texture2D BlackFloat
        {
            get
            {
                if (!s_blackFloat)
                {
                    s_blackFloat = new Texture2D(1, 1, TextureFormat.RFloat, false, true) { hideFlags = HideFlags.HideAndDontSave, name = "Grass Mesh Black" };
                    s_blackFloat.SetPixel(0, 0, Color.clear);
                    s_blackFloat.Apply(false, true);
                }

                return s_blackFloat;
            }
        }

        private void SetIntVector(CommandBuffer cmd, int id, int x, int y, int z, int w)
        {
            s_int4[0] = x;
            s_int4[1] = y;
            s_int4[2] = z;
            s_int4[3] = w;
            cmd.SetComputeIntParams(_shader, id, s_int4);
        }

        private void ExecuteCull(CommandBuffer cmd, CullData data)
        {
            CameraState state = data.State;
            Layout layout = _layout;
            Camera camera = state.Camera;
            if (!state.Valid || !camera)
            {
                return;
            }

            ComputeShader shader = _shader;
            int lists = Mathf.Max(1, layout.ListCount);
            SetIntVector(cmd, Id.Capacity, state.InstanceCapacity, layout.ListCount, layout.Draws.Count, SystemInfo.usesReversedZBuffer ? 1 : 0);
            SetIntVector(cmd, Id.LayoutOffsets, state.ObjectToWorldOffset, state.WorldToObjectOffset, state.ColorOffset, Time.frameCount);

            // Reset.
            cmd.SetComputeBufferParam(shader, ResetKernel, Id.ListCount, state.ListCounts);
            cmd.SetComputeBufferParam(shader, ResetKernel, Id.Counters, state.Counters);
            cmd.DispatchCompute(shader, ResetKernel, (lists + 63) / 64, 1, 1);

            // Camera, history and shadow splits.
            Vector3 position = data.CameraPosition;
            GeometryUtility.CalculateFrustumPlanes(camera, s_unityPlanes);
            for (int i = 0; i < 6; i++)
            {
                s_planes[i] = new Vector4(s_unityPlanes[i].normal.x, s_unityPlanes[i].normal.y, s_unityPlanes[i].normal.z, s_unityPlanes[i].distance);
            }

            float maxDistance = 0f;
            foreach (KindData kind in state.KindValues)
            {
                maxDistance = Mathf.Max(maxDistance, kind.LodDistance[Mathf.Clamp(kind.LodCount - 1, 0, 3)]);
            }

            cmd.SetComputeVectorParam(shader, Id.CameraPosition, new Vector4(position.x, position.y, position.z, maxDistance));
            cmd.SetComputeVectorArrayParam(shader, Id.FrustumPlanes, s_planes);
            bool occlusion = data.Occlusion && state.HiZValid && state.HiZ && HistoryMatches(state, camera, maxDistance);
            cmd.SetComputeMatrixParam(shader, Id.HiZViewProjection, state.HiZViewProjection);
            cmd.SetComputeVectorParam(shader, Id.HiZSize, new Vector4(state.HiZWidth, state.HiZHeight, state.HiZ ? state.HiZ.mipmapCount : 1, occlusion ? 1f : 0f));
            Vector3 light = state.LightDirection;
            cmd.SetComputeVectorParam(shader, Id.ShadowLight, new Vector4(light.x, light.y, light.z, state.SplitCount));
            cmd.SetComputeVectorArrayParam(shader, Id.ShadowSplits, state.Splits);
            cmd.SetComputeVectorParam(shader, Id.StatsRect, StatsRect);

            cmd.SetComputeBufferParam(shader, ScatterKernel, Id.Kinds, state.Kinds);
            cmd.SetComputeBufferParam(shader, ScatterKernel, Id.ListOffset, state.ListOffsets);
            cmd.SetComputeBufferParam(shader, ScatterKernel, Id.ListCapacity, state.ListCapacities);
            cmd.SetComputeBufferParam(shader, ScatterKernel, Id.ListCount, state.ListCounts);
            cmd.SetComputeBufferParam(shader, ScatterKernel, Id.Counters, state.Counters);
            cmd.SetComputeBufferParam(shader, ScatterKernel, Id.Stats, state.Stats);
            cmd.SetComputeBufferParam(shader, ScatterKernel, Id.Visible, state.Visible);
            cmd.SetComputeBufferParam(shader, ScatterKernel, Id.InstanceData, state.Instances);
            cmd.SetComputeTextureParam(shader, ScatterKernel, Id.HiZ, occlusion ? (Texture)state.HiZ : BlackFloat);

            double time = TimeOverride ?? Time.timeAsDouble;
            foreach (GrassMeshDetails owner in layout.Owners)
            {
                if (!owner)
                {
                    continue;
                }

                // Per owner: its wind and water line.
                owner.Wind.Resolve(time, out Vector4 direction, out Vector4 offset, out float turbulence);
                int period = Mathf.Max(1, Mathf.RoundToInt((float)(GrassMeshWind.OffsetPeriod / Mathf.Max(1f, owner.Wind.GustSize))));
                float gustScale = (float)(period / GrassMeshWind.OffsetPeriod);
                cmd.SetComputeVectorParam(shader, Id.WindDirection, direction);
                cmd.SetComputeVectorParam(shader, Id.WindParams, new Vector4(turbulence, gustScale, (float)(time % 3600.0), period));
                cmd.SetComputeVectorParam(shader, Id.WindOffset, offset);
                bool water = owner.TryResolveWaterLine(out float level, out float shoreFade);
                cmd.SetComputeVectorParam(shader, Id.Water, new Vector4(water ? 1f : 0f, level, shoreFade, 0f));

                float ownerDistance = 0f;
                for (int k = 0; k < layout.Kinds.Count; k++)
                {
                    if (layout.Kinds[k].Owner == owner)
                    {
                        KindData kind = state.KindValues[k];
                        ownerDistance = Mathf.Max(ownerDistance, kind.LodDistance[Mathf.Clamp(kind.LodCount - 1, 0, 3)]);
                    }
                }

                for (int t = 0; t < owner.Terrains.Count; t++)
                {
                    DispatchTerrain(cmd, shader, layout, owner, owner.Terrains[t], t, position, ownerDistance);
                }
            }

            cmd.SetComputeBufferParam(shader, FinalizeKernel, Id.DrawList, state.DrawLists);
            cmd.SetComputeBufferParam(shader, FinalizeKernel, Id.ListCount, state.ListCounts);
            cmd.SetComputeBufferParam(shader, FinalizeKernel, Id.ListCapacity, state.ListCapacities);
            cmd.SetComputeBufferParam(shader, FinalizeKernel, Id.Args, state.Args);
            cmd.DispatchCompute(shader, FinalizeKernel, (Mathf.Max(1, layout.Draws.Count) + 63) / 64, 1, 1);
            state.RequestReadback(cmd);
        }

        private void DispatchTerrain(CommandBuffer cmd, ComputeShader shader, Layout layout, GrassMeshDetails owner,
            GrassMeshDetailTerrain terrainEntry, int terrainIndex, Vector3 camera, float reach)
        {
            Terrain terrain = terrainEntry?.Terrain;
            if (!terrain || !terrain.isActiveAndEnabled || reach <= 0f)
            {
                return;
            }

            TerrainData data = terrain.terrainData;
            GrassMeshDetailDensity.Entry density = GrassMeshDetailDensity.Get(owner, terrainEntry);
            if (density == null || !density.Texture || !data.heightmapTexture || density.SliceKinds.Length == 0)
            {
                return;
            }

            Vector3 origin = terrain.GetPosition();
            Vector3 size = data.size;
            int resolution = density.Resolution;
            float cellX = size.x / resolution, cellZ = size.z / resolution;
            int x0 = Mathf.Clamp(Mathf.FloorToInt((camera.x - reach - origin.x) / cellX), 0, resolution);
            int x1 = Mathf.Clamp(Mathf.CeilToInt((camera.x + reach - origin.x) / cellX), 0, resolution);
            int z0 = Mathf.Clamp(Mathf.FloorToInt((camera.z - reach - origin.z) / cellZ), 0, resolution);
            int z1 = Mathf.Clamp(Mathf.CeilToInt((camera.z + reach - origin.z) / cellZ), 0, resolution);
            if (x1 <= x0 || z1 <= z0)
            {
                return;
            }

            DetailScatterMode mode = data.detailScatterMode;
            cmd.SetComputeVectorParam(shader, Id.TerrainOrigin, new Vector4(origin.x, origin.y, origin.z, resolution));
            cmd.SetComputeVectorParam(shader, Id.TerrainSize, new Vector4(size.x, size.y, size.z, terrain.detailObjectDensity));
            SetIntVector(cmd, Id.CellStart, x0, z0, x1 - x0, z1 - z0);
            Texture holes = data.holesTexture;
            bool hasHoles = holes;
            cmd.SetComputeTextureParam(shader, ScatterKernel, Id.TerrainHeightmap, data.heightmapTexture);
            cmd.SetComputeTextureParam(shader, ScatterKernel, Id.TerrainHoles, holes ? holes : Texture2D.whiteTexture);
            cmd.SetComputeTextureParam(shader, ScatterKernel, Id.Density, density.Texture);

            int seed = unchecked(terrainIndex * 7919 + (int)(Mathf.Round(origin.x) * 31 + Mathf.Round(origin.z) * 131));
            for (int begin = 0; begin < density.SliceKinds.Length; begin += GrassMeshDetailDensity.MaxSlicesPerDispatch)
            {
                int count = Mathf.Min(GrassMeshDetailDensity.MaxSlicesPerDispatch, density.SliceKinds.Length - begin);
                Array.Clear(s_sliceKinds, 0, s_sliceKinds.Length);
                Array.Clear(s_sliceScale, 0, s_sliceScale.Length);
                for (int s = 0; s < count; s++)
                {
                    int localKind = density.SliceKinds[begin + s];
                    int global = GlobalKindIndex(layout, owner, localKind);
                    s_sliceKinds[s] = Mathf.Max(0, global);
                    float scale = global < 0 ? 0f : GrassMeshDetailDensity.SliceScale(owner.Kinds[localKind], terrain, mode, owner.DensityScale);
                    Vector4 v = s_sliceScale[s >> 2];
                    v[s & 3] = scale;
                    s_sliceScale[s >> 2] = v;
                }

                cmd.SetComputeIntParams(shader, Id.SliceKinds, s_sliceKinds);
                cmd.SetComputeVectorArrayParam(shader, Id.SliceScale, s_sliceScale);
                SetIntVector(cmd, Id.TerrainInfo, mode == DetailScatterMode.InstanceCountMode ? 1 : 0, count, hasHoles ? 1 : 0, seed);
                SetIntVector(cmd, Id.SliceInfo, begin, 0, 0, 0);
                cmd.DispatchCompute(shader, ScatterKernel, (x1 - x0 + 7) / 8, (z1 - z0 + 7) / 8, 1);
            }
        }

        private static int GlobalKindIndex(Layout layout, GrassMeshDetails owner, int localKind)
        {
            for (int k = 0; k < layout.Kinds.Count; k++)
            {
                if (layout.Kinds[k].Owner == owner && layout.Kinds[k].Index == localKind)
                {
                    return k;
                }
            }

            return -1;
        }

        private static bool HistoryMatches(CameraState state, Camera camera, float reach)
        {
            // A cut (a jump of the camera) makes last frame's depth meaningless: draw everything for one frame.
            float moved = (camera.transform.position - state.HiZPosition).magnitude;
            float turned = Quaternion.Angle(camera.transform.rotation, state.HiZRotation);
            return moved <= Mathf.Max(2f, reach * 0.05f) && turned <= 12f;
        }

        private void ExecuteHiZ(CommandBuffer cmd, HiZData data)
        {
            CameraState state = data.State;
            if (!state.Valid || data.Width <= 1 || data.Height <= 1)
            {
                return;
            }

            ComputeShader shader = _shader;
            state.EnsureHiZ(data.Width, data.Height);
            SetIntVector(cmd, Id.Capacity, state.InstanceCapacity, _layout.ListCount, _layout.Draws.Count, SystemInfo.usesReversedZBuffer ? 1 : 0);
            RTHandle depth = data.Depth;
            int w = state.HiZWidth, h = state.HiZHeight;
            cmd.SetComputeTextureParam(shader, BuildHiZKernel, Id.SourceDepth, depth, 0, RenderTextureSubElement.Default);
            cmd.SetComputeTextureParam(shader, BuildHiZKernel, Id.HiZOut, state.HiZMips[0]);
            cmd.SetComputeVectorParam(shader, Id.HiZSource, new Vector4(data.Width, data.Height, w, h));
            cmd.DispatchCompute(shader, BuildHiZKernel, (w + 7) / 8, (h + 7) / 8, 1);
            cmd.CopyTexture(state.HiZMips[0], 0, 0, state.HiZ, 0, 0);
            for (int m = 1; m < state.HiZMips.Length; m++)
            {
                RenderTexture source = state.HiZMips[m - 1], target = state.HiZMips[m];
                cmd.SetComputeTextureParam(shader, ReduceHiZKernel, Id.HiZIn, source);
                cmd.SetComputeTextureParam(shader, ReduceHiZKernel, Id.HiZOut, target);
                cmd.SetComputeVectorParam(shader, Id.HiZSource, new Vector4(source.width, source.height, target.width, target.height));
                cmd.DispatchCompute(shader, ReduceHiZKernel, (target.width + 7) / 8, (target.height + 7) / 8, 1);
                cmd.CopyTexture(target, 0, 0, state.HiZ, 0, m);
            }

            state.HiZViewProjection = data.ViewProjection;
            state.HiZPosition = data.Position;
            state.HiZRotation = data.Rotation;
            state.HiZValid = true;
        }
    }
}
