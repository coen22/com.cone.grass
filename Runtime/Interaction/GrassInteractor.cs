using UnityEngine;

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
        /// <summary>
        /// The character's collider. When unassigned on enable, the one enabled, non-trigger capsule, character
        /// controller or sphere collider on this GameObject is used, if exactly one exists.
        /// </summary>
        public Collider body;
        /// <summary>Optional GrassSlope producer using premultiplied vertex colours; null uses the retained package material.</summary>
        [Tooltip("Optional GrassSlope producer using premultiplied vertex colors. Null uses the retained package material.")]
        public Shader interactionShader;
        /// <summary>Layers whose colliders are accepted as the supporting ground.</summary>
        public LayerMask groundLayers = ~0;
        /// <summary>Extra horizontal interaction radius around the collider footprint, in metres.</summary>
        [Min(0f)] public float radiusPadding = 0.15f;
        /// <summary>Ground clearance within which the collider still bends the grass; the bend weakens with clearance.</summary>
        [Min(0.01f)] public float bladeHeight = 0.5f;
        /// <summary>Bend strength of a contact, from 0 to 1.</summary>
        [Range(0f, 1f)] public float strength = 0.9f;
        /// <summary>Seconds over which a new contact approaches its full bend; zero bends immediately.</summary>
        [Min(0f)] public float attackSeconds = 0.06f;
        /// <summary>Finite recovery duration, in seconds, after the collider footprint leaves a contact node.</summary>
        [Tooltip("Finite recovery duration after the collider footprint leaves a contact node.")]
        [Min(0.01f)] public float recoverySeconds = 0.55f;

        /// <summary>Assigns <paramref name="actor"/> to <see cref="body"/>, then configures the base component.</summary>
        /// <param name="actor">The character's collider.</param>
        /// <param name="support">Optional explicit supporting collider.</param>
        /// <param name="selfRoot">Optional self-collision exclusion root.</param>
        public override void Configure(Collider actor, Collider support = null, Transform selfRoot = null)
        {
            body = actor;
            base.Configure(actor, support, selfRoot);
        }

        /// <summary>Discovers <see cref="body"/> when it is unassigned, then registers the interactor.</summary>
        protected override void OnEnable()
        {
            if (ReferenceEquals(body, null))
            {
                Collider candidate = null;
                foreach (Collider local in GetComponents<Collider>())
                {
                    if (!local || !local.enabled || local.isTrigger ||
                        !(local is CapsuleCollider || local is CharacterController || local is SphereCollider))
                        continue;
                    if (candidate)
                    {
                        candidate = null;
                        break;
                    }
                    candidate = local;
                }
                // An additive scene can enable this component before it is loaded.
                // Sample validates the full shape and physics after structural discovery.
                body = candidate;
            }
            base.OnEnable();
        }

        /// <summary>Copies this component's public settings into the base interaction settings before each observation.</summary>
        protected override void ApplyConfiguration() => ConfigureInteraction(body, interactionShader,
            groundLayers, radiusPadding, bladeHeight, strength, attackSeconds, recoverySeconds);
    }
}
