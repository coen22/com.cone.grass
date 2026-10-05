using System;
using UnityEngine;
using UnityEngine.SceneManagement;

public enum GrassInteractorSupportStatus
{
    Supported,
    MissingActor,
    InactiveActor,
    InvalidActorRoot,
    UnsupportedCollider,
    UnsupportedTransform,
    InvalidShape,
    InvalidPhysicsScene,
    InvalidSettings,
    InvalidSupport,
    MissingSupport,
    Ungrounded,
    InitialOverlap,
    AmbiguousSupport,
    QueryOverflow
}

/// <summary>The actual circular footprint of an upright capsule, controller or sphere.</summary>
public readonly struct GrassInteractorShape
{
    public readonly Collider Actor;
    public readonly Transform ActorRoot;
    public readonly PhysicsScene PhysicsScene;
    public readonly Vector3 Center;
    public readonly float Radius;
    public readonly float Height;
    public Vector3 LowerSphereCenter => Center - Vector3.up * (Height * 0.5f - Radius);

    internal GrassInteractorShape(Collider actor, Transform actorRoot, PhysicsScene physicsScene,
        Vector3 center, float radius, float height)
    {
        Actor = actor;
        ActorRoot = actorRoot;
        PhysicsScene = physicsScene;
        Center = center;
        Radius = radius;
        Height = height;
    }

    /// <summary>A virtual upright pose for bounded path probes; it never moves the actor.</summary>
    public GrassInteractorShape WithPose(Vector3 center, float radius, float height) =>
        new GrassInteractorShape(Actor, ActorRoot, PhysicsScene, center, radius, height);
}

public readonly struct GrassInteractorSupportSample
{
    public readonly Collider Support;
    public readonly Vector3 GroundPoint;
    /// <summary>Vertical sphere-cast separation, including the correct lower-cap contact on slopes.</summary>
    public readonly float Clearance;
    public readonly int SupportSignature;

    internal GrassInteractorSupportSample(Collider support, Vector3 point, float clearance, int signature)
    {
        Support = support;
        GroundPoint = point;
        Clearance = clearance;
        SupportSignature = signature;
    }
}

/// <summary>
/// Bounded support queries with reused buffers. Synchronize physics transforms
/// once before a batch. Automatic ownership uses live placement coverage and exact supporting
/// colliders; an explicit support also permits deliberately bound Legacy or generic coverage.
/// </summary>
public sealed class GrassInteractorSupport
{
    public const int DefaultCapacity = 64;
    public const int MaximumCapacity = 256;
    private const float CastSeparation = 0.01f;
    private const float AxisTolerance = 0.0001f;

    private struct AreaCandidate
    {
        public GrassPlacementArea Area;
        public Collider Support;
        public Bounds Bounds;
        public bool RequiresContact;
    }

    private readonly RaycastHit[] hits;
    private readonly Collider[] overlaps;
    private readonly AreaCandidate[] candidates;
    private int candidateCount;

    public int Capacity => hits.Length;

    public GrassInteractorSupport(int capacity = DefaultCapacity)
    {
        if (capacity < 1 || capacity > MaximumCapacity)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        hits = new RaycastHit[capacity];
        overlaps = new Collider[capacity];
        candidates = new AreaCandidate[capacity];
    }

