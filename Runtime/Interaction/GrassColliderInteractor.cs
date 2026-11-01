using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Serialization;

namespace Cone.Grass
{
    /// <summary>
    /// Optional collider-driven producer for the existing world-XZ grass interaction map.
    /// ClearHistory must be called when a gameplay session or character is reset in place.
    /// </summary>
    [ExecuteAlways, DisallowMultipleComponent, DefaultExecutionOrder(10000)]
    [AddComponentMenu("Cone/Grass/Collider Interactor")]
    public class GrassColliderInteractor : MonoBehaviour
    {
        /// <summary>When the interactor observes its collider.</summary>
        public enum UpdateMode
        {
            /// <summary>Observes in LateUpdate with <see cref="Time.timeAsDouble"/> while playing.</summary>
            LateUpdate,
            /// <summary>Observes in FixedUpdate with <see cref="Time.fixedTimeAsDouble"/> while playing.</summary>
            FixedUpdate,
            /// <summary>Observes only when <see cref="Sample"/> is called.</summary>
            Manual
        }

        /// <summary>The interactor's state after its latest observation; <see cref="Diagnostic"/> explains the problem states.</summary>
        public enum InteractionState
        {
            /// <summary>The component is disabled or not eligible.</summary>
            Disabled,
            /// <summary>No actor collider is assigned.</summary>
            MissingCollider,
            /// <summary>The actor collider is not a supported upright shape, or the self root does not contain it.</summary>
            UnsupportedCollider,
            /// <summary>The collider has no unique, usable grass support within its grounding clearance.</summary>
            NoGroundContact,
            /// <summary>The collider touches the grass and records contacts.</summary>
            Active,
            /// <summary>The collider has left the grass; earlier contacts are still recovering.</summary>
            Recovering,
            /// <summary>A discontinuous or over-budget path was cleared and only the current footprint was kept.</summary>
            PathReset,
            /// <summary>The current footprint alone exceeds the interaction budget.</summary>
            TrailBudgetExceeded,
            /// <summary>No interaction material with a GrassSlope pass is available.</summary>
            MissingCaptureMaterial,
            /// <summary>The interaction clock was not finite.</summary>
            InvalidTime
        }

        private const int MaximumPathQueries = 64;
        private const double MaximumContinuousSeconds = 0.25;
        private static readonly List<GrassColliderInteractor> s_activeInteractors = new List<GrassColliderInteractor>();
        private static int s_revision = 1;
        private static int s_registryGeneration = 1;

        [Header("Character and support")]
        [Tooltip("The actual upright CapsuleCollider, CharacterController or SphereCollider that touches the grass.")]
        [SerializeField, FormerlySerializedAs("actorCollider")] private Collider _actorCollider;
        [Tooltip("Self-collision exclusion root. Defaults to this transform only when the actor collider is beneath it.")]
        [SerializeField, FormerlySerializedAs("actorRoot")] private Transform _actorRoot;
        [Tooltip("Optional explicit supporting collider for legacy or generic placement. Other nearer floors and ambiguous grass owners still reject contact.")]
        [SerializeField, FormerlySerializedAs("explicitSupport")] private Collider _explicitSupport;
        [Tooltip("Optional producer using premultiplied vertex RGBA and the GrassSlope pass. Empty uses the retained package material.")]
        [SerializeField, FormerlySerializedAs("captureShader")] private Shader _captureShader;
        [SerializeField, FormerlySerializedAs("acceptedSupportLayers")] private LayerMask _acceptedSupportLayers = ~0;
        [Tooltip("Floors on these layers carry contacts where no grass area reaches, so effects that read the interaction field "
            + "(ground fog) see footsteps where nothing grows. Grass areas keep their own ownership.")]
        [SerializeField, FormerlySerializedAs("uncoveredSupportLayers")] private LayerMask _uncoveredSupportLayers;
        [SerializeField, FormerlySerializedAs("updateMode")] private UpdateMode _updateMode = UpdateMode.LateUpdate;
        [SerializeField, Min(0f), FormerlySerializedAs("groundClearance")] private float _groundClearance = 0.15f;
        [SerializeField, Min(0f), FormerlySerializedAs("allowedPenetration")] private float _allowedPenetration = 0.1f;
        [SerializeField, FormerlySerializedAs("attenuateWithClearance")] private bool _attenuateWithClearance;

