using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

[NonParallelizable]
public sealed class GrassSettingsLifecycleTests
{
    [Test]
    public void NewMotionSettingsStartWithHistoryDisabled()
    {
        Assert.That(new GrassMotionVectors.Settings().mode, Is.EqualTo(GrassMotionVectors.Mode.Off));
    }

    [Test]
    public void AdditiveSceneActivationRegistersSettingsBeforeFirstUpdate()
    {
        InfiniteGrassRenderer previous = InfiniteGrassRenderer.Instance;
        if (previous)
            previous.enabled = false;
        Scene scene = default;
        string path = "Assets/GrassSettingsLifecycle_" + Guid.NewGuid().ToString("N") + ".unity";
        try
        {
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var gameObject = new GameObject("Grass settings registration");
            SceneManager.MoveGameObjectToScene(gameObject, scene);
            InfiniteGrassRenderer settings = gameObject.AddComponent<InfiniteGrassRenderer>();
            Assert.That(InfiniteGrassRenderer.Instance, Is.SameAs(settings));
            Assert.That(EditorSceneManager.SaveScene(scene, path), Is.True);
            Assert.That(EditorSceneManager.CloseScene(scene, true), Is.True);
            Assert.That(InfiniteGrassRenderer.Instance, Is.Null);

            scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            InfiniteGrassRenderer loaded = scene.GetRootGameObjects()[0].GetComponent<InfiniteGrassRenderer>();
            Assert.That(loaded, Is.Not.Null);
            Assert.That(InfiniteGrassRenderer.Instance, Is.SameAs(loaded),
                "Registration must not wait for Update: OnEnable runs while the additive scene is becoming loaded.");
            Assert.That(loaded.Revision, Is.GreaterThan(0));
        }
        finally
        {
            if (scene.IsValid() && scene.isLoaded)
                EditorSceneManager.CloseScene(scene, true);
            AssetDatabase.DeleteAsset(path);
            if (previous)
                previous.enabled = true;
        }
    }

    [Test]
    public void PreviewSceneSettingsCannotTakeSceneOwnership()
    {
        Scene preview = EditorSceneManager.NewPreviewScene();
        InfiniteGrassRenderer owner = InfiniteGrassRenderer.Instance;
        try
        {
            var gameObject = new GameObject("Preview settings");
            gameObject.SetActive(false);
            SceneManager.MoveGameObjectToScene(gameObject, preview);
            InfiniteGrassRenderer settings = gameObject.AddComponent<InfiniteGrassRenderer>();
            gameObject.SetActive(true);
            Assert.That(InfiniteGrassRenderer.Instance, Is.SameAs(owner));
            Assert.That(InfiniteGrassRenderer.Instance, Is.Not.SameAs(settings));
        }
        finally
        {
            EditorSceneManager.ClosePreviewScene(preview);
        }
    }

    [UnityTest]
    public IEnumerator EnabledOwnerMovedToPreviewStopsRenderingAndReleasesOwnership()
    {
        InfiniteGrassRenderer previous = InfiniteGrassRenderer.Instance;
        bool previousEnabled = previous && previous.enabled;
        if (previous)
            previous.enabled = false;
        Scene preview = default;
        GameObject gameObject = null;
        try
        {
            gameObject = new GameObject("Grass owner moved to preview");
            InfiniteGrassRenderer settings = gameObject.AddComponent<InfiniteGrassRenderer>();
            Assert.That(settings.IsReadyForRendering, Is.True);
            Mesh[] meshes = settings.GetLodMeshes();
            preview = EditorSceneManager.NewPreviewScene();

            SceneManager.MoveGameObjectToScene(gameObject, preview);
            Assert.That(settings.isActiveAndEnabled, Is.True);
            Assert.That(settings.IsReadyForRendering, Is.False,
                "Both renderer entry points must reject a preview owner before its next Update.");
            EditorApplication.QueuePlayerLoopUpdate();
            yield return null;

            Assert.That(InfiniteGrassRenderer.Instance, Is.Null);
            foreach (Mesh mesh in meshes)
                Assert.That(mesh == null, Is.True, "A former owner must release its generated meshes.");
        }
        finally
        {
            if (gameObject)
                Object.DestroyImmediate(gameObject);
            if (preview.IsValid())
                EditorSceneManager.ClosePreviewScene(preview);
            if (previous)
                previous.enabled = previousEnabled;
        }
    }
}