    /// <summary>
    /// Reads collider geometry rather than its loose world AABB. Capsules may use any local
    /// direction whose transformed axis is vertical. Shear, tilted capsules/controllers and
    /// mirrored controllers are rejected instead of approximated as a different volume.
    /// </summary>
    public static bool TryGetShape(Collider actor, Transform selfRoot,
        out GrassInteractorShape shape, out GrassInteractorSupportStatus status)
    {
        shape = default;
        if (!ValidateActor(actor, selfRoot, out PhysicsScene scene, out status))
            return false;
        if (!(actor is CapsuleCollider) && !(actor is CharacterController) && !(actor is SphereCollider))
            return Reject(GrassInteractorSupportStatus.UnsupportedCollider, out status);

        Transform transform = actor.transform;
        Matrix4x4 matrix = transform.localToWorldMatrix;
        Vector3 scale = transform.lossyScale;
        if (!Finite(matrix) || !Finite(scale) || !TryGetAxes(matrix, out Vector3 x, out Vector3 y, out Vector3 z))
            return Reject(GrassInteractorSupportStatus.UnsupportedTransform, out status);

        Vector3 localCenter;
        double radius, height;
        if (actor is CapsuleCollider capsule)
        {
            int direction = capsule.direction;
            if (direction < 0 || direction > 2 || !Upright(direction == 0 ? x : direction == 1 ? y : z))
                return Reject(GrassInteractorSupportStatus.UnsupportedTransform, out status);
            localCenter = capsule.center;
            double radialScale = direction == 0 ? Math.Max(Math.Abs(scale.y), Math.Abs(scale.z)) :
                direction == 1 ? Math.Max(Math.Abs(scale.x), Math.Abs(scale.z)) :
                Math.Max(Math.Abs(scale.x), Math.Abs(scale.y));
            radius = capsule.radius * radialScale;
            height = capsule.height * (double)Mathf.Abs(scale[direction]);
        }
        else if (actor is CharacterController controller)
        {
            if (!Upright(y) || scale.x <= 0f || scale.y <= 0f || scale.z <= 0f)
                return Reject(GrassInteractorSupportStatus.UnsupportedTransform, out status);
            localCenter = controller.center;
            radius = controller.radius * (double)Mathf.Max(scale.x, scale.z);
            height = controller.height * (double)scale.y;
        }
        else
        {
            var sphere = (SphereCollider)actor;
            localCenter = sphere.center;
            radius = sphere.radius * (double)Mathf.Max(Mathf.Abs(scale.x),
                Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            height = radius * 2d;
        }

        if (!Finite(localCenter) || !Finite(radius) || !Finite(height) || radius <= 0d || height < 0d)
            return Reject(GrassInteractorSupportStatus.InvalidShape, out status);
        height = Math.Max(height, radius * 2d);
        shape = new GrassInteractorShape(actor, selfRoot, scene, matrix.MultiplyPoint3x4(localCenter),
            (float)radius, (float)height);
        if (!ValidPose(shape))
        {
            shape = default;
            return Reject(GrassInteractorSupportStatus.InvalidShape, out status);
        }
        status = GrassInteractorSupportStatus.Supported;
        return true;
    }

    /// <summary>
    /// Finds the nearest non-self physical support beneath the real lower cap. A separate
    /// ownership margin protects the generated mesh halo; it never enlarges the actor cast
    /// or requires physical contact with coverage touched only by that halo.
    /// </summary>
    public bool TryResolve(in GrassInteractorShape shape, Collider explicitSupport,
        float maxGroundClearance, float allowedPenetration,
        out GrassInteractorSupportSample sample, out GrassInteractorSupportStatus status,
        float ownershipMargin = 0f, int acceptedSupportLayers = Physics.AllLayers)
    {
        sample = default;
        try
        {
            if (!ValidateActor(shape.Actor, shape.ActorRoot, out PhysicsScene scene, out status))
                return false;
            if (scene != shape.PhysicsScene)
                return Reject(GrassInteractorSupportStatus.InvalidPhysicsScene, out status);
            if (!ValidPose(shape))
                return Reject(GrassInteractorSupportStatus.InvalidShape, out status);
            if (!Finite(maxGroundClearance) || maxGroundClearance < 0f ||
                !Finite(allowedPenetration) || allowedPenetration < 0f ||
                !Finite(ownershipMargin) || ownershipMargin < 0f)
                return Reject(GrassInteractorSupportStatus.InvalidSettings, out status);

            bool hasExplicit = GrassPlacementArea.HasAssignedReference(explicitSupport);
            if (hasExplicit && (!IsUsableSupport(explicitSupport) || !AcceptedLayer(explicitSupport, acceptedSupportLayers) ||
                IsSelf(explicitSupport, shape) ||
                explicitSupport.gameObject.scene.GetPhysicsScene() != scene))
                return Reject(GrassInteractorSupportStatus.InvalidSupport, out status);

            Bounds footprint = new Bounds(shape.Center, Vector3.zero)
                { extents = new Vector3(shape.Radius, 0f, shape.Radius) };
            Bounds envelope = GrassDispatchMath.ExpandXZ(footprint, ownershipMargin);
            if (!Finite(envelope.min) || !Finite(envelope.max) ||
                envelope.min.x >= envelope.max.x || envelope.min.z >= envelope.max.z)
                return Reject(GrassInteractorSupportStatus.InvalidSettings, out status);
            if (!CollectOwnership(shape, explicitSupport, footprint, envelope, ownershipMargin, acceptedSupportLayers, out status))
                return false;

            // Lifting by the permitted penetration starts ordinary grounded casts outside
            // the floor. Initial overlaps have no reliable support point or normal.
            float lift = (float)((double)allowedPenetration + CastSeparation);
            Vector3 origin = shape.LowerSphereCenter + Vector3.up * lift;
            float distance = (float)((double)lift + maxGroundClearance + shape.Radius);
            if (!Finite(lift) || !Finite(origin) || !Finite(distance) || distance <= 0f ||
                !Finite(origin.y - distance) || origin.y <= shape.LowerSphereCenter.y)
                return Reject(GrassInteractorSupportStatus.InvalidSettings, out status);

            int overlapCount = scene.OverlapSphere(origin, shape.Radius, overlaps,
                Physics.AllLayers, QueryTriggerInteraction.Ignore);
            if (overlapCount == overlaps.Length)
                return Reject(GrassInteractorSupportStatus.QueryOverflow, out status);
            for (int i = 0; i < overlapCount; i++)
                if (overlaps[i] && !IsSelf(overlaps[i], shape))
                    return Reject(GrassInteractorSupportStatus.InitialOverlap, out status);

            int hitCount = scene.SphereCast(origin, shape.Radius, Vector3.down, hits, distance,
                Physics.AllLayers, QueryTriggerInteraction.Ignore);
            // NonAlloc results are unordered and a full buffer need not contain the closest hit.
            if (hitCount == hits.Length)
                return Reject(GrassInteractorSupportStatus.QueryOverflow, out status);
            int nearest = -1;
            for (int i = 0; i < hitCount; i++)
            {
                Collider collider = hits[i].collider;
                if (!collider || IsSelf(collider, shape))
                    continue;
                if (!Finite(hits[i].distance) || hits[i].distance <= 0f)
                    return Reject(GrassInteractorSupportStatus.InitialOverlap, out status);
                if (nearest < 0 || hits[i].distance < hits[nearest].distance)
                    nearest = i;
            }
            if (nearest < 0)
                return Reject(GrassInteractorSupportStatus.Ungrounded, out status);

            float nearestDistance = hits[nearest].distance;
            int chosen = -1;
            bool foreignAtNearest = false;
            for (int i = 0; i < hitCount; i++)
            {
                // A coincident foreign floor is also ambiguous; never depend on native hit order.
                Collider collider = hits[i].collider;
                if (!collider || IsSelf(collider, shape) || hits[i].distance != nearestDistance)
                    continue;
                if (!UsableHit(hits[i]))
                    return Reject(GrassInteractorSupportStatus.Ungrounded, out status);
                bool owned = collider == explicitSupport || HasContactOwner(collider);
                foreignAtNearest |= !owned;
                if (owned && (!hasExplicit || collider == explicitSupport) &&
                    (chosen < 0 || collider.GetEntityId().CompareTo(hits[chosen].collider.GetEntityId()) < 0))
                    chosen = i;
            }
            if (chosen < 0)
                return Reject(GrassInteractorSupportStatus.MissingSupport, out status);
            if (foreignAtNearest)
                return Reject(GrassInteractorSupportStatus.AmbiguousSupport, out status);
            RaycastHit ground = hits[chosen];
            float clearance = ground.distance - lift;
            if (clearance < -allowedPenetration || clearance > maxGroundClearance)
                return Reject(GrassInteractorSupportStatus.Ungrounded, out status);
            for (int i = 0; i < candidateCount; i++)
            {
                if (!candidates[i].RequiresContact || candidates[i].Support == ground.collider)
                    continue;
                bool contacted = false;
                for (int j = 0; j < hitCount; j++)
                    if (hits[j].collider == candidates[i].Support &&
                        SupportsFootprintPoint(hits[j], shape, lift, maxGroundClearance, allowedPenetration))
                    {
                        contacted = true;
                        break;
                    }
                if (!contacted)
                    return Reject(GrassInteractorSupportStatus.MissingSupport, out status);
            }

            sample = new GrassInteractorSupportSample(ground.collider, ground.point, clearance,
                GetSupportSignature(ground.collider));
            status = GrassInteractorSupportStatus.Supported;
            return true;
        }
        finally
        {
            // Queries never retain scene objects across disable or scene teardown.
            Array.Clear(overlaps, 0, overlaps.Length);
            Array.Clear(candidates, 0, candidateCount);
            candidateCount = 0;
        }
    }

    private bool CollectOwnership(in GrassInteractorShape shape, Collider explicitSupport,
        Bounds footprint, Bounds envelope, float margin, int acceptedSupportLayers,
        out GrassInteractorSupportStatus status)
    {
        bool actualCoverage = false, explicitHasCoverage = false;
        var areas = GrassPlacementArea.ActiveAreas;
        for (int i = 0; i < areas.Count; i++)
        {
            GrassPlacementArea area = areas[i];
            // Rendering combines loaded areas across physics scenes. Their host scene
            // cannot hide an overlapping owner, or exclude an area bound to our floor.
            if (!area || !area.TryGetCoverageWorldBounds(out Bounds bounds) ||
                !InsideCircle(bounds, shape.Center, (double)shape.Radius + margin) || !area.IntersectsCoverage(envelope))
                continue;
            Collider support;
            if (area.HasAssignedTerrain)
            {
                Terrain terrain = area.Terrain;
                var terrainCollider = terrain ? terrain.GetComponent<TerrainCollider>() : null;
                support = terrainCollider && terrainCollider.terrainData == terrain.terrainData ? terrainCollider : null;
            }
            else
                support = area.HasAssignedPaintSurface ? area.PaintSurface : explicitSupport;
            if (!IsUsableSupport(support) || !AcceptedLayer(support, acceptedSupportLayers) || IsSelf(support, shape) ||
                support.gameObject.scene.GetPhysicsScene() != shape.PhysicsScene)
                return Reject(GrassInteractorSupportStatus.MissingSupport, out status);
            if (candidateCount == candidates.Length)
                return Reject(GrassInteractorSupportStatus.QueryOverflow, out status);

            bool contact = InsideCircle(bounds, shape.Center, shape.Radius) && area.IntersectsCoverage(footprint);
            actualCoverage |= contact;
            explicitHasCoverage |= support == explicitSupport;
            for (int j = 0; j < candidateCount; j++)
            {
                AreaCandidate other = candidates[j];
                if (other.Support != support && GrassDispatchMath.TryIntersectXZ(bounds, other.Bounds, out Bounds common) &&
                    InsideCircle(common, shape.Center, (double)shape.Radius + margin) &&
                    area.IntersectsCoverage(common) && other.Area.IntersectsCoverage(common))
                    return Reject(GrassInteractorSupportStatus.AmbiguousSupport, out status);
            }
            candidates[candidateCount++] = new AreaCandidate
                { Area = area, Support = support, Bounds = bounds, RequiresContact = contact };
        }
        if (!explicitSupport && !actualCoverage)
            return Reject(GrassInteractorSupportStatus.MissingSupport, out status);
        if (explicitSupport && !explicitHasCoverage)
        {
            // An explicit Legacy owner has no authored crop. Its full envelope cannot
            // safely share the same XZ interaction with another grass surface there.
            for (int i = 0; i < candidateCount; i++)
                if (candidates[i].Support != explicitSupport)
                    return Reject(GrassInteractorSupportStatus.AmbiguousSupport, out status);
        }
        status = GrassInteractorSupportStatus.Supported;
        return true;
    }

    private bool HasContactOwner(Collider collider)
    {
        for (int i = 0; i < candidateCount; i++)
            if (candidates[i].RequiresContact && candidates[i].Support == collider)
                return true;
        return false;
    }

    /// <summary>Native liveness for support queries and retained explicit-support history.</summary>
    public static bool IsUsableSupport(Collider support)
    {
        if (!support || !support.enabled || support.isTrigger || !support.gameObject.activeInHierarchy ||
            !UsableScene(support.gameObject.scene) || !Finite(support.transform.localToWorldMatrix))
            return false;
        Bounds bounds = support.bounds;
        if (!Finite(bounds.min) || !Finite(bounds.max))
            return false;
        if (support is TerrainCollider terrain && !terrain.terrainData)
            return false;
        if (support is MeshCollider mesh && (!mesh.sharedMesh || mesh.sharedMesh.vertexCount == 0))
            return false;
        return true;
    }

    /// <summary>
    /// Cheap identity, pose and geometry metadata for history invalidation; zero means unusable.
    /// In-place mesh cooking changes that preserve this metadata require the owner's explicit reset.
    /// </summary>
    public static int GetSupportSignature(Collider support)
    {
        if (!IsUsableSupport(support))
            return 0;
        unchecked
        {
            int hash = support.GetEntityId().GetHashCode();
            hash = hash * 397 ^ support.gameObject.scene.handle.GetRawData().GetHashCode();
            hash = hash * 397 ^ support.transform.localToWorldMatrix.GetHashCode();
            hash = hash * 397 ^ support.bounds.GetHashCode();
            hash = hash * 397 ^ support.gameObject.layer;
            if (support is BoxCollider box)
                hash = (hash * 397 ^ box.center.GetHashCode()) * 397 ^ box.size.GetHashCode();
            else if (support is SphereCollider sphere)
                hash = (hash * 397 ^ sphere.center.GetHashCode()) * 397 ^ sphere.radius.GetHashCode();
            else if (support is CapsuleCollider capsule)
            {
                hash = (hash * 397 ^ capsule.center.GetHashCode()) * 397 ^ capsule.radius.GetHashCode();
                hash = (hash * 397 ^ capsule.height.GetHashCode()) * 397 ^ capsule.direction;
            }
            else if (support is CharacterController controller)
            {
                hash = (hash * 397 ^ controller.center.GetHashCode()) * 397 ^ controller.radius.GetHashCode();
                hash = hash * 397 ^ controller.height.GetHashCode();
            }
            else if (support is MeshCollider mesh)
            {
                Mesh source = mesh.sharedMesh;
                hash = (hash * 397 ^ source.GetEntityId().GetHashCode()) * 397 ^ source.bounds.GetHashCode();
                hash = (hash * 397 ^ source.vertexCount) * 397 ^ source.subMeshCount;
                hash = (hash * 397 ^ (int)mesh.cookingOptions) * 397 ^ mesh.convex.GetHashCode();
            }
            else if (support is TerrainCollider terrain)
            {
                TerrainData source = terrain.terrainData;
                hash = (hash * 397 ^ source.GetEntityId().GetHashCode()) * 397 ^ source.size.GetHashCode();
                hash = hash * 397 ^ source.heightmapResolution;
                Texture heights = source.heightmapTexture, holes = source.holesTexture;
                hash = hash * 397 ^ (heights ? (int)heights.updateCount : 0);
                hash = hash * 397 ^ (holes ? (int)holes.updateCount : 0);
            }
            return hash == 0 ? 1 : hash;
        }
    }

    private static bool ValidateActor(Collider actor, Transform selfRoot, out PhysicsScene scene,
        out GrassInteractorSupportStatus status)
    {
        scene = default;
        if (!actor)
            return Reject(GrassInteractorSupportStatus.MissingActor, out status);
        if (!actor.enabled || !actor.gameObject.activeInHierarchy)
            return Reject(GrassInteractorSupportStatus.InactiveActor, out status);
        if (!selfRoot || !actor.transform.IsChildOf(selfRoot))
            return Reject(GrassInteractorSupportStatus.InvalidActorRoot, out status);
        if (!UsableScene(actor.gameObject.scene) || !(scene = actor.gameObject.scene.GetPhysicsScene()).IsValid())
            return Reject(GrassInteractorSupportStatus.InvalidPhysicsScene, out status);
        status = GrassInteractorSupportStatus.Supported;
        return true;
    }

    private static bool IsSelf(Collider collider, in GrassInteractorShape shape)
    {
        if (collider == shape.Actor || collider.transform.IsChildOf(shape.ActorRoot))
            return true;
        Rigidbody body = shape.Actor.attachedRigidbody;
        // Static colliders all have null bodies; null equality must not hide the floor.
        return body && collider.attachedRigidbody == body;
    }

    private static bool UsableHit(RaycastHit hit) => IsUsableSupport(hit.collider) &&
        Finite(hit.point) && Finite(hit.normal) && hit.normal.y > 0f && Finite(hit.distance) && hit.distance > 0f;

    private static bool SupportsFootprintPoint(RaycastHit hit, in GrassInteractorShape shape,
        float lift, float clearance, float penetration)
    {
        if (!UsableHit(hit))
            return false;
        double rawGap = (double)hit.distance - lift;
        if (rawGap < -penetration)
            return false;
        double x = (double)hit.point.x - shape.Center.x, z = (double)hit.point.z - shape.Center.z;
        double radius = shape.Radius, horizontalSquared = x * x + z * z;
        if (!Finite(horizontalSquared) || horizontalSquared > radius * radius)
            return false;
        // At a neighboring tile edge, the lower cap must sweep farther than at
        // its center even when both tiles have the same height. Remove only that
        // geometric drop; a raised/sloped side point does not imply penetration.
        double verticalRadius = Math.Sqrt(radius * radius - horizontalSquared);
        double edgeDrop = horizontalSquared / (radius + verticalRadius);
        double gap = Math.Max(0d, rawGap - edgeDrop);
        double pointGap = Math.Max(0d, (double)shape.LowerSphereCenter.y - radius - hit.point.y);
        return Finite(gap) && gap <= clearance && pointGap <= clearance;
    }

    private static bool AcceptedLayer(Collider collider, int mask) => (mask & (1 << collider.gameObject.layer)) != 0;

    private static bool UsableScene(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded)
            return false;
#if UNITY_EDITOR
        if (UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(scene))
            return false;
#endif
        return true;
    }

