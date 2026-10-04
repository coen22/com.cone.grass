Shader "InfiniteGrass/Modifiers/GrassInteractor"
{
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
            #pragma vertex Vertex
            #pragma fragment Fragment
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            float4x4 _GrassCaptureVP;

            struct Attributes
            {
                float3 positionOS : POSITION;
                float4 color : COLOR;
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color : TEXCOORD0;
            };
            Varyings Vertex(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS);
                output.positionCS = mul(_GrassCaptureVP, float4(positionWS, 1));
                // Match the existing interaction capture's XZ projection:
                // supporting surface height does not clip the field mesh.
                output.positionCS.z = 0.5 * output.positionCS.w;
                output.color = input.color;
                return output;
            }
            float4 Fragment(Varyings input) : SV_Target
            {
                // RGB was premultiplied by each node's recovery strength before
                // interpolation. Multiplying here again would corrupt the decoded
                // bend direction when neighboring nodes have different ages.
                return input.color;
            }
            ENDHLSL
        }
    }
}
