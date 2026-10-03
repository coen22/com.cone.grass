Shader "InfiniteGrass/Modifiers/GrassMaskShader"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Opacity ("Opacity", Range(0,1)) = 1
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent" }
        Pass
        {
            Name "GrassMask"
            Tags { "LightMode"="GrassMask" }
            ZWrite Off
            ZTest Always
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex GrassCaptureVertex
            #pragma fragment Fragment
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float _Opacity;
            CBUFFER_END
            #include "GrassCaptureCommon.hlsl"
            half4 Fragment(GrassCaptureVaryings input) : SV_Target
            {
                half4 source = GRASS_SAMPLE_CAPTURE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
                half coverage = saturate(source.r * source.a * input.color.a * _Opacity);
                // White RGB with coverage in alpha avoids applying opacity twice.
                return half4(1, 1, 1, coverage);
            }
            ENDHLSL
        }
    }
}
