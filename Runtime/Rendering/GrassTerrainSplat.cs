using UnityEngine;

/// <summary>
/// Binds a painted terrain's first control map and its first four layers for the blade shader,
/// so a blade can take TerrainLit's albedo, normal detail, smoothness and metallic at its root (GrassTerrainSplat in
/// GrassBladeCommon.hlsl). Without a usable terrain the shader falls back to the ground capture.
/// </summary>
internal static class GrassTerrainSplat
{
    private const int Layers = 4;
    private static readonly int TerrainRect = Shader.PropertyToID("_GrassSplatTerrain");
    private static readonly int Control = Shader.PropertyToID("_GrassSplatControl");
    private static readonly int ControlTexelSize = Shader.PropertyToID("_GrassSplatControl_TexelSize");
    private static readonly int Tile = Shader.PropertyToID("_GrassSplatTile");
    private static readonly int Remap = Shader.PropertyToID("_GrassSplatRemap");
    private static readonly int Widths = Shader.PropertyToID("_GrassSplatWidths");
    private static readonly int Surface = Shader.PropertyToID("_GrassSplatSurface");
    private static readonly int[] Diffuse =
    {
        Shader.PropertyToID("_GrassSplatDiffuse0"), Shader.PropertyToID("_GrassSplatDiffuse1"),
        Shader.PropertyToID("_GrassSplatDiffuse2"), Shader.PropertyToID("_GrassSplatDiffuse3")
    };
    private static readonly int[] Normal =
    {
        Shader.PropertyToID("_GrassSplatNormal0"), Shader.PropertyToID("_GrassSplatNormal1"),
        Shader.PropertyToID("_GrassSplatNormal2"), Shader.PropertyToID("_GrassSplatNormal3")
    };
    private static readonly Vector4[] tiles = new Vector4[Layers];
    private static readonly Vector4[] remaps = new Vector4[Layers];
    private static readonly Vector4[] surfaces = new Vector4[Layers];

    public static void Bind(MaterialPropertyBlock properties, Terrain terrain)
    {
        TerrainData data = terrain ? terrain.terrainData : null;
        Texture2D control = data && data.alphamapTextureCount > 0 ? data.GetAlphamapTexture(0) : null;
        TerrainLayer[] layers = control ? data.terrainLayers : null;
        bool usable = layers != null && layers.Length > 0;
        Vector3 origin = usable ? terrain.transform.position : Vector3.zero;
        Vector3 size = usable ? data.size : Vector3.zero;
        properties.SetVector(TerrainRect, new Vector4(origin.x, origin.z, size.x, size.z));
        properties.SetTexture(Control, usable ? control : Texture2D.blackTexture);
        properties.SetVector(ControlTexelSize, usable
            ? new Vector4(1f / control.width, 1f / control.height, control.width, control.height)
            : Vector4.one);
        Vector4 widths = Vector4.one;
        for (int i = 0; i < Layers; i++)
        {
            TerrainLayer layer = usable && i < layers.Length ? layers[i] : null;
            Texture2D diffuse = layer ? layer.diffuseTexture : null;
            Vector2 tileSize = layer ? layer.tileSize : Vector2.one;
            tileSize = new Vector2(Mathf.Max(tileSize.x, 0.0001f), Mathf.Max(tileSize.y, 0.0001f));
            Vector2 offset = layer ? layer.tileOffset : Vector2.zero;
            // TerrainLit's layer UV is (position - terrain origin + tile offset) / tile size.
            tiles[i] = new Vector4(1f / tileSize.x, 1f / tileSize.y,
                (offset.x - origin.x) / tileSize.x, (offset.y - origin.z) / tileSize.y);
            // TerrainLit scales the albedo by (remap max - remap min) without adding the minimum.
            Vector4 scale = layer ? layer.diffuseRemapMax - layer.diffuseRemapMin : Vector4.zero;
            remaps[i] = new Vector4(scale.x, scale.y, scale.z, layer ? layer.normalScale : 1f);
            widths[i] = diffuse ? diffuse.width : 1f;
            // TerrainLit's smoothness constant, its source (TerrainLitPasses SplatmapMix) and metallic.
            surfaces[i] = layer ? new Vector4(layer.smoothness, (float)layer.smoothnessSource, layer.metallic, 0f)
                : Vector4.zero;
            properties.SetTexture(Diffuse[i], diffuse ? diffuse : Texture2D.whiteTexture);
            properties.SetTexture(Normal[i], layer && layer.normalMapTexture ? layer.normalMapTexture : Texture2D.normalTexture);
        }
        properties.SetVectorArray(Tile, tiles);
        properties.SetVectorArray(Remap, remaps);
        properties.SetVectorArray(Surface, surfaces);
        properties.SetVector(Widths, widths);
    }
}