        [Header("Interaction and recovery")]
        [SerializeField, Range(0f, 1f), FormerlySerializedAs("bendStrength")] private float _bendStrength = 0.85f;
        [SerializeField, Min(0f), FormerlySerializedAs("attackDuration")] private float _attackDuration;
        [SerializeField, Min(0.01f), FormerlySerializedAs("recoveryDuration")] private float _recoveryDuration = 2f;
        [Tooltip("Extra horizontal interaction influence. Support casts always use the actual collider radius.")]
        [SerializeField, Min(0f), FormerlySerializedAs("footprintPadding")] private float _footprintPadding;
        [Tooltip("Fixed world-grid spacing for this history. Smaller values need more nodes and finer grass capture resolution.")]
        [SerializeField, Min(0.01f), FormerlySerializedAs("gridSpacing")] private float _gridSpacing = 0.15f;
        [Tooltip("Maximum retained contact nodes. Overflow clears the path and attempts the complete current footprint only.")]
        [SerializeField, Range(16, 2048), FormerlySerializedAs("nodeCapacity")] private int _nodeCapacity = 1024;
        [Tooltip("Motion farther than this between observations clears the old field instead of joining the positions.")]
        [SerializeField, Min(0.01f), FormerlySerializedAs("teleportDistance")] private float _teleportDistance = 5f;

        private GrassInteractionField _field;
        private GrassInteractionMesh _interactionMesh;
        private GrassInteractorSupport _supportResolver;
        private GrassInteractorShape _previousShape, _observedShape;
        private Collider _configuredActor, _configuredSupport, _lastGroundSupport;
        private Shader _configuredShader;
        private Transform _configuredRoot;
        private Scene _configuredScene, _configuredActorScene;
        private UpdateMode _configuredUpdateMode;
        private float _configuredSpacing, _configuredClearance, _configuredPenetration, _configuredPadding;
        private int _configuredLayers, _configuredUncoveredLayers;
        private int _configuredCapacity, _registeredGeneration;
        private int _lastGroundSignature;
        private uint _historyGeneration, _drawRevision;
        private double _previousTime, _responseTime, _observedTime, _lastTime;
        private float _response;
        private bool _registered, _previousValid, _responseValid, _observationValid, _hasTime, _visible, _warnedBudget, _warnedMaterial, _playing;
        private Vector2 _historyMinimum, _historyMaximum;

        /// <summary>The enabled, eligible interactors the renderer feature captures.</summary>
        public static IReadOnlyList<GrassColliderInteractor> ActiveInteractors => s_activeInteractors;
        /// <summary>Advances when any interactor registers, unregisters or changes its visible interaction.</summary>
        public static uint Revision => unchecked((uint)Volatile.Read(ref s_revision));
        /// <summary>Advances when this interactor's drawn interaction changes.</summary>
        public uint DrawRevision => _drawRevision;
        /// <summary>The upright collider that touches the grass.</summary>
        public Collider ActorCollider => _actorCollider;
        /// <summary>The optional explicit supporting collider, or null for automatic support.</summary>
        public Collider ExplicitSupport => _explicitSupport;
        /// <summary>
        /// Floors on these layers carry contacts where no grass area reaches, so effects that read the interaction field
        /// (ground fog) see footsteps where nothing grows.
        /// </summary>
        public LayerMask UncoveredSupportLayers { get => _uncoveredSupportLayers; set => _uncoveredSupportLayers = value; }
        /// <summary>Number of retained contact nodes in the interaction history.</summary>
        public int ActiveNodeCount => _field == null ? 0 : _field.Count;
        /// <summary>Number of retained sparse contact nodes, including currently touched nodes.</summary>
        public int RetainedStampCount => ActiveNodeCount;
        /// <summary>Current collider contact response before the independent recovery of departed nodes.</summary>
        public float ContactStrength => _response;
        /// <summary>When the interactor observes its collider; <see cref="UpdateMode.Manual"/> leaves it to <see cref="Sample"/>.</summary>
        public UpdateMode SamplingMode { get => _updateMode; set => _updateMode = value; }
        /// <summary>The state after the latest observation.</summary>
        public InteractionState State { get; private set; } = InteractionState.Disabled;
        /// <summary>The result of the latest collider shape or support query.</summary>
        public GrassInteractorSupportStatus LastSupportStatus { get; private set; }

