using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

/// <summary>Persistent, single-view grass rendering for the URP RenderGraph path.</summary>
public partial class GrassDataRendererFeature : ScriptableRendererFeature
{
    // Keep these serialized names so existing renderer assets retain their references.
    [SerializeField] private LayerMask heightMapLayer;
    [SerializeField] private Material heightMapMat;
    [SerializeField] private ComputeShader computeShader;

    private GrassDataPass grassPass;

    public override void Create()
    {
        grassPass?.Dispose();
        grassPass = new GrassDataPass(heightMapLayer, heightMapMat, computeShader)
        {
            // Opaque depth and lighting are available; grass still precedes transparents.
            renderPassEvent = RenderPassEvent.BeforeRenderingTransparents
        };
        RenderPipelineManager.beginContextRendering -= OnBeginContextRendering;
        RenderPipelineManager.beginContextRendering += OnBeginContextRendering;
    }

    private void OnBeginContextRendering(ScriptableRenderContext context, List<Camera> cameras)
    {
        // URP skips AddRenderPasses on inactive features and renderers unused by
        // the current cameras. Their GPU caches still need a cleanup path.
        if (!isActive)
            grassPass?.ReleaseCameras();
        else
            grassPass?.PruneCameras();
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        grassPass?.PruneCameras();
        InfiniteGrassRenderer owner = InfiniteGrassRenderer.Instance;
        // Without blades only the interaction capture runs: it needs no blade material, compute or indirect draws.
        bool blades = owner && owner.drawBlades;
        if (!owner || !owner.IsReadyForRendering || blades && (!owner.grassMaterial || !computeShader ||
            !SystemInfo.supportsComputeShaders || !SystemInfo.supportsInstancing || !SystemInfo.supportsIndirectArgumentsBuffer))
        {
            grassPass?.ReleaseCameras();
            GrassInteractionPublisher.Withdraw();
            return;
        }
        if (grassPass == null || blades && !grassPass.TryGetForwardPass(owner, out _))
            return;

        Camera camera = renderingData.cameraData.camera;
        if (!(renderer is UniversalRenderer) || renderingData.cameraData.renderType != CameraRenderType.Base || !owner.AdmitCamera(camera))
        {
            grassPass?.ReleaseCamera(camera);
            return;
        }

        bool contacts = blades && GrassContactShadows.TryGetParameters(owner.contactShadows, out _, out _, out _);
        bool motion = blades && grassPass.WantsMotion(camera, renderingData.cameraData.postProcessEnabled,
            renderingData.cameraData.antialiasing, renderingData.cameraData.cameraTargetDescriptor.msaaSamples,
            owner.motionVectors, owner.grassMaterial);
        ScriptableRenderPassInput inputs = contacts || motion ? ScriptableRenderPassInput.Depth : ScriptableRenderPassInput.None;
        if (motion)
            inputs |= ScriptableRenderPassInput.Motion;
        // Explicit Depth schedules URP's depth copy and built-in motion producer
        // before this pass, so grass can augment the completed vector textures.
        grassPass.ConfigureInput(inputs);
        // The contact overlay multiplies the existing attachment without sampling camera color.
        grassPass.requiresIntermediateTexture = false;
        renderer.EnqueuePass(grassPass);
    }

    protected override void Dispose(bool disposing)
    {
        RenderPipelineManager.beginContextRendering -= OnBeginContextRendering;
        grassPass?.Dispose();
        grassPass = null;
        GrassInteractionPublisher.Withdraw();
    }

    private void OnDisable() => Dispose(true);