    private static bool ValidPose(in GrassInteractorShape shape)
    {
        if (!Finite(shape.Center) || !Finite(shape.Radius) || shape.Radius <= 0f ||
            !Finite(shape.Height) || shape.Height < (double)shape.Radius * 2d || !Finite(shape.LowerSphereCenter))
            return false;
        Vector3 half = new Vector3(shape.Radius, shape.Height * 0.5f, shape.Radius);
        Vector3 minimum = shape.Center - half, maximum = shape.Center + half;
        return Finite(minimum) && Finite(maximum) && minimum.x < maximum.x &&
            minimum.y < maximum.y && minimum.z < maximum.z;
    }

    private static bool TryGetAxes(Matrix4x4 matrix, out Vector3 x, out Vector3 y, out Vector3 z)
    {
        x = matrix.GetColumn(0); y = matrix.GetColumn(1); z = matrix.GetColumn(2);
        if (!Normalize(ref x) || !Normalize(ref y) || !Normalize(ref z))
            return false;
        return Mathf.Abs(Vector3.Dot(x, y)) <= AxisTolerance && Mathf.Abs(Vector3.Dot(x, z)) <= AxisTolerance &&
            Mathf.Abs(Vector3.Dot(y, z)) <= AxisTolerance;
    }

    private static bool Normalize(ref Vector3 axis)
    {
        double length = Math.Sqrt((double)axis.x * axis.x + (double)axis.y * axis.y + (double)axis.z * axis.z);
        if (!Finite(length) || length <= 0d)
            return false;
        axis = new Vector3((float)(axis.x / length), (float)(axis.y / length), (float)(axis.z / length));
        return true;
    }

    private static bool Upright(Vector3 axis) => Mathf.Abs(axis.x) <= AxisTolerance && Mathf.Abs(axis.z) <= AxisTolerance;

    private static bool InsideCircle(Bounds bounds, Vector3 center, double radius)
    {
        Vector3 minimum = bounds.min, maximum = bounds.max;
        if (minimum.x >= maximum.x || minimum.z >= maximum.z)
            return false;
        double x = Math.Max((double)minimum.x, Math.Min((double)center.x, maximum.x)) - center.x;
        double z = Math.Max((double)minimum.z, Math.Min((double)center.z, maximum.z)) - center.z;
        return x * x + z * z < radius * radius;
    }

    private static bool Reject(GrassInteractorSupportStatus value, out GrassInteractorSupportStatus status)
    {
        status = value;
        return false;
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    private static bool Finite(Matrix4x4 value)
    {
        for (int i = 0; i < 16; i++)
            if (!Finite(value[i]))
                return false;
        return true;
    }
}
