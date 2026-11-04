using System;
using UnityEngine;

namespace Cone.Grass
{
    /// <summary>
    /// Supplies the world-space water level for <see cref="InfiniteGrassRenderer.WaterLine"/>, for example from a water
    /// system. The optional Crest 5 bridge (<c>Cone.Grass.Crest.GrassCrestWaterLevel</c>) is one.
    /// </summary>
    public interface IGrassWaterLevelSource
    {
        /// <summary>Reports the current water level, in world-space metres.</summary>
        /// <param name="level">The water surface height (world Y) the grass must stay above.</param>
        /// <returns><c>false</c> when no level is available this frame; the water line then uses its own level.</returns>
        bool TryGetWaterLevel(out float level);
    }

    /// <summary>
    /// Keeps grass and flowers above a water level. No blade roots at or below the level, and the population thins
    /// in over a shoreline band above it, so the grass doesn't stop in a hard line at the water's edge.
    /// Serialized on <see cref="InfiniteGrassRenderer.WaterLine"/>.
    /// </summary>
    [Serializable]
    public sealed class GrassWaterLine
    {
        [Tooltip("Grow no grass at or below Level. A Source (such as the Crest bridge) turns the water line on too."), SerializeField]
        private bool _enabled;
        [Tooltip("World-space water height (Y), in metres. A Source overrides it while it reports a level."), SerializeField]
        private float _level;
        [Tooltip("Height of the shoreline band above the water, in metres, over which the grass thins in. Zero is a hard line."), Min(0f), SerializeField]
        private float _shoreFade = 0.5f;

        /// <summary>Grows no grass at or below <see cref="Level"/>. Assigning a <see cref="Source"/> turns the water line on too.</summary>
        public bool Enabled { get => _enabled; set => _enabled = value; }

        /// <summary>World-space water height (Y), in metres. A <see cref="Source"/> overrides it while it reports a level.</summary>
        public float Level { get => _level; set => _level = value; }

        /// <summary>
        /// Height of the shoreline band above the water, in metres. Blade density rises linearly from zero at the water
        /// level to full at this height above it. Zero gives a hard line.
        /// </summary>
        public float ShoreFade { get => _shoreFade; set => _shoreFade = value; }

        /// <summary>
        /// An optional runtime source of the water level, not serialized. While it reports a level, that level replaces
        /// <see cref="Level"/>; a destroyed Unity object counts as no source.
        /// </summary>
        public IGrassWaterLevelSource Source { get; set; }

        /// <summary>Whether the water line applies: <see cref="Enabled"/> is on or a live <see cref="Source"/> is assigned.</summary>
        public bool IsActive => _enabled || HasSource;

        private bool HasSource => Source != null && !(Source is UnityEngine.Object unityObject && unityObject == null);

        /// <summary>
        /// Resolves the level and shoreline band this frame. Returns <c>false</c> when the water line doesn't apply or
        /// has no finite level.
        /// </summary>
        public bool TryResolve(out float level, out float shoreFade)
        {
            level = 0f;
            shoreFade = 0f;
            if (!IsActive)
            {
                return false;
            }

            bool fromSource = false;
            if (HasSource)
            {
                try
                {
                    fromSource = Source.TryGetWaterLevel(out level) && IsFinite(level);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    fromSource = false;
                }
            }

            if (!fromSource)
            {
                if (!_enabled)
                {
                    return false;
                }

                level = _level;
            }

            shoreFade = IsFinite(_shoreFade) ? Mathf.Max(0f, _shoreFade) : 0f;
            return IsFinite(level);
        }

        /// <summary>
        /// The fraction of blades kept on ground at <paramref name="groundHeight"/>: zero at or below
        /// <paramref name="level"/>, rising linearly to one at <paramref name="shoreFade"/> above it. The GPU placement
        /// kernel applies the same factor.
        /// </summary>
        public static float Coverage(float groundHeight, float level, float shoreFade)
        {
            float above = groundHeight - level;
            if (!(above > 0f))
            {
                return 0f;
            }

            return shoreFade > 0f ? Mathf.Clamp01(above / shoreFade) : 1f;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
