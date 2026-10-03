#ifndef CONE_GRASS_BLADE_COMMON_INCLUDED
#define CONE_GRASS_BLADE_COMMON_INCLUDED
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

CBUFFER_START(UnityPerMaterial)
    half4 _Color;
    half4 _AOColor;
    float _GrassWidth;
    float _GrassHeight;
    float _GrassCurving;
    float _GrassWidthRandomness;
    float _GrassHeightRandomness;
    float _ExpandDistantGrassWidth;
    float4 _ExpandDistantGrassRange;
    float4 _WindTexture_ST;
    float _WindStrength;
    float4 _WindScroll;
    half _RandomNormal;
    half _GroundBlendStrength;
    float _GroundBlendHeight;
    float _UseGroundNormal;
    float _UseAdditionalLights;
    float _SpecularFadeStart;
    float _SpecularFadeEnd;
    float _MinimumPixelWidth;
    float _GrassAlphaToCoverage;
    float _MaxSubdivision;
    float _SubdivisionDistance;
    float _SubdivisionHeightBoost;
    float _SubdivisionBumpWidth;
    float _FullDensityDistance;
    float _DensityFalloffExponent;
CBUFFER_END

// Renderer-owned resources and constants belong outside UnityPerMaterial.
// xyz = stable world pivot; w = continuous coverage, never camera distance.
StructuredBuffer<float4> _GrassPositions;
int _GrassInstanceOffset;
int _GrassMainLightShadowCascades;
float4 _GrassSHAr;
float4 _GrassSHAg;
float4 _GrassSHAb;
float4 _GrassSHBr;
float4 _GrassSHBg;
float4 _GrassSHBb;
float4 _GrassSHC;
float2 _CenterPos;
float _DrawDistance;
float _TextureUpdateThreshold;
float4 _GrassHeightMapRT_TexelSize;

TEXTURE2D(_WindTexture);
SAMPLER(sampler_WindTexture);
TEXTURE2D(_GrassColorRT);
SAMPLER(sampler_GrassColorRT);
TEXTURE2D(_GrassSlopeRT);
SAMPLER(sampler_GrassSlopeRT);
TEXTURE2D(_GrassGroundColorRT);
SAMPLER(sampler_GrassGroundColorRT);
TEXTURE2D(_GrassHeightMapRT);
SAMPLER(sampler_GrassHeightMapRT);

struct GrassAttributes
{
    float3 positionOS : POSITION;
    float2 uv : TEXCOORD0;
};

struct GrassVertexData
{
    float3 positionWS;
    half3 normalWS;
    float2 mapUV;
    float2 shapeCoordinates;
    float height;
    float cameraDistance;
    float coverage;
    uint seed;
};

uint GrassHash(uint value)
{
    value ^= value >> 16;
    value *= 0x85ebca6bu;
    value ^= value >> 13;
    value *= 0xc2b2ae35u;
    value ^= value >> 16;
    return value;
}

float GrassRandom(uint seed)
{
    return (GrassHash(seed) & 0x00ffffffu) * (1.0 / 16777216.0);
}

float2 GrassWorldToMapUV(float3 positionWS)
{
    float extent = max(_DrawDistance + _TextureUpdateThreshold, 0.001);
    return (positionWS.xz - _CenterPos) / (2.0 * extent) + 0.5;
}

float GrassDistanceDensity(float cameraDistance)
{
    float fadeRange = max(_DrawDistance - _FullDensityDistance, 0.001);
    float fade = saturate((_DrawDistance - cameraDistance) / fadeRange);
    return pow(fade, max(_DensityFalloffExponent, 0.001));
}

float GrassWorldUnitsPerPixel(float3 positionWS)
{
    float eyeDepth = max(abs(TransformWorldToView(positionWS).z), 0.001);
    float projectionScale = max(abs(UNITY_MATRIX_P._m11), 0.001);
    float depthScale = lerp(eyeDepth, 1.0, unity_OrthoParams.w);
    return (2.0 * depthScale) / (max(_ScaledScreenParams.y, 1.0) * projectionScale);
}

