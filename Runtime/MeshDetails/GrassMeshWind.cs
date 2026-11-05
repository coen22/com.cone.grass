using System;
using UnityEngine;

namespace Cone.Grass
{
    /// <summary>Where mesh kinds take their wind from.</summary>
    public enum GrassMeshWindSource
    {
        /// <summary>The scene's shared wind (com.cone.render-features' <c>ConeWind</c>) when it is installed, else <see cref="Own"/>.</summary>
        Auto = 0,
        /// <summary>The scene's shared wind only; still air when <c>ConeWind</c> is not installed or the scene has no WindZone.</summary>
        Scene = 1,
        /// <summary>The settings below, which follow a WindZone's parameters.</summary>
        Own = 2,
        /// <summary>No wind.</summary>
        None = 3
    }

    /// <summary>
    /// The wind that leans mesh-kind instances: each instance is sheared downwind in proportion to its height, so it works
    /// with any material (URP Lit, Shader Graph, Cone/Foliage) without a wind vertex shader.
    /// </summary>
    [Serializable]
    public sealed class GrassMeshWind
    {
        /// <summary>The ConeWind offset period, in metres: the shared air drift wraps at it (render-features' <c>ConeWind.OffsetPeriod</c>).</summary>
        public const double OffsetPeriod = 1024.0;

        [SerializeField] private GrassMeshWindSource _source = GrassMeshWindSource.Auto;
        [Tooltip("Multiplies the wind's strength for every kind."), Min(0f), SerializeField]
        private float _strength = 1f;
        [Tooltip("Size of the gusts that roll across the plants, in metres."), Min(1f), SerializeField]
        private float _gustSize = 16f;

        [Header("Own Wind (WindZone parameters)")]
        [Tooltip("Compass direction the wind blows towards, in degrees around world Y (0 = +Z)."), SerializeField]
        private float _direction = 48f;
        [SerializeField] private float _main = 0.2f;
        [SerializeField] private float _turbulence = 0.1f;
        [SerializeField] private float _pulseMagnitude = 0.8f;
        [Min(0f), SerializeField] private float _pulseFrequency = 0.5f;

        [NonSerialized] private double _time = double.NaN;
        [NonSerialized] private double _offsetX, _offsetZ;

        /// <summary>Where the wind comes from.</summary>
        public GrassMeshWindSource Source { get => _source; set => _source = value; }

        /// <summary>Multiplies the wind's strength for every kind.</summary>
        public float Strength { get => _strength; set => _strength = value; }

        /// <summary>Size of the gusts that roll across the plants, in metres.</summary>
        public float GustSize { get => _gustSize; set => _gustSize = value; }

        /// <summary>Own wind: the compass direction it blows towards, in degrees around world Y (0 = +Z).</summary>
        public float Direction { get => _direction; set => _direction = value; }

        /// <summary>Own wind: the steady strength (<see cref="WindZone.windMain"/>).</summary>
        public float Main { get => _main; set => _main = value; }

        /// <summary>Own wind: the turbulence (<see cref="WindZone.windTurbulence"/>).</summary>
        public float Turbulence { get => _turbulence; set => _turbulence = value; }

        /// <summary>Own wind: the pulse magnitude (<see cref="WindZone.windPulseMagnitude"/>).</summary>
        public float PulseMagnitude { get => _pulseMagnitude; set => _pulseMagnitude = value; }

        /// <summary>Own wind: the pulse frequency in Hz (<see cref="WindZone.windPulseFrequency"/>).</summary>
        public float PulseFrequency { get => _pulseFrequency; set => _pulseFrequency = value; }

