using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

public partial class GrassDataRendererFeature
{
    // The height, modifier, interaction and placement captures.
    private sealed partial class GrassDataPass
    {
        private void BuildHeightCapture(RenderGraph graph, CameraState state, TextureHandle height,
            TextureHandle depth, Matrix4x4 captureVP)
        {
            if (!heightMaterial && state.RequiresMeshHeight && !missingHeightWarning)
            {
                Debug.LogWarning("Grass mesh surfaces require the height capture material. Explicit Terrain areas still use TerrainData.");
                missingHeightWarning = true;
            }
            BuildRendererCapture(graph, state, "Grass Surface Height", state.HeightDraws, height, depth, captureVP, Color.clear);
            if (state.SurfaceTerrains.Count == 0 || !EnsurePlacementMaterial())
                return;
            int terrainPass = placementMaterial.FindPass("TerrainHeight");
            if (terrainPass < 0)
                return;

            state.TerrainDraws.Clear();
            for (int i = 0; i < state.SurfaceTerrains.Count; i++)
            {
                Terrain terrain = state.SurfaceTerrains[i];
                TerrainData data = terrain ? terrain.terrainData : null;
                if (!data || !data.heightmapTexture)
                    continue;
                Vector3 origin = terrain.transform.position;
                Vector3 size = data.size;
                if (!IsFinite(origin) || !IsFinite(size) || size.x <= 0f || size.z <= 0f)
                    continue;
                Texture heightmap = data.heightmapTexture;
                Texture holes = data.holesTexture;
                state.TerrainDraws.Add(new TerrainDraw
                {
                    Origin = origin,
                    Size = size,
                    Height = ImportTexture(graph, state, heightmap),
                    Holes = ImportTexture(graph, state, holes ? holes : Texture2D.whiteTexture),
                    HasHoles = holes,
                    HeightTexelSize = new Vector4(1f / heightmap.width, 1f / heightmap.height, heightmap.width, heightmap.height),
                    LocalToWorld = Matrix4x4.TRS(origin + new Vector3(size.x * 0.5f, 0f, size.z * 0.5f),
                        Quaternion.identity, new Vector3(size.x, 1f, size.z))
                });
            }
            if (state.TerrainDraws.Count == 0)
                return;
            using (IRasterRenderGraphBuilder builder = graph.AddRasterRenderPass<TerrainCapturePass>(
                       "Grass Terrain Surface Height", out TerrainCapturePass pass))
            {
                pass.Draws = state.TerrainDraws;
                pass.Quad = captureQuad;
                pass.Material = placementMaterial;
                pass.PassIndex = terrainPass;
                pass.CaptureVP = captureVP;
                for (int i = 0; i < pass.Draws.Count; i++)
                {
                    builder.UseTexture(pass.Draws[i].Height, AccessFlags.Read);
                    builder.UseTexture(pass.Draws[i].Holes, AccessFlags.Read);
                }
                builder.SetRenderAttachment(height, 0, AccessFlags.ReadWrite);
                builder.SetRenderAttachmentDepth(depth, AccessFlags.ReadWrite);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (TerrainCapturePass data, RasterGraphContext context) =>
                {
                    context.cmd.SetGlobalMatrix(Id.CaptureVP, data.CaptureVP);
                    for (int i = 0; i < data.Draws.Count; i++)
                    {
                        TerrainDraw draw = data.Draws[i];
                        Vector3 origin = draw.Origin;
                        Vector3 size = draw.Size;
                        MaterialPropertyBlock properties = context.renderGraphPool.GetTempMaterialPropertyBlock();
                        properties.SetVector(Id.PlacementTerrain, new Vector4(origin.x, origin.z, size.x, size.z));
                        properties.SetInteger(Id.PlacementHasTerrain, 1);
                        properties.SetTexture(Id.PlacementHeight, draw.Height);
                        properties.SetVector(Id.PlacementHeightTexelSize, draw.HeightTexelSize);
                        properties.SetTexture(Id.PlacementHoles, draw.Holes);
                        properties.SetInteger(Id.PlacementHasHoles, draw.HasHoles ? 1 : 0);
                        properties.SetFloat(Id.PlacementOriginY, origin.y);
                        properties.SetFloat(Id.PlacementHeightRange, size.y);
                        context.cmd.DrawMesh(data.Quad, draw.LocalToWorld, data.Material, 0, data.PassIndex, properties);
                    }
                });
            }
        }

