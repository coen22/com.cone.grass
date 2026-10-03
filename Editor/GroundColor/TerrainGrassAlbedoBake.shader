Shader "Hidden/InfiniteGrass/Editor/TerrainAlbedoBake"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "TerrainAlbedo"
            Cull Off
            ZWrite Off
            ZTest Always
            Blend One One
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex BakeVertex
            #pragma fragment BakeFragment
            #pragma multi_compile_local _ _TERRAIN_BLEND_HEIGHT
            #pragma multi_compile_local _ _MASKMAP

            // Consume the installed URP implementation. Its mask remapping,
            // opacity-as-density and height blending must stay in step with the
            // terrain being matched. This intentionally captures unlit albedo.
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            // Persistent output uses the bake resolution's derivatives. The last
            // camera's upscaler mip bias must not affect source texture sampling.
            #if defined(PLATFORM_SAMPLE_TEXTURE2D)
                #undef SAMPLE_TEXTURE2D
                #define SAMPLE_TEXTURE2D(textureName, samplerName, uv) \
                    PLATFORM_SAMPLE_TEXTURE2D(textureName, samplerName, uv)
            #endif
            #include "Packages/com.unity.render-pipelines.universal/Shaders/Terrain/TerrainLitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/Terrain/TerrainLitPasses.hlsl"
            float _BakeAdditionalGroup;

            struct BakeVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            BakeVaryings BakeVertex(uint vertexID : SV_VertexID)
            {
                BakeVaryings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                output.uv = GetFullScreenTriangleTexCoord(vertexID);
                return output;
            }

            half4 BakeFragment(BakeVaryings input) : SV_Target
            {
                float4 uvMainAndLM = float4(input.uv, 0, 0);
                float4 uv01 = float4(TRANSFORM_TEX(input.uv, _Splat0), TRANSFORM_TEX(input.uv, _Splat1));
                float4 uv23 = float4(TRANSFORM_TEX(input.uv, _Splat2), TRANSFORM_TEX(input.uv, _Splat3));
                float2 controlUV = (input.uv * (_Control_TexelSize.zw - 1) + 0.5) * _Control_TexelSize.xy;
                half4 control = SAMPLE_TEXTURE2D(_Control, sampler_Control, controlUV);
                // Native TerrainLit restores this original group weight after
                // normalizing height/density weights. Preserve it before mixing.
                half groupWeight = dot(control, half4(1, 1, 1, 1));
                // Native TerrainLit's additional passes discard negligible groups.
                if (_BakeAdditionalGroup > 0.5)
                    clip(groupWeight <= 0.005h ? -1.0h : 1.0h);
                half4 masks[4];
                ComputeMasks(masks, half4(_LayerHasMask0, _LayerHasMask1, _LayerHasMask2, _LayerHasMask3), uv01, uv23);
                #if defined(_TERRAIN_BLEND_HEIGHT)
                    if (_NumLayersCount <= 4)
                        HeightBasedSplatModify(control, masks);
                #endif
                half weight;
                half4 diffuse;
                half4 unusedSmoothness;
                half3 unusedNormal = half3(0, 0, 1);
                SplatmapMix(uvMainAndLM, uv01, uv23, control, weight, diffuse, unusedSmoothness, unusedNormal);
                return half4(diffuse.rgb * groupWeight, groupWeight);
            }
            ENDHLSL
        }
    }
}
