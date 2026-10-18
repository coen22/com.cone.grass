using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public partial class GrassDataRendererFeature
{
    // Per-camera persistent resources.
    private sealed partial class GrassDataPass
    {
        private sealed class CameraState : IDisposable
        {
            public readonly Camera Camera;
            public InfiniteGrassRenderer Owner;
            public uint OwnerRevision, InventoryRevision, OwnerInteractionRevision;
            public ulong GroundVersion, NextGroundVersion, SurfaceVersion, NextSurfaceVersion;
            public ulong InteractionVersion, NextInteractionVersion;
            public bool CacheValid, WarnedBudget, Disposed;
            public Vector2 Center;
            public Vector2 CaptureRange;
            public float CaptureExtent, MotionSpacing;
            public bool Authored, DensityShortens, HasMeshSurfaceFallback, RequiresMeshHeight, SurfaceDirty, HadDensitySources;
            public double LastUsedTime;
            public RTHandle Height, HeightDepth, Density, Mask, Color, Slope, Ground;
            public GraphicsBuffer Positions, Counts, Arguments;
            public Material BladeMaterial;
            private Material sourceMaterial;
            private int allocatedCapacity;
            private readonly Mesh[] argumentMeshes = new Mesh[3];
            private readonly GraphicsBuffer.IndirectDrawIndexedArgs[] argumentData = new GraphicsBuffer.IndirectDrawIndexedArgs[3];
            public readonly int[] LodOffsets = new int[4];
            public readonly int[] LodCapacities = new int[4];
            public readonly Plane[] Planes = new Plane[6];
            public readonly Vector4[] Frustum = new Vector4[6];
            public readonly MaterialPropertyBlock[] DrawProperties = { new MaterialPropertyBlock(), new MaterialPropertyBlock(), new MaterialPropertyBlock() };
            public readonly MaterialPropertyBlock CaptureProperties = new MaterialPropertyBlock();
            public readonly List<int> CaptureTextureIds = new List<int>();
            public readonly List<Texture> CaptureTextures = new List<Texture>();
            public readonly TextureHandle[] VertexTextures = new TextureHandle[6];
            public Renderer[] CaptureRenderers;
            public IReadOnlyList<Renderer> ModifierRenderers;
            public readonly HashSet<Renderer> ExplicitSurfaces = new HashSet<Renderer>();
            public readonly List<Terrain> SurfaceTerrains = new List<Terrain>();
            public readonly List<TerrainDraw> TerrainDraws = new List<TerrainDraw>();
            public readonly List<RendererDraw> HeightDraws = new List<RendererDraw>();
            public readonly List<RendererDraw> MaskDraws = new List<RendererDraw>();
            public readonly List<RendererDraw> ColorDraws = new List<RendererDraw>();
            public readonly List<RendererDraw> SlopeDraws = new List<RendererDraw>();
            public readonly List<InteractionDraw> InteractionDraws = new List<InteractionDraw>();
            public readonly List<PlacementSource> Sources = new List<PlacementSource>();
            public readonly Dictionary<EntityId, TerrainGroup> Groups = new Dictionary<EntityId, TerrainGroup>();
            public readonly List<EntityId> StaleGroupKeys = new List<EntityId>();
            public readonly List<TerrainGroup> ActiveGroups = new List<TerrainGroup>();
            public readonly List<TerrainGroup> InactiveGroups = new List<TerrainGroup>();
            public TerrainGroup PrimaryDensityGroup;
            public readonly List<DispatchData> Dispatches = new List<DispatchData>();
            public int DispatchCount;
            public long CandidateCount;
            public readonly Dictionary<Texture, TextureWrapper> TextureWrappers = new Dictionary<Texture, TextureWrapper>();
            public readonly Dictionary<Texture, TextureHandle> ImportedTextures = new Dictionary<Texture, TextureHandle>();
            private readonly List<Texture> staleTextures = new List<Texture>();
            public bool ReadbackPending;
            public float NextReadbackTime;
            public InfiniteGrassRenderer ReadbackOwner;
            public uint ReadbackCapacity0, ReadbackCapacity1, ReadbackCapacity2;
            public readonly Action<AsyncGPUReadbackRequest> ReadbackCallback;

            public CameraState(Camera camera)
            {
                Camera = camera;
                ReadbackCallback = OnReadback;
            }

            public void ReleaseDispatchGroups()
            {
                // Only the current plan can hold references: successful and
                // rejected plans release them before a shorter plan reuses the pool.
                for (int i = 0; i < DispatchCount; i++)
                    Dispatches[i].Group = null;
            }

            public void RejectDispatches()
            {
                ReleaseDispatchGroups();
                DispatchCount = 0;
                CandidateCount = 0;
            }

            public bool EnsureResources(InfiniteGrassRenderer owner, Mesh[] meshes, int argumentStride)
            {
                bool changed = false;
                int resolution = Mathf.Clamp(owner.captureResolution, 128, SystemInfo.maxTextureSize);
                changed |= Allocate(ref Height, resolution, GraphicsFormat.R32G32_SFloat, "Grass World Height");
                changed |= Allocate(ref HeightDepth, resolution, GraphicsFormat.D32_SFloat, "Grass Capture Depth", true);
                changed |= Allocate(ref Density, resolution, GraphicsFormat.R8_UNorm, "Grass Density");
                changed |= Allocate(ref Mask, resolution, GraphicsFormat.R8_UNorm, "Grass Exclusion");
                changed |= Allocate(ref Color, resolution, GraphicsFormat.R16G16B16A16_SFloat, "Grass Color");
                changed |= Allocate(ref Slope, resolution, GraphicsFormat.R8G8B8A8_UNorm, "Grass Interaction");
                changed |= Allocate(ref Ground, resolution, GraphicsFormat.R16G16B16A16_SFloat, "Grass Ground Color");

                int capacity = Mathf.Max(3, owner.Capacity);
                if (Positions == null || !Positions.IsValid() || allocatedCapacity < capacity || capacity <= allocatedCapacity / 2)
                {
                    Positions?.Dispose();
                    Positions = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, sizeof(float) * 4)
                    { name = "Grass Positions " + Camera.GetEntityId() };
                    allocatedCapacity = capacity;
                }
                if (Counts == null || !Counts.IsValid())
                {
                    Counts?.Dispose();
                    Counts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 4, sizeof(uint)) { name = "Grass Counts" };
                }
                bool uploadArguments = Arguments == null || !Arguments.IsValid();
                if (uploadArguments)
                {
                    Arguments?.Dispose();
                    Arguments = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments,
                        3, argumentStride) { name = "Grass Indirect Arguments" };
                }
                owner.GetLodCapacity(LodCapacities, LodOffsets);
                uploadArguments |= UpdateArgumentData(meshes);
                if (uploadArguments)
                    Arguments.SetData(argumentData);
                return changed;
            }

            private bool UpdateArgumentData(Mesh[] meshes)
            {
                bool changed = false;
                for (int lod = 0; lod < 3; lod++)
                {
                    var next = new GraphicsBuffer.IndirectDrawIndexedArgs
                    {
                        indexCountPerInstance = meshes[lod].GetIndexCount(0),
                        instanceCount = 0,
                        startIndex = meshes[lod].GetIndexStart(0),
                        baseVertexIndex = meshes[lod].GetBaseVertex(0),
                        startInstance = 0
                    };
                    // Public cache accessors expose mutable Mesh instances.
                    // Topology can change while the mesh reference stays stable.
                    GraphicsBuffer.IndirectDrawIndexedArgs previous = argumentData[lod];
                    changed |= argumentMeshes[lod] != meshes[lod] ||
                        previous.indexCountPerInstance != next.indexCountPerInstance ||
                        previous.startIndex != next.startIndex ||
                        previous.baseVertexIndex != next.baseVertexIndex;
                    argumentMeshes[lod] = meshes[lod];
                    argumentData[lod] = next;
                }
                return changed;
            }

            public bool UpdateMaterial(InfiniteGrassRenderer owner, bool multisampled)
            {
                bool changed = !BladeMaterial || sourceMaterial != owner.grassMaterial ||
                    BladeMaterial.shader != owner.grassMaterial.shader;
                if (changed)
                {
                    CoreUtils.Destroy(BladeMaterial);
                    BladeMaterial = new Material(owner.grassMaterial) { hideFlags = HideFlags.HideAndDontSave };
                    sourceMaterial = owner.grassMaterial;
                }
                BladeMaterial.CopyPropertiesFromMaterial(owner.grassMaterial);
                BladeMaterial.enableInstancing = true;
                BladeMaterial.SetFloat(Id.AlphaToCoverage, owner.alphaToCoverage && multisampled ? 1f : 0f);
                BladeMaterial.SetFloat(Id.GroundStrength, owner.groundBlendStrength);
                BladeMaterial.SetFloat(Id.GroundHeight, owner.groundBlendHeight);
                BladeMaterial.SetFloat(Id.MinimumWidth, owner.minimumPixelWidth);
                BladeMaterial.SetFloat(Id.SpecularStart, owner.specularFadeRange.x);
                BladeMaterial.SetFloat(Id.SpecularEnd, owner.specularFadeRange.y);
                BladeMaterial.SetFloat(Id.Subdivision, owner.grassMeshSubdivision);
                BladeMaterial.SetFloat(Id.SubdivisionDistance, owner.subdivisionDistance);
                BladeMaterial.SetFloat(Id.SubdivisionHeightBoost, owner.subdivisionHeightBoost);
                BladeMaterial.SetFloat(Id.SubdivisionBumpWidth, owner.subdivisionBumpWidth);
                BladeMaterial.SetFloat(Id.FullDensity, owner.fullDensityDistance);
                BladeMaterial.SetFloat(Id.DensityExponent, owner.densityFalloffExponent);
                BladeMaterial.SetFloat(Id.BladeSpacing, owner.spacing);
                return changed;
            }

            public void SetDrawProperties(Vector2 center, float distance,
                float capturePadding, Texture wind, int mainLightCascades, Terrain splatTerrain)
            {
                // Indirect draws have no per-renderer UnityPerDraw probe setup.
                // Use the scene ambient probe, packed by Core's official helper;
                // this does not sample local LightProbes at individual grass roots.
                SHCoefficients ambient = new SHCoefficients(RenderSettings.ambientProbe);
                for (int lod = 0; lod < 3; lod++)
                {
                    MaterialPropertyBlock properties = DrawProperties[lod];
                    properties.Clear();
                    properties.SetInteger(Id.ExplicitTime, 0);
                    properties.SetBuffer(Id.Positions, Positions);
                    properties.SetInteger(Id.InstanceOffset, LodOffsets[lod]);
                    properties.SetInteger(Id.MainLightCascades, mainLightCascades);
                    properties.SetVector(Id.SHAr, ambient.SHAr);
                    properties.SetVector(Id.SHAg, ambient.SHAg);
                    properties.SetVector(Id.SHAb, ambient.SHAb);
                    properties.SetVector(Id.SHBr, ambient.SHBr);
                    properties.SetVector(Id.SHBg, ambient.SHBg);
                    properties.SetVector(Id.SHBb, ambient.SHBb);
                    properties.SetVector(Id.SHC, ambient.SHC);
                    properties.SetVector(Id.Center, new Vector4(center.x, center.y, 0f, 0f));
                    properties.SetFloat(Id.DrawDistance, distance);
                    properties.SetFloat(Id.CapturePadding, capturePadding);
                    properties.SetTexture(Id.Height, Height.rt);
                    properties.SetTexture(Id.Color, Color.rt);
                    properties.SetTexture(Id.Slope, Slope.rt);
                    properties.SetTexture(Id.Ground, Ground.rt);
                    // The canopy over a root follows its population's density (GrassPopulationDensity).
                    properties.SetTexture(Id.Density, Density.rt);
                    properties.SetInteger(Id.Authored, Authored ? 1 : 0);
                    properties.SetInteger(Id.DensityShortens, DensityShortens ? 1 : 0);
                    properties.SetTexture(Id.Wind, wind);
                    properties.SetVector(Id.HeightTexelSize, new Vector4(1f / Height.rt.width,
                        1f / Height.rt.height, Height.rt.width, Height.rt.height));
                    GrassTerrainSplat.Bind(properties, splatTerrain);
                }
            }

            public static bool Allocate(ref RTHandle handle, int size, GraphicsFormat format,
                string name, bool depth = false)
            {
                if (handle != null && (handle.rt == null || !handle.rt.IsCreated()))
                {
                    handle.Release();
                    handle = null;
                }
                if (!SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Render))
                    format = depth ? SystemInfo.GetGraphicsFormat(DefaultFormat.DepthStencil) :
                        (format == GraphicsFormat.R8_UNorm ? GraphicsFormat.R8G8B8A8_UNorm : GraphicsFormat.R32G32B32A32_SFloat);
                var descriptor = new RenderTextureDescriptor(size, size)
                {
                    graphicsFormat = depth ? GraphicsFormat.None : format,
                    depthStencilFormat = depth ? format : GraphicsFormat.None,
                    msaaSamples = 1,
                    volumeDepth = 1,
                    dimension = TextureDimension.Tex2D,
                    useMipMap = false,
                    autoGenerateMips = false,
                    sRGB = false
                };
                return RenderingUtils.ReAllocateHandleIfNeeded(ref handle, descriptor,
                    depth ? FilterMode.Point : FilterMode.Bilinear, TextureWrapMode.Clamp, name: name);
            }

            private void OnReadback(AsyncGPUReadbackRequest request)
            {
                ReadbackPending = false;
                if (Disposed || request.hasError || !ReadbackOwner || !ReadbackOwner.isActiveAndEnabled ||
                    ReadbackOwner != InfiniteGrassRenderer.Instance)
                    return;
                var counts = request.GetData<uint>();
                if (counts.Length < 4)
                    return;
                ReadbackOwner.VisibleGrassCount = Math.Min(counts[0], ReadbackCapacity0) +
                    Math.Min(counts[1], ReadbackCapacity1) + Math.Min(counts[2], ReadbackCapacity2);
                ReadbackOwner.OverflowGrassCount = counts[3];
            }

            public void PruneTextureWrappers()
            {
                staleTextures.Clear();
                foreach (KeyValuePair<Texture, TextureWrapper> pair in TextureWrappers)
                    if (!pair.Key || LastUsedTime - pair.Value.LastUsedTime > GroupIdleSeconds)
                        staleTextures.Add(pair.Key);
                for (int i = 0; i < staleTextures.Count; i++)
                {
                    TextureWrappers[staleTextures[i]].Handle.Release();
                    TextureWrappers.Remove(staleTextures[i]);
                }
            }

            public void Dispose()
            {
                if (Disposed)
                    return;
                Disposed = true;
                Height?.Release();
                HeightDepth?.Release();
                Density?.Release();
                Mask?.Release();
                Color?.Release();
                Slope?.Release();
                Ground?.Release();
                Positions?.Dispose();
                Counts?.Dispose();
                Arguments?.Dispose();
                foreach (TerrainGroup group in Groups.Values)
                    group.DensityMap?.Release();
                Groups.Clear();
                foreach (TextureWrapper wrapper in TextureWrappers.Values)
                    wrapper.Handle.Release();
                TextureWrappers.Clear();
                CaptureProperties.Clear();
                CaptureTextureIds.Clear();
                CaptureTextures.Clear();
                InteractionDraws.Clear();
                CoreUtils.Destroy(BladeMaterial);
            }
        }
    }
}
