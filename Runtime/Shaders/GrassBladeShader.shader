Shader "InfiniteGrass/GrassBladeShader"
{
    Properties
    {
        _Color("Color", Color) = (0,0,0,1)
        _AOColor("AO Color", Color) = (0.5,0.5,0.5,1)

        [Header(Grass Shape)][Space]
        _GrassWidth("Grass Width", Float) = 1
        _GrassHeight("Grass Height", Float) = 1
        _GrassWidthRandomness("Grass Width Randomness", Range(0, 1)) = 0.25
        _GrassHeightRandomness("Grass Height Randomness", Range(0, 1)) = 0.5
        _GrassCurving("Grass Curving", Float) = 0.1
        [Space]
        _ExpandDistantGrassWidth("Expand Distant Grass Width", Float) = 1
        _ExpandDistantGrassRange("Expand Distant Grass Range", Vector) = (50, 200, 0, 0)

        [Header(Wind)][Space]
        _WindTexture("Wind Texture", 2D) = "white" {}
        _WindScroll("Wind Scroll", Vector) = (1, 1, 0, 0)
        _WindStrength("Wind Strength", Float) = 1

        [Header(Ground Blending)][Space]
        _GroundBlendStrength("Ground Color Blend", Range(0, 1)) = 1
        _GroundBlendHeight("Ground Blend Height (Blade Fraction)", Range(0, 1)) = 0.25
        [Toggle(_GRASS_GROUND_NORMAL)] _UseGroundNormal("Match Ground Normal at Roots", Float) = 0

        [Header(Lighting)][Space]
        _RandomNormal("Random Normal", Range(0, 1)) = 0.1
        [Toggle(_GRASS_ADDITIONAL_LIGHTS)] _UseAdditionalLights("Forward+ Additional Lights", Float) = 0
        _SpecularFadeStart("Specular Fade Start (Metres)", Float) = 20
        _SpecularFadeEnd("Specular Fade End (Metres)", Float) = 80

        [Header(Antialiasing)][Space]
        _MinimumPixelWidth("Minimum Blade Width (Pixels)", Range(0, 3)) = 1
        [HideInInspector] _GrassAlphaToCoverage("Camera Uses MSAA", Float) = 0

        // Retained so existing materials keep their density and shape settings.
        // Geometry LOD now selects separate meshes in the renderer.
        [HideInInspector] _MaxSubdivision("Max Subdivision", Float) = 5
        [HideInInspector] _SubdivisionDistance("Subdivision Distance", Float) = 100
        [HideInInspector] _SubdivisionHeightBoost("Subdivision Height Boost", Float) = 0
        [HideInInspector] _SubdivisionBumpWidth("Subdivision Bump Width", Float) = 20
        [HideInInspector] _FullDensityDistance("Full Density Distance", Float) = 30
        [HideInInspector] _DensityFalloffExponent("Density Falloff Exponent", Float) = 4
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "GrassForward"
            Tags { "LightMode" = "UniversalForwardOnly" }
            Cull Back
            ZWrite On
            ZTest LEqual
            Blend Off
            AlphaToMask [_GrassAlphaToCoverage]

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex GrassForwardVertex
            #pragma fragment GrassForwardFragment

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog
            #pragma shader_feature_local_fragment _GRASS_ADDITIONAL_LIGHTS
            #pragma shader_feature_local _GRASS_GROUND_NORMAL

            #include "GrassBladeCommon.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            half3 GrassAmbientLight(half3 normalWS)
            {
                // Explicit scene ambient probe: these indirect commands do not
                // populate the per-object unity_SH* values used by SampleSH.
                half3 ambient = SHEvalLinearL0L1(normalWS, _GrassSHAr, _GrassSHAg, _GrassSHAb);
                ambient += SHEvalLinearL2(normalWS, _GrassSHBr, _GrassSHBg, _GrassSHBb, _GrassSHC);
                #if defined(UNITY_COLORSPACE_GAMMA)
                    ambient = LinearToSRGB(ambient);
                #endif
                return max(half3(0, 0, 0), ambient);
            }

            struct GrassForwardVaryings
            {
                float4 positionCS : SV_POSITION;
                float4 positionWSAndFog : TEXCOORD0;
                half4 normalWSAndSpecular : TEXCOORD1;
                half3 grassAlbedo : TEXCOORD2;
                float2 shapeCoordinates : TEXCOORD3;
                nointerpolation float coverage : TEXCOORD4;
                nointerpolation uint seed : TEXCOORD5;
                nointerpolation half4 groundColor : TEXCOORD6;
                #if defined(_GRASS_GROUND_NORMAL)
                    nointerpolation half3 groundNormal : TEXCOORD7;
                #endif
            };

            GrassForwardVaryings GrassForwardVertex(GrassAttributes input, uint instanceID : SV_InstanceID)
            {
                GrassVertexData blade = BuildGrassVertex(input, instanceID);
                GrassForwardVaryings output = (GrassForwardVaryings)0;
                output.positionCS = TransformWorldToHClip(blade.positionWS);
                output.positionWSAndFog = float4(blade.positionWS, ComputeFogFactor(output.positionCS.z));
                output.shapeCoordinates = blade.shapeCoordinates;
                output.coverage = blade.coverage;
                output.seed = blade.seed;

                half3 rootAlbedo = lerp(_AOColor.rgb, _Color.rgb, blade.height);
                half3 albedo = lerp(_Color.rgb, rootAlbedo, GrassDistanceDensity(blade.cameraDistance));
                half4 colorModifier = SAMPLE_TEXTURE2D_LOD(_GrassColorRT, sampler_GrassColorRT, blade.mapUV, 0);
                // Modifier captures also store premultiplied RGB over a clear target.
                colorModifier.rgb /= max(colorModifier.a, 0.0001h);
                output.grassAlbedo = lerp(albedo, colorModifier.rgb, saturate(colorModifier.a));
                output.groundColor = SAMPLE_TEXTURE2D_LOD(_GrassGroundColorRT, sampler_GrassGroundColorRT, blade.mapUV, 0);
                // Area captures use premultiplied over blending. Recover the albedo
                // before applying its accumulated strength, avoiding dark soft edges.
                output.groundColor.rgb /= max(output.groundColor.a, 0.0001h);

                float specularFade = 1.0 - smoothstep(_SpecularFadeStart,
                    max(_SpecularFadeStart + 0.001, _SpecularFadeEnd), blade.cameraDistance);
                half specular = blade.height * 0.12h * (1.0h - saturate(colorModifier.a)) * specularFade;
                output.normalWSAndSpecular = half4(blade.normalWS, specular);

                #if defined(_GRASS_GROUND_NORMAL)
                    output.groundNormal = GrassGroundNormal(blade.mapUV);
                #endif
                return output;
            }

            half3 GrassDirectLight(Light light, half3 normalWS, half3 viewDirectionWS,
                half3 albedo, half specularStrength, half groundBlend)
            {
                half NdotL = dot(normalWS, light.direction);
                // Preserve the softer blade lighting; roots approach the terrain's Lambert response.
                half diffuse = lerp(saturate(NdotL * 0.5h + 0.5h), saturate(NdotL), groundBlend);
                half3 halfDirection = SafeNormalize(light.direction + viewDirectionWS);
                half specular = saturate(dot(normalWS, halfDirection));
                specular *= specular;
                specular *= specular;
                specular *= specular;
                specular *= specular;
                specular *= specularStrength * (1.0h - groundBlend);
                return (albedo * diffuse + specular) * light.color *
                    (light.distanceAttenuation * light.shadowAttenuation);
            }

            Light GrassMainLight(float3 positionWS)
            {
                #if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                    // URP creates its screen shadow texture before this indirect draw.
                    // That texture represents the opaque surface behind a blade, so
                    // sample the shadow atlas at the blade's actual position instead.
                    Light mainLight = GetMainLight();
                    half cascadeIndex = _GrassMainLightShadowCascades > 1
                        ? ComputeCascadeIndex(positionWS) : half(0.0);
                    float4 shadowCoord = float4(mul(_MainLightWorldToShadow[cascadeIndex],
                        float4(positionWS, 1.0)).xyz, 0.0);
                    half shadow = SampleShadowmap(
                        TEXTURE2D_SHADOW_ARGS(_MainLightShadowmapTexture, sampler_LinearClampCompare),
                        shadowCoord, GetMainLightShadowSamplingData(), GetMainLightShadowParams(), false);
                    mainLight.shadowAttenuation = lerp(shadow, 1.0h, GetMainLightShadowFade(positionWS));
                #else
                    Light mainLight = GetMainLight(TransformWorldToShadowCoord(positionWS),
                        positionWS, half4(1, 1, 1, 1));
                #endif
                // These indirect draws have no MeshRenderer per-object light mask.
                mainLight.distanceAttenuation = 1.0;
                return mainLight;
            }

            half4 GrassForwardFragment(GrassForwardVaryings input) : SV_Target
            {
                half alpha = GrassFragmentCoverage(input.shapeCoordinates, input.coverage, input.seed);
                half groundBlend = GrassGroundBlend(input.shapeCoordinates.y, input.groundColor.a);
                half3 albedo = lerp(input.grassAlbedo, input.groundColor.rgb, groundBlend);
                half3 normalWS = SafeNormalize(input.normalWSAndSpecular.xyz);
                #if defined(_GRASS_GROUND_NORMAL)
                    normalWS = SafeNormalize(lerp(normalWS, input.groundNormal, groundBlend));
                #endif

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWSAndFog.xyz;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(inputData.positionWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);

                Light mainLight = GrassMainLight(inputData.positionWS);
                half3 lighting = GrassAmbientLight(normalWS) * albedo;
                lighting += GrassDirectLight(mainLight, normalWS, inputData.viewDirectionWS,
                    albedo, input.normalWSAndSpecular.w, groundBlend);

                // Forward+ supplies a spatial light list without MeshRenderer light indices.
                // The material keyword compiles these loops out in the default quality mode.
                #if defined(_GRASS_ADDITIONAL_LIGHTS) && USE_CLUSTER_LIGHT_LOOP
                    UNITY_LOOP for (uint lightIndex = 0;
                        lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); ++lightIndex)
                    {
                        CLUSTER_LIGHT_LOOP_SUBTRACTIVE_LIGHT_CHECK
                        Light additionalLight = GetAdditionalLight(lightIndex, inputData.positionWS, half4(1, 1, 1, 1));
                        lighting += GrassDirectLight(additionalLight, normalWS, inputData.viewDirectionWS,
                            albedo, input.normalWSAndSpecular.w, groundBlend);
                    }
                    uint pixelLightCount = GetAdditionalLightsCount();
                    LIGHT_LOOP_BEGIN(pixelLightCount)
                        Light additionalLight = GetAdditionalLight(lightIndex, inputData.positionWS, half4(1, 1, 1, 1));
                        lighting += GrassDirectLight(additionalLight, normalWS, inputData.viewDirectionWS,
                            albedo, input.normalWSAndSpecular.w, groundBlend);
                    LIGHT_LOOP_END
                #endif

                return half4(MixFog(lighting, input.positionWSAndFog.w), alpha);
            }
            ENDHLSL
        }

        // A dedicated, single-sample depth source for the contact-shadow renderer.
        // Both passes call the same deformation and coverage functions.
        Pass
        {
            Name "GrassContactDepth"
            Tags { "LightMode" = "GrassContactDepth" }
            Cull Back
            ZWrite On
            ZTest LEqual
            Blend Off
            ColorMask RG
            AlphaToMask Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex GrassContactDepthVertex
            #pragma fragment GrassContactDepthFragment

            #include "GrassBladeCommon.hlsl"

            struct GrassDepthVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 shapeCoordinates : TEXCOORD0;
                nointerpolation float coverage : TEXCOORD1;
                nointerpolation uint seed : TEXCOORD2;
                float3 positionWS : TEXCOORD3;
            };

            GrassDepthVaryings GrassContactDepthVertex(GrassAttributes input, uint instanceID : SV_InstanceID)
            {
                GrassVertexData blade = BuildGrassVertex(input, instanceID);
                GrassDepthVaryings output;
                output.positionCS = mul(_GrassContactViewProjection, float4(blade.positionWS, 1.0));
                output.positionWS = blade.positionWS;
                output.shapeCoordinates = blade.shapeCoordinates;
                output.coverage = blade.coverage;
                output.seed = blade.seed;
                return output;
            }

            float2 GrassContactDepthFragment(GrassDepthVaryings input) : SV_Target
            {
                float3 cameraDelta = input.positionWS - _WorldSpaceCameraPos;
                clip(_GrassContactCasterDistance * _GrassContactCasterDistance - dot(cameraDelta, cameraDelta));
                float coverage = GrassFragmentCoverage(input.shapeCoordinates, input.coverage, input.seed);
                // Fragment SV_POSITION.z is device depth, matching the depth attachment.
                // AlphaToMask stays off: fractional coverage is preserved explicitly.
                return float2(input.positionCS.z, coverage);
            }
            ENDHLSL
        }

        Pass
        {
            Name "GrassMotionVectors"
            Tags { "LightMode" = "MotionVectors" }
            Cull Back
            ZWrite On
            ZTest LEqual
            Blend Off
            ColorMask RG
            AlphaToMask Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex GrassMotionVertex
            #pragma fragment GrassMotionFragment
            #include "GrassMotionVectors.hlsl"
            ENDHLSL
        }
    }
}