        /// <summary>A short explanation of how to fix the current <see cref="State"/>, or empty when there is nothing to fix.</summary>
        public string Diagnostic => State switch
        {
            InteractionState.MissingCollider => "Assign the character's actual collider.",
            InteractionState.UnsupportedCollider => "Use a supported upright collider and an explicit self root that contains it.",
            InteractionState.NoGroundContact => "The collider has no unique, usable grass support within its grounding clearance.",
            InteractionState.PathReset => "The discontinuous or over-budget path was cleared; only the current footprint was retained.",
            InteractionState.TrailBudgetExceeded => "The current footprint exceeds the interaction budget. Increase grid spacing or reduce the collider footprint.",
            InteractionState.MissingCaptureMaterial => ReferenceEquals(_captureShader, null)
                ? "The retained InfiniteGrassInteraction resource material is missing or has no GrassSlope pass."
                : "The assigned interaction shader must provide a GrassSlope pass using premultiplied vertex RGBA.",
            InteractionState.InvalidTime => "The interaction clock must be finite and monotonic between observations.",
            _ => string.Empty
        };

        /// <summary>Assign the actor and optional explicit support without retaining a previous actor's trail.</summary>
        /// <param name="actor">The upright CapsuleCollider, CharacterController or SphereCollider that touches the grass.</param>
        /// <param name="support">Optional explicit supporting collider; null resolves the support automatically.</param>
        /// <param name="selfRoot">Optional self-collision exclusion root.</param>
        public virtual void Configure(Collider actor, Collider support = null, Transform selfRoot = null)
        {
            _actorCollider = actor;
            _explicitSupport = support;
            _actorRoot = selfRoot;
            ClearHistory();
        }

        /// <summary>One explicit observation. Select Manual sampling to disable automatic update callbacks.</summary>
        /// <param name="now">The interaction clock in seconds; it must be finite, and running backwards clears the history.</param>
        public void Sample(double now) => Advance(now);

        /// <summary>Same as <see cref="ClearHistory"/>.</summary>
        public void ResetInteraction() => ClearHistory();

        /// <summary>Compatibility surfaces configure this one producer before each observation.</summary>
        protected virtual void ApplyConfiguration() { }

        /// <summary>
        /// Sets the base interaction settings from a derived component, with the bend fading out as the collider's
        /// clearance above its support approaches <paramref name="clearance"/>.
        /// </summary>
        /// <param name="actor">The collider that touches the grass.</param>
        /// <param name="shader">Optional GrassSlope producer; null uses the retained package material.</param>
        /// <param name="acceptedLayers">Layer mask of accepted supporting colliders.</param>
        /// <param name="padding">Extra horizontal interaction radius, in metres.</param>
        /// <param name="clearance">Grounding clearance, in metres.</param>
        /// <param name="amplitude">Bend strength, from 0 to 1.</param>
        /// <param name="attack">Seconds over which a contact approaches its full bend.</param>
        /// <param name="recovery">Seconds a departed contact takes to recover.</param>
        protected void ConfigureInteraction(Collider actor, Shader shader, int acceptedLayers, float padding,
            float clearance, float amplitude, float attack, float recovery)
        {
            _actorCollider = actor;
            _captureShader = shader;
            _acceptedSupportLayers = acceptedLayers;
            _footprintPadding = padding;
            _groundClearance = clearance;
            _attenuateWithClearance = true;
            _bendStrength = amplitude;
            _attackDuration = attack;
            _recoveryDuration = recovery;
        }

