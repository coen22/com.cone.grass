#ifndef CONE_GRASS_HEAD_COMMON_INCLUDED
#define CONE_GRASS_HEAD_COMMON_INCLUDED

// Flower and herb heads (GrassVariantProfile), shared by the head shader's forward, contact-depth and motion passes, so
// every pass rebuilds the same head on the same bent blade tip.
#include "GrassBladeCommon.hlsl"

#define GRASS_HEAD_DAISY 1
#define GRASS_HEAD_CUP 2
#define GRASS_HEAD_UMBEL 3
#define GRASS_HEAD_SPIKE 4
#define GRASS_HEAD_CLOVER 5
#define GRASS_HEAD_MESH 6
#define GRASS_HEAD_PUFF 7

#define GRASS_STAGE_OPEN 0
#define GRASS_STAGE_BUD 1
#define GRASS_STAGE_SEED 2

struct GrassHeadAttributes
{
    float3 positionOS : POSITION;
    float3 normalOS : NORMAL;
    float2 uv : TEXCOORD0;
    // x: 0 for the flat face, 1 for the two crossed side quads.
    float2 part : TEXCOORD1;
};

// A head's frame on its blade tip: where it sits, which way it faces, its size and stage.
struct GrassHeadFrame
{
    float3 tip;
    float3 up;
    float3 right;
    float3 forward;
    float size;
    uint shape;
    uint stage;
};

// Height of the side quads and of the face, in head diameters: scale and offset of the quad's -0.5..0.5,
// then the face's offset above the tip.
float3 GrassHeadExtent(uint shape)
{
    if (shape == GRASS_HEAD_CUP) return float3(0.5, -0.15, 0.0);
    if (shape == GRASS_HEAD_UMBEL) return float3(0.3, 0.05, 0.05);
    if (shape == GRASS_HEAD_SPIKE) return float3(1.7, 0.75, 1.55);
    if (shape == GRASS_HEAD_CLOVER) return float3(0.1, 0.0, 0.0);
    if (shape == GRASS_HEAD_PUFF) return float3(0.8, 0.05, 0.1);
    return float3(0.4, 0.05, 0.02);
}

// The head on a root's blade tip. toSun is the main light's direction (only its XZ is used); densityCoverage is the
// root's packed coverage, so a head follows a density transition. The motion pass calls this with the previous root,
// view and sun, so it rebuilds the previous head exactly as the forward pass drew it.
GrassHeadFrame GrassBuildHeadFrame(GrassRootData root, float densityCoverage, float3 toSun)
{
    GrassHeadFrame frame;
    float4 head = _GrassKindHead[root.kind];
    frame.shape = (uint)head.z;
    frame.tip = GrassCenterAtHeight(root, 1.0);
    // The face turns from the stem's tip toward the sun, with each head's own jitter; a leaf turns less.
    float3 stemUp = GrassTangentAtHeight(root, 1.0);
    float3 sun = float3(toSun.x, 0.0, toSun.z);
    float2 jitter = float2(GrassRandom(root.seed + 17u), GrassRandom(root.seed + 18u)) - 0.5;
    float turn = frame.shape == GRASS_HEAD_CLOVER ? 0.15 : 0.45;
    float3 up = SafeNormalize(stemUp + sun * turn + float3(jitter.x, 0.0, jitter.y) * 0.5);
    up = SafeNormalize(float3(up.x, max(up.y, 0.35), up.z));
    // Some heads face sideways, nodding over on their stems.
    if (GrassRandom(root.seed + 19u) < _GrassKindStem[root.kind].z)
    {
        float nod = GrassRandom(root.seed + 20u) * 6.2831853;
        up = SafeNormalize(float3(cos(nod), 0.35, sin(nod)));
    }
    float yaw = GrassRandom(root.seed + 12u) * 6.2831853;
    float3 around = float3(cos(yaw), 0.0, sin(yaw));
    frame.up = up;
    frame.right = SafeNormalize(around - up * dot(around, up));
    frame.forward = cross(frame.right, up);

    // A few heads are closed buds and a few have gone to seed; a clover leaf is always open.
    float stageRandom = GrassRandom(root.seed + 16u);
    frame.stage = frame.shape == GRASS_HEAD_CLOVER || frame.shape == GRASS_HEAD_MESH ? GRASS_STAGE_OPEN :
        stageRandom < _GrassVariantParams2.z ? GRASS_STAGE_BUD :
        stageRandom < _GrassVariantParams2.z + _GrassVariantParams2.w ? GRASS_STAGE_SEED : GRASS_STAGE_OPEN;
    // Each head's size varies either way by the kind's randomness; buds and seed heads are smaller.
    float size = head.x * (1.0 + saturate(head.y) * (2.0 * GrassRandom(root.seed + 11u) - 1.0)) *
        (frame.stage == GRASS_STAGE_BUD ? 0.55 : frame.stage == GRASS_STAGE_SEED ? 0.75 : 1.0);
    // No pixel floor: under about three pixels, or near its draw distance, the head shrinks away and the
    // stem's top carries its colour instead (GrassHeadVisibility); it also follows a density transition.
    frame.size = size * GrassHeadVisibility(size, root.cameraDistance, root.worldUnitsPerPixel) *
        sqrt(densityCoverage);
    return frame;
}

// A head vertex in its frame's local coordinates: rise is the height in head diameters above the tip (side quads),
// normal the local normal before the frame turns it.
float3 GrassHeadLocalVertex(GrassHeadAttributes input, uint shape, out float rise, out float3 normal)
{
    float3 local = input.positionOS;
    rise = 0.0;
    if (shape == GRASS_HEAD_MESH)
    {
        normal = input.normalOS;
        return local;
    }
    bool side = input.part.x > 0.5;
    float3 extent = GrassHeadExtent(shape);
    rise = side ? local.y * extent.x + extent.y : extent.z;
    local.y = rise;
    // A slight dome on the face; the side quads face the camera, a little toward the sky.
    normal = side ? input.normalOS : float3(local.x * 0.8, 1.0, local.z * 0.8);
    return local;
}

