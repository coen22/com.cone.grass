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
public sealed class GrassContactShadows : IDisposable
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
    private static readonly int CasterDistance = Shader.PropertyToID("_GrassContactCasterDistance");
    private static readonly int ViewProjection = Shader.PropertyToID("_GrassContactViewProjection");
    private static readonly int InverseViewProjection = Shader.PropertyToID("_GrassContactInverseViewProjection");
    private static readonly int InverseProjection = Shader.PropertyToID("_GrassContactInverseProjection");
    private static readonly int ViewMatrix = Shader.PropertyToID("_GrassContactViewMatrix");

    private Material _contactMaterial;
    private bool _warnedMissingShader;
    private bool _warnedUnsupportedFormats;
    private bool _formatsChecked;
    private bool _disposed;
    private GraphicsFormat _depthCoverageFormat;
    private GraphicsFormat _depthAttachmentFormat;

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
    public void Record(
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
        if (_disposed || !TryGetParameters(settings, out Vector4 parameters,
                out Vector4 limits, out float casterDistance))
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
        if (cameraData.camera == null || cameraData.camera.stereoEnabled ||
            !resources.activeColorTexture.IsValid() || !resources.cameraDepthTexture.IsValid())
            return;

        int mainLightIndex = lightData.mainLightIndex;
        if (mainLightIndex < 0 || mainLightIndex >= lightData.visibleLights.Length ||
            lightData.visibleLights[mainLightIndex].lightType != LightType.Directional ||
            lightData.visibleLights[mainLightIndex].finalColor.maxColorComponent <= 0f)
            return;

        TextureHandle cameraColor = resources.activeColorTexture;
        // Imported system backbuffers have RenderTargetInfo but deliberately no
        // TextureDesc. Query the actual attachment's format without assuming an
        // intermediate camera texture exists.
        RenderTargetInfo cameraTarget = renderGraph.GetRenderTargetInfo(cameraColor);
        TextureDesc sceneDepthDescriptor = renderGraph.GetTextureDesc(resources.cameraDepthTexture);
        // Each camera depth texture spans that camera's viewport, even when its
        // final target is a larger shared backbuffer. Match that sampling domain.
        if (cameraData.cameraTargetDescriptor.dimension != TextureDimension.Tex2D ||
            cameraTarget.volumeDepth != 1 ||
            sceneDepthDescriptor.dimension != TextureDimension.Tex2D ||
            sceneDepthDescriptor.msaaSamples != MSAASamples.None ||
            !TryGetFormats(cameraTarget.format))
            return;

        int depthPassIndex = bladeMaterial.FindPass("GrassContactDepth");
        if (depthPassIndex < 0 || !TryGetMaterial(out Material contactMaterial))
            return;

        // Shared color/motion property blocks do not consume this contact-only
        // property. Keep all deformed casters reachable by an in-range ray.
        for (int lod = 0; lod < 3; lod++)
            drawProperties[lod].SetFloat(CasterDistance, casterDistance);

        TextureDesc grassDescriptor = sceneDepthDescriptor;
        grassDescriptor.name = "Grass Contact Depth And Coverage";
        // URP 17.6 TextureDesc has one format field for color OR depth. Do not
        // clear depthBufferBits on a copied color descriptor: its setter changes format.
        // Preserve continuous A2C coverage as well as raw depth. Turning a partly
        // covered blade into a fully opaque occluder makes MSAA contacts too dark.
        grassDescriptor.format = _depthCoverageFormat;
        grassDescriptor.msaaSamples = MSAASamples.None;
        grassDescriptor.bindTextureMS = false;
        grassDescriptor.isShadowMap = false;
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
        depthDescriptor.format = _depthAttachmentFormat;
        TextureHandle grassDepthAttachment = renderGraph.CreateTexture(depthDescriptor);

        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<DepthPassData>(
                   "Grass Contact Depth", out DepthPassData pass))
        {
            pass.Arguments = argumentsBuffer;
            pass.LodMeshes = lodMeshes;
            pass.BladeMaterial = bladeMaterial;
            pass.DrawProperties = drawProperties;
            pass.DepthPassIndex = depthPassIndex;
            pass.GrassDepth = grassDepth;
            pass.Projection = cameraData.GetProjectionMatrix();
            pass.View = cameraData.GetViewMatrix();

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
                // The grass depth texture defines the projection/UV domain used by
                // the entire effect. It can differ from the camera color target.
                Matrix4x4 projection = GL.GetGPUProjectionMatrix(data.Projection,
                    context.GetTextureUVOrigin(data.GrassDepth) == TextureUVOrigin.BottomLeft);
                Matrix4x4 viewProjection = projection * data.View;
                for (int lod = 0; lod < 3; lod++)
                {
                    data.DrawProperties[lod].SetMatrix(ViewProjection, viewProjection);
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
            pass.Color = cameraColor;
            pass.Material = contactMaterial;
            pass.Parameters = parameters;
            pass.Limits = limits;
            pass.Projection = cameraData.GetProjectionMatrix();
            pass.View = cameraData.GetViewMatrix();
            pass.SetBackBufferViewport = resources.isActiveTargetBackBuffer;
            pass.Viewport = cameraData.camera.pixelRect;

            builder.UseTexture(pass.SceneDepth, AccessFlags.Read);
            builder.UseTexture(pass.GrassDepth, AccessFlags.Read);
            // Fixed-function multiplication preserves the existing color separately
            // for every MSAA sample. Resolving color and copying it to all samples
            // here would break its correspondence with depth before transparents.
            builder.SetRenderAttachment(cameraColor, 0, AccessFlags.ReadWrite);
            builder.SetRenderFunc(static (ContactPassData data, RasterGraphContext context) =>
            {
                TextureUVOrigin grassOrigin = context.GetTextureUVOrigin(data.GrassDepth);
                Matrix4x4 projection = GL.GetGPUProjectionMatrix(data.Projection,
                    grassOrigin == TextureUVOrigin.BottomLeft);
                if (!TryGetProjectionMatrices(projection, data.View, out Matrix4x4 viewProjection,
                        out Matrix4x4 inverseViewProjection, out Matrix4x4 inverseProjection))
                    return;

                // Intermediate camera textures already cover the camera viewport.
                // A direct backbuffer draw must preserve other camera rectangles.
                if (data.SetBackBufferViewport)
                    context.cmd.SetViewport(data.Viewport);

                // Blit.hlsl's full-screen vertex entry point, with a pooled property
                // block so queued cameras never share mutable material parameters.
                MaterialPropertyBlock properties = context.renderGraphPool.GetTempMaterialPropertyBlock();
                properties.SetTexture(SceneDepthTexture, data.SceneDepth);
                properties.SetTexture(GrassDepthTexture, data.GrassDepth);
                // Vertex UVs are normalized in the grass depth domain. Convert
                // destination origin -> grass origin, then grass -> scene origin
                // when sampling. RTHandle allocation scales are applied only to
                // samples, never to projection or world reconstruction.
                properties.SetVector(BlitScaleBias, GetUVScaleBias(Vector2.one,
                    grassOrigin != context.GetTextureUVOrigin(data.Color)));
                properties.SetVector(DepthScale, GetUVScaleBias(GetTextureScale(data.SceneDepth),
                    grassOrigin != context.GetTextureUVOrigin(data.SceneDepth)));
                properties.SetVector(GrassDepthScale, GetUVScaleBias(GetTextureScale(data.GrassDepth), false));
                properties.SetVector(ContactParameters, data.Parameters);
                properties.SetVector(ContactLimits, data.Limits);
                properties.SetMatrix(ViewProjection, viewProjection);
                properties.SetMatrix(InverseViewProjection, inverseViewProjection);
                properties.SetMatrix(InverseProjection, inverseProjection);
                properties.SetMatrix(ViewMatrix, data.View);
                context.cmd.DrawProcedural(Matrix4x4.identity, data.Material, 0,
                    MeshTopology.Triangles, 3, 1, properties);
            });
        }
    }

    /// <summary>Release this renderer feature's cached resources. Safe to call repeatedly.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CoreUtils.Destroy(_contactMaterial);
        _contactMaterial = null;
    }

    /// <summary>
    /// Validates serialized settings and builds the shader parameters. The caster
    /// radius contains every point reached from an in-range receiver, including
    /// the initial ray bias. Invalid values never reach GPU projection or clipping.
    /// </summary>
    public static bool TryGetParameters(Settings settings, out Vector4 parameters,
        out Vector4 limits, out float casterDistance)
    {
        parameters = default;
        limits = default;
        casterDistance = 0f;
        if (settings == null || !settings.enabled ||
            !IsFinite(settings.strength) || !IsFinite(settings.rayLength) ||
            !IsFinite(settings.bias) || !IsFinite(settings.thickness) ||
            !IsFinite(settings.maxDistance) || settings.strength <= 0f ||
            settings.rayLength <= 0f || settings.maxDistance <= 0f)
            return false;

        parameters = new Vector4(Mathf.Clamp01(settings.strength),
            Mathf.Max(0.01f, settings.rayLength), Mathf.Max(0.001f, settings.bias),
            Mathf.Max(0.001f, settings.thickness));
        limits = new Vector4(Mathf.Max(0.1f, settings.maxDistance),
            Mathf.Clamp(settings.steps, 4, 32), 0.025f, 0f);
        casterDistance = limits.x + parameters.y + parameters.z;
        // The depth shader compares squared distances, so guard the square too.
        if (!IsFinite(casterDistance) || !IsFinite(casterDistance * casterDistance))
        {
            parameters = default;
            limits = default;
            casterDistance = 0f;
            return false;
        }
        return true;
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    /// <summary>
    /// Converts a normalized viewport UV into a texture allocation's UV. Origin
    /// inversion takes place within the populated viewport, before its scale.
    /// </summary>
    public static Vector4 GetUVScaleBias(Vector2 scale, bool flipY) => flipY
        ? new Vector4(scale.x, -scale.y, 0f, scale.y)
        : new Vector4(scale.x, scale.y, 0f, 0f);

    /// <summary>
    /// Builds matching projection and reconstruction matrices. gpuProjection
    /// must already use the depth texture's GPU clip range and UV orientation.
    /// Works with perspective, orthographic, asymmetric and oblique projections.
    /// </summary>
    public static bool TryGetProjectionMatrices(Matrix4x4 gpuProjection, Matrix4x4 view,
        out Matrix4x4 viewProjection, out Matrix4x4 inverseViewProjection,
        out Matrix4x4 inverseProjection)
    {
        viewProjection = gpuProjection * view;
        inverseViewProjection = viewProjection.inverse;
        inverseProjection = gpuProjection.inverse;
        if (viewProjection.determinant == 0f || gpuProjection.determinant == 0f)
            return false;
        for (int i = 0; i < 16; i++)
            if (!IsFinite(viewProjection[i]) || !IsFinite(inverseViewProjection[i]) ||
                !IsFinite(inverseProjection[i]))
                return false;
        return true;
    }

    private static Vector2 GetTextureScale(TextureHandle texture)
    {
        RTHandle handle = texture;
        if (!handle.useScaling)
            return Vector2.one;

        Vector4 scale = handle.rtHandleProperties.rtHandleScale;
        return new Vector2(scale.x, scale.y);
    }

    private bool TryGetFormats(GraphicsFormat cameraColorFormat)
    {
        if (!_formatsChecked)
        {
            _formatsChecked = true;
            // Preserve raw depth precision. RGBA32F is a compatible fallback on
            // devices that cannot both render and sample an RG32F texture.
            _depthCoverageFormat = SupportsDepthCoverageFormat(GraphicsFormat.R32G32_SFloat)
                ? GraphicsFormat.R32G32_SFloat
                : SupportsDepthCoverageFormat(GraphicsFormat.R32G32B32A32_SFloat)
                    ? GraphicsFormat.R32G32B32A32_SFloat : GraphicsFormat.None;
            _depthAttachmentFormat = GraphicsFormat.D32_SFloat;
            if (!SystemInfo.IsFormatSupported(_depthAttachmentFormat, GraphicsFormatUsage.Render))
                _depthAttachmentFormat = SystemInfo.GetGraphicsFormat(DefaultFormat.DepthStencil);
            if (!GraphicsFormatUtility.IsDepthFormat(_depthAttachmentFormat) ||
                !SystemInfo.IsFormatSupported(_depthAttachmentFormat, GraphicsFormatUsage.Render))
                _depthAttachmentFormat = GraphicsFormat.None;
        }

        bool supported = _depthCoverageFormat != GraphicsFormat.None &&
            _depthAttachmentFormat != GraphicsFormat.None &&
            cameraColorFormat != GraphicsFormat.None &&
            SystemInfo.IsFormatSupported(cameraColorFormat, GraphicsFormatUsage.Blend);
        if (!supported && !_warnedUnsupportedFormats)
        {
            _warnedUnsupportedFormats = true;
            Debug.LogWarning("Grass contact shadows require a sampleable 32-bit floating-point color target, a supported depth target, and a camera color format that supports blending.");
        }
        return supported;
    }

    private static bool SupportsDepthCoverageFormat(GraphicsFormat format) =>
        SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Render) &&
        SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Sample);

    private bool TryGetMaterial(out Material material)
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
        public TextureHandle GrassDepth;
        public Matrix4x4 Projection;
        public Matrix4x4 View;
    }

    private sealed class ContactPassData
    {
        public TextureHandle SceneDepth;
        public TextureHandle GrassDepth;
        public TextureHandle Color;
        public Material Material;
        public Vector4 Parameters;
        public Vector4 Limits;
        public Matrix4x4 Projection;
        public Matrix4x4 View;
        public bool SetBackBufferViewport;
        public Rect Viewport;
    }
}