        /// <summary>Clear immediately on teleports, respawns, session changes or a floating-origin shift.</summary>
        [ContextMenu("Clear interaction history")]
        public void ClearHistory()
        {
            _observationValid = false;
            _observedShape = default;
            _observedTime = 0.0;
            ClearRecordedHistory(false);
        }

        private void ClearRecordedHistory(bool preserveContactResponse)
        {
            _field?.Clear();
            _interactionMesh?.Clear();
            _previousValid = false;
            if (!preserveContactResponse)
            {
                _response = 0f;
                _responseTime = 0.0;
                _responseValid = false;
                _lastGroundSupport = null;
            }
            _historyMinimum = _historyMaximum = default;
            unchecked { _historyGeneration++; }
            SetVisible(false);
            State = isActiveAndEnabled ? InteractionState.NoGroundContact : InteractionState.Disabled;
        }

        /// <summary>The renderer records these owned objects; callers must not mutate or destroy them.</summary>
        /// <param name="mesh">The interaction mesh in world space.</param>
        /// <param name="material">The material whose GrassSlope pass draws the mesh.</param>
        /// <param name="bounds">The mesh's world bounds.</param>
        /// <returns>Whether the interactor currently has visible interaction to draw.</returns>
        public bool TryGetDraw(out Mesh mesh, out Material material, out Bounds bounds)
        {
            mesh = null;
            material = null;
            bounds = default;
            if (!_visible || !IsEligible() || !gameObject.scene.isLoaded || _interactionMesh == null ||
                _configuredScene != gameObject.scene || !_actorCollider || _configuredActorScene != _actorCollider.gameObject.scene)
            {
                return false;
            }

            ValidateRecordedSupport();
            if (!_visible)
            {
                return false;
            }

            mesh = _interactionMesh.Mesh;
            material = _interactionMesh.Material;
            bounds = _interactionMesh.Bounds;
            return mesh && material;
        }

        private void ValidateRecordedSupport()
        {
            if (ReferenceEquals(_lastGroundSupport, null))
            {
                return;
            }
            // Rendering can occur before another observation, including in Manual
            // mode. Reject invalid retained positions without sampling or physics work.
            if (!GrassInteractorSupport.IsUsableSupport(_lastGroundSupport))
            {
                ClearHistory();
            }
            else if (GrassInteractorSupport.GetSupportSignature(_lastGroundSupport) != _lastGroundSignature)
            {
                ClearRecordedHistory(true);
            }
        }

        private void Reset()
        {
            Collider[] colliders = GetComponents<Collider>();
            if (colliders.Length == 1)
            {
                _actorCollider = colliders[0];
            }
        }

        /// <summary>Registers the interactor and its change callbacks and clears its history.</summary>
        protected virtual void OnEnable()
        {
            if (!IsEligible())
            {
                return;
            }

            if (!s_activeInteractors.Contains(this))
            {
                s_activeInteractors.Add(this);
                Interlocked.Increment(ref s_revision);
            }
            _registered = true;
            _registeredGeneration = s_registryGeneration;
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
            _playing = Application.IsPlaying(gameObject);
            _hasTime = false;
            ClearHistory();
        }

        /// <summary>Unregisters the interactor, clears its history and releases its interaction mesh.</summary>
        protected void OnDisable()
        {
            GrassPlacementArea.SourceChanged -= OnPlacementChanged;
            TerrainCallbacks.heightmapChanged -= OnTerrainHeightChanged;
            TerrainCallbacks.textureChanged -= OnTerrainTextureChanged;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= EditorUpdate;
#endif
            ClearHistory();
            if (s_activeInteractors.Remove(this))
            {
                Interlocked.Increment(ref s_revision);
            }

            _registered = false;
            _interactionMesh?.Dispose();
            _interactionMesh = null;
            _field = null;
            _supportResolver = null;
            _configuredActor = _configuredSupport = null;
            _configuredShader = null;
            _configuredRoot = null;
            _configuredScene = _configuredActorScene = default;
            _hasTime = _warnedBudget = _warnedMaterial = false;
            State = InteractionState.Disabled;
        }

