
#if UNITY_EDITOR
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.Serialization;
#endif

namespace Cone.Grass.MicroVerse
{
    /// <summary>
    /// Editor authoring bridge for saved MicroVerse MaskTarget texture subassets.
    /// The resolved inputs live on GrassPlacementArea. This component does no player work.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(GrassPlacementArea))]
    [AddComponentMenu("Infinite Grass/MicroVerse Mask Bridge")]
    public sealed class GrassMicroVerseBridge : MonoBehaviour
    {
#if UNITY_EDITOR
        // Keep the producer asset reference out of the player. Runtime rendering only
        // needs the Texture2D reference serialized on the adjacent placement area.
        [SerializeField, FormerlySerializedAs("maskTarget")] private ScriptableObject _maskTarget;
        [SerializeField, FormerlySerializedAs("terrain")] private Terrain _terrain;
        [SerializeField, FormerlySerializedAs("textureSubAssetName")] private string _textureSubAssetName;
        [SerializeField, FormerlySerializedAs("groundLayer")] private TerrainLayer _groundLayer;
        [SerializeField, FormerlySerializedAs("groundColorOverride")] private Texture2D _groundColorOverride;
        [SerializeField, FormerlySerializedAs("bakeTerrainGroundColor")] private bool _bakeTerrainGroundColor;
        [SerializeField, Range(64, 4096), FormerlySerializedAs("groundBakeResolution")] private int _groundBakeResolution = 1024;
        [SerializeField, FormerlySerializedAs("groundBakeAssetPath")] private string _groundBakeAssetPath;
        [SerializeField, HideInInspector, FormerlySerializedAs("bakedGroundColor")] private Texture2D _bakedGroundColor;
        [SerializeField, HideInInspector, FormerlySerializedAs("groundBakeSourceKey")] private string _groundBakeSourceKey;
        [SerializeField, HideInInspector, FormerlySerializedAs("groundBakeOutputKey")] private string _groundBakeOutputKey;
        [SerializeField, FormerlySerializedAs("groundTint")] private Color _groundTint = Color.white;
        [SerializeField, Range(0f, 1f), FormerlySerializedAs("groundColorStrength")] private float _groundColorStrength = 1f;
        [SerializeField, FormerlySerializedAs("autoRefresh")] private bool _autoRefresh = true;
        [SerializeField, FormerlySerializedAs("pollWhileEditing")] private bool _pollWhileEditing = true;
        [SerializeField, Range(0.25f, 10f), FormerlySerializedAs("pollInterval")] private float _pollInterval = 1f;

        private static readonly HashSet<GrassMicroVerseBridge> s_activeBridges =
            new HashSet<GrassMicroVerseBridge>();

        [System.NonSerialized] private int _refreshRequested = 1;
        [System.NonSerialized] private GrassPlacementArea _placementArea;

        public static IReadOnlyCollection<GrassMicroVerseBridge> ActiveBridges => s_activeBridges;
        public ScriptableObject MaskTarget => _maskTarget;
        public Terrain Terrain => _terrain;
        public string TextureSubAssetName => _textureSubAssetName;
        public TerrainLayer GroundLayer => _groundLayer;
        public Texture2D GroundColorOverride => _groundColorOverride;
        public bool BakeTerrainGroundColor => _bakeTerrainGroundColor;
        public int GroundBakeResolution => Mathf.Clamp(_groundBakeResolution, 64, 4096);
        public string GroundBakeAssetPath => _groundBakeAssetPath;
        public Texture2D BakedGroundColor => _bakedGroundColor;
        public string GroundBakeSourceKey => _groundBakeSourceKey;
        public string GroundBakeOutputKey => _groundBakeOutputKey;
        public Color GroundTint => _groundTint;
        public float GroundColorStrength => _groundColorStrength;
        public bool AutoRefresh => _autoRefresh;
        public bool PollWhileEditing => _pollWhileEditing;
        public float PollInterval => Mathf.Clamp(_pollInterval, 0.25f, 10f);
        public GrassPlacementArea PlacementArea => _placementArea
            ? _placementArea
            : _placementArea = GetComponent<GrassPlacementArea>();

        public bool LastRefreshSucceeded { get; private set; }
        public string LastRefreshMessage { get; private set; }

        private void Reset()
        {
            _terrain = GetComponentInParent<Terrain>();
            // An unconfigured adapter must produce zero grass, including before the
            // first delayed editor refresh or while its mask is being regenerated.
            PlacementArea.ConfigureTexture(_terrain, null);
            Interlocked.Exchange(ref _refreshRequested, 1);
        }

        private void OnEnable()
        {
            // Build preflight reads saved scenes in a preview scene. Those objects
            // must not enter the live polling registry or modify the open scene.
            // OnEnable can run while a normal scene is still loading. Registration
            // must not depend on isLoaded; the watcher waits until loading completes.
            if (gameObject.scene.IsValid() &&
                !UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(gameObject.scene))
            {
                s_activeBridges.Add(this);
            }

            Interlocked.Exchange(ref _refreshRequested, 1);
        }

        private void OnDisable()
        {
            s_activeBridges.Remove(this);
        }

        private void OnValidate()
        {
            // Unity may invoke validation from a loading thread. AssetDatabase and
            // rendering calls are deferred to the editor update by this flag.
            Interlocked.Exchange(ref _refreshRequested, 1);
        }

        public bool ConsumeRefreshRequest()
        {
            return Interlocked.Exchange(ref _refreshRequested, 0) != 0;
        }

        public void SetRefreshResult(bool succeeded, string message)
        {
            LastRefreshSucceeded = succeeded;
            LastRefreshMessage = message;
        }

        public void SetBakedGroundColor(Texture2D texture, string assetPath, string sourceKey, string outputKey)
        {
            _bakedGroundColor = texture;
            _groundBakeAssetPath = assetPath;
            _groundBakeSourceKey = sourceKey;
            _groundBakeOutputKey = outputKey;
        }
#endif
    }
}