    private static class Id
    {
        public static readonly int CaptureVP = Shader.PropertyToID("_GrassCaptureVP");
        public static readonly int Positions = Shader.PropertyToID("_GrassPositions");
        public static readonly int Counts = Shader.PropertyToID("_GrassCounts");
        public static readonly int Arguments = Shader.PropertyToID("_GrassIndirectArgs");
        public static readonly int Height = Shader.PropertyToID("_GrassHeightMapRT");
        public static readonly int Density = Shader.PropertyToID("_GrassDensityRT");
        public static readonly int Mask = Shader.PropertyToID("_GrassMaskMapRT");
        public static readonly int Color = Shader.PropertyToID("_GrassColorRT");
        public static readonly int Slope = Shader.PropertyToID("_GrassSlopeRT");
        public static readonly int Ground = Shader.PropertyToID("_GrassGroundColorRT");
        public static readonly int Wind = Shader.PropertyToID("_WindTexture");
        public static readonly int HeightTexelSize = Shader.PropertyToID("_GrassHeightMapRT_TexelSize");
        public static readonly int Center = Shader.PropertyToID("_CenterPos");
        public static readonly int DrawDistance = Shader.PropertyToID("_DrawDistance");
        public static readonly int CapturePadding = Shader.PropertyToID("_TextureUpdateThreshold");
        public static readonly int Spacing = Shader.PropertyToID("_Spacing");
        public static readonly int FullDensity = Shader.PropertyToID("_FullDensityDistance");
        public static readonly int DensityExponent = Shader.PropertyToID("_DensityFalloffExponent");
        public static readonly int BladeSpacing = Shader.PropertyToID("_GrassSpacing");
        public static readonly int DensityTransition = Shader.PropertyToID("_DensityTransition");
        public static readonly int Authored = Shader.PropertyToID("_AuthoredAreas");
        public static readonly int DensityShortens = Shader.PropertyToID("_DensityShortensBlades");
        public static readonly int GridStart = Shader.PropertyToID("_GridStartIndex");
        public static readonly int GridSize = Shader.PropertyToID("_GridSize");
        public static readonly int CameraPosition = Shader.PropertyToID("_CameraPosition");
        public static readonly int Frustum = Shader.PropertyToID("_FrustumPlanes");
        public static readonly int BoundsRadius = Shader.PropertyToID("_BladeBoundsRadius");
        public static readonly int LodDistances = Shader.PropertyToID("_LodDistances");
        public static readonly int LodTransition = Shader.PropertyToID("_LodTransitionWidth");
        public static readonly int LodOffsets = Shader.PropertyToID("_LodOffsets");
        public static readonly int LodCapacities = Shader.PropertyToID("_LodCapacities");
        public static readonly int ArgsStride = Shader.PropertyToID("_ArgsStride");
        public static readonly int ArgsCountOffset = Shader.PropertyToID("_ArgsInstanceCountOffset");
        public static readonly int UseTerrain = Shader.PropertyToID("_UseTerrain");
        public static readonly int TerrainOrigin = Shader.PropertyToID("_TerrainOrigin");
        public static readonly int TerrainSize = Shader.PropertyToID("_TerrainSize");
        public static readonly int TerrainHeight = Shader.PropertyToID("_TerrainHeightmap");
        public static readonly int TerrainHoles = Shader.PropertyToID("_TerrainHoles");
        public static readonly int TerrainHasHoles = Shader.PropertyToID("_TerrainHasHoles");
        public static readonly int InstanceOffset = Shader.PropertyToID("_GrassInstanceOffset");
        public static readonly int MainLightCascades = Shader.PropertyToID("_GrassMainLightShadowCascades");
        public static readonly int ExplicitTime = Shader.PropertyToID("_GrassUseExplicitTime");
        public static readonly int SHAr = Shader.PropertyToID("_GrassSHAr");
        public static readonly int SHAg = Shader.PropertyToID("_GrassSHAg");
        public static readonly int SHAb = Shader.PropertyToID("_GrassSHAb");
        public static readonly int SHBr = Shader.PropertyToID("_GrassSHBr");
        public static readonly int SHBg = Shader.PropertyToID("_GrassSHBg");
        public static readonly int SHBb = Shader.PropertyToID("_GrassSHBb");
        public static readonly int SHC = Shader.PropertyToID("_GrassSHC");
        public static readonly int AlphaToCoverage = Shader.PropertyToID("_GrassAlphaToCoverage");
        public static readonly int GroundStrength = Shader.PropertyToID("_GroundBlendStrength");
        public static readonly int GroundHeight = Shader.PropertyToID("_GroundBlendHeight");
        public static readonly int MinimumWidth = Shader.PropertyToID("_MinimumPixelWidth");
        public static readonly int SpecularStart = Shader.PropertyToID("_SpecularFadeStart");
        public static readonly int SpecularEnd = Shader.PropertyToID("_SpecularFadeEnd");
        public static readonly int Subdivision = Shader.PropertyToID("_MaxSubdivision");
        public static readonly int SubdivisionDistance = Shader.PropertyToID("_SubdivisionDistance");
        public static readonly int SubdivisionHeightBoost = Shader.PropertyToID("_SubdivisionHeightBoost");
        public static readonly int SubdivisionBumpWidth = Shader.PropertyToID("_SubdivisionBumpWidth");