        /// <summary>
        /// The wind for the GPU at <paramref name="time"/> seconds: <paramref name="direction"/> xyz the unit direction it
        /// blows along and w the strength now; <paramref name="offset"/> xyz how far the air has drifted (wrapped at
        /// <see cref="OffsetPeriod"/>); <paramref name="turbulence"/> the turbulence. Returns whether the shared scene wind was read.
        /// </summary>
        public bool Resolve(double time, out Vector4 direction, out Vector4 offset, out float turbulence)
        {
            direction = Vector4.zero;
            offset = Vector4.zero;
            turbulence = 0f;
            bool scene = _source == GrassMeshWindSource.Scene || _source == GrassMeshWindSource.Auto && GrassConeWind.IsAvailable;
            if (_source == GrassMeshWindSource.None)
            {
                return false;
            }

            if (scene)
            {
                if (!GrassConeWind.TryRead(out Vector4 d, out Vector4 p, out Vector4 o))
                {
                    return true;
                }

                direction = new Vector4(d.x, d.y, d.z, p.w * Finite(_strength));
                offset = o;
                turbulence = p.x * Finite(_strength);
                return true;
            }

            float radians = Finite(_direction) * Mathf.Deg2Rad;
            var axis = new Vector3(Mathf.Sin(radians), 0f, Mathf.Cos(radians));
            double now = double.IsNaN(time) || double.IsInfinity(time) ? 0.0 : time;
            if (double.IsNaN(_time) || now < _time)
            {
                _time = now;
            }

            // The drift is integrated exactly, as ConeWindState.DistanceBetween does, so it does not depend on the frame rate.
            double distance = DistanceBetween(_time, now);
            _offsetX += axis.x * distance;
            _offsetZ += axis.z * distance;
            _time = now;
            float strength = (float)(Finite(_main) * (1.0 + Finite(_pulseMagnitude) * Math.Sin(2.0 * Math.PI * Finite(_pulseFrequency) * now)));
            direction = new Vector4(axis.x, 0f, axis.z, strength * Finite(_strength));
            offset = new Vector4((float)Wrap(_offsetX), 0f, (float)Wrap(_offsetZ), (float)now);
            turbulence = Finite(_turbulence) * Finite(_strength);
            return false;
        }

        private double DistanceBetween(double from, double to)
        {
            double main = Finite(_main);
            double distance = main * (to - from);
            double frequency = Finite(_pulseFrequency);
            if (Finite(_pulseMagnitude) != 0f && frequency > 0.0)
            {
                double w = 2.0 * Math.PI * frequency;
                distance -= main * Finite(_pulseMagnitude) / w * (Math.Cos(w * to) - Math.Cos(w * from));
            }

            return distance;
        }

        private static double Wrap(double value) => value - OffsetPeriod * Math.Floor(value / OffsetPeriod);

        private static float Finite(float value) => float.IsFinite(value) ? value : 0f;
    }

    /// <summary>
    /// Reads com.cone.render-features' shared wind (<c>Cone.RenderFeatures.ConeWind</c>, render-features PR #73) through the
    /// global shader vectors it publishes before each frame: <c>_ConeWindDirection</c> (direction, main),
    /// <c>_ConeWindParams</c> (turbulence, pulse magnitude, pulse frequency, strength now) and <c>_ConeWindOffset</c>
    /// (the air's drift, wrapped at 1024). Grass references no render-features type, so it compiles with any version.
    /// </summary>
    public static class GrassConeWind
    {
        private static readonly int s_direction = Shader.PropertyToID("_ConeWindDirection");
        private static readonly int s_parameters = Shader.PropertyToID("_ConeWindParams");
        private static readonly int s_offset = Shader.PropertyToID("_ConeWindOffset");
        private static bool? s_available;

        /// <summary>
        /// Whether the shared wind is installed: com.cone.render-features is in the project (the
        /// <c>CONE_GRASS_RENDER_FEATURES</c> version define) and it has <c>ConeWind</c>.
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
                if (OverrideAvailable.HasValue)
                {
                    return OverrideAvailable.Value;
                }

#if CONE_GRASS_RENDER_FEATURES
                s_available ??= Type.GetType("Cone.RenderFeatures.ConeWind, Cone.RenderFeatures.Core") != null;
                return s_available.Value;
#else
                return false;
#endif
            }
        }

        /// <summary>Tests: forces <see cref="IsAvailable"/>; null uses the project.</summary>
        internal static bool? OverrideAvailable { get; set; }

        /// <summary>Reads the shared wind's globals. Returns false when they hold no wind (no zone, or not published yet).</summary>
        public static bool TryRead(out Vector4 direction, out Vector4 parameters, out Vector4 offset)
        {
            direction = Shader.GetGlobalVector(s_direction);
            parameters = Shader.GetGlobalVector(s_parameters);
            offset = Shader.GetGlobalVector(s_offset);
            bool finite = float.IsFinite(direction.x) && float.IsFinite(direction.y) && float.IsFinite(direction.z) &&
                          float.IsFinite(parameters.x) && float.IsFinite(parameters.w) && float.IsFinite(offset.x) && float.IsFinite(offset.z);
            return finite && new Vector3(direction.x, direction.y, direction.z).sqrMagnitude > 1e-8f;
        }
    }
}
