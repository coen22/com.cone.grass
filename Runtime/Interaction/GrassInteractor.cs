using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Read-only body/support queries producing one bounded GrassSlope capture mesh.</summary>
[DisallowMultipleComponent, AddComponentMenu("Cone/Grass/Collider Interactor")]
[DefaultExecutionOrder(10000)]
public sealed class GrassInteractor : MonoBehaviour
{
    public Collider body;
    public Shader interactionShader;
    public LayerMask groundLayers = ~0;
    [Min(.01f)] public float radiusPadding = .15f;
    [Min(.01f)] public float bladeHeight = .5f;
    [Range(0f, 1f)] public float strength = .9f;
    [Min(.001f)] public float attackSeconds = .06f;
    [Min(.001f)] public float recoverySeconds = .55f;

    const int Capacity = 64;
    readonly RaycastHit[] hits = new RaycastHit[32];
    readonly Stamp[] stamps = new Stamp[Capacity];
    readonly Vector3[] vertices = new Vector3[(Capacity + 1) * 4];
    readonly Vector2[] uv = new Vector2[(Capacity + 1) * 4];
    readonly Vector4[] tangents = new Vector4[(Capacity + 1) * 4];
    readonly Color[] colors = new Color[(Capacity + 1) * 4];
    readonly int[] triangles = new int[(Capacity + 1) * 6];
    Mesh mesh;
    Material material;
    GameObject capture;
    MeshRenderer captureRenderer;
    Collider previousSupport;
    Vector3 previousLocal;
    Vector3 sampledLocal, lastDirection;
    Vector3 previousBodyCenter;
    Collider sampledSupport;
    double previousTime;
    float response;
    int count;
    bool observed;

    public int RetainedStampCount => count;
    public float ContactStrength => response;
    public Renderer CaptureRenderer => captureRenderer;

    struct Stamp
    {
        public Collider Support;
        public Vector3 LocalPosition, Direction;
        public float Radius, Strength;
        public double Time;
    }

    void OnEnable()
    {
        if (!body) body = GetComponent<Collider>();
        ResetInteraction();
    }

    void LateUpdate() => Sample(Time.timeAsDouble);

