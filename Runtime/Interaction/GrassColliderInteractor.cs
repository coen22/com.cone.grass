using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Optional collider-driven producer for the existing world-XZ grass interaction map.
/// ClearHistory must be called when a gameplay session or character is reset in place.
/// </summary>
[ExecuteAlways, DisallowMultipleComponent, DefaultExecutionOrder(10000)]
[AddComponentMenu("Cone/Grass/Collider Interactor")]
public class GrassColliderInteractor : MonoBehaviour
{
    public enum UpdateMode { LateUpdate, FixedUpdate, Manual }
    public enum InteractionState
    {
        Disabled, MissingCollider, UnsupportedCollider, NoGroundContact, Active, Recovering,
        PathReset, TrailBudgetExceeded, MissingCaptureMaterial, InvalidTime
    }

    private const int MaximumPathQueries = 64;
    private const double MaximumContinuousSeconds = 0.25;
    private static readonly List<GrassColliderInteractor> activeInteractors = new List<GrassColliderInteractor>();
    private static int revision = 1;
    private static int registryGeneration = 1;

    [Header("Character and support")]
    [Tooltip("The actual upright CapsuleCollider, CharacterController or SphereCollider that touches the grass.")]
    [SerializeField] private Collider actorCollider;
    [Tooltip("Self-collision exclusion root. Defaults to this transform only when the actor collider is beneath it.")]
    [SerializeField] private Transform actorRoot;
    [Tooltip("Optional explicit supporting collider for legacy or generic placement. Other nearer floors and ambiguous grass owners still reject contact.")]
    [SerializeField] private Collider explicitSupport;
    [Tooltip("Optional producer using premultiplied vertex RGBA and the GrassSlope pass. Empty uses the retained package material.")]
    [SerializeField] private Shader captureShader;
    [SerializeField] private LayerMask acceptedSupportLayers = ~0;
    [SerializeField] private UpdateMode updateMode = UpdateMode.LateUpdate;
    [SerializeField, Min(0f)] private float groundClearance = 0.15f;
    [SerializeField, Min(0f)] private float allowedPenetration = 0.1f;
    [SerializeField] private bool attenuateWithClearance;

    [Header("Interaction and recovery")]
    [SerializeField, Range(0f, 1f)] private float bendStrength = 0.85f;
    [SerializeField, Min(0f)] private float attackDuration;
    [SerializeField, Min(0.01f)] private float recoveryDuration = 2f;
    [Tooltip("Extra horizontal interaction influence. Support casts always use the actual collider radius.")]
    [SerializeField, Min(0f)] private float footprintPadding;
    [Tooltip("Fixed world-grid spacing for this history. Smaller values need more nodes and finer grass capture resolution.")]
    [SerializeField, Min(0.01f)] private float gridSpacing = 0.15f;
    [Tooltip("Maximum retained contact nodes. Overflow clears the path and attempts the complete current footprint only.")]
    [SerializeField, Range(16, 2048)] private int nodeCapacity = 1024;
    [Tooltip("Motion farther than this between observations clears the old field instead of joining the positions.")]
    [SerializeField, Min(0.01f)] private float teleportDistance = 5f;

    private GrassInteractionField field;
    private GrassInteractionMesh interactionMesh;
    private GrassInteractorSupport supportResolver;
    private GrassInteractorShape previousShape, observedShape;
    private Collider configuredActor, configuredSupport, lastGroundSupport;
    private Shader configuredShader;
    private Transform configuredRoot;
    private Scene configuredScene, configuredActorScene;
    private UpdateMode configuredUpdateMode;
    private float configuredSpacing, configuredClearance, configuredPenetration, configuredPadding;
    private int configuredLayers;
    private int configuredCapacity, registeredGeneration;
    private int lastGroundSignature;
    private uint historyGeneration, drawRevision;
    private double previousTime, responseTime, observedTime, lastTime;
    private float response;
    private bool registered, previousValid, responseValid, observationValid, hasTime, visible, warnedBudget, warnedMaterial, playing;
    private Vector2 historyMinimum, historyMaximum;

