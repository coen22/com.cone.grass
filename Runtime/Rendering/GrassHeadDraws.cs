using UnityEngine;

/// <summary>
/// The flower and herb head draws (GrassVariantProfile) that follow the three blade LOD draws: one quad-cross queue and
/// one profile-mesh queue, at indirect arguments 3 and 4, each with its own property block. The contact and motion
/// renderers draw the heads beside the blades with the head shader's own passes (#90).
/// </summary>
public readonly struct GrassHeadDraws
{
    public const int Queues = 2;
    /// <summary>The first head queue's command in the indirect arguments, after the three LOD commands.</summary>
    public const int FirstArgument = 3;

    public readonly Material Material;
    public readonly Mesh[] Meshes;
    public readonly MaterialPropertyBlock[] Properties;
    public readonly int[] Capacities;

    public GrassHeadDraws(Material material, Mesh[] meshes, MaterialPropertyBlock[] properties, int[] capacities)
    {
        Material = material;
        Meshes = meshes;
        Properties = properties;
        Capacities = capacities;
    }

    /// <summary>Whether this head queue draws anything: a head material, a mesh, a property block and capacity.</summary>
    public bool Draws(int queue) =>
        Material && Meshes != null && Properties != null && Capacities != null && queue >= 0 && queue < Queues &&
        queue < Meshes.Length && queue < Properties.Length && queue < Capacities.Length &&
        Meshes[queue] && Properties[queue] != null && Capacities[queue] > 0;

    /// <summary>The head shader's pass of this name when any queue draws, otherwise -1.</summary>
    public int FindPass(string name)
    {
        if (!Material)
            return -1;
        for (int queue = 0; queue < Queues; queue++)
            if (Draws(queue))
                return Material.FindPass(name);
        return -1;
    }

    /// <summary>The byte offset of a head queue's indirect command.</summary>
    public static int ArgumentOffset(int queue) => (FirstArgument + queue) * GraphicsBuffer.IndirectDrawIndexedArgs.size;
}
