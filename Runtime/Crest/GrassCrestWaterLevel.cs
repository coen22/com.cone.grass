using UnityEngine;
using WaveHarmonic.Crest;

namespace Cone.Grass.Crest
{
    /// <summary>
    /// Feeds the Crest 5 water level into an <see cref="InfiniteGrassRenderer"/>'s
    /// <see cref="InfiniteGrassRenderer.WaterLine"/>, so grass and flowers stay above the water. It reads Crest's sea
    /// level, or the water surface height that Crest's collision query returns at a sample point (which includes waves
    /// and level inputs such as raised lakes). Compiled only when the Crest package (<c>com.waveharmonic.crest</c>) is
    /// installed.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Cone/Grass/Grass Crest Water Level")]
    public sealed class GrassCrestWaterLevel : MonoBehaviour, IGrassWaterLevelSource
    {
        /// <summary>Which Crest height the water line uses.</summary>
        public enum LevelMode
        {
            /// <summary>The Crest Water Renderer's sea level (its transform height).</summary>
            SeaLevel,

            /// <summary>
            /// The water surface height at the sample point, from Crest's collision query. Falls back to the sea level
            /// while the query has no result yet.
            /// </summary>
            SurfaceHeight,
        }

        [Tooltip("The grass renderer to drive. Empty uses the active Infinite Grass Renderer."), SerializeField]
        private InfiniteGrassRenderer _renderer;
        [Tooltip("Sea Level reads the Water Renderer's height; Surface Height queries the water surface at the sample point."), SerializeField]
        private LevelMode _mode = LevelMode.SurfaceHeight;
        [Tooltip("Where Surface Height is queried. Empty uses this object's position."), SerializeField]
        private Transform _samplePoint;
        [Tooltip("Added to the Crest height, in metres. A small positive value keeps blades out of wave troughs and the meniscus."), SerializeField]
        private float _offset;
        [Tooltip("Smallest wave length, in metres, the surface query resolves. Larger values ignore small ripples."), Min(0f), SerializeField]
        private float _minimumWaveLength = 1f;

        private readonly SampleCollisionHelper _sampler = new SampleCollisionHelper();
        private InfiniteGrassRenderer _attached;
        private int _cachedFrame = -1;
        private float _cachedLevel;
        private bool _cachedValid;

        /// <summary>The grass renderer to drive; <c>null</c> uses <see cref="InfiniteGrassRenderer.Instance"/>.</summary>
        public InfiniteGrassRenderer GrassRenderer
        {
            get => _renderer;
            set
            {
                _renderer = value;
                Attach();
            }
        }

        /// <summary>Which Crest height the water line uses.</summary>
        public LevelMode Mode { get => _mode; set => _mode = value; }

        /// <summary>Where <see cref="LevelMode.SurfaceHeight"/> is queried; <c>null</c> uses this object's position.</summary>
        public Transform SamplePoint { get => _samplePoint; set => _samplePoint = value; }

        /// <summary>Added to the Crest height, in metres.</summary>
        public float Offset { get => _offset; set => _offset = value; }

        /// <summary>Smallest wave length, in metres, the surface query resolves.</summary>
        public float MinimumWaveLength { get => _minimumWaveLength; set => _minimumWaveLength = value; }

        /// <summary>The grass renderer this component currently drives, if any.</summary>
        public InfiniteGrassRenderer AttachedRenderer => _attached;

        private void OnEnable()
        {
            _cachedFrame = -1;
            Attach();
        }

        private void OnDisable() => Detach();

        private void Update()
        {
            // The active renderer can appear, change or go after this component is enabled.
            if (Target() != _attached)
            {
                Attach();
            }
        }

        private void OnValidate()
        {
            _cachedFrame = -1;
            if (isActiveAndEnabled)
            {
                Attach();
            }
        }

        /// <inheritdoc/>
        public bool TryGetWaterLevel(out float level)
        {
            // Several cameras render each frame; Crest's helper serves one query per frame.
            int frame = Time.frameCount;
            if (_cachedFrame != frame)
            {
                _cachedFrame = frame;
                _cachedValid = Sample(out _cachedLevel);
            }

            level = _cachedLevel;
            return _cachedValid;
        }

        private bool Sample(out float level)
        {
            level = 0f;
            WaterRenderer water = WaterRenderer.Instance;
            if (water == null || !water.isActiveAndEnabled)
            {
                return false;
            }

            level = water.SeaLevel;
            if (_mode == LevelMode.SurfaceHeight)
            {
                Vector3 position = _samplePoint != null ? _samplePoint.position : transform.position;
                if (_sampler.SampleHeight(position, out float surface, Mathf.Max(0f, _minimumWaveLength)) &&
                    !float.IsNaN(surface) && !float.IsInfinity(surface))
                {
                    level = surface;
                }
            }

            level += _offset;
            return !float.IsNaN(level) && !float.IsInfinity(level);
        }

        private InfiniteGrassRenderer Target() =>
            !isActiveAndEnabled ? null : _renderer != null ? _renderer : InfiniteGrassRenderer.Instance;

        private void Attach()
        {
            InfiniteGrassRenderer target = Target();
            if (target == _attached && (target == null || ReferenceEquals(target.WaterLine.Source, this)))
            {
                return;
            }

            Detach();
            if (target != null)
            {
                target.WaterLine.Source = this;
                _attached = target;
            }
        }

        private void Detach()
        {
            if (_attached != null && ReferenceEquals(_attached.WaterLine.Source, this))
            {
                _attached.WaterLine.Source = null;
            }

            _attached = null;
        }
    }
}
