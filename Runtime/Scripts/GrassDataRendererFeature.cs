using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>Persistent, single-view grass rendering for the URP RenderGraph path.</summary>
public class GrassDataRendererFeature : ScriptableRendererFeature
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
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        InfiniteGrassRenderer owner = InfiniteGrassRenderer.Instance;
        if (!owner || !owner.isActiveAndEnabled)
        {
            grassPass?.ReleaseCameras();
            return;
        }

        Camera camera = renderingData.cameraData.camera;
        if (grassPass == null || !owner.grassMaterial || !computeShader ||
            !SystemInfo.supportsComputeShaders || !SystemInfo.supportsInstancing ||
            camera == null || camera.stereoEnabled ||
            renderingData.cameraData.renderType != CameraRenderType.Base ||
            (camera.cameraType != CameraType.Game && camera.cameraType != CameraType.SceneView) ||
            (camera.cameraType == CameraType.SceneView && !owner.renderInSceneView))
            return;

        bool contacts = owner.contactShadows != null && owner.contactShadows.enabled &&
            owner.contactShadows.strength > 0f;
        grassPass.ConfigureInput(contacts ? ScriptableRenderPassInput.Depth : ScriptableRenderPassInput.None);
        // The contact overlay multiplies the existing attachment without sampling camera color.
        grassPass.requiresIntermediateTexture = false;
        renderer.EnqueuePass(grassPass);
    }

    protected override void Dispose(bool disposing)
    {
        grassPass?.Dispose();
        grassPass = null;
        GrassContactShadows.Cleanup();
    }

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
        public static readonly int MainTexture = Shader.PropertyToID("_MainTex");
        public static readonly int HeightTexelSize = Shader.PropertyToID("_GrassHeightMapRT_TexelSize");
        public static readonly int Center = Shader.PropertyToID("_CenterPos");
        public static readonly int DrawDistance = Shader.PropertyToID("_DrawDistance");
        public static readonly int CapturePadding = Shader.PropertyToID("_TextureUpdateThreshold");
        public static readonly int Spacing = Shader.PropertyToID("_Spacing");
        public static readonly int FullDensity = Shader.PropertyToID("_FullDensityDistance");
        public static readonly int DensityExponent = Shader.PropertyToID("_DensityFalloffExponent");
        public static readonly int DensityTransition = Shader.PropertyToID("_DensityTransition");
        public static readonly int Authored = Shader.PropertyToID("_AuthoredAreas");
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
        public static readonly int PlacementHasGround = Shader.PropertyToID("_PlacementHasGroundColor");
        public static readonly int PlacementTint = Shader.PropertyToID("_PlacementGroundTint");
        public static readonly int PlacementGroundStrength = Shader.PropertyToID("_PlacementGroundStrength");
        public static readonly int PlacementLayer = Shader.PropertyToID("_PlacementGroundLayerTexture");
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

    private sealed class GrassDataPass : ScriptableRenderPass, IDisposable
    {
        private const int TileCells = 256;
        private const int MaximumDispatchCells = 65535 * 8;
        private const long MaximumCandidates = 64L * 1024 * 1024;
        private const long MaximumTileSearch = 16384;
        private static readonly ShaderTagId LightMode = new ShaderTagId("LightMode");

        private readonly LayerMask surfaceLayer;
        private readonly Material heightMaterial;
        private readonly ComputeShader generationShader;
        private readonly Dictionary<Camera, CameraState> cameras = new Dictionary<Camera, CameraState>();
        private readonly List<Camera> staleCameras = new List<Camera>();
        private readonly List<Material> sharedMaterials = new List<Material>();
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

        public GrassDataPass(LayerMask layer, Material height, ComputeShader shader)
        {
            surfaceLayer = layer;
            heightMaterial = height;
            generationShader = shader;
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
            InfiniteGrassRenderer owner = InfiniteGrassRenderer.Instance;
            if (!owner || !owner.isActiveAndEnabled || !owner.grassMaterial || !kernelsValid)
                return;

            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalResourceData resources = frameData.Get<UniversalResourceData>();
            Camera camera = cameraData.camera;
            if (!camera || camera.stereoEnabled || cameraData.renderType != CameraRenderType.Base ||
                !resources.activeColorTexture.IsValid() || !resources.activeDepthTexture.IsValid())
                return;

            PruneCameras();
            if (!cameras.TryGetValue(camera, out CameraState state))
            {
                state = new CameraState(camera);
                cameras.Add(camera, state);
            }
            state.LastFrame = Time.frameCount;
            state.ImportedTextures.Clear();
            state.FrameTextureDependencies.Clear();

            bool authored = owner.placementMode == GrassPlacementMode.AuthoredAreas;
            if (authored && !EnsurePlacementMaterial())
                return;

            Mesh[] meshes = owner.GetLodMeshes();
            if (meshes == null || meshes.Length != 3 || !meshes[0] || !meshes[1] || !meshes[2])
                return;

            bool allocationChanged = state.EnsureResources(owner, meshes, argumentStride);
            state.UpdateMaterial(owner,
                graph.GetTextureDesc(resources.activeColorTexture).msaaSamples != MSAASamples.None);
            int forwardPass = state.BladeMaterial.FindPass("GrassForward");
            if (forwardPass < 0)
            {
                if (!state.WarnedMaterial)
                {
                    Debug.LogWarning("The grass material needs the GrassForward pass from the package blade shader.", owner);
                    state.WarnedMaterial = true;
                }
                return;
            }

            float spacing = Mathf.Max(0.001f, owner.spacing);
            float distanceLimit = Mathf.Max(0.01f, owner.drawDistance);
            float capturePadding = Mathf.Max(0.01f, owner.textureUpdateThreshold);
            Vector3 cameraPosition = camera.transform.position;
            Vector2 center = new Vector2(Mathf.Floor(cameraPosition.x / capturePadding) * capturePadding,
                Mathf.Floor(cameraPosition.z / capturePadding) * capturePadding);
            float extent = distanceLimit + capturePadding;
            Bounds captureBounds = new Bounds(new Vector3(center.x, 0f, center.y),
                new Vector3(2f * extent, 0f, 2f * extent));

            // Capture the complete window, not just this frame's view. Rotating a
            // stationary camera must not reveal regions missing from cached maps.
            CollectSources(state, captureBounds);
            bool staticDirty = allocationChanged || !state.CacheValid ||
                state.Owner != owner || state.Center != center ||
                state.CaptureExtent != extent || state.CaptureRange != owner.captureHeightRange ||
                state.Authored != authored || state.OwnerRevision != owner.Revision ||
                state.PlacementRevision != GrassPlacementArea.Revision;
            if (staticDirty || state.CaptureRenderers == null)
            {
                // Runtime source creation/material replacement can explicitly call
                // InfiniteGrassRenderer.RefreshGrassData to refresh this inventory.
                state.CaptureRenderers = UnityEngine.Object.FindObjectsByType<Renderer>(
                    FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                RebuildModifierInventory(state);
            }

            float minHeight = Mathf.Min(owner.captureHeightRange.x, owner.captureHeightRange.y);
            float maxHeight = Mathf.Max(owner.captureHeightRange.x, owner.captureHeightRange.y);
            for (int i = 0; i < state.ActiveGroups.Count; i++)
            {
                Terrain terrain = state.ActiveGroups[i].Terrain;
                if (!terrain || !terrain.terrainData)
                    continue;
                minHeight = Mathf.Min(minHeight, terrain.transform.position.y);
                maxHeight = Mathf.Max(maxHeight, terrain.transform.position.y + terrain.terrainData.size.y);
            }
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
                    group.DensityMap?.Release();
                    group.DensityMap = null;
                    group.DensityTexture = density;
                }
                else
                {
                    staticDirty |= CameraState.Allocate(ref group.DensityMap, state.Density.rt.width,
                        GraphicsFormat.R8_UNorm, "Grass Surface Density");
                    group.DensityTexture = graph.ImportTexture(group.DensityMap);
                }
            }
            bool recaptureHeight = staticDirty || !owner.cacheSurfaceData;
            bool recaptureModifiers = staticDirty || owner.updateModifiersEveryFrame;

            if (recaptureHeight)
            {
                CollectRendererDraws(state, captureBounds, true, false, authored);
                BuildHeightCapture(graph, state, height, heightDepth, captureVP);
            }
            if (staticDirty)
                BuildPlacementCapture(graph, state, density, ground, captureVP, authored);
            if (recaptureModifiers)
            {
                CollectRendererDraws(state, captureBounds, false, true, authored);
                BuildRendererCapture(graph, state, "Grass Exclusion", state.MaskDraws, mask, default, captureVP, Color.clear);
                BuildRendererCapture(graph, state, "Grass Color Modifiers", state.ColorDraws, color, default, captureVP, Color.clear);
                BuildRendererCapture(graph, state, "Grass Interaction", state.SlopeDraws, slope, default, captureVP, Color.clear);
            }

            state.CacheValid = true;
            state.Owner = owner;
            state.Center = center;
            state.CaptureExtent = extent;
            state.CaptureRange = owner.captureHeightRange;
            state.Authored = authored;
            state.OwnerRevision = owner.Revision;
            state.PlacementRevision = GrassPlacementArea.Revision;

            float bladeRadius = CalculateBladeRadius(state.BladeMaterial, owner, camera);
            Bounds cameraBounds = CalculateCameraBounds(camera, distanceLimit, bladeRadius);
            owner.cameraBounds = cameraBounds;
            GeometryUtility.CalculateFrustumPlanes(camera, state.Planes);
            for (int i = 0; i < 6; i++)
                state.Frustum[i] = new Vector4(state.Planes[i].normal.x, state.Planes[i].normal.y,
                    state.Planes[i].normal.z, state.Planes[i].distance);

            BuildDispatches(graph, state, owner, cameraBounds, spacing, authored, density);
            BuildGeneration(graph, state, owner, height, density, mask, positions, counts, arguments,
                center, cameraPosition, spacing, distanceLimit, capturePadding, bladeRadius, authored);

            Texture windTexture = state.BladeMaterial.GetTexture(Id.Wind);
            if (!windTexture || windTexture.dimension != TextureDimension.Tex2D)
                windTexture = Texture2D.grayTexture;
            TextureHandle wind = ImportTexture(graph, state, windTexture);
            state.SetDrawProperties(center, distanceLimit, capturePadding, windTexture,
                frameData.Get<UniversalShadowData>().mainLightShadowCascadesCount);
            state.VertexTextures[0] = height;
            state.VertexTextures[1] = color;
            state.VertexTextures[2] = slope;
            state.VertexTextures[3] = ground;
            state.VertexTextures[4] = wind;

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

            GrassContactShadows.Record(graph, frameData, positions, arguments, state.Positions,
                state.Arguments, meshes, state.BladeMaterial, state.DrawProperties, owner.contactShadows,
                state.VertexTextures);
            BuildReadback(graph, state, counts, owner);
            state.PruneTextureWrappers();
        }

        private void CollectSources(CameraState state, Bounds captureBounds)
        {
            state.Sources.Clear();
            state.ActiveGroups.Clear();
            state.ExplicitSurfaces.Clear();
            state.HasMeshSurfaceFallback = false;
            foreach (TerrainGroup group in state.Groups.Values)
                group.Sources.Clear();

            IReadOnlyList<GrassPlacementArea> areas = GrassPlacementArea.ActiveAreas;
            for (int i = 0; i < areas.Count; i++)
            {
                GrassPlacementArea area = areas[i];
                if (!area || !area.TryGetCaptureData(out GrassPlacementDrawData data) ||
                    !IntersectsXZ(data.WorldBounds, captureBounds))
                    continue;
                var source = new PlacementSource { Area = area, Data = data };
                state.Sources.Add(source);
                if (!data.Terrain)
                {
                    Renderer supportingRenderer = area.PaintSurface ? area.PaintSurface.GetComponent<Renderer>() : null;
                    if (!supportingRenderer && area.PaintSurface)
                        supportingRenderer = area.PaintSurface.GetComponentInParent<Renderer>();
                    if (supportingRenderer)
                        state.ExplicitSurfaces.Add(supportingRenderer);
                    else
                        state.HasMeshSurfaceFallback = true;
                }
                int key = data.Terrain ? data.Terrain.GetInstanceID() : 0;
                if (!state.Groups.TryGetValue(key, out TerrainGroup group))
                {
                    group = new TerrainGroup();
                    state.Groups.Add(key, group);
                }
                if (group.Sources.Count == 0)
                {
                    group.Terrain = data.Terrain;
                    state.ActiveGroups.Add(group);
                }
                group.Sources.Add(source);
            }
            state.StaleGroupKeys.Clear();
            foreach (KeyValuePair<int, TerrainGroup> pair in state.Groups)
                if (pair.Value.Sources.Count == 0)
                    state.StaleGroupKeys.Add(pair.Key);
            for (int i = 0; i < state.StaleGroupKeys.Count; i++)
            {
                TerrainGroup inactive = state.Groups[state.StaleGroupKeys[i]];
                inactive.DensityMap?.Release();
                state.Groups.Remove(state.StaleGroupKeys[i]);
            }
        }

        private void CollectRendererDraws(CameraState state, Bounds captureBounds, bool heights, bool modifiers, bool authored)
        {
            if (heights) state.HeightDraws.Clear();
            if (modifiers)
            {
                state.MaskDraws.Clear();
                state.ColorDraws.Clear();
                state.SlopeDraws.Clear();
            }
            IReadOnlyList<Renderer> renderers = heights ?
                (IReadOnlyList<Renderer>)state.CaptureRenderers : state.ModifierRenderers;
            if (renderers == null)
                return;
            for (int i = 0; i < renderers.Count; i++)
            {
                Renderer renderer = renderers[i];
                if (!renderer || !renderer.enabled || renderer.forceRenderingOff ||
                    !renderer.gameObject.activeInHierarchy || !renderer.gameObject.scene.IsValid() ||
                    !renderer.gameObject.scene.isLoaded || !IntersectsXZ(renderer.bounds, captureBounds))
                    continue;
#if UNITY_EDITOR
                if (UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(renderer.gameObject.scene))
                    continue;
#endif
                sharedMaterials.Clear();
                renderer.GetSharedMaterials(sharedMaterials);
                for (int submesh = 0; submesh < sharedMaterials.Count; submesh++)
                {
                    Material sourceMaterial = sharedMaterials[submesh];
                    bool selectedSurface = state.ExplicitSurfaces.Contains(renderer) ||
                        ((!authored || state.HasMeshSurfaceFallback) &&
                            (surfaceLayer.value & (1 << renderer.gameObject.layer)) != 0);
                    if (heights && heightMaterial && selectedSurface)
                        state.HeightDraws.Add(new RendererDraw(renderer, heightMaterial, submesh, 0));
                    if (!modifiers || !sourceMaterial)
                        continue;
                    AddModifierDraw(state.MaskDraws, renderer, sourceMaterial, submesh, "GrassMask");
                    AddModifierDraw(state.ColorDraws, renderer, sourceMaterial, submesh, "GrassColor");
                    AddModifierDraw(state.SlopeDraws, renderer, sourceMaterial, submesh, "GrassSlope");
                }
            }
        }

        private static void AddModifierDraw(List<RendererDraw> draws, Renderer renderer,
            Material material, int submesh, string lightMode)
        {
            int pass = FindModifierPass(material, lightMode);
            if (pass >= 0)
                draws.Add(new RendererDraw(renderer, material, submesh, pass));
        }

        private void RebuildModifierInventory(CameraState state)
        {
            state.ModifierRenderers.Clear();
            for (int i = 0; i < state.CaptureRenderers.Length; i++)
            {
                Renderer renderer = state.CaptureRenderers[i];
                if (!renderer)
                    continue;
                sharedMaterials.Clear();
                renderer.GetSharedMaterials(sharedMaterials);
                for (int m = 0; m < sharedMaterials.Count; m++)
                {
                    Material material = sharedMaterials[m];
                    if (!material || (FindModifierPass(material, "GrassMask") < 0 &&
                        FindModifierPass(material, "GrassColor") < 0 && FindModifierPass(material, "GrassSlope") < 0))
                        continue;
                    state.ModifierRenderers.Add(renderer);
                    break;
                }
            }
        }

        private static int FindModifierPass(Material material, string lightMode)
        {
            int pass = material.FindPass(lightMode);
            if (pass < 0)
            {
                for (int i = 0; i < material.passCount; i++)
                {
                    if (material.shader.FindPassTagValue(i, LightMode).name != lightMode)
                        continue;
                    pass = i;
                    break;
                }
            }
            return pass;
        }

        private void BuildHeightCapture(RenderGraph graph, CameraState state, TextureHandle height,
            TextureHandle depth, Matrix4x4 captureVP)
        {
            if (!heightMaterial && !missingHeightWarning)
            {
                Debug.LogWarning("Grass mesh surfaces require the height capture material. Explicit Terrain areas still use TerrainData.");
                missingHeightWarning = true;
            }
            BuildRendererCapture(graph, state, "Grass Surface Height", state.HeightDraws, height, depth, captureVP, Color.clear);
            if (!EnsurePlacementMaterial())
                return;
            int terrainPass = placementMaterial.FindPass("TerrainHeight");
            if (terrainPass < 0)
                return;

            using (IRasterRenderGraphBuilder builder = graph.AddRasterRenderPass<TerrainCapturePass>(
                       "Grass Terrain Surface Height", out TerrainCapturePass pass))
            {
                pass.Groups = state.ActiveGroups;
                pass.Quad = captureQuad;
                pass.Material = placementMaterial;
                pass.PassIndex = terrainPass;
                pass.CaptureVP = captureVP;
                for (int i = 0; i < pass.Groups.Count; i++)
                {
                    TerrainGroup group = pass.Groups[i];
                    if (!group.Terrain || !group.Terrain.terrainData)
                        continue;
                    TerrainData data = group.Terrain.terrainData;
                    group.HeightTexture = ImportTexture(graph, state, data.heightmapTexture);
                    group.HolesTexture = ImportTexture(graph, state, data.holesTexture ? data.holesTexture : Texture2D.whiteTexture);
                    builder.UseTexture(group.HeightTexture, AccessFlags.Read);
                    builder.UseTexture(group.HolesTexture, AccessFlags.Read);
                }
                builder.SetRenderAttachment(height, 0, AccessFlags.ReadWrite);
                builder.SetRenderAttachmentDepth(depth, AccessFlags.ReadWrite);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (TerrainCapturePass data, RasterGraphContext context) =>
                {
                    context.cmd.SetGlobalMatrix(Id.CaptureVP, data.CaptureVP);
                    for (int i = 0; i < data.Groups.Count; i++)
                    {
                        Terrain terrain = data.Groups[i].Terrain;
                        if (!terrain || !terrain.terrainData)
                            continue;
                        TerrainData terrainData = terrain.terrainData;
                        Vector3 origin = terrain.transform.position;
                        Vector3 size = terrainData.size;
                        MaterialPropertyBlock properties = context.renderGraphPool.GetTempMaterialPropertyBlock();
                        properties.SetVector(Id.PlacementTerrain, new Vector4(origin.x, origin.z, size.x, size.z));
                        properties.SetInteger(Id.PlacementHasTerrain, 1);
                        properties.SetTexture(Id.PlacementHeight, data.Groups[i].HeightTexture);
                        Texture heightmap = terrainData.heightmapTexture;
                        properties.SetVector(Id.PlacementHeightTexelSize, new Vector4(1f / heightmap.width,
                            1f / heightmap.height, heightmap.width, heightmap.height));
                        properties.SetTexture(Id.PlacementHoles, data.Groups[i].HolesTexture);
                        properties.SetInteger(Id.PlacementHasHoles, terrainData.holesTexture ? 1 : 0);
                        properties.SetFloat(Id.PlacementOriginY, origin.y);
                        properties.SetFloat(Id.PlacementHeightRange, size.y);
                        Matrix4x4 matrix = Matrix4x4.TRS(origin + new Vector3(size.x * 0.5f, 0f, size.z * 0.5f),
                            Quaternion.identity, new Vector3(size.x, 1f, size.z));
                        context.cmd.DrawMesh(data.Quad, matrix, data.Material, 0, data.PassIndex, properties);
                    }
                });
            }
        }

        private static void BuildRendererCapture(RenderGraph graph, CameraState state, string name, List<RendererDraw> draws,
            TextureHandle target, TextureHandle depth, Matrix4x4 captureVP, Color clear)
        {
            using (IRasterRenderGraphBuilder builder = graph.AddRasterRenderPass<RendererCapturePass>(
                       name, out RendererCapturePass pass))
            {
                pass.Draws = draws;
                pass.CaptureVP = captureVP;
                pass.HasDepth = depth.IsValid();
                pass.Clear = clear;
                for (int i = 0; i < draws.Count; i++)
                {
                    Material material = draws[i].Material;
                    Texture input = material && material.HasProperty(Id.MainTexture) ? material.GetTexture(Id.MainTexture) : null;
                    if (input)
                        builder.UseTexture(ImportTexture(graph, state, input), AccessFlags.Read);
                }
                builder.SetRenderAttachment(target, 0, AccessFlags.Write);
                if (pass.HasDepth)
                    builder.SetRenderAttachmentDepth(depth, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (RendererCapturePass data, RasterGraphContext context) =>
                {
                    context.cmd.SetGlobalMatrix(Id.CaptureVP, data.CaptureVP);
                    // CommandBuffer clears use Unity's far-depth convention;
                    // CoreUtils also supplies 1 even on reversed-Z backends.
                    context.cmd.ClearRenderTarget(data.HasDepth, true, data.Clear, 1f);
                    for (int i = 0; i < data.Draws.Count; i++)
                    {
                        RendererDraw draw = data.Draws[i];
                        if (draw.Renderer && draw.Material)
                            context.cmd.DrawRenderer(draw.Renderer, draw.Material, draw.Submesh, draw.Pass);
                    }
                });
            }
        }

        private void BuildPlacementCapture(RenderGraph graph, CameraState state, TextureHandle density,
            TextureHandle ground, Matrix4x4 captureVP, bool authored)
        {
            state.FrameTextureDependencies.Clear();
            for (int i = 0; i < state.Sources.Count; i++)
            {
                GrassPlacementDrawData source = state.Sources[i].Data;
                AddTextureDependency(graph, state, source.DensityTexture);
                AddTextureDependency(graph, state, source.GroundColorTexture);
                AddTextureDependency(graph, state, source.GroundLayerTexture);
            }
            int outputCount = 2 + Mathf.Max(0, state.ActiveGroups.Count - 1);
            for (int output = 0; output < outputCount; output++)
            {
                using (IRasterRenderGraphBuilder builder = graph.AddRasterRenderPass<PlacementCapturePass>(
                           output == 1 ? "Grass Ground Color" : "Grass Authored Surface Density", out PlacementCapturePass pass))
                {
                    pass.Sources = output == 1 || state.ActiveGroups.Count == 0 ? state.Sources :
                        state.ActiveGroups[output == 0 ? 0 : output - 1].Sources;
                    pass.Quad = captureQuad;
                    pass.Material = placementMaterial;
                    pass.CaptureVP = captureVP;
                    pass.PassIndex = output == 1 ? 1 : 0;
                    pass.DrawSources = authored && placementMaterial;
                    foreach (TextureHandle texture in state.FrameTextureDependencies)
                        builder.UseTexture(texture, AccessFlags.Read);
                    TextureHandle target = output == 1 ? ground :
                        (output == 0 ? density : state.ActiveGroups[output - 1].DensityTexture);
                    builder.SetRenderAttachment(target, 0, AccessFlags.Write);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc(static (PlacementCapturePass data, RasterGraphContext context) =>
                    {
                        context.cmd.SetGlobalMatrix(Id.CaptureVP, data.CaptureVP);
                        context.cmd.ClearRenderTarget(false, true, Color.clear);
                        if (!data.DrawSources)
                            return;
                        for (int i = 0; i < data.Sources.Count; i++)
                        {
                            GrassPlacementDrawData source = data.Sources[i].Data;
                            MaterialPropertyBlock properties = context.renderGraphPool.GetTempMaterialPropertyBlock();
                            properties.SetMatrix(Id.PlacementMapping, source.WorldToMask);
                            properties.SetVector(Id.PlacementTerrain, source.TerrainRect);
                            properties.SetInteger(Id.PlacementHasTerrain, source.Terrain ? 1 : 0);
                            properties.SetInteger(Id.PlacementShape, (int)source.Shape);
                            properties.SetFloat(Id.PlacementDensity, source.Density);
                            properties.SetFloat(Id.PlacementFalloff, source.EdgeFalloff);
                            properties.SetTexture(Id.PlacementTexture, source.DensityTexture ? source.DensityTexture : Texture2D.whiteTexture);
                            properties.SetTexture(Id.PlacementGroundTexture, source.GroundColorTexture ? source.GroundColorTexture : Texture2D.whiteTexture);
                            properties.SetInteger(Id.PlacementHasGround, source.GroundColorTexture ? 1 : 0);
                            properties.SetColor(Id.PlacementTint, source.GroundTint);
                            properties.SetFloat(Id.PlacementGroundStrength, source.GroundColorStrength);
                            properties.SetTexture(Id.PlacementLayer, source.GroundLayerTexture ? source.GroundLayerTexture : Texture2D.whiteTexture);
                            properties.SetInteger(Id.PlacementHasLayer, source.GroundLayerTexture ? 1 : 0);
                            properties.SetVector(Id.PlacementLayerUV, source.GroundLayerUV);
                            properties.SetVector(Id.PlacementRemapMin, source.GroundLayerRemapMin);
                            properties.SetVector(Id.PlacementRemapMax, source.GroundLayerRemapMax);
                            context.cmd.DrawMesh(data.Quad, source.LocalToWorld, data.Material, 0, data.PassIndex, properties);
                        }
                    });
                }
            }
        }

        private void BuildDispatches(RenderGraph graph, CameraState state, InfiniteGrassRenderer owner,
            Bounds bounds, float spacing, bool authored, TextureHandle fallbackDensity)
        {
            state.DispatchCount = 0;
            state.CandidateCount = 0;
            bool valid = TryGridBounds(bounds, spacing, out int minX, out int minZ, out int maxX, out int maxZ);
            if (valid && !authored)
                valid = AddDispatch(state, null, minX, minZ, maxX - minX, maxZ - minZ);

            if (valid && authored)
            {
                for (int g = 0; g < state.ActiveGroups.Count && valid; g++)
                {
                    TerrainGroup group = state.ActiveGroups[g];
                    group.Tiles.Clear();
                    for (int a = 0; a < group.Sources.Count && valid; a++)
                    {
                        PlacementSource source = group.Sources[a];
                        if (!IntersectXZ(source.Data.WorldBounds, bounds, out Bounds intersection))
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
                            Bounds tileBounds = new Bounds(
                                new Vector3((tx + 0.5f) * TileCells * spacing, 0f, (tz + 0.5f) * TileCells * spacing),
                                new Vector3((TileCells + 1) * spacing, 0f, (TileCells + 1) * spacing));
                            if (!source.Area.IntersectsCoverage(tileBounds))
                                continue;
                            group.Tiles.Add(tile);
                            if ((long)group.Tiles.Count * TileCells * TileCells > MaximumCandidates)
                            {
                                valid = false;
                                break;
                            }
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
                state.DispatchCount = 0;
                state.CandidateCount = 0;
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
            long candidates = (long)width * height;
            if (candidates > MaximumCandidates - state.CandidateCount)
                return false;
            state.CandidateCount += candidates;
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
                pass.FullDensityDistance = owner.fullDensityDistance;
                pass.DensityExponent = owner.densityFalloffExponent;
                pass.DensityTransition = owner.densityTransition;
                pass.LodDistances = new Vector4(owner.nearLodDistance, owner.subdivisionDistance, 0f, 0f);
                pass.LodTransition = owner.lodTransitionWidth;
                pass.BoundsRadius = radius;
                pass.Authored = authored;
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
                            (dispatch.SizeInCells[0] + 7) / 8, (dispatch.SizeInCells[1] + 7) / 8, 1);
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

        private static Matrix4x4 MakeCaptureMatrix(Vector2 center, float extent, float minY, float maxY)
        {
            Matrix4x4 view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) *
                Matrix4x4.TRS(new Vector3(center.x, maxY + 1f, center.y),
                    Quaternion.LookRotation(Vector3.down, Vector3.forward), Vector3.one).inverse;
            Matrix4x4 projection = Matrix4x4.Ortho(-extent, extent, -extent, extent, 0.01f, maxY - minY + 2f);
            return GL.GetGPUProjectionMatrix(projection, true) * view;
        }

        private static float CalculateBladeRadius(Material material, InfiniteGrassRenderer owner, Camera camera)
        {
            float height = Mathf.Max(0f, material.GetFloat("_GrassHeight")) * (1f + Mathf.Max(0f, owner.subdivisionHeightBoost));
            float width = Mathf.Max(0f, material.GetFloat("_GrassWidth")) + Mathf.Max(0f, material.GetFloat("_ExpandDistantGrassWidth"));
            float pixelWidth = owner.minimumPixelWidth * 2f *
                (camera.orthographic ? 1f : owner.drawDistance) /
                Mathf.Max(1f, camera.pixelHeight * Mathf.Abs(camera.projectionMatrix.m11));
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
            double x0 = Math.Floor((double)bounds.min.x / spacing - 0.5);
            double z0 = Math.Floor((double)bounds.min.z / spacing - 0.5);
            double x1 = Math.Ceiling((double)bounds.max.x / spacing + 0.5) + 1;
            double z1 = Math.Ceiling((double)bounds.max.z / spacing + 0.5) + 1;
            const double limit = int.MaxValue / 2.0 - TileCells;
            minX = minZ = maxX = maxZ = 0;
            if (double.IsNaN(x0 + z0 + x1 + z1) || x0 < -limit || z0 < -limit ||
                x1 > limit || z1 > limit || x1 <= x0 || z1 <= z0)
                return false;
            minX = (int)x0;
            minZ = (int)z0;
            maxX = (int)x1;
            maxZ = (int)z1;
            return true;
        }

        private static int FloorDivide(int value, int divisor) =>
            value >= 0 ? value / divisor : (int)(((long)value - divisor + 1) / divisor);

        private static bool IntersectsXZ(Bounds a, Bounds b) =>
            a.min.x <= b.max.x && a.max.x >= b.min.x && a.min.z <= b.max.z && a.max.z >= b.min.z;

        private static bool IntersectXZ(Bounds a, Bounds b, out Bounds intersection)
        {
            Vector3 min = new Vector3(Mathf.Max(a.min.x, b.min.x), 0f, Mathf.Max(a.min.z, b.min.z));
            Vector3 max = new Vector3(Mathf.Min(a.max.x, b.max.x), 0f, Mathf.Min(a.max.z, b.max.z));
            intersection = default;
            if (min.x >= max.x || min.z >= max.z)
                return false;
            intersection.SetMinMax(min, max);
            return true;
        }

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
            wrapper.LastFrame = Time.frameCount;
            imported = graph.ImportTexture(wrapper.Handle);
            state.ImportedTextures.Add(texture, imported);
            return imported;
        }

        private static void AddTextureDependency(RenderGraph graph, CameraState state, Texture texture)
        {
            if (texture)
                state.FrameTextureDependencies.Add(ImportTexture(graph, state, texture));
        }

        private void PruneCameras()
        {
            staleCameras.Clear();
            foreach (KeyValuePair<Camera, CameraState> pair in cameras)
                if (!pair.Key || Time.frameCount - pair.Value.LastFrame > 120)
                    staleCameras.Add(pair.Key);
            for (int i = 0; i < staleCameras.Count; i++)
            {
                cameras[staleCameras[i]].Dispose();
                cameras.Remove(staleCameras[i]);
            }
        }

        public void ReleaseCameras()
        {
            foreach (CameraState state in cameras.Values)
                state.Dispose();
            cameras.Clear();
        }

        public void Dispose()
        {
            ReleaseCameras();
            CoreUtils.Destroy(placementMaterial);
            CoreUtils.Destroy(captureQuad);
            placementMaterial = null;
            captureQuad = null;
        }

        private sealed class CameraState : IDisposable
        {
            public readonly Camera Camera;
            public InfiniteGrassRenderer Owner;
            public uint OwnerRevision, PlacementRevision;
            public bool CacheValid, WarnedBudget, WarnedMaterial, Disposed;
            public Vector2 Center;
            public Vector2 CaptureRange;
            public float CaptureExtent;
            public bool Authored, HasMeshSurfaceFallback;
            public int LastFrame;
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
            public readonly TextureHandle[] VertexTextures = new TextureHandle[5];
            public Renderer[] CaptureRenderers;
            public readonly List<Renderer> ModifierRenderers = new List<Renderer>();
            public readonly HashSet<Renderer> ExplicitSurfaces = new HashSet<Renderer>();
            public readonly List<RendererDraw> HeightDraws = new List<RendererDraw>();
            public readonly List<RendererDraw> MaskDraws = new List<RendererDraw>();
            public readonly List<RendererDraw> ColorDraws = new List<RendererDraw>();
            public readonly List<RendererDraw> SlopeDraws = new List<RendererDraw>();
            public readonly List<PlacementSource> Sources = new List<PlacementSource>();
            public readonly Dictionary<int, TerrainGroup> Groups = new Dictionary<int, TerrainGroup>();
            public readonly List<int> StaleGroupKeys = new List<int>();
            public readonly List<TerrainGroup> ActiveGroups = new List<TerrainGroup>();
            public readonly List<DispatchData> Dispatches = new List<DispatchData>();
            public int DispatchCount;
            public long CandidateCount;
            public readonly Dictionary<Texture, TextureWrapper> TextureWrappers = new Dictionary<Texture, TextureWrapper>();
            public readonly Dictionary<Texture, TextureHandle> ImportedTextures = new Dictionary<Texture, TextureHandle>();
            public readonly HashSet<TextureHandle> FrameTextureDependencies = new HashSet<TextureHandle>();
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
                if (Positions == null || allocatedCapacity < capacity)
                {
                    Positions?.Dispose();
                    Positions = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, sizeof(float) * 4)
                    { name = "Grass Positions " + Camera.GetInstanceID() };
                    allocatedCapacity = capacity;
                }
                if (Counts == null)
                    Counts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 4, sizeof(uint)) { name = "Grass Counts" };
                bool uploadArguments = Arguments == null;
                if (Arguments == null)
                    Arguments = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.IndirectArguments,
                        3, argumentStride) { name = "Grass Indirect Arguments" };
                owner.GetLodCapacity(LodCapacities, LodOffsets);
                for (int lod = 0; lod < 3; lod++)
                {
                    if (argumentMeshes[lod] != meshes[lod])
                    {
                        argumentMeshes[lod] = meshes[lod];
                        uploadArguments = true;
                    }
                    argumentData[lod] = new GraphicsBuffer.IndirectDrawIndexedArgs
                    {
                        indexCountPerInstance = meshes[lod].GetIndexCount(0),
                        instanceCount = 0,
                        startIndex = meshes[lod].GetIndexStart(0),
                        baseVertexIndex = meshes[lod].GetBaseVertex(0),
                        startInstance = 0
                    };
                }
                if (uploadArguments)
                    Arguments.SetData(argumentData);
                return changed;
            }

            public void UpdateMaterial(InfiniteGrassRenderer owner, bool multisampled)
            {
                if (!BladeMaterial || sourceMaterial != owner.grassMaterial)
                {
                    CoreUtils.Destroy(BladeMaterial);
                    BladeMaterial = new Material(owner.grassMaterial) { hideFlags = HideFlags.HideAndDontSave };
                    sourceMaterial = owner.grassMaterial;
                    WarnedMaterial = false;
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
            }

            public void SetDrawProperties(Vector2 center, float distance,
                float capturePadding, Texture wind, int mainLightCascades)
            {
                // Indirect draws have no per-renderer UnityPerDraw probe setup.
                // Use the scene ambient probe, packed by Core's official helper;
                // this does not sample local LightProbes at individual grass roots.
                SHCoefficients ambient = new SHCoefficients(RenderSettings.ambientProbe);
                for (int lod = 0; lod < 3; lod++)
                {
                    MaterialPropertyBlock properties = DrawProperties[lod];
                    properties.Clear();
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
                    properties.SetTexture(Id.Wind, wind);
                    properties.SetVector(Id.HeightTexelSize, new Vector4(1f / Height.rt.width,
                        1f / Height.rt.height, Height.rt.width, Height.rt.height));
                }
            }

            public static bool Allocate(ref RTHandle handle, int size, GraphicsFormat format,
                string name, bool depth = false)
            {
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
                if (Disposed || request.hasError || !ReadbackOwner)
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
                    if (!pair.Key || Time.frameCount - pair.Value.LastFrame > 120)
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
                CoreUtils.Destroy(BladeMaterial);
            }
        }

        private sealed class TextureWrapper
        {
            public RTHandle Handle;
            public int LastFrame;
        }

        private struct PlacementSource
        {
            public GrassPlacementArea Area;
            public GrassPlacementDrawData Data;
        }

        private readonly struct RendererDraw
        {
            public readonly Renderer Renderer;
            public readonly Material Material;
            public readonly int Submesh, Pass;
            public RendererDraw(Renderer renderer, Material material, int submesh, int pass)
            {
                Renderer = renderer;
                Material = material;
                Submesh = submesh;
                Pass = pass;
            }
        }

        private sealed class TerrainGroup
        {
            public Terrain Terrain;
            public RTHandle DensityMap;
            public TextureHandle HeightTexture, HolesTexture, DensityTexture;
            public readonly List<PlacementSource> Sources = new List<PlacementSource>();
            public readonly HashSet<Vector2Int> Tiles = new HashSet<Vector2Int>();
            public readonly List<Vector2Int> SortedTiles = new List<Vector2Int>();
        }

        private sealed class TileComparer : IComparer<Vector2Int>
        {
            public static readonly TileComparer Instance = new TileComparer();
            public int Compare(Vector2Int a, Vector2Int b)
            {
                int z = a.y.CompareTo(b.y);
                return z != 0 ? z : a.x.CompareTo(b.x);
            }
        }

        private sealed class DispatchData
        {
            public TerrainGroup Group;
            public readonly int[] Start = new int[2];
            public readonly int[] SizeInCells = new int[2];
            public bool UseTerrain, HasHoles;
            public Vector3 Origin, Size;
            public TextureHandle TerrainHeight, TerrainHoles, Density;
        }

        private sealed class RendererCapturePass
        {
            public List<RendererDraw> Draws;
            public Matrix4x4 CaptureVP;
            public bool HasDepth;
            public Color Clear;
        }

        private sealed class TerrainCapturePass
        {
            public List<TerrainGroup> Groups;
            public Mesh Quad;
            public Material Material;
            public int PassIndex;
            public Matrix4x4 CaptureVP;
        }

        private sealed class PlacementCapturePass
        {
            public List<PlacementSource> Sources;
            public Mesh Quad;
            public Material Material;
            public Matrix4x4 CaptureVP;
            public int PassIndex;
            public bool DrawSources;
        }

        private sealed class GenerationPass
        {
            public ComputeShader Shader;
            public int ResetKernel, GenerateKernel, FinalizeKernel;
            public GraphicsBuffer Positions, Counts, Arguments;
            public TextureHandle Height, Density, Mask;
            public List<DispatchData> Dispatches;
            public int DispatchCount;
            public int[] LodOffsets, LodCapacities;
            public Vector4[] Frustum;
            public Vector2 Center;
            public Vector3 CameraPosition;
            public float Spacing, DrawDistance, CapturePadding, FullDensityDistance, DensityExponent,
                DensityTransition, LodTransition, BoundsRadius;
            public Vector4 LodDistances;
            public bool Authored;
            public int ArgumentStride, ArgumentCountOffset;
        }

        private sealed class DrawPass
        {
            public GraphicsBuffer Arguments;
            public Mesh[] Meshes;
            public Material Material;
            public MaterialPropertyBlock[] Properties;
            public int ShaderPass, ArgumentStride;
        }

        private sealed class ReadbackPass
        {
            public CameraState State;
            public GraphicsBuffer Counts;
        }
    }
}
