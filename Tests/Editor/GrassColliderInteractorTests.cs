using System;
using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

public sealed class GrassColliderInteractorTests
{
    private const float LiveStrength = 0.85f;
    private GrassPlacementArea[] previousAreas;
    private GrassColliderInteractor[] previousInteractors;
    private bool[] previousInteractorEnabled;

    [SetUp]
    public void IsolateLoadedGrassCoverage()
    {
        previousInteractors = new GrassColliderInteractor[GrassColliderInteractor.ActiveInteractors.Count];
        previousInteractorEnabled = new bool[previousInteractors.Length];
        for (int i = 0; i < previousInteractors.Length; i++)
        {
            previousInteractors[i] = GrassColliderInteractor.ActiveInteractors[i];
            previousInteractorEnabled[i] = previousInteractors[i] && previousInteractors[i].enabled;
        }
        for (int i = 0; i < previousInteractors.Length; i++)
            if (previousInteractors[i])
                previousInteractors[i].enabled = false;
        previousAreas = new GrassPlacementArea[GrassPlacementArea.ActiveAreas.Count];
        for (int i = 0; i < previousAreas.Length; i++)
            previousAreas[i] = GrassPlacementArea.ActiveAreas[i];
        for (int i = 0; i < previousAreas.Length; i++)
            if (previousAreas[i])
                previousAreas[i].enabled = false;
    }

    [TearDown]
    public void RestoreLoadedGrassCoverage()
    {
        if (previousAreas != null)
            for (int i = 0; i < previousAreas.Length; i++)
                if (previousAreas[i])
                    previousAreas[i].enabled = true;
        previousAreas = null;
        // Coverage is restored before producers are re-enabled and resubscribed.
        if (previousInteractors != null)
            for (int i = 0; i < previousInteractors.Length; i++)
                if (previousInteractors[i])
                    previousInteractors[i].enabled = previousInteractorEnabled[i];
        previousInteractors = null;
        previousInteractorEnabled = null;
    }

    [Test]
    public void ActualColliderCreatesCameraIndependentOutputAndReusesItsOwnedResources()
    {
        using (var fixture = new InteractorFixture())
        {
            fixture.Advance(0.0);
            Assert.That(fixture.Interactor.LastSupportStatus, Is.EqualTo(GrassInteractorSupportStatus.Supported));
            Assert.That(fixture.Interactor.State, Is.EqualTo(GrassColliderInteractor.InteractionState.Active));
            Assert.That(fixture.Interactor.ActiveNodeCount, Is.GreaterThan(0));
            Assert.That(fixture.Interactor.TryGetDraw(out Mesh mesh, out Material material, out _), Is.True);
            Assert.That(mesh.vertexCount, Is.GreaterThan(0));
            Assert.That(material.FindPass("GrassSlope"), Is.GreaterThanOrEqualTo(0));
            Assert.That(fixture.Actor.GetComponent<Renderer>(), Is.Null);
            Assert.That(fixture.Actor.transform.childCount, Is.Zero,
                "The producer registers its mesh directly instead of creating a helper Renderer or collider.");
            Assert.That(fixture.Scene.GetRootGameObjects().Length, Is.EqualTo(2),
                "This fixture contains only the real actor and supporting floor, with no camera.");
            uint revision = fixture.Interactor.DrawRevision;
            Assert.That(fixture.Field.TryGetNode(Vector2Int.zero, 0.0, 2f, out _, out float weight), Is.True);
            Assert.That(weight, Is.EqualTo(LiveStrength));
            Assert.That(fixture.Interactor.ContactStrength, Is.EqualTo(LiveStrength));

            fixture.Actor.transform.position += Vector3.right * 0.2f;
            fixture.Advance(1.0 / 30.0);

            Assert.That(fixture.Interactor.TryGetDraw(out Mesh updatedMesh, out Material updatedMaterial, out _), Is.True);
            Assert.That(updatedMesh, Is.SameAs(mesh));
            Assert.That(updatedMaterial, Is.SameAs(material));
            Assert.That(fixture.Interactor.DrawRevision, Is.Not.EqualTo(revision));
            Assert.That(CountRegistration(fixture.Interactor), Is.EqualTo(1));
            LogAssert.NoUnexpectedReceived();
        }
    }

    [Test]
    public void ManualModeUsesOnlyExplicitObservations()
    {
        using (var fixture = new InteractorFixture())
        {
            fixture.Advance(0.0);
            fixture.Actor.transform.position += Vector3.right * 2f;
            uint revision = fixture.Interactor.DrawRevision;
            Invoke(fixture.Interactor, "EditorUpdate");
            Assert.That(fixture.Interactor.DrawRevision, Is.EqualTo(revision));
            Assert.That(fixture.Field.TryGetNode(new Vector2Int(13, 0), 0.0, 2f, out _, out _), Is.False);

            fixture.Interactor.Sample(0.1);

            Assert.That(fixture.Interactor.DrawRevision, Is.Not.EqualTo(revision));
            Assert.That(fixture.Field.TryGetNode(new Vector2Int(13, 0), 0.1, 2f, out _, out float weight), Is.True);
            Assert.That(weight, Is.EqualTo(LiveStrength));
        }
    }

