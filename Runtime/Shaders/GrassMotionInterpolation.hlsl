#ifndef CONE_GRASS_MOTION_INTERPOLATION_INCLUDED
#define CONE_GRASS_MOTION_INTERPOLATION_INCLUDED

// Coordinates are the tapered material coordinate (across, normalized height).
// This matches CreateBladeMesh's LL-to-UR diagonal, including its final triangle.
float3 GrassInterpolatePreviousTriangle(float2 shape, float lowerHeight, float upperHeight,
    float3 lowerCenter, float3 lowerSpan, float3 upperCenter, float3 upperSpan)
{
    float t = saturate((shape.y - lowerHeight) / max(upperHeight - lowerHeight, 0.00001));
    float lowerHalf = 0.5 * (1.0 - lowerHeight);
    float upperHalf = 0.5 * (1.0 - upperHeight);
    float diagonalX = lerp(-lowerHalf, upperHalf, t);
    float3 lowerLeft = lowerCenter - lowerSpan;
    float3 upperRight = upperCenter + upperSpan;
    if (shape.x >= diagonalX || upperHalf <= 0.00001)
    {
        float rightWeight = (shape.x + lowerHalf * (1.0 - t) - upperHalf * t) / max(2.0 * lowerHalf, 0.00001);
        return lowerLeft * (1.0 - t - rightWeight) + upperRight * t +
            (lowerCenter + lowerSpan) * rightWeight;
    }
    float upperRightWeight = (shape.x + lowerHalf * (1.0 - t) + upperHalf * t) / max(2.0 * upperHalf, 0.00001);
    return lowerLeft * (1.0 - t) + (upperCenter - upperSpan) * (t - upperRightWeight) +
        upperRight * upperRightWeight;
}

#endif
