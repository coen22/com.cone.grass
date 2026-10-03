Shader "Hidden/InfiniteGrass/MotionCopy"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "CopyDeformation"
            Cull Off
            ZWrite Off
            ZTest Always
            Blend Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment CopyDeformation
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            TEXTURE2D(_GrassHistorySource);
            float4 CopyDeformation(Varyings input) : SV_Target
            {
                // Copy texel centers in sampled (linear) space, including sRGB wind
                // assets, into an uncompressed linear history texture.
                return SAMPLE_TEXTURE2D_LOD(_GrassHistorySource, sampler_PointClamp, input.texcoord, 0);
            }
            ENDHLSL
        }
    }
}
