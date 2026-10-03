using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Optional, short-range screen-space contact shadows involving the indirect grass.
/// Unity 6.6 URP has no built-in directional contact-shadow feature. This effect
/// multiplies the already shaded color, so it also attenuates ambient lighting;
/// keep its strength restrained. Off-screen and hidden occluders are unavailable.
/// </summary>
public static class GrassContactShadows
{
    [Serializable]
    public sealed class Settings
    {
        [Tooltip("Adds a grass depth draw and a screen-space contact pass. The overlay also darkens ambient lighting.")]
        public bool enabled;

        [Range(0f, 1f)] public float strength = 0.35f;
        [Min(0.01f)] public float rayLength = 0.75f;
        [Range(4, 32)] public int steps = 8;
        [Min(0.001f)] public float bias = 0.03f;
        [Min(0.001f)] public float thickness = 0.15f;
        [Min(0.1f)] public float maxDistance = 50f;
    }

    private static readonly int BlitScaleBias = Shader.PropertyToID("_BlitScaleBias");
    private static readonly int SceneDepthTexture = Shader.PropertyToID("_GrassContactSceneDepth");
    private static readonly int GrassDepthTexture = Shader.PropertyToID("_GrassContactGrassDepth");
    private static readonly int DepthScale = Shader.PropertyToID("_GrassContactDepthScale");
    private static readonly int GrassDepthScale = Shader.PropertyToID("_GrassContactGrassDepthScale");
    private static readonly int ContactParameters = Shader.PropertyToID("_GrassContactParameters");
    private static readonly int ContactLimits = Shader.PropertyToID("_GrassContactLimits");

    private static Material _contactMaterial;
    private static bool _warnedMissingShader;