        public static readonly int PlacementMapping = Shader.PropertyToID("_PlacementWorldToMask");
        public static readonly int PlacementTerrain = Shader.PropertyToID("_PlacementTerrainRect");
        public static readonly int PlacementHasTerrain = Shader.PropertyToID("_PlacementHasTerrain");
        public static readonly int PlacementShape = Shader.PropertyToID("_PlacementShape");
        public static readonly int PlacementDensity = Shader.PropertyToID("_PlacementDensity");
        public static readonly int PlacementFalloff = Shader.PropertyToID("_PlacementEdgeFalloff");
        public static readonly int PlacementTexture = Shader.PropertyToID("_PlacementDensityTexture");
        public static readonly int PlacementGroundTexture = Shader.PropertyToID("_PlacementGroundColorTexture");
        public static readonly int PlacementGroundMapping = Shader.PropertyToID("_PlacementGroundWorldToMask");
        public static readonly int PlacementTerrainGroundColor = Shader.PropertyToID("_PlacementGroundColorUsesTerrainBounds");
        public static readonly int PlacementHasGround = Shader.PropertyToID("_PlacementHasGroundColor");
        public static readonly int PlacementTint = Shader.PropertyToID("_PlacementGroundTint");
        public static readonly int PlacementGroundStrength = Shader.PropertyToID("_PlacementGroundStrength");
        public static readonly int PlacementLayer = Shader.PropertyToID("_PlacementGroundLayerTexture");
        public static readonly int PlacementLayerSampler = Shader.PropertyToID("_PlacementGroundLayerSamplerTexture");
        public static readonly int PlacementLayerIsSampler = Shader.PropertyToID("_PlacementGroundLayerIsSampler");
        public static readonly int PlacementHasLayer = Shader.PropertyToID("_PlacementHasGroundLayer");
        public static readonly int PlacementLayerUV = Shader.PropertyToID("_PlacementGroundLayerUV");
        public static readonly int PlacementRemapMin = Shader.PropertyToID("_PlacementGroundRemapMin");
        public static readonly int PlacementRemapMax = Shader.PropertyToID("_PlacementGroundRemapMax");
        public static readonly int PlacementHeight = Shader.PropertyToID("_PlacementTerrainHeightmap");
        public static readonly int PlacementHeightTexelSize = Shader.PropertyToID("_PlacementTerrainHeightmap_TexelSize");
        public static readonly int PlacementHoles = Shader.PropertyToID("_PlacementTerrainHoles");
        public static readonly int PlacementHasHoles = Shader.PropertyToID("_PlacementTerrainHasHoles");
        public static readonly int PlacementOriginY = Shader.PropertyToID("_PlacementTerrainOriginY");
        public static readonly int PlacementHeightRange = Shader.PropertyToID("_PlacementTerrainHeight");
    }

    private sealed partial class GrassDataPass : ScriptableRenderPass, IDisposable
    {
        private const int TileCells = 256;
        private const int MaximumDispatchCells = GrassDispatchMath.MaximumDispatchCells;
        private const long MaximumCandidates = GrassDispatchMath.MaximumCandidates;
        private const long MaximumTileSearch = 16384;
        private const int MaximumRetainedInactiveGroups = 8;
        private const double CameraIdleSeconds = 10.0;
        private const double GroupIdleSeconds = 3.0;
        private const ulong VersionSeed = 14695981039346656037UL;
        private static readonly ShaderTagId LightMode = new ShaderTagId("LightMode");

