#ifndef CONE_GRASS_MOTION_HISTORY_LOOKUP_INCLUDED
#define CONE_GRASS_MOTION_HISTORY_LOOKUP_INCLUDED

StructuredBuffer<uint> _GrassPreviousRootKeys;
StructuredBuffer<float4> _GrassPreviousRoots;

uint GrassHistoryHash(uint value)
{
    value ^= value >> 16;
    value *= 0x85ebca6bu;
    value ^= value >> 13;
    value *= 0xc2b2ae35u;
    value ^= value >> 16;
    return value;
}

uint GrassHistorySeed(float2 positionXZ)
{
    return GrassHistoryHash(asuint(positionXZ.x)) ^ GrassHistoryHash(asuint(positionXZ.y) + 0x9e3779b9u);
}

bool GrassFindPreviousRoot(float2 positionXZ, uint hashMask, out float4 previous)
{
    previous = 0.0;
    uint seed = GrassHistorySeed(positionXZ);
    uint fingerprint = seed & 0x7f000000u;
    uint slot = seed & hashMask;
    [loop]
    for (uint probe = 0; probe < 128u; ++probe)
    {
        uint key = _GrassPreviousRootKeys[slot];
        if (key == 0u)
            return false;
        if ((key & 0x7f000000u) == fingerprint)
        {
            float4 candidate = _GrassPreviousRoots[(key & 0x00ffffffu) - 1u];
            if (all(asuint(candidate.xz) == asuint(positionXZ)))
            {
                // Hash construction marks all repeated XZ roots as ambiguous.
                // No source ID is available to distinguish stacked surfaces.
                previous = candidate;
                return (key & 0x80000000u) == 0u;
            }
        }
        slot = (slot + 1u) & hashMask;
    }
    return false;
}

#endif
