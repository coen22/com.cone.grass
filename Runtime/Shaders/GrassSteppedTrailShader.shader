Shader "InfiniteGrass/Modifiers/GrassSteppedTrailShader"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Strength ("Strength", Range(0,1)) = 0.85
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
                float _Strength;
            CBUFFER_END
            #include "GrassCaptureCommon.hlsl"
            half4 Fragment(GrassCaptureVaryings input) : SV_Target
            {
                half4 source = GRASS_SAMPLE_CAPTURE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
                float2 direction = -input.tangentWS.xz;
                float directionLength = length(direction);
                direction = directionLength > 0.0001 ? direction / directionLength : float2(0,0);
                half strength = saturate(source.r * source.a * input.color.a * _Strength);
                return half4((direction * 0.5 + 0.5) * strength, 0, strength);
            }
            ENDHLSL
        }
    }
}