    /// <summary>Samples actual collider geometry. Does not advance physics, the player, or the camera.</summary>
    public void Sample(double now)
    {
        if (!isActiveAndEnabled || double.IsNaN(now) || double.IsInfinity(now)) return;
        float dt = observed ? (float)(now - previousTime) : 0f;
        if (dt < 0f || dt > .25f) { ResetInteraction(); dt = 0f; }
        if (observed && body && (body.bounds.center - previousBodyCenter).sqrMagnitude > 16f)
        { ResetInteraction(); dt = 0f; }
        if (!EnsureCapture()) return;
        bool contact = TrySupport(out Collider support, out Vector3 center, out float radius, out float target);
        float departedStrength = response;
        Vector3 departedDirection = lastDirection;
        response = GrassInteractionMath.Response(response, contact ? target : 0f,
            Mathf.Max(0f, dt), contact ? attackSeconds : recoverySeconds);
        if (contact && observed && previousSupport == support)
        {
            Vector3 previous = support.transform.TransformPoint(previousLocal);
            Vector3 travel = center - previous;
            travel.y = 0f;
            int samples = GrassInteractionMath.SegmentSamples(travel.magnitude, radius);
            if (samples == 0)
            {
                ResetInteraction();
                dt = 0f;
            }
            else if (travel.sqrMagnitude > .000001f)
            {
                lastDirection = travel.normalized;
                Vector3 anchor = sampledSupport == support ? support.transform.TransformPoint(sampledLocal) : previous;
                Vector3 segment = center - anchor; segment.y = 0f;
                float spacing = radius * .4f;
                int steps = Mathf.Min(32, Mathf.FloorToInt(segment.magnitude / spacing));
                for (int i = 1; i <= steps; i++)
                {
                    Vector3 position = anchor + segment.normalized * (i * spacing);
                    AddStamp(support, position, lastDirection, radius, response, now);
                    sampledLocal = support.transform.InverseTransformPoint(position);
                }
            }
        }
        if (contact && sampledSupport != support)
        {
            sampledSupport = support; sampledLocal = support.transform.InverseTransformPoint(center); lastDirection = Vector3.zero;
        }
        if (previousSupport && (!contact || previousSupport != support) && departedStrength > .005f)
            AddStamp(previousSupport, previousSupport.transform.TransformPoint(previousLocal), departedDirection,
                Mathf.Max(.05f, radiusPadding + Mathf.Max(body ? body.bounds.extents.x : .1f, body ? body.bounds.extents.z : .1f)),
                departedStrength, previousTime);
        if (!contact)
        {
            // A jump breaks the grounded path even when it lands on the same collider.
            // Keep recovering stamps, but anchor the next grounded segment at its landing.
            sampledSupport = null; sampledLocal = Vector3.zero;
        }
        // First contact ramps in using observed time; no camera movement or texture recenter is required.
        int quads = 0;
        if (contact && response > .005f)
            WriteQuad(quads++, center, radius, response, lastDirection);
        int retained = 0;
        for (int i = 0; i < count; i++)
        {
            Stamp stamp = stamps[i];
            float remaining = GrassInteractionMath.Recovery(stamp.Strength, (float)(now - stamp.Time), recoverySeconds);
            if (!stamp.Support || !stamp.Support.enabled || !stamp.Support.gameObject.activeInHierarchy || remaining <= .005f)
                continue;
            stamps[retained++] = stamp;
            WriteQuad(quads++, stamp.Support.transform.TransformPoint(stamp.LocalPosition), stamp.Radius,
                remaining, stamp.Support.transform.TransformDirection(stamp.Direction));
        }
        System.Array.Clear(stamps, retained, count - retained);
        count = retained;
        // Degenerate unused triangles ensure old mesh contents never reach the next capture.
        for (int i = quads * 6; i < triangles.Length; i++) triangles[i] = 0;
        System.Array.Clear(vertices, quads * 4, vertices.Length - quads * 4);
        System.Array.Clear(colors, quads * 4, colors.Length - quads * 4);
        mesh.vertices = vertices; mesh.uv = uv; mesh.colors = colors; mesh.tangents = tangents;
        mesh.triangles = triangles; mesh.RecalculateBounds();
        captureRenderer.enabled = quads > 0;
        InfiniteGrassRenderer.Instance?.RefreshGrassInteraction();
        previousSupport = contact ? support : null;
        previousLocal = contact ? support.transform.InverseTransformPoint(center) : Vector3.zero;
        previousTime = now; observed = true;
        previousBodyCenter = body ? body.bounds.center : transform.position;
    }

    bool TrySupport(out Collider support, out Vector3 center, out float radius, out float target)
    {
        support = null; center = Vector3.zero; radius = 0f; target = 0f;
        if (!body || !body.enabled || !body.gameObject.activeInHierarchy) return false;
        Bounds bounds = body.bounds;
        float height = Mathf.Max(.01f, bladeHeight);
        if (!GrassInteractionMath.Finite(bounds.center) || !GrassInteractionMath.Finite(bounds.extents) ||
            !GrassInteractionMath.Finite(height)) return false;
        // Starting near the capsule centre avoids losing contact when the body
        // bottom is a few centimetres inside its actual supporting surface.
        Vector3 origin = bounds.center + Vector3.up * .05f;
        int length = Physics.RaycastNonAlloc(origin, Vector3.down, hits, bounds.extents.y + height + .1f, groundLayers, QueryTriggerInteraction.Ignore);
        if (length == hits.Length) return false; // Ambiguous truncated query never guesses a support.
        float nearest = float.PositiveInfinity;
        for (int i = 0; i < length; i++)
        {
            RaycastHit hit = hits[i];
            if (!hit.collider || hit.collider == body || hit.collider.transform.IsChildOf(transform) ||
                hit.normal.y < .35f || hit.distance >= nearest) continue;
            nearest = hit.distance; support = hit.collider; center = hit.point;
        }
        if (!support) return false;
        radius = Mathf.Clamp(Mathf.Max(bounds.extents.x, bounds.extents.z) + Mathf.Max(0f, radiusPadding), .05f, 4f);
        target = Mathf.Clamp01(strength) * GrassInteractionMath.HeightContact(bounds.min.y, center.y, height);
        return target > 0f && GrassInteractionMath.Finite(radius) && GrassInteractionMath.Finite(target);
    }