    public static IReadOnlyList<GrassColliderInteractor> ActiveInteractors => activeInteractors;
    public static uint Revision => unchecked((uint)Volatile.Read(ref revision));
    public uint DrawRevision => drawRevision;
    public Collider ActorCollider => actorCollider;
    public Collider ExplicitSupport => explicitSupport;
    public int ActiveNodeCount => field == null ? 0 : field.Count;
    /// <summary>Number of retained sparse contact nodes, including currently touched nodes.</summary>
    public int RetainedStampCount => ActiveNodeCount;
    /// <summary>Current collider contact response before the independent recovery of departed nodes.</summary>
    public float ContactStrength => response;
    public UpdateMode SamplingMode { get => updateMode; set => updateMode = value; }
    public InteractionState State { get; private set; } = InteractionState.Disabled;
    public GrassInteractorSupportStatus LastSupportStatus { get; private set; }

    public string Diagnostic => State switch
    {
        InteractionState.MissingCollider => "Assign the character's actual collider.",
        InteractionState.UnsupportedCollider => "Use a supported upright collider and an explicit self root that contains it.",
        InteractionState.NoGroundContact => "The collider has no unique, usable grass support within its grounding clearance.",
        InteractionState.PathReset => "The discontinuous or over-budget path was cleared; only the current footprint was retained.",
        InteractionState.TrailBudgetExceeded => "The current footprint exceeds the interaction budget. Increase grid spacing or reduce the collider footprint.",
        InteractionState.MissingCaptureMaterial => ReferenceEquals(captureShader, null)
            ? "The retained InfiniteGrassInteraction resource material is missing or has no GrassSlope pass."
            : "The assigned interaction shader must provide a GrassSlope pass using premultiplied vertex RGBA.",
        InteractionState.InvalidTime => "The interaction clock must be finite and monotonic between observations.",
        _ => string.Empty
    };

    /// <summary>Assign the actor and optional explicit support without retaining a previous actor's trail.</summary>
    public virtual void Configure(Collider actor, Collider support = null, Transform selfRoot = null)
    {
        actorCollider = actor;
        explicitSupport = support;
        actorRoot = selfRoot;
        ClearHistory();
    }

    /// <summary>One explicit observation. Select Manual sampling to disable automatic update callbacks.</summary>
    public void Sample(double now) => Advance(now);

    public void ResetInteraction() => ClearHistory();

    /// <summary>Compatibility surfaces configure this one producer before each observation.</summary>
    protected virtual void ApplyConfiguration() { }

    protected void ConfigureInteraction(Collider actor, Shader shader, int acceptedLayers, float padding,
        float clearance, float amplitude, float attack, float recovery)
    {
        actorCollider = actor;
        captureShader = shader;
        acceptedSupportLayers = acceptedLayers;
        footprintPadding = padding;
        groundClearance = clearance;
        attenuateWithClearance = true;
        bendStrength = amplitude;
        attackDuration = attack;
        recoveryDuration = recovery;
    }

    /// <summary>Clear immediately on teleports, respawns, session changes or a floating-origin shift.</summary>
    [ContextMenu("Clear interaction history")]
    public void ClearHistory()
    {
        observationValid = false;
        observedShape = default;
        observedTime = 0.0;
        ClearRecordedHistory(false);
    }

    private void ClearRecordedHistory(bool preserveContactResponse)
    {
        field?.Clear();
        interactionMesh?.Clear();
        previousValid = false;
        if (!preserveContactResponse)
        {
            response = 0f;
            responseTime = 0.0;
            responseValid = false;
            lastGroundSupport = null;
        }
        historyMinimum = historyMaximum = default;
        unchecked { historyGeneration++; }
        SetVisible(false);
        State = isActiveAndEnabled ? InteractionState.NoGroundContact : InteractionState.Disabled;
    }

    /// <summary>The renderer records these owned objects; callers must not mutate or destroy them.</summary>
    public bool TryGetDraw(out Mesh mesh, out Material material, out Bounds bounds)
    {
        mesh = null;
        material = null;
        bounds = default;
        if (!visible || !IsEligible() || !gameObject.scene.isLoaded || interactionMesh == null ||
            configuredScene != gameObject.scene || !actorCollider || configuredActorScene != actorCollider.gameObject.scene)
            return false;
        ValidateRecordedSupport();
        if (!visible)
            return false;
        mesh = interactionMesh.Mesh;
        material = interactionMesh.Material;
        bounds = interactionMesh.Bounds;
        return mesh && material;
    }