GrassVertexData BuildGrassVertex(GrassAttributes input, uint instanceID)
{
    GrassVertexData blade;
    float4 positionData = _GrassPositions[(uint)_GrassInstanceOffset + instanceID];
    float3 pivot = positionData.xyz;
    blade.seed = GrassHash(asuint(pivot.x)) ^ GrassHash(asuint(pivot.z) + 0x9e3779b9u);
    blade.height = saturate(input.uv.y);
    blade.cameraDistance = distance(_WorldSpaceCameraPos, pivot);
    blade.mapUV = GrassWorldToMapUV(pivot);

    float width = max(0.0, _GrassWidth) *
        (1.0 - GrassRandom(blade.seed + 1u) * saturate(_GrassWidthRandomness));
    float widthRange = max(_ExpandDistantGrassRange.y - _ExpandDistantGrassRange.x, 0.001);
    width += saturate((blade.cameraDistance - _ExpandDistantGrassRange.x) / widthRange) *
        max(0.0, _ExpandDistantGrassWidth);

    // The mesh's x coordinates span -0.25 .. +0.25. Expand subpixel blades
    // and compensate their coverage by the same ratio to retain grass density.
    float originalFullWidth = width * 0.5;
    float minimumFullWidth = max(0.0, _MinimumPixelWidth) * GrassWorldUnitsPerPixel(pivot);
    float expandedFullWidth = max(originalFullWidth, minimumFullWidth);
    blade.coverage = saturate(positionData.w) *
        saturate(originalFullWidth / max(expandedFullWidth, 0.00001));
    width = expandedFullWidth * 2.0;

    float height = max(0.0, _GrassHeight) *
        (1.0 - GrassRandom(blade.seed + 2u) * saturate(_GrassHeightRandomness));
    float bump = saturate(1.0 - abs(blade.cameraDistance - _SubdivisionDistance) /
        max(_SubdivisionBumpWidth, 0.001));
    height *= max(0.0, 1.0 + _SubdivisionHeightBoost * bump);

    float3 cameraForward = -UNITY_MATRIX_V[2].xyz;
    float4 slope = SAMPLE_TEXTURE2D_LOD(_GrassSlopeRT, sampler_GrassSlopeRT, blade.mapUV, 0);
    // Decode the captured direction before strength interpolation. Clear pixels
    // contain zero RG and alpha; their neutral signed direction must remain zero.
    float2 slopeRG = slope.a > 0.0001 ? saturate(slope.rg / slope.a) : float2(0.5, 0.5);
    float2 slopeXZ = slopeRG * 2.0 - 1.0;
    float3 slopeDirection = SafeNormalize(float3(slopeXZ.x,
        1.0 - max(abs(slopeXZ.x), abs(slopeXZ.y)) * 0.5, slopeXZ.y));
    float3 bladeDirection = SafeNormalize(lerp(float3(0, 1, 0), slopeDirection, saturate(slope.a)));

    float2 windUV = pivot.xz * _WindTexture_ST.xy + _WindTexture_ST.zw + _WindScroll.xy * _Time.y;
    float2 wind = SAMPLE_TEXTURE2D_LOD(_WindTexture, sampler_WindTexture, windUV, 0).rg * 2.0 - 1.0;
    bladeDirection.xz += wind * _WindStrength * (1.0 - saturate(slope.a)) * blade.height;
    bladeDirection = SafeNormalize(bladeDirection);

    float3 rightTangent = cross(bladeDirection, cameraForward);
    // Looking almost parallel to a blade must not produce a zero or NaN tangent.
    if (dot(rightTangent, rightTangent) < 0.00001)
        rightTangent = UNITY_MATRIX_V[0].xyz;
    rightTangent = SafeNormalize(rightTangent);

    float taper = 1.0 - blade.height;
    float3 displacement = bladeDirection * blade.height * height +
        rightTangent * input.positionOS.x * width * taper;
    float2 curvature = float2(GrassRandom(blade.seed + 3u), GrassRandom(blade.seed + 4u)) * 2.0 - 1.0;
    displacement.xz += blade.height * blade.height * curvature * _GrassCurving;
    blade.positionWS = pivot + displacement;

    float3 normalJitter = float3(GrassRandom(blade.seed + 5u) * 2.0 - 1.0,
        0.0, GrassRandom(blade.seed + 6u) * 2.0 - 1.0);
    blade.normalWS = SafeNormalize(bladeDirection - cameraForward * 0.5 + _RandomNormal * normalJitter);
    // This coordinate remains linear across the tapered mesh, including its final
    // triangle, so the same analytic edge coverage works for every geometry LOD.
    blade.shapeCoordinates = float2((input.uv.x - 0.5) * taper, blade.height);
    return blade;
}