    [Test]
    public void StationaryContactRefreshesExistingNodesAndDepartureRecoversToFinalRemoval()
    {
        using (var fixture = new InteractorFixture())
        {
            fixture.Advance(0.0);
            int initialCount = fixture.Interactor.ActiveNodeCount;
            Assert.That(initialCount, Is.GreaterThan(0));
            Assert.That(fixture.Interactor.TryGetDraw(out Mesh mesh, out Material material, out _), Is.True);
            for (int step = 1; step <= 30; step++)
                fixture.Advance(step * 0.1);
            Assert.That(fixture.Interactor.ActiveNodeCount, Is.EqualTo(initialCount),
                "A stopped actor refreshes last contact rather than appending overlapping stamps.");
            Assert.That(fixture.Field.TryGetNode(Vector2Int.zero, 3.0, 2f, out _, out float liveWeight), Is.True);
            Assert.That(liveWeight, Is.EqualTo(LiveStrength));

            fixture.Actor.transform.position += Vector3.up * 3f;
            fixture.Advance(3.05);
            Assert.That(fixture.Interactor.State, Is.EqualTo(GrassColliderInteractor.InteractionState.Recovering));
            Assert.That(fixture.Field.TryGetNode(Vector2Int.zero, 3.05, 2f, out _, out float departureWeight), Is.True);
            Assert.That(departureWeight, Is.EqualTo(LiveStrength * GrassInteractionField.RecoveryWeight(0.05, 2f)).Within(0.00001f));
            fixture.Advance(4.0);
            Assert.That(fixture.Field.TryGetNode(Vector2Int.zero, 4.0, 2f, out _, out float halfwayWeight), Is.True);
            Assert.That(halfwayWeight, Is.EqualTo(LiveStrength * 0.5f).Within(0.00001f));
            uint recoveryRevision = fixture.Interactor.DrawRevision;

            fixture.Advance(5.0);

            Assert.That(fixture.Interactor.ActiveNodeCount, Is.Zero);
            Assert.That(fixture.Interactor.TryGetDraw(out _, out _, out _), Is.False);
            Assert.That(fixture.Interactor.DrawRevision, Is.Not.EqualTo(recoveryRevision),
                "Final removal must invalidate a previously captured slope map.");
            Assert.That(mesh != null && material != null, Is.True, "Expiry retains resources for the next contact.");
            Assert.That(mesh.vertexCount, Is.Zero);
            fixture.Actor.transform.position -= Vector3.up * 3f;
            fixture.Advance(5.05);
            Assert.That(fixture.Interactor.TryGetDraw(out Mesh renewedMesh, out Material renewedMaterial, out _), Is.True);
            Assert.That(renewedMesh, Is.SameAs(mesh));
            Assert.That(renewedMaterial, Is.SameAs(material));
        }
    }

    [Test]
    public void JumpAndLandingDoNotJoinTheAirbornePath()
    {
        using (var fixture = new InteractorFixture())
        {
            fixture.Advance(0.0);
            fixture.Actor.transform.position = new Vector3(1f, 3.9f, 0f);
            fixture.Advance(0.1);
            Assert.That(fixture.Interactor.State, Is.EqualTo(GrassColliderInteractor.InteractionState.Recovering));
            fixture.Actor.transform.position = new Vector3(2f, 0.9f, 0f);
            fixture.Advance(0.2);

            Assert.That(fixture.Interactor.State, Is.EqualTo(GrassColliderInteractor.InteractionState.Active));
            Assert.That(fixture.Field.TryGetNode(Vector2Int.zero, 0.2, 2f, out _, out float oldWeight), Is.True);
            Assert.That(oldWeight, Is.LessThan(LiveStrength), "The departed footprint continues recovering.");
            Assert.That(fixture.Field.TryGetNode(new Vector2Int(7, 0), 0.2, 2f, out _, out _), Is.False,
                "The node at x=1.05 lies between the two ground contacts and was never touched on the ground.");
            Assert.That(fixture.Field.TryGetNode(new Vector2Int(13, 0), 0.2, 2f, out _, out float landingWeight), Is.True);
            Assert.That(landingWeight, Is.EqualTo(LiveStrength));

            fixture.Actor.transform.position += Vector3.right * 0.2f;
            fixture.Advance(0.3);
            Assert.That(fixture.Field.TryGetNode(new Vector2Int(7, 0), 0.3, 2f, out _, out _), Is.False,
                "The next grounded step must start at landing rather than reconnect to the takeoff anchor.");
            Assert.That(fixture.Field.TryGetNode(Vector2Int.zero, 0.3, 2f, out _, out float takeoffWeight), Is.True);
            Assert.That(takeoffWeight, Is.LessThan(oldWeight), "Takeoff history keeps recovering without a new contact.");
            Assert.That(fixture.Field.TryGetNode(new Vector2Int(15, 0), 0.3, 2f, out _, out float stepWeight), Is.True);
            Assert.That(stepWeight, Is.EqualTo(LiveStrength));
        }
    }

