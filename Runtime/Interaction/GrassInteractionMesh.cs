using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Draws a recovering interaction field as non-overlapping world-space grid cells.</summary>
public sealed class GrassInteractionMesh : IDisposable
{
    private Dictionary<Vector2Int, byte> cellSet = new Dictionary<Vector2Int, byte>();
    private readonly List<Vector2Int> cells = new List<Vector2Int>();
    private readonly List<Vector3> vertices = new List<Vector3>();
    private readonly List<Color> colors = new List<Color>();
    private readonly List<int> triangles = new List<int>();

    public Mesh Mesh { get; private set; }
    public Material Material { get; private set; }
    public Bounds Bounds { get; private set; }

    public GrassInteractionMesh(Shader shader = null)
    {
        Mesh = new Mesh { name = "Grass Collider Interaction", hideFlags = HideFlags.HideAndDontSave };
        Mesh.MarkDynamic();
        Material candidate;
        if (!object.ReferenceEquals(shader, null))
        {
            if (!shader)
                return;
            candidate = new Material(shader);
        }
        else
        {
            // The retained material includes the default producer in players.
            // Each helper owns only its clone, never the template asset.
            Material template = Resources.Load<Material>("InfiniteGrassInteraction");
            if (!template || !template.shader)
                return;
            candidate = new Material(template);
        }
        if (candidate.FindPass("GrassSlope") < 0)
        {
            CoreUtils.Destroy(candidate);
            return;
        }
        candidate.name = "Grass Collider Interaction";
        candidate.hideFlags = HideFlags.HideAndDontSave;
        Material = candidate;
    }

    /// <summary>Returns true when a valid field contributes visible cells; no camera or clock is owned here.</summary>
    public bool Rebuild(GrassInteractionField field, double now, float recoverySeconds, float strength)
    {
        Clear();
        if (!Mesh || !Material || field == null || field.Count == 0 ||
            double.IsNaN(now) || double.IsInfinity(now) ||
            !Finite(recoverySeconds) || recoverySeconds < 0f || !Finite(strength) || strength <= 0f)
            return false;

        strength = Mathf.Clamp01(strength);
        EnsureCapacity(field.Capacity);
        for (int i = 0; i < field.Count; i++)
        {
            Vector2Int key = field.GetKey(i);
            if (!field.TryGetNode(key, now, recoverySeconds, out _, out _))
                continue;
            // The field reserves signed-index headroom for this one-node halo.
            AddCell(new Vector2Int(key.x - 1, key.y - 1));
            AddCell(new Vector2Int(key.x, key.y - 1));
            AddCell(new Vector2Int(key.x - 1, key.y));
            AddCell(key);
        }

        for (int i = 0; i < cells.Count; i++)
        {
            Vector2Int key = cells[i];
            Vector2Int upper = new Vector2Int(key.x + 1, key.y + 1);
            Vector2 lowerPosition = field.GetPosition(key);
            Vector2 upperPosition = field.GetPosition(upper);
            // Finite halo positions are guaranteed by the field, but a cell
            // smaller than a world-coordinate float ULP can still collapse.
            if (upperPosition.x <= lowerPosition.x || upperPosition.y <= lowerPosition.y)
                continue;
            Color c0 = GetColor(field, key, now, recoverySeconds, strength);
            Color c1 = GetColor(field, new Vector2Int(upper.x, key.y), now, recoverySeconds, strength);
            Color c2 = GetColor(field, new Vector2Int(key.x, upper.y), now, recoverySeconds, strength);
            Color c3 = GetColor(field, upper, now, recoverySeconds, strength);
            if (c0.a <= 0f && c1.a <= 0f && c2.a <= 0f && c3.a <= 0f)
                continue;

            int start = vertices.Count;
            vertices.Add(new Vector3(lowerPosition.x, 0f, lowerPosition.y));
            vertices.Add(new Vector3(upperPosition.x, 0f, lowerPosition.y));
            vertices.Add(new Vector3(lowerPosition.x, 0f, upperPosition.y));
            vertices.Add(new Vector3(upperPosition.x, 0f, upperPosition.y));
            colors.Add(c0);
            colors.Add(c1);
            colors.Add(c2);
            colors.Add(c3);
            triangles.Add(start);
            triangles.Add(start + 2);
            triangles.Add(start + 1);
            triangles.Add(start + 1);
            triangles.Add(start + 2);
            triangles.Add(start + 3);
        }

        if (vertices.Count == 0)
            return false;
        if (!TryCalculateBounds(out Bounds bounds))
        {
            Clear();
            return false;
        }
        Mesh.SetVertices(vertices, 0, vertices.Count, MeshUpdateFlags.DontRecalculateBounds);
        Mesh.SetColors(colors);
        Mesh.SetTriangles(triangles, 0, false);
        Mesh.bounds = Bounds = bounds;
        return true;
    }

