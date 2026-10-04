using UnityEngine;

/// <summary>
/// Preserves the published player-interaction component and serialized settings.
/// Sampling, support queries, recovery and resource ownership belong to the base component.
/// </summary>
[ExecuteAlways, DisallowMultipleComponent, DefaultExecutionOrder(10000)]
[AddComponentMenu("Cone/Grass/Interactor")]
public sealed class GrassInteractor : GrassColliderInteractor
{
    public Collider body;
    [Tooltip("Optional GrassSlope producer using premultiplied vertex colors. Null uses the retained package material.")]
    public Shader interactionShader;
    public LayerMask groundLayers = ~0;
    [Min(0f)] public float radiusPadding = 0.15f;
    [Min(0.01f)] public float bladeHeight = 0.5f;
    [Range(0f, 1f)] public float strength = 0.9f;
    [Min(0f)] public float attackSeconds = 0.06f;
    [Tooltip("Finite recovery duration after the collider footprint leaves a contact node.")]
    [Min(0.01f)] public float recoverySeconds = 0.55f;

    public override void Configure(Collider actor, Collider support = null, Transform selfRoot = null)
    {
        body = actor;
        base.Configure(actor, support, selfRoot);
    }

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

    protected override void ApplyConfiguration() => ConfigureInteraction(body, interactionShader,
        groundLayers, radiusPadding, bladeHeight, strength, attackSeconds, recoverySeconds);
}