        private static void BuildRendererCapture(RenderGraph graph, CameraState state, string name, List<RendererDraw> draws,
            TextureHandle target, TextureHandle depth, Matrix4x4 captureVP, Color clear,
            List<InteractionDraw> interactions = null)
        {
            using (IRasterRenderGraphBuilder builder = graph.AddRasterRenderPass<RendererCapturePass>(
                       name, out RendererCapturePass pass))
            {
                pass.Draws = draws;
                pass.Interactions = interactions;
                pass.CaptureVP = captureVP;
                pass.HasDepth = depth.IsValid();
                pass.Clear = clear;
                for (int i = 0; i < draws.Count; i++)
                {
                    RendererDraw draw = draws[i];
                    int textureCount = CollectCaptureTextures(draw.Renderer, draw.Material, draw.Submesh,
                        state.CaptureProperties, state.CaptureTextureIds, state.CaptureTextures);
                    for (int texture = 0; texture < textureCount; texture++)
                        builder.UseTexture(ImportTexture(graph, state, state.CaptureTextures[texture]), AccessFlags.Read);
                }
                for (int i = 0; interactions != null && i < interactions.Count; i++)
                {
                    int textureCount = CollectCaptureTextures(null, interactions[i].Material, 0,
                        state.CaptureProperties, state.CaptureTextureIds, state.CaptureTextures);
                    for (int texture = 0; texture < textureCount; texture++)
                        builder.UseTexture(ImportTexture(graph, state, state.CaptureTextures[texture]), AccessFlags.Read);
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
                    // Interactors register their world-space mesh directly. Their
                    // owned geometry never enters scene or surface-height discovery.
                    for (int i = 0; data.Interactions != null && i < data.Interactions.Count; i++)
                    {
                        InteractionDraw draw = data.Interactions[i];
                        if (draw.Mesh && draw.Material)
                            context.cmd.DrawMesh(draw.Mesh, Matrix4x4.identity, draw.Material, 0, draw.Pass);
                    }
                });
            }
        }

        private static int CollectCaptureTextures(Renderer renderer, Material material, int materialIndex,
            MaterialPropertyBlock properties, List<int> textureIds, List<Texture> textures)
        {
            textureIds.Clear();
            textures.Clear();
            if (!material)
                return 0;
            // Custom capture passes can expose inputs beyond _MainTex. Reuse
            // the list overload so discovering them does not allocate an array.
            material.GetTexturePropertyNameIDs(textureIds);
            if (textureIds.Count == 0)
                return 0;
            for (int i = 0; i < textureIds.Count; i++)
                AddCaptureTexture(material.GetTexture(textureIds[i]), textures);
            if (renderer && renderer.HasPropertyBlock())
            {
                // Declare both override scopes conservatively, including inputs
                // supplied only through an externally rendered texture override.
                renderer.GetPropertyBlock(properties);
                for (int i = 0; i < textureIds.Count; i++)
                    AddCaptureTexture(properties.GetTexture(textureIds[i]), textures);
                renderer.GetPropertyBlock(properties, materialIndex);
                for (int i = 0; i < textureIds.Count; i++)
                    AddCaptureTexture(properties.GetTexture(textureIds[i]), textures);
            }
            return textures.Count;
        }

        private static Texture ResolveWindTexture(Material material)
        {
            Texture texture = material.GetTexture(Id.Wind);
            // Released RenderTextures remain live Unity objects. Sampling one
            // would recreate storage with undefined contents; its producer must
            // restore the wind map before grass can use it again. Wind shaders
            // use Texture2D sampling, so unresolved multisampled storage is also
            // incompatible even while the producer's texture remains created.
            return texture && texture.dimension == TextureDimension.Tex2D &&
                (!(texture is RenderTexture renderTexture) || (renderTexture.IsCreated() &&
                    (renderTexture.antiAliasing <= 1 || !renderTexture.bindTextureMS)))
                ? texture : Texture2D.grayTexture;
        }

        private static void AddCaptureTexture(Texture texture, List<Texture> textures)
        {
            if (!texture)
                return;
            for (int i = 0; i < textures.Count; i++)
                if (textures[i] == texture)
                    return;
            textures.Add(texture);
        }

        private void BuildPlacementCapture(RenderGraph graph, CameraState state, TextureHandle density,
            TextureHandle ground, Matrix4x4 captureVP, bool authored, bool groundDirty, bool clearEmptyDensity)
        {
            if (authored)
            {
                for (int i = 0; i < state.ActiveGroups.Count; i++)
                {
                    TerrainGroup group = state.ActiveGroups[i];
                    if (!group.DensityDirty)
                        continue;
                    BuildPlacementCapture(graph, state, group.Sources, group.DensityTexture,
                        captureVP, 0, true);
                    group.DensityCaptured = true;
                    group.CapturedDensityVersion = group.DensityVersion;
                }
            }
            if ((!authored || state.ActiveGroups.Count == 0) && clearEmptyDensity)
                BuildPlacementCapture(graph, state, state.Sources, density, captureVP, 0, false);
            if (groundDirty)
                BuildPlacementCapture(graph, state, state.Sources, ground, captureVP, 1, authored);
        }

        private void BuildPlacementCapture(RenderGraph graph, CameraState state,
            List<PlacementSource> sources, TextureHandle target, Matrix4x4 captureVP, int passIndex, bool drawSources)
        {
            using (IRasterRenderGraphBuilder builder = graph.AddRasterRenderPass<PlacementCapturePass>(
                       passIndex == 1 ? "Grass Ground Color" : "Grass Authored Surface Density", out PlacementCapturePass pass))
            {
                pass.Sources = sources;
                pass.Quad = captureQuad;
                pass.Material = placementMaterial;
                pass.CaptureVP = captureVP;
                pass.PassIndex = passIndex;
                pass.DrawSources = drawSources && placementMaterial;
                // Each map imports only its own source textures. Static maps do
                // not keep wrappers or unrelated source inputs alive every frame.
                for (int i = 0; pass.DrawSources && i < sources.Count; i++)
                {
                    GrassPlacementDrawData source = sources[i].Data;
                    if (source.DensityTexture)
                        builder.UseTexture(ImportTexture(graph, state, source.DensityTexture), AccessFlags.Read);
                    if (passIndex != 1)
                        continue;
                    if (source.GroundColorTexture)
                        builder.UseTexture(ImportTexture(graph, state, source.GroundColorTexture), AccessFlags.Read);
                    if (source.GroundLayerTexture)
                    {
                        builder.UseTexture(ImportTexture(graph, state, source.GroundLayerTexture), AccessFlags.Read);
                        Texture samplerTexture = ResolveGroundLayerSamplerTexture(source);
                        if (samplerTexture != source.GroundLayerTexture)
                            builder.UseTexture(ImportTexture(graph, state, samplerTexture), AccessFlags.Read);
                    }
                }
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
                        properties.SetMatrix(Id.PlacementGroundMapping, source.GroundWorldToMask);
                        properties.SetVector(Id.PlacementTerrain, source.TerrainRect);
                        properties.SetInteger(Id.PlacementHasTerrain, source.Terrain ? 1 : 0);
                        properties.SetInteger(Id.PlacementShape, (int)source.Shape);
                        properties.SetFloat(Id.PlacementDensity, source.Density);
                        properties.SetFloat(Id.PlacementFalloff, source.EdgeFalloff);
                        properties.SetTexture(Id.PlacementTexture, source.DensityTexture ? source.DensityTexture : Texture2D.whiteTexture);
                        properties.SetTexture(Id.PlacementGroundTexture, source.GroundColorTexture ? source.GroundColorTexture : Texture2D.whiteTexture);
                        properties.SetInteger(Id.PlacementTerrainGroundColor, source.GroundColorUsesTerrainBounds ? 1 : 0);
                        properties.SetInteger(Id.PlacementHasGround, source.GroundColorTexture ? 1 : 0);
                        properties.SetColor(Id.PlacementTint, source.GroundTint);
                        properties.SetFloat(Id.PlacementGroundStrength, source.GroundColorStrength);
                        properties.SetTexture(Id.PlacementLayer, source.GroundLayerTexture ? source.GroundLayerTexture : Texture2D.whiteTexture);
                        Texture layerSampler = ResolveGroundLayerSamplerTexture(source);
                        properties.SetTexture(Id.PlacementLayerSampler, layerSampler);
                        properties.SetInteger(Id.PlacementLayerIsSampler, source.GroundLayerTexture == layerSampler ? 1 : 0);
                        properties.SetInteger(Id.PlacementHasLayer, source.GroundLayerTexture ? 1 : 0);
                        properties.SetVector(Id.PlacementLayerUV, source.GroundLayerUV);
                        properties.SetVector(Id.PlacementRemapMin, source.GroundLayerRemapMin);
                        properties.SetVector(Id.PlacementRemapMax, source.GroundLayerRemapMax);
                        context.cmd.DrawMesh(data.Quad, source.LocalToWorld, data.Material, 0, data.PassIndex, properties);
                    }
                });
            }
        }

        // Each area's per-kind weight multipliers over its footprint, at the largest of overlapping areas. A texel no
        // area reaches stays zero, which the generation reads as the profile's own mix.
        private void BuildVariantCapture(RenderGraph graph, CameraState state, Matrix4x4 captureVP)
        {
            int passIndex = placementMaterial ? placementMaterial.FindPass("PlacementVariants") : -1;
            using (IRasterRenderGraphBuilder builder = graph.AddRasterRenderPass<VariantCapturePass>(
                       "Grass Kind Weights", out VariantCapturePass pass))
            {
                pass.Sources = state.Sources;
                pass.Quad = captureQuad;
                pass.Material = placementMaterial;
                pass.CaptureVP = captureVP;
                pass.PassIndex = passIndex;
                for (int i = 0; passIndex >= 0 && i < state.Sources.Count; i++)
                {
                    Texture densityTexture = state.Sources[i].Data.DensityTexture;
                    if (densityTexture)
                        builder.UseTexture(ImportTexture(graph, state, densityTexture), AccessFlags.Read);
                }
                builder.SetRenderAttachment(graph.ImportTexture(state.VariantWeights0), 0, AccessFlags.Write);
                builder.SetRenderAttachment(graph.ImportTexture(state.VariantWeights1), 1, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (VariantCapturePass data, RasterGraphContext context) =>
                {
                    context.cmd.SetGlobalMatrix(Id.CaptureVP, data.CaptureVP);
                    context.cmd.ClearRenderTarget(false, true, Color.clear);
                    if (data.PassIndex < 0 || !data.Material)
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
                        properties.SetVector(Id.PlacementVariantWeights0, source.VariantWeights0);
                        properties.SetVector(Id.PlacementVariantWeights1, source.VariantWeights1);
                        context.cmd.DrawMesh(data.Quad, source.LocalToWorld, data.Material, 0, data.PassIndex, properties);
                    }
                });
            }
        }

        private static Texture ResolveGroundLayerSamplerTexture(GrassPlacementDrawData source) =>
            source.GroundLayerSamplerTexture ? source.GroundLayerSamplerTexture :
            source.GroundLayerTexture ? source.GroundLayerTexture : Texture2D.whiteTexture;

        // An owner that draws no blades keeps only the interaction capture, over the window the blades would use, so
        // the published field still carries contacts where nothing grows. It is a clear and the interactors' meshes, so
        // it recaptures every frame instead of keeping the blade path's versions.
        private void RecordInteractionField(RenderGraph graph, Camera camera, InfiniteGrassRenderer owner)
        {
            float capturePadding = Mathf.Max(0.01f, FiniteOr(owner.textureUpdateThreshold, 10f));
            float extent = Mathf.Max(0.01f, FiniteOr(owner.drawDistance, 300f)) + capturePadding;
            Vector3 cameraPosition = camera.transform.position;
            Vector2 center = new Vector2(Mathf.Floor(cameraPosition.x / capturePadding) * capturePadding,
                Mathf.Floor(cameraPosition.z / capturePadding) * capturePadding);
            if (!IsFinite(cameraPosition) || !IsFinite(new Vector3(center.x, extent * 2f, center.y)))
                return;

            PruneCameras();
            if (!cameras.TryGetValue(camera, out CameraState state))
            {
                state = new CameraState(camera);
                cameras.Add(camera, state);
            }
            state.LastUsedTime = Time.realtimeSinceStartupAsDouble;
            state.ImportedTextures.Clear();
            // The blade caches are not kept up here; drawing blades again recaptures them all.
            state.CacheValid = false;
            motion.Release(camera);
            state.EnsureInteractionResource(owner);

            Vector2 range = ResolveCaptureHeightRange(owner.captureHeightRange);
            Bounds bounds = new Bounds();
            bounds.SetMinMax(new Vector3(center.x - extent, range.x, center.y - extent),
                new Vector3(center.x + extent, Mathf.Max(range.x + 1f, range.y), center.y + extent));
            CollectInteractionDraws(state, bounds);
            state.SlopeDraws.Clear();
            BuildRendererCapture(graph, state, "Grass Interaction", state.SlopeDraws, graph.ImportTexture(state.Slope),
                default, MakeCaptureMatrix(center, extent, bounds.min.y, bounds.max.y), Color.clear, state.InteractionDraws);
            GrassInteractionPublisher.Publish(state.Slope.rt, center, extent);
            state.PruneTextureWrappers();
        }
    }
}