    /// <summary>Removes visible output immediately, retaining the reusable native resources.</summary>
    public void Clear()
    {
        cellSet.Clear();
        cells.Clear();
        vertices.Clear();
        colors.Clear();
        triangles.Clear();
        Bounds = default;
        if (Mesh)
        {
            Mesh.Clear();
            Mesh.bounds = default;
        }
    }

    public void Dispose()
    {
        Clear();
        CoreUtils.Destroy(Mesh);
        CoreUtils.Destroy(Material);
        Mesh = null;
        Material = null;
    }

    private void EnsureCapacity(int nodeCapacity)
    {
        int cellCapacity = nodeCapacity * 4;
        if (cells.Capacity >= cellCapacity)
            return;
        cellSet = new Dictionary<Vector2Int, byte>(cellCapacity);
        cells.Capacity = cellCapacity;
        // At MaximumCapacity this is 32,768 vertices, within UInt16 indices.
        vertices.Capacity = cellCapacity * 4;
        colors.Capacity = cellCapacity * 4;
        triangles.Capacity = cellCapacity * 6;
    }

    private void AddCell(Vector2Int key)
    {
        if (cellSet.ContainsKey(key))
            return;
        cellSet.Add(key, 0);
        cells.Add(key);
    }

    private bool TryCalculateBounds(out Bounds bounds)
    {
        float minX = vertices[0].x, maxX = minX;
        float minZ = vertices[0].z, maxZ = minZ;
        for (int i = 1; i < vertices.Count; i++)
        {
            minX = Mathf.Min(minX, vertices[i].x);
            maxX = Mathf.Max(maxX, vertices[i].x);
            minZ = Mathf.Min(minZ, vertices[i].z);
            maxZ = Mathf.Max(maxZ, vertices[i].z);
        }
        double width = (double)maxX - minX;
        double depth = (double)maxZ - minZ;
        bounds = default;
        if (width > float.MaxValue || depth > float.MaxValue ||
            !TryGetOutwardAxis(minX, maxX, out float centerX, out float extentX) ||
            !TryGetOutwardAxis(minZ, maxZ, out float centerZ, out float extentZ))
            return false;
        bounds.center = new Vector3(centerX, 0f, centerZ);
        bounds.extents = new Vector3(extentX, 0f, extentZ);
        return true;
    }

    private static bool TryGetOutwardAxis(float minimum, float maximum, out float center, out float extent)
    {
        // As in GrassDispatchMath, round the extent away from the rounded
        // center. Nearest-rounded center/size can exclude an emitted endpoint.
        center = (float)(((double)minimum + maximum) * 0.5);
        double requiredExtent = Math.Max((double)center - minimum, (double)maximum - center);
        extent = (float)requiredExtent;
        if ((double)extent < requiredExtent)
            extent = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(extent) + 1);
        // Also verify the actual float center +/- extent representation used by
        // Bounds.min/max, including ranges with endpoints at different scales.
        if (center - extent > minimum || center + extent < maximum)
            extent = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(extent) + 1);
        return Finite(center - extent) && Finite(center + extent) &&
            center - extent <= minimum && center + extent >= maximum;
    }

    private static Color GetColor(GrassInteractionField field, Vector2Int key,
        double now, float recoverySeconds, float strength)
    {
        if (!field.TryGetNode(key, now, recoverySeconds, out Vector2 direction, out float weight))
            return Color.clear;
        // Premultiply before vertex interpolation so an expiring node's
        // direction vanishes continuously beside neighbors that remain live.
        // The dedicated capture pass writes this value without another multiply.
        float alpha = weight * strength;
        return new Color((direction.x * 0.5f + 0.5f) * alpha,
            (direction.y * 0.5f + 0.5f) * alpha, 0f, alpha);
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
