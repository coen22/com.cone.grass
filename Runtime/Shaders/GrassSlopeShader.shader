Shader "InfiniteGrass/Modifiers/GrassSlopeShader"
{
    Properties
    {
        [MainTexture] _MainTex ("Texture", 2D) = "white" {}
        [MainColor] _BaseColor ("Base Color", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent" }
        Pass
        {
            Name "GrassSlope"
            Tags { "LightMode"="GrassSlope" }
            ZWrite Off
            ZTest Always
            Cull Off
            Blend One OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex GrassCaptureVertex
            #pragma fragment Fragment
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4 _BaseColor;
            CBUFFER_END
            #include "GrassCaptureCommon.hlsl"
            half4 Fragment(GrassCaptureVaryings input) : SV_Target
            {
                half4 source = GRASS_SAMPLE_CAPTURE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv) * input.color * _BaseColor;
                half coverage = saturate(source.a);
                return half4(source.rgb * coverage, coverage);
            }
            ENDHLSL
        }
    }
}