        private readonly LayerMask surfaceLayer;
        private readonly Material heightMaterial;
        private readonly ComputeShader generationShader;
        private readonly Dictionary<Camera, CameraState> cameras = new Dictionary<Camera, CameraState>();
        private readonly List<Camera> staleCameras = new List<Camera>();
        private readonly List<Material> sharedMaterials = new List<Material>();
        private readonly GrassContactShadows contacts = new GrassContactShadows();
        private readonly GrassMotionVectors motion = new GrassMotionVectors();
        private readonly List<Renderer> modifierInventory = new List<Renderer>();
        private Renderer[] rendererInventory;
        private readonly List<Terrain> terrainInventory = new List<Terrain>();
        private InfiniteGrassRenderer inventoryOwner;
        private uint inventoryOwnerRevision;
        private uint inventoryRevision;
        private bool inventoryDirty = true;
        private readonly int resetKernel;
        private readonly int generateKernel;
        private readonly int finalizeKernel;
        private readonly int argumentStride;
        private readonly int argumentCountOffset;
        private readonly bool kernelsValid;
        private Material placementMaterial;
        private Mesh captureQuad;
        private bool missingPlacementWarning;
        private bool missingHeightWarning;
        private bool missingForwardWarning;
        private bool warnedBufferLimit;
        private bool disposed;

        public GrassDataPass(LayerMask layer, Material height, ComputeShader shader)
        {
            surfaceLayer = layer;
            heightMaterial = height;
            generationShader = shader;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            TerrainCallbacks.heightmapChanged += OnTerrainHeightChanged;
            TerrainCallbacks.textureChanged += OnTerrainTextureChanged;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.hierarchyChanged += InvalidateInventory;
#endif
            argumentStride = GraphicsBuffer.IndirectDrawIndexedArgs.size;
            try
            {
                argumentCountOffset = FindInstanceCountOffset();
                if (shader && shader.HasKernel("ResetCounts") && shader.HasKernel("CSMain") &&
                    shader.HasKernel("FinalizeArgs"))
                {
                    resetKernel = shader.FindKernel("ResetCounts");
                    generateKernel = shader.FindKernel("CSMain");
                    finalizeKernel = shader.FindKernel("FinalizeArgs");
                    kernelsValid = true;
                }
            }
            catch (Exception exception)
            {
                Debug.LogError("Grass indirect argument layout is unsupported: " + exception.Message);
            }
        }