    void AddStamp(Collider support, Vector3 position, Vector3 direction, float radius, float value, double now)
    {
        if (count == Capacity)
        {
            System.Array.Copy(stamps, 1, stamps, 0, Capacity - 1);
            count--;
        }
        stamps[count++] = new Stamp { Support = support, LocalPosition = support.transform.InverseTransformPoint(position),
            Direction = support.transform.InverseTransformDirection(direction), Radius = radius, Strength = value, Time = now };
    }

    void WriteQuad(int index, Vector3 center, float radius, float value, Vector3 direction)
    {
        int v = index * 4, t = index * 6;
        for (int i = 0; i < 4; i++)
        {
            float x = (i == 1 || i == 2) ? 1f : -1f, z = i >= 2 ? 1f : -1f;
            vertices[v + i] = transform.InverseTransformPoint(center + new Vector3(x * radius, .02f, z * radius));
            uv[v + i] = new Vector2((x + 1f) * .5f, (z + 1f) * .5f);
            colors[v + i] = new Color(1f, 1f, 1f, Mathf.Clamp01(value));
            Vector3 localDirection = transform.InverseTransformDirection(direction);
            tangents[v + i] = new Vector4(localDirection.x, localDirection.y, localDirection.z, 1f);
        }
        triangles[t] = v; triangles[t + 1] = v + 2; triangles[t + 2] = v + 1;
        triangles[t + 3] = v; triangles[t + 4] = v + 3; triangles[t + 5] = v + 2;
    }

    bool EnsureCapture()
    {
        if (capture && mesh && material) return true;
        Shader shader = interactionShader ? interactionShader : Shader.Find("InfiniteGrass/Modifiers/GrassInteractor");
        if (!shader) return false;
        material = new Material(shader) { name = "Grass collider interaction", hideFlags = HideFlags.HideAndDontSave };
        mesh = new Mesh { name = "Grass collider interaction", hideFlags = HideFlags.HideAndDontSave };
        mesh.MarkDynamic();
        capture = new GameObject("Grass collider capture") { hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild };
        // A scene-owned child gives Play exit a destruction path even without domain reload.
        capture.transform.SetParent(transform, false);
        capture.AddComponent<MeshFilter>().sharedMesh = mesh;
        captureRenderer = capture.AddComponent<MeshRenderer>(); captureRenderer.sharedMaterial = material;
        captureRenderer.shadowCastingMode = ShadowCastingMode.Off; captureRenderer.receiveShadows = false;
        captureRenderer.enabled = false;
        InfiniteGrassRenderer.Instance?.RefreshGrassData();
        return true;
    }

    public void ResetInteraction()
    {
        System.Array.Clear(stamps, 0, stamps.Length);
        count = 0; response = 0f; observed = false; previousSupport = sampledSupport = null; lastDirection = Vector3.zero;
        if (captureRenderer) captureRenderer.enabled = false;
        InfiniteGrassRenderer.Instance?.RefreshGrassInteraction();
    }

    void OnDisable() => Release();
    void OnDestroy() => Release();
    void Release()
    {
        ResetInteraction();
        CoreUtils.Destroy(capture); CoreUtils.Destroy(mesh); CoreUtils.Destroy(material);
        capture = null; captureRenderer = null; mesh = null; material = null;
        InfiniteGrassRenderer.Instance?.RefreshGrassData();
    }
}