float3 GrassHeadWorldPosition(GrassHeadFrame frame, float3 local)
{
    return frame.tip + (frame.right * local.x + frame.up * local.y + frame.forward * local.z) * frame.size;
}

// The current head vertex, as the forward pass draws it; the contact and motion passes rebuild the same one.
float3 GrassCurrentHeadVertex(GrassHeadAttributes input, uint instanceID, out GrassRootData root,
    out GrassHeadFrame frame, out float4 positionData, out float rise, out float3 normal)
{
    GrassViewParameters view = GrassCurrentView();
    positionData = _GrassPositions[(uint)_GrassInstanceOffset + instanceID];
    root = BuildGrassRootForView(positionData, view);
    frame = GrassBuildHeadFrame(root, GrassUnpackCoverage(positionData.w), _MainLightPosition.xyz);
    float3 local = GrassHeadLocalVertex(input, frame.shape, rise, normal);
    return GrassHeadWorldPosition(frame, local);
}

// Signed distance-like value, positive inside the head's outline, in units of its quad.
float GrassHeadOutline(uint shape, uint stage, bool side, float2 uv, float rise, float petals, out bool centre)
{
    centre = false;
    if (shape == GRASS_HEAD_MESH)
        return 1.0;
    if (stage == GRASS_STAGE_BUD)
    {
        // A closed bud: a small egg, seen from above or the side.
        float x = uv.x * 2.0 - 1.0;
        return side ? 1.0 - sqrt(x * x / 0.09 + (rise / 0.13) * (rise / 0.13)) : 0.35 - length(uv * 2.0 - 1.0);
    }
    if (stage == GRASS_STAGE_SEED && (shape == GRASS_HEAD_DAISY || shape == GRASS_HEAD_CUP))
    {
        // Petals gone: the disc alone, a low dome.
        float x = uv.x * 2.0 - 1.0;
        centre = true;
        return side ? 1.0 - sqrt(x * x / 0.1 + (rise / 0.14) * (rise / 0.14)) : 0.32 - length(uv * 2.0 - 1.0);
    }
    if (side)
    {
        float x = uv.x * 2.0 - 1.0;
        if (shape == GRASS_HEAD_CUP)
        {
            float bottom = -0.3 * (1.0 - x * x / 0.72);
            return min(min(rise - bottom, 0.03 - rise), 0.85 - abs(x));
        }
        if (shape == GRASS_HEAD_UMBEL)
        {
            // A shallow dome of separate florets, not a solid band.
            float floret = 0.32 - abs(frac(x * 4.5) - 0.5);
            return min(min(0.05 - abs(rise - 0.08 * (1.0 - x * x)), floret * 0.25), 0.92 - abs(x));
        }
        if (shape == GRASS_HEAD_SPIKE)
        {
            float t = (rise - 0.75) / 0.75;
            float width = 0.32 * sqrt(saturate(1.0 - t * t)) * (0.85 + 0.15 * sin(rise * 28.0));
            return width - abs(x);
        }
        if (shape == GRASS_HEAD_CLOVER)
            return -1.0;
        if (shape == GRASS_HEAD_PUFF)
        {
            // A soft ball of florets: a slightly ragged round outline.
            float wobble = 0.04 * sin(x * 23.0 + rise * 17.0);
            return 1.0 - sqrt(x * x / 0.18 + ((rise - 0.1) / 0.3) * ((rise - 0.1) / 0.3)) + wobble;
        }
        // Daisy: petals edge-on, with the disc as a low dome.
        float dome = 1.0 - (x * x / 0.08 + (rise / 0.12) * (rise / 0.12));
        centre = dome > 0.0 && rise > 0.0;
        return max(min(0.045 + 0.03 * (1.0 - x * x) - abs(rise), 1.0 - abs(x)), dome * 0.1);
    }
    float2 q = uv * 2.0 - 1.0;
    float r = length(q);
    float a = atan2(q.y, q.x);
    if (shape == GRASS_HEAD_CUP)
    {
        centre = r < 0.18;
        return 0.8 + 0.2 * cos(5.0 * a) - r;
    }
    if (shape == GRASS_HEAD_UMBEL)
    {
        // Florets on a grid cut to a disc, with gaps between them.
        float2 cell = frac(q * 3.5) - 0.5;
        centre = length(cell) < 0.1;
        return min(0.36 - length(cell), 0.9 - r);
    }
    if (shape == GRASS_HEAD_SPIKE)
    {
        centre = r < 0.15;
        return 0.45 - r;
    }
    if (shape == GRASS_HEAD_PUFF)
        return 0.42 + 0.04 * cos(a * 11.0) - r;
    if (shape == GRASS_HEAD_CLOVER)
    {
        centre = r < 0.07;
        return 0.3 + 0.7 * pow(abs(cos(1.5 * a)), 0.6) - r;
    }
    centre = r < 0.27;
    return 0.3 + 0.7 * smoothstep(0.1, 0.9, abs(cos(a * petals * 0.5))) - r;
}

// The outline's coverage of a fragment: its edge fraction, which MSAA turns into sample coverage.
float GrassHeadCoverage(float outline)
{
    return saturate(outline / max(fwidth(outline), 1e-4) + 0.5);
}

float GrassHeadPetals(uint seed)
{
    return 11.0 + floor(GrassRandom(seed + 15u) * 6.0);
}

#endif