        public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
        {
            // Each camera publishes its own capture below; one that returns before that must not read another camera's.
            GrassInteractionPublisher.Withdraw();
            InfiniteGrassRenderer owner = InfiniteGrassRenderer.Instance;
            if (disposed)
                return;
            bool blades = owner && owner.drawBlades;
            if (!owner || !owner.IsReadyForRendering || blades && !owner.grassMaterial)
            {
                ReleaseCameras();
                return;
            }
            int forwardPass = -1;
            if (blades && !TryGetForwardPass(owner, out forwardPass))
                return;
            if (blades && !kernelsValid)
            {
                ReleaseCameras();
                return;
            }

            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalResourceData resources = frameData.Get<UniversalResourceData>();
            Camera camera = cameraData.camera;
            if (cameraData.renderType != CameraRenderType.Base || !owner.AdmitCamera(camera) ||
                !resources.activeColorTexture.IsValid() || !resources.activeDepthTexture.IsValid())
            {
                ReleaseCamera(camera);
                return;
            }
            if (!blades)
            {
                RecordInteractionField(graph, camera, owner);
                return;
            }

            if (!ValidateBufferCapacity(owner, camera, SystemInfo.maxGraphicsBufferSize))
                return;
            PruneCameras();
            if (!cameras.TryGetValue(camera, out CameraState state))
            {
                state = new CameraState(camera);
                cameras.Add(camera, state);
            }
            state.LastUsedTime = Time.realtimeSinceStartupAsDouble;
            state.ImportedTextures.Clear();

            bool authored = owner.placementMode == GrassPlacementMode.AuthoredAreas;
            if (authored && !EnsurePlacementMaterial())
                return;

            Mesh[] meshes = owner.GetLodMeshes();
            if (meshes == null || meshes.Length != 3 || !meshes[0] || !meshes[1] || !meshes[2])
                return;

            bool ownerChanged = state.Owner != owner;
            bool allocationChanged = state.EnsureResources(owner, meshes, argumentStride);
            bool materialChanged = state.UpdateMaterial(owner,
                graph.GetRenderTargetInfo(resources.activeColorTexture).msaaSamples > 1);

            float spacing = Mathf.Max(0.001f, FiniteOr(owner.spacing, 0.1f));
            float distanceLimit = Mathf.Max(0.01f, FiniteOr(owner.drawDistance, 300f));
            float capturePadding = Mathf.Max(0.01f, FiniteOr(owner.textureUpdateThreshold, 10f));
            if (ownerChanged || materialChanged || state.MotionSpacing != spacing)
                motion.ResetHistory(camera);
            state.MotionSpacing = spacing;
            Vector3 cameraPosition = camera.transform.position;
            if (!IsFinite(cameraPosition))
                return;
            Vector2 center = new Vector2(Mathf.Floor(cameraPosition.x / capturePadding) * capturePadding,
                Mathf.Floor(cameraPosition.z / capturePadding) * capturePadding);
            float extent = distanceLimit + capturePadding;
            if (!IsFinite(new Vector3(center.x, extent * 2f, center.y)))
                return;
            Bounds captureBounds = new Bounds(new Vector3(center.x, 0f, center.y),
                new Vector3(2f * extent, 0f, 2f * extent));

            // Capture the complete window, not just this frame's view. Rotating a
            // stationary camera must not reveal regions missing from cached maps.
            EnsureInventory(owner);
            state.CaptureRenderers = rendererInventory;
            state.ModifierRenderers = modifierInventory;
            CollectSources(state, captureBounds, authored);
            CollectSurfaceTerrains(state, captureBounds, authored);
            Vector2 captureRange = ResolveCaptureHeightRange(owner.captureHeightRange);
            bool captureMappingChanged = allocationChanged || !state.CacheValid ||
                state.Owner != owner || state.Center != center ||
                state.CaptureExtent != extent || state.CaptureRange != captureRange ||
                state.Authored != authored;
            bool inventoryChanged = state.InventoryRevision != inventoryRevision;
            bool refreshRequested = state.OwnerRevision != owner.Revision;
            bool groundDirty = captureMappingChanged || refreshRequested || state.GroundVersion != state.NextGroundVersion;

            float minHeight = captureRange.x;
            float maxHeight = captureRange.y;
            for (int i = 0; i < state.SurfaceTerrains.Count; i++)
            {
                Terrain terrain = state.SurfaceTerrains[i];
                if (!terrain || !terrain.terrainData)
                    continue;
                Vector3 origin = terrain.transform.position;
                Vector3 size = terrain.terrainData.size;
                if (!IsFinite(origin) || !IsFinite(size))
                    continue;
                minHeight = Mathf.Min(minHeight, origin.y);
                maxHeight = Mathf.Max(maxHeight, origin.y + size.y);
            }
            if (!IsFinite(new Vector3(minHeight, maxHeight, maxHeight - minHeight + 2f)))
                return;
            captureBounds.SetMinMax(new Vector3(center.x - extent, minHeight, center.y - extent),
                new Vector3(center.x + extent, Mathf.Max(minHeight + 1f, maxHeight), center.y + extent));
            Matrix4x4 captureVP = MakeCaptureMatrix(center, extent, captureBounds.min.y, captureBounds.max.y);

            TextureHandle height = graph.ImportTexture(state.Height);
            TextureHandle heightDepth = graph.ImportTexture(state.HeightDepth);
            TextureHandle density = graph.ImportTexture(state.Density);
            TextureHandle mask = graph.ImportTexture(state.Mask);
            TextureHandle color = graph.ImportTexture(state.Color);
            TextureHandle slope = graph.ImportTexture(state.Slope);
            TextureHandle ground = graph.ImportTexture(state.Ground);
            BufferHandle positions = graph.ImportBuffer(state.Positions);
            BufferHandle counts = graph.ImportBuffer(state.Counts);
            BufferHandle arguments = graph.ImportBuffer(state.Arguments);

            // Density belongs to its supporting surface. A shared union texture
            // can otherwise leak one Terrain's coverage into another dispatch.
            for (int i = 0; i < state.ActiveGroups.Count; i++)
            {
                TerrainGroup group = state.ActiveGroups[i];
                if (i == 0)
                {
                    if (state.PrimaryDensityGroup != group)
                        group.DensityCaptured = false;
                    if (!group.UsesPrimaryDensity)
                    {
                        group.DensityMap?.Release();
                        group.DensityMap = null;
                        group.DensityCaptured = false;
                    }
                    group.UsesPrimaryDensity = true;
                    group.DensityTexture = density;
                }
                else
                {
                    bool reallocated = CameraState.Allocate(ref group.DensityMap, state.Density.rt.width,
                        GraphicsFormat.R8_UNorm, "Grass Surface Density");
                    if (group.UsesPrimaryDensity || reallocated)
                        group.DensityCaptured = false;
                    group.UsesPrimaryDensity = false;
                    group.DensityTexture = graph.ImportTexture(group.DensityMap);
                }
                group.DensityDirty = captureMappingChanged || refreshRequested || !group.DensityCaptured ||
                    group.CapturedDensityVersion != group.DensityVersion;
            }
            state.PrimaryDensityGroup = state.ActiveGroups.Count > 0 ? state.ActiveGroups[0] : null;
            bool recaptureHeight = captureMappingChanged || inventoryChanged || state.SurfaceDirty ||
                state.SurfaceVersion != state.NextSurfaceVersion || !owner.cacheSurfaceData;
            bool recaptureModifiers = captureMappingChanged || inventoryChanged || owner.updateModifiersEveryFrame;
            CollectInteractionDraws(state, captureBounds);
            bool recaptureInteraction = recaptureModifiers || state.InteractionVersion != state.NextInteractionVersion ||
                state.OwnerInteractionRevision != owner.InteractionRevision;

            if (recaptureHeight)
            {
                CollectRendererDraws(state, captureBounds, true, false, authored);
                BuildHeightCapture(graph, state, height, heightDepth, captureVP);
            }
            BuildPlacementCapture(graph, state, density, ground, captureVP, authored, groundDirty,
                captureMappingChanged || state.HadDensitySources != (state.ActiveGroups.Count > 0));
            if (recaptureModifiers)
            {
                CollectRendererDraws(state, captureBounds, false, true, authored);
                BuildRendererCapture(graph, state, "Grass Exclusion", state.MaskDraws, mask, default, captureVP, Color.clear);
                BuildRendererCapture(graph, state, "Grass Color Modifiers", state.ColorDraws, color, default, captureVP, Color.clear);
            }
            if (recaptureInteraction)
            {
                if (!recaptureModifiers)
                    CollectRendererDraws(state, captureBounds, false, true, authored);
                BuildRendererCapture(graph, state, "Grass Interaction", state.SlopeDraws, slope, default,
                    captureVP, Color.clear, state.InteractionDraws);
            }

            state.CacheValid = true;
            state.Owner = owner;
            state.Center = center;
            state.CaptureExtent = extent;
            GrassInteractionPublisher.Publish(state.Slope.rt, center, extent);
            state.CaptureRange = captureRange;
            state.Authored = authored;
            state.DensityShortens = authored && owner.densityShortensBlades;
            state.OwnerRevision = owner.Revision;
            state.InventoryRevision = inventoryRevision;
            state.InteractionVersion = state.NextInteractionVersion;
            state.OwnerInteractionRevision = owner.InteractionRevision;
            state.SurfaceVersion = state.NextSurfaceVersion;
            state.GroundVersion = state.NextGroundVersion;
            state.SurfaceDirty = false;
            state.HadDensitySources = state.ActiveGroups.Count > 0;

            // Match the target-size input URP uses for _ScaledScreenParams.
            float scaledHeight = Mathf.Max(1, cameraData.cameraTargetDescriptor.height);
            if (camera.allowDynamicResolution)
                scaledHeight *= Mathf.Clamp(ScalableBufferManager.heightScaleFactor, 0.01f, 1f);
            float bladeRadius = CalculateBladeRadius(state.BladeMaterial, owner, camera, scaledHeight);
            Bounds cameraBounds = CalculateCameraBounds(camera, distanceLimit, bladeRadius);
            owner.cameraBounds = cameraBounds;
            GeometryUtility.CalculateFrustumPlanes(camera, state.Planes);
            for (int i = 0; i < 6; i++)
                state.Frustum[i] = new Vector4(state.Planes[i].normal.x, state.Planes[i].normal.y,
                    state.Planes[i].normal.z, state.Planes[i].distance);

            BuildDispatches(graph, state, owner, cameraBounds, spacing, authored, density);
            BuildGeneration(graph, state, owner, height, density, mask, positions, counts, arguments,
                center, cameraPosition, spacing, distanceLimit, capturePadding, bladeRadius, authored);
            if (state.DispatchCount == 0)
            {
                // Counts and indirect instance counts still reset to zero, but
                // there is no grass to draw, snapshot, or ray-march this frame.
                motion.Release(camera);
                BuildReadback(graph, state, counts, owner);
                state.PruneTextureWrappers();
                return;
            }

            Texture windTexture = ResolveWindTexture(state.BladeMaterial);
            TextureHandle wind = ImportTexture(graph, state, windTexture);
            state.SetDrawProperties(center, distanceLimit, capturePadding, windTexture,
                frameData.Get<UniversalShadowData>().mainLightShadowCascadesCount, SplatTerrain(state));
            state.VertexTextures[0] = height;
            state.VertexTextures[1] = color;
            state.VertexTextures[2] = slope;
            state.VertexTextures[3] = ground;
            state.VertexTextures[4] = wind;
            state.VertexTextures[5] = density;

            using (IRasterRenderGraphBuilder builder = graph.AddRasterRenderPass<DrawPass>(
                       "Grass Indirect LODs", out DrawPass pass))
            {
                pass.Arguments = state.Arguments;
                pass.Meshes = meshes;
                pass.Material = state.BladeMaterial;
                pass.Properties = state.DrawProperties;
                pass.ShaderPass = forwardPass;
                pass.ArgumentStride = argumentStride;
                builder.UseBuffer(positions, AccessFlags.Read);
                builder.UseBuffer(arguments, AccessFlags.Read);
                for (int i = 0; i < state.VertexTextures.Length; i++)
                    builder.UseTexture(state.VertexTextures[i], AccessFlags.Read);
                // The package blade shader samples these atlases at blade world
                // positions. Avoid extending unrelated global texture lifetimes.
                if (resources.mainShadowsTexture.IsValid())
                    builder.UseTexture(resources.mainShadowsTexture, AccessFlags.Read);
                if (resources.additionalShadowsTexture.IsValid())
                    builder.UseTexture(resources.additionalShadowsTexture, AccessFlags.Read);
                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.ReadWrite);
                builder.SetRenderFunc(static (DrawPass data, RasterGraphContext context) =>
                {
                    for (int lod = 0; lod < 3; lod++)
                        context.cmd.DrawMeshInstancedIndirect(data.Meshes[lod], 0, data.Material,
                            data.ShaderPass, data.Arguments, lod * data.ArgumentStride, data.Properties[lod]);
                });
            }

