Shader "InfiniteGrass/GrassHeightMapShader"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" }
        Pass
        {
            Name "GrassHeight"
            Tags { "LightMode"="GrassHeight" }
            Cull Off
            ZWrite On
            ZTest LEqual
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vertex
            #pragma fragment Fragment
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            float4x4 _GrassCaptureVP;
            struct Attributes
            {
                float3 positionOS : POSITION;
                half4 color : COLOR;
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 heightAndValidity : TEXCOORD0;
            };
            Varyings Vertex(Attributes input)
            {
                Varyings output;
                float3 world = TransformObjectToWorld(input.positionOS);
                output.positionCS = mul(_GrassCaptureVP, float4(world, 1));
                // World-space height makes the cached map independent of camera altitude.
                // Preserve fractional red vertex-color eligibility for legacy surfaces.
                output.heightAndValidity = float2(world.y, saturate(input.color.r));
                return output;
            }
            float2 Fragment(Varyings input) : SV_Target
            {
                return input.heightAndValidity;
            }
            ENDHLSL
        }
    }
}
