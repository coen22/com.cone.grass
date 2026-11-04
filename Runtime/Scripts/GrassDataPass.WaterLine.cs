using UnityEngine;
using UnityEngine.Rendering;

namespace Cone.Grass
{
    public partial class GrassDataRendererFeature
    {
        // The water line (InfiniteGrassRenderer.WaterLine) as the placement kernel's GrassWaterParameters.
        private sealed partial class GrassDataPass
        {
            private struct WaterLineData
            {
                private static readonly int s_level = Shader.PropertyToID("_GrassWaterLevel");
                private static readonly int s_shoreFade = Shader.PropertyToID("_GrassWaterShoreFade");
                private static readonly int s_enabled = Shader.PropertyToID("_GrassWaterEnabled");

                public bool Enabled;
                public float Level, ShoreFade;

                public static WaterLineData From(InfiniteGrassRenderer owner)
                {
                    var data = new WaterLineData();
                    data.Enabled = owner.WaterLine.TryResolve(out data.Level, out data.ShoreFade);
                    return data;
                }

                public void Apply(ComputeCommandBuffer cmd, ComputeShader shader)
                {
                    cmd.SetComputeIntParam(shader, s_enabled, Enabled ? 1 : 0);
                    cmd.SetComputeFloatParam(shader, s_level, Enabled ? Level : 0f);
                    cmd.SetComputeFloatParam(shader, s_shoreFade, Enabled ? ShoreFade : 0f);
                }
            }
        }
    }
}