    private void ValidateRecordedSupport()
    {
        if (ReferenceEquals(lastGroundSupport, null))
            return;
        // Rendering can occur before another observation, including in Manual
        // mode. Reject invalid retained positions without sampling or physics work.
        if (!GrassInteractorSupport.IsUsableSupport(lastGroundSupport))
            ClearHistory();
        else if (GrassInteractorSupport.GetSupportSignature(lastGroundSupport) != lastGroundSignature)
            ClearRecordedHistory(true);
    }

    private void Reset()
    {
        Collider[] colliders = GetComponents<Collider>();
        if (colliders.Length == 1)
            actorCollider = colliders[0];
    }

    protected virtual void OnEnable()
    {
        if (!IsEligible())
            return;
        if (!activeInteractors.Contains(this))
        {
            activeInteractors.Add(this);
            Interlocked.Increment(ref revision);
        }
        registered = true;
        registeredGeneration = registryGeneration;
        GrassPlacementArea.SourceChanged -= OnPlacementChanged;
        GrassPlacementArea.SourceChanged += OnPlacementChanged;
        TerrainCallbacks.heightmapChanged -= OnTerrainHeightChanged;
        TerrainCallbacks.textureChanged -= OnTerrainTextureChanged;
        TerrainCallbacks.heightmapChanged += OnTerrainHeightChanged;
        TerrainCallbacks.textureChanged += OnTerrainTextureChanged;
#if UNITY_EDITOR
        UnityEditor.EditorApplication.update -= EditorUpdate;
        UnityEditor.EditorApplication.update += EditorUpdate;
#endif
        playing = Application.IsPlaying(gameObject);
        hasTime = false;
        ClearHistory();
    }

    protected void OnDisable()
    {
        GrassPlacementArea.SourceChanged -= OnPlacementChanged;
        TerrainCallbacks.heightmapChanged -= OnTerrainHeightChanged;
        TerrainCallbacks.textureChanged -= OnTerrainTextureChanged;
#if UNITY_EDITOR
        UnityEditor.EditorApplication.update -= EditorUpdate;
#endif
        ClearHistory();
        if (activeInteractors.Remove(this))
            Interlocked.Increment(ref revision);
        registered = false;
        interactionMesh?.Dispose();
        interactionMesh = null;
        field = null;
        supportResolver = null;
        configuredActor = configuredSupport = null;
        configuredShader = null;
        configuredRoot = null;
        configuredScene = configuredActorScene = default;
        hasTime = warnedBudget = warnedMaterial = false;
        State = InteractionState.Disabled;
    }

    protected void OnDestroy() => OnDisable();

    protected void Update()
    {
        if (!IsEligible())
        {
            if (registered)
                OnDisable();
            return;
        }
        if (!registered || registeredGeneration != registryGeneration)
            OnEnable();
    }

    protected void LateUpdate()
    {
        if (Application.IsPlaying(gameObject) && updateMode == UpdateMode.LateUpdate)
            Advance(Time.timeAsDouble);
    }

    protected void FixedUpdate()
    {
        if (Application.IsPlaying(gameObject) && updateMode == UpdateMode.FixedUpdate)
            Advance(Time.fixedTimeAsDouble);
    }

#if UNITY_EDITOR
    private void EditorUpdate()
    {
        if (this && updateMode != UpdateMode.Manual && !Application.IsPlaying(gameObject))
            Advance(UnityEditor.EditorApplication.timeSinceStartup);
    }
#endif

