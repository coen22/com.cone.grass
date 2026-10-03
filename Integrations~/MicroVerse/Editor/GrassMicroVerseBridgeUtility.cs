using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>
/// Resolves documented saved texture subassets using public Unity APIs.
/// It does not assume a MicroVerse assembly, getter or generation event name.
/// </summary>
public static class GrassMicroVerseBridgeUtility
{
    private const string GroundOutputConflictMessage =
        "Use a different ground-albedo output asset for each Terrain and resolution. Another bridge already uses this path.";

    private sealed class MaskAssets
    {
        public ScriptableObject Target;
        public int DirtyCount;
        public Hash128 Dependency;
        public Texture2D[] Textures;
    }

    private struct TextureState : IEquatable<TextureState>
    {
        public EntityId Entity;
        // Cache ownership only; moving an unchanged asset is not a pixel edit.
        public string AssetPath;
        public int Width, Height, DirtyCount, ImportRevision, Format;
        public uint UpdateCount;
        public Hash128 Dependency;

        public bool Equals(TextureState other) =>
            Entity == other.Entity && Width == other.Width && Height == other.Height &&
            DirtyCount == other.DirtyCount && ImportRevision == other.ImportRevision &&
            Format == other.Format && UpdateCount == other.UpdateCount && Dependency == other.Dependency;
    }

    private struct ObservedInputs
    {
        public TextureState Density, GroundColor;
    }

    private struct GroundBakeState
    {
        public Hash128 SourceSignature;
        public string AssetPath, SavedKey;
        public int AssetRevision;
        public bool SourcesSaved;
    }

    private static readonly Dictionary<string, MaskAssets> masks =
        new Dictionary<string, MaskAssets>(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> importRevisions =
        new Dictionary<string, int>(StringComparer.Ordinal);
    private static readonly Dictionary<GrassMicroVerseBridge, ObservedInputs> observed =
        new Dictionary<GrassMicroVerseBridge, ObservedInputs>();
    private static readonly Dictionary<GrassMicroVerseBridge, GroundBakeState> groundBakes =
        new Dictionary<GrassMicroVerseBridge, GroundBakeState>();
    private static readonly HashSet<GrassMicroVerseBridge> activeRefreshes =
        new HashSet<GrassMicroVerseBridge>();
    private static readonly List<string> unusedMaskPaths = new List<string>();
    private static readonly HashSet<ScriptableObject> liveMaskTargets = new HashSet<ScriptableObject>();
    private static readonly List<GrassMicroVerseBridge> unusedBridges = new List<GrassMicroVerseBridge>();
    private static readonly HashSet<string> usedImportPaths = new HashSet<string>(StringComparer.Ordinal);
    private static readonly List<string> unusedImportPaths = new List<string>();
    private static int assetRevision;

    /// <summary>Returns the cached list; callers must not modify the array.</summary>
    public static Texture2D[] GetTextureSubAssets(GrassMicroVerseBridge bridge, bool forceRediscover = false)
    {
        if (!bridge || !bridge.MaskTarget)
            return Array.Empty<Texture2D>();

        string path = AssetDatabase.GetAssetPath(bridge.MaskTarget);
        if (string.IsNullOrEmpty(path))
            return Array.Empty<Texture2D>();

        int dirtyCount = EditorUtility.GetDirtyCount(bridge.MaskTarget);
        // This queries Unity's cached asset dependency hash, not texture pixels.
        Hash128 dependency = AssetDatabase.GetAssetDependencyHash(path);
        if (!forceRediscover && masks.TryGetValue(path, out MaskAssets existing) &&
            existing.Target == bridge.MaskTarget && existing.DirtyCount == dirtyCount &&
            existing.Dependency == dependency && AllAlive(existing.Textures))
            return existing.Textures;

        PruneUnusedMaskCaches();
        var textures = new List<Texture2D>();
        foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(path))
        {
            if (asset is Texture2D texture && AssetDatabase.IsSubAsset(texture))
                textures.Add(texture);
        }
        textures.Sort((left, right) => string.CompareOrdinal(left.name, right.name));
        var entry = new MaskAssets
        {
            Target = bridge.MaskTarget,
            DirtyCount = dirtyCount,
            Dependency = dependency,
            Textures = textures.ToArray()
        };
        masks[path] = entry;
        if (!importRevisions.ContainsKey(path))
            importRevisions.Add(path, 0);
        return entry.Textures;
    }

