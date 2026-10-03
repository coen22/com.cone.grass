using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

/// <summary>
/// Editor-only, unlit ground-albedo capture for the native URP Terrain/Lit shader.
/// The saved texture maps the complete Terrain XZ extent to UV 0..1. No source
/// texture needs CPU readability; only the completed GPU capture is read back.
/// </summary>
[InitializeOnLoad]
public static class TerrainGrassAlbedoBaker
{
    public const int MinimumResolution = 16;
    public const int MaximumResolution = 4096;
    public const string NativeTerrainShaderName = "Universal Render Pipeline/Terrain/Lit";
    private const string BakeShaderName = "Hidden/InfiniteGrass/Editor/TerrainAlbedoBake";
    // Increment when the output contract changes, invalidating adapter caches.
    public const int BakerVersion = 1;
    private const double SignatureRetentionSeconds = 600d;
    private sealed class TerrainSourceState
    {
        public Terrain Terrain;
        public uint Revision, Generation;
        public double LastUsed;
        public HashSet<string> Paths = new HashSet<string>(StringComparer.Ordinal);
        public HashSet<string> CurrentPaths = new HashSet<string>(StringComparer.Ordinal);
    }
    private sealed class SourceImportState
    {
        public uint Revision;
        public int References;
    }
    private static readonly Dictionary<EntityId, TerrainSourceState> terrainSources = new Dictionary<EntityId, TerrainSourceState>();
    private static readonly Dictionary<string, SourceImportState> importRevisions =
        new Dictionary<string, SourceImportState>(StringComparer.Ordinal);
    private static readonly List<EntityId> expiredSources = new List<EntityId>();
    private static uint sourceGeneration;
    private static double nextSourceCleanup;
    private static uint undoRevision;

    /// <summary>Raised after the texture asset has been updated and saved, even when its identity is unchanged.</summary>
    public static event Action<Terrain, Texture2D> Baked;
    /// <summary>Terrain control maps changed, or a caller explicitly invalidated their terrain.</summary>
    public static event Action<Terrain> SourceChanged;

    static TerrainGrassAlbedoBaker()
    {
        TerrainCallbacks.textureChanged += OnTerrainTextureChanged;
        Undo.undoRedoPerformed += OnUndoRedo;
        EditorApplication.update += CleanupSourceCache;
    }

    /// <summary>
    /// A cheap editor-session signature, independent of the output asset path.
    /// Uses object properties, texture update counts, terrain callbacks and source
    /// import revisions; it never reads pixels or hashes texture contents.
    /// </summary>
    public static bool TryGetSourceSignature(Terrain terrain, int resolution, out Hash128 signature, out string error)
    {
        signature = default;
        if (!TryGetInputs(terrain, resolution, out Material material, out TerrainLayer[] layers, out error))
            return false;

        TerrainData data = terrain.terrainData;
        TerrainSourceState tracked = TrackTerrain(terrain);
        tracked.CurrentPaths.Clear();
        StringBuilder state = new StringBuilder(512 + layers.Length * 160);
        Append(state, BakerVersion);
        Append(state, resolution);
        Append(state, (int)QualitySettings.activeColorSpace);
        Append(state, undoRevision.GetHashCode());
        AppendObject(state, terrain, tracked);
        AppendObject(state, data, tracked);
        Append(state, tracked.Generation.GetHashCode());
        Append(state, tracked.Revision.GetHashCode());
        Append(state, data.size.x.GetHashCode());
        Append(state, data.size.z.GetHashCode());
        AppendObject(state, material, tracked);
        AppendObject(state, material.shader, tracked);
        Append(state, material.IsKeywordEnabled("_TERRAIN_BLEND_HEIGHT") ? 1 : 0);
        Append(state, material.GetFloat("_HeightTransition").GetHashCode());
        Append(state, layers.Length);
        for (int i = 0; i < data.alphamapTextureCount; i++)
            AppendTexture(state, data.GetAlphamapTexture(i), tracked);
        foreach (TerrainLayer layer in layers)
        {
            AppendObject(state, layer, tracked);
            Append(state, layer.tileSize.x.GetHashCode());
            Append(state, layer.tileSize.y.GetHashCode());
            Append(state, layer.tileOffset.x.GetHashCode());
            Append(state, layer.tileOffset.y.GetHashCode());
            AppendVector(state, layer.diffuseRemapMin);
            AppendVector(state, layer.diffuseRemapMax);
            AppendVector(state, layer.maskMapRemapMin);
            AppendVector(state, layer.maskMapRemapMax);
            AppendTexture(state, layer.diffuseTexture, tracked);
            AppendTexture(state, layer.maskMapTexture, tracked);
        }
        FinishTrackingPaths(tracked);
        signature = Hash128.Compute(state.ToString());
        return true;
    }

