using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace Cone.Grass
{
    /// <summary>Where an <see cref="InfiniteGrassRenderer"/> grows grass.</summary>
    public enum GrassPlacementMode
    {
        /// <summary>Only inside enabled <see cref="GrassPlacementArea"/> components.</summary>
        AuthoredAreas = 0,
        /// <summary>On the surfaces the renderer feature's height-map layer captures.</summary>
        LegacySurfaceLayer = 1
    }

    /// <summary>Scene settings and shared blade meshes. The renderer feature owns camera GPU resources.</summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class InfiniteGrassRenderer : MonoBehaviour
    {
        /// <summary>The one enabled renderer in a loaded scene that owns the grass settings, or null.</summary>
        public static InfiniteGrassRenderer Instance { get; private set; }

        [Header("Rendering"), SerializeField, FormerlySerializedAs("grassMaterial")]
        private Material _grassMaterial;

        /// <summary>The blade material, for example the package's Grass Blade material.</summary>
        public Material GrassMaterial { get => _grassMaterial; set => _grassMaterial = value; }
        [Tooltip("Authored Areas requires explicit Grass Placement Area components. Legacy mode uses the feature's height layer."), SerializeField, FormerlySerializedAs("placementMode")]
        private GrassPlacementMode _placementMode = GrassPlacementMode.AuthoredAreas;
        /// <summary>
        /// Authored Areas requires explicit Grass Placement Area components. Legacy mode uses the feature's height
        /// layer.
        /// </summary>
        public GrassPlacementMode PlacementMode { get => _placementMode; set => _placementMode = value; }
        [SerializeField, FormerlySerializedAs("renderInSceneView")] private bool _renderInSceneView = true;
        /// <summary>Whether scene-view cameras draw the grass too.</summary>
        public bool RenderInSceneView { get => _renderInSceneView; set => _renderInSceneView = value; }
        [Tooltip("Off draws no grass and keeps only the interaction capture, so effects that read the interaction field "
    + "(com.cone.render-features' ground fog) still see contacts where nothing grows, such as a dungeon floor."), SerializeField, FormerlySerializedAs("drawBlades")]
        private bool _drawBlades = true;
        /// <summary>
        /// Off draws no grass and keeps only the interaction capture, so effects that read the interaction field still
        /// see contacts where nothing grows.
        /// </summary>
        public bool DrawBlades { get => _drawBlades; set => _drawBlades = value; }
        [Tooltip("After Opaque Copies keeps contact shadows and motion vectors. Before Opaque Copies puts the grass in the depth and " +
            "opaque textures, so water refraction seen from above includes it."), SerializeField]
        private GrassDrawOrder _drawOrder = GrassDrawOrder.AfterOpaqueCopies;

        /// <summary>
        /// When the grass draws relative to URP's depth and opaque texture copies. <see cref="GrassDrawOrder.BeforeOpaqueCopies"/>
        /// lets water refraction include the grass, without contact shadows or grass motion vectors. Crest's underwater fog covers the grass fully after the copies and only partly before them (#102).
        /// </summary>
        public GrassDrawOrder DrawOrder { get => _drawOrder; set => _drawOrder = value; }
        [SerializeField]
        private GrassReflectionSettings _reflections = new GrassReflectionSettings();

        /// <summary>
        /// Which reflection cameras draw the grass (planar ones such as Crest's water reflections by default) and the cheaper
        /// path they take: wider spacing, a shorter draw distance, no interaction capture, contact shadows or motion vectors.
        /// </summary>
        public GrassReflectionSettings Reflections { get => _reflections ??= new GrassReflectionSettings(); set => _reflections = value ?? new GrassReflectionSettings(); }

        [Header("Population")]
        [Min(0.01f), SerializeField, FormerlySerializedAs("spacing")] private float _spacing = 0.1f;

        /// <summary>World-grid spacing between blade roots, in metres.</summary>
        public float Spacing { get => _spacing; set => _spacing = value; }
        [Min(1f), SerializeField, FormerlySerializedAs("drawDistance")] private float _drawDistance = 300f;
        /// <summary>Camera distance up to which grass is drawn, in metres.</summary>
        public float DrawDistance { get => _drawDistance; set => _drawDistance = value; }
        [Min(0f), SerializeField, FormerlySerializedAs("fullDensityDistance")] private float _fullDensityDistance = 30f;
        /// <summary>Camera distance up to which the population keeps its full density, in metres.</summary>
        public float FullDensityDistance { get => _fullDensityDistance; set => _fullDensityDistance = value; }
        [Min(0.1f), SerializeField, FormerlySerializedAs("densityFalloffExponent")] private float _densityFalloffExponent = 4f;
        /// <summary>Exponent of the density falloff between the full-density distance and the draw distance.</summary>
        public float DensityFalloffExponent { get => _densityFalloffExponent; set => _densityFalloffExponent = value; }
        [Range(0f, 0.25f), SerializeField, FormerlySerializedAs("densityTransition")] private float _densityTransition = 0.05f;
        /// <summary>Width of the band of random thresholds over which thinned blades fade instead of popping.</summary>
        public float DensityTransition { get => _densityTransition; set => _densityTransition = value; }
        [Tooltip("Authored areas keep every blade where their density is above zero and shorten it in proportion, "
    + "instead of thinning the population. A painted falloff then tapers an edge to stubble."), SerializeField, FormerlySerializedAs("densityShortensBlades")]
        private bool _densityShortensBlades;
        /// <summary>
        /// Authored areas keep every blade where their density is above zero and shorten it in proportion, instead of
        /// thinning the population.
        /// </summary>
        public bool DensityShortensBlades { get => _densityShortensBlades; set => _densityShortensBlades = value; }

        [Header("Water Line"), SerializeField]
        private GrassWaterLine _waterLine = new GrassWaterLine();

        /// <summary>
        /// Keeps grass and flowers above a water level, with a shoreline band where they thin in. Off by default; a
        /// level source such as the Crest bridge can drive it.
        /// </summary>
        public GrassWaterLine WaterLine { get => _waterLine ??= new GrassWaterLine(); set => _waterLine = value ?? new GrassWaterLine(); }

        [Header("Plant Kinds")]
        [Tooltip("Several kinds of blade, herb and flower, mixed by weight and gathered into drifts. Empty grows the material's one blade."), SerializeField, FormerlySerializedAs("variants")]
        private GrassVariantProfile _variants;

        /// <summary>
        /// Several kinds of blade, herb and flower, mixed by weight and gathered into drifts. Empty grows the
        /// material's one blade.
        /// </summary>
        public GrassVariantProfile Variants { get => _variants; set => _variants = value; }
        [Tooltip("Capacity of each flower-head queue, in millions. Heads beyond it are dropped and counted as overflow.")]
        [Range(0.001f, 4f), SerializeField, FormerlySerializedAs("maxHeadCount")] private float _maxHeadCount = 0.25f;
        /// <summary>
        /// Capacity of each flower-head queue, in millions. Heads beyond it are dropped and counted as overflow.
        /// </summary>
        public float MaxHeadCount { get => _maxHeadCount; set => _maxHeadCount = value; }

        [Header("Geometry LOD")]
        [Range(0, 8), SerializeField, FormerlySerializedAs("grassMeshSubdivision")] private int _grassMeshSubdivision = 5;

        /// <summary>Subdivisions of the near LOD blade mesh, 0 to 8; the middle LOD uses at most two.</summary>
        public int GrassMeshSubdivision { get => _grassMeshSubdivision; set => _grassMeshSubdivision = value; }
        [Min(0.01f), SerializeField, FormerlySerializedAs("nearLodDistance")] private float _nearLodDistance = 30f;
        /// <summary>Camera distance at which blades change from the near to the middle LOD, in metres.</summary>
        public float NearLodDistance { get => _nearLodDistance; set => _nearLodDistance = value; }
        [Tooltip("Start of the far, single-triangle LOD."), SerializeField, FormerlySerializedAs("subdivisionDistance")]
        private float _subdivisionDistance = 100f;
        /// <summary>Start of the far, single-triangle LOD, in metres.</summary>
        public float SubdivisionDistance { get => _subdivisionDistance; set => _subdivisionDistance = value; }
        [Min(0f), SerializeField, FormerlySerializedAs("lodTransitionWidth")] private float _lodTransitionWidth = 10f;
        /// <summary>Distance over which blades dither between neighbouring LODs, in metres.</summary>
        public float LodTransitionWidth { get => _lodTransitionWidth; set => _lodTransitionWidth = value; }
        [SerializeField, FormerlySerializedAs("subdivisionHeightBoost")] private float _subdivisionHeightBoost;
        /// <summary>Relative blade height added around <see cref="SubdivisionDistance"/>, from -0.95 to 10.</summary>
        public float SubdivisionHeightBoost { get => _subdivisionHeightBoost; set => _subdivisionHeightBoost = value; }
        [Min(0.01f), SerializeField, FormerlySerializedAs("subdivisionBumpWidth")] private float _subdivisionBumpWidth = 20f;
        /// <summary>
        /// Distance either side of <see cref="SubdivisionDistance"/> over which the height boost fades, in metres.
        /// </summary>
        public float SubdivisionBumpWidth { get => _subdivisionBumpWidth; set => _subdivisionBumpWidth = value; }
        [Tooltip("Conservative world-space radius around a root for frustum culling. Increase for tall/wide, strongly bent grass.")]
        [Min(0.1f), SerializeField, FormerlySerializedAs("cullingPadding")] private float _cullingPadding = 2f;
        /// <summary>
        /// Conservative world-space radius around a root for frustum culling. Increase for tall, wide or strongly bent
        /// grass.
        /// </summary>
        public float CullingPadding { get => _cullingPadding; set => _cullingPadding = value; }

        [Header("Data Capture")]
        [Tooltip("Power-of-two capture resolution. Higher values improve small boundaries at greater memory and capture cost."), SerializeField, FormerlySerializedAs("captureResolution")]
        private int _captureResolution = 1024;

        /// <summary>
        /// Power-of-two capture resolution, 128 to 2048. Higher values improve small boundaries at greater memory and
        /// capture cost.
        /// </summary>
        public int CaptureResolution { get => _captureResolution; set => _captureResolution = value; }
        [Min(0.1f), SerializeField, FormerlySerializedAs("textureUpdateThreshold")] private float _textureUpdateThreshold = 10f;
        /// <summary>
        /// The capture window snaps to this world grid, in metres, and extends this far beyond the draw distance.
        /// </summary>
        public float TextureUpdateThreshold { get => _textureUpdateThreshold; set => _textureUpdateThreshold = value; }
        [Tooltip("Stable world-space minimum/maximum Y used by the top-down mesh capture."), SerializeField, FormerlySerializedAs("captureHeightRange")]
        private Vector2 _captureHeightRange = new Vector2(-100f, 1000f);
        /// <summary>Stable world-space minimum and maximum Y used by the top-down mesh capture.</summary>
        public Vector2 CaptureHeightRange { get => _captureHeightRange; set => _captureHeightRange = value; }
        [SerializeField, FormerlySerializedAs("cacheSurfaceData")] private bool _cacheSurfaceData = true;
        /// <summary>
        /// Keeps the captured surface height between frames until the capture window, the scene inventory or a source
        /// changes.
        /// </summary>
        public bool CacheSurfaceData { get => _cacheSurfaceData; set => _cacheSurfaceData = value; }
        [Tooltip("Keep enabled for moving mask/color/slope modifiers. Static scenes can disable it and call RefreshGrassData after edits."), SerializeField, FormerlySerializedAs("updateModifiersEveryFrame")]
        private bool _updateModifiersEveryFrame = true;
        /// <summary>
        /// Recaptures mask, colour and interaction modifiers every frame. Static scenes can disable it and call
        /// <see cref="RefreshGrassData"/> after edits.
        /// </summary>
        public bool UpdateModifiersEveryFrame { get => _updateModifiersEveryFrame; set => _updateModifiersEveryFrame = value; }

        [Header("GPU Capacity")]
        [Tooltip("Total capacity across all three LOD queues, in millions. Overflow is dropped safely and exposed in diagnostics.")]
        [Range(0.01f, 16f), SerializeField, FormerlySerializedAs("maxBufferCount")] private float _maxBufferCount = 2f;

        /// <summary>
        /// Total capacity across all three LOD queues, in millions. Overflow is dropped safely and exposed in
        /// diagnostics.
        /// </summary>
        public float MaxBufferCount { get => _maxBufferCount; set => _maxBufferCount = value; }
        [SerializeField, FormerlySerializedAs("lodCapacityWeights")] private Vector3 _lodCapacityWeights = new Vector3(0.35f, 0.45f, 0.2f);
        /// <summary>Relative shares of <see cref="Capacity"/> given to the near, middle and far LOD queues.</summary>
        public Vector3 LodCapacityWeights { get => _lodCapacityWeights; set => _lodCapacityWeights = value; }

        [Header("Ground Blending")]
        [Range(0f, 1f), SerializeField, FormerlySerializedAs("groundBlendStrength")] private float _groundBlendStrength = 0.8f;

        /// <summary>How strongly the blade root blends toward the authored ground albedo.</summary>
        public float GroundBlendStrength { get => _groundBlendStrength; set => _groundBlendStrength = value; }
        [Tooltip("Fraction of the blade height that blends toward the authored ground albedo.")]
        [Range(0.001f, 1f), SerializeField, FormerlySerializedAs("groundBlendHeight")] private float _groundBlendHeight = 0.25f;
        /// <summary>Fraction of the blade height that blends toward the authored ground albedo.</summary>
        public float GroundBlendHeight { get => _groundBlendHeight; set => _groundBlendHeight = value; }

        [Header("Aliasing")]
        [Tooltip("Uses alpha-to-coverage only when the camera's actual color target is multisampled."), SerializeField, FormerlySerializedAs("alphaToCoverage")]
        private bool _alphaToCoverage = true;

        /// <summary>Uses alpha-to-coverage only when the camera's actual colour target is multisampled.</summary>
        public bool AlphaToCoverage { get => _alphaToCoverage; set => _alphaToCoverage = value; }
        [Range(0f, 3f), SerializeField, FormerlySerializedAs("minimumPixelWidth")] private float _minimumPixelWidth = 1f;
        /// <summary>Distant blades widen to at least this many pixels, 0 to 3.</summary>
        public float MinimumPixelWidth { get => _minimumPixelWidth; set => _minimumPixelWidth = value; }
        [SerializeField, FormerlySerializedAs("specularFadeRange")] private Vector2 _specularFadeRange = new Vector2(30f, 100f);
        /// <summary>Camera distances, in metres, over which the blades' specular highlight fades out.</summary>
        public Vector2 SpecularFadeRange { get => _specularFadeRange; set => _specularFadeRange = value; }
        [SerializeField, FormerlySerializedAs("motionVectors")] private GrassMotionVectors.Settings _motionVectors = new GrassMotionVectors.Settings();
        /// <summary>
        /// Optional grass motion vectors for temporal anti-aliasing, motion blur and custom consumers.
        /// </summary>
        public GrassMotionVectors.Settings MotionVectors { get => _motionVectors; set => _motionVectors = value; }

        [Header("Optional Contact Shadows"), SerializeField, FormerlySerializedAs("contactShadows")]
        private GrassContactShadows.Settings _contactShadows = new GrassContactShadows.Settings();

        /// <summary>Optional screen-space contact shadows involving the grass.</summary>
        public GrassContactShadows.Settings ContactShadows { get => _contactShadows; set => _contactShadows = value; }

        [Header("Diagnostics")]
        [Tooltip("Periodically reads counts asynchronously. Counts may lag behind the rendered frame."), SerializeField, FormerlySerializedAs("previewVisibleGrassCount")]
        private bool _previewVisibleGrassCount;

        /// <summary>
        /// Periodically reads counts asynchronously and shows them on screen. Counts may lag behind the rendered frame.
        /// </summary>
        public bool PreviewVisibleGrassCount { get => _previewVisibleGrassCount; set => _previewVisibleGrassCount = value; }

        /// <summary>The world bounds the feature last generated grass in for a camera; not serialized.</summary>
        public Bounds CameraBounds { get; set; }
        /// <summary>Blades drawn, as last read back while <see cref="PreviewVisibleGrassCount"/> is on.</summary>
        public uint VisibleGrassCount { get; internal set; }
        /// <summary>
        /// Blades dropped over capacity, as last read back while <see cref="PreviewVisibleGrassCount"/> is on.
        /// </summary>
        public uint OverflowGrassCount { get; internal set; }
        /// <summary>
        /// Advances on <see cref="RefreshGrassData"/>; the feature recaptures its cached maps when it changes.
        /// </summary>
        public uint Revision { get; private set; }
        /// <summary>Advances on <see cref="RefreshGrassInteraction"/>.</summary>
        public uint InteractionRevision { get; private set; }
        /// <summary>Total blade capacity across the three LOD queues, from <see cref="MaxBufferCount"/>.</summary>
        public int Capacity => Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp(Finite(_maxBufferCount, 2f), 0f, 16f) * 1000000f), 3, 16000000);
        /// <summary>Roots each head queue can hold: quad-cross heads, and the profile's mesh heads.</summary>
        public int HeadCapacity => Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp(Finite(_maxHeadCount, 0.25f), 0f, 4f) * 1000000f), 1, 4000000);

        /// <summary>The capacity of head queue 0 (quad-cross) or 1 (mesh) this owner needs: zero when no kind grows that head.</summary>
        /// <param name="queue">0 for the quad-cross queue, 1 for the mesh queue.</param>
        public int HeadQueueCapacity(int queue)
        {
            if (!_variants || _variants.KindCount < 1)
            {
                return 0;
            }

            bool mesh = queue == 1;
            if (mesh && !_variants.HeadMesh)
            {
                return 0;
            }

            return _variants.HasHeads(mesh ? GrassHeadShape.Mesh : GrassHeadShape.Daisy) ? HeadCapacity : 0;
        }
        /// <summary>
        /// Whether this is the enabled <see cref="Instance"/> in a loaded scene that is not a preview scene.
        /// </summary>
        public bool IsReadyForRendering => Instance == this && isActiveAndEnabled && IsSceneInstance();

        /// <summary>
        /// Whether a camera draws this grass: a game view, the scene view when <see cref="RenderInSceneView"/> is on, or a
        /// reflection camera <see cref="Reflections"/> admits, that sees the layer this owner is on. A utility camera that culls everything, such as a depth or ground capture that
        /// submits its own renders through the pipeline, then costs the grass no capture or generation pass.
        /// </summary>
        public bool DrawsFor(Camera camera)
        {
            if (camera == null || camera.stereoEnabled || (camera.cullingMask & (1 << gameObject.layer)) == 0)
            {
                return false;
            }

            return camera.cameraType == CameraType.Game || (camera.cameraType == CameraType.SceneView && _renderInSceneView) ||
                Reflections.Admits(camera);
        }

        /// <summary>
        /// <see cref="DrawsFor"/> at the feature's admission points. A refused camera also withdraws the interaction field the
        /// last admitted camera published, so effects reading it on the refused camera (ground fog) see no field rather than
        /// that camera's contacts.
        /// </summary>
        /// <param name="camera">The camera about to render.</param>
        /// <returns>Whether the camera draws this grass.</returns>
        public bool AdmitCamera(Camera camera)
        {
            if (DrawsFor(camera))
            {
                return true;
            }

            GrassInteractionPublisher.Withdraw();
            return false;
        }

        private Mesh[] _meshes;
        private Mesh _headMesh;
        private int _cachedSubdivision = -1;
        private GUIStyle _diagnosticStyle;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance = null;
        }

        private void OnEnable()
        {
            // Scene objects receive OnEnable before an additive scene reports
            // isLoaded. Register now; rendering starts after scene activation.
            if (!IsSceneInstance(false))
            {
                return;
            }
            // Moving the current owner into a preview scene need not invoke
            // OnDisable. A replacement can enable before that owner's next Update.
            if (Instance && Instance != this && !Instance.IsSceneInstance(false))
            {
                Instance.ReleaseMeshes();
                Instance = null;
            }
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("Only one enabled Infinite Grass Renderer can own the scene settings.", this);
                enabled = false;
                return;
            }

            Instance = this;
            ValidateSettings();
            RefreshGrassData();
        }

        private void OnValidate()
        {
            ValidateSettings();
            RefreshGrassData();
        }

        private void OnDisable()
        {
            if (Instance == this)
            {
                Instance = null;
            }

            ReleaseMeshes();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }

            ReleaseMeshes();
        }

        // Scene moves can change eligibility without an OnDisable callback. Also
        // restore ownership when Enter Play Mode skips scene and domain reload.
        private void Update()
        {
            bool sceneEligible = isActiveAndEnabled && IsSceneInstance();
            if (Instance == this && !sceneEligible)
            {
                Instance = null;
                ReleaseMeshes();
            }
            if (Instance == null && sceneEligible)
            {
                Instance = this;
                ValidateSettings();
                RefreshGrassData();
            }
        }

        private bool IsSceneInstance(bool requireLoaded = true)
        {
            if (!gameObject.scene.IsValid() || (requireLoaded && !gameObject.scene.isLoaded))
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

        /// <summary>
        /// One-sided blade area per square metre of ground at full density, as the blade shader's
        /// GrassCanopyAreaIndex computes it: blades per square metre at this spacing times the mean
        /// blade's triangle of physical base width and height. Ground shaders can darken under the
        /// grass with the same canopy.
        /// </summary>
        public float CanopyAreaIndex()
        {
            if (!_grassMaterial || !_grassMaterial.HasProperty("_GrassWidth") || !_grassMaterial.HasProperty("_GrassHeight"))
            {
                return 0f;
            }

            float width = 0.5f * Mathf.Max(_grassMaterial.GetFloat("_GrassWidth"), 0f) *
                (1f - 0.5f * Mathf.Clamp01(_grassMaterial.GetFloat("_GrassWidthRandomness")));
            float height = Mathf.Max(_grassMaterial.GetFloat("_GrassHeight"), 0f) *
                (1f - 0.5f * Mathf.Clamp01(_grassMaterial.GetFloat("_GrassHeightRandomness")));
            float cell = Mathf.Max(_spacing, 0.001f);
            float mix = _variants && _variants.KindCount > 0 ? _variants.MeanBladeAreaScale() : 1f;
            return 0.5f * width * height * mix / (cell * cell);
        }

        /// <summary>
        /// Requests a full recapture of the cached grass data, for example after editing a static scene.
        /// </summary>
        [ContextMenu("Refresh Grass Data")]
        public void RefreshGrassData()
        {
            unchecked { Revision++; }
        }

        /// <summary>Same as <see cref="RefreshGrassData"/>.</summary>
        public void MarkDirty()
        {
            RefreshGrassData();
        }

        /// <summary>Refresh only interaction capture, including with a stationary camera.</summary>
        public void RefreshGrassInteraction()
        {
            unchecked { InteractionRevision++; }
        }

        /// <summary>
        /// Splits <see cref="Capacity"/> into the three LOD queues by <see cref="LodCapacityWeights"/>; entry 3 of each
        /// array is set to zero.
        /// </summary>
        /// <param name="capacities">Receives each LOD queue's capacity; at least four entries.</param>
        /// <param name="offsets">Receives each LOD queue's first index in the shared buffer; at least four entries.</param>
        /// <exception cref="ArgumentException">An array is null or has fewer than four entries.</exception>
        public void GetLodCapacity(int[] capacities, int[] offsets)
        {
            if (capacities == null || capacities.Length < 4 || offsets == null || offsets.Length < 4)
            {
                throw new ArgumentException("LOD capacity and offset arrays must contain at least four entries.");
            }

            // Public fields can change after OnValidate/OnEnable. Float arithmetic
            // can overflow even when every supplied weight is finite. Widen before
            // adding or multiplying, then convert only the bounded final shares.
            double near = Math.Max(0.01f, Finite(_lodCapacityWeights.x, 0.35f));
            double middle = Math.Max(0.01f, Finite(_lodCapacityWeights.y, 0.45f));
            double far = Math.Max(0.01f, Finite(_lodCapacityWeights.z, 0.2f));
            double total = near + middle + far;
            int capacity = Capacity;
            capacities[0] = Mathf.Clamp(Mathf.FloorToInt((float)(capacity * near / total)), 1, capacity - 2);
            capacities[1] = Mathf.Clamp(Mathf.FloorToInt((float)(capacity * middle / total)), 1, capacity - capacities[0] - 1);
            capacities[2] = capacity - capacities[0] - capacities[1];
            capacities[3] = 0;
            offsets[0] = 0;
            offsets[1] = capacities[0];
            offsets[2] = capacities[0] + capacities[1];
            offsets[3] = 0;
        }

        /// <summary>Compatibility accessor for the highest detail mesh.</summary>
        public Mesh GetGrassMeshCache()
        {
            return GetLodMeshes()[0];
        }

        /// <summary>
        /// The near, middle and far blade meshes, rebuilt when <see cref="GrassMeshSubdivision"/> changes. The renderer
        /// owns them; callers must not destroy them.
        /// </summary>
        public Mesh[] GetLodMeshes()
        {
            int subdivisions = Mathf.Clamp(_grassMeshSubdivision, 0, 8);
            if (_meshes != null && _cachedSubdivision == subdivisions && _meshes[0] && _meshes[1] && _meshes[2])
            {
                return _meshes;
            }

            ReleaseMeshes();
            _meshes = new[]
            {
                CreateBladeMesh(subdivisions),
                CreateBladeMesh(Mathf.Min(subdivisions, 2)),
                CreateBladeMesh(0)
            };
            _cachedSubdivision = subdivisions;
            return _meshes;
        }

        /// <summary>Shares row vertices, including a two-vertex tip for opaque footprint caps.
        /// The shader collapses that tip for pointed/A2C blades; UV.y remains true normalized height.</summary>
        /// <param name="subdivisions">Row subdivisions, clamped to 0 to 8.</param>
        /// <returns>A new mesh the caller owns.</returns>
        public static Mesh CreateBladeMesh(int subdivisions)
        {
            subdivisions = Mathf.Clamp(subdivisions, 0, 8);
            int rows = subdivisions + 1;
            var vertices = new Vector3[rows * 2 + 2];
            var uv = new Vector2[vertices.Length];
            var indices = new int[(subdivisions + 1) * 6];

            for (int row = 0; row < rows; row++)
            {
                float height = row / (float)rows;
                vertices[row * 2] = new Vector3(-0.25f, height, 0f);
                vertices[row * 2 + 1] = new Vector3(0.25f, height, 0f);
                uv[row * 2] = new Vector2(0f, height);
                uv[row * 2 + 1] = new Vector2(1f, height);
            }

            for (int row = 0; row < subdivisions; row++)
            {
                int lowerLeft = row * 2;
                int upperLeft = lowerLeft + 2;
                int index = row * 6;
                indices[index] = lowerLeft;
                indices[index + 1] = upperLeft + 1;
                indices[index + 2] = lowerLeft + 1;
                indices[index + 3] = lowerLeft;
                indices[index + 4] = upperLeft;
                indices[index + 5] = upperLeft + 1;
            }

            int tip = rows * 2;
            vertices[tip] = new Vector3(-0.25f, 1f, 0f);
            vertices[tip + 1] = new Vector3(0.25f, 1f, 0f);
            uv[tip] = new Vector2(0f, 1f);
            uv[tip + 1] = new Vector2(1f, 1f);
            int last = indices.Length - 6;
            indices[last] = (rows - 1) * 2;
            indices[last + 1] = tip + 1;
            indices[last + 2] = (rows - 1) * 2 + 1;
            indices[last + 3] = (rows - 1) * 2;
            indices[last + 4] = tip;
            indices[last + 5] = tip + 1;

            var mesh = new Mesh
            {
                name = "Grass blade (" + subdivisions + " subdivisions)",
                hideFlags = HideFlags.HideAndDontSave,
                vertices = vertices,
                uv = uv,
                triangles = indices
            };
            mesh.RecalculateBounds();
            mesh.UploadMeshData(false);
            return mesh;
        }

        /// <summary>The quad-cross every procedural head draws: one flat face and two crossed upright quads, one unit across.</summary>
        public Mesh GetHeadMesh()
        {
            if (!_headMesh)
            {
                _headMesh = CreateHeadMesh();
            }

            return _headMesh;
        }

        /// <summary>
        /// A flat face in XZ at the origin (uv2.x = 0) and two upright quads crossed on Y (uv2.x = 1), each spanning -0.5 to
        /// 0.5. The head shader raises and scales the upright quads per head shape and cuts every outline procedurally.
        /// </summary>
        /// <returns>A new mesh the caller owns.</returns>
        public static Mesh CreateHeadMesh()
        {
            var vertices = new Vector3[12];
            var normals = new Vector3[12];
            var uv = new Vector2[12];
            var part = new Vector2[12];
            var indices = new int[18];
            for (int quad = 0; quad < 3; quad++)
            {
                for (int corner = 0; corner < 4; corner++)
                {
                    int index = quad * 4 + corner;
                    float u = corner & 1, v = corner >> 1;
                    float a = u - 0.5f, b = v - 0.5f;
                    vertices[index] = quad == 0 ? new Vector3(a, 0f, b) : quad == 1 ? new Vector3(a, b, 0f) : new Vector3(0f, b, a);
                    normals[index] = quad == 0 ? Vector3.up : quad == 1 ? Vector3.forward : Vector3.right;
                    uv[index] = new Vector2(u, v);
                    part[index] = new Vector2(quad == 0 ? 0f : 1f, 0f);
                }
                int first = quad * 4;
                int i = quad * 6;
                indices[i] = first;
                indices[i + 1] = first + 2;
                indices[i + 2] = first + 1;
                indices[i + 3] = first + 1;
                indices[i + 4] = first + 2;
                indices[i + 5] = first + 3;
            }
            var mesh = new Mesh
            {
                name = "Grass head quad-cross",
                hideFlags = HideFlags.HideAndDontSave,
                vertices = vertices,
                normals = normals,
                uv = uv,
                triangles = indices
            };
            mesh.SetUVs(1, part);
            mesh.RecalculateBounds();
            mesh.UploadMeshData(false);
            return mesh;
        }

        private void ReleaseMeshes()
        {
            if (_meshes != null)
            {
                foreach (Mesh mesh in _meshes)
                {
                    CoreUtils.Destroy(mesh);
                }

                _meshes = null;
            }
            CoreUtils.Destroy(_headMesh);
            _headMesh = null;
            _cachedSubdivision = -1;
        }

        private void ValidateSettings()
        {
            _spacing = Mathf.Clamp(Finite(_spacing, 0.1f), 0.01f, 10f);
            _drawDistance = Mathf.Clamp(Finite(_drawDistance, 300f), 1f, 10000f);
            _fullDensityDistance = Mathf.Clamp(Finite(_fullDensityDistance, 30f), 0f, _drawDistance - 0.001f);
            _densityFalloffExponent = Mathf.Clamp(Finite(_densityFalloffExponent, 4f), 0.1f, 16f);
            _densityTransition = Mathf.Clamp(Finite(_densityTransition, 0.05f), 0f, 0.25f);
            _grassMeshSubdivision = Mathf.Clamp(_grassMeshSubdivision, 0, 8);
            _nearLodDistance = Mathf.Clamp(Finite(_nearLodDistance, 30f), 0.01f, _drawDistance);
            _subdivisionDistance = Mathf.Clamp(Finite(_subdivisionDistance, 100f), _nearLodDistance, _drawDistance);
            _lodTransitionWidth = Mathf.Clamp(Finite(_lodTransitionWidth, 10f), 0f, _subdivisionDistance);
            _subdivisionHeightBoost = Mathf.Clamp(Finite(_subdivisionHeightBoost, 0f), -0.95f, 10f);
            _subdivisionBumpWidth = Mathf.Max(0.01f, Finite(_subdivisionBumpWidth, 20f));
            _cullingPadding = Mathf.Max(0.1f, Finite(_cullingPadding, 2f));
            _textureUpdateThreshold = Mathf.Clamp(Finite(_textureUpdateThreshold, 10f), 0.1f, _drawDistance);
            _captureResolution = Mathf.ClosestPowerOfTwo(Mathf.Clamp(_captureResolution, 128, 2048));
            _captureHeightRange.x = Finite(_captureHeightRange.x, -100f);
            _captureHeightRange.y = Mathf.Max(_captureHeightRange.x + 1f, Finite(_captureHeightRange.y, 1000f));
            _maxBufferCount = Mathf.Clamp(Finite(_maxBufferCount, 2f), 0.01f, 16f);
            _maxHeadCount = Mathf.Clamp(Finite(_maxHeadCount, 0.25f), 0.001f, 4f);
            _lodCapacityWeights.x = Mathf.Clamp(Finite(_lodCapacityWeights.x, 0.35f), 0.01f, 100f);
            _lodCapacityWeights.y = Mathf.Clamp(Finite(_lodCapacityWeights.y, 0.45f), 0.01f, 100f);
            _lodCapacityWeights.z = Mathf.Clamp(Finite(_lodCapacityWeights.z, 0.2f), 0.01f, 100f);
            _groundBlendStrength = Mathf.Clamp01(Finite(_groundBlendStrength, 0.8f));
            _groundBlendHeight = Mathf.Clamp(Finite(_groundBlendHeight, 0.25f), 0.001f, 1f);
            _minimumPixelWidth = Mathf.Clamp(Finite(_minimumPixelWidth, 1f), 0f, 3f);
            _specularFadeRange.x = Mathf.Max(0f, Finite(_specularFadeRange.x, 30f));
            _specularFadeRange.y = Mathf.Max(_specularFadeRange.x + 0.01f, Finite(_specularFadeRange.y, 100f));
            if (_motionVectors == null)
            {
                _motionVectors = new GrassMotionVectors.Settings();
            }

            if (_contactShadows == null)
            {
                _contactShadows = new GrassContactShadows.Settings();
            }
        }

        private static float Finite(float value, float fallback)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;
        }

        private void OnGUI()
        {
            if (!_previewVisibleGrassCount || Instance != this)
            {
                return;
            }

            if (_diagnosticStyle == null)
            {
                _diagnosticStyle = new GUIStyle(GUI.skin.label) { fontSize = 18 };
            }

            GUI.Label(new Rect(24f, 24f, 600f, 60f),
                "Grass: " + VisibleGrassCount.ToString("N0") + " drawn; " +
                OverflowGrassCount.ToString("N0") + " over capacity (async)", _diagnosticStyle);
        }
    }
}
