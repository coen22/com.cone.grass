using System;
using UnityEngine;

/// <summary>Integer-safe grid and work-budget calculations shared by grass generation.</summary>
public static class GrassDispatchMath
{
    public const int MaximumDispatchCells = 65535 * 8;
    public const long MaximumCandidates = 64L * 1024 * 1024;
    private const double MaximumCellCoordinate = int.MaxValue / 2.0 - 256;

    /// <summary>
    /// Finds all cell centers whose half-cell jitter can intersect the XZ bounds.
    /// Bounds are conservative at equality; the shader owns the final surface test.
    /// </summary>
    public static bool TryGetGridRange(Bounds bounds, float spacing, out GrassGridRange range)
    {
        range = default;
        if (!Finite(spacing) || spacing <= 0f)
            return false;
        Vector3 min = bounds.min;
        Vector3 max = bounds.max;
        if (!Finite(min.x) || !Finite(min.z) || !Finite(max.x) || !Finite(max.z) ||
            min.x >= max.x || min.z >= max.z)
            return false;

        double x0 = Math.Ceiling((double)min.x / spacing - 0.5);
        double z0 = Math.Ceiling((double)min.z / spacing - 0.5);
        double x1 = Math.Floor((double)max.x / spacing + 0.5) + 1.0;
        double z1 = Math.Floor((double)max.z / spacing + 0.5) + 1.0;
        if (x0 < -MaximumCellCoordinate || z0 < -MaximumCellCoordinate ||
            x1 > MaximumCellCoordinate || z1 > MaximumCellCoordinate ||
            x1 <= x0 || z1 <= z0)
            return false;

        range = new GrassGridRange((int)x0, (int)z0, (int)(x1 - x0), (int)(z1 - z0));
        return true;
    }

    public static int FloorDivide(int value, int divisor)
    {
        if (divisor <= 0)
            throw new ArgumentOutOfRangeException(nameof(divisor));
        return value >= 0 ? value / divisor : (int)(((long)value - divisor + 1L) / divisor);
    }

    /// <summary>
    /// Includes the world-space support of a filtered capture texel. Use before
    /// camera intersection and when testing a candidate tile against source occupancy.
    /// </summary>
    public static Bounds ExpandXZ(Bounds bounds, float margin)
    {
        if (!Finite(margin) || margin < 0f || margin > float.MaxValue * 0.5f)
            throw new ArgumentOutOfRangeException(nameof(margin));
        bounds.Expand(new Vector3(margin * 2f, 0f, margin * 2f));
        return bounds;
    }

    /// <summary>A failed reservation leaves the existing budget unchanged.</summary>
    public static bool TryAddCandidateBudget(long current, int width, int height, long limit, out long updated)
    {
        updated = current;
        if (current < 0 || limit < 0 || current > limit || width < 0 || height < 0)
            return false;
        long count = (long)width * height;
        if (count > limit - current)
            return false;
        updated = current + count;
        return true;
    }

    public static int ThreadGroupCount(int cells)
    {
        if (cells < 0 || cells > MaximumDispatchCells)
            throw new ArgumentOutOfRangeException(nameof(cells));
        return (cells + 7) / 8;
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}

public readonly struct GrassGridRange
{
    public readonly int MinX;
    public readonly int MinZ;
    public readonly int Width;
    public readonly int Height;
    public int MaxX => checked(MinX + Width);
    public int MaxZ => checked(MinZ + Height);
    public long CandidateCount => (long)Width * Height;

    public GrassGridRange(int minX, int minZ, int width, int height)
    {
        MinX = minX;
        MinZ = minZ;
        Width = width;
        Height = height;
    }
}
