#ifndef CONE_GRASS_FORWARD_LIGHTING_INCLUDED
#define CONE_GRASS_FORWARD_LIGHTING_INCLUDED
// The scene light the indirect blade and head draws share. Include after URP's Lighting.hlsl.

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

#endif
