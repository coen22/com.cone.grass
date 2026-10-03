#ifndef CONE_GRASS_MOTION_VECTORS_INCLUDED
#define CONE_GRASS_MOTION_VECTORS_INCLUDED

#include "GrassBladeCommon.hlsl"
#include "GrassMotionHistoryLookup.hlsl"
#include "GrassMotionInterpolation.hlsl"

int _GrassMotionHashMask;
int _GrassMotionHistoryValid;
float4 _GrassPreviousDimensions;
float4 _GrassPreviousShaping;
float4 _GrassPreviousRanges;
float4 _GrassPreviousWindST;
float4 _GrassPreviousWindMotion;
float4 _GrassPreviousCameraPosition;
float4 _GrassPreviousCameraForward;
float4 _GrassPreviousCameraRight;
float4 _GrassPreviousProjection;
float4 _GrassPreviousMap; // center XZ, reciprocal full extent, unused

TEXTURE2D(_GrassPreviousSlope);
SAMPLER(sampler_GrassPreviousSlope);
TEXTURE2D(_GrassPreviousWind);
SAMPLER(sampler_GrassPreviousWind);

GrassViewParameters GrassPreviousView()
{
    GrassViewParameters view;
    view.cameraPosition = _GrassPreviousCameraPosition.xyz;
    view.cameraForward = _GrassPreviousCameraForward.xyz;
    view.cameraRight = _GrassPreviousCameraRight.xyz;
    view.viewTranslationZ = -_GrassPreviousCameraForward.w;
    view.projection = _GrassPreviousProjection.xyz;
    return view;
}

bool FindPreviousGrassRoot(float3 pivot, out float4 previous)
{
    previous = 0.0;
    if (_GrassMotionHistoryValid == 0)
        return false;

    // Appending and LOD compaction may reorder every instance between frames.
    // Match the exact stable XZ coordinates; Y is allowed to move with the surface.
    return GrassFindPreviousRoot(pivot.xz, (uint)_GrassMotionHashMask, previous);
}

struct GrassMotionVaryings
{
    float4 positionCS : SV_POSITION;
    float3 currentPositionWS : TEXCOORD0;
    float2 shapeCoordinates : TEXCOORD1;
    nointerpolation float4 previousPivotAndRows : TEXCOORD2;
    nointerpolation float4 previousDirectionAndWidth : TEXCOORD3;
    nointerpolation float4 previousWindAndHeight : TEXCOORD4;
    nointerpolation float2 previousCurvature : TEXCOORD5;
    nointerpolation float coverage : TEXCOORD6;
    nointerpolation uint seed : TEXCOORD7;
};

GrassMotionVaryings GrassMotionVertex(GrassAttributes input, uint instanceID : SV_InstanceID)
{
    float4 positionData = _GrassPositions[(uint)_GrassInstanceOffset + instanceID];
    GrassRootData current = BuildCurrentGrassRoot(positionData);
    GrassMotionVaryings output = (GrassMotionVaryings)0;
    output.currentPositionWS = EvaluateGrassPosition(current, GrassCurrentView(), input.uv);
    // Rasterization includes the current camera jitter, exactly as the color pass.
    output.positionCS = TransformWorldToHClip(output.currentPositionWS);
    output.shapeCoordinates = float2((input.uv.x - 0.5) * (1.0 - input.uv.y), input.uv.y);
    output.coverage = current.coverage;
    output.seed = current.seed;

    float4 previous;
    if (FindPreviousGrassRoot(current.pivot, previous))
    {
        GrassShapeParameters shape;
        shape.dimensions = _GrassPreviousDimensions;
        shape.shaping = _GrassPreviousShaping;
        shape.ranges = _GrassPreviousRanges;
        shape.windST = _GrassPreviousWindST;
        shape.windMotion = _GrassPreviousWindMotion;
        float2 mapUV = (previous.xz - _GrassPreviousMap.xy) * _GrassPreviousMap.z + 0.5;
        float4 slope = SAMPLE_TEXTURE2D_LOD(_GrassPreviousSlope, sampler_GrassPreviousSlope, mapUV, 0);
        float2 windUV = previous.xz * shape.windST.xy + shape.windST.zw + shape.windMotion.xy * shape.windMotion.w;
        float2 wind = SAMPLE_TEXTURE2D_LOD(_GrassPreviousWind, sampler_GrassPreviousWind, windUV, 0).rg * 2.0 - 1.0;
        float rows = floor(previous.w);
        float previousCoverage = frac(previous.w) * 2.0;
        GrassRootData root = BuildGrassRoot(float4(previous.xyz, previousCoverage),
            shape, GrassPreviousView(), slope, wind, mapUV);
        output.previousPivotAndRows = float4(root.pivot, rows);
        output.previousDirectionAndWidth = float4(root.slopeDirection, root.width);
        output.previousWindAndHeight = float4(root.wind, root.height, root.coverage);
        output.previousCurvature = root.curvature;
    }
    return output;
}