            if (WantsMotion(camera, cameraData.postProcessEnabled, cameraData.antialiasing,
                    cameraData.cameraTargetDescriptor.msaaSamples, owner.motionVectors, state.BladeMaterial))
                motion.Record(graph, frameData, positions, counts, arguments, state.Positions,
                    state.Counts, state.Arguments, meshes, state.BladeMaterial, state.DrawProperties,
                    state.VertexTextures, state.LodOffsets, state.LodCapacities, spacing);
            contacts.Record(graph, frameData, positions, arguments, state.Positions,
                state.Arguments, meshes, state.BladeMaterial, state.DrawProperties, owner.contactShadows,
                state.VertexTextures);
            BuildReadback(graph, state, counts, owner);
            state.PruneTextureWrappers();
        }

        public void PruneCameras()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            staleCameras.Clear();
            foreach (KeyValuePair<Camera, CameraState> pair in cameras)
                if (!pair.Key || now - pair.Value.LastUsedTime > CameraIdleSeconds)
                    staleCameras.Add(pair.Key);
            for (int i = 0; i < staleCameras.Count; i++)
                ReleaseCamera(staleCameras[i]);
        }

        public void ReleaseCamera(Camera camera)
        {
            motion.Release(camera);
            if (ReferenceEquals(camera, null) || !cameras.TryGetValue(camera, out CameraState state))
                return;
            state.Dispose();
            cameras.Remove(camera);
        }

        public void ReleaseCameras()
        {
            foreach (KeyValuePair<Camera, CameraState> pair in cameras)
            {
                motion.Release(pair.Key);
                pair.Value.Dispose();
            }
            cameras.Clear();
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            TerrainCallbacks.heightmapChanged -= OnTerrainHeightChanged;
            TerrainCallbacks.textureChanged -= OnTerrainTextureChanged;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.hierarchyChanged -= InvalidateInventory;
#endif
            ReleaseCameras();
            motion.Dispose();
            contacts.Dispose();
            CoreUtils.Destroy(placementMaterial);
            CoreUtils.Destroy(captureQuad);
            placementMaterial = null;
            captureQuad = null;
        }
    }
}