    [TestCase("teleport", 9f, 0.1)]
    [TestCase("long observation gap", 2f, 0.5)]
    [TestCase("clock reversal", 2f, -0.1)]
    [TestCase("explicit session reset", 2f, 0.1)]
    public void DiscontinuitiesClearOldHistoryAndSeedOnlyTheCurrentFootprint(string reason, float x, double now)
    {
        using (var fixture = new InteractorFixture())
        {
            fixture.Advance(0.0);
            Assert.That(fixture.Field.TryGetNode(Vector2Int.zero, 0.0, 2f, out _, out _), Is.True);
            if (reason == "explicit session reset")
            {
                fixture.Interactor.ClearHistory();
                Assert.That(fixture.Interactor.ActiveNodeCount, Is.Zero);
                Assert.That(fixture.Interactor.TryGetDraw(out _, out _, out _), Is.False);
            }
            fixture.Actor.transform.position = new Vector3(x, 0.9f, 0f);
            fixture.Advance(now);

            Assert.That(fixture.Interactor.ActiveNodeCount, Is.GreaterThan(0));
            Assert.That(fixture.Field.TryGetNode(Vector2Int.zero, now, 2f, out _, out _), Is.False, reason);
            var middle = new Vector2Int(Mathf.RoundToInt(x * 0.5f / fixture.Field.CellSize), 0);
            Assert.That(fixture.Field.TryGetNode(middle, now, 2f, out _, out _), Is.False, reason);
            var current = new Vector2Int(Mathf.RoundToInt(x / fixture.Field.CellSize), 0);
            Assert.That(fixture.Field.TryGetNode(current, now, 2f, out _, out float weight), Is.True);
            Assert.That(weight, Is.EqualTo(LiveStrength));
            LogAssert.NoUnexpectedReceived();
        }
    }

    [Test]
    public void LiveColliderRadiusChangesAlterTheFootprintAndPreserveActualDepartureRecovery()
    {
        using (var fixture = new InteractorFixture())
        {
            fixture.Advance(0.0);
            var diagonal = new Vector2Int(2, 2);
            Assert.That(fixture.Field.TryGetNode(diagonal, 0.0, 2f, out _, out _), Is.False);
            fixture.Collider.radius = 0.45f;
            fixture.Advance(0.1);
            Assert.That(fixture.Field.TryGetNode(diagonal, 0.1, 2f, out _, out float grownWeight), Is.True);
            Assert.That(grownWeight, Is.EqualTo(LiveStrength));

            fixture.Collider.radius = 0.15f;
            fixture.Advance(0.2);
            Assert.That(fixture.Field.TryGetNode(diagonal, 0.2, 2f, out _, out float departedWeight), Is.True);
            Assert.That(departedWeight, Is.LessThan(LiveStrength));
            Assert.That(departedWeight, Is.GreaterThan(LiveStrength * GrassInteractionField.RecoveryWeight(0.1, 2f)),
                "A shrinking collider leaves this node during the sweep, after the previous center observation.");
            Assert.That(fixture.Field.TryGetNode(Vector2Int.zero, 0.2, 2f, out _, out float liveWeight), Is.True);
            Assert.That(liveWeight, Is.EqualTo(LiveStrength));
        }
    }

    [Test]
    public void UnsupportedHistoryCapacityResetsTransactionallyToTheWholeCurrentFootprint()
    {
        using (var fixture = new InteractorFixture())
        {
            Set(fixture.Interactor, "nodeCapacity", 16);
            fixture.Advance(0.0);
            Assert.That(fixture.Interactor.ActiveNodeCount, Is.InRange(1, 16));
            fixture.Actor.transform.position += Vector3.right;
            LogAssert.Expect(LogType.Warning, new Regex("^Grass interaction exceeded its bounded history, sweep or support-query budget\\."));
            fixture.Advance(0.1);

            Assert.That(fixture.Interactor.State, Is.EqualTo(GrassColliderInteractor.InteractionState.PathReset));
            Assert.That(fixture.Interactor.ActiveNodeCount, Is.InRange(1, 16));
            Assert.That(fixture.Field.TryGetNode(Vector2Int.zero, 0.1, 2f, out _, out _), Is.False);
            Assert.That(fixture.Field.TryGetNode(new Vector2Int(3, 0), 0.1, 2f, out _, out _), Is.False,
                "Overflow cannot retain a truncated prefix of the attempted path.");
            Assert.That(fixture.Field.TryGetNode(new Vector2Int(7, 0), 0.1, 2f, out _, out float weight), Is.True);
            Assert.That(weight, Is.EqualTo(LiveStrength));
            LogAssert.NoUnexpectedReceived();
        }
    }