    /// <summary>
    /// Call after the grass color draw, after opaque rendering. The caller must
    /// request a camera depth texture. Camera color is an unsampled blend
    /// attachment, so its individual MSAA samples and alpha remain intact.
    /// Arguments contains three consecutive GraphicsBuffer.IndirectDrawIndexedArgs
    /// commands. Each property block includes its LOD's grass instance offset.
    /// All graph textures read by blade deformation/coverage must be listed in
    /// vertexTextureHandles; binding them in a property block is not a dependency.
    /// The supplied buffers, meshes and property blocks must remain valid until
    /// this graph has executed. Only the separate depth textures are sampled.
    /// </summary>
    public static void Record(
        RenderGraph renderGraph,
        ContextContainer frameData,
        BufferHandle positionsHandle,
        BufferHandle argumentsHandle,
        GraphicsBuffer positionsBuffer,
        GraphicsBuffer argumentsBuffer,
        Mesh[] lodMeshes,
        Material bladeMaterial,
        MaterialPropertyBlock[] drawProperties,
        Settings settings,
        TextureHandle[] vertexTextureHandles = null)
    {
        // In particular, do not allocate textures or load/create a material while disabled.
        if (settings == null || !settings.enabled || settings.strength <= 0f ||
            settings.rayLength <= 0f || settings.maxDistance <= 0f)
            return;

        if (positionsBuffer == null || argumentsBuffer == null || bladeMaterial == null ||
            !positionsHandle.IsValid() || !argumentsHandle.IsValid() ||
            lodMeshes == null || lodMeshes.Length != 3 ||
            drawProperties == null || drawProperties.Length != 3)
            return;

        for (int lod = 0; lod < 3; lod++)
            if (lodMeshes[lod] == null || drawProperties[lod] == null)
                return;

        UniversalResourceData resources = frameData.Get<UniversalResourceData>();
        UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
        UniversalLightData lightData = frameData.Get<UniversalLightData>();

        // The indirect grass renderer currently supports single-view cameras.
        // Avoid producing one eye's shadow mask for both eyes in an XR camera.
        if (cameraData.camera.stereoEnabled ||
            !resources.activeColorTexture.IsValid() || !resources.cameraDepthTexture.IsValid())
            return;

        int mainLightIndex = lightData.mainLightIndex;
        if (mainLightIndex < 0 || mainLightIndex >= lightData.visibleLights.Length ||
            lightData.visibleLights[mainLightIndex].lightType != LightType.Directional ||
            lightData.visibleLights[mainLightIndex].finalColor.maxColorComponent <= 0f)
            return;

        int depthPassIndex = bladeMaterial.FindPass("GrassContactDepth");
        if (depthPassIndex < 0 || !TryGetMaterial(out Material contactMaterial))
            return;

        TextureHandle cameraColor = resources.activeColorTexture;
        TextureDesc cameraDescriptor = renderGraph.GetTextureDesc(cameraColor);
        TextureDesc grassDescriptor = cameraDescriptor;
        grassDescriptor.name = "Grass Contact Depth And Coverage";
        // URP 17.6 TextureDesc has one format field for color OR depth. Do not
        // clear depthBufferBits on a copied color descriptor: its setter changes format.
        // Preserve continuous A2C coverage as well as raw depth. Turning a partly
        // covered blade into a fully opaque occluder makes MSAA contacts too dark.
        grassDescriptor.format = GraphicsFormat.R32G32_SFloat;
        grassDescriptor.msaaSamples = MSAASamples.None;
        grassDescriptor.bindTextureMS = false;
        grassDescriptor.memoryless = RenderTextureMemoryless.None;
        grassDescriptor.enableRandomWrite = false;
        grassDescriptor.useMipMap = false;
        grassDescriptor.autoGenerateMips = false;
        grassDescriptor.filterMode = FilterMode.Point;
        grassDescriptor.wrapMode = TextureWrapMode.Clamp;
        grassDescriptor.clearBuffer = true;
        float farDepth = SystemInfo.usesReversedZBuffer ? 0f : 1f;
        grassDescriptor.clearColor = new Color(farDepth, 0f, 0f, 0f);
        TextureHandle grassDepth = renderGraph.CreateTexture(grassDescriptor);

        TextureDesc depthDescriptor = grassDescriptor;
        depthDescriptor.name = "Grass Contact Depth Attachment";
        depthDescriptor.format = GraphicsFormat.D32_SFloat;
        TextureHandle grassDepthAttachment = renderGraph.CreateTexture(depthDescriptor);

        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<DepthPassData>(
                   "Grass Contact Depth", out DepthPassData pass))
        {
            pass.Arguments = argumentsBuffer;
            pass.LodMeshes = lodMeshes;
            pass.BladeMaterial = bladeMaterial;
            pass.DrawProperties = drawProperties;
            pass.DepthPassIndex = depthPassIndex;

            builder.UseBuffer(positionsHandle, AccessFlags.Read);
            builder.UseBuffer(argumentsHandle, AccessFlags.Read);
            if (vertexTextureHandles != null)
            {
                foreach (TextureHandle texture in vertexTextureHandles)
                    if (texture.IsValid())
                        builder.UseTexture(texture, AccessFlags.Read);
            }
            builder.SetRenderAttachment(grassDepth, 0, AccessFlags.Write);
            builder.SetRenderAttachmentDepth(grassDepthAttachment, AccessFlags.Write);
            builder.SetRenderFunc(static (DepthPassData data, RasterGraphContext context) =>
            {
                for (int lod = 0; lod < 3; lod++)
                {
                    context.cmd.DrawMeshInstancedIndirect(
                        data.LodMeshes[lod], 0, data.BladeMaterial, data.DepthPassIndex,
                        data.Arguments, lod * GraphicsBuffer.IndirectDrawIndexedArgs.size,
                        data.DrawProperties[lod]);
                }
            });
        }

        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<ContactPassData>(
                   "Grass Contact Shadows", out ContactPassData pass))
        {
            pass.SceneDepth = resources.cameraDepthTexture;
            pass.GrassDepth = grassDepth;
            pass.Material = contactMaterial;
            pass.Parameters = new Vector4(
                Mathf.Clamp01(settings.strength), Mathf.Max(0.01f, settings.rayLength),
                Mathf.Max(0.001f, settings.bias), Mathf.Max(0.001f, settings.thickness));
            pass.Limits = new Vector4(
                Mathf.Max(0.1f, settings.maxDistance), Mathf.Clamp(settings.steps, 4, 32), 0.025f, 0f);

            builder.UseTexture(pass.SceneDepth, AccessFlags.Read);
            builder.UseTexture(pass.GrassDepth, AccessFlags.Read);
            // Fixed-function multiplication preserves the existing color separately
            // for every MSAA sample. Resolving color and copying it to all samples
            // here would break its correspondence with depth before transparents.
            builder.SetRenderAttachment(cameraColor, 0, AccessFlags.ReadWrite);
            builder.SetRenderFunc(static (ContactPassData data, RasterGraphContext context) =>
            {
                // Blit.hlsl's full-screen vertex entry point, with a pooled property
                // block so queued cameras never share mutable material parameters.
                MaterialPropertyBlock properties = context.renderGraphPool.GetTempMaterialPropertyBlock();
                properties.SetTexture(SceneDepthTexture, data.SceneDepth);
                properties.SetTexture(GrassDepthTexture, data.GrassDepth);
                // There is no blit source: vertex UVs span the camera viewport.
                properties.SetVector(BlitScaleBias, new Vector4(1f, 1f, 0f, 0f));
                properties.SetVector(DepthScale, GetTextureScale(data.SceneDepth));
                properties.SetVector(GrassDepthScale, GetTextureScale(data.GrassDepth));
                properties.SetVector(ContactParameters, data.Parameters);
                properties.SetVector(ContactLimits, data.Limits);
                context.cmd.DrawProcedural(Matrix4x4.identity, data.Material, 0,
                    MeshTopology.Triangles, 3, 1, properties);
            });
        }
    }

    /// <summary>Release the cached material from the owning renderer feature's Dispose.</summary>
    public static void Cleanup()
    {
        CoreUtils.Destroy(_contactMaterial);
        _contactMaterial = null;
        _warnedMissingShader = false;
    }

    private static Vector4 GetTextureScale(TextureHandle texture)
    {
        RTHandle handle = texture;
        if (!handle.useScaling)
            return new Vector4(1f, 1f, 0f, 0f);

        Vector4 scale = handle.rtHandleProperties.rtHandleScale;
        return new Vector4(scale.x, scale.y, 0f, 0f);
    }

    private static bool TryGetMaterial(out Material material)
    {
        if (_contactMaterial == null)
        {
            Shader shader = Resources.Load<Shader>("InfiniteGrassContactShadows");
            if (shader != null && shader.isSupported)
                _contactMaterial = CoreUtils.CreateEngineMaterial(shader);
            else if (!_warnedMissingShader)
            {
                Debug.LogWarning("Grass contact shadows require the supported InfiniteGrassContactShadows resource shader.");
                _warnedMissingShader = true;
            }
        }

        material = _contactMaterial;
        return material != null;
    }

    private sealed class DepthPassData
    {
        public GraphicsBuffer Arguments;
        public Mesh[] LodMeshes;
        public Material BladeMaterial;
        public MaterialPropertyBlock[] DrawProperties;
        public int DepthPassIndex;
    }

    private sealed class ContactPassData
    {
        public TextureHandle SceneDepth;
        public TextureHandle GrassDepth;
        public Material Material;
        public Vector4 Parameters;
        public Vector4 Limits;
    }
}
