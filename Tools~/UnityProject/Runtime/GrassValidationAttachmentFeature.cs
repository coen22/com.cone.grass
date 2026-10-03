using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>Observes the grass camera's bound targets only during an opted-in validation run.</summary>
public sealed class GrassValidationAttachmentFeature : ScriptableRendererFeature
{
    private AttachmentPass pass;

    public override void Create()
    {
        // The generated renderer inserts this feature immediately after grass.
        // Use the same event, without requesting a camera texture or a blit.
        pass = new AttachmentPass { renderPassEvent = RenderPassEvent.BeforeRenderingTransparents };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (renderingData.cameraData.camera.TryGetComponent(out GrassValidationPlayerProbe probe) && probe.IsRunning)
            renderer.EnqueuePass(pass);
    }

    private sealed class AttachmentPass : ScriptableRenderPass
    {
        private sealed class PassData
        {
            public GrassValidationPlayerProbe Probe;
            public int Stage, Frame, RequestedSamples, SupportedSamples, CameraDescriptorSamples;
            public string Antialiasing;
            public bool Backbuffer;
            public TextureHandle Color, Depth;
            public RenderTargetInfo ColorInfo, DepthInfo;
        }

        public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
        {
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalResourceData resources = frameData.Get<UniversalResourceData>();
            if (!cameraData.camera.TryGetComponent(out GrassValidationPlayerProbe probe) || !probe.IsRunning ||
                !resources.activeColorTexture.IsValid() || !resources.activeDepthTexture.IsValid())
                return;

            RenderTargetInfo color = graph.GetRenderTargetInfo(resources.activeColorTexture);
            RenderTargetInfo depth = graph.GetRenderTargetInfo(resources.activeDepthTexture);
            // Ask the driver about the actual paired formats, not a generic ARGB32
            // texture. A supported request that renders at another count fails.
            var descriptor = new RenderTextureDescriptor(color.width, color.height)
            {
                graphicsFormat = color.format, depthStencilFormat = depth.format,
                msaaSamples = probe.RequestedMsaa, dimension = TextureDimension.Tex2D, volumeDepth = 1
            };
            int supported = SystemInfo.GetRenderTextureSupportedMSAASampleCount(descriptor);
            using (var builder = graph.AddRasterRenderPass<PassData>("Grass validation attachment samples", out var data))
            {
                data.Probe = probe;
                data.Stage = probe.ActiveStage;
                data.Frame = Time.frameCount;
                data.RequestedSamples = probe.RequestedMsaa;
                data.SupportedSamples = supported;
                data.CameraDescriptorSamples = cameraData.cameraTargetDescriptor.msaaSamples;
                data.Antialiasing = cameraData.antialiasing.ToString();
                data.Backbuffer = resources.isActiveTargetBackBuffer;
                data.Color = resources.activeColorTexture;
                data.Depth = resources.activeDepthTexture;
                data.ColorInfo = color;
                data.DepthInfo = depth;
                // Bind the same targets as GrassForward. Declaring color as
                // ReadWrite preserves external screen output if this observer
                // becomes a separate native pass; a final read-only attachment
                // can otherwise receive a DontCare store action. No draw,
                // sampled texture read or forced intermediate target is added.
                builder.SetRenderAttachment(data.Color, 0, AccessFlags.ReadWrite);
                builder.SetRenderAttachmentDepth(data.Depth, AccessFlags.Read);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc((PassData sample, RasterGraphContext context) =>
                {
                    RTHandle colorHandle = sample.Color;
                    RTHandle depthHandle = sample.Depth;
                    RenderTexture colorTexture = colorHandle.rt;
                    RenderTexture depthTexture = depthHandle.rt;
                    sample.Probe.RecordAttachments(sample.Stage, sample.Frame,
                        sample.RequestedSamples, sample.SupportedSamples, sample.CameraDescriptorSamples,
                        colorTexture ? colorTexture.antiAliasing : sample.ColorInfo.msaaSamples,
                        depthTexture ? depthTexture.antiAliasing : sample.DepthInfo.msaaSamples,
                        sample.ColorInfo.width, sample.ColorInfo.height,
                        sample.DepthInfo.width, sample.DepthInfo.height,
                        sample.ColorInfo.format.ToString(), sample.DepthInfo.format.ToString(),
                        colorTexture ? "allocated RenderTexture" : "imported target metadata",
                        depthTexture ? "allocated RenderTexture" : "imported target metadata",
                        sample.Backbuffer, sample.Antialiasing);
                });
            }
        }
    }
}