float3 PreviousGrassSurface(GrassMotionVaryings input)
{
    GrassRootData root = (GrassRootData)0;
    root.pivot = input.previousPivotAndRows.xyz;
    root.slopeDirection = input.previousDirectionAndWidth.xyz;
    root.width = input.previousDirectionAndWidth.w;
    root.wind = input.previousWindAndHeight.xy;
    root.height = input.previousWindAndHeight.z;
    root.curvature = input.previousCurvature;
    float rows = input.previousPivotAndRows.w;
    float height = saturate(input.shapeCoordinates.y);
    float row = min(floor(height * rows), rows - 1.0);
    float lowerHeight = row / rows;
    float upperHeight = (row + 1.0) / rows;
    float3 lowerCenter, lowerSpan, upperCenter, upperSpan;
    EvaluateGrassRow(root, GrassPreviousView(), lowerHeight, lowerCenter, lowerSpan);
    EvaluateGrassRow(root, GrassPreviousView(), upperHeight, upperCenter, upperSpan);

    // Reconstruct the actual previous triangles, including their LL-to-UR
    // diagonal. Evaluating a continuous curve, or interpolating only the current
    // LOD's vertices, gives wrong velocities when a curved blade becomes a triangle.
    return GrassInterpolatePreviousTriangle(input.shapeCoordinates, lowerHeight, upperHeight,
        lowerCenter, lowerSpan, upperCenter, upperSpan);
}

float4 GrassMotionFragment(GrassMotionVaryings input) : SV_Target
{
    // URP motion is single-sample even when camera color uses MSAA. Auto mode
    // enables this pass for temporal AA/object blur; matching deterministic
    // coverage is retained for explicit custom consumers on MSAA cameras.
    GrassFragmentCoverageWithMode(input.shapeCoordinates, input.coverage, input.seed, false);
    float4 currentCS = mul(_NonJitteredViewProjMatrix, float4(input.currentPositionWS, 1.0));
    float3 previousWS = input.previousPivotAndRows.w >= 1.0
        ? PreviousGrassSurface(input) : input.currentPositionWS;
    float4 previousCS = mul(_PrevViewProjMatrix, float4(previousWS, 1.0));

    if (_GrassMotionHistoryValid == 0)
        return 0.0; // Newly initialized or explicitly reset temporal history.
    if (previousCS.w <= 0.00001)
        return float4(2.0, 2.0, 0.0, 0.0); // Previous point was behind the camera.

    // URP's documented non-jittered UV motion convention. Its stock helper also
    // checks unity_MotionVectorsParams, which this indirect command does not own.
    float2 velocity = (currentCS.xy / max(currentCS.w, 0.00001) - previousCS.xy / previousCS.w) * 0.5;
    #if UNITY_UV_STARTS_AT_TOP
        velocity.y = -velocity.y;
    #endif
    return float4(clamp(velocity, -2.0, 2.0), 0.0, 0.0);
}

#endif