    private void Advance(double now)
    {
        if (!IsEligible() || !gameObject.scene.isLoaded)
            return;
        ApplyConfiguration();
        if (!IsEligible())
            return;
        if (!registered || registeredGeneration != registryGeneration)
            OnEnable();
        bool nowPlaying = Application.IsPlaying(gameObject);
        if (playing != nowPlaying || (hasTime && now < lastTime))
        {
            ClearHistory();
            hasTime = false;
        }
        playing = nowPlaying;
        if (double.IsNaN(now) || double.IsInfinity(now))
        {
            ClearHistory();
            hasTime = false;
            State = InteractionState.InvalidTime;
            return;
        }
        lastTime = now;
        hasTime = true;
        if (!actorCollider)
        {
            ClearHistory();
            State = InteractionState.MissingCollider;
            return;
        }

        Transform selfRoot = actorRoot;
        if (!selfRoot && actorCollider.transform.IsChildOf(transform))
            selfRoot = transform;
        float spacing = Mathf.Max(0.01f, FiniteOr(gridSpacing, 0.15f));
        int capacity = Mathf.Clamp(nodeCapacity, 16, 2048);
        float clearance = Mathf.Max(0f, FiniteOr(groundClearance, 0.15f));
        float penetration = Mathf.Max(0f, FiniteOr(allowedPenetration, 0.1f));
        float padding = Mathf.Max(0f, FiniteOr(footprintPadding, 0f));
        if (configuredActor != actorCollider || configuredRoot != selfRoot || configuredSupport != explicitSupport ||
            configuredScene != gameObject.scene || configuredActorScene != actorCollider.gameObject.scene ||
            configuredUpdateMode != updateMode || configuredSpacing != spacing || configuredCapacity != capacity ||
            configuredClearance != clearance || configuredPenetration != penetration ||
            configuredPadding != padding || configuredLayers != acceptedSupportLayers.value)
        {
            ClearHistory();
            if (field != null && (configuredSpacing != spacing || configuredCapacity != capacity))
                field = null;
            configuredActor = actorCollider;
            configuredSupport = explicitSupport;
            configuredRoot = selfRoot;
            configuredScene = gameObject.scene;
            configuredActorScene = actorCollider.gameObject.scene;
            configuredUpdateMode = updateMode;
            configuredSpacing = spacing;
            configuredCapacity = capacity;
            configuredClearance = clearance;
            configuredPenetration = penetration;
            configuredPadding = padding;
            configuredLayers = acceptedSupportLayers.value;
        }
        if (!ReferenceEquals(configuredShader, captureShader))
        {
            interactionMesh?.Dispose();
            interactionMesh = null;
            configuredShader = captureShader;
            SetVisible(false);
        }

        // Synchronize once for the complete current/path query batch, including
        // controllers moved through Transform with auto-sync disabled.
        Physics.SyncTransforms();
        ValidateRecordedSupport();
        float recovery = Mathf.Max(0.01f, FiniteOr(recoveryDuration, 2f));
        field?.Prune(now, recovery);
        if (!GrassInteractorSupport.TryGetShape(actorCollider, selfRoot,
                out GrassInteractorShape shape, out GrassInteractorSupportStatus status))
        {
            LastSupportStatus = status;
            previousValid = false;
            responseValid = false;
            response = 0f;
            State = InteractionState.UnsupportedCollider;
            Rebuild(now, recovery);
            return;
        }
        bool reset = ObservePose(in shape, now);
        supportResolver ??= new GrassInteractorSupport();
        uint generationBeforeQuery = historyGeneration;
        float ownershipMargin = padding + spacing * 1.415f;
        bool grounded = supportResolver.TryResolve(in shape, explicitSupport, clearance, penetration,
            out GrassInteractorSupportSample support, out status, ownershipMargin, acceptedSupportLayers.value);
        if (!this || !registered || !IsEligible())
            return;
        LastSupportStatus = status;
        if (!grounded)
        {
            previousValid = false;
            responseValid = false;
            response = 0f;
            State = field != null && field.Count > 0 ? InteractionState.Recovering : InteractionState.NoGroundContact;
            Rebuild(now, recovery);
            return;
        }

        field ??= new GrassInteractionField(spacing, capacity);
        bool continuous = previousValid && generationBeforeQuery == historyGeneration;
        Vector2 currentCenter = new Vector2(shape.Center.x, shape.Center.z);
        Vector2 oldCenter = new Vector2(previousShape.Center.x, previousShape.Center.z);
        float distance = continuous ? Vector3.Distance(previousShape.Center, shape.Center) : 0f;
        bool pathBudgetExceeded = false;
        if (continuous)
        {
            double step = Math.Min(previousShape.Radius, shape.Radius) * 0.5;
            double intervals = Math.Ceiling(distance / step);
            // Reserve one query for an ownership-change endpoint recheck.
            if (double.IsNaN(intervals) || double.IsInfinity(intervals) || intervals >= MaximumPathQueries)
            {
                ClearRecordedHistory(false);
                continuous = false;
                reset = true;
                pathBudgetExceeded = true;
            }
            else
            {
                for (int i = 1; i < (int)intervals; i++)
                {
                    float fraction = i / (float)intervals;
                    GrassInteractorShape intermediate = shape.WithPose(
                        Vector3.Lerp(previousShape.Center, shape.Center, fraction),
                        Mathf.Lerp(previousShape.Radius, shape.Radius, fraction),
                        Mathf.Lerp(previousShape.Height, shape.Height, fraction));
                    bool supported = supportResolver.TryResolve(in intermediate, explicitSupport, clearance, penetration,
                        out _, out _, ownershipMargin, acceptedSupportLayers.value);
                    if (!supported)
                    {
                        responseValid = false;
                        response = 0f;
                        continuous = false;
                        break;
                    }
                    if (generationBeforeQuery != historyGeneration)
                    {
                        continuous = false;
                        break;
                    }
                }
            }
        }

        if (!this || !registered || !IsEligible())
            return;
        // Refreshing authored ownership can synchronously invalidate this field.
        // If an intermediate query changed it, re-check the endpoint once before
        // stamping; a second change rejects the observation instead of reusing
        // an earlier support decision.
        if (generationBeforeQuery != historyGeneration)
        {
            continuous = false;
            uint currentGeneration = historyGeneration;
            bool stillGrounded = supportResolver.TryResolve(in shape, explicitSupport, clearance, penetration,
                out support, out status, ownershipMargin, acceptedSupportLayers.value);
            if (!this || !registered || !IsEligible())
                return;
            if (!stillGrounded || currentGeneration != historyGeneration)
            {
                LastSupportStatus = status;
                previousValid = false;
                responseValid = false;
                response = 0f;
                State = field.Count > 0 ? InteractionState.Recovering : InteractionState.NoGroundContact;
                Rebuild(now, recovery);
                return;
            }
            LastSupportStatus = status;
        }

        Vector3 facing = selfRoot ? selfRoot.forward : actorCollider.transform.forward;
        Vector2 fallback = new Vector2(-facing.x, -facing.z);
        if (fallback.sqrMagnitude < 0.000001f)
            fallback = Vector2.down;
        fallback.Normalize();
        float target = Mathf.Clamp01(FiniteOr(bendStrength, 0.85f));
        if (attenuateWithClearance && clearance > 0f)
            target *= 1f - Mathf.SmoothStep(0f, 1f, Mathf.Max(0f, support.Clearance) / clearance);
        float attack = Mathf.Max(0f, FiniteOr(attackDuration, 0f));
        // Losing world-space history does not imply that a collider left a
        // moving support. Carry only a still-valid bounded contact observation;
        // after invalidation a different support starts a new response.
        bool carryResponse = responseValid && (continuous || ReferenceEquals(support.Support, lastGroundSupport));
        float startResponse = carryResponse ? response : 0f;
        float nextResponse = GrassInteractionField.EvaluateAttack(startResponse, target,
            carryResponse ? now - responseTime : 0.0, attack);
        float radius = shape.Radius + padding;
        bool accepted = continuous
            ? field.AdvanceSweptCircle(oldCenter, previousShape.Radius + padding, previousTime,
                currentCenter, radius, now, fallback, startResponse, target, attack)
            : field.AdvanceSweptCircle(currentCenter, radius, now, currentCenter, radius, now,
                fallback, nextResponse, nextResponse, 0f);
        if (!accepted || pathBudgetExceeded)
        {
            if (!accepted)
            {
                ClearRecordedHistory(false);
                accepted = field.AdvanceSweptCircle(currentCenter, radius, now,
                    currentCenter, radius, now, fallback, nextResponse, nextResponse, 0f);
            }
            State = accepted ? InteractionState.PathReset : InteractionState.TrailBudgetExceeded;
            if (!warnedBudget)
                Debug.LogWarning(accepted
                    ? "Grass interaction exceeded its bounded history, sweep or support-query budget. The old trail was cleared and only the complete current footprint was retained. Increase Grid Spacing, shorten Recovery Seconds, increase Node Capacity within its limit, or provide more frequent movement observations."
                    : Diagnostic, this);
            warnedBudget = true;
        }
        else
        {
            warnedBudget = false;
            State = reset ? InteractionState.PathReset : InteractionState.Active;
        }
        previousValid = accepted;
        response = nextResponse;
        responseTime = now;
        responseValid = true;
        previousShape = shape;
        previousTime = now;
        lastGroundSupport = support.Support;
        lastGroundSignature = support.SupportSignature;
        Rebuild(now, recovery);
    }

