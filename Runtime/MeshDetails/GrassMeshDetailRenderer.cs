using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Cone.Grass
{
    /// <summary>
    /// The GPU side of <see cref="GrassMeshDetails"/>: one BatchRendererGroup whose indirect draw commands read instance
    /// data and visible lists that the scatter kernel writes per camera each frame. URP draws them in its own passes.
    /// </summary>
    internal sealed unsafe partial class GrassMeshDetailRenderer : IDisposable
    {
        internal const int MaxSplits = 4;
        internal const int InstanceBytes = 48 + 48 + 16;
        private const int HeaderBytes = 64;
        private static readonly List<GrassMeshDetailRenderer> s_renderers = new List<GrassMeshDetailRenderer>();
        private static readonly int s_objectToWorld = UnityEngine.Shader.PropertyToID("unity_ObjectToWorld");
        private static readonly int s_worldToObject = UnityEngine.Shader.PropertyToID("unity_WorldToObject");
        private static readonly int s_baseColor = UnityEngine.Shader.PropertyToID("_BaseColor");
        private static readonly int s_color = UnityEngine.Shader.PropertyToID("_Color");

        private readonly ComputeShader _shader;
        private readonly BatchRendererGroup _brg;
        private readonly Dictionary<Mesh, BatchMeshID> _meshes = new Dictionary<Mesh, BatchMeshID>();
        private readonly Dictionary<Material, BatchMaterialID> _materials = new Dictionary<Material, BatchMaterialID>();
        private readonly Dictionary<Camera, CameraState> _cameras = new Dictionary<Camera, CameraState>();
        private readonly Dictionary<EntityId, CameraState> _byEntity = new Dictionary<EntityId, CameraState>();
        private readonly List<Camera> _stale = new List<Camera>();
        private readonly HashSet<Material> _warnedMaterials = new HashSet<Material>();
        private readonly Layout _layout = new Layout();
        private uint _layoutRevision;
        private CameraState _current;
        private bool _disposed;

        internal readonly int ResetKernel, ScatterKernel, FinalizeKernel, BuildHiZKernel, ReduceHiZKernel;
        internal readonly bool IsSupported;

        internal GrassMeshDetailRenderer(ComputeShader shader)
        {
            _shader = shader;
            IsSupported = shader && SystemInfo.supportsComputeShaders && SystemInfo.supportsIndirectArgumentsBuffer &&
                          BatchRendererGroup.BufferTarget == BatchBufferTarget.RawBuffer &&
                          shader.HasKernel("Scatter") && shader.HasKernel("ResetLists") && shader.HasKernel("FinalizeArgs") &&
                          shader.HasKernel("BuildHiZ") && shader.HasKernel("ReduceHiZ");
            if (!IsSupported)
            {
                return;
            }

            ResetKernel = shader.FindKernel("ResetLists");
            ScatterKernel = shader.FindKernel("Scatter");
            FinalizeKernel = shader.FindKernel("FinalizeArgs");
            BuildHiZKernel = shader.FindKernel("BuildHiZ");
            ReduceHiZKernel = shader.FindKernel("ReduceHiZ");
            _brg = new BatchRendererGroup(OnPerformCulling, IntPtr.Zero);
            _brg.SetGlobalBounds(new Bounds(Vector3.zero, new Vector3(1e7f, 1e7f, 1e7f)));
            _brg.SetEnabledViewTypes(new[] { BatchCullingViewType.Camera, BatchCullingViewType.Light });
            s_renderers.Add(this);
        }

        internal ComputeShader Shader => _shader;

        internal Layout CurrentLayout => _layout;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            s_renderers.Remove(this);
            foreach (CameraState state in _cameras.Values)
            {
                state.Release(_brg);
            }

            _cameras.Clear();
            _byEntity.Clear();
            if (_brg != null)
            {
                foreach (BatchMeshID id in _meshes.Values)
                {
                    _brg.UnregisterMesh(id);
                }

                foreach (BatchMaterialID id in _materials.Values)
                {
                    _brg.UnregisterMaterial(id);
                }

                _brg.Dispose();
            }

            _meshes.Clear();
            _materials.Clear();
        }

        /// <summary>Which cameras draw mesh kinds: game, scene view and (when a component allows it) reflection cameras.</summary>
        internal static bool Admits(Camera camera, bool reflectionsWanted)
        {
            if (!camera)
            {
                return false;
            }

            switch (camera.cameraType)
            {
                case CameraType.Game:
                case CameraType.SceneView:
                    return true;
                case CameraType.Reflection:
                    return reflectionsWanted;
                default:
                    return false;
            }
        }

        /// <summary>Before URP culls a camera: picks its state, so the BatchRendererGroup callbacks that follow can find it.</summary>
        internal void PreCull(Camera camera, bool eligible)
        {
            _current = null;
            if (!IsSupported || _disposed)
            {
                return;
            }

            PruneCameras();
            RefreshLayout();
            if (!eligible || _layout.Draws.Count == 0 || !Admits(camera, _layout.AnyReflections))
            {
                ReleaseCamera(camera);
                return;
            }

            if (!_cameras.TryGetValue(camera, out CameraState state))
            {
                state = new CameraState(camera);
                _cameras.Add(camera, state);
            }

            state.Ensure(this, _layout, _brg);
            if (!state.Valid)
            {
                return;
            }

            state.Frame = Time.frameCount;
            state.LastUsed = Time.realtimeSinceStartupAsDouble;
            state.SplitCount = 0;
            _byEntity[camera.GetEntityId()] = state;
            _byEntity[camera.gameObject.GetEntityId()] = state;
            _current = state;
        }

        /// <summary>The state URP is rendering, when PreCull admitted the camera this frame.</summary>
        internal bool TryGetPrepared(Camera camera, out CameraState state)
        {
            return _cameras.TryGetValue(camera, out state) && state.Valid && state.Frame == Time.frameCount;
        }

        internal void ReleaseCamera(Camera camera)
        {
            if (camera && _cameras.TryGetValue(camera, out CameraState state))
            {
                state.Release(_brg);
                _cameras.Remove(camera);
                RemoveEntity(state);
            }
        }

        private void RemoveEntity(CameraState state)
        {
            var keys = new List<EntityId>();
            foreach (KeyValuePair<EntityId, CameraState> pair in _byEntity)
            {
                if (pair.Value == state)
                {
                    keys.Add(pair.Key);
                }
            }

            foreach (EntityId key in keys)
            {
                _byEntity.Remove(key);
            }
        }

        private void PruneCameras()
        {
            _stale.Clear();
            double now = Time.realtimeSinceStartupAsDouble;
            foreach (KeyValuePair<Camera, CameraState> pair in _cameras)
            {
                if (!pair.Key || now - pair.Value.LastUsed > 10.0)
                {
                    _stale.Add(pair.Key);
                }
            }

            foreach (Camera camera in _stale)
            {
                CameraState state = _cameras[camera];
                state.Release(_brg);
                _cameras.Remove(camera);
                RemoveEntity(state);
            }
        }

        // -----------------------------------------------------------------------------------------------------------------
        // Layout: every enabled kind of every active component, flattened, with its lists and draws.

        internal sealed class KindEntry
        {
            public GrassMeshDetails Owner;
            public int Index;
            public GrassMeshKind Kind;
            public int LodCount;
            public int ListBase;
            public KindData Data;
        }

        internal struct DrawEntry
        {
            public int List;
            public int Split; // -1 camera
            public bool Fade;
            public BatchMeshID Mesh;
            public BatchMaterialID Material;
            public uint IndexCount, StartIndex, BaseVertex;
            public MeshTopology Topology;
            public int Layer;
            public int OwnerIndex;
        }

        internal sealed class Layout
        {
            public readonly List<KindEntry> Kinds = new List<KindEntry>();
            public readonly List<DrawEntry> Draws = new List<DrawEntry>();
            public readonly List<GrassMeshDetails> Owners = new List<GrassMeshDetails>();
            public int ListCount;
            public int[] ListKind = Array.Empty<int>();
            public bool[] ListShadow = Array.Empty<bool>();
            public bool AnyReflections;
            public bool AnyColor;
            public uint Revision;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct KindData
        {
            public Vector4 LodDistance;
            public Vector4 ScaleRange;
            public Vector4 Healthy;
            public Vector4 Dry;
            public Vector4 BoundsCenter;
            public Vector4 BoundsExtent;
            public Vector4 Placement;
            public Vector4 Wind;
            public int LodCount, CastsShadows, ListBase, ColorMode;
        }

        internal const int KindDataStride = 8 * 16 + 16;

        private void RefreshLayout()
        {
            uint revision = GrassMeshDetails.GlobalRevision;
            if (_layoutRevision == revision && _layout.Revision == revision)
            {
                return;
            }

            _layoutRevision = revision;
            _layout.Kinds.Clear();
            _layout.Draws.Clear();
            _layout.Owners.Clear();
            _layout.AnyReflections = false;
            _layout.AnyColor = false;
            _layout.Revision = revision;
            int lists = 0;
            var listKind = new List<int>();
            var listShadow = new List<bool>();
            foreach (GrassMeshDetails owner in GrassMeshDetails.Active)
            {
                if (!owner || !owner.isActiveAndEnabled)
                {
                    continue;
                }

                int ownerIndex = _layout.Owners.Count;
                _layout.Owners.Add(owner);
                _layout.AnyReflections |= owner.DrawInReflections;
                for (int k = 0; k < owner.Kinds.Count; k++)
                {
                    GrassMeshKind kind = owner.Kinds[k];
                    int lodCount = kind != null && kind.Enabled ? kind.UsableLodCount : 0;
                    if (lodCount == 0)
                    {
                        continue;
                    }

                    var entry = new KindEntry { Owner = owner, Index = k, Kind = kind, LodCount = lodCount, ListBase = lists };
                    int kindIndex = _layout.Kinds.Count;
                    _layout.Kinds.Add(entry);
                    _layout.AnyColor |= kind.ColorVariation != GrassMeshColorVariation.None;
                    bool shadows = owner.CastShadows && kind.ShadowCasting != ShadowCastingMode.Off && kind.ShadowDistance > 0f;
                    for (int l = 0; l < lodCount * (2 + MaxSplits); l++)
                    {
                        listKind.Add(kindIndex);
                        listShadow.Add(l >= lodCount * 2);
                    }

                    for (int l = 0; l < lodCount; l++)
                    {
                        GrassMeshKindLod lod = kind.Lods[l];
                        Mesh mesh = lod.Mesh;
                        BatchMeshID meshID = RegisterMesh(mesh);
                        for (int sm = 0; sm < mesh.subMeshCount; sm++)
                        {
                            Material material = lod.MaterialFor(sm);
                            if (!material || !SupportsBatchRendererGroup(material))
                            {
                                continue;
                            }

                            SubMeshDescriptor sub = mesh.GetSubMesh(sm);
                            var draw = new DrawEntry
                            {
                                Mesh = meshID,
                                Material = RegisterMaterial(material),
                                IndexCount = (uint)sub.indexCount,
                                StartIndex = (uint)sub.indexStart,
                                BaseVertex = (uint)sub.baseVertex,
                                Topology = sub.topology,
                                Layer = owner.gameObject.layer,
                                OwnerIndex = ownerIndex
                            };
                            draw.List = lists + l * 2;
                            draw.Split = -1;
                            draw.Fade = false;
                            _layout.Draws.Add(draw);
                            draw.List = lists + l * 2 + 1;
                            draw.Fade = true;
                            _layout.Draws.Add(draw);
                            if (shadows)
                            {
                                for (int split = 0; split < MaxSplits; split++)
                                {
                                    draw.List = lists + lodCount * 2 + l * MaxSplits + split;
                                    draw.Split = split;
                                    draw.Fade = false;
                                    _layout.Draws.Add(draw);
                                }
                            }
                        }
                    }

                    lists += lodCount * (2 + MaxSplits);
                }
            }

            _layout.ListCount = lists;
            _layout.ListKind = listKind.ToArray();
            _layout.ListShadow = listShadow.ToArray();
            for (int i = 0; i < _layout.Kinds.Count; i++)
            {
                _layout.Kinds[i].Data = BuildKindData(_layout.Kinds[i], 1f);
            }
        }

        internal static KindData BuildKindData(KindEntry entry, float cameraDistanceScale)
        {
            GrassMeshKind kind = entry.Kind;
            GrassMeshDetails owner = entry.Owner;
            float distanceScale = Mathf.Max(0.01f, owner.DistanceScale) * cameraDistanceScale;
            var data = new KindData();
            var bounds = new Bounds();
            bool hasBounds = false;
            for (int l = 0; l < entry.LodCount; l++)
            {
                GrassMeshKindLod lod = kind.Lods[l];
                float previous = l == 0 ? 0f : data.LodDistance[l - 1];
                data.LodDistance[l] = Mathf.Max(previous + 0.01f, Finite(lod.EndDistance, 80f) * distanceScale);
                if (hasBounds)
                {
                    bounds.Encapsulate(lod.Mesh.bounds);
                }
                else
                {
                    bounds = lod.Mesh.bounds;
                    hasBounds = true;
                }
            }

            float minWidth = Mathf.Max(0f, Finite(kind.MinWidth, 1f)), maxWidth = Mathf.Max(minWidth, Finite(kind.MaxWidth, 1f));
            float minHeight = Mathf.Max(0f, Finite(kind.MinHeight, 1f)), maxHeight = Mathf.Max(minHeight, Finite(kind.MaxHeight, 1f));
            data.ScaleRange = new Vector4(minWidth, maxWidth, minHeight, maxHeight);
            Color baseColor = BaseColor(kind);
            bool tint = kind.ColorVariation == GrassMeshColorVariation.HealthyDry;
            Color healthy = tint ? baseColor.linear * kind.HealthyColor.linear : baseColor.linear;
            Color dry = tint ? baseColor.linear * kind.DryColor.linear : baseColor.linear;
            healthy.a = dry.a = baseColor.a;
            data.Healthy = healthy;
            data.Dry = dry;
            bool shadows = owner.CastShadows && kind.ShadowCasting != ShadowCastingMode.Off;
            data.BoundsCenter = new Vector4(bounds.center.x, bounds.center.y, bounds.center.z,
                shadows ? Mathf.Max(0f, Finite(kind.ShadowDistance, 40f)) * distanceScale : 0f);
            data.BoundsExtent = new Vector4(bounds.extents.x, bounds.extents.y, bounds.extents.z,
                Mathf.Max(0.001f, Finite(kind.CrossFadeWidth, 6f)) * distanceScale);
            data.Placement = new Vector4(Mathf.Max(0f, Finite(kind.NoiseSpread, 0.1f)), kind.NoiseSeed % 4096,
                Mathf.Clamp01(Finite(kind.AlignToGround, 0f)), Mathf.Clamp01(Finite(kind.PositionJitter, 1f)));
            data.Wind = new Vector4(Finite(kind.WindBend, 0f), Mathf.Clamp01(Finite(kind.WindFlutter, 0f)), 0f, 0f);
            data.LodCount = entry.LodCount;
            data.CastsShadows = shadows ? 1 : 0;
            data.ListBase = entry.ListBase;
            data.ColorMode = tint ? 1 : 0;
            return data;
        }

        private static Color BaseColor(GrassMeshKind kind)
        {
            Material material = kind.Lods.Count > 0 && kind.Lods[0] != null ? kind.Lods[0].MaterialFor(0) : null;
            if (material && material.HasProperty(s_baseColor))
            {
                return material.GetColor(s_baseColor);
            }

            return material && material.HasProperty(s_color) ? material.GetColor(s_color) : Color.white;
        }

        private BatchMeshID RegisterMesh(Mesh mesh)
        {
            if (!_meshes.TryGetValue(mesh, out BatchMeshID id))
            {
                id = _brg.RegisterMesh(mesh);
                _meshes.Add(mesh, id);
            }

            return id;
        }

        private BatchMaterialID RegisterMaterial(Material material)
        {
            if (!_materials.TryGetValue(material, out BatchMaterialID id))
            {
                id = _brg.RegisterMaterial(material);
                _materials.Add(material, id);
            }

            return id;
        }

        /// <summary>Whether a material's shader has the DOTS instancing variant a BatchRendererGroup draw needs.</summary>
        internal static bool HasDotsInstancing(Material material)
        {
            Shader shader = material ? material.shader : null;
            if (!shader)
            {
                return false;
            }

            foreach (string keyword in shader.keywordSpace.keywordNames)
            {
                if (keyword == "DOTS_INSTANCING_ON")
                {
                    return true;
                }
            }

            return false;
        }

        private bool SupportsBatchRendererGroup(Material material)
        {
            if (HasDotsInstancing(material))
            {
                return true;
            }

            if (_warnedMaterials.Add(material))
            {
                Debug.LogWarning($"Grass mesh details skip material '{material.name}': its shader '{material.shader.name}' has no DOTS " +
                    "instancing variant, which BatchRendererGroup draws need. Use URP Lit, a URP Shader Graph or Cone/Foliage.", material);
            }

            return false;
        }

        private static float Finite(float value, float fallback) => float.IsFinite(value) ? value : fallback;

        // -----------------------------------------------------------------------------------------------------------------
        // BatchRendererGroup culling: hands URP the indirect draws whose lists the scatter kernel fills later this frame.

        private JobHandle OnPerformCulling(BatchRendererGroup group, BatchCullingContext context, BatchCullingOutput output, IntPtr userContext)
        {
            CameraState state = null;
            int splitCount = 0;
            if (context.viewType == BatchCullingViewType.Camera)
            {
                if (!_byEntity.TryGetValue(context.viewID.GetEntityId(), out state) || state.Frame != Time.frameCount)
                {
                    return default;
                }
            }
            else if (context.viewType == BatchCullingViewType.Light)
            {
                state = _current;
                // Only directional cascades: punctual lights get no detail shadows.
                if (state == null || !state.Valid || state.Frame != Time.frameCount ||
                    context.projectionType != BatchCullingProjectionType.Orthographic || context.cullingSplits.Length == 0)
                {
                    return default;
                }

                splitCount = Mathf.Min(context.cullingSplits.Length, MaxSplits);
                Matrix4x4 lightToWorld = context.localToWorldMatrix;
                Vector3 forward = lightToWorld.GetColumn(2);
                state.LightDirection = forward.sqrMagnitude > 0f ? -forward.normalized : Vector3.up;
                for (int i = 0; i < splitCount; i++)
                {
                    CullingSplit split = context.cullingSplits[i];
                    state.Splits[i] = new Vector4(split.sphereCenter.x, split.sphereCenter.y, split.sphereCenter.z, split.sphereRadius);
                }

                state.SplitCount = splitCount;
            }
            else
            {
                return default;
            }

            if (state == null || !state.Valid || _layout.Draws.Count == 0)
            {
                return default;
            }

            bool shadowView = context.viewType == BatchCullingViewType.Light;
            int count = 0;
            foreach (DrawEntry draw in _layout.Draws)
            {
                if (shadowView ? draw.Split >= 0 && draw.Split < splitCount : draw.Split < 0)
                {
                    count++;
                }
            }

            if (count == 0)
            {
                return default;
            }

            var commands = (BatchCullingOutputDrawCommands*)output.drawCommands.GetUnsafePtr();
            int owners = _layout.Owners.Count;
            commands->indirectDrawCommands = Malloc<BatchDrawCommandIndirect>(count);
            commands->indirectDrawCommandCount = count;
            commands->drawRanges = Malloc<BatchDrawRange>(owners);
            commands->drawRangeCount = 0;
            commands->drawCommands = null;
            commands->drawCommandCount = 0;
            commands->visibleInstances = null;
            commands->visibleInstanceCount = 0;
            commands->instanceSortingPositions = null;
            commands->instanceSortingPositionFloatCount = 0;
            commands->proceduralDrawCommands = null;
            commands->proceduralDrawCommandCount = 0;
            commands->proceduralIndirectDrawCommands = null;
            commands->proceduralIndirectDrawCommandCount = 0;
            commands->drawCommandPickingEntityIds = null;

            int written = 0;
            uint visibleBytes = (uint)state.VisibleBytes;
            for (int owner = 0; owner < owners; owner++)
            {
                int begin = written;
                int layer = 0;
                for (int d = 0; d < _layout.Draws.Count; d++)
                {
                    DrawEntry draw = _layout.Draws[d];
                    if (draw.OwnerIndex != owner || (shadowView ? draw.Split < 0 || draw.Split >= splitCount : draw.Split >= 0))
                    {
                        continue;
                    }

                    layer = draw.Layer;
                    BatchDrawCommandFlags flags = draw.Fade ? BatchDrawCommandFlags.LODCrossFade : BatchDrawCommandFlags.None;
                    commands->indirectDrawCommands[written++] = new BatchDrawCommandIndirect
                    {
                        flags = flags,
                        visibleOffset = (uint)state.ListOffset[draw.List],
                        batchID = state.Batch,
                        materialID = draw.Material,
                        splitVisibilityMask = (ushort)(shadowView ? 1 << draw.Split : 0xff),
                        lightmapIndex = 0xffff,
                        sortingPosition = 0,
                        meshID = draw.Mesh,
                        topology = draw.Topology,
                        visibleInstancesBufferHandle = state.Visible.bufferHandle,
                        visibleInstancesBufferWindowOffset = 0,
                        visibleInstancesBufferWindowSizeBytes = visibleBytes,
                        indirectArgsBufferHandle = state.Args.bufferHandle,
                        indirectArgsBufferOffset = (uint)(d * GraphicsBuffer.IndirectDrawIndexedArgs.size)
                    };
                }

                if (written == begin)
                {
                    continue;
                }

                commands->drawRanges[commands->drawRangeCount++] = new BatchDrawRange
                {
                    drawCommandsType = BatchDrawCommandType.Indirect,
                    drawCommandsBegin = (uint)begin,
                    drawCommandsCount = (uint)(written - begin),
                    filterSettings = new BatchFilterSettings
                    {
                        renderingLayerMask = 1,
                        layer = (byte)layer,
                        motionMode = MotionVectorGenerationMode.Camera,
                        shadowCastingMode = shadowView ? ShadowCastingMode.On : ShadowCastingMode.Off,
                        receiveShadows = true,
                        staticShadowCaster = false,
                        allDepthSorted = false
                    }
                };
            }

            return default;
        }

        private static T* Malloc<T>(int count) where T : unmanaged
        {
            return (T*)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<T>() * Mathf.Max(1, count), UnsafeUtility.AlignOf<T>(), Allocator.TempJob);
        }

        // -----------------------------------------------------------------------------------------------------------------
        // Diagnostics.

        internal static bool TryGetCounts(GrassMeshDetails owner, Camera camera, out int[] cameraInstances, out int[] shadowInstances)
        {
            cameraInstances = null;
            shadowInstances = null;
            foreach (GrassMeshDetailRenderer renderer in s_renderers)
            {
                if (camera && renderer._cameras.TryGetValue(camera, out CameraState state) && state.LastCounts != null &&
                    renderer.TryCollect(owner, state.LastCounts, state.LastCountsLayout, out cameraInstances, out shadowInstances))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Tests: reads the camera's lists and statistics now, synchronously.</summary>
        internal static bool TryReadNow(Camera camera, out CameraReadback readback)
        {
            readback = null;
            foreach (GrassMeshDetailRenderer renderer in s_renderers)
            {
                if (camera && renderer._cameras.TryGetValue(camera, out CameraState state) && state.Valid)
                {
                    readback = state.ReadNow(renderer._layout);
                    return true;
                }
            }

            return false;
        }

        private bool TryCollect(GrassMeshDetails owner, uint[] counts, uint layoutRevision, out int[] cameraInstances, out int[] shadowInstances)
        {
            cameraInstances = new int[owner.Kinds.Count];
            shadowInstances = new int[owner.Kinds.Count];
            if (layoutRevision != _layout.Revision)
            {
                return false;
            }

            for (int list = 0; list < _layout.ListCount && list < counts.Length; list++)
            {
                KindEntry entry = _layout.Kinds[_layout.ListKind[list]];
                if (entry.Owner != owner)
                {
                    continue;
                }

                // Instance draws: an instance cross-fading between two LODs is drawn (and counted) in both.
                if (_layout.ListShadow[list])
                {
                    shadowInstances[entry.Index] += (int)counts[list];
                }
                else
                {
                    cameraInstances[entry.Index] += (int)counts[list];
                }
            }

            return true;
        }
    }

    /// <summary>A synchronous read of one camera's mesh-detail lists, for tests and diagnostics.</summary>
    internal sealed class CameraReadback
    {
        public uint[] ListCounts;
        public uint[] ListCapacities;
        public uint[] ListOffsets;
        public uint[] Visible;
        public uint[] Counters;
        public uint[] Stats;
        public float[] InstanceData;
        public int InstanceCapacity;
        public int[] ListKind;
        public bool[] ListShadow;
        public int[] KindListBase;
        public int[] KindLodCount;
    }
}