[NonParallelizable]
public sealed class GrassRendererLifecycleTests
{
    [TestCase(true)]
    [TestCase(false)]
    public void UnavailableGrassInputsReleaseEveryCameraWithoutWaitingForIdleTimeout(bool missingMaterial)
    {
        using (var fixture = new RendererFixture())
        {
            fixture.Owner.grassMaterial = missingMaterial ? null : AssetDatabase.LoadAssetAtPath<Material>(
                "Packages/com.cone.grass/Runtime/Materials/Grass Blade.mat");
            ComputeShader shader = missingMaterial ? AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/com.cone.grass/Runtime/Compute/GrassPositionsCompute.compute") : null;
            SetField(fixture.Feature, "computeShader", shader);
            Assert.That(missingMaterial ? shader != null : fixture.Owner.grassMaterial != null, Is.True);
            RenderingData renderingData = default;

            fixture.Feature.AddRenderPasses(null, ref renderingData);

            Assert.That(fixture.States.Count, Is.Zero);
            Assert.That(fixture.Histories.Count, Is.Zero);
            foreach (object state in fixture.CameraStates)
                Assert.That(GetField(state, "Disposed"), Is.True);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PruningOneUnavailableCameraPreservesOtherCameraState(bool destroyedCamera)
    {
        using (var fixture = new RendererFixture())
        {
            if (destroyedCamera)
                Object.DestroyImmediate(fixture.Cameras[0].gameObject);
            else
                SetField(fixture.CameraStates[0], "LastUsedTime", Time.realtimeSinceStartupAsDouble - 11.0);

            fixture.Pass.GetType().GetMethod("PruneCameras").Invoke(fixture.Pass, null);

            Assert.That(fixture.States.Count, Is.EqualTo(1));
            Assert.That(fixture.Histories.Count, Is.EqualTo(1));
            Assert.That(fixture.States.Contains(fixture.Cameras[1]), Is.True);
            Assert.That(fixture.Histories.Contains(fixture.Cameras[1]), Is.True);
            Assert.That(GetField(fixture.CameraStates[0], "Disposed"), Is.True);
            Assert.That(GetField(fixture.CameraStates[1], "Disposed"), Is.False);
        }
    }

    private static object GetField(object owner, string name) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(owner);

    private static void SetField(object owner, string name, object value) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(owner, value);

    private sealed class RendererFixture : IDisposable
    {
        public readonly GrassDataRendererFeature Feature;
        public readonly InfiniteGrassRenderer Owner;
        public readonly object Pass;
        public readonly IDictionary States, Histories;
        public readonly Camera[] Cameras = new Camera[2];
        public readonly object[] CameraStates = new object[2];
        private readonly InfiniteGrassRenderer previous;
        private readonly bool previousEnabled;

        public RendererFixture()
        {
            previous = InfiniteGrassRenderer.Instance;
            previousEnabled = previous && previous.enabled;
            if (previous)
                previous.enabled = false;
            try
            {
                Owner = new GameObject("Grass renderer lifetime settings").AddComponent<InfiniteGrassRenderer>();
                Assert.That(Owner.IsReadyForRendering, Is.True);
                Feature = ScriptableObject.CreateInstance<GrassDataRendererFeature>();
                Feature.Create();
                Pass = GetField(Feature, "grassPass");
                States = (IDictionary)GetField(Pass, "cameras");
                Histories = (IDictionary)GetField(GetField(Pass, "motion"), "cameras");
                Type stateType = Pass.GetType().GetNestedType("CameraState", BindingFlags.NonPublic);
                Type historyType = typeof(GrassMotionVectors).GetNestedType("CameraHistory", BindingFlags.NonPublic);
                for (int i = 0; i < Cameras.Length; i++)
                {
                    Cameras[i] = new GameObject("Grass lifetime camera " + i).AddComponent<Camera>();
                    CameraStates[i] = Activator.CreateInstance(stateType, new object[] { Cameras[i] });
                    SetField(CameraStates[i], "LastUsedTime", Time.realtimeSinceStartupAsDouble);
                    States.Add(Cameras[i], CameraStates[i]);
                    Histories.Add(Cameras[i], Activator.CreateInstance(historyType, true));
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (Feature)
            {
                Feature.Dispose();
                Object.DestroyImmediate(Feature);
            }
            if (Owner)
                Object.DestroyImmediate(Owner.gameObject);
            foreach (Camera camera in Cameras)
                if (camera)
                    Object.DestroyImmediate(camera.gameObject);
            if (previous)
                previous.enabled = previousEnabled;
        }
    }
}
