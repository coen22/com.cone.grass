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
int _GrassUseExplicitTime;
float _GrassTime;
float _GrassContactCasterDistance;
float4x4 _GrassContactViewProjection;
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

// The current and previous frames evaluate the same deformation. Keeping these
// inputs explicit also lets motion vectors use the previous camera-facing width.
struct GrassShapeParameters
{
    float4 dimensions; // width, height, width randomness, height randomness
    float4 shaping; // curvature, distant width, subdivision boost, bump width
    float4 ranges; // distant width start/end, subdivision distance, minimum pixels
    float4 windST;
    float4 windMotion; // scroll XY, strength, time
};

struct GrassViewParameters
{
    float3 cameraPosition;
    float3 cameraForward;
    float3 cameraRight;
    float viewTranslationZ;
    float3 projection; // render height in pixels, abs(projection.m11), orthographic
};

struct GrassRootData
{
    float3 pivot;
    float3 slopeDirection;
    float2 wind;
    float2 curvature;
    float2 mapUV;
    float width;
    float height;
    float coverage;
    float cameraDistance;
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

GrassShapeParameters GrassCurrentShape()
{
    GrassShapeParameters shape;
    shape.dimensions = float4(_GrassWidth, _GrassHeight, _GrassWidthRandomness, _GrassHeightRandomness);
    shape.shaping = float4(_GrassCurving, _ExpandDistantGrassWidth, _SubdivisionHeightBoost, _SubdivisionBumpWidth);
    shape.ranges = float4(_ExpandDistantGrassRange.xy, _SubdivisionDistance, _MinimumPixelWidth);
    shape.windST = _WindTexture_ST;
    shape.windMotion = float4(_WindScroll.xy, _WindStrength,
        _GrassUseExplicitTime != 0 ? _GrassTime : _Time.y);
    return shape;
}

GrassViewParameters GrassCurrentView()
{
    GrassViewParameters view;
    view.cameraPosition = _WorldSpaceCameraPos;
    view.cameraForward = -UNITY_MATRIX_V[2].xyz;
    view.cameraRight = UNITY_MATRIX_V[0].xyz;
    view.viewTranslationZ = UNITY_MATRIX_V[2].w;
    view.projection = float3(_ScaledScreenParams.y, abs(UNITY_MATRIX_P._m11), unity_OrthoParams.w);
    return view;
}

float GrassWorldUnitsPerPixel(float3 positionWS, GrassViewParameters view)
{
    float eyeDepth = max(abs(dot(positionWS, -view.cameraForward) + view.viewTranslationZ), 0.001);
    float depthScale = lerp(eyeDepth, 1.0, view.projection.z);
    return (2.0 * depthScale) / (max(view.projection.x, 1.0) * max(view.projection.y, 0.001));
}

GrassRootData BuildGrassRoot(float4 positionData, GrassShapeParameters shape,
    GrassViewParameters view, float4 slope, float2 wind, float2 mapUV)
{
    GrassRootData root;
    root.pivot = positionData.xyz;
    root.seed = GrassHash(asuint(root.pivot.x)) ^ GrassHash(asuint(root.pivot.z) + 0x9e3779b9u);
    root.cameraDistance = distance(view.cameraPosition, root.pivot);
    root.mapUV = mapUV;
    float width = max(0.0, shape.dimensions.x) *
        (1.0 - GrassRandom(root.seed + 1u) * saturate(shape.dimensions.z));
    float widthRange = max(shape.ranges.y - shape.ranges.x, 0.001);
    width += saturate((root.cameraDistance - shape.ranges.x) / widthRange) * max(0.0, shape.shaping.y);

    // Expand subpixel blades and compensate coverage by the same ratio.
    float originalFullWidth = width * 0.5;
    float minimumFullWidth = max(0.0, shape.ranges.w) * GrassWorldUnitsPerPixel(root.pivot, view);
    float expandedFullWidth = max(originalFullWidth, minimumFullWidth);
    // Preserve the ratio at small world scales too. A fixed denominator floor
    // would thin even an unexpanded blade that projects to several full pixels.
    float widthCoverage = expandedFullWidth > 0.0 ? originalFullWidth / expandedFullWidth : 0.0;
    root.coverage = saturate(positionData.w) * saturate(widthCoverage);
    root.width = expandedFullWidth * 2.0;
    root.height = max(0.0, shape.dimensions.y) *
        (1.0 - GrassRandom(root.seed + 2u) * saturate(shape.dimensions.w));
    float bump = saturate(1.0 - abs(root.cameraDistance - shape.ranges.z) / max(shape.shaping.w, 0.001));
    root.height *= max(0.0, 1.0 + shape.shaping.z * bump);

    // Decode the captured direction before strength interpolation. Clear pixels
    // contain zero RG and alpha; their neutral signed direction must remain zero.
    float2 slopeRG = slope.a > 0.0001 ? saturate(slope.rg / slope.a) : float2(0.5, 0.5);
    float2 slopeXZ = slopeRG * 2.0 - 1.0;
    float3 slopeDirection = SafeNormalize(float3(slopeXZ.x,
        1.0 - max(abs(slopeXZ.x), abs(slopeXZ.y)) * 0.5, slopeXZ.y));
    root.slopeDirection = SafeNormalize(lerp(float3(0, 1, 0), slopeDirection, saturate(slope.a)));
    root.wind = wind * shape.windMotion.z * (1.0 - saturate(slope.a));
    root.curvature = (float2(GrassRandom(root.seed + 3u), GrassRandom(root.seed + 4u)) * 2.0 - 1.0) * shape.shaping.x;
    return root;
}

float3 GrassDirectionAtHeight(GrassRootData root, float height)
{
    float3 direction = root.slopeDirection;
    direction.xz += root.wind * height;
    return SafeNormalize(direction);
}

void EvaluateGrassRow(GrassRootData root, GrassViewParameters view, float height,
    out float3 center, out float3 halfSpan)
{
    height = saturate(height);
    float3 bladeDirection = GrassDirectionAtHeight(root, height);
    center = root.pivot + bladeDirection * height * root.height;
    center.xz += height * height * root.curvature;
    // Perspective rays vary across the image. Facing every blade along the
    // camera's central ray can backface-cull visible roots behind the camera in
    // world XZ when looking down. Orthographic rays remain parallel instead.
    float3 viewDirection = view.projection.z > 0.5
        ? view.cameraForward : SafeNormalize(center - view.cameraPosition);
    float3 rightTangent = cross(bladeDirection, viewDirection);
    if (dot(rightTangent, rightTangent) < 0.00001)
        rightTangent = view.cameraRight;
    rightTangent = SafeNormalize(rightTangent);
    halfSpan = rightTangent * 0.25 * root.width * (1.0 - height);
}

float3 EvaluateGrassPosition(GrassRootData root, GrassViewParameters view, float2 uv)
{
    float3 center, halfSpan;
    EvaluateGrassRow(root, view, uv.y, center, halfSpan);
    return center + (uv.x * 2.0 - 1.0) * halfSpan;
}

GrassRootData BuildCurrentGrassRoot(float4 positionData)
{
    GrassShapeParameters shape = GrassCurrentShape();
    float2 mapUV = GrassWorldToMapUV(positionData.xyz);
    float4 slope = SAMPLE_TEXTURE2D_LOD(_GrassSlopeRT, sampler_GrassSlopeRT, mapUV, 0);
    float2 windUV = positionData.xz * shape.windST.xy + shape.windST.zw + shape.windMotion.xy * shape.windMotion.w;
    float2 wind = SAMPLE_TEXTURE2D_LOD(_WindTexture, sampler_WindTexture, windUV, 0).rg * 2.0 - 1.0;
    return BuildGrassRoot(positionData, shape, GrassCurrentView(), slope, wind, mapUV);
}

GrassVertexData BuildGrassVertex(GrassAttributes input, uint instanceID)
{
    GrassRootData root = BuildCurrentGrassRoot(_GrassPositions[(uint)_GrassInstanceOffset + instanceID]);
    GrassViewParameters view = GrassCurrentView();
    GrassVertexData blade;
    blade.positionWS = EvaluateGrassPosition(root, view, input.uv);
    blade.seed = root.seed;
    blade.height = saturate(input.uv.y);
    blade.cameraDistance = root.cameraDistance;
    blade.mapUV = root.mapUV;
    blade.coverage = root.coverage;

    float3 normalJitter = float3(GrassRandom(blade.seed + 5u) * 2.0 - 1.0,
        0.0, GrassRandom(blade.seed + 6u) * 2.0 - 1.0);
    blade.normalWS = SafeNormalize(GrassDirectionAtHeight(root, blade.height) - view.cameraForward * 0.5 + _RandomNormal * normalJitter);
    // This coordinate remains linear across the tapered mesh, including its final
    // triangle, so the same analytic edge coverage works for every geometry LOD.
    blade.shapeCoordinates = float2((input.uv.x - 0.5) * (1.0 - blade.height), blade.height);
    return blade;
}

half GrassGroundBlend(float bladeHeight, half mapStrength)
{
    float rootFalloff = 1.0 - smoothstep(0.0, max(_GroundBlendHeight, 0.0001), bladeHeight);
    return saturate(mapStrength * _GroundBlendStrength) * rootFalloff * step(0.0001, _GroundBlendHeight);
}

float GrassGroundHeight(float2 uv, float fallback)
{
    float2 captured = SAMPLE_TEXTURE2D_LOD(_GrassHeightMapRT, sampler_GrassHeightMapRT, uv, 0).rg;
    if (!all(isfinite(captured)) || captured.g <= 0.0)
        return fallback;
    if (captured.g >= 1.0)
        return captured.r;

    // Height is not premultiplied by eligibility. Exclude clear/invalid texels
    // from bilinear height weights without dividing a vertex-color mask into Y.
    int2 size = max(int2(_GrassHeightMapRT_TexelSize.zw), int2(1, 1));
    float2 pixel = uv * size - 0.5;
    int2 basePixel = int2(floor(pixel));
    float2 fraction = frac(pixel);
    float height = 0.0;
    float validWeight = 0.0;
    [unroll]
    for (uint corner = 0u; corner < 4u; ++corner)
    {
        int2 offset = int2(corner & 1u, corner >> 1u);
        int2 coordinate = clamp(basePixel + offset, int2(0, 0), size - 1);
        float2 neighbor = LOAD_TEXTURE2D(_GrassHeightMapRT, coordinate).rg;
        float2 weightXY = lerp(1.0 - fraction, fraction, float2(offset));
        float weight = weightXY.x * weightXY.y;
        if (neighbor.g > 0.0 && all(isfinite(neighbor)))
        {
            height += neighbor.r * weight;
            validWeight += weight;
        }
    }
    return validWeight > 0.0 ? height / validWeight : fallback;
}

half3 GrassGroundNormal(float2 uv)
{
    float2 texel = _GrassHeightMapRT_TexelSize.xy;
    if (any(texel <= 0.0) || any(uv < texel) || any(uv > 1.0 - texel))
        return half3(0, 1, 0);

    float2 center = SAMPLE_TEXTURE2D_LOD(_GrassHeightMapRT, sampler_GrassHeightMapRT, uv, 0).rg;
    if (!all(isfinite(center)) || center.g <= 0.0)
        return half3(0, 1, 0);
    float height = GrassGroundHeight(uv, center.r);
    // A missing neighbor contributes the valid center height rather than a
    // false cliff down to the capture's clear world height of zero.
    float left = GrassGroundHeight(uv - float2(texel.x, 0), height);
    float right = GrassGroundHeight(uv + float2(texel.x, 0), height);
    float down = GrassGroundHeight(uv - float2(0, texel.y), height);
    float up = GrassGroundHeight(uv + float2(0, texel.y), height);

    float2 sampleDistance = max(texel * (2.0 * (_DrawDistance + _TextureUpdateThreshold)), 0.0001);
    return SafeNormalize(float3((left - right) / (2.0 * sampleDistance.x),
        1.0, (down - up) / (2.0 * sampleDistance.y)));
}

half GrassFragmentCoverageWithMode(float2 shapeCoordinates, float instanceCoverage, uint seed, bool alphaToCoverage)
{
    float edgeDistance = (1.0 - shapeCoordinates.y) * 0.5 - abs(shapeCoordinates.x);
    float edgeFootprint = max(fwidth(shapeCoordinates.x) + 0.5 * fwidth(shapeCoordinates.y), 0.0001);
    float edgeCoverage = saturate(edgeDistance / edgeFootprint + 0.5);
    float coverage = saturate(instanceCoverage * edgeCoverage);
    clip(coverage - 0.00001);

    // The single-sample contact pass stores this analytic silhouette estimate
    // beside raw depth, so a partly covered blade is not a solid occluder.
    if (alphaToCoverage)
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

half GrassFragmentCoverage(float2 shapeCoordinates, float instanceCoverage, uint seed)
{
    return GrassFragmentCoverageWithMode(shapeCoordinates, instanceCoverage, seed, _GrassAlphaToCoverage > 0.5);
}

half GrassForwardCoverage(float2 shapeCoordinates, float instanceCoverage, uint seed)
{
    if (_GrassAlphaToCoverage > 0.5)
    {
        // The tapered mesh is the blade silhouette. Hardware MSAA already
        // covers its geometric edges; A2C is ANDed with that sample mask.
        // Export only density/width compensation here. Applying the analytic
        // edge fraction as well would attenuate the same silhouette twice.
        half coverage = saturate(instanceCoverage);
        clip(coverage - 0.00001);
        return coverage;
    }
    return GrassFragmentCoverageWithMode(shapeCoordinates, instanceCoverage, seed, false);
}

#endif
