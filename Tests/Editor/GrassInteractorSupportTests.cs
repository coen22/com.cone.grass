using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

public sealed class GrassInteractorSupportTests : GrassPhysicsFixtureTests
{
    private readonly List<Scene> scenes = new List<Scene>();
    private readonly List<Object> assets = new List<Object>();
    private readonly List<GrassPlacementArea> previousAreas = new List<GrassPlacementArea>();
    private Scene scene;

    [SetUp]
    public void SetUp()
    {
        // Physics scenes isolate collision queries, but loaded grass still shares XZ
        // captures. Preserve existing authored coverage while these fixtures own it.
        var active = GrassPlacementArea.ActiveAreas;
        for (int i = 0; i < active.Count; i++)
            if (active[i] && active[i].enabled)
                previousAreas.Add(active[i]);
        foreach (GrassPlacementArea area in previousAreas)
            area.enabled = false;
        scene = NewPhysicsScene();
    }

    [TearDown]
    public void TearDown()
    {
        for (int i = scenes.Count - 1; i >= 0; i--)
            if (scenes[i].IsValid() && scenes[i].isLoaded)
                GrassPhysicsFixtureScenes.Close(scenes[i]);
        for (int i = assets.Count - 1; i >= 0; i--)
            if (assets[i])
                Object.DestroyImmediate(assets[i]);
        scenes.Clear();
        assets.Clear();
        foreach (GrassPlacementArea area in previousAreas)
            if (area)
                area.enabled = true;
        previousAreas.Clear();
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void ActualShapesPreserveOffsetYawAndNonuniformScale(int kind)
    {
        GameObject actor = NewObject("scaled actor");
        actor.transform.position = new Vector3(12f, 7f, -20f);
        actor.transform.rotation = Quaternion.Euler(0f, 73f, 0f);
        actor.transform.localScale = new Vector3(2f, 3f, 4f);
        Vector3 localCenter = new Vector3(1f, 0.5f, -1.5f);
        Collider collider;
        if (kind == 0)
        {
            var capsule = actor.AddComponent<CapsuleCollider>();
            capsule.center = localCenter;
            capsule.radius = 0.25f;
            capsule.height = 2f;
            collider = capsule;
        }
        else if (kind == 1)
        {
            var controller = actor.AddComponent<CharacterController>();
            controller.center = localCenter;
            controller.radius = 0.25f;
            controller.height = 2f;
            collider = controller;
        }
        else
        {
            var sphere = actor.AddComponent<SphereCollider>();
            sphere.center = localCenter;
            sphere.radius = 0.25f;
            collider = sphere;
        }

        Assert.That(GrassInteractorSupport.TryGetShape(collider, actor.transform, out var shape, out var status), Is.True);
        Assert.That(status, Is.EqualTo(GrassInteractorSupportStatus.Supported));
        Assert.That(shape.Center, Is.EqualTo(actor.transform.TransformPoint(localCenter)));
        Assert.That(shape.Radius, Is.EqualTo(1f).Within(0.0001f));
        Assert.That(shape.Height, Is.EqualTo(kind == 2 ? 2f : 6f).Within(0.0001f));
        Assert.That(shape.LowerSphereCenter.y, Is.EqualTo(shape.Center.y - (kind == 2 ? 0f : 2f)).Within(0.0001f));
        Assert.That(shape.Actor, Is.SameAs(collider));
        Assert.That(shape.ActorRoot, Is.SameAs(actor.transform));
        Assert.That(shape.PhysicsScene, Is.EqualTo(scene.GetPhysicsScene()));
    }

    [Test]
    public void LocalXDirectionCapsuleCanBeRotatedUprightAndSphereKeepsItsRealMaximumScale()
    {
        CapsuleCollider capsule = NewCapsule(new Vector3(0f, 3f, 0f));
        capsule.direction = 0;
        capsule.radius = 0.25f;
        capsule.transform.localScale = new Vector3(3f, 2f, 4f);
        capsule.transform.rotation = Quaternion.Euler(0f, 0f, 90f);
        Assert.That(GrassInteractorSupport.TryGetShape(capsule, capsule.transform, out var shape, out _), Is.True);
        Assert.That(shape.Radius, Is.EqualTo(1f).Within(0.0001f));
        Assert.That(shape.Height, Is.EqualTo(6f).Within(0.0001f));

        var sphere = NewObject("mirrored sphere").AddComponent<SphereCollider>();
        sphere.radius = 0.25f;
        sphere.transform.localScale = new Vector3(-2f, 3f, 4f);
        sphere.transform.rotation = Quaternion.Euler(27f, 63f, 12f);
        Assert.That(GrassInteractorSupport.TryGetShape(sphere, sphere.transform, out shape, out _), Is.True);
        Assert.That(shape.Radius, Is.EqualTo(1f).Within(0.0001f));
        Assert.That(shape.Height, Is.EqualTo(2f).Within(0.0001f));
    }

    [Test]
    public void UnsupportedTiltShearAndActorRootFailWithoutSubstitutingAnAabb()
    {
        CapsuleCollider capsule = NewCapsule(Vector3.up);
        capsule.transform.rotation = Quaternion.Euler(5f, 0f, 0f);
        Assert.That(GrassInteractorSupport.TryGetShape(capsule, capsule.transform, out _, out var status), Is.False);
        Assert.That(status, Is.EqualTo(GrassInteractorSupportStatus.UnsupportedTransform));

        Transform parent = NewObject("scaled parent").transform;
        parent.localScale = new Vector3(2f, 1f, 1f);
        capsule.transform.SetParent(parent, false);
        capsule.transform.localRotation = Quaternion.Euler(0f, 45f, 0f);
        Assert.That(GrassInteractorSupport.TryGetShape(capsule, parent, out _, out status), Is.False);
        Assert.That(status, Is.EqualTo(GrassInteractorSupportStatus.UnsupportedTransform));

        var controller = NewObject("mirrored controller").AddComponent<CharacterController>();
        controller.transform.localScale = new Vector3(-1f, 1f, 1f);
        Assert.That(GrassInteractorSupport.TryGetShape(controller, controller.transform, out _, out status), Is.False);
        Assert.That(status, Is.EqualTo(GrassInteractorSupportStatus.UnsupportedTransform));
        controller.transform.localScale = Vector3.one;
        Assert.That(GrassInteractorSupport.TryGetShape(controller, parent, out _, out status), Is.False);
        Assert.That(status, Is.EqualTo(GrassInteractorSupportStatus.InvalidActorRoot));

        var box = NewObject("unsupported actor").AddComponent<BoxCollider>();
        Assert.That(GrassInteractorSupport.TryGetShape(box, box.transform, out _, out status), Is.False);
        Assert.That(status, Is.EqualTo(GrassInteractorSupportStatus.UnsupportedCollider));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void ActualColliderStandingOnExplicitFloorResolvesAndRaisedPoseDoesNot(int kind)
    {
        BoxCollider floor = NewFloor(Vector3.zero, new Vector2(10f, 10f));
        Collider actor;
        if (kind == 0)
            actor = NewCapsule(Vector3.up);
        else if (kind == 1)
        {
            var controller = NewObject("controller").AddComponent<CharacterController>();
            controller.height = 2f;
            controller.radius = 0.5f;
            controller.center = Vector3.zero;
            controller.transform.position = Vector3.up;
            actor = controller;
        }
        else
        {
            var sphere = NewObject("sphere").AddComponent<SphereCollider>();
            sphere.radius = 0.5f;
            sphere.transform.position = Vector3.up * 0.5f;
            actor = sphere;
        }
        Physics.SyncTransforms();
        GrassInteractorShape shape = Shape(actor);
        var resolver = new GrassInteractorSupport();

        Assert.That(resolver.TryResolve(shape, floor, 0.1f, 0.05f, out var sample, out var status), Is.True);
        Assert.That(status, Is.EqualTo(GrassInteractorSupportStatus.Supported));
        Assert.That(sample.Support, Is.SameAs(floor));
        Assert.That(sample.Clearance, Is.EqualTo(0f).Within(0.002f));
        Assert.That(sample.GroundPoint.y, Is.EqualTo(0f).Within(0.002f));
        Assert.That(sample.SupportSignature, Is.EqualTo(GrassInteractorSupport.GetSupportSignature(floor)));

        GrassInteractorShape raised = shape.WithPose(shape.Center + Vector3.up * 0.2f, shape.Radius, shape.Height);
        Assert.That(resolver.TryResolve(raised, floor, 0.1f, 0.05f, out _, out status), Is.False);
        Assert.That(status, Is.EqualTo(GrassInteractorSupportStatus.Ungrounded));
        Assert.That(actor.transform.position, Is.EqualTo(kind == 2 ? Vector3.up * 0.5f : Vector3.up),
            "Virtual path and jump probes must not move a producer-owned collider.");
    }

    [Test]
    public void GroundedSlopeUsesLowerSphereClearanceInsteadOfTheCenterlineBottomGap()
    {
        const float radius = 0.5f;
        Quaternion rotation = Quaternion.Euler(0f, 0f, 45f);
        Vector3 normal = rotation * Vector3.up;
        BoxCollider floor = NewFloor(Vector3.zero, new Vector2(20f, 20f));
        floor.transform.rotation = rotation;
        floor.transform.position = -normal * 0.25f;
        float lowerSphereY = radius / normal.y;
        CapsuleCollider actor = NewCapsule(new Vector3(0f, lowerSphereY + 0.5f, 0f));
        Physics.SyncTransforms();
        GrassInteractorShape shape = Shape(actor);
        Assert.That(shape.Center.y - shape.Height * 0.5f, Is.GreaterThan(0.2f),
            "The grounded centerline bottom is more than the allowed clearance above this sloped plane.");
        var resolver = new GrassInteractorSupport();
        Assert.That(resolver.TryResolve(shape, floor, 0.05f, 0.05f, out var sample, out _), Is.True);
        Assert.That(sample.Clearance, Is.EqualTo(0f).Within(0.002f));
        Assert.That(sample.GroundPoint.x, Is.EqualTo(radius * -normal.x).Within(0.003f));

        shape = shape.WithPose(shape.Center + Vector3.up * 0.2f, shape.Radius, shape.Height);
        Assert.That(resolver.TryResolve(shape, floor, 0.05f, 0.05f, out _, out var status), Is.False);
        Assert.That(status, Is.EqualTo(GrassInteractorSupportStatus.Ungrounded));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ActorHierarchyAndSharedBodyAreIgnoredButStaticNullBodiesAreNot(bool sharedBodySibling)
    {
        BoxCollider floor = NewFloor(Vector3.zero, new Vector2(10f, 10f));
        GameObject actorRoot = NewObject("actor root");
        if (sharedBodySibling)
            actorRoot.AddComponent<Rigidbody>().isKinematic = true;
        CapsuleCollider actor = NewCapsule(Vector3.up);
        actor.transform.SetParent(actorRoot.transform, true);
        GameObject helper = NewObject("actor helper");
        helper.transform.SetParent(actorRoot.transform, false);
        helper.transform.position = new Vector3(0f, 0.7f, 0f);
        helper.AddComponent<BoxCollider>().size = Vector3.one * 0.4f;
        Transform selfRoot = sharedBodySibling ? actor.transform : actorRoot.transform;
        Physics.SyncTransforms();
        Assert.That(GrassInteractorSupport.TryGetShape(actor, selfRoot, out var shape, out _), Is.True);
        Assert.That(new GrassInteractorSupport().TryResolve(shape, floor, 0.1f, 0.05f, out var sample, out _), Is.True);
        Assert.That(sample.Support, Is.SameAs(floor));
        Assert.That(floor.attachedRigidbody, Is.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void NearestForeignFloorCannotBeSkippedToReachGrassBelow(bool explicitBinding)
    {
        BoxCollider floor = NewFloor(Vector3.zero, new Vector2(10f, 10f));
        NewArea(floor, Vector3.zero, new Vector2(10f, 10f));
        NewFloor(new Vector3(0f, 0.2f, 0f), new Vector2(10f, 10f));
        CapsuleCollider actor = NewCapsule(Vector3.up * 1.2f);
        Physics.SyncTransforms();
        Assert.That(new GrassInteractorSupport().TryResolve(Shape(actor), explicitBinding ? floor : null,
            0.1f, 0.05f, out _, out var status), Is.False);
        Assert.That(status, Is.EqualTo(GrassInteractorSupportStatus.MissingSupport));
    }

    [Test]
    public void CoincidentForeignFloorIsRejectedRegardlessOfUnorderedHitSelection()
    {
        BoxCollider floor = NewFloor(Vector3.zero, new Vector2(10f, 10f));
        NewFloor(Vector3.zero, new Vector2(10f, 10f));
        CapsuleCollider actor = NewCapsule(Vector3.up);
        Physics.SyncTransforms();
        Assert.That(new GrassInteractorSupport().TryResolve(Shape(actor), floor, 0.1f, 0.05f,
            out _, out var status), Is.False);
        Assert.That(status, Is.EqualTo(GrassInteractorSupportStatus.AmbiguousSupport));
    }

    [Test]
    public void VerticallyOverlappingGrassOwnersAreAmbiguousWhileDuplicateAreasOnOneFloorAreValid()
    {
        BoxCollider lower = NewFloor(Vector3.zero, new Vector2(10f, 10f));
        NewArea(lower, Vector3.zero, new Vector2(10f, 10f));
        NewArea(lower, Vector3.zero, new Vector2(10f, 10f));
        CapsuleCollider actor = NewCapsule(Vector3.up);
        Physics.SyncTransforms();
        var resolver = new GrassInteractorSupport();
        Assert.That(resolver.TryResolve(Shape(actor), null, 0.1f, 0.05f, out _, out _), Is.True);

        BoxCollider upper = NewFloor(Vector3.up * 3f, new Vector2(10f, 10f));
        GrassPlacementArea upperArea = NewArea(upper, Vector3.up * 3f, new Vector2(10f, 10f));
        Physics.SyncTransforms();
        Assert.That(resolver.TryResolve(Shape(actor), null, 0.1f, 0.05f, out _, out var status), Is.False);
        Assert.That(status, Is.EqualTo(GrassInteractorSupportStatus.AmbiguousSupport),
            "The shared XZ capture would bend both floors even though only the lower floor is under the actor.");
        upperArea.enabled = false;
        Assert.That(resolver.TryResolve(Shape(actor), null, 0.1f, 0.05f, out _, out _), Is.True);
    }

    [TestCase(false, -0.25f)]
    [TestCase(false, -0.4f)]
    [TestCase(true, -0.25f)]
    [TestCase(true, -0.4f)]
    [TestCase(true, -0.75f)]
    public void AdjacentSameHeightTilesHaveNoPositiveAreaOwnershipOverlap(bool terrainTiles, float actorX)
    {
        Collider left;
        if (terrainTiles)
        {
            Terrain a = NewTerrain(new Vector3(-10f, 0f, -5f), new Vector3(10f, 1f, 10f));
            Terrain b = NewTerrain(new Vector3(0f, 0f, -5f), new Vector3(10f, 1f, 10f));
            NewTerrainArea(a);
            NewTerrainArea(b);
            left = a.GetComponent<TerrainCollider>();
        }
        else
        {
            left = NewFloor(new Vector3(-5f, 0f, 0f), new Vector2(10f, 10f));
            BoxCollider right = NewFloor(new Vector3(5f, 0f, 0f), new Vector2(10f, 10f));
            NewArea(left, new Vector3(-5f, 0f, 0f), new Vector2(10f, 10f));
            NewArea(right, new Vector3(5f, 0f, 0f), new Vector2(10f, 10f));
        }
        CapsuleCollider actor = NewCapsule(new Vector3(actorX, 1f, 0f));
        Physics.SyncTransforms();
        Assert.That(new GrassInteractorSupport().TryResolve(Shape(actor), null, 0.1f, 0.05f,
            out var sample, out _, 0.5f), Is.True);
        Assert.That(sample.Support, Is.SameAs(left),
            "A neighboring tile touched only by the mesh halo needs no enlarged physical sphere cast.");
    }

    [Test]
    public void AffectedNeighborMustAlsoPassTheConfiguredGroundClearance()
    {
        BoxCollider left = NewFloor(new Vector3(-5f, 0f, 0f), new Vector2(10f, 10f));
        BoxCollider right = NewFloor(new Vector3(5f, 0f, 0f), new Vector2(10f, 10f));
        NewArea(left, new Vector3(-5f, 0f, 0f), new Vector2(10f, 10f));
        NewArea(right, new Vector3(5f, 0f, 0f), new Vector2(10f, 10f));
        CapsuleCollider actor = NewCapsule(new Vector3(-0.25f, 1f, 0f));
        Physics.SyncTransforms();
        var resolver = new GrassInteractorSupport();
        Assert.That(resolver.TryResolve(Shape(actor), null, 0.15f, 0.1f, out _, out _), Is.True);
        // The neighboring cap contact is now 0.4 + (0.5 - sqrt(0.5^2 - 0.25^2))
        // below its original position: inside the broad query reach, beyond clearance.
        right.transform.position += Vector3.down * 0.4f;
        Physics.SyncTransforms();
        Assert.That(resolver.TryResolve(Shape(actor), null, 0.15f, 0.1f, out _, out var status), Is.False);
        Assert.That(status, Is.EqualTo(GrassInteractorSupportStatus.MissingSupport));
    }

    [Test]
    public void AcceptedSupportLayersNeverHideANearerExcludedPhysicalFloor()
    {
        BoxCollider floor = NewFloor(Vector3.zero, new Vector2(10f, 10f));
        floor.gameObject.layer = 8;
        GrassPlacementArea area = NewArea(floor, Vector3.zero, new Vector2(10f, 10f));
        CapsuleCollider actor = NewCapsule(Vector3.up);
        Physics.SyncTransforms();
        var resolver = new GrassInteractorSupport();
        Assert.That(resolver.TryResolve(Shape(actor), null, 0.1f, 0.05f, out _, out _, 0f, 1 << 8), Is.True);
        Assert.That(resolver.TryResolve(Shape(actor), null, 0.1f, 0.05f, out _, out var status, 0f, 1 << 9), Is.False);
        Assert.That(status, Is.EqualTo(GrassInteractorSupportStatus.MissingSupport));
        Assert.That(resolver.TryResolve(Shape(actor), floor, 0.1f, 0.05f, out _, out status, 0f, 1 << 9), Is.False);
        Assert.That(status, Is.EqualTo(GrassInteractorSupportStatus.InvalidSupport));

        area.enabled = false; // Deliberate Legacy binding still uses the same accepted floor.
        BoxCollider nearer = NewFloor(new Vector3(0f, 0.2f, 0f), new Vector2(10f, 10f));
        nearer.gameObject.layer = 9;
        actor.transform.position = Vector3.up * 1.2f;
        Physics.SyncTransforms();
        Assert.That(resolver.TryResolve(Shape(actor), floor, 0.1f, 0.05f, out _, out status, 0f, 1 << 8), Is.False);
        Assert.That(status, Is.EqualTo(GrassInteractorSupportStatus.MissingSupport),
            "The query must still see an excluded nearer floor instead of tunneling to accepted ground below it.");
    }

    [Test]
    public void DisjointTextureCropsRemainSeparateEvenWhenTheirFullMappingBoundsOverlap()
    {
        BoxCollider left = NewFloor(new Vector3(-2.5f, 0f, 0f), new Vector2(5f, 10f));
        BoxCollider right = NewFloor(new Vector3(2.5f, 0f, 0f), new Vector2(5f, 10f));
        GrassPlacementArea a = NewArea(left, Vector3.zero, new Vector2(10f, 10f));
        GrassPlacementArea b = NewArea(right, Vector3.zero, new Vector2(10f, 10f));
        SetExternalCrop(a, new Rect(0f, 0f, 0.5f, 1f));
        SetExternalCrop(b, new Rect(0.5f, 0f, 0.5f, 1f));
        CapsuleCollider actor = NewCapsule(new Vector3(-0.25f, 1f, 0f));
        Physics.SyncTransforms();
        Assert.That(a.WorldBounds.min.x, Is.EqualTo(b.WorldBounds.min.x));
        Assert.That(a.WorldBounds.max.x, Is.EqualTo(b.WorldBounds.max.x));
        var resolver = new GrassInteractorSupport();
        Assert.That(resolver.TryResolve(Shape(actor), null, 0.1f, 0.05f, out _, out _), Is.True);

        b.SetTextureCoverageBounds(new Rect(0.45f, 0f, 0.55f, 1f));
        Assert.That(resolver.TryResolve(Shape(actor), null, 0.1f, 0.05f, out _, out var status), Is.False);
        Assert.That(status, Is.EqualTo(GrassInteractorSupportStatus.AmbiguousSupport));
    }

    [Test]
    public void OwnershipMarginFindsStackedGrassBeyondTheRealColliderWithoutChangingGrounding()
    {
        BoxCollider lower = NewFloor(Vector3.zero, new Vector2(10f, 10f));
        NewArea(lower, Vector3.zero, new Vector2(10f, 10f));
        BoxCollider upper = NewFloor(new Vector3(1f, 3f, 0f), new Vector2(0.2f, 1f));
        NewArea(upper, new Vector3(1f, 3f, 0f), new Vector2(0.2f, 1f));
        CapsuleCollider actor = NewCapsule(Vector3.up);
        Physics.SyncTransforms();
        var resolver = new GrassInteractorSupport();
        Assert.That(resolver.TryResolve(Shape(actor), null, 0.1f, 0.05f, out _, out _), Is.True);
        Assert.That(resolver.TryResolve(Shape(actor), null, 0.1f, 0.05f, out _, out var status, 0.5f), Is.False);
        Assert.That(status, Is.EqualTo(GrassInteractorSupportStatus.AmbiguousSupport));
    }

    [Test]
    public void AreaHostPhysicsSceneDoesNotOverrideItsExplicitTerrainSupportScene()
    {
        Terrain terrain = NewTerrain(new Vector3(-5f, 0f, -5f), new Vector3(10f, 1f, 10f));
        GrassPlacementArea area = NewTerrainArea(terrain);
        Scene host = NewPhysicsScene();
        SceneManager.MoveGameObjectToScene(area.gameObject, host);
        CapsuleCollider actor = NewCapsule(Vector3.up);
        Physics.SyncTransforms();
        Assert.That(new GrassInteractorSupport().TryResolve(Shape(actor), null, 0.1f, 0.05f,
            out var sample, out _), Is.True);
        Assert.That(sample.Support, Is.SameAs(terrain.GetComponent<TerrainCollider>()));
    }

    [Test]
    public void ForeignPhysicsSceneCannotHideGrassSharingTheRenderedXzEnvelope()
    {
        BoxCollider floor = NewFloor(Vector3.zero, new Vector2(10f, 10f));
        NewArea(floor, Vector3.zero, new Vector2(10f, 10f));
        CapsuleCollider actor = NewCapsule(Vector3.up);
        Scene foreign = NewPhysicsScene();
        Terrain upper = NewTerrain(new Vector3(-5f, 3f, -5f), new Vector3(10f, 1f, 10f));
        GrassPlacementArea upperArea = NewTerrainArea(upper);
        SceneManager.MoveGameObjectToScene(upper.gameObject, foreign);
        SceneManager.MoveGameObjectToScene(upperArea.gameObject, foreign);
        Physics.SyncTransforms();
        var resolver = new GrassInteractorSupport();
        Assert.That(resolver.TryResolve(Shape(actor), null, 0.1f, 0.05f, out _, out var status), Is.False);
        Assert.That(status, Is.EqualTo(GrassInteractorSupportStatus.MissingSupport),
            "Physics isolation does not partition the renderer's shared XZ interaction capture.");

        upper.transform.position += Vector3.right * 100f;
        Physics.SyncTransforms();
        Assert.That(resolver.TryResolve(Shape(actor), null, 0.1f, 0.05f, out _, out _), Is.True);
    }

    [Test]
    public void PaintedOwnershipQueriesKeepPendingCpuPixelsAndGpuUploadCountUntouched()
    {
        BoxCollider floor = NewFloor(Vector3.zero, new Vector2(10f, 10f));
        GrassPlacementArea area = NewArea(floor, Vector3.zero, new Vector2(10f, 10f));
        var density = ScriptableObject.CreateInstance<GrassDensityAsset>();
        assets.Add(density);
        density.Resize(16, 16, false);
        density.Fill(1f);
        area.SetDensityAsset(density, false);
        RectInt pending = density.PendingUploadRegion;
        uint uploads = density.TextureUploadCount, revision = density.Revision;
        CapsuleCollider actor = NewCapsule(Vector3.up);
        Physics.SyncTransforms();
        Assert.That(new GrassInteractorSupport().TryResolve(Shape(actor), null, 0.1f, 0.05f,
            out _, out _), Is.True);
        Assert.That(density.TextureUploadCount, Is.EqualTo(uploads));
        Assert.That(density.PendingUploadRegion, Is.EqualTo(pending));
        Assert.That(density.Revision, Is.EqualTo(revision));
        density.Fill(0f);
        Assert.That(new GrassInteractorSupport().TryResolve(Shape(actor), null, 0.1f, 0.05f,
            out _, out var status), Is.False);
        Assert.That(status, Is.EqualTo(GrassInteractorSupportStatus.MissingSupport));
    }

    [Test]
    public void TerrainColliderMustMatchTheAuthoredTerrainDataAndRecoverAfterRebinding()
    {
        Terrain terrain = NewTerrain(new Vector3(-5f, 0f, -5f), new Vector3(10f, 1f, 10f));
        GrassPlacementArea area = NewTerrainArea(terrain);
        TerrainCollider collider = terrain.GetComponent<TerrainCollider>();
        var other = new TerrainData { heightmapResolution = 33, size = new Vector3(10f, 1f, 10f) };
        assets.Add(other);
        collider.terrainData = other;
        CapsuleCollider actor = NewCapsule(Vector3.up);
        Physics.SyncTransforms();
        var resolver = new GrassInteractorSupport();
        Assert.That(resolver.TryResolve(Shape(actor), null, 0.1f, 0.05f, out _, out var status), Is.False);
        Assert.That(status, Is.EqualTo(GrassInteractorSupportStatus.MissingSupport));
        collider.terrainData = terrain.terrainData;
        Physics.SyncTransforms();
        Assert.That(resolver.TryResolve(Shape(actor), null, 0.1f, 0.05f, out _, out _), Is.True);
        // Local mapping makes a live mesh fallback possible, so the destroyed
        // assigned reference must itself prevent rebinding to that surface.
        BoxCollider fallback = NewFloor(Vector3.zero, new Vector2(10f, 10f));
        var local = new SerializedObject(area);
        local.FindProperty("useTerrainBounds").boolValue = false;
        local.FindProperty("paintSurface").objectReferenceValue = fallback;
        local.ApplyModifiedPropertiesWithoutUndo();
        EntityId assigned = terrain.GetEntityId();
        Assert.That(assigned, Is.Not.EqualTo(EntityId.None));
        Assert.That(area.TryGetCaptureData(out _), Is.True);
        Bounds probe = new Bounds(Vector3.zero, Vector3.one);

        terrain.enabled = false;
        Assert.That(area.Terrain.GetEntityId(), Is.EqualTo(assigned));
        Assert.That(area.TryGetCaptureData(out _), Is.False);
        Assert.That(area.IntersectsCoverage(probe), Is.False);
        terrain.enabled = true;
        Assert.That(area.Terrain.GetEntityId(), Is.EqualTo(assigned));
        Assert.That(area.TryGetCaptureData(out _), Is.True);

        Object.DestroyImmediate(terrain);
        Assert.That(ReferenceEquals(area.Terrain, terrain), Is.True);
        Assert.That(!terrain, Is.True);
        Assert.That(area.Terrain.GetEntityId(), Is.EqualTo(assigned),
            "A destroyed assigned terrain must retain its nonzero entity identity.");
        Assert.That(fallback && collider, Is.True);
        Assert.That(area.TryGetCaptureData(out _), Is.False,
            "An assigned destroyed terrain must not fall back to the live mesh.");
        Assert.That(area.IntersectsCoverage(probe), Is.False);

        var cleared = new SerializedObject(area);
        cleared.FindProperty("terrain").objectReferenceValue = null;
        cleared.ApplyModifiedPropertiesWithoutUndo();
        Assert.That(!area.Terrain, Is.True);
        Assert.That(area.PaintSurface, Is.SameAs(fallback));
        Assert.That(area.TryGetCaptureData(out _), Is.True,
            "Explicitly clearing the terrain permits the authored mesh binding.");
        Assert.That(area.IntersectsCoverage(probe), Is.True);
    }

    [Test]
    public void VirtualPathProbesRejectARealHoleInsideOneMeshColliderBounds()
    {
        var mesh = new Mesh
        {
            vertices = new[]
            {
                new Vector3(-3f, 0f, -1f), new Vector3(-3f, 0f, 1f), new Vector3(-1f, 0f, 1f), new Vector3(-1f, 0f, -1f),
                new Vector3(1f, 0f, -1f), new Vector3(1f, 0f, 1f), new Vector3(3f, 0f, 1f), new Vector3(3f, 0f, -1f)
            },
            triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 }
        };
        mesh.RecalculateBounds();
        assets.Add(mesh);
        var floor = NewObject("floor with a gap").AddComponent<MeshCollider>();
        floor.sharedMesh = mesh;
        CapsuleCollider actor = NewCapsule(new Vector3(-2f, 1f, 0f));
        Physics.SyncTransforms();
        GrassInteractorShape shape = Shape(actor);
        var resolver = new GrassInteractorSupport();
        Assert.That(resolver.TryResolve(shape, floor, 0.1f, 0.05f, out _, out _), Is.True);
        Assert.That(resolver.TryResolve(shape.WithPose(new Vector3(2f, 1f, 0f), 0.5f, 2f), floor,
            0.1f, 0.05f, out _, out _), Is.True);
        Assert.That(floor.bounds.Contains(Vector3.zero), Is.True);
        Assert.That(resolver.TryResolve(shape.WithPose(Vector3.up, 0.5f, 2f), floor,
            0.1f, 0.05f, out _, out var status), Is.False);
        Assert.That(status, Is.EqualTo(GrassInteractorSupportStatus.Ungrounded));
        Assert.That(actor.transform.position, Is.EqualTo(new Vector3(-2f, 1f, 0f)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FullCastOrInitialOverlapBuffersRejectAndTheSameResolverRecovers(bool overflowOverlaps)
    {
        BoxCollider floor = NewFloor(Vector3.zero, new Vector2(10f, 10f));
        CapsuleCollider actor = NewCapsule(Vector3.up);
        var extras = new List<GameObject>();
        for (int i = 0; i < 5; i++)
        {
            if (overflowOverlaps)
            {
                GameObject helper = NewObject("self overlap " + i);
                helper.transform.SetParent(actor.transform, false);
                helper.transform.localPosition = Vector3.down * 0.4f;
                helper.AddComponent<BoxCollider>().size = Vector3.one * 0.2f;
                extras.Add(helper);
            }
            else
                extras.Add(NewFloor(Vector3.down * (0.02f * (i + 1)), new Vector2(10f, 10f)).gameObject);
        }
        Physics.SyncTransforms();
        var resolver = new GrassInteractorSupport(4);
        Assert.That(resolver.TryResolve(Shape(actor), floor, 0.1f, 0.05f, out _, out var status), Is.False);
        Assert.That(status, Is.EqualTo(GrassInteractorSupportStatus.QueryOverflow));
        foreach (GameObject extra in extras)
            Object.DestroyImmediate(extra);
        Physics.SyncTransforms();
        Assert.That(resolver.TryResolve(Shape(actor), floor, 0.1f, 0.05f, out var sample, out _), Is.True);
        Assert.That(sample.Support, Is.SameAs(floor));
    }

    [Test]
    public void InitialForeignOverlapCannotSupplyAFakeGroundPoint()
    {
        BoxCollider floor = NewFloor(Vector3.zero, new Vector2(10f, 10f));
        CapsuleCollider actor = NewCapsule(Vector3.up * 0.8f);
        Physics.SyncTransforms();
        Assert.That(new GrassInteractorSupport().TryResolve(Shape(actor), floor, 0.1f, 0.05f,
            out _, out var status), Is.False);
        Assert.That(status, Is.EqualTo(GrassInteractorSupportStatus.InitialOverlap));
    }

    [Test]
    public void OwnershipCandidateBudgetRejectsBeforeTruncatingAndClearsStaleCandidates()
    {
        BoxCollider floor = NewFloor(Vector3.zero, new Vector2(10f, 10f));
        var areas = new List<GrassPlacementArea>();
        for (int i = 0; i < 5; i++)
            areas.Add(NewArea(floor, Vector3.zero, new Vector2(10f, 10f)));
        CapsuleCollider actor = NewCapsule(Vector3.up);
        Physics.SyncTransforms();
        var resolver = new GrassInteractorSupport(4);
        Assert.That(resolver.TryResolve(Shape(actor), null, 0.1f, 0.05f, out _, out var status), Is.False);
        Assert.That(status, Is.EqualTo(GrassInteractorSupportStatus.QueryOverflow));
        areas[4].enabled = false;
        areas[3].enabled = false;
        Assert.That(resolver.TryResolve(Shape(actor), null, 0.1f, 0.05f, out _, out _), Is.True);
    }

    [Test]
    public void DisabledTriggerDestroyedAndForeignSceneExplicitSupportsFailClosed()
    {
        BoxCollider floor = NewFloor(Vector3.zero, new Vector2(10f, 10f));
        CapsuleCollider actor = NewCapsule(Vector3.up);
        Physics.SyncTransforms();
        GrassInteractorShape shape = Shape(actor);
        var resolver = new GrassInteractorSupport();
        floor.enabled = false;
        AssertInvalidSupport();
        floor.enabled = true;
        floor.isTrigger = true;
        AssertInvalidSupport();
        floor.isTrigger = false;
        SceneManager.MoveGameObjectToScene(floor.gameObject, NewPhysicsScene());
        Physics.SyncTransforms();
        AssertInvalidSupport();
        SceneManager.MoveGameObjectToScene(floor.gameObject, scene);
        Physics.SyncTransforms();
        Assert.That(resolver.TryResolve(shape, floor, 0.1f, 0.05f, out _, out _), Is.True);
        Object.DestroyImmediate(floor);
        AssertInvalidSupport();

        void AssertInvalidSupport()
        {
            Assert.That(resolver.TryResolve(shape, floor, 0.1f, 0.05f, out _, out var status), Is.False);
            Assert.That(status, Is.EqualTo(GrassInteractorSupportStatus.InvalidSupport));
        }
    }

    [Test]
    public void SupportSignatureTracksPoseGeometryAndDisableWithoutRetainingDeadNativeObjects()
    {
        BoxCollider floor = NewFloor(Vector3.zero, new Vector2(10f, 10f));
        Physics.SyncTransforms();
        int initial = GrassInteractorSupport.GetSupportSignature(floor);
        Assert.That(initial, Is.Not.Zero);
        Assert.That(GrassInteractorSupport.GetSupportSignature(floor), Is.EqualTo(initial));
        floor.transform.position += Vector3.right;
        Physics.SyncTransforms();
        int moved = GrassInteractorSupport.GetSupportSignature(floor);
        Assert.That(moved, Is.Not.EqualTo(initial));
        floor.size = new Vector3(0.8f, 1f, 1f);
        Physics.SyncTransforms();
        Assert.That(GrassInteractorSupport.GetSupportSignature(floor), Is.Not.EqualTo(moved));
        floor.enabled = false;
        Assert.That(GrassInteractorSupport.IsUsableSupport(floor), Is.False);
        Assert.That(GrassInteractorSupport.GetSupportSignature(floor), Is.Zero);
        Object.DestroyImmediate(floor);
        Assert.That(GrassInteractorSupport.GetSupportSignature(floor), Is.Zero);
    }

    [Test]
    public void InvalidVirtualShapesAndSettingsRejectBeforeNativeQueries()
    {
        CapsuleCollider actor = NewCapsule(Vector3.up);
        GrassInteractorShape shape = Shape(actor);
        var resolver = new GrassInteractorSupport();
        Assert.That(resolver.TryResolve(shape.WithPose(shape.Center, float.NaN, 2f), null,
            0.1f, 0.05f, out _, out var status), Is.False);
        Assert.That(status, Is.EqualTo(GrassInteractorSupportStatus.InvalidShape));
        Assert.That(resolver.TryResolve(shape.WithPose(shape.Center, 1f, 1f), null,
            0.1f, 0.05f, out _, out status), Is.False);
        Assert.That(status, Is.EqualTo(GrassInteractorSupportStatus.InvalidShape));
        Assert.That(resolver.TryResolve(shape, null, 0.1f, 0.05f, out _, out status, float.PositiveInfinity), Is.False);
        Assert.That(status, Is.EqualTo(GrassInteractorSupportStatus.InvalidSettings));
        actor.enabled = false;
        Assert.That(resolver.TryResolve(shape, null, 0.1f, 0.05f, out _, out status), Is.False);
        Assert.That(status, Is.EqualTo(GrassInteractorSupportStatus.InactiveActor));
    }

    [Test]
    public void WarmedSupportQueriesReuseTheirManagedBuffers()
    {
        BoxCollider floor = NewFloor(Vector3.zero, new Vector2(10f, 10f));
        NewArea(floor, Vector3.zero, new Vector2(10f, 10f));
        CapsuleCollider actor = NewCapsule(Vector3.up);
        Physics.SyncTransforms();
        GrassInteractorShape shape = Shape(actor);
        var resolver = new GrassInteractorSupport();
        for (int i = 0; i < 8; i++)
            Assert.That(resolver.TryResolve(shape, null, 0.1f, 0.05f, out _, out _), Is.True);
        int accepted = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 16; i++)
            if (resolver.TryResolve(shape, null, 0.1f, 0.05f, out _, out _))
                accepted++;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.That(accepted, Is.EqualTo(16));
        Assert.That(allocated, Is.Zero);
    }

    private Scene NewPhysicsScene()
    {
        Scene created = GrassPhysicsFixtureScenes.Create("Grass support " + Guid.NewGuid().ToString("N"));
        scenes.Add(created);
        return created;
    }

    private GameObject NewObject(string name)
    {
        var item = new GameObject(name);
        SceneManager.MoveGameObjectToScene(item, scene);
        return item;
    }

    private CapsuleCollider NewCapsule(Vector3 center)
    {
        var collider = NewObject("actor capsule").AddComponent<CapsuleCollider>();
        collider.radius = 0.5f;
        collider.height = 2f;
        collider.center = Vector3.zero;
        collider.transform.position = center;
        return collider;
    }

    private BoxCollider NewFloor(Vector3 topCenter, Vector2 size)
    {
        GameObject item = GameObject.CreatePrimitive(PrimitiveType.Cube);
        item.name = "support floor";
        SceneManager.MoveGameObjectToScene(item, scene);
        item.transform.position = topCenter - Vector3.up * 0.25f;
        item.transform.localScale = new Vector3(size.x, 0.5f, size.y);
        return item.GetComponent<BoxCollider>();
    }

    private GrassPlacementArea NewArea(Collider support, Vector3 center, Vector2 size)
    {
        var area = NewObject("grass area").AddComponent<GrassPlacementArea>();
        area.transform.position = center;
        var serialized = new SerializedObject(area);
        serialized.FindProperty("shape").enumValueIndex = (int)GrassPlacementShape.Box;
        serialized.FindProperty("size").vector2Value = size;
        serialized.FindProperty("edgeFalloff").floatValue = 0f;
        serialized.FindProperty("paintSurface").objectReferenceValue = support;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        return area;
    }

    private Terrain NewTerrain(Vector3 origin, Vector3 size)
    {
        var data = new TerrainData { heightmapResolution = 33, size = size };
        assets.Add(data);
        GameObject item = Terrain.CreateTerrainGameObject(data);
        SceneManager.MoveGameObjectToScene(item, scene);
        item.transform.position = origin;
        return item.GetComponent<Terrain>();
    }

    private GrassPlacementArea NewTerrainArea(Terrain terrain)
    {
        var area = NewObject("terrain grass area").AddComponent<GrassPlacementArea>();
        area.ConfigureTexture(terrain, Texture2D.whiteTexture);
        return area;
    }

    private static void SetExternalCrop(GrassPlacementArea area, Rect crop)
    {
        var serialized = new SerializedObject(area);
        serialized.FindProperty("shape").enumValueIndex = (int)GrassPlacementShape.Texture;
        serialized.FindProperty("densityTexture").objectReferenceValue = Texture2D.whiteTexture;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        area.SetTextureCoverageBounds(crop);
    }

    private static GrassInteractorShape Shape(Collider actor)
    {
        Assert.That(GrassInteractorSupport.TryGetShape(actor, actor.transform, out var shape, out var status), Is.True,
            "Shape setup failed with " + status);
        return shape;
    }
}

// LocalPhysicsMode.Physics3D is a runtime API. One entry per fixture preserves
// all existing collision cases without paying for an entry per individual case.
public abstract class GrassPhysicsFixtureTests
{
    private bool previousPause;

    [UnityOneTimeSetUp]
    public IEnumerator EnterPhysicsSession()
    {
        // Read the owner's options; never change them to make a fixture pass.
        bool reload = !EditorSettings.enterPlayModeOptionsEnabled ||
            (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) == 0;
        yield return new EnterPlayMode(expectDomainReload: reload);
        previousPause = EditorApplication.isPaused;
        // These fixtures drive physics queries and sampling explicitly. Pausing
        // also preserves their original immediate Editor resource-release checks:
        // URP CoreUtils.Destroy uses DestroyImmediate in a paused Editor session.
        EditorApplication.isPaused = true;
        GrassPhysicsFixtureScenes.Begin();
        GrassPhysicsFixtureScenes.ProveIsolation();
    }

    [UnityOneTimeTearDown]
    public IEnumerator ExitPhysicsSession()
    {
        if (!Application.isPlaying)
        {
            EditorApplication.isPaused = previousPause;
            Assert.Fail("The shared physics session ended before its owned-scene cleanup was verified.");
        }
        // Let runtime scene unloads progress without changing any Editor setting.
        EditorApplication.isPaused = false;
        string cleanupFailure = null;
        IEnumerator drain = GrassPhysicsFixtureScenes.Drain();
        while (true)
        {
            bool next;
            try { next = drain.MoveNext(); }
            catch (Exception error) { cleanupFailure = error.ToString(); break; }
            if (!next)
                break;
            yield return drain.Current;
        }
        try { cleanupFailure = GrassPhysicsFixtureScenes.CheckCleanup() ?? cleanupFailure; }
        catch (Exception error) { cleanupFailure = error.ToString(); }
        // Even a cleanup assertion must return the Editor to its original mode.
        EditorApplication.isPaused = previousPause;
        yield return new ExitPlayMode();
        EditorApplication.isPaused = previousPause;
        Assert.That(cleanupFailure, Is.Null, cleanupFailure);
    }
}

internal static class GrassPhysicsFixtureScenes
{
    private sealed class OwnedScene
    {
        internal Scene Scene;
        internal PhysicsScene Physics;
        internal bool Local;
        internal AsyncOperation Unload;
    }

    private static readonly List<OwnedScene> owned = new List<OwnedScene>();
    private static Scene active;
    private static Scene[] existing;
    private static HashSet<Object> nativeResources;
    private static Dictionary<GrassColliderInteractor, bool> existingProducers;

    internal static void Begin()
    {
        Assert.That(owned, Is.Empty, "The previous fixture must release all of its scenes.");
        active = SceneManager.GetActiveScene();
        existing = new Scene[SceneManager.sceneCount];
        for (int i = 0; i < existing.Length; i++)
            existing[i] = SceneManager.GetSceneAt(i);
        nativeResources = InteractionResources();
        existingProducers = new Dictionary<GrassColliderInteractor, bool>();
        foreach (GrassColliderInteractor producer in Resources.FindObjectsOfTypeAll<GrassColliderInteractor>())
            if (producer && producer.gameObject.scene.IsValid() && producer.gameObject.scene.isLoaded)
                existingProducers.Add(producer, producer.enabled);
    }

    internal static Scene Create(string name) => Create(name, LocalPhysicsMode.Physics3D);

    private static Scene Create(string name, LocalPhysicsMode mode)
    {
        Assert.That(Application.isPlaying, Is.True, "Enter the shared fixture Play session before creating a local physics world.");
        Scene scene = SceneManager.CreateScene(name, new CreateSceneParameters(mode));
        var entry = new OwnedScene { Scene = scene, Physics = scene.GetPhysicsScene(), Local = mode == LocalPhysicsMode.Physics3D };
        owned.Add(entry); // Own it before an assertion can fail.
        Assert.That(EditorSceneManager.IsPreviewScene(scene), Is.False);
        Assert.That(entry.Physics.IsValid(), Is.True);
        if (entry.Local)
        {
            Assert.That(entry.Physics, Is.Not.EqualTo(Physics.defaultPhysicsScene));
            foreach (OwnedScene other in owned)
                if (other != entry && other.Scene.IsValid() && other.Scene.isLoaded)
                    Assert.That(entry.Physics, Is.Not.EqualTo(other.Physics), "Each fixture scene needs a distinct native physics world.");
        }
        Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(active), "Creating a fixture must preserve the active scene.");
        return scene;
    }

    internal static void ProveIsolation()
    {
        Scene first = Create("Grass physics isolation A " + Guid.NewGuid().ToString("N"));
        Scene second = Create("Grass physics isolation B " + Guid.NewGuid().ToString("N"));
        Scene shared = Create("Grass default physics control " + Guid.NewGuid().ToString("N"), LocalPhysicsMode.None);
        Vector3 point = new Vector3(12345f, 678f, -12345f);
        try
        {
            Collider a = Probe(first, point);
            Collider b = Probe(second, point);
            Collider control = Probe(shared, point);
            Physics.SyncTransforms();
            Vector3 origin = point + Vector3.up * 2f;
            Assert.That(first.GetPhysicsScene().Raycast(origin, Vector3.down, out RaycastHit hitA, 3f), Is.True);
            Assert.That(hitA.collider, Is.SameAs(a), "World A must not query the coincident sibling or default-world collider.");
            Assert.That(second.GetPhysicsScene().Raycast(origin, Vector3.down, out RaycastHit hitB, 3f), Is.True);
            Assert.That(hitB.collider, Is.SameAs(b), "World B must not query the coincident sibling or default-world collider.");
            Assert.That(shared.GetPhysicsScene(), Is.EqualTo(Physics.defaultPhysicsScene), "The non-local control must actually share the default world.");
            Assert.That(Physics.defaultPhysicsScene.Raycast(origin, Vector3.down, out RaycastHit hitDefault, 3f), Is.True);
            Assert.That(hitDefault.collider, Is.SameAs(control), "The default world must not contain either local collider.");
            Assert.That(GrassInteractorSupport.TryGetShape(a, a.transform, out var shape, out _), Is.True);
            Assert.That(shape.PhysicsScene, Is.EqualTo(first.GetPhysicsScene()), "Production support must accept the non-preview isolated scene.");
        }
        finally
        {
            Close(shared);
            Close(second);
            Close(first);
        }
    }

    private static Collider Probe(Scene scene, Vector3 point)
    {
        var item = new GameObject("Grass native physics isolation probe");
        SceneManager.MoveGameObjectToScene(item, scene);
        item.transform.position = point;
        return item.AddComponent<SphereCollider>();
    }

    internal static void Close(Scene scene)
    {
        OwnedScene entry = owned.Find(item => item.Scene == scene);
        Assert.That(entry, Is.Not.Null, "Only the current fixture's owned scene may be unloaded.");
        if (entry.Unload != null || !scene.IsValid() || !scene.isLoaded)
            return;
        // Runtime scene unload is asynchronous. Remove components/registrations now,
        // then await the scene and deferred native-resource releases before exit.
        foreach (GameObject root in scene.GetRootGameObjects())
            Object.DestroyImmediate(root);
        entry.Unload = SceneManager.UnloadSceneAsync(scene);
        Assert.That(entry.Unload, Is.Not.Null);
    }

    internal static IEnumerator Drain()
    {
        foreach (OwnedScene entry in owned)
            Close(entry.Scene);
        foreach (OwnedScene entry in owned)
            if (entry.Unload != null)
                while (!entry.Unload.isDone)
                    yield return null;
        yield return null; // CoreUtils.Destroy uses runtime deferred destruction.
    }

    internal static string CheckCleanup()
    {
        string failure = null;
        foreach (OwnedScene entry in owned)
            if ((entry.Scene.IsValid() && entry.Scene.isLoaded) || (entry.Local && entry.Physics.IsValid()))
                failure = "An owned scene or its isolated native physics world survived fixture teardown.";
        if (SceneManager.GetActiveScene() != active || SceneManager.sceneCount != existing.Length)
            failure = "Fixture teardown changed the pre-existing active scene or loaded scene count.";
        for (int i = 0; i < existing.Length && i < SceneManager.sceneCount; i++)
            if (SceneManager.GetSceneAt(i) != existing[i])
                failure = "Fixture teardown changed a pre-existing scene's identity/order.";
        var remaining = InteractionResources();
        remaining.ExceptWith(nativeResources);
        foreach (var previous in existingProducers)
        {
            if (!previous.Key || previous.Key.enabled != previous.Value)
                failure = "Fixture teardown did not restore a pre-existing grass producer's enabled state.";
            else
            {
                // Existing tests temporarily disable producers. Their own resources
                // may be recreated, but a fixture must leave no unowned native pair.
                var resources = typeof(GrassColliderInteractor).GetField("interactionMesh", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(previous.Key) as GrassInteractionMesh;
                if (resources != null)
                {
                    if (resources.Mesh) remaining.Remove(resources.Mesh);
                    if (resources.Material) remaining.Remove(resources.Material);
                }
            }
        }
        if (remaining.Count != 0)
            failure = "Fixture teardown leaked an unowned grass interaction mesh/material.";
        owned.Clear();
        existing = null;
        nativeResources = null;
        existingProducers = null;
        return failure;
    }

    private static HashSet<Object> InteractionResources()
    {
        var ids = new HashSet<Object>();
        foreach (Mesh mesh in Resources.FindObjectsOfTypeAll<Mesh>())
            if (mesh.name == "Grass Collider Interaction")
                ids.Add(mesh);
        foreach (Material material in Resources.FindObjectsOfTypeAll<Material>())
            if (material.name == "Grass Collider Interaction")
                ids.Add(material);
        return ids;
    }
}
