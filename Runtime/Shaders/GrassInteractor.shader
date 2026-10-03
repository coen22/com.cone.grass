Shader "InfiniteGrass/Modifiers/GrassInteractor"
{
    Properties { _MainTex ("Capture contract", 2D) = "white" {} }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" }
        Pass
        {
            Name "GrassSlope"
            Tags { "LightMode"="GrassSlope" }
            ZWrite Off ZTest Always Cull Off
            Blend One OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex GrassCaptureVertex
            #pragma fragment Fragment
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
            CBUFFER_END
            #include "GrassCaptureCommon.hlsl"
            half4 Fragment(GrassCaptureVaryings input) : SV_Target
            {
                float2 radial = input.uv * 2.0 - 1.0;
                float radius = length(radial);
                half weight = (1.0 - smoothstep(0.25, 1.0, radius)) * input.color.a;
                float2 direction = radial + input.tangentWS.xz * 0.65;
                float magnitude = length(direction);
                direction = magnitude > 0.0001 ? direction / magnitude : float2(0, 0);
                return half4((direction * 0.5 + 0.5) * weight, 0, weight);
            }
            ENDHLSL
        }
    }
}
