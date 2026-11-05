using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Cone.Grass
{
    /// <summary>
    /// Draws <see cref="GrassMeshDetails"/> mesh kinds for the cameras of this URP renderer: a scatter and cull pass before
    /// the shadow maps and a Hi-Z pass after the opaques. The meshes themselves are drawn by URP's own passes through a
    /// BatchRendererGroup, so they appear in the depth prepass, the normals, the shadow cascades and forward or deferred
    /// lighting like any renderer.
    /// </summary>
    [DisallowMultipleRendererFeature("Grass Mesh Details")]
    public sealed class GrassMeshDetailFeature : ScriptableRendererFeature
    {
        [Tooltip("The scatter and cull compute shader; empty uses the package's GrassMeshDetails."), SerializeField]
        private ComputeShader _computeShader;

        private GrassMeshDetailRenderer _renderer;
        private GrassMeshDetailRenderer.CullPass _cullPass;
        private GrassMeshDetailRenderer.HiZPass _hiZPass;
        private bool _warnedUnsupported;

        /// <summary>The compute shader in use.</summary>
        public ComputeShader ComputeShader => _computeShader ? _computeShader : Resources.Load<ComputeShader>("GrassMeshDetails");

        /// <inheritdoc/>
        public override void Create()
        {
            _renderer?.Dispose();
            _renderer = new GrassMeshDetailRenderer(ComputeShader);
            _cullPass = new GrassMeshDetailRenderer.CullPass(_renderer);
            _hiZPass = new GrassMeshDetailRenderer.HiZPass(_renderer);
        }

        /// <inheritdoc/>
        public override void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData)
        {
            if (_renderer == null)
            {
                return;
            }

            bool eligible = isActive && renderer is UniversalRenderer && cameraData.renderType == CameraRenderType.Base &&
                            !cameraData.xr.enabled;
            if (eligible && !_renderer.IsSupported && !_warnedUnsupported && GrassMeshDetails.Active.Count > 0)
            {
                _warnedUnsupported = true;
                Debug.LogWarning("Grass mesh details need compute shaders, indirect draws and BatchRendererGroup raw buffers; " +
                    "this graphics device has not got them, so no mesh kinds are drawn.", this);
            }

            _renderer.PreCull(cameraData.camera, eligible);
        }

        /// <inheritdoc/>
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_renderer == null || !_renderer.TryGetPrepared(renderingData.cameraData.camera, out _))
            {
                return;
            }

            renderer.EnqueuePass(_cullPass);
            if (GrassMeshDetailRenderer.OcclusionWanted(_renderer.CurrentLayout))
            {
                _hiZPass.ConfigureInput(ScriptableRenderPassInput.Depth);
                renderer.EnqueuePass(_hiZPass);
            }
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            _renderer?.Dispose();
            _renderer = null;
        }
    }
}
