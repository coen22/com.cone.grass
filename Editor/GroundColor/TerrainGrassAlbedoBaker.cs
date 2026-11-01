using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace Cone.Grass.Editor
{
    /// <summary>
    /// Editor-only, unlit ground-albedo capture for the native URP Terrain/Lit albedo contract.
    /// The saved texture maps the complete Terrain XZ extent to UV 0..1. No source
    /// texture needs CPU readability; only the completed GPU capture is read back.
    /// </summary>
    [InitializeOnLoad]
    public static partial class TerrainGrassAlbedoBaker
    {
        /// <summary>The smallest bake resolution.</summary>
        public const int MinimumResolution = 16;
        /// <summary>The largest bake resolution.</summary>
        public const int MaximumResolution = 4096;
        /// <summary>The native URP terrain shader whose albedo the baker reproduces.</summary>
        public const string NativeTerrainShaderName = "Universal Render Pipeline/Terrain/Lit";
        /// <summary>The editor capture shader that renders the albedo.</summary>
        public const string BakeShaderName = "Hidden/InfiniteGrass/Editor/TerrainAlbedoBake";
        // Custom shaders opt in only when their painted albedo uses unmodified TerrainLit mixing.
        /// <summary>
        /// The shader tag a custom terrain shader sets to <see cref="TerrainAlbedoContract"/> to opt in to baking.
        /// </summary>
        public const string TerrainAlbedoTag = "GrassAlbedo";
        /// <summary>
        /// The <see cref="TerrainAlbedoTag"/> value that declares unmodified TerrainLit albedo mixing.
        /// </summary>
        public const string TerrainAlbedoContract = "TerrainLit";
        // Increment when the output contract changes, invalidating adapter caches.
        /// <summary>The output contract's version; it is part of every source signature.</summary>
        public const int BakerVersion = 2;
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
        private readonly struct BakedOutputState
        {
            private readonly uint _updateCount;
            private readonly int _width, _height, _mipCount, _anisoLevel, _dirtyCount;
            private readonly TextureFormat _format;
            private readonly UnityEngine.Experimental.Rendering.GraphicsFormat _graphicsFormat;
            private readonly FilterMode _filterMode;
            private readonly TextureWrapMode _wrapU, _wrapV;
            private readonly float _mipBias;
            private readonly bool _isReadable, _isDirty;

            public BakedOutputState(Texture2D texture)
            {
                _updateCount = texture.updateCount;
                _width = texture.width;
                _height = texture.height;
                _mipCount = texture.mipmapCount;
                _format = texture.format;
                _graphicsFormat = texture.graphicsFormat;
                _filterMode = texture.filterMode;
                _wrapU = texture.wrapModeU;
                _wrapV = texture.wrapModeV;
                _anisoLevel = texture.anisoLevel;
                _mipBias = texture.mipMapBias;
                _isReadable = texture.isReadable;
                _isDirty = EditorUtility.IsDirty(texture);
                _dirtyCount = EditorUtility.GetDirtyCount(texture);
            }

            public bool Matches(Texture2D texture) => texture &&
                _updateCount == texture.updateCount && _width == texture.width && _height == texture.height &&
                _mipCount == texture.mipmapCount && _format == texture.format && _graphicsFormat == texture.graphicsFormat &&
                _filterMode == texture.filterMode && _wrapU == texture.wrapModeU && _wrapV == texture.wrapModeV &&
                _anisoLevel == texture.anisoLevel && _mipBias == texture.mipMapBias && _isReadable == texture.isReadable &&
                _isDirty == EditorUtility.IsDirty(texture) && _dirtyCount == EditorUtility.GetDirtyCount(texture);
        }
        private static readonly Dictionary<EntityId, TerrainSourceState> s_terrainSources = new Dictionary<EntityId, TerrainSourceState>();
        private static readonly Dictionary<string, SourceImportState> s_importRevisions =
            new Dictionary<string, SourceImportState>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Texture2D> s_activeBakes =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<EntityId> s_expiredSources = new List<EntityId>();
        private static uint s_sourceGeneration;
        private static double s_nextSourceCleanup;
        private static uint s_undoRevision;

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
        /// <param name="terrain">The terrain to bake.</param>
        /// <param name="resolution">The bake resolution.</param>
        /// <param name="signature">The source signature.</param>
        /// <param name="error">Why the terrain cannot be baked, when the method returns false.</param>
        public static bool TryGetSourceSignature(Terrain terrain, int resolution, out Hash128 signature, out string error)
        {
            signature = default;
            if (!TryGetInputs(terrain, resolution, out Material material, out TerrainLayer[] layers, out error))
            {
                return false;
            }

            TerrainData data = terrain.terrainData;
            TerrainSourceState tracked = TrackTerrain(terrain);
            tracked.CurrentPaths.Clear();
            StringBuilder state = new StringBuilder(512 + layers.Length * 160);
            Append(state, BakerVersion);
            Append(state, resolution);
            Append(state, (int)QualitySettings.activeColorSpace);
            Append(state, s_undoRevision.GetHashCode());
            AppendObject(state, terrain, tracked);
            AppendObject(state, data, tracked);
            Append(state, tracked.Generation.GetHashCode());
            Append(state, tracked.Revision.GetHashCode());
            Append(state, data.size.x.GetHashCode());
            Append(state, data.size.z.GetHashCode());
            AppendObject(state, material, tracked);
            AppendObject(state, material.shader, tracked);
            // The capture shader also defines the produced pixels. Track its own
            // asset/imports, not only the native TerrainLit source it includes.
            AppendObject(state, Shader.Find(BakeShaderName), tracked);
            Append(state, material.IsKeywordEnabled("_TERRAIN_BLEND_HEIGHT") ? 1 : 0);
            Append(state, material.GetFloat("_HeightTransition").GetHashCode());
            Append(state, layers.Length);
            for (int i = 0; i < data.alphamapTextureCount; i++)
            {
                AppendTexture(state, data.GetAlphamapTexture(i), tracked);
            }

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
        /// <param name="terrain">The terrain whose sources changed.</param>
        public static void Invalidate(Terrain terrain)
        {
            if (!terrain)
            {
                return;
            }

            EntityId id = terrain.GetEntityId();
            // Terrains with no previous signature have no cache to invalidate.
            // Generation callbacks must not retain every terrain ever opened.
            if (s_terrainSources.TryGetValue(id, out TerrainSourceState tracked) && tracked.Terrain == terrain)
            {
                tracked.Revision = unchecked(tracked.Revision + 1);
            }

            Action<Terrain> subscribers = SourceChanged;
            if (subscribers == null)
            {
                return;
            }
            // Keep producer invalidation and the other consumers independent of a
            // failed observer, just as for notification of a completed bake.
            foreach (Action<Terrain> subscriber in subscribers.GetInvocationList())
            {
                try { subscriber(terrain); }
                catch (Exception exception) { Debug.LogException(exception); }
            }
        }

        /// <summary>
        /// Bake to a linear RGBAHalf Texture2D .asset below Assets/. An existing main
        /// Texture2D asset is updated in place, preserving its GUID and references.
        /// Supports native Terrain/Lit and shaders explicitly declaring its painted-albedo contract.
        /// </summary>
        /// <param name="terrain">The terrain to bake.</param>
        /// <param name="resolution">Square output resolution, from <see cref="MinimumResolution"/> to <see cref="MaximumResolution"/>.</param>
        /// <param name="assetPath">The output .asset path below Assets/.</param>
        /// <param name="texture">The saved texture asset.</param>
        /// <param name="error">Why the bake failed, when the method returns false.</param>
        public static bool TryBake(Terrain terrain, int resolution, string assetPath,
            out Texture2D texture, out string error)
        {
            texture = null;
            if (!TryGetInputs(terrain, resolution, out Material source, out TerrainLayer[] layers, out error))
            {
                return false;
            }

            if (!TryValidateOutputPath(assetPath, layers, out assetPath, out Texture2D existing, out error))
            {
                return false;
            }

            if (!TryGetCaptureShader(out Shader shader, out error))
            {
                return false;
            }

            if (s_activeBakes.ContainsKey(assetPath) || IsActiveBakeOutput(existing))
            {
                error = "A ground-albedo bake already owns this output. Queue another bake after the current callback finishes.";
                return false;
            }
            s_activeBakes.Add(assetPath, existing);
            try
            {
                return TryBakeOutput(terrain, resolution, assetPath, source, layers, existing, shader, out texture, out error);
            }
            finally
            {
                // Hold ownership through saves and notifications: both can invoke
                // authoring callbacks before the caller receives the completed bake.
                s_activeBakes.Remove(assetPath);
            }
        }

        /// <summary>
        /// Reports output ownership through saving and observer notification. Editor
        /// integrations can defer a refresh without treating this temporary state as
        /// missing source data. Aliases and a moved output keep the same ownership.
        /// </summary>
        /// <param name="assetPath">The output asset path.</param>
        /// <returns>Whether a bake currently owns that output.</returns>
        public static bool IsBakingOutput(string assetPath)
        {
            if (s_activeBakes.Count == 0 || string.IsNullOrEmpty(assetPath))
            {
                return false;
            }

            string normalized = assetPath.Replace('\\', '/');
            return s_activeBakes.ContainsKey(normalized) ||
                IsActiveBakeOutput(AssetDatabase.LoadAssetAtPath<Texture2D>(normalized));
        }

        /// <summary>
        /// Resolves the same live main asset after saving or callback-driven moves.
        /// A detached, destroyed or displaced object is no longer a saved output.
        /// </summary>
        /// <param name="texture">A texture returned by a bake.</param>
        /// <param name="assetPath">The texture's current main asset path.</param>
        /// <returns>Whether the texture is still the main asset saved at that path.</returns>
        public static bool TryGetOutputAssetPath(Texture2D texture, out string assetPath)
        {
            assetPath = null;
            if (!texture)
            {
                return false;
            }

            string path = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            Object current = AssetDatabase.LoadMainAssetAtPath(path);
            if (!texture || current != texture)
            {
                return false;
            }

            assetPath = path;
            return true;
        }

        private static bool IsActiveBakeOutput(Texture2D texture)
        {
            if (!texture)
            {
                return false;
            }
            // An observer can move an asset before requesting its new path. Its
            // identity still belongs to the operation that is notifying observers.
            foreach (Texture2D active in s_activeBakes.Values)
            {
                if (active == texture)
                {
                    return true;
                }
            }

            return false;
        }

        private static void NotifyBaked(Terrain terrain, Texture2D texture)
        {
            Action<Terrain, Texture2D> subscribers = Baked;
            if (subscribers == null)
            {
                return;
            }
            // An observer exception must not prevent later observers from receiving
            // the notification. Any output edits are checked after all observers.
            foreach (Action<Terrain, Texture2D> subscriber in subscribers.GetInvocationList())
            {
                try { subscriber(terrain, texture); }
                catch (Exception exception) { Debug.LogException(exception); }
            }
        }

        private static void NotifyBoundAreas(Texture2D texture)
        {
            // CopySerialized preserves asset identity and may reset its updateCount.
            // Notify all consumers explicitly, including areas outside this baker's UI.
            List<GrassPlacementArea> affected = new List<GrassPlacementArea>();
            IReadOnlyList<GrassPlacementArea> areas = GrassPlacementArea.ActiveAreas;
            for (int i = 0; i < areas.Count; i++)
            {
                if (areas[i] && areas[i].GroundColorTexture == texture)
                {
                    affected.Add(areas[i]);
                }
            }

            foreach (GrassPlacementArea area in affected)
            {
                if (!area)
                {
                    continue;
                }

                try { area.MarkGroundColorDirty(); }
                catch (Exception exception) { Debug.LogException(exception); }
            }
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
            if (!SupportsTerrainAlbedo(material))
            {
                error = "Ground-albedo baking requires native '" + NativeTerrainShaderName +
                    "' or a shader declaring GrassAlbedo=TerrainLit. Other custom shaders need their own final-albedo output.";
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

        // Before the first URP render, Unity may select a built-in fallback SubShader.
        // Validate the declared albedo contract across SubShaders; material override tags still take precedence.
        private static bool SupportsTerrainAlbedo(Material material) => material && material.shader &&
            material.HasProperty("_HeightTransition") &&
            (material.shader.name == NativeTerrainShaderName ||
                material.GetTag(TerrainAlbedoTag, true, string.Empty) == TerrainAlbedoContract);

        private static bool TryValidateOutputPath(string path, TerrainLayer[] layers, out string normalized,
            out Texture2D existing, out string error)
        {
            normalized = (path ?? string.Empty).Replace('\\', '/');
            existing = null;
            if (!normalized.StartsWith("Assets/", StringComparison.Ordinal) ||
                !normalized.EndsWith(".asset", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("/../") || normalized.Contains("/./") || normalized.Contains("//"))
            {
                error = "Choose a Texture2D .asset path in an existing folder below Assets/.";
                return false;
            }
            // AssetDatabase resolves existing assets without regard to path casing. Resolve
            // its canonical path before checking whether this output is a source texture.
            string guid = AssetDatabase.AssetPathToGUID(normalized, AssetPathToGUIDOptions.OnlyExistingAssets);
            if (!string.IsNullOrEmpty(guid))
            {
                normalized = AssetDatabase.GUIDToAssetPath(guid);
            }

            if (!AssetDatabase.IsValidFolder(Path.GetDirectoryName(normalized)?.Replace('\\', '/')))
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
                if (IsSourceTexture(layer.diffuseTexture, current, normalized) ||
                    IsSourceTexture(layer.maskMapTexture, current, normalized) ||
                    IsSourceTexture(layer.normalMapTexture, current, normalized))
                {
                    error = "The baked texture cannot replace one of the Terrain's source textures.";
                    return false;
                }
            }
            existing = current as Texture2D;
            error = null;
            return true;
        }

        private static bool IsSourceTexture(Texture2D source, Object output, string outputPath) => source &&
            (source == output || string.Equals(AssetDatabase.GetAssetPath(source), outputPath, StringComparison.OrdinalIgnoreCase));

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
            {
                return;
            }

            Append(state, EditorUtility.GetDirtyCount(value));
            string path = AssetDatabase.GetAssetPath(value);
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            if (!s_importRevisions.TryGetValue(path, out SourceImportState imported))
            {
                imported = new SourceImportState();
                s_importRevisions.Add(path, imported);
            }
            if (tracked.CurrentPaths.Add(path) && !tracked.Paths.Contains(path))
            {
                imported.References++;
            }

            Append(state, imported.Revision.GetHashCode());
        }
        private static void AppendTexture(StringBuilder state, Texture texture, TerrainSourceState tracked)
        {
            AppendObject(state, texture, tracked);
            if (!texture)
            {
                return;
            }

            Append(state, texture.updateCount.GetHashCode());
            Append(state, texture.width); Append(state, texture.height);
            Append(state, (int)texture.filterMode);
            // Texture.wrapMode reads only U. A V-only sampler edit still changes the
            // terrain albedo when its diffuse or mask coordinates leave 0..1.
            Append(state, (int)texture.wrapModeU); Append(state, (int)texture.wrapModeV);
            Append(state, texture.mipMapBias.GetHashCode());
            Append(state, texture.anisoLevel);
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector4 value) => Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);
        private static void OnTerrainTextureChanged(Terrain terrain, string textureName, RectInt region, bool synched) => Invalidate(terrain);
        private static void OnUndoRedo() { unchecked { s_undoRevision++; } }

        private static TerrainSourceState TrackTerrain(Terrain terrain)
        {
            EntityId id = terrain.GetEntityId();
            if (s_terrainSources.TryGetValue(id, out TerrainSourceState tracked) && tracked.Terrain != terrain)
            {
                ReleaseSourcePaths(tracked);
                s_terrainSources.Remove(id);
                tracked = null;
            }
            if (tracked == null)
            {
                tracked = new TerrainSourceState
                {
                    Terrain = terrain,
                    // Recreating a dormant cache entry must change its signature,
                    // even if an unreported GPU edit left the object counters alone.
                    Generation = unchecked(++s_sourceGeneration)
                };
                s_terrainSources.Add(id, tracked);
            }
            tracked.LastUsed = EditorApplication.timeSinceStartup;
            return tracked;
        }

        private static void FinishTrackingPaths(TerrainSourceState tracked)
        {
            foreach (string path in tracked.Paths)
            {
                if (!tracked.CurrentPaths.Contains(path))
                {
                    ReleasePath(path);
                }
            }

            HashSet<string> old = tracked.Paths;
            tracked.Paths = tracked.CurrentPaths;
            tracked.CurrentPaths = old;
            tracked.CurrentPaths.Clear();
        }

        private static void ReleasePath(string path)
        {
            if (s_importRevisions.TryGetValue(path, out SourceImportState imported) && --imported.References <= 0)
            {
                s_importRevisions.Remove(path);
            }
        }

        private static void ReleaseSourcePaths(TerrainSourceState tracked)
        {
            foreach (string path in tracked.Paths)
            {
                ReleasePath(path);
            }

            foreach (string path in tracked.CurrentPaths)
            {
                if (!tracked.Paths.Contains(path))
                {
                    ReleasePath(path);
                }
            }
        }

        private static void CleanupSourceCache()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now < s_nextSourceCleanup)
            {
                return;
            }

            s_nextSourceCleanup = now + 30d;
            foreach (KeyValuePair<EntityId, TerrainSourceState> entry in s_terrainSources)
            {
                if (!entry.Value.Terrain || now - entry.Value.LastUsed > SignatureRetentionSeconds)
                {
                    s_expiredSources.Add(entry.Key);
                }
            }

            foreach (EntityId id in s_expiredSources)
            {
                ReleaseSourcePaths(s_terrainSources[id]);
                s_terrainSources.Remove(id);
            }
            s_expiredSources.Clear();
        }

        internal static void NotifyImportedAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            BumpTrackedPaths(imported); BumpTrackedPaths(deleted); BumpTrackedPaths(moved); BumpTrackedPaths(movedFrom);
        }
        private static void BumpTrackedPaths(string[] paths)
        {
            foreach (string path in paths)
            {
                if (s_importRevisions.TryGetValue(path, out SourceImportState imported))
                {
                    imported.Revision = unchecked(imported.Revision + 1);
                }
            }
        }
    }

    internal sealed class TerrainGrassAlbedoSourcePostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            TerrainGrassAlbedoBaker.NotifyImportedAssets(imported, deleted, moved, movedFrom);
        }
    }
}
