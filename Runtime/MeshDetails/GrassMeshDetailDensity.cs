using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;

namespace Cone.Grass
{
    /// <summary>
    /// The terrain detail density layers as GPU textures, and Unity's rules for how many instances a layer value places.
    /// </summary>
    public static class GrassMeshDetailDensity
    {
        /// <summary>The most kinds one terrain dispatch scatters; a terrain with more is dispatched more than once.</summary>
        public const int MaxSlicesPerDispatch = 16;

        private static readonly Dictionary<(GrassMeshDetails, Terrain), Entry> s_entries = new Dictionary<(GrassMeshDetails, Terrain), Entry>();
        private static readonly List<(GrassMeshDetails, Terrain)> s_stale = new List<(GrassMeshDetails, Terrain)>();

        /// <summary>One terrain's layers for one component: a texture array with one slice per scattered layer.</summary>
        internal sealed class Entry
        {
            public Texture2DArray Texture;
            public int[] SliceKinds = Array.Empty<int>();
            public int[] SliceLayers = Array.Empty<int>();
            public int Resolution;
            public uint Revision;
            public int DirtyCount = -1;
            public TerrainData Data;
        }

        /// <summary>
        /// The expected number of instances Unity places in one detail cell for a layer value: in
        /// <see cref="DetailScatterMode.InstanceCountMode"/> the value is the count; in
        /// <see cref="DetailScatterMode.CoverageMode"/> it is a coverage (value / 255) of the prototype's density per square
        /// metre over the cell. <paramref name="scale"/> is the kind's density times the terrain's detail density.
        /// </summary>
        /// <param name="mode">The terrain's scatter mode.</param>
        /// <param name="value">The detail layer value, 0 to 255.</param>
        /// <param name="scale">The density multiplier from <see cref="SliceScale"/>.</param>
        /// <param name="cellArea">The area of one detail cell, in square metres.</param>
        public static float ExpectedInstancesPerCell(DetailScatterMode mode, int value, float scale, float cellArea)
        {
            if (value <= 0 || !(scale > 0f))
            {
                return 0f;
            }

            return mode == DetailScatterMode.InstanceCountMode ? value * scale : value / 255f * scale * cellArea;
        }

        /// <summary>
        /// The density multiplier for one kind on one terrain: the kind's per-square-metre density in coverage mode (1 in
        /// instance-count mode), times the terrain's Detail Density when the kind follows it, times the component's scale.
        /// </summary>
        public static float SliceScale(GrassMeshKind kind, Terrain terrain, DetailScatterMode mode, float componentScale)
        {
            float scale = mode == DetailScatterMode.CoverageMode ? Mathf.Max(0f, kind.Density) : 1f;
            if (kind.UseDensityScaling && terrain)
            {
                scale *= Mathf.Clamp01(terrain.detailObjectDensity);
            }

            return scale * Mathf.Max(0f, componentScale);
        }

        /// <summary>The density texture of <paramref name="terrain"/> for <paramref name="owner"/>, built or refreshed as needed.</summary>
        internal static Entry Get(GrassMeshDetails owner, GrassMeshDetailTerrain terrain)
        {
            Terrain unityTerrain = terrain.Terrain;
            TerrainData data = unityTerrain ? unityTerrain.terrainData : null;
            if (!data)
            {
                return null;
            }

            var key = (owner, unityTerrain);
            s_entries.TryGetValue(key, out Entry entry);
            int dirty = DirtyCount(data);
            if (entry != null && entry.Revision == owner.Revision && entry.Data == data && entry.DirtyCount == dirty &&
                entry.Resolution == data.detailResolution)
            {
                return entry;
            }

            entry ??= new Entry();
            Build(entry, owner, terrain, data);
            entry.Revision = owner.Revision;
            entry.DirtyCount = dirty;
            entry.Data = data;
            s_entries[key] = entry;
            return entry;
        }

        /// <summary>Releases every density texture of <paramref name="owner"/>.</summary>
        internal static void Release(GrassMeshDetails owner)
        {
            s_stale.Clear();
            foreach (KeyValuePair<(GrassMeshDetails, Terrain), Entry> pair in s_entries)
            {
                if (pair.Key.Item1 == owner || !pair.Key.Item1)
                {
                    s_stale.Add(pair.Key);
                }
            }

            foreach ((GrassMeshDetails, Terrain) key in s_stale)
            {
                Destroy(s_entries[key].Texture);
                s_entries.Remove(key);
            }
        }

        private static void Build(Entry entry, GrassMeshDetails owner, GrassMeshDetailTerrain terrain, TerrainData data)
        {
            int resolution = data.detailResolution;
            int layers = data.detailPrototypes.Length;
            int[] layerKinds = terrain.LayerKinds;
            var sliceKinds = new List<int>();
            var sliceLayers = new List<int>();
            var values = new List<byte[]>();
            for (int layer = 0; layer < layers && layer < layerKinds.Length; layer++)
            {
                int kind = layerKinds[layer];
                if (kind < 0 || kind >= owner.Kinds.Count || owner.Kinds[kind] == null || !owner.Kinds[kind].Enabled ||
                    owner.Kinds[kind].UsableLodCount == 0)
                {
                    continue;
                }

                int[,] layerValues = data.GetDetailLayer(0, 0, resolution, resolution, layer);
                var bytes = new byte[resolution * resolution];
                bool any = false;
                for (int y = 0; y < resolution; y++)
                {
                    int row = y * resolution;
                    for (int x = 0; x < resolution; x++)
                    {
                        // GetDetailLayer is indexed [y, x]; the texture row is y.
                        int v = Mathf.Clamp(layerValues[y, x], 0, 255);
                        bytes[row + x] = (byte)v;
                        any |= v != 0;
                    }
                }

                if (!any)
                {
                    continue;
                }

                sliceKinds.Add(kind);
                sliceLayers.Add(layer);
                values.Add(bytes);
            }

            Destroy(entry.Texture);
            entry.Texture = null;
            entry.Resolution = resolution;
            entry.SliceKinds = sliceKinds.ToArray();
            entry.SliceLayers = sliceLayers.ToArray();
            if (values.Count == 0 || resolution <= 0)
            {
                return;
            }

            var texture = new Texture2DArray(resolution, resolution, values.Count, GraphicsFormat.R8_UNorm,
                TextureCreationFlags.DontInitializePixels)
            {
                name = "Grass Mesh Detail Density " + (terrain.Terrain ? terrain.Terrain.name : string.Empty),
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            for (int slice = 0; slice < values.Count; slice++)
            {
                texture.SetPixelData(values[slice], 0, slice);
            }

            texture.Apply(false, true);
            entry.Texture = texture;
        }

        private static int DirtyCount(TerrainData data)
        {
#if UNITY_EDITOR
            // Painting details in the editor bumps the asset's dirty count; that is the only signal Unity gives.
            return UnityEditor.EditorUtility.GetDirtyCount(data);
#else
            return 0;
#endif
        }

        private static void Destroy(Object obj)
        {
            if (!obj)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Object.Destroy(obj);
            }
            else
            {
                Object.DestroyImmediate(obj);
            }
        }
    }
}