        /// <summary>Releases the interactor as <see cref="OnDisable"/> does.</summary>
        protected void OnDestroy() => OnDisable();

        /// <summary>Registers or unregisters the interactor when its eligibility changes, for example after a scene move.</summary>
        protected void Update()
        {
            if (!IsEligible())
            {
                if (_registered)
                {
                    OnDisable();
                }

                return;
            }
            if (!_registered || _registeredGeneration != s_registryGeneration)
            {
                OnEnable();
            }
        }

        /// <summary>Observes the collider while playing in <see cref="UpdateMode.LateUpdate"/> mode.</summary>
        protected void LateUpdate()
        {
            if (Application.IsPlaying(gameObject) && _updateMode == UpdateMode.LateUpdate)
            {
                Advance(Time.timeAsDouble);
            }
        }

        /// <summary>Observes the collider while playing in <see cref="UpdateMode.FixedUpdate"/> mode.</summary>
        protected void FixedUpdate()
        {
            if (Application.IsPlaying(gameObject) && _updateMode == UpdateMode.FixedUpdate)
            {
                Advance(Time.fixedTimeAsDouble);
            }
        }

#if UNITY_EDITOR
        private void EditorUpdate()
        {
            if (this && _updateMode != UpdateMode.Manual && !Application.IsPlaying(gameObject))
            {
                Advance(UnityEditor.EditorApplication.timeSinceStartup);
            }
        }
#endif

