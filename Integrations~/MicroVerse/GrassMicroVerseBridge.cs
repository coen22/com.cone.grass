using UnityEngine;

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Threading;
#endif

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
    [SerializeField] private ScriptableObject maskTarget;
    [SerializeField] private Terrain terrain;
    [SerializeField] private string textureSubAssetName;
    [SerializeField] private TerrainLayer groundLayer;
    [SerializeField] private Texture2D groundColorOverride;
    [SerializeField] private Color groundTint = Color.white;
    [SerializeField, Range(0f, 1f)] private float groundColorStrength = 1f;
    [SerializeField] private bool autoRefresh = true;
    [SerializeField] private bool pollWhileEditing = true;
    [SerializeField, Range(0.25f, 10f)] private float pollInterval = 1f;

    private static readonly HashSet<GrassMicroVerseBridge> activeBridges =
        new HashSet<GrassMicroVerseBridge>();

    [System.NonSerialized] private int refreshRequested = 1;
    [System.NonSerialized] private GrassPlacementArea placementArea;

    public static IReadOnlyCollection<GrassMicroVerseBridge> ActiveBridges => activeBridges;
    public ScriptableObject MaskTarget => maskTarget;
    public Terrain Terrain => terrain;
    public string TextureSubAssetName => textureSubAssetName;
    public TerrainLayer GroundLayer => groundLayer;
    public Texture2D GroundColorOverride => groundColorOverride;
    public Color GroundTint => groundTint;
    public float GroundColorStrength => groundColorStrength;
    public bool AutoRefresh => autoRefresh;
    public bool PollWhileEditing => pollWhileEditing;
    public float PollInterval => Mathf.Clamp(pollInterval, 0.25f, 10f);
    public GrassPlacementArea PlacementArea => placementArea
        ? placementArea
        : placementArea = GetComponent<GrassPlacementArea>();

    public bool LastRefreshSucceeded { get; private set; }
    public string LastRefreshMessage { get; private set; }

    private void Reset()
    {
        terrain = GetComponentInParent<Terrain>();
        // An unconfigured adapter must produce zero grass, including before the
        // first delayed editor refresh or while its mask is being regenerated.
        PlacementArea.ConfigureTexture(terrain, null);
        Interlocked.Exchange(ref refreshRequested, 1);
    }

    private void OnEnable()
    {
        activeBridges.Add(this);
        Interlocked.Exchange(ref refreshRequested, 1);
    }

    private void OnDisable()
    {
        activeBridges.Remove(this);
    }

    private void OnValidate()
    {
        // Unity may invoke validation from a loading thread. AssetDatabase and
        // rendering calls are deferred to the editor update by this flag.
        Interlocked.Exchange(ref refreshRequested, 1);
    }

    public bool ConsumeRefreshRequest()
    {
        return Interlocked.Exchange(ref refreshRequested, 0) != 0;
    }

    public void SetRefreshResult(bool succeeded, string message)
    {
        LastRefreshSucceeded = succeeded;
        LastRefreshMessage = message;
    }
#endif
}