    [Test]
    public void ACurrentFootprintThatCannotFitClearsOutputAndDebouncesItsDiagnostic()
    {
        using (var fixture = new InteractorFixture())
        {
            Set(fixture.Interactor, "nodeCapacity", 16);
            fixture.Advance(0.0);
            Assert.That(fixture.Interactor.TryGetDraw(out Mesh mesh, out _, out _), Is.True);
            fixture.Collider.height = 4f;
            fixture.Collider.radius = 2f;
            fixture.Actor.transform.position = Vector3.up * 2f;
            LogAssert.Expect(LogType.Warning,
                "The current footprint exceeds the interaction budget. Increase grid spacing or reduce the collider footprint.");
            fixture.Advance(0.1);
            Assert.That(fixture.Interactor.State, Is.EqualTo(GrassColliderInteractor.InteractionState.TrailBudgetExceeded));
            Assert.That(fixture.Interactor.ActiveNodeCount, Is.Zero);
            Assert.That(mesh.vertexCount, Is.Zero);
            Assert.That(fixture.Interactor.TryGetDraw(out _, out _, out _), Is.False);
            fixture.Advance(0.2);
            Assert.That(fixture.Interactor.ActiveNodeCount, Is.Zero);
            LogAssert.NoUnexpectedReceived();
        }
    }

    [Test]
    public void SupportQueryBudgetResetsWithoutSilentlySkippingTheMiddleOfThePath()
    {
        using (var fixture = new InteractorFixture())
        {
            Set(fixture.Interactor, "gridSpacing", 0.05f);
            fixture.Collider.radius = 0.1f;
            fixture.Advance(0.0);
            fixture.Actor.transform.position += Vector3.right * 4f;
            LogAssert.Expect(LogType.Warning, new Regex("^Grass interaction exceeded its bounded history, sweep or support-query budget\\."));
            fixture.Advance(0.1);

            Assert.That(fixture.Interactor.State, Is.EqualTo(GrassColliderInteractor.InteractionState.PathReset));
            Assert.That(fixture.Field.TryGetNode(Vector2Int.zero, 0.1, 2f, out _, out _), Is.False);
            Assert.That(fixture.Field.TryGetNode(new Vector2Int(40, 0), 0.1, 2f, out _, out _), Is.False);
            Assert.That(fixture.Field.TryGetNode(new Vector2Int(80, 0), 0.1, 2f, out _, out float weight), Is.True);
            Assert.That(weight, Is.EqualTo(LiveStrength));
            LogAssert.NoUnexpectedReceived();
        }
    }