        private void Advance(double now)
        {
            if (!IsEligible() || !gameObject.scene.isLoaded)
            {
                return;
            }

            ApplyConfiguration();
            if (!IsEligible())
            {
                return;
            }

            if (!_registered || _registeredGeneration != s_registryGeneration)
            {
                OnEnable();
            }

            bool nowPlaying = Application.IsPlaying(gameObject);
            if (_playing != nowPlaying || (_hasTime && now < _lastTime))
            {
                ClearHistory();
                _hasTime = false;
            }
            _playing = nowPlaying;
            if (double.IsNaN(now) || double.IsInfinity(now))
            {
                ClearHistory();
                _hasTime = false;
                State = InteractionState.InvalidTime;
                return;
            }
            _lastTime = now;
            _hasTime = true;
            if (!_actorCollider)
            {
                ClearHistory();
                State = InteractionState.MissingCollider;
                return;
            }

            Transform selfRoot = _actorRoot;
            if (!selfRoot && _actorCollider.transform.IsChildOf(transform))
            {
                selfRoot = transform;
            }

            float spacing = Mathf.Max(0.01f, FiniteOr(_gridSpacing, 0.15f));
            int capacity = Mathf.Clamp(_nodeCapacity, 16, 2048);
            float clearance = Mathf.Max(0f, FiniteOr(_groundClearance, 0.15f));
            float penetration = Mathf.Max(0f, FiniteOr(_allowedPenetration, 0.1f));
            float padding = Mathf.Max(0f, FiniteOr(_footprintPadding, 0f));
            if (_configuredActor != _actorCollider || _configuredRoot != selfRoot || _configuredSupport != _explicitSupport ||
                _configuredScene != gameObject.scene || _configuredActorScene != _actorCollider.gameObject.scene ||
                _configuredUpdateMode != _updateMode || _configuredSpacing != spacing || _configuredCapacity != capacity ||
                _configuredClearance != clearance || _configuredPenetration != penetration ||
                _configuredPadding != padding || _configuredLayers != _acceptedSupportLayers.value ||
                _configuredUncoveredLayers != _uncoveredSupportLayers.value)
            {
                ClearHistory();
                if (_field != null && (_configuredSpacing != spacing || _configuredCapacity != capacity))
                {
                    _field = null;
                }

                _configuredActor = _actorCollider;
                _configuredSupport = _explicitSupport;
                _configuredRoot = selfRoot;
                _configuredScene = gameObject.scene;
                _configuredActorScene = _actorCollider.gameObject.scene;
                _configuredUpdateMode = _updateMode;
                _configuredSpacing = spacing;
                _configuredCapacity = capacity;
                _configuredClearance = clearance;
                _configuredPenetration = penetration;
                _configuredPadding = padding;
                _configuredLayers = _acceptedSupportLayers.value;
                _configuredUncoveredLayers = _uncoveredSupportLayers.value;
            }
            if (!ReferenceEquals(_configuredShader, _captureShader))
            {
                _interactionMesh?.Dispose();
                _interactionMesh = null;
                _configuredShader = _captureShader;
                SetVisible(false);
            }

            // Synchronize once for the complete current/path query batch, including
            // controllers moved through Transform with auto-sync disabled.
            Physics.SyncTransforms();
            ValidateRecordedSupport();
            float recovery = Mathf.Max(0.01f, FiniteOr(_recoveryDuration, 2f));
            _field?.Prune(now, recovery);
            if (!GrassInteractorSupport.TryGetShape(_actorCollider, selfRoot,
                    out GrassInteractorShape shape, out GrassInteractorSupportStatus status))
            {
                LastSupportStatus = status;
                _previousValid = false;
                _responseValid = false;
                _response = 0f;
                State = InteractionState.UnsupportedCollider;
                Rebuild(now, recovery);
                return;
            }
            bool reset = ObservePose(in shape, now);
            _supportResolver ??= new GrassInteractorSupport();
            uint generationBeforeQuery = _historyGeneration;
            float ownershipMargin = padding + spacing * 1.415f;
            bool grounded = _supportResolver.TryResolve(in shape, _explicitSupport, clearance, penetration,
                out GrassInteractorSupportSample support, out status, ownershipMargin, _acceptedSupportLayers.value,
                _uncoveredSupportLayers.value);
            if (!this || !_registered || !IsEligible())
            {
                return;
            }

            LastSupportStatus = status;
            if (!grounded)
            {
                _previousValid = false;
                _responseValid = false;
                _response = 0f;
                State = _field != null && _field.Count > 0 ? InteractionState.Recovering : InteractionState.NoGroundContact;
                Rebuild(now, recovery);
                return;
            }

            _field ??= new GrassInteractionField(spacing, capacity);
            bool continuous = _previousValid && generationBeforeQuery == _historyGeneration;
            Vector2 currentCenter = new Vector2(shape.Center.x, shape.Center.z);
            Vector2 oldCenter = new Vector2(_previousShape.Center.x, _previousShape.Center.z);
            float distance = continuous ? Vector3.Distance(_previousShape.Center, shape.Center) : 0f;
            bool pathBudgetExceeded = false;
            if (continuous)
            {
                double step = Math.Min(_previousShape.Radius, shape.Radius) * 0.5;
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
                            Vector3.Lerp(_previousShape.Center, shape.Center, fraction),
                            Mathf.Lerp(_previousShape.Radius, shape.Radius, fraction),
                            Mathf.Lerp(_previousShape.Height, shape.Height, fraction));
                        bool supported = _supportResolver.TryResolve(in intermediate, _explicitSupport, clearance, penetration,
                            out _, out _, ownershipMargin, _acceptedSupportLayers.value,
                            _uncoveredSupportLayers.value);
                        if (!supported)
                        {
                            _responseValid = false;
                            _response = 0f;
                            continuous = false;
                            break;
                        }
                        if (generationBeforeQuery != _historyGeneration)
                        {
                            continuous = false;
                            break;
                        }
                    }
                }
            }

            if (!this || !_registered || !IsEligible())
            {
                return;
            }
            // Refreshing authored ownership can synchronously invalidate this field.
            // If an intermediate query changed it, re-check the endpoint once before
            // stamping; a second change rejects the observation instead of reusing
            // an earlier support decision.
            if (generationBeforeQuery != _historyGeneration)
            {
                continuous = false;
                uint currentGeneration = _historyGeneration;
                bool stillGrounded = _supportResolver.TryResolve(in shape, _explicitSupport, clearance, penetration,
                    out support, out status, ownershipMargin, _acceptedSupportLayers.value,
                    _uncoveredSupportLayers.value);
                if (!this || !_registered || !IsEligible())
                {
                    return;
                }

                if (!stillGrounded || currentGeneration != _historyGeneration)
                {
                    LastSupportStatus = status;
                    _previousValid = false;
                    _responseValid = false;
                    _response = 0f;
                    State = _field.Count > 0 ? InteractionState.Recovering : InteractionState.NoGroundContact;
                    Rebuild(now, recovery);
                    return;
                }
                LastSupportStatus = status;
            }

            Vector3 facing = selfRoot ? selfRoot.forward : _actorCollider.transform.forward;
            Vector2 fallback = new Vector2(-facing.x, -facing.z);
            if (fallback.sqrMagnitude < 0.000001f)
            {
                fallback = Vector2.down;
            }

            fallback.Normalize();
            float target = Mathf.Clamp01(FiniteOr(_bendStrength, 0.85f));
            if (_attenuateWithClearance && clearance > 0f)
            {
                target *= 1f - Mathf.SmoothStep(0f, 1f, Mathf.Max(0f, support.Clearance) / clearance);
            }

            float attack = Mathf.Max(0f, FiniteOr(_attackDuration, 0f));
            // Losing world-space history does not imply that a collider left a
            // moving support. Carry only a still-valid bounded contact observation;
            // after invalidation a different support starts a new response.
            bool carryResponse = _responseValid && (continuous || ReferenceEquals(support.Support, _lastGroundSupport));
            float startResponse = carryResponse ? _response : 0f;
            float nextResponse = GrassInteractionField.EvaluateAttack(startResponse, target,
                carryResponse ? now - _responseTime : 0.0, attack);
            float radius = shape.Radius + padding;
            bool accepted = continuous
                ? _field.AdvanceSweptCircle(oldCenter, _previousShape.Radius + padding, _previousTime,
                    currentCenter, radius, now, fallback, startResponse, target, attack)
                : _field.AdvanceSweptCircle(currentCenter, radius, now, currentCenter, radius, now,
                    fallback, nextResponse, nextResponse, 0f);
            if (!accepted || pathBudgetExceeded)
            {
                if (!accepted)
                {
                    ClearRecordedHistory(false);
                    accepted = _field.AdvanceSweptCircle(currentCenter, radius, now,
                        currentCenter, radius, now, fallback, nextResponse, nextResponse, 0f);
                }
                State = accepted ? InteractionState.PathReset : InteractionState.TrailBudgetExceeded;
                if (!_warnedBudget)
                {
                    Debug.LogWarning(accepted
                        ? "Grass interaction exceeded its bounded history, sweep or support-query budget. The old trail was cleared and only the complete current footprint was retained. Increase Grid Spacing, shorten Recovery Seconds, increase Node Capacity within its limit, or provide more frequent movement observations."
                        : Diagnostic, this);
                }

                _warnedBudget = true;
            }
            else
            {
                _warnedBudget = false;
                State = reset ? InteractionState.PathReset : InteractionState.Active;
            }
            _previousValid = accepted;
            _response = nextResponse;
            _responseTime = now;
            _responseValid = true;
            _previousShape = shape;
            _previousTime = now;
            _lastGroundSupport = support.Support;
            _lastGroundSignature = support.SupportSignature;
            Rebuild(now, recovery);
        }

        private bool ObservePose(in GrassInteractorShape shape, double now)
        {
            // Airborne observations still bound relocation and elapsed time. They
            // must not make a later teleport look like the first observed pose.
            double delta = now - _observedTime;
            float distance = _observationValid ? Vector3.Distance(_observedShape.Center, shape.Center) : 0f;
            float teleport = Mathf.Max(0.01f, FiniteOr(_teleportDistance, 5f));
            bool reset = _observationValid && (delta < 0 || delta > MaximumContinuousSeconds || distance > teleport ||
                (delta == 0 && (distance > 0f || _observedShape.Radius != shape.Radius || _observedShape.Height != shape.Height)) ||
                !ReferenceEquals(_observedShape.Actor, shape.Actor) || !ReferenceEquals(_observedShape.ActorRoot, shape.ActorRoot) ||
                _observedShape.PhysicsScene != shape.PhysicsScene);
            if (reset)
            {
                ClearHistory();
            }

            _observedShape = shape;
            _observedTime = now;
            _observationValid = true;
            return reset;
        }

        private void Rebuild(double now, float recovery)
        {
            if (_field == null || _field.Count == 0)
            {
                _interactionMesh?.Clear();
                _historyMinimum = _historyMaximum = default;
                SetVisible(false);
                return;
            }
            UpdateHistoryBounds();
            _interactionMesh ??= new GrassInteractionMesh(_captureShader);
            bool next = _interactionMesh.Rebuild(_field, now, recovery, 1f);
            if (!_interactionMesh.Material)
            {
                State = InteractionState.MissingCaptureMaterial;
                if (!_warnedMaterial)
                {
                    Debug.LogWarning(Diagnostic, this);
                }

                _warnedMaterial = true;
            }
            else
            {
                _warnedMaterial = false;
            }

            SetVisible(next);
        }

        private void UpdateHistoryBounds()
        {
            Vector2Int minimum = _field.GetKey(0), maximum = minimum;
            for (int i = 1; i < _field.Count; i++)
            {
                Vector2Int key = _field.GetKey(i);
                minimum = Vector2Int.Min(minimum, key);
                maximum = Vector2Int.Max(maximum, key);
            }
            // A node contributes to its four adjacent cells, including their halo.
            // Retained history needs invalidation even when its material is
            // unavailable, so use field endpoints instead of visible draw bounds.
            _historyMinimum = _field.GetPosition(minimum - Vector2Int.one);
            _historyMaximum = _field.GetPosition(maximum + Vector2Int.one);
        }

        private void SetVisible(bool next)
        {
            // A final removal revision clears cached interaction even with static
            // modifiers and stationary cameras. Mesh updates have the same signal.
            if (next || _visible != next)
            {
                Interlocked.Increment(ref s_revision);
                unchecked { _drawRevision++; }
            }
            _visible = next;
        }

        private void OnPlacementChanged(GrassPlacementArea area, Bounds bounds, GrassPlacementChange change)
        {
            if ((change & (GrassPlacementChange.Density | GrassPlacementChange.Surface)) != 0 &&
                IntersectsHistory(bounds.min, bounds.max))
            {
                ClearRecordedHistory(true);
            }
        }

        private void OnTerrainHeightChanged(Terrain terrain, RectInt region, bool synched)
        {
            if (terrain && terrain.terrainData &&
                IntersectsHistory(terrain.transform.position, terrain.transform.position + terrain.terrainData.size))
            {
                ClearRecordedHistory(true);
            }
        }

        private void OnTerrainTextureChanged(Terrain terrain, string textureName, RectInt region, bool synched)
        {
            if (textureName == TerrainData.HolesTextureName)
            {
                OnTerrainHeightChanged(terrain, region, synched);
            }
        }

        private bool IsEligible()
        {
            if (!isActiveAndEnabled || !gameObject.scene.IsValid())
            {
                return false;
            }
#if UNITY_EDITOR
            if (UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(gameObject.scene))
            {
                return false;
            }
#endif
            return true;
        }

        private bool IntersectsHistory(Vector3 minimum, Vector3 maximum) => _field != null && _field.Count > 0 &&
            _historyMinimum.x <= maximum.x && _historyMaximum.x >= minimum.x &&
            _historyMinimum.y <= maximum.z && _historyMaximum.y >= minimum.z;

        private static float FiniteOr(float value, float fallback) =>
            float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRegistry()
        {
            s_activeInteractors.Clear();
            unchecked { s_registryGeneration++; }
            Interlocked.Increment(ref s_revision);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RestoreRegistry()
        {
            GrassColliderInteractor[] interactors = FindObjectsByType<GrassColliderInteractor>(FindObjectsInactive.Exclude);
            for (int i = 0; i < interactors.Length; i++)
            {
                if (interactors[i].IsEligible())
                {
                    interactors[i].OnEnable();
                }
            }
        }
    }
}