    public static void InvalidateAssetPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        masks.Remove(path);
        if (importRevisions.TryGetValue(path, out int revision))
            importRevisions[path] = unchecked(revision + 1);
        // A save can change a persisted dependency hash without changing the
        // producer's in-memory update counter. Recheck the saved bake key.
        assetRevision = unchecked(assetRevision + 1);
    }

    public static void ClearCaches()
    {
        masks.Clear();
        observed.Clear();
        groundBakes.Clear();
        importRevisions.Clear();
        unusedMaskPaths.Clear();
        liveMaskTargets.Clear();
        unusedBridges.Clear();
        usedImportPaths.Clear();
        unusedImportPaths.Clear();
        assetRevision = 0;
    }

    public static void Forget(GrassMicroVerseBridge bridge)
    {
        observed.Remove(bridge);
        groundBakes.Remove(bridge);
        PruneUnusedMaskCaches();
    }

    /// <summary>
    /// Releases observations made by manual refresh as well as automatic polls.
    /// Called periodically by the editor watcher; it performs no texture reads.
    /// </summary>
    public static void PruneInactiveCaches()
    {
        unusedBridges.Clear();
        foreach (GrassMicroVerseBridge bridge in observed.Keys)
            if (!OwnsLiveCache(bridge)) unusedBridges.Add(bridge);
        foreach (GrassMicroVerseBridge bridge in groundBakes.Keys)
            if (!OwnsLiveCache(bridge) && !observed.ContainsKey(bridge)) unusedBridges.Add(bridge);
        foreach (GrassMicroVerseBridge bridge in unusedBridges)
        {
            observed.Remove(bridge);
            groundBakes.Remove(bridge);
        }
        unusedBridges.Clear();
        PruneUnusedMaskCaches();

        usedImportPaths.Clear();
        foreach (string path in masks.Keys)
            usedImportPaths.Add(path);
        foreach (ObservedInputs inputs in observed.Values)
        {
            if (!string.IsNullOrEmpty(inputs.Density.AssetPath)) usedImportPaths.Add(inputs.Density.AssetPath);
            if (!string.IsNullOrEmpty(inputs.GroundColor.AssetPath)) usedImportPaths.Add(inputs.GroundColor.AssetPath);
        }
        unusedImportPaths.Clear();
        foreach (string path in importRevisions.Keys)
            if (!usedImportPaths.Contains(path)) unusedImportPaths.Add(path);
        foreach (string path in unusedImportPaths)
            importRevisions.Remove(path);
        unusedImportPaths.Clear();
        usedImportPaths.Clear();
    }

    private static bool OwnsLiveCache(GrassMicroVerseBridge bridge) =>
        bridge && bridge.isActiveAndEnabled && bridge.gameObject.scene.IsValid() &&
        !EditorSceneManager.IsPreviewScene(bridge.gameObject.scene);

    internal static void PruneUnusedMaskCaches()
    {
        liveMaskTargets.Clear();
        foreach (GrassMicroVerseBridge bridge in GrassMicroVerseBridge.ActiveBridges)
            if (OwnsLiveCache(bridge) && bridge.MaskTarget) liveMaskTargets.Add(bridge.MaskTarget);
        unusedMaskPaths.Clear();
        foreach (KeyValuePair<string, MaskAssets> entry in masks)
        {
            if (!liveMaskTargets.Contains(entry.Value.Target))
                unusedMaskPaths.Add(entry.Key);
        }
        foreach (string path in unusedMaskPaths)
        {
            masks.Remove(path);
            importRevisions.Remove(path);
        }
        unusedMaskPaths.Clear();
        liveMaskTargets.Clear();
    }

    public static bool TryResolve(GrassMicroVerseBridge bridge, out Texture2D texture,
        out string message, bool forceRediscover = false)
    {
        texture = null;
        if (!bridge || !bridge.Terrain || !bridge.Terrain.terrainData)
        {
            message = "Assign the Terrain that owns this generated mask.";
            return false;
        }
        if (!bridge.MaskTarget || string.IsNullOrEmpty(AssetDatabase.GetAssetPath(bridge.MaskTarget)))
        {
            message = "Assign a saved MicroVerse MaskTarget asset.";
            return false;
        }
        if (string.IsNullOrEmpty(bridge.TextureSubAssetName))
        {
            message = "Choose this terrain's generated density texture. Save the MicroVerse scene first if the list is empty.";
            return false;
        }

        int matches = 0;
        foreach (Texture2D candidate in GetTextureSubAssets(bridge, forceRediscover))
        {
            // Exact, user-selected names only. Subasset order and partial terrain
            // names do not establish which tile a generated texture belongs to.
            if (!candidate || !string.Equals(candidate.name, bridge.TextureSubAssetName, StringComparison.Ordinal))
                continue;
            texture = candidate;
            matches++;
        }
        if (matches != 1)
        {
            message = matches == 0
                ? "The selected texture is missing. Finish MicroVerse generation, save, then select its replacement."
                : "Several textures have this name. Give the terrain and mask unique names, regenerate, then select the correct texture.";
            texture = null;
            return false;
        }
        if (GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat))
        {
            message = "Density is linear data. Set the MaskTarget format to R8 or linear RGBA, regenerate, then refresh.";
            texture = null;
            return false;
        }
        GraphicsFormat format = texture.graphicsFormat;
        if (format == GraphicsFormat.None || GraphicsFormatUtility.IsAlphaOnlyFormat(format) ||
            GraphicsFormatUtility.IsIntegerFormat(format) || GraphicsFormatUtility.IsDepthStencilFormat(format) ||
            texture.format == TextureFormat.Alpha8)
        {
            // The placement shader uses ordinary filtered float sampling of .r.
            // Alpha8 can retain legacy swizzling independently of graphicsFormat;
            // storing alpha alone does not supply this red-channel contract.
            message = "Density needs a normalized or floating-point red channel. Use R8 or linear RGBA; alpha-only, integer and depth formats are unsupported.";
            texture = null;
            return false;
        }
        if (bridge.GroundLayer && !HasTerrainLayer(bridge.Terrain, bridge.GroundLayer))
        {
            message = "The selected ground TerrainLayer is absent from this terrain. Apply the matching Texture Stamp and save first.";
            texture = null;
            return false;
        }
        message = $"Bound {texture.name} ({texture.width} x {texture.height}) to {bridge.Terrain.name}.";
        return true;
    }

    /// <summary>
    /// Binds saved output, or clears coverage on an unresolved configuration.
    /// Polls pass forceInvalidate=false: unchanged textures do no capture work.
    /// Explicit refresh also handles GPU producers that do not advance updateCount.
    /// </summary>
    public static bool Refresh(GrassMicroVerseBridge bridge, bool recordUndo = false,
        bool markSceneDirty = true, bool forceInvalidate = true, bool forceGroundBake = false)
    {
        if (!bridge || !bridge.PlacementArea)
            return false;

        if (!activeRefreshes.Add(bridge))
        {
            bridge.SetRefreshResult(false,
                "This bridge is already refreshing. Queue another refresh after the current callback finishes.");
            return false;
        }
        try
        {
            if (bridge.BakeTerrainGroundColor &&
                TerrainGrassAlbedoBaker.IsBakingOutput(ResolveGroundBakeOutput(bridge, out _)))
            {
                // Another bridge can legitimately share this terrain's output.
                // A temporary ownership conflict is not missing producer data:
                // retain its committed density and color until a later refresh.
                bridge.SetRefreshResult(false,
                    "This ground-albedo output is still being baked. Queue a refresh after the current callback finishes.");
                return false;
            }
            return RefreshBindings(bridge, recordUndo, markSceneDirty, forceInvalidate, forceGroundBake);
        }
        finally
        {
            activeRefreshes.Remove(bridge);
        }
    }

    private static bool RefreshBindings(GrassMicroVerseBridge bridge, bool recordUndo,
        bool markSceneDirty, bool forceInvalidate, bool forceGroundBake)
    {
        bool valid = TryResolve(bridge, out Texture2D density, out string message, forceInvalidate);
        Texture2D color = valid ? bridge.GroundColorOverride : null;
        if (valid && bridge.BakeTerrainGroundColor)
            valid = TryRefreshGroundBake(bridge, forceGroundBake, recordUndo, markSceneDirty, out color, out message);

        // A synchronous bake observer can delete the authoring object. Its
        // saved output survives, but there is no remaining placement to commit.
        if (!bridge || !bridge.PlacementArea)
        {
            if (bridge)
                bridge.SetRefreshResult(false, message);
            return false;
        }

        GrassPlacementArea area = bridge.PlacementArea;
        Texture desiredDensity = valid ? density : null;
        TerrainLayer desiredLayer = valid ? bridge.GroundLayer : null;
        Texture desiredColor = valid ? color : null;
        float strength = GroundStrength(bridge, desiredLayer, desiredColor);
        bool bindingChanged = !MatchesPlacement(bridge, desiredDensity, desiredLayer, desiredColor, strength);
        if (bindingChanged)
        {
            if (recordUndo)
                Undo.RecordObject(area, "Refresh MicroVerse grass mask");
            area.ConfigureTexture(bridge.Terrain, desiredDensity, desiredLayer, desiredColor);
            area.ConfigureGroundLayer(desiredLayer, strength);
            area.SetGroundColorTexture(desiredColor, true, strength);
            area.SetGroundColor(bridge.GroundTint, strength);
        }

        var current = new ObservedInputs
        {
            Density = Observe(desiredDensity),
            GroundColor = Observe(desiredColor ? desiredColor : desiredLayer ? desiredLayer.diffuseTexture : null)
        };
        bool known = observed.TryGetValue(bridge, out ObservedInputs previous);
        if (forceInvalidate || !known || !current.Density.Equals(previous.Density))
            area.MarkCoverageDirty();
        if (forceGroundBake || !known || !current.GroundColor.Equals(previous.GroundColor))
            area.MarkGroundColorDirty();
        observed[bridge] = current;

        if (markSceneDirty && bindingChanged)
            MarkAuthoringObjectDirty(area);
        bridge.SetRefreshResult(valid, valid && bridge.BakeTerrainGroundColor
            ? $"Bound {density.name}; terrain ground albedo is baked at {bridge.GroundBakeResolution} x {bridge.GroundBakeResolution}."
            : message);
        return valid;
    }

    /// <summary>
    /// Read-only build check against serialized placement inputs. This is run
    /// before every player build, even when Unity reuses cached scene data.
    /// </summary>
    public static bool TryValidateForBuild(GrassMicroVerseBridge bridge, out string message)
    {
        if (!TryResolve(bridge, out Texture2D density, out message, true))
            return false;
        if (!IsSaved(bridge.MaskTarget, out message) || !IsSaved(density, out message) ||
            !IsSaved(bridge.Terrain.terrainData, out message) ||
            (bridge.GroundLayer && !IsSaved(bridge.GroundLayer, out message)))
            return false;

        Texture2D groundColor = bridge.GroundColorOverride;
        if (bridge.BakeTerrainGroundColor)
        {
            groundColor = bridge.BakedGroundColor;
            if (!groundColor || !IsSaved(groundColor, out message))
            {
                message = "Bake the terrain ground albedo, then refresh and save the scene.";
                return false;
            }
            if (!TryGetSavedGroundSourceKey(bridge, out string sourceKey, out message) ||
                !GroundSourcesAreSaved(bridge, out message))
                return false;
            if (bridge.GroundBakeSourceKey != sourceKey ||
                AssetDatabase.GetAssetPath(groundColor) != bridge.GroundBakeAssetPath ||
                bridge.GroundBakeOutputKey != AssetDatabase.GetAssetDependencyHash(bridge.GroundBakeAssetPath).ToString() ||
                groundColor.width != bridge.GroundBakeResolution || groundColor.height != bridge.GroundBakeResolution)
            {
                message = "The saved ground-albedo bake is stale. Finish generation, save its source assets, bake, then save the scene.";
                return false;
            }
        }
        else if (groundColor && !IsSaved(groundColor, out message))
            return false;
        else if (!groundColor && bridge.GroundLayer && bridge.GroundLayer.diffuseTexture)
        {
            if (!IsSaved(bridge.GroundLayer.diffuseTexture, out message) ||
                !GroundLayerSamplerIsSaved(bridge, out message))
                return false;
        }

        float strength = GroundStrength(bridge, bridge.GroundLayer, groundColor);
        if (!MatchesPlacement(bridge, density, bridge.GroundLayer, groundColor, strength))
        {
            message = "Saved placement inputs differ from the current generated outputs. Open this scene, refresh the bridge and save it before building.";
            return false;
        }
        message = null;
        return true;
    }

    private static bool TryRefreshGroundBake(GrassMicroVerseBridge bridge, bool forceBake,
        bool recordUndo, bool markSceneDirty, out Texture2D texture, out string message)
    {
        string path = ResolveGroundBakeOutput(bridge, out texture);
        if (string.IsNullOrEmpty(path))
        {
            message = "Choose a ground-albedo output .asset under Assets before enabling automatic baking.";
            return false;
        }
        foreach (GrassMicroVerseBridge other in GrassMicroVerseBridge.ActiveBridges)
        {
            if (ConflictsWithGroundOutput(bridge, other, path))
            {
                message = GroundOutputConflictMessage;
                return false;
            }
        }
        if (!TerrainGrassAlbedoBaker.TryGetSourceSignature(bridge.Terrain, bridge.GroundBakeResolution,
            out Hash128 signature, out message))
            return false;

        bool known = groundBakes.TryGetValue(bridge, out GroundBakeState previous);
        bool sourcesSaved = GroundSourcesAreSaved(bridge, out _);
        string outputKey = texture ? AssetDatabase.GetAssetDependencyHash(path).ToString() : string.Empty;
        if (!forceBake && known && texture && previous.AssetPath == path &&
            previous.SourceSignature == signature && previous.SavedKey == bridge.GroundBakeSourceKey &&
            bridge.GroundBakeOutputKey == outputKey &&
            previous.AssetRevision == assetRevision &&
            previous.SourcesSaved == sourcesSaved &&
            path == bridge.GroundBakeAssetPath && texture.width == bridge.GroundBakeResolution &&
            texture.height == bridge.GroundBakeResolution)
            return true;
        if (!TryGetSavedGroundSourceKey(bridge, out string sourceKey, out message))
            return false;
        // A live GPU bake can preview dirty source assets, but their previous
        // disk hash cannot certify the new image for a later editor session or
        // build. Keep an invalid durable key until the sources have been saved.
        if (!sourcesSaved)
            sourceKey = string.Empty;

        bool sourceChanged = known && previous.SourceSignature != signature;
        bool needsBake = forceBake || !texture || sourceChanged || bridge.GroundBakeSourceKey != sourceKey ||
            (!known && !sourcesSaved) || (known && previous.SourcesSaved != sourcesSaved) ||
            bridge.GroundBakeOutputKey != outputKey ||
            texture.width != bridge.GroundBakeResolution || texture.height != bridge.GroundBakeResolution;
        if (needsBake)
        {
            // Inactive scene objects can still be enabled in the player. They
            // do not enter ActiveBridges, but their saved output must not be
            // overwritten by another terrain or resolution. Scan only before
            // an actual bake, rather than on every unchanged editor poll.
            if (HasLoadedSceneOutputConflict(bridge, path))
            {
                message = GroundOutputConflictMessage;
                return false;
            }
            if (!TerrainGrassAlbedoBaker.TryBake(bridge.Terrain, bridge.GroundBakeResolution,
                path, out texture, out message))
                return false;
            if (!bridge || !bridge.PlacementArea)
            {
                texture = null;
                message = "The grass bridge or placement area was removed during the ground bake.";
                return false;
            }
        }

        // The baker normalizes separators and Unity resolves paths without case
        // sensitivity. Persist the actual asset path, including after creation,
        // so a pasted Windows path cannot bypass ownership checks or stay stale.
        path = AssetDatabase.GetAssetPath(texture);
        outputKey = AssetDatabase.GetAssetDependencyHash(path).ToString();
        if (bridge.BakedGroundColor != texture || bridge.GroundBakeAssetPath != path ||
            bridge.GroundBakeSourceKey != sourceKey || bridge.GroundBakeOutputKey != outputKey)
        {
            if (recordUndo)
                Undo.RecordObject(bridge, "Update MicroVerse ground-albedo binding");
            bridge.SetBakedGroundColor(texture, path, sourceKey, outputKey);
            if (markSceneDirty)
                MarkAuthoringObjectDirty(bridge);
        }
        // Keep the signature of the inputs that produced this image. Saving the
        // output and notifying Baked subscribers can also edit source assets;
        // adopting their later signature would certify those edits without ever
        // baking them. The next poll must observe that change and bake again.
        groundBakes[bridge] = new GroundBakeState
        {
            SourceSignature = signature, AssetPath = path, SavedKey = sourceKey, AssetRevision = assetRevision,
            SourcesSaved = sourcesSaved
        };
        message = null;
        return true;
    }

    private static bool TryGetSavedGroundSourceKey(GrassMicroVerseBridge bridge,
        out string key, out string message)
    {
        key = null;
        if (!TerrainGrassAlbedoBaker.TryGetSourceSignature(bridge.Terrain, bridge.GroundBakeResolution, out _, out message))
            return false;

        TerrainData data = bridge.Terrain.terrainData;
        Material material = GetTerrainMaterial(bridge.Terrain);
        var state = new StringBuilder();
        state.Append(TerrainGrassAlbedoBaker.BakerVersion.ToString(CultureInfo.InvariantCulture)).Append('|');
        state.Append(bridge.GroundBakeResolution.ToString(CultureInfo.InvariantCulture)).Append('|');
        state.Append(((int)QualitySettings.activeColorSpace).ToString(CultureInfo.InvariantCulture)).Append('|');
        AppendFloat(state, data.size.x);
        AppendFloat(state, data.size.z);
        if (!AppendSavedAsset(state, data, out message) ||
            !AppendSavedAsset(state, material, out message) ||
            !AppendSavedAsset(state, material.shader, out message) ||
            !AppendSavedAsset(state, Shader.Find(TerrainGrassAlbedoBaker.BakeShaderName), out message))
            return false;
        state.Append(material.IsKeywordEnabled("_TERRAIN_BLEND_HEIGHT") ? '1' : '0').Append('|');
        AppendFloat(state, material.GetFloat("_HeightTransition"));
        foreach (TerrainLayer layer in data.terrainLayers)
        {
            if (!AppendSavedAsset(state, layer, out message) ||
                !AppendSavedAsset(state, layer.diffuseTexture, out message) ||
                !AppendSavedAsset(state, layer.maskMapTexture, out message))
                return false;
            AppendFloat(state, layer.tileSize.x); AppendFloat(state, layer.tileSize.y);
            AppendFloat(state, layer.tileOffset.x); AppendFloat(state, layer.tileOffset.y);
            AppendVector(state, layer.diffuseRemapMin); AppendVector(state, layer.diffuseRemapMax);
            AppendVector(state, layer.maskMapRemapMin); AppendVector(state, layer.maskMapRemapMax);
        }
        key = Hash128.Compute(state.ToString()).ToString();
        message = null;
        return true;
    }

    private static bool AppendSavedAsset(StringBuilder state, Object asset, out string message)
    {
        message = null;
        if (!asset)
        {
            state.Append("null|");
            return true;
        }
        string path = AssetDatabase.GetAssetPath(asset);
        if (string.IsNullOrEmpty(path) ||
            !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long localId))
        {
            message = $"Save '{asset.name}' as a project asset before baking reproducible ground albedo.";
            return false;
        }
        state.Append(guid).Append(':').Append(localId.ToString(CultureInfo.InvariantCulture)).Append(':');
        state.Append(AssetDatabase.GetAssetDependencyHash(path)).Append('|');
        return true;
    }

    private static bool GroundSourcesAreSaved(GrassMicroVerseBridge bridge, out string message)
    {
        TerrainData data = bridge.Terrain.terrainData;
        if (!IsSaved(data, out message) || !IsSaved(GetTerrainMaterial(bridge.Terrain), out message))
            return false;
        foreach (TerrainLayer layer in data.terrainLayers)
        {
            if (!IsSaved(layer, out message) ||
                (layer.diffuseTexture && !IsSaved(layer.diffuseTexture, out message)) ||
                (layer.maskMapTexture && !IsSaved(layer.maskMapTexture, out message)))
                return false;
        }
        for (int i = 0; i < data.alphamapTextureCount; i++)
            if (!IsSaved(data.GetAlphamapTexture(i), out message)) return false;
        message = null;
        return true;
    }

    private static bool GroundLayerSamplerIsSaved(GrassMicroVerseBridge bridge, out string message)
    {
        // Mirror the core's first matching layer and four-layer group rule using
        // serialized dependencies only. Calling TryGetCaptureData here would
        // mutate observations and incorrectly require nonempty grass coverage.
        TerrainLayer[] layers = bridge.Terrain.terrainData.terrainLayers;
        for (int index = 0; index < layers.Length; index++)
        {
            if (layers[index] != bridge.GroundLayer)
                continue;
            TerrainLayer samplerLayer = layers[index / 4 * 4];
            if (samplerLayer && (!IsSaved(samplerLayer, out message) ||
                (samplerLayer.diffuseTexture && !IsSaved(samplerLayer.diffuseTexture, out message))))
                return false;
            break;
        }
        // A missing group-first layer/diffuse uses Unity's built-in gray sampler.
        // A color override wins before this helper and does not consume it.
        message = null;
        return true;
    }

    private static bool IsSaved(Object asset, out string message)
    {
        if (!asset || string.IsNullOrEmpty(AssetDatabase.GetAssetPath(asset)) || EditorUtility.IsDirty(asset))
        {
            message = $"Save generated/source asset '{(asset ? asset.name : "(missing)")}' before building.";
            return false;
        }
        message = null;
        return true;
    }

    private static Material GetTerrainMaterial(Terrain terrain) =>
        terrain.materialTemplate ? terrain.materialTemplate : GraphicsSettings.currentRenderPipeline.defaultTerrainMaterial;

    private static string ResolveGroundBakeOutput(GrassMicroVerseBridge bridge, out Texture2D texture)
    {
        texture = bridge.BakedGroundColor;
        // Follow a moved output only while the reference still owns its main
        // asset. A detached/replaced reference must not hide the configured
        // recovery path or be reused as a completed saved bake.
        if (TerrainGrassAlbedoBaker.TryGetOutputAssetPath(texture, out string path))
            return path;
        texture = null;
        return CanonicalAssetPath(bridge.GroundBakeAssetPath);
    }

    private static string CanonicalAssetPath(string path)
    {
        string normalized = (path ?? string.Empty).Replace('\\', '/');
        if (string.IsNullOrEmpty(normalized))
            return normalized;
        string guid = AssetDatabase.AssetPathToGUID(normalized, AssetPathToGUIDOptions.OnlyExistingAssets);
        return string.IsNullOrEmpty(guid) ? normalized : AssetDatabase.GUIDToAssetPath(guid);
    }

    private static bool SameAssetPath(string left, string right) =>
        !string.IsNullOrEmpty(left) && !string.IsNullOrEmpty(right) &&
        string.Equals(left.Replace('\\', '/'), right.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static bool ConflictsWithGroundOutput(GrassMicroVerseBridge bridge,
        GrassMicroVerseBridge other, string path) =>
        other && other != bridge && other.BakeTerrainGroundColor &&
        (other.enabled || other.BakedGroundColor) && other.gameObject.scene.IsValid() &&
        !EditorSceneManager.IsPreviewScene(other.gameObject.scene) &&
        (other.Terrain != bridge.Terrain || other.GroundBakeResolution != bridge.GroundBakeResolution) &&
        ((other.BakedGroundColor && SameAssetPath(AssetDatabase.GetAssetPath(other.BakedGroundColor), path)) ||
            SameAssetPath(other.GroundBakeAssetPath, path));

    private static bool HasLoadedSceneOutputConflict(GrassMicroVerseBridge bridge, string path)
    {
        for (int index = 0; index < SceneManager.sceneCount; index++)
        {
            Scene scene = SceneManager.GetSceneAt(index);
            if (!scene.isLoaded || EditorSceneManager.IsPreviewScene(scene))
                continue;
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (GrassMicroVerseBridge other in root.GetComponentsInChildren<GrassMicroVerseBridge>(true))
                    if (ConflictsWithGroundOutput(bridge, other, path)) return true;
        }
        return false;
    }

    private static void AppendFloat(StringBuilder state, float value) =>
        state.Append(value.ToString("R", CultureInfo.InvariantCulture)).Append('|');

    private static void AppendVector(StringBuilder state, Vector4 value)
    {
        AppendFloat(state, value.x); AppendFloat(state, value.y);
        AppendFloat(state, value.z); AppendFloat(state, value.w);
    }

    private static float GroundStrength(GrassMicroVerseBridge bridge, TerrainLayer layer, Texture color) =>
        (layer || color) && !float.IsNaN(bridge.GroundColorStrength) && !float.IsInfinity(bridge.GroundColorStrength)
            ? Mathf.Clamp01(bridge.GroundColorStrength) : 0f;

    private static bool MatchesPlacement(GrassMicroVerseBridge bridge, Texture density, TerrainLayer layer,
        Texture color, float strength)
    {
        GrassPlacementArea area = bridge.PlacementArea;
        return area && area.Shape == GrassPlacementShape.Texture && area.UsesTerrainBounds &&
            area.Terrain == bridge.Terrain && !area.DensityAsset && area.DensityTexture == density &&
            area.GroundLayer == layer && area.GroundColorTexture == color &&
            (!color || area.GroundColorUsesTerrainBounds) &&
            area.GroundTint == bridge.GroundTint && area.GroundColorStrength == strength && area.EdgeFalloff == 0f;
    }

    private static TextureState Observe(Texture texture)
    {
        if (!texture)
            return default;
        string path = AssetDatabase.GetAssetPath(texture);
        int revision = 0;
        if (!string.IsNullOrEmpty(path) && !importRevisions.TryGetValue(path, out revision))
            importRevisions.Add(path, 0);
        return new TextureState
        {
            Entity = texture.GetEntityId(), AssetPath = path, Width = texture.width, Height = texture.height,
            DirtyCount = EditorUtility.GetDirtyCount(texture), ImportRevision = revision,
            UpdateCount = texture.updateCount, Format = (int)texture.graphicsFormat,
            Dependency = string.IsNullOrEmpty(path) ? default : AssetDatabase.GetAssetDependencyHash(path)
        };
    }

    private static bool AllAlive(Texture2D[] textures)
    {
        foreach (Texture2D texture in textures)
            if (!texture) return false;
        return true;
    }

    private static bool HasTerrainLayer(Terrain terrain, TerrainLayer layer)
    {
        foreach (TerrainLayer candidate in terrain.terrainData.terrainLayers)
            if (candidate == layer) return true;
        return false;
    }

    private static void MarkAuthoringObjectDirty(Component component)
    {
        EditorUtility.SetDirty(component);
        PrefabUtility.RecordPrefabInstancePropertyModifications(component);
        if (component.gameObject.scene.IsValid() && component.gameObject.scene.isLoaded &&
            !EditorSceneManager.IsPreviewScene(component.gameObject.scene))
            EditorSceneManager.MarkSceneDirty(component.gameObject.scene);
    }
}