    private bool ObservePose(in GrassInteractorShape shape, double now)
    {
        // Airborne observations still bound relocation and elapsed time. They
        // must not make a later teleport look like the first observed pose.
        double delta = now - observedTime;
        float distance = observationValid ? Vector3.Distance(observedShape.Center, shape.Center) : 0f;
        float teleport = Mathf.Max(0.01f, FiniteOr(teleportDistance, 5f));
        bool reset = observationValid && (delta < 0 || delta > MaximumContinuousSeconds || distance > teleport ||
            (delta == 0 && (distance > 0f || observedShape.Radius != shape.Radius || observedShape.Height != shape.Height)) ||
            !ReferenceEquals(observedShape.Actor, shape.Actor) || !ReferenceEquals(observedShape.ActorRoot, shape.ActorRoot) ||
            observedShape.PhysicsScene != shape.PhysicsScene);
        if (reset)
            ClearHistory();
        observedShape = shape;
        observedTime = now;
        observationValid = true;
        return reset;
    }

    private void Rebuild(double now, float recovery)
    {
        if (field == null || field.Count == 0)
        {
            interactionMesh?.Clear();
            historyMinimum = historyMaximum = default;
            SetVisible(false);
            return;
        }
        UpdateHistoryBounds();
        interactionMesh ??= new GrassInteractionMesh(captureShader);
        bool next = interactionMesh.Rebuild(field, now, recovery, 1f);
        if (!interactionMesh.Material)
        {
            State = InteractionState.MissingCaptureMaterial;
            if (!warnedMaterial)
                Debug.LogWarning(Diagnostic, this);
            warnedMaterial = true;
        }
        else
            warnedMaterial = false;
        SetVisible(next);
    }

