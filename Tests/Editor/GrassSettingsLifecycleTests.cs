using System;
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

[NonParallelizable]
public sealed class GrassSettingsLifecycleTests
{
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