half GrassGroundBlend(float bladeHeight, half mapStrength)
{
    float rootFalloff = 1.0 - smoothstep(0.0, max(_GroundBlendHeight, 0.0001), bladeHeight);
    return saturate(mapStrength * _GroundBlendStrength) * rootFalloff * step(0.0001, _GroundBlendHeight);
}

half3 GrassGroundNormal(float2 uv)
{
    float2 texel = _GrassHeightMapRT_TexelSize.xy;
    if (any(texel <= 0.0) || any(uv < texel) || any(uv > 1.0 - texel))
        return half3(0, 1, 0);

    float2 left = SAMPLE_TEXTURE2D_LOD(_GrassHeightMapRT, sampler_GrassHeightMapRT, uv - float2(texel.x, 0), 0).rg;
    float2 right = SAMPLE_TEXTURE2D_LOD(_GrassHeightMapRT, sampler_GrassHeightMapRT, uv + float2(texel.x, 0), 0).rg;
    float2 down = SAMPLE_TEXTURE2D_LOD(_GrassHeightMapRT, sampler_GrassHeightMapRT, uv - float2(0, texel.y), 0).rg;
    float2 up = SAMPLE_TEXTURE2D_LOD(_GrassHeightMapRT, sampler_GrassHeightMapRT, uv + float2(0, texel.y), 0).rg;
    if (min(min(left.g, right.g), min(down.g, up.g)) < 0.5)
        return half3(0, 1, 0);

    float2 sampleDistance = max(texel * (2.0 * (_DrawDistance + _TextureUpdateThreshold)), 0.0001);
    return SafeNormalize(float3((left.r - right.r) / (2.0 * sampleDistance.x),
        1.0, (down.r - up.r) / (2.0 * sampleDistance.y)));
}

half GrassFragmentCoverage(float2 shapeCoordinates, float instanceCoverage, uint seed)
{
    float edgeDistance = (1.0 - shapeCoordinates.y) * 0.5 - abs(shapeCoordinates.x);
    float edgeFootprint = max(fwidth(shapeCoordinates.x) + 0.5 * fwidth(shapeCoordinates.y), 0.0001);
    float edgeCoverage = saturate(edgeDistance / edgeFootprint + 0.5);
    float coverage = saturate(instanceCoverage * edgeCoverage);
    clip(coverage - 0.00001);

    // Color exports this fraction to A2C. The single-sample contact pass stores
    // it beside raw depth, so a partly covered blade is not a solid occluder.
    if (_GrassAlphaToCoverage > 0.5)
        return coverage;

    // Quantize in blade coordinates and hash its stable world pivot. Camera motion
    // and time never reseed the pattern. Without MSAA the contact pass clips the
    // identical fragments and records coverage 1 for the surviving samples.
    float across = shapeCoordinates.x / max(1.0 - shapeCoordinates.y, 0.0001) + 0.5;
    uint2 cell = (uint2)floor(saturate(float2(across, shapeCoordinates.y)) * float2(4.0, 16.0));
    float threshold = GrassRandom(seed ^ GrassHash(cell.x + cell.y * 17u));
    clip(coverage - max(threshold, 0.00001));
    return 1.0h;
}

#endif