    private void UpdateHistoryBounds()
    {
        Vector2Int minimum = field.GetKey(0), maximum = minimum;
        for (int i = 1; i < field.Count; i++)
        {
            Vector2Int key = field.GetKey(i);
            minimum = Vector2Int.Min(minimum, key);
            maximum = Vector2Int.Max(maximum, key);
        }
        // A node contributes to its four adjacent cells, including their halo.
        // Retained history needs invalidation even when its material is
        // unavailable, so use field endpoints instead of visible draw bounds.
        historyMinimum = field.GetPosition(minimum - Vector2Int.one);
        historyMaximum = field.GetPosition(maximum + Vector2Int.one);
    }

    private void SetVisible(bool next)
    {
        // A final removal revision clears cached interaction even with static
        // modifiers and stationary cameras. Mesh updates have the same signal.
        if (next || visible != next)
        {
            Interlocked.Increment(ref revision);
            unchecked { drawRevision++; }
        }
        visible = next;
    }

    private void OnPlacementChanged(GrassPlacementArea area, Bounds bounds, GrassPlacementChange change)
    {
        if ((change & (GrassPlacementChange.Density | GrassPlacementChange.Surface)) != 0 &&
            IntersectsHistory(bounds.min, bounds.max))
            ClearRecordedHistory(true);
    }

    private void OnTerrainHeightChanged(Terrain terrain, RectInt region, bool synched)
    {
        if (terrain && terrain.terrainData &&
            IntersectsHistory(terrain.transform.position, terrain.transform.position + terrain.terrainData.size))
            ClearRecordedHistory(true);
    }

    private void OnTerrainTextureChanged(Terrain terrain, string textureName, RectInt region, bool synched)
    {
        if (textureName == TerrainData.HolesTextureName)
            OnTerrainHeightChanged(terrain, region, synched);
    }

    private bool IsEligible()
    {
        if (!isActiveAndEnabled || !gameObject.scene.IsValid())
            return false;
#if UNITY_EDITOR
        if (UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(gameObject.scene))
            return false;
#endif
        return true;
    }

    private bool IntersectsHistory(Vector3 minimum, Vector3 maximum) => field != null && field.Count > 0 &&
        historyMinimum.x <= maximum.x && historyMaximum.x >= minimum.x &&
        historyMinimum.y <= maximum.z && historyMaximum.y >= minimum.z;

    private static float FiniteOr(float value, float fallback) =>
        float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetRegistry()
    {
        activeInteractors.Clear();
        unchecked { registryGeneration++; }
        Interlocked.Increment(ref revision);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void RestoreRegistry()
    {
        GrassColliderInteractor[] interactors = FindObjectsByType<GrassColliderInteractor>(FindObjectsInactive.Exclude);
        for (int i = 0; i < interactors.Length; i++)
            if (interactors[i].IsEligible())
                interactors[i].OnEnable();
    }
}
