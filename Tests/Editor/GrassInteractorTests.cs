using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

public sealed class GrassInteractorTests
{
    private readonly List<GrassPlacementArea> previousAreas = new List<GrassPlacementArea>();

    [SetUp]
    public void IsolateAuthoredGrassOwnership()
    {
        previousAreas.Clear();
        foreach (GrassPlacementArea area in GrassPlacementArea.ActiveAreas)
            previousAreas.Add(area);
        foreach (GrassPlacementArea area in previousAreas)
            if (area)
                area.enabled = false;
    }

    [TearDown]
    public void RestoreAuthoredGrassOwnership()
    {
        foreach (GrassPlacementArea area in previousAreas)
            if (area)
                area.enabled = true;
        previousAreas.Clear();
    }

    [Test]
    public void PublishedScriptIdentityAndSerializedSettingsRemainAvailable()
    {
        using (var fixture = new InteractorFixture())
        {
            GrassInteractor interactor = fixture.Interactor;
            Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(MonoScript.FromMonoBehaviour(interactor))),
                Is.EqualTo("b060e224fe684dcbbe8deb10e97e549f"),
                "Scenes using the published script GUID must resolve to the compatibility component.");
            Assert.That(interactor.body, Is.SameAs(fixture.Body), "OnEnable still finds an unassigned local collider.");
            Assert.That(interactor.groundLayers.value, Is.EqualTo(~0));
            Assert.That(interactor.radiusPadding, Is.EqualTo(0.15f));
            Assert.That(interactor.bladeHeight, Is.EqualTo(0.5f));
            Assert.That(interactor.strength, Is.EqualTo(0.9f));
            Assert.That(interactor.attackSeconds, Is.EqualTo(0.06f));
            Assert.That(interactor.recoverySeconds, Is.EqualTo(0.55f));
            using (var serialized = new SerializedObject(interactor))
                foreach (string name in new[] { "body", "interactionShader", "groundLayers", "radiusPadding",
                             "bladeHeight", "strength", "attackSeconds", "recoverySeconds" })
                    Assert.That(serialized.FindProperty(name), Is.Not.Null, name);
            LogAssert.NoUnexpectedReceived();
        }
    }

    [Test]
    public void PublishedFieldsAndReflectedDiagnosticsUseOneManuallySampledCore()
    {
        using (var fixture = new InteractorFixture())
        {
            GrassInteractor interactor = fixture.Interactor;
            interactor.Sample(0.0);
            interactor.Sample(0.1);

            float expected = interactor.strength * (1f - Mathf.Exp(-0.1f / interactor.attackSeconds));
            Assert.That((float)typeof(GrassInteractor).GetProperty("ContactStrength").GetValue(interactor),
                Is.EqualTo(expected).Within(0.00001f), "The facade must pass its attack and strength settings to real contact state.");
            Assert.That((int)typeof(GrassInteractor).GetProperty("RetainedStampCount").GetValue(interactor),
                Is.EqualTo(interactor.ActiveNodeCount).And.GreaterThan(0));
            int paddedCount = interactor.ActiveNodeCount;
            Assert.That(interactor.TryGetDraw(out Mesh mesh, out Material material, out _), Is.True);
            Assert.That(material.shader, Is.SameAs(interactor.interactionShader));
            Assert.That(fixture.Actor.GetComponents<GrassColliderInteractor>().Length, Is.EqualTo(1));
            Assert.That(fixture.Actor.GetComponentsInChildren<Renderer>(true), Is.Empty);
            Assert.That(fixture.Actor.transform.childCount, Is.Zero);
            Assert.That(fixture.Scene.GetRootGameObjects().Length, Is.EqualTo(2), "No helper object or camera is created.");
            Assert.That(RegistrationCount(interactor), Is.EqualTo(1));

            interactor.radiusPadding = 0f;
            interactor.Sample(0.2);
            interactor.Sample(0.3);
            Assert.That(interactor.ActiveNodeCount, Is.GreaterThan(0).And.LessThan(paddedCount),
                "Changing the published padding must alter the field instead of only updating an unused facade field.");
            Assert.That(fixture.Body.radius, Is.EqualTo(0.3f), "Interaction padding must not modify the borrowed collider.");
            Assert.That(interactor.TryGetDraw(out Mesh reusedMesh, out Material reusedMaterial, out _), Is.True);
            Assert.That(reusedMesh, Is.SameAs(mesh));
            Assert.That(reusedMaterial, Is.SameAs(material));

            interactor.groundLayers = 0;
            interactor.Sample(0.4);
            Assert.That(interactor.ActiveNodeCount, Is.Zero);
            Assert.That(interactor.ContactStrength, Is.Zero);
            Assert.That(interactor.TryGetDraw(out _, out _, out _), Is.False);
            interactor.groundLayers = ~0;
            interactor.bladeHeight = 0.1f;
            fixture.Actor.transform.position += Vector3.up * 0.25f;
            interactor.Sample(0.5);
            Assert.That(interactor.ActiveNodeCount, Is.Zero, "The published blade height must limit accepted ground clearance.");
            interactor.bladeHeight = 0.5f;
            interactor.Sample(0.6);
            interactor.Sample(0.7);
            Assert.That(interactor.ActiveNodeCount, Is.GreaterThan(0));
            Assert.That(interactor.ContactStrength, Is.GreaterThan(0f).And.LessThan(expected),
                "Contact above the floor is attenuated through the shared support result.");
            LogAssert.NoUnexpectedReceived();
        }
    }

    [Test]
    public void MultipleEligibleLocalCollidersRequireAnExplicitBody()
    {
        using (var fixture = new InteractorFixture(ambiguousBody: true))
        {
            GrassInteractor interactor = fixture.Interactor;
            Assert.That(ReferenceEquals(interactor.body, null), Is.True,
                "Automatic binding must not select an arbitrary eligible collider.");
            interactor.Sample(0.0);
            Assert.That(interactor.State, Is.EqualTo(GrassColliderInteractor.InteractionState.MissingCollider));
            Assert.That(interactor.ActiveNodeCount, Is.Zero);
            interactor.Configure(fixture.Body, fixture.Ground);
            interactor.Sample(0.1);
            interactor.Sample(0.2);
            Assert.That(interactor.body, Is.SameAs(fixture.Body));
            Assert.That(interactor.ActiveNodeCount, Is.GreaterThan(0));
            LogAssert.NoUnexpectedReceived();
        }
    }

    [Test]
    public void PublicConfigureRebindsTheFacadeAndDestroyedExplicitBodiesAreNotReplaced()
    {
        using (var fixture = new InteractorFixture())
        {
            GrassInteractor interactor = fixture.Interactor;
            interactor.Sample(0.0);
            interactor.Sample(0.1);
            Assert.That(interactor.ActiveNodeCount, Is.GreaterThan(0));
            var replacement = new GameObject("Replacement actual body");
            try
            {
                SceneManager.MoveGameObjectToScene(replacement, fixture.Scene);
                replacement.transform.position = new Vector3(3f, 0.9f, 0f);
                var collider = replacement.AddComponent<CapsuleCollider>();
                collider.radius = 0.3f;
                collider.height = 1.8f;
                interactor.Configure(collider, fixture.Ground, replacement.transform);
                Assert.That(interactor.RetainedStampCount, Is.Zero);
                interactor.Sample(0.2);
                interactor.Sample(0.3);
                Assert.That(interactor.body, Is.SameAs(collider));
                Assert.That(interactor.ActorCollider, Is.SameAs(collider));
                Assert.That(interactor.TryGetDraw(out _, out _, out Bounds bounds), Is.True);
                Assert.That(bounds.min.x, Is.GreaterThan(1f), "A later sample must use the replacement body and exclude the old trail.");

                Object.DestroyImmediate(collider);
                interactor.enabled = false;
                interactor.enabled = true;
                interactor.Sample(0.4);
                Assert.That(ReferenceEquals(interactor.body, collider), Is.True,
                    "A destroyed explicitly assigned body must stay missing instead of selecting the original local collider.");
                Assert.That(interactor.State, Is.EqualTo(GrassColliderInteractor.InteractionState.MissingCollider));
                Assert.That(interactor.ActiveNodeCount, Is.Zero);
                Assert.That(fixture.Body && fixture.Body.enabled, Is.True);
                Assert.That(RegistrationCount(interactor), Is.EqualTo(1));
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                Object.DestroyImmediate(replacement);
            }
        }
    }

    [Test]
    public void PublicResetFiniteRecoveryAndDisableShareCoreResourceOwnership()
    {
        using (var fixture = new InteractorFixture())
        {
            GrassInteractor interactor = fixture.Interactor;
            interactor.Sample(0.0);
            interactor.Sample(0.1);
            Assert.That(interactor.TryGetDraw(out Mesh mesh, out Material material, out _), Is.True);

            interactor.ResetInteraction();
            Assert.That(interactor.RetainedStampCount, Is.Zero);
            Assert.That(interactor.ContactStrength, Is.Zero);
            Assert.That(interactor.TryGetDraw(out _, out _, out _), Is.False);
            Assert.That(mesh != null && material != null, Is.True, "Reset keeps the one reusable native resource pair.");
            Assert.That(mesh.vertexCount, Is.Zero);
            Assert.That(RegistrationCount(interactor), Is.EqualTo(1));
            interactor.Sample(0.2);
            interactor.Sample(0.3);
            Assert.That(interactor.TryGetDraw(out Mesh rebuiltMesh, out Material rebuiltMaterial, out _), Is.True);
            Assert.That(rebuiltMesh, Is.SameAs(mesh));
            Assert.That(rebuiltMaterial, Is.SameAs(material));

            fixture.Actor.transform.position += Vector3.up * 3f;
            interactor.Sample(0.4);
            Assert.That(interactor.RetainedStampCount, Is.GreaterThan(0));
            Assert.That(interactor.ContactStrength, Is.Zero, "Departed nodes recover independently of current collider contact.");
            Assert.That(interactor.TryGetDraw(out _, out _, out _), Is.True);
            interactor.Sample(0.3 + interactor.recoverySeconds + 0.000001);
            Assert.That(interactor.RetainedStampCount, Is.Zero, "Recovery Seconds is the finite departure-to-removal duration.");
            Assert.That(interactor.TryGetDraw(out _, out _, out _), Is.False);
            Assert.That(mesh != null && material != null, Is.True);

            interactor.enabled = false;
            Assert.That(RegistrationCount(interactor), Is.Zero);
            Assert.That(mesh == null && material == null, Is.True, "Base OnDisable owns both native resources.");
            Assert.That(fixture.Body && fixture.Body.enabled && fixture.Ground && fixture.Ground.enabled, Is.True);
            LogAssert.NoUnexpectedReceived();
        }
    }

    private static int RegistrationCount(GrassColliderInteractor interactor)
    {
        int count = 0;
        foreach (GrassColliderInteractor active in GrassColliderInteractor.ActiveInteractors)
            if (active == interactor)
                count++;
        return count;
    }

    private sealed class InteractorFixture : IDisposable
    {
        public readonly Scene Scene;
        public readonly GameObject Actor;
        public readonly CapsuleCollider Body;
        public readonly BoxCollider Ground;
        public readonly GrassInteractor Interactor;

        public InteractorFixture(bool ambiguousBody = false)
        {
            Scene = SceneManager.CreateScene("Grass compatibility fixture " + Guid.NewGuid().ToString("N"),
                new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            try
            {
                var floor = new GameObject("Explicit grass support");
                SceneManager.MoveGameObjectToScene(floor, Scene);
                floor.transform.position = Vector3.down * 0.5f;
                Ground = floor.AddComponent<BoxCollider>();
                Ground.size = new Vector3(20f, 1f, 20f);
                Actor = new GameObject("Published interaction component");
                Actor.SetActive(false);
                SceneManager.MoveGameObjectToScene(Actor, Scene);
                Actor.transform.position = Vector3.up * 0.9f;
                Body = Actor.AddComponent<CapsuleCollider>();
                Body.direction = 1;
                Body.radius = 0.3f;
                Body.height = 1.8f;
                if (ambiguousBody)
                    Actor.AddComponent<SphereCollider>();
                Interactor = Actor.AddComponent<GrassInteractor>();
                Interactor.SamplingMode = GrassColliderInteractor.UpdateMode.Manual;
                Interactor.interactionShader = Shader.Find("InfiniteGrass/Modifiers/GrassInteractor");
                Assert.That(Interactor.interactionShader, Is.Not.Null, "The published shader must be present; this is not a skip.");
                Interactor.Configure(null, Ground);
                Actor.SetActive(true);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

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
}
