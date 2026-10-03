#ifndef CONE_GRASS_CAPTURE_COMMON_INCLUDED
#define CONE_GRASS_CAPTURE_COMMON_INCLUDED
#include "GrassCaptureSampling.hlsl"

// A dedicated top-down matrix. Capturing grass data never replaces the camera matrices.
float4x4 _GrassCaptureVP;

struct GrassCaptureAttributes
{
    float3 positionOS : POSITION;
    float2 uv : TEXCOORD0;
    half4 color : COLOR;
    float4 tangentOS : TANGENT;
};

struct GrassCaptureVaryings
{
    float4 positionCS : SV_POSITION;
    float2 uv : TEXCOORD0;
    half4 color : TEXCOORD1;
    float3 tangentWS : TEXCOORD2;
};

GrassCaptureVaryings GrassCaptureVertex(GrassCaptureAttributes input)
{
    GrassCaptureVaryings output;
    float3 positionWS = TransformObjectToWorld(input.positionOS);
    output.positionCS = mul(_GrassCaptureVP, float4(positionWS, 1));
    // Color, exclusion and interaction are XZ projections; their object's Y is not a clipping bound.
    output.positionCS.z = 0.5 * output.positionCS.w;
    output.uv = input.uv * _MainTex_ST.xy + _MainTex_ST.zw;
    output.color = input.color;
    output.tangentWS = TransformObjectToWorldDir(input.tangentOS.xyz, false);
    return output;
}
#endif
