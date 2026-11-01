using UnityEngine;
using UnityEngine.Serialization;

namespace Cone.Grass
{
    /// <summary>
    /// Preserves the published player-interaction component and serialized settings.
    /// Sampling, support queries, recovery and resource ownership belong to the base component.
    /// </summary>
    [ExecuteAlways, DisallowMultipleComponent, DefaultExecutionOrder(10000)]
    [AddComponentMenu("Cone/Grass/Interactor")]
    public sealed class GrassInteractor : GrassColliderInteractor
    {
        [SerializeField, FormerlySerializedAs("body")] private Collider _body;
        /// <summary>
        /// The character's collider. When unassigned on enable, the one enabled, non-trigger capsule, character
        /// controller or sphere collider on this GameObject is used, if exactly one exists.
        /// </summary>
        public Collider Body { get => _body; set => _body = value; }
        [Tooltip("Optional GrassSlope producer using premultiplied vertex colors. Null uses the retained package material."), SerializeField, FormerlySerializedAs("interactionShader")]
        private Shader _interactionShader;
        /// <summary>Optional GrassSlope producer using premultiplied vertex colours; null uses the retained package material.</summary>
        public Shader InteractionShader { get => _interactionShader; set => _interactionShader = value; }
        [SerializeField, FormerlySerializedAs("groundLayers")] private LayerMask _groundLayers = ~0;
        /// <summary>Layers whose colliders are accepted as the supporting ground.</summary>
        public LayerMask GroundLayers { get => _groundLayers; set => _groundLayers = value; }
        [Min(0f), SerializeField, FormerlySerializedAs("radiusPadding")] private float _radiusPadding = 0.15f;
        /// <summary>Extra horizontal interaction radius around the collider footprint, in metres.</summary>
        public float RadiusPadding { get => _radiusPadding; set => _radiusPadding = value; }
        [Min(0.01f), SerializeField, FormerlySerializedAs("bladeHeight")] private float _bladeHeight = 0.5f;
        /// <summary>Ground clearance within which the collider still bends the grass; the bend weakens with clearance.</summary>
        public float BladeHeight { get => _bladeHeight; set => _bladeHeight = value; }
        [Range(0f, 1f), SerializeField, FormerlySerializedAs("strength")] private float _strength = 0.9f;
        /// <summary>Bend strength of a contact, from 0 to 1.</summary>
        public float Strength { get => _strength; set => _strength = value; }
        [Min(0f), SerializeField, FormerlySerializedAs("attackSeconds")] private float _attackSeconds = 0.06f;
        /// <summary>Seconds over which a new contact approaches its full bend; zero bends immediately.</summary>
        public float AttackSeconds { get => _attackSeconds; set => _attackSeconds = value; }
        [Tooltip("Finite recovery duration after the collider footprint leaves a contact node.")]
        [Min(0.01f), SerializeField, FormerlySerializedAs("recoverySeconds")] private float _recoverySeconds = 0.55f;
        /// <summary>Finite recovery duration, in seconds, after the collider footprint leaves a contact node.</summary>
        public float RecoverySeconds { get => _recoverySeconds; set => _recoverySeconds = value; }

        /// <summary>Assigns <paramref name="actor"/> to <see cref="Body"/>, then configures the base component.</summary>
        /// <param name="actor">The character's collider.</param>
        /// <param name="support">Optional explicit supporting collider.</param>
        /// <param name="selfRoot">Optional self-collision exclusion root.</param>
        public override void Configure(Collider actor, Collider support = null, Transform selfRoot = null)
        {
            _body = actor;
            base.Configure(actor, support, selfRoot);
        }

        /// <summary>Discovers <see cref="Body"/> when it is unassigned, then registers the interactor.</summary>
        protected override void OnEnable()
        {
            // An empty serialized field loads in the Editor as Unity's null placeholder, not a C# null.
            if (!GrassPlacementArea.HasAssignedReference(_body))
            {
                Collider candidate = null;
                foreach (Collider local in GetComponents<Collider>())
                {
                    if (!local || !local.enabled || local.isTrigger ||
                        !(local is CapsuleCollider || local is CharacterController || local is SphereCollider))
                    {
                        continue;
                    }

                    if (candidate)
                    {
                        candidate = null;
                        break;
                    }
                    candidate = local;
                }
                // An additive scene can enable this component before it is loaded.
                // Sample validates the full shape and physics after structural discovery.
                _body = candidate;
            }
            base.OnEnable();
        }

        /// <summary>Copies this component's public settings into the base interaction settings before each observation.</summary>
        protected override void ApplyConfiguration() => ConfigureInteraction(_body, _interactionShader,
            _groundLayers, _radiusPadding, _bladeHeight, _strength, _attackSeconds, _recoverySeconds);
    }
}
