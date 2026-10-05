Shader "Hidden/InfiniteGrass/Placement"
{
    Properties
    {
        // Retain the texture property even when only its sampler is used. URP
        // shares the first diffuse sampler within each four-layer terrain group.
        [HideInInspector] _PlacementGroundLayerSamplerTexture ("Terrain layer sampler source", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Cull Off
        ZWrite Off
        ZTest Always

        HLSLINCLUDE
        #include "../Shaders/GrassCaptureSampling.hlsl"
        float4x4 _GrassCaptureVP;
        float4x4 _PlacementWorldToMask;
        float4x4 _PlacementGroundWorldToMask;
        float4 _PlacementTerrainRect;
        int _PlacementHasTerrain;
        int _PlacementShape;
        float _PlacementDensity;
        float _PlacementEdgeFalloff;
        float4 _PlacementGroundTint;
        float _PlacementGroundStrength;
        float4 _PlacementGroundLayerUV;
        float4 _PlacementGroundRemapMin;
        float4 _PlacementGroundRemapMax;
        int _PlacementHasGroundColor;
        int _PlacementGroundColorUsesTerrainBounds;
        int _PlacementHasGroundLayer;
        int _PlacementGroundLayerIsSampler;
        TEXTURE2D(_PlacementDensityTexture);
        TEXTURE2D(_PlacementGroundColorTexture);
        TEXTURE2D(_PlacementGroundLayerTexture);
        TEXTURE2D(_PlacementGroundLayerSamplerTexture);
        SAMPLER(sampler_PlacementGroundLayerSamplerTexture);
        TEXTURE2D(_PlacementTerrainHeightmap);
        TEXTURE2D(_PlacementTerrainHoles);
        float4 _PlacementTerrainHeightmap_TexelSize;
        float _PlacementTerrainOriginY;
        float _PlacementTerrainHeight;
        int _PlacementTerrainHasHoles;
        // Core.hlsl supplies the shared clamp/repeat samplers through GlobalSamplers.hlsl.

        struct Attributes { float3 positionOS : POSITION; };
        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            float2 uv : TEXCOORD1;
        };
        Varyings Vertex(Attributes input)
        {
            Varyings output;
            output.positionWS = TransformObjectToWorld(input.positionOS);
            output.positionCS = mul(_GrassCaptureVP, float4(output.positionWS, 1));
            // Placement is an XZ projection. The area's transform Y must not clip it.
            output.positionCS.z = 0.5 * output.positionCS.w;
            output.uv = mul(_PlacementWorldToMask, float4(output.positionWS, 1)).xz;
            return output;
        }

        void ClipTerrain(float2 worldXZ)
        {
            if (_PlacementHasTerrain != 0)
            {
                float2 terrainUV = (worldXZ - _PlacementTerrainRect.xy) /
                    max(_PlacementTerrainRect.zw, float2(0.001, 0.001));
                clip(min(min(terrainUV.x, terrainUV.y), min(1-terrainUV.x, 1-terrainUV.y)));
            }
        }

        float Coverage(Varyings input)
        {
            ClipTerrain(input.positionWS.xz);
            float2 uv = input.uv;
            clip(min(min(uv.x, uv.y), min(1-uv.x, 1-uv.y)));
            float value = 1;
            if (_PlacementShape == 2)
                value = GRASS_SAMPLE_CAPTURE_TEXTURE2D(_PlacementDensityTexture, sampler_LinearClamp, uv).r;
            else
            {
                float2 centered = abs(uv * 2 - 1);
                float radius = _PlacementShape == 1 ? length(centered) : max(centered.x, centered.y);
                value = _PlacementEdgeFalloff > 0.0001 ?
                    smoothstep(0, _PlacementEdgeFalloff, 1-radius) : step(radius, 1);
            }
            return saturate(value * _PlacementDensity);
        }
        ENDHLSL

        Pass
        {
            Name "PlacementDensity"
            Blend One One
            BlendOp Max
            ColorMask R
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vertex
            #pragma fragment FragmentDensity
            float4 FragmentDensity(Varyings input) : SV_Target
            {
                return Coverage(input).xxxx;
            }
            ENDHLSL
        }

        Pass
        {
            Name "PlacementGroundColor"
            Blend One OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vertex
            #pragma fragment FragmentGround
            float4 FragmentGround(Varyings input) : SV_Target
            {
                float coverage = Coverage(input);
                clip(coverage > 0 ? 1 : -1);
                // The complete TerrainLit color already includes the spline's splat transition.
                // Sparse surviving roots still need the full requested match to that ground color.
                float matchWeight = _PlacementHasGroundColor != 0 && _PlacementGroundColorUsesTerrainBounds != 0
                    ? 1 : coverage;
                float alpha = matchWeight * saturate(_PlacementGroundStrength);
                clip(alpha - 0.00001);
                float3 albedo = 1;
                if (_PlacementHasGroundColor != 0)
                {
                    float2 groundUV = mul(_PlacementGroundWorldToMask, float4(input.positionWS, 1)).xz;
                    albedo = GRASS_SAMPLE_CAPTURE_TEXTURE2D(_PlacementGroundColorTexture, sampler_LinearClamp, groundUV).rgb;
                }
                else if (_PlacementHasGroundLayer != 0)
                {
                    float2 layerUV = input.positionWS.xz * _PlacementGroundLayerUV.xy + _PlacementGroundLayerUV.zw;
                    // Match native TerrainLit's sampler source, including per-axis
                    // addressing and filtering, rather than forcing linear repeat.
                    // Sample the group-first texture directly when it is also the layer.
                    // This keeps the sampler source in the compiled resource table;
                    // Unity cannot bind a sampler for a texture optimized out of the shader.
                    if (_PlacementGroundLayerIsSampler != 0)
                        albedo = GRASS_SAMPLE_CAPTURE_TEXTURE2D(_PlacementGroundLayerSamplerTexture,
                            sampler_PlacementGroundLayerSamplerTexture, layerUV).rgb;
                    else
                        albedo = GRASS_SAMPLE_CAPTURE_TEXTURE2D(_PlacementGroundLayerTexture,
                            sampler_PlacementGroundLayerSamplerTexture, layerUV).rgb;
                    // URP TerrainLit applies diffuse remap scale (max-min), without adding min.
                    albedo *= _PlacementGroundRemapMax.rgb - _PlacementGroundRemapMin.rgb;
                }
                albedo *= _PlacementGroundTint.rgb;
                // Premultiplied storage keeps filtered area boundaries free of black halos.
                // Blade shading unpremultiplies RGB before applying this alpha as blend strength.
                return float4(albedo * alpha, alpha);
            }
            ENDHLSL
        }

        Pass
        {
            Name "TerrainHeight"
            ZWrite On
            ZTest LEqual
            Blend Off
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vertex
            #pragma fragment FragmentTerrainHeight
            struct HeightOutput
            {
                float2 heightAndValidity : SV_Target;
                float depth : SV_Depth;
            };
            HeightOutput FragmentTerrainHeight(Varyings input)
            {
                ClipTerrain(input.positionWS.xz);
                float2 terrainUV = (input.positionWS.xz - _PlacementTerrainRect.xy) /
                    max(_PlacementTerrainRect.zw, float2(0.001, 0.001));
                if (_PlacementTerrainHasHoles != 0)
                    clip(SAMPLE_TEXTURE2D_LOD(_PlacementTerrainHoles, sampler_PointClamp, terrainUV, 0).r - 0.0005);
                float2 resolution = max(_PlacementTerrainHeightmap_TexelSize.zw, float2(1,1));
                float2 heightUV = (terrainUV * (resolution - 1) + 0.5) / resolution;
                // Height data must not inherit the viewing camera's global mip bias.
                float height = UnpackHeightmap(SAMPLE_TEXTURE2D_LOD(_PlacementTerrainHeightmap, sampler_LinearClamp, heightUV, 0));
                float worldY = _PlacementTerrainOriginY + height * _PlacementTerrainHeight * (65535.0 / 32766.0);
                float4 positionCS = mul(_GrassCaptureVP, float4(input.positionWS.x, worldY, input.positionWS.z, 1));
                HeightOutput output;
                output.heightAndValidity = float2(worldY, 1);
                output.depth = positionCS.z / positionCS.w;
                #if !UNITY_REVERSED_Z
                    output.depth = (output.depth - UNITY_NEAR_CLIP_VALUE) / (1.0 - UNITY_NEAR_CLIP_VALUE);
                #endif
                return output;
            }
            ENDHLSL
        }
    }
}