    [Test]
    public void SmallColliderProbesUseItsActualRadiusAndDoNotBridgeASameMeshHole()
    {
        using (var fixture = new InteractorFixture())
        {
            GameObject surface = null;
            Mesh mesh = null;
            try
            {
                surface = new GameObject("One supporting mesh with a narrow hole");
                SceneManager.MoveGameObjectToScene(surface, fixture.Scene);
                mesh = new Mesh { name = "Grass support probe hole" };
                mesh.vertices = new[]
                {
                    new Vector3(-1f, 0f, -1f), new Vector3(0.006f, 0f, -1f),
                    new Vector3(-1f, 0f, 1f), new Vector3(0.006f, 0f, 1f),
                    new Vector3(0.016f, 0f, -1f), new Vector3(1f, 0f, -1f),
                    new Vector3(0.016f, 0f, 1f), new Vector3(1f, 0f, 1f)
                };
                mesh.triangles = new[] { 0, 2, 1, 1, 2, 3, 4, 6, 5, 5, 6, 7 };
                var supportingCollider = surface.AddComponent<MeshCollider>();
                supportingCollider.sharedMesh = mesh;
                fixture.Ground.enabled = false;
                fixture.Collider.radius = 1f / 256f;
                fixture.Actor.transform.position = new Vector3(-1f / 256f, 0.9f, 0f);
                fixture.Interactor.Configure(fixture.Collider, supportingCollider);
                Set(fixture.Interactor, "gridSpacing", 0.01f);
                fixture.Advance(0.0);
                Assert.That(fixture.Interactor.LastSupportStatus, Is.EqualTo(GrassInteractorSupportStatus.Supported));

                Assert.That(GrassInteractorSupport.TryGetShape(fixture.Collider, fixture.Actor.transform,
                    out GrassInteractorShape shape, out _), Is.True);
                var resolver = new GrassInteractorSupport();
                GrassInteractorShape oldMidpoint = shape.WithPose(new Vector3(0.005859375f, 0.9f, 0f), shape.Radius, shape.Height);
                Assert.That(resolver.TryResolve(in oldMidpoint, supportingCollider, 0.15f, 0.1f, out _, out _), Is.True,
                    "A 0.01m minimum step tests only this grounded midpoint and misses the actual gap.");
                GrassInteractorShape unsupported = shape.WithPose(new Vector3(0.01171875f, 0.9f, 0f), shape.Radius, shape.Height);
                Assert.That(resolver.TryResolve(in unsupported, supportingCollider, 0.15f, 0.1f, out _, out _), Is.False,
                    "At an actual-radius probe the complete lower sphere is inside the mesh hole.");
                fixture.Actor.transform.position = new Vector3(4f / 256f, 0.9f, 0f);

                fixture.Advance(0.1);

                Assert.That(fixture.Interactor.LastSupportStatus, Is.EqualTo(GrassInteractorSupportStatus.Supported),
                    "The current endpoint still touches the same supporting mesh's right edge.");
                Assert.That(fixture.Field.TryGetNode(new Vector2Int(1, 0), 0.1, 2f, out _, out _), Is.False,
                    "The grid node at x=0.01 cannot come from a swept path whose intermediate support was lost.");
                Assert.That(fixture.Field.TryGetNode(Vector2Int.zero, 0.1, 2f, out _, out _), Is.True,
                    "The prior grounded footprint may continue its bounded recovery.");
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                if (surface)
                    Object.DestroyImmediate(surface);
                if (mesh)
                    Object.DestroyImmediate(mesh);
            }
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void MovingSupportClearsWorldHistoryWhileLiveAttackContinues(bool authoredSource)
    {
        using (var fixture = new InteractorFixture())
        {
            Mesh surfaceMesh = null;
            try
            {
                GrassPlacementArea area = null;
                if (authoredSource)
                {
                    surfaceMesh = new Mesh { name = "Moving authored grass floor" };
                    surfaceMesh.vertices = new[]
                    {
                        new Vector3(-5f, 0.5f, -5f), new Vector3(5f, 0.5f, -5f),
                        new Vector3(-5f, 0.5f, 5f), new Vector3(5f, 0.5f, 5f)
                    };
                    surfaceMesh.triangles = new[] { 0, 2, 1, 1, 2, 3 };
                    fixture.Ground.gameObject.AddComponent<MeshFilter>().sharedMesh = surfaceMesh;
                    fixture.Ground.gameObject.AddComponent<MeshRenderer>().sharedMaterials = new Material[1];
                    area = fixture.Ground.gameObject.AddComponent<GrassPlacementArea>();
                    Set(area, "shape", GrassPlacementShape.Box);
                    Set(area, "paintSurface", fixture.Ground);
                    Invoke(area, "RefreshState");
                }
                Set(fixture.Interactor, "attackDuration", 0.2f);
                fixture.Advance(0.0);
                Assert.That(fixture.Interactor.ContactStrength, Is.Zero);
                Assert.That(fixture.Interactor.ActiveNodeCount, Is.Zero);

                for (int step = 1; step <= 3; step++)
                {
                    float priorResponse = fixture.Interactor.ContactStrength;
                    int priorNodes = fixture.Interactor.ActiveNodeCount;
                    uint priorRevision = area ? area.SourceRevision : 0;
                    fixture.Ground.transform.position += Vector3.right;
                    fixture.Actor.transform.position += Vector3.right;
                    if (area)
                    {
                        // The area normally updates before the late-order interactor.
                        // Exercise its real event before the interactor sees the new support signature.
                        Invoke(area, "RefreshState");
                        Assert.That(area.SourceRevision, Is.Not.EqualTo(priorRevision));
                        if (priorNodes > 0)
                        {
                            Assert.That(fixture.Interactor.ActiveNodeCount, Is.Zero);
                            Assert.That(fixture.Interactor.ContactStrength, Is.EqualTo(priorResponse),
                                "Source invalidation retains only a candidate live response for the next validated observation.");
                        }
                    }
                    double now = step * 0.1;
                    fixture.Advance(now);
                    Assert.That(fixture.Interactor.LastSupportStatus, Is.EqualTo(GrassInteractorSupportStatus.Supported));
                    Assert.That(fixture.Interactor.ContactStrength, Is.GreaterThan(priorResponse));
                    Assert.That(fixture.Interactor.ContactStrength,
                        Is.EqualTo(GrassInteractionField.EvaluateAttack(0f, LiveStrength, now, 0.2f)).Within(0.000001f));
                    Assert.That(fixture.Interactor.TryGetDraw(out Mesh mesh, out _, out _), Is.True);
                    Assert.That(mesh.vertexCount, Is.GreaterThan(0));
                    var oldKey = new Vector2Int(Mathf.RoundToInt((step - 1f) / fixture.Field.CellSize), 0);
                    var currentKey = new Vector2Int(Mathf.RoundToInt(step / fixture.Field.CellSize), 0);
                    Assert.That(fixture.Field.TryGetNode(oldKey, now, 2f, out _, out _), Is.False,
                        "Moving support invalidates old world positions instead of attaching or joining a trail to the floor.");
                    Assert.That(fixture.Field.TryGetNode(currentKey, now, 2f, out _, out float pressure), Is.True);
                    Assert.That(pressure, Is.EqualTo(fixture.Interactor.ContactStrength),
                        "The endpoint-only record uses the response evaluated for this observation.");
                }

                // Signature invalidation must not hide a vertical teleport from
                // the independent response clock's complete pose bound.
                fixture.Ground.transform.position += Vector3.up * 6f;
                fixture.Actor.transform.position += Vector3.up * 6f;
                if (area)
                    Invoke(area, "RefreshState");
                fixture.Advance(0.4);
                Assert.That(fixture.Interactor.LastSupportStatus, Is.EqualTo(GrassInteractorSupportStatus.Supported));
                Assert.That(fixture.Interactor.ContactStrength, Is.Zero);
                Assert.That(fixture.Interactor.ActiveNodeCount, Is.Zero);
                fixture.Advance(0.5);
                Assert.That(fixture.Interactor.ContactStrength, Is.GreaterThan(0f));
                fixture.Interactor.ClearHistory();
                Assert.That(fixture.Interactor.ContactStrength, Is.Zero);
                fixture.Advance(0.6);
                Assert.That(fixture.Interactor.ContactStrength, Is.Zero);
                fixture.Advance(0.7);
                Assert.That(fixture.Interactor.ContactStrength, Is.GreaterThan(0f));
                fixture.Ground.enabled = false;
                fixture.Advance(0.8);
                Assert.That(fixture.Interactor.ContactStrength, Is.Zero);
                fixture.Ground.enabled = true;
                fixture.Advance(0.9);
                Assert.That(fixture.Interactor.ContactStrength, Is.Zero);
                fixture.Advance(1.0);
                Assert.That(fixture.Interactor.ContactStrength, Is.GreaterThan(0f));
                fixture.Advance(1.5);
                Assert.That(fixture.Interactor.ContactStrength, Is.Zero, "A long observation gap starts a new attack.");
                fixture.Advance(1.6);
                Assert.That(fixture.Interactor.ContactStrength, Is.GreaterThan(0f));
                fixture.Advance(1.2);
                Assert.That(fixture.Interactor.ContactStrength, Is.Zero, "Clock reversal remains a full session reset.");
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                if (surfaceMesh)
                    Object.DestroyImmediate(surfaceMesh);
            }
        }
    }

    [Test]
    public void DisableReleasesOwnedResourcesAndReenableStartsASeparateHistory()
    {
        using (var fixture = new InteractorFixture())
        {
            fixture.Advance(0.0);
            Assert.That(fixture.Interactor.TryGetDraw(out Mesh mesh, out Material material, out _), Is.True);
            Material template = Resources.Load<Material>("InfiniteGrassInteraction");
            fixture.Interactor.enabled = false;
            Assert.That(fixture.Interactor.State, Is.EqualTo(GrassColliderInteractor.InteractionState.Disabled));
            Assert.That(fixture.Interactor.ActiveNodeCount, Is.Zero);
            Assert.That(CountRegistration(fixture.Interactor), Is.Zero);
            Assert.That(mesh == null && material == null, Is.True);
            Assert.That(Get(fixture.Interactor, "supportResolver"), Is.Null);
            Assert.That(template != null, Is.True, "The consumer owns a clone, never the retained resource asset.");

            fixture.Interactor.enabled = true;
            Invoke(fixture.Interactor, "OnEnable");
            Assert.That(CountRegistration(fixture.Interactor), Is.EqualTo(1), "Lifecycle restoration must not duplicate registration.");
            fixture.Actor.transform.position += Vector3.right * 2f;
            fixture.Advance(0.1);
            Assert.That(fixture.Interactor.TryGetDraw(out Mesh newMesh, out Material newMaterial, out _), Is.True);
            Assert.That(ReferenceEquals(newMesh, mesh), Is.False);
            Assert.That(ReferenceEquals(newMaterial, material), Is.False);
            Assert.That(fixture.Field.TryGetNode(Vector2Int.zero, 0.1, 2f, out _, out _), Is.False);
        }
    }

    [Test]
    public void LosingTheExplicitSupportClearsItsStoredTrailAndPreservesTheBorrowedCollider()
    {
        using (var fixture = new InteractorFixture())
        {
            fixture.Advance(0.0);
            Assert.That(fixture.Interactor.ActiveNodeCount, Is.GreaterThan(0));
            fixture.Ground.enabled = false;
            fixture.Advance(0.1);
            Assert.That(fixture.Interactor.ActiveNodeCount, Is.Zero);
            Assert.That(fixture.Interactor.TryGetDraw(out _, out _, out _), Is.False);
            Assert.That(fixture.Ground != null, Is.True);
            Assert.That(fixture.Ground.enabled, Is.False);
            Assert.That(fixture.Interactor.LastSupportStatus, Is.EqualTo(GrassInteractorSupportStatus.InvalidSupport));
        }
    }

    [TestCase(GrassPlacementChange.Density)]
    [TestCase(GrassPlacementChange.Surface)]
    public void InvisibleRetainedHistoryStillObservesRelevantCoverageInvalidation(GrassPlacementChange change)
    {
        using (var fixture = new InteractorFixture())
        {
            Shader invalidShader = Shader.Find("InfiniteGrass/GrassHeightMapShader");
            Assert.That(invalidShader, Is.Not.Null);
            Set(fixture.Interactor, "captureShader", invalidShader);
            LogAssert.Expect(LogType.Warning,
                "The assigned interaction shader must provide a GrassSlope pass using premultiplied vertex RGBA.");
            fixture.Advance(0.0);
            int count = fixture.Interactor.ActiveNodeCount;
            Assert.That(count, Is.GreaterThan(0));
            Assert.That(fixture.Interactor.TryGetDraw(out _, out _, out _), Is.False);
            var near = new Bounds(Vector3.zero, Vector3.one);
            var far = new Bounds(Vector3.right * 100f, Vector3.one);
            Invoke(fixture.Interactor, "OnPlacementChanged", null, far, change);
            Assert.That(fixture.Interactor.ActiveNodeCount, Is.EqualTo(count));
            Invoke(fixture.Interactor, "OnPlacementChanged", null, near, GrassPlacementChange.GroundColor);
            Assert.That(fixture.Interactor.ActiveNodeCount, Is.EqualTo(count));

            Invoke(fixture.Interactor, "OnPlacementChanged", null, near, change);

            Assert.That(fixture.Interactor.ActiveNodeCount, Is.Zero,
                "Bounds must come from retained nodes even when an invalid assigned shader prevents drawing.");
            LogAssert.NoUnexpectedReceived();
        }
    }

    [Test]
    public void MovingToAnotherSceneRejectsOldDrawsBeforeTheNextUpdateAndClearsHistory()
    {
        Scene destination = SceneManager.CreateScene("Grass interactor destination " + Guid.NewGuid().ToString("N"),
            new CreateSceneParameters(LocalPhysicsMode.Physics3D));
        try
        {
            using (var fixture = new InteractorFixture())
            {
                fixture.Advance(0.0);
                Assert.That(fixture.Interactor.TryGetDraw(out _, out _, out _), Is.True);
                SceneManager.MoveGameObjectToScene(fixture.Actor, destination);
                Assert.That(fixture.Interactor.TryGetDraw(out _, out _, out _), Is.False,
                    "A draw recorded in the previous scene cannot be reused while support belongs to another physics scene.");
                fixture.Advance(0.1);
                Assert.That(fixture.Interactor.ActiveNodeCount, Is.Zero);
                Assert.That(fixture.Interactor.LastSupportStatus, Is.EqualTo(GrassInteractorSupportStatus.InvalidSupport));
            }
        }
        finally
        {
            if (destination.IsValid() && destination.isLoaded)
                EditorSceneManager.CloseScene(destination, true);
        }
    }

    [Test]
    public void MovingToPreviewReleasesResourcesAndReturningRegistersAFreshProducer()
    {
        Scene preview = EditorSceneManager.NewPreviewScene();
        try
        {
            using (var fixture = new InteractorFixture())
            {
                fixture.Advance(0.0);
                Assert.That(fixture.Interactor.TryGetDraw(out Mesh mesh, out Material material, out _), Is.True);
                SceneManager.MoveGameObjectToScene(fixture.Actor, preview);
                Assert.That(fixture.Interactor.TryGetDraw(out _, out _, out _), Is.False);
                Invoke(fixture.Interactor, "Update");
                Assert.That(mesh == null && material == null, Is.True);
                Assert.That(CountRegistration(fixture.Interactor), Is.Zero);

                SceneManager.MoveGameObjectToScene(fixture.Actor, fixture.Scene);
                Invoke(fixture.Interactor, "Update");
                fixture.Advance(0.1);
                Assert.That(CountRegistration(fixture.Interactor), Is.EqualTo(1));
                Assert.That(fixture.Interactor.TryGetDraw(out Mesh newMesh, out Material newMaterial, out _), Is.True);
                Assert.That(ReferenceEquals(newMesh, mesh), Is.False);
                Assert.That(ReferenceEquals(newMaterial, material), Is.False);
            }
        }
        finally
        {
            if (preview.IsValid())
                EditorSceneManager.ClosePreviewScene(preview);
        }
    }

    [Test]
    public void CameraCaptureVersionsIgnoreDistantUpdatesAndObserveRelevantExpiryAndRemoval()
    {
        using (var near = new InteractorFixture())
        using (var far = new InteractorFixture(new Vector3(1000f, 0f, 0f)))
        using (var first = new CameraCaptureFixture(Vector3.zero))
        using (var second = new CameraCaptureFixture(Vector3.right * 1000f))
        {
            near.Advance(0.0);
            far.Advance(0.0);
            ulong firstVersion = first.Collect();
            ulong secondVersion = second.Collect();
            Assert.That(first.DrawCount, Is.EqualTo(1));
            Assert.That(second.DrawCount, Is.EqualTo(1));
            Set(first.State, "InteractionVersion", firstVersion);
            Set(second.State, "InteractionVersion", secondVersion);

            far.Actor.transform.position += Vector3.right * 0.2f;
            far.Advance(0.1);
            Assert.That(first.Collect(), Is.EqualTo(firstVersion), "Off-camera changes leave this capture valid.");
            Assert.That(second.Collect(), Is.Not.EqualTo(secondVersion));
            Assert.That(Get(first.State, "InteractionVersion"), Is.EqualTo(Get(first.State, "NextInteractionVersion")));
            Assert.That(Get(second.State, "InteractionVersion"), Is.Not.EqualTo(Get(second.State, "NextInteractionVersion")),
                "This independent version check requests a slope capture even when static modifier captures are cached.");

            near.Actor.transform.position += Vector3.up * 3f;
            near.Advance(2.0);
            ulong emptyVersion = first.Collect();
            Assert.That(emptyVersion, Is.Not.EqualTo(firstVersion));
            Assert.That(first.DrawCount, Is.Zero);
            Assert.That(first.Collect(), Is.EqualTo(emptyVersion), "An already empty relevant set has a stable version.");
            far.Interactor.enabled = false;
            Assert.That(second.Collect(), Is.EqualTo(emptyVersion));
            Assert.That(second.DrawCount, Is.Zero);
            Assert.That(Get(first.State, "Positions"), Is.Null);
            Assert.That(Get(second.State, "Positions"), Is.Null,
                "This case verifies CPU capture invalidation and owned mesh lifetime, without claiming a rendered pass.");
        }
    }

    private sealed class InteractorFixture : IDisposable
    {
        public readonly Scene Scene;
        public readonly GameObject Actor;
        public readonly CapsuleCollider Collider;
        public readonly BoxCollider Ground;
        public readonly GrassColliderInteractor Interactor;
        public GrassInteractionField Field => (GrassInteractionField)Get(Interactor, "field");

        public InteractorFixture(Vector3 origin = default)
        {
            Scene = SceneManager.CreateScene("Grass collider fixture " + Guid.NewGuid().ToString("N"),
                new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            try
            {
                var floor = new GameObject("Explicit grass support");
                SceneManager.MoveGameObjectToScene(floor, Scene);
                floor.transform.position = origin + Vector3.down * 0.5f;
                Ground = floor.AddComponent<BoxCollider>();
                Ground.size = new Vector3(200f, 1f, 20f);
                Actor = new GameObject("Actual character collider");
                Actor.SetActive(false);
                SceneManager.MoveGameObjectToScene(Actor, Scene);
                Actor.transform.position = origin + Vector3.up * 0.9f;
                Collider = Actor.AddComponent<CapsuleCollider>();
                Collider.direction = 1;
                Collider.radius = 0.3f;
                Collider.height = 1.8f;
                Interactor = Actor.AddComponent<GrassColliderInteractor>();
                Interactor.Configure(Collider, Ground);
                Interactor.SamplingMode = GrassColliderInteractor.UpdateMode.Manual;
                Actor.SetActive(true);
                Assert.That(Resources.Load<Material>("InfiniteGrassInteraction"), Is.Not.Null);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Advance(double now) => Interactor.Sample(now);

        public void Dispose()
        {
            if (Actor)
                Object.DestroyImmediate(Actor);
            if (Ground)
                Object.DestroyImmediate(Ground.gameObject);
            if (Scene.IsValid() && Scene.isLoaded)
                EditorSceneManager.CloseScene(Scene, true);
        }
    }

    private sealed class CameraCaptureFixture : IDisposable
    {
        private static readonly Type PassType = typeof(GrassDataRendererFeature).GetNestedType("GrassDataPass", BindingFlags.NonPublic);
        private static readonly Type StateType = PassType.GetNestedType("CameraState", BindingFlags.NonPublic);
        private readonly Camera camera;
        private readonly Bounds bounds;
        public readonly object State;
        public int DrawCount => ((IList)Get(State, "InteractionDraws")).Count;

        public CameraCaptureFixture(Vector3 center)
        {
            camera = new GameObject("Grass interaction capture window").AddComponent<Camera>();
            State = Activator.CreateInstance(StateType, new object[] { camera });
            bounds = new Bounds(center, Vector3.one * 10f);
        }

        public ulong Collect()
        {
            PassType.GetMethod("CollectInteractionDraws", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new[] { State, (object)bounds });
            return (ulong)Get(State, "NextInteractionVersion");
        }

        public void Dispose()
        {
            ((IDisposable)State)?.Dispose();
            if (camera)
                Object.DestroyImmediate(camera.gameObject);
        }
    }

    private static int CountRegistration(GrassColliderInteractor component)
    {
        int count = 0;
        for (int i = 0; i < GrassColliderInteractor.ActiveInteractors.Count; i++)
            if (ReferenceEquals(GrassColliderInteractor.ActiveInteractors[i], component))
                count++;
        return count;
    }

    private static object Get(object target, string name) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(target);

    private static void Set(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(target, value);

    private static object Invoke(object target, string name, params object[] args) => target.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
}
