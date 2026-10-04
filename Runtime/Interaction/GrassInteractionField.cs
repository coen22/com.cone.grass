using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>A bounded world-space field of the last time and pressure a collider footprint covered each grid node.</summary>
public sealed class GrassInteractionField
{
    public const int DefaultCapacity = 1024;
    public const int MaximumCapacity = 2048;
    public const int MaximumSweepCandidates = 4096;

    private struct Node
    {
        public Vector2Int Key;
        public Vector2 Direction;
        public double LastContactTime;
        public float ContactStrength;
    }

    private readonly Node[] nodes;
    private readonly Dictionary<Vector2Int, int> indices;

    public float CellSize { get; }
    public int Capacity => nodes.Length;
    public Vector2 Origin { get; }
    public int Count { get; private set; }

    public GrassInteractionField(float cellSize, int capacity = DefaultCapacity, Vector2 origin = default)
    {
        if (!Finite(cellSize) || cellSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(cellSize));
        if (capacity <= 0 || capacity > MaximumCapacity)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        if (!Finite(origin))
            throw new ArgumentOutOfRangeException(nameof(origin));
        CellSize = cellSize;
        Origin = origin;
        nodes = new Node[capacity];
        indices = new Dictionary<Vector2Int, int>(capacity);
    }

    /// <summary>Dense indices are valid until the next mutation; pruning may reorder them.</summary>
    public Vector2Int GetKey(int index)
    {
        if ((uint)index >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return nodes[index].Key;
    }

    public Vector2 GetPosition(Vector2Int key) => new Vector2(
        (float)(Origin.x + (double)key.x * CellSize),
        (float)(Origin.y + (double)key.y * CellSize));

    /// <summary>Returns the pressure stored at departure multiplied by its current recovery weight.</summary>
    public bool TryGetNode(Vector2Int key, double now, float recovery,
        out Vector2 direction, out float strength)
    {
        direction = Vector2.zero;
        strength = 0f;
        if (!indices.TryGetValue(key, out int index))
            return false;
        float weight = nodes[index].ContactStrength * RecoveryWeight(now - nodes[index].LastContactTime, recovery);
        if (weight <= 0f)
            return false;
        direction = nodes[index].Direction;
        strength = weight;
        return true;
    }

    /// <summary>
    /// Applies the whole linear center/radius sweep, including its last contact at each node.
    /// A rejected input, work budget or capacity leaves every existing node unchanged.
    /// Support changes, teleports and session resets are handled by the owning component.
    /// </summary>
    public bool AdvanceSweptCircle(Vector2 p0, float r0, double t0,
        Vector2 p1, float r1, double t1, Vector2 fallbackDirection)
        => AdvanceSweptCircle(p0, r0, t0, p1, r1, t1, fallbackDirection, 1f, 1f, 0f);

    /// <summary>
    /// Applies a sweep whose pressure approaches a constant target exponentially.
    /// Each node stores the pressure at its own last contact, independently of later actor pressure.
    /// Zero pressure neither adds a node nor erases an earlier recovering contact.
    /// </summary>
    public bool AdvanceSweptCircle(Vector2 p0, float r0, double t0,
        Vector2 p1, float r1, double t1, Vector2 fallbackDirection,
        float responseAtStart, float targetResponse, float attackSeconds)
    {
        if (!ValidCircle(p0, r0) || !ValidCircle(p1, r1) || !Finite(fallbackDirection) ||
            !Finite(t0) || !Finite(t1) || t1 < t0 || !Finite(t1 - t0) ||
            !Finite(responseAtStart) || !Finite(targetResponse) || !Finite(attackSeconds) || attackSeconds < 0f ||
            (t1 == t0 && (p0.x != p1.x || p0.y != p1.y || r0 != r1)))
            return false;
        if (!TryGetRange(p0, r0, p1, r1, out int firstX, out int firstY, out int lastX, out int lastY))
            return false;

        var sweep = new CircleSweep(p0, r0, p1, r1);
        double duration = t1 - t0;
        int additions = 0;
        // The first bounded walk reserves capacity without changing existing
        // timestamps or directions. The second walk cannot fail halfway through.
        for (int y = firstY; y <= lastY; y++)
        for (int x = firstX; x <= lastX; x++)
        {
            var key = new Vector2Int(x, y);
            if (!indices.ContainsKey(key) && sweep.TryGetLatest(
                    Origin.x + (double)x * CellSize, Origin.y + (double)y * CellSize, out double fraction) &&
                EvaluateAttack(responseAtStart, targetResponse, fraction * duration, attackSeconds) > 0f)
            {
                additions++;
                if (additions > Capacity - Count)
                    return false;
            }
        }

        for (int y = firstY; y <= lastY; y++)
        for (int x = firstX; x <= lastX; x++)
        {
            double worldX = Origin.x + (double)x * CellSize;
            double worldY = Origin.y + (double)y * CellSize;
            if (!sweep.TryGetLatest(worldX, worldY, out double fraction))
                continue;
            float contactStrength = EvaluateAttack(responseAtStart, targetResponse, fraction * duration, attackSeconds);
            if (contactStrength <= 0f)
                continue;
            double contactTime = fraction == 1.0 ? t1 : fraction == 0.0 ? t0 : t0 + fraction * duration;
            var key = new Vector2Int(x, y);
            if (!indices.TryGetValue(key, out int index))
            {
                index = Count++;
                indices.Add(key, index);
            }
            else if (contactTime < nodes[index].LastContactTime)
            {
                continue;
            }
            nodes[index] = new Node
            {
                Key = key,
                LastContactTime = contactTime,
                ContactStrength = contactStrength,
                Direction = sweep.GetDirection(worldX, worldY, fraction, fallbackDirection)
            };
        }
        return true;
    }

    /// <summary>Removes nodes only when their stored pressure times the evaluated recovery weight is zero.</summary>
    public int Prune(double now, float recovery)
    {
        if (!Finite(now) || !Finite(recovery) || recovery < 0f)
            return 0;
        int removed = 0;
        for (int i = Count - 1; i >= 0; i--)
        {
            if (nodes[i].ContactStrength * RecoveryWeight(now - nodes[i].LastContactTime, recovery) > 0f)
                continue;
            indices.Remove(nodes[i].Key);
            int last = --Count;
            if (i != last)
            {
                nodes[i] = nodes[last];
                indices[nodes[i].Key] = i;
            }
            nodes[last] = default;
            removed++;
        }
        return removed;
    }

    public void Clear()
    {
        indices.Clear();
        Array.Clear(nodes, 0, Count);
        Count = 0;
    }

    /// <summary>Clamped pressure after an elapsed interval with a constant target; zero attack means immediate contact.</summary>
    public static float EvaluateAttack(float responseAtStart, float targetResponse, double elapsed, float attackSeconds)
    {
        if (!Finite(responseAtStart) || !Finite(targetResponse) || !Finite(elapsed) || elapsed < 0.0 ||
            !Finite(attackSeconds) || attackSeconds < 0f)
            return 0f;
        float start = Mathf.Clamp01(responseAtStart);
        float target = Mathf.Clamp01(targetResponse);
        if (attackSeconds == 0f || start == target)
            return target;
        if (elapsed == 0.0)
            return start;
        return (float)(target + ((double)start - target) * Math.Exp(-elapsed / attackSeconds));
    }

    public static float RecoveryWeight(double age, float recovery)
    {
        if (!Finite(age) || age < 0.0 || !Finite(recovery) || recovery < 0f)
            return 0f;
        if (recovery == 0f)
            return age == 0.0 ? 1f : 0f;
        if (age >= recovery)
            return 0f;
        // Evaluate the complementary smoothstep directly so subtracting two
        // almost equal values cannot turn the final part of recovery into zero.
        double remaining = (recovery - age) / recovery;
        return (float)(remaining * remaining * (3.0 - 2.0 * remaining));
    }

    /// <summary>Latest fraction in [0,1] at which a linearly moving/changing circular footprint covers a point.</summary>
    public static bool TryGetLatestContact(Vector2 point, Vector2 p0, float r0,
        Vector2 p1, float r1, out double fraction)
    {
        fraction = 0.0;
        return Finite(point) && ValidCircle(p0, r0) && ValidCircle(p1, r1) &&
            new CircleSweep(p0, r0, p1, r1).TryGetLatest(point.x, point.y, out fraction);
    }

    private bool TryGetRange(Vector2 p0, float r0, Vector2 p1, float r1,
        out int firstX, out int firstY, out int lastX, out int lastY)
    {
        firstX = firstY = 0;
        lastX = lastY = -1;
        // With linear centers and radii, all four p +/- r bounds are linear too.
        double x0 = Math.Ceiling((Math.Min((double)p0.x - r0, (double)p1.x - r1) - Origin.x) / CellSize);
        double y0 = Math.Ceiling((Math.Min((double)p0.y - r0, (double)p1.y - r1) - Origin.y) / CellSize);
        double x1 = Math.Floor((Math.Max((double)p0.x + r0, (double)p1.x + r1) - Origin.x) / CellSize);
        double y1 = Math.Floor((Math.Max((double)p0.y + r0, (double)p1.y + r1) - Origin.y) / CellSize);
        if (x0 > x1 || y0 > y1)
            return true;
        // Leave room for the mesh's one-node zero-alpha halo. Check before any
        // integer conversion, product or traversal; a long diagonal is still bounded.
        if (x0 <= int.MinValue || y0 <= int.MinValue || x1 >= int.MaxValue || y1 >= int.MaxValue)
            return false;
        long width = (long)x1 - (long)x0 + 1L;
        long height = (long)y1 - (long)y0 + 1L;
        if (width > MaximumSweepCandidates || height > MaximumSweepCandidates ||
            width * height > MaximumSweepCandidates)
            return false;
        if (!Finite((float)(Origin.x + (x0 - 1.0) * CellSize)) ||
            !Finite((float)(Origin.y + (y0 - 1.0) * CellSize)) ||
            !Finite((float)(Origin.x + (x1 + 1.0) * CellSize)) ||
            !Finite((float)(Origin.y + (y1 + 1.0) * CellSize)))
            return false;
        firstX = (int)x0;
        firstY = (int)y0;
        lastX = (int)x1;
        lastY = (int)y1;
        return true;
    }

    private readonly struct CircleSweep
    {
        private readonly double x0, y0, dx, dy, radius0, radius1, dr, quadratic;

        public CircleSweep(Vector2 p0, float r0, Vector2 p1, float r1)
        {
            x0 = p0.x;
            y0 = p0.y;
            dx = (double)p1.x - p0.x;
            dy = (double)p1.y - p0.y;
            radius0 = r0;
            radius1 = r1;
            dr = (double)r1 - r0;
            quadratic = dx * dx + dy * dy - dr * dr;
        }

        public bool TryGetLatest(double x, double y, out double fraction)
        {
            fraction = 0.0;
            double mx = x - x0;
            double my = y - y0;
            double endX = mx - dx;
            double endY = my - dy;
            if (endX * endX + endY * endY <= radius1 * radius1)
            {
                fraction = 1.0;
                return true;
            }

            double projection = mx * dx + my * dy + radius0 * dr;
            double constant = mx * mx + my * my - radius0 * radius0;
            if (quadratic == 0.0)
            {
                if (projection == 0.0)
                    return false;
                double root = constant / (2.0 * projection);
                if (root < 0.0 || root > 1.0)
                    return false;
                fraction = root;
                return true;
            }

            // This is B^2/4 - A*C in geometric form. For a constant radius,
            // it avoids cancelling a large along-path distance at a tangent.
            double radialX = radius0 * dx + dr * mx;
            double radialY = radius0 * dy + dr * my;
            double cross = mx * dy - my * dx;
            double discriminant = radialX * radialX + radialY * radialY - cross * cross;
            if (discriminant < 0.0)
                return false;
            double squareRoot = Math.Sqrt(discriminant);
            double q = projection + (projection >= 0.0 ? squareRoot : -squareRoot);
            double root0 = q / quadratic;
            double root1 = q == 0.0 ? root0 : constant / q;
            double latest = -1.0;
            if (root0 >= 0.0 && root0 <= 1.0)
                latest = root0;
            if (root1 >= 0.0 && root1 <= 1.0)
                latest = Math.Max(latest, root1);
            if (latest < 0.0)
                return false;
            fraction = latest;
            return true;
        }

        public Vector2 GetDirection(double x, double y, double fraction, Vector2 fallback)
        {
            double radialX = x - (x0 + fraction * dx);
            double radialY = y - (y0 + fraction * dy);
            if (radialX == 0.0 && radialY == 0.0)
            {
                radialX = fallback.x;
                radialY = fallback.y;
            }
            double length = Math.Sqrt(radialX * radialX + radialY * radialY);
            return length > 0.0
                ? new Vector2((float)(radialX / length), (float)(radialY / length))
                : Vector2.zero;
        }
    }

    private static bool ValidCircle(Vector2 center, float radius) => Finite(center) && Finite(radius) && radius > 0f;
    private static bool Finite(Vector2 value) => Finite(value.x) && Finite(value.y);
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