    /// <summary>Notify after source GPU edits that do not invoke Unity's Terrain texture callback.</summary>
    public static void Invalidate(Terrain terrain)
    {
        if (!terrain)
            return;
        EntityId id = terrain.GetEntityId();
        // Terrains with no previous signature have no cache to invalidate.
        // Generation callbacks must not retain every terrain ever opened.
        if (terrainSources.TryGetValue(id, out TerrainSourceState tracked) && tracked.Terrain == terrain)
            tracked.Revision = unchecked(tracked.Revision + 1);
        SourceChanged?.Invoke(terrain);
    }

    /// <summary>
    /// Bake to a linear RGBAHalf Texture2D .asset below Assets/. An existing main
    /// Texture2D asset is updated in place, preserving its GUID and references.
    /// This explicitly supports the native URP Terrain/Lit material only.
    /// </summary>
    public static bool TryBake(Terrain terrain, int resolution, string assetPath,
        out Texture2D texture, out string error)
    {
        texture = null;
        if (!TryGetInputs(terrain, resolution, out Material source, out TerrainLayer[] layers, out error))
            return false;
        if (!TryValidateOutputPath(assetPath, layers, out assetPath, out Texture2D existing, out error))
            return false;
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ||
            !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf) ||
            !SystemInfo.SupportsTextureFormat(TextureFormat.RGBAHalf))
        {
            error = "Ground-albedo baking needs an Editor graphics device with RGBAHalf render and texture support.";
            return false;
        }

        Shader shader = Shader.Find(BakeShaderName);
        if (!shader || !shader.isSupported || ShaderUtil.ShaderHasError(shader))
        {
            error = "The grass TerrainLit albedo bake shader is unavailable or failed to compile. Check the Unity shader console.";
            return false;
        }

        Material material = null;
        RenderTexture capture = null;
        Texture2D staging = null;
        CommandBuffer commands = null;
        RenderTexture previousActive = RenderTexture.active;
        bool previousSRGBWrite = GL.sRGBWrite;
        try
        {
            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            if (source.IsKeywordEnabled("_TERRAIN_BLEND_HEIGHT"))
                material.EnableKeyword("_TERRAIN_BLEND_HEIGHT");
            foreach (TerrainLayer layer in layers)
            {
                if (layer.maskMapTexture)
                {
                    material.EnableKeyword("_MASKMAP");
                    break;
                }
            }
            ShaderUtil.CompilePass(material, 0, true);
            if (ShaderUtil.ShaderHasError(shader))
                throw new InvalidOperationException("The TerrainLit albedo bake shader failed to compile.");

            capture = RenderTexture.GetTemporary(resolution, resolution, 0,
                RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            capture.filterMode = FilterMode.Bilinear;
            capture.wrapMode = TextureWrapMode.Clamp;
            commands = new CommandBuffer { name = "Bake terrain grass ground albedo" };
            commands.SetRenderTarget(capture);
            commands.SetViewport(new Rect(0, 0, resolution, resolution));
            commands.ClearRenderTarget(false, true, Color.clear);

            TerrainData data = terrain.terrainData;
            for (int group = 0; group < (layers.Length + 3) / 4; group++)
            {
                MaterialPropertyBlock properties = BuildGroupProperties(data, source, layers, group);
                commands.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3, 1, properties);
            }

            GL.sRGBWrite = false;
            Graphics.ExecuteCommandBuffer(commands);
            RenderTexture.active = capture;
            staging = new Texture2D(resolution, resolution, TextureFormat.RGBAHalf, true, true)
            {
                name = existing ? existing.name : Path.GetFileNameWithoutExtension(assetPath),
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 1
            };
            staging.ReadPixels(new Rect(0, 0, resolution, resolution), 0, 0, false);
            staging.Apply(true, false);

            if (existing)
            {
                Undo.RegisterCompleteObjectUndo(existing, "Bake terrain grass ground albedo");
                EditorUtility.CopySerialized(staging, existing);
                EditorUtility.SetDirty(existing);
                texture = existing;
            }
            else
            {
                AssetDatabase.CreateAsset(staging, assetPath);
                texture = staging;
                staging = null;
            }
            AssetDatabase.SaveAssetIfDirty(texture);
        }
        catch (Exception exception)
        {
            error = "Could not bake terrain ground albedo: " + exception.Message;
            texture = null;
            return false;
        }
        finally
        {
            RenderTexture.active = previousActive;
            GL.sRGBWrite = previousSRGBWrite;
            commands?.Release();
            if (capture)
                RenderTexture.ReleaseTemporary(capture);
            if (material)
                Object.DestroyImmediate(material);
            if (staging)
                Object.DestroyImmediate(staging);
        }

        error = null;
        NotifyBoundAreas(texture);
        // A subscriber failure must not turn a successfully saved bake into a reported failure.
        try { Baked?.Invoke(terrain, texture); }
        catch (Exception exception) { Debug.LogException(exception); }
        return true;
    }

    private static void NotifyBoundAreas(Texture2D texture)
    {
        // CopySerialized preserves asset identity and may reset its updateCount.
        // Notify all consumers explicitly, including areas outside this baker's UI.
        List<GrassPlacementArea> affected = new List<GrassPlacementArea>();
        IReadOnlyList<GrassPlacementArea> areas = GrassPlacementArea.ActiveAreas;
        for (int i = 0; i < areas.Count; i++)
            if (areas[i] && areas[i].GroundColorTexture == texture)
                affected.Add(areas[i]);
        foreach (GrassPlacementArea area in affected)
        {
            if (!area)
                continue;
            try { area.MarkGroundColorDirty(); }
            catch (Exception exception) { Debug.LogException(exception); }
        }
    }

    private static MaterialPropertyBlock BuildGroupProperties(TerrainData data, Material source,
        TerrainLayer[] layers, int group)
    {
        MaterialPropertyBlock properties = new MaterialPropertyBlock();
        Texture2D control = data.GetAlphamapTexture(group);
        properties.SetTexture("_Control", control);
        properties.SetVector("_Control_TexelSize", new Vector4(1f / control.width, 1f / control.height,
            control.width, control.height));
        properties.SetFloat("_NumLayersCount", layers.Length);
        properties.SetFloat("_HeightTransition", source.GetFloat("_HeightTransition"));
        properties.SetFloat("_BakeAdditionalGroup", group > 0 ? 1f : 0f);
        for (int slot = 0; slot < 4; slot++)
        {
            int index = group * 4 + slot;
            TerrainLayer layer = index < layers.Length ? layers[index] : null;
            string suffix = slot.ToString();
            Texture2D diffuse = layer && layer.diffuseTexture ? layer.diffuseTexture : Texture2D.grayTexture;
            Texture2D mask = layer && layer.maskMapTexture ? layer.maskMapTexture : Texture2D.grayTexture;
            properties.SetTexture("_Splat" + suffix, diffuse);
            properties.SetTexture("_Mask" + suffix, mask);
            properties.SetFloat("_LayerHasMask" + suffix, layer && layer.maskMapTexture ? 1f : 0f);
            properties.SetVector("_Splat" + suffix + "_ST", layer ?
                new Vector4(data.size.x / layer.tileSize.x, data.size.z / layer.tileSize.y,
                    layer.tileOffset.x / layer.tileSize.x, layer.tileOffset.y / layer.tileSize.y) :
                new Vector4(1f, 1f, 0f, 0f));
            properties.SetVector("_DiffuseRemapScale" + suffix,
                layer ? layer.diffuseRemapMax - layer.diffuseRemapMin : Vector4.zero);
            properties.SetVector("_MaskMapRemapScale" + suffix,
                layer ? layer.maskMapRemapMax - layer.maskMapRemapMin : Vector4.one);
            properties.SetVector("_MaskMapRemapOffset" + suffix,
                layer ? layer.maskMapRemapMin : Vector4.zero);
        }
        return properties;
    }

    private static bool TryGetInputs(Terrain terrain, int resolution, out Material material,
        out TerrainLayer[] layers, out string error)
    {
        material = null;
        layers = null;
        if (!terrain || !terrain.terrainData)
        {
            error = "Assign a Terrain with TerrainData before baking ground albedo.";
            return false;
        }
        if (resolution < MinimumResolution || resolution > MaximumResolution)
        {
            error = "Bake resolution must be between " + MinimumResolution + " and " + MaximumResolution + ".";
            return false;
        }
        if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset pipeline))
        {
            error = "Ground-albedo baking supports a project using the Universal Render Pipeline.";
            return false;
        }
        material = terrain.materialTemplate ? terrain.materialTemplate : pipeline.defaultTerrainMaterial;
        if (!material || !material.shader || material.shader.name != NativeTerrainShaderName)
        {
            error = "Ground-albedo baking supports only the native '" + NativeTerrainShaderName +
                "' shader. Custom terrain materials, Shader Graph terrain shaders and MicroSplat need their own final-albedo output.";
            return false;
        }
        TerrainData data = terrain.terrainData;
        if (!Finite(data.size.x) || !Finite(data.size.z) || data.size.x <= 0f || data.size.z <= 0f ||
            !Finite(material.GetFloat("_HeightTransition")))
        {
            error = "The terrain size or material height-transition setting is invalid.";
            return false;
        }
        layers = data.terrainLayers;
        if (layers.Length == 0 || data.alphamapTextureCount < (layers.Length + 3) / 4)
        {
            error = "The Terrain needs terrain layers and their generated control maps before baking.";
            return false;
        }
        for (int i = 0; i < layers.Length; i++)
        {
            TerrainLayer layer = layers[i];
            if (!layer || !Finite(layer.tileSize.x) || !Finite(layer.tileSize.y) ||
                Mathf.Abs(layer.tileSize.x) < 0.00001f || Mathf.Abs(layer.tileSize.y) < 0.00001f ||
                !Finite(layer.tileOffset.x) || !Finite(layer.tileOffset.y) ||
                !Finite(layer.diffuseRemapMin) || !Finite(layer.diffuseRemapMax) ||
                !Finite(layer.maskMapRemapMin) || !Finite(layer.maskMapRemapMax))
            {
                error = "Terrain layer " + i + " is missing or has invalid tiling/remap values.";
                return false;
            }
        }
        for (int i = 0; i < (layers.Length + 3) / 4; i++)
        {
            if (!data.GetAlphamapTexture(i))
            {
                error = "Terrain control map " + i + " is unavailable.";
                return false;
            }
        }
        error = null;
        return true;
    }

    private static bool TryValidateOutputPath(string path, TerrainLayer[] layers, out string normalized,
        out Texture2D existing, out string error)
    {
        normalized = (path ?? string.Empty).Replace('\\', '/');
        existing = null;
        if (!normalized.StartsWith("Assets/", StringComparison.Ordinal) ||
            !normalized.EndsWith(".asset", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/../") || normalized.Contains("/./") || normalized.Contains("//") ||
            !AssetDatabase.IsValidFolder(Path.GetDirectoryName(normalized)?.Replace('\\', '/')))
        {
            error = "Choose a Texture2D .asset path in an existing folder below Assets/.";
            return false;
        }
        Object current = AssetDatabase.LoadMainAssetAtPath(normalized);
        if (current && !(current is Texture2D))
        {
            error = "The output path already contains an asset that is not a Texture2D.";
            return false;
        }
        foreach (TerrainLayer layer in layers)
        {
            if ((layer.diffuseTexture && AssetDatabase.GetAssetPath(layer.diffuseTexture) == normalized) ||
                (layer.maskMapTexture && AssetDatabase.GetAssetPath(layer.maskMapTexture) == normalized))
            {
                error = "The baked texture cannot replace one of the Terrain's source textures.";
                return false;
            }
        }
        existing = current as Texture2D;
        error = null;
        return true;
    }

    private static void Append(StringBuilder state, int value) => state.Append(value).Append('|');
    private static void AppendVector(StringBuilder state, Vector4 value)
    {
        Append(state, value.x.GetHashCode()); Append(state, value.y.GetHashCode());
        Append(state, value.z.GetHashCode()); Append(state, value.w.GetHashCode());
    }
    private static void AppendObject(StringBuilder state, Object value, TerrainSourceState tracked)
    {
        Append(state, value ? value.GetEntityId().GetHashCode() : 0);
        if (!value)
            return;
        Append(state, EditorUtility.GetDirtyCount(value));
        string path = AssetDatabase.GetAssetPath(value);
        if (string.IsNullOrEmpty(path))
            return;
        if (!importRevisions.TryGetValue(path, out SourceImportState imported))
        {
            imported = new SourceImportState();
            importRevisions.Add(path, imported);
        }
        if (tracked.CurrentPaths.Add(path) && !tracked.Paths.Contains(path))
            imported.References++;
        Append(state, imported.Revision.GetHashCode());
    }
    private static void AppendTexture(StringBuilder state, Texture texture, TerrainSourceState tracked)
    {
        AppendObject(state, texture, tracked);
        if (!texture)
            return;
        Append(state, texture.updateCount.GetHashCode());
        Append(state, texture.width); Append(state, texture.height);
        Append(state, (int)texture.filterMode); Append(state, (int)texture.wrapMode);
        Append(state, texture.mipMapBias.GetHashCode());
    }
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static bool Finite(Vector4 value) => Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);
    private static void OnTerrainTextureChanged(Terrain terrain, string textureName, RectInt region, bool synched) => Invalidate(terrain);
    private static void OnUndoRedo() { unchecked { undoRevision++; } }

    private static TerrainSourceState TrackTerrain(Terrain terrain)
    {
        EntityId id = terrain.GetEntityId();
        if (terrainSources.TryGetValue(id, out TerrainSourceState tracked) && tracked.Terrain != terrain)
        {
            ReleaseSourcePaths(tracked);
            terrainSources.Remove(id);
            tracked = null;
        }
        if (tracked == null)
        {
            tracked = new TerrainSourceState
            {
                Terrain = terrain,
                // Recreating a dormant cache entry must change its signature,
                // even if an unreported GPU edit left the object counters alone.
                Generation = unchecked(++sourceGeneration)
            };
            terrainSources.Add(id, tracked);
        }
        tracked.LastUsed = EditorApplication.timeSinceStartup;
        return tracked;
    }

    private static void FinishTrackingPaths(TerrainSourceState tracked)
    {
        foreach (string path in tracked.Paths)
            if (!tracked.CurrentPaths.Contains(path))
                ReleasePath(path);
        HashSet<string> old = tracked.Paths;
        tracked.Paths = tracked.CurrentPaths;
        tracked.CurrentPaths = old;
        tracked.CurrentPaths.Clear();
    }

    private static void ReleasePath(string path)
    {
        if (importRevisions.TryGetValue(path, out SourceImportState imported) && --imported.References <= 0)
            importRevisions.Remove(path);
    }

    private static void ReleaseSourcePaths(TerrainSourceState tracked)
    {
        foreach (string path in tracked.Paths)
            ReleasePath(path);
        foreach (string path in tracked.CurrentPaths)
            if (!tracked.Paths.Contains(path))
                ReleasePath(path);
    }

    private static void CleanupSourceCache()
    {
        double now = EditorApplication.timeSinceStartup;
        if (now < nextSourceCleanup)
            return;
        nextSourceCleanup = now + 30d;
        foreach (KeyValuePair<EntityId, TerrainSourceState> entry in terrainSources)
            if (!entry.Value.Terrain || now - entry.Value.LastUsed > SignatureRetentionSeconds)
                expiredSources.Add(entry.Key);
        foreach (EntityId id in expiredSources)
        {
            ReleaseSourcePaths(terrainSources[id]);
            terrainSources.Remove(id);
        }
        expiredSources.Clear();
    }

    internal static void NotifyImportedAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
    {
        BumpTrackedPaths(imported); BumpTrackedPaths(deleted); BumpTrackedPaths(moved); BumpTrackedPaths(movedFrom);
    }
    private static void BumpTrackedPaths(string[] paths)
    {
        foreach (string path in paths)
            if (importRevisions.TryGetValue(path, out SourceImportState imported))
                imported.Revision = unchecked(imported.Revision + 1);
    }
}

public sealed class TerrainGrassAlbedoSourcePostprocessor : AssetPostprocessor
{
    private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
    {
        TerrainGrassAlbedoBaker.NotifyImportedAssets(imported, deleted, moved, movedFrom);
    }
}
