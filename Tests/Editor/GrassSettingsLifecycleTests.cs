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
public sealed class GrassPopulationSettingsTests
{
    [TestCase(0f, 3)]
    [TestCase(-float.MaxValue, 3)]
    [TestCase(2f, 2000000)]
    [TestCase(16f, 16000000)]
    [TestCase(2147.48364f, 16000000)]
    [TestCase(10000f, 16000000)]
    [TestCase(float.MaxValue, 16000000)]
    [TestCase(float.NaN, 2000000)]
    [TestCase(float.PositiveInfinity, 2000000)]
    [TestCase(float.NegativeInfinity, 2000000)]
    public void ScriptedCapacityRequestsAreBoundedBeforeIntegerConversion(float millions, int expected)
    {
        var gameObject = new GameObject("Grass capacity settings");
        gameObject.SetActive(false);
        try
        {
            var settings = gameObject.AddComponent<InfiniteGrassRenderer>();
            // Runtime assignments do not pass through OnValidate or OnEnable.
            settings.maxBufferCount = millions;
            Assert.That(settings.Capacity, Is.EqualTo(expected));
        }
        finally
        {
            Object.DestroyImmediate(gameObject);
        }
    }

    [TestCase(2f, 0.35f, 0.45f, 0.2f, 700000, 900000, 400000)]
    [TestCase(16f, 0.35f, 0.45f, 0.2f, 5600000, 7200000, 3200000)]
    [TestCase(16f, float.MaxValue, float.MaxValue, float.MaxValue, 5333333, 5333333, 5333334)]
    [TestCase(16f, 1e32f, 1f, 1f, 15999998, 1, 1)]
    [TestCase(16f, 1f, 1e32f, 1f, 1, 15999998, 1)]
    [TestCase(16f, 1f, 1f, 1e32f, 1, 1, 15999998)]
    [TestCase(float.MaxValue, float.MaxValue, 1f, 1f, 15999998, 1, 1)]
    [TestCase(2f, float.NaN, float.PositiveInfinity, float.NegativeInfinity, 700000, 900000, 400000)]
    [TestCase(0f, float.MaxValue, float.MaxValue, float.MaxValue, 1, 1, 1)]
    public void ScriptedLodWeightsKeepPositiveContiguousPartitions(float millions, float near, float middle,
        float far, int expectedNear, int expectedMiddle, int expectedFar)
    {
        var gameObject = new GameObject("Grass LOD capacity settings");
        gameObject.SetActive(false);
        try
        {
            var settings = gameObject.AddComponent<InfiniteGrassRenderer>();
            settings.maxBufferCount = millions;
            settings.lodCapacityWeights = new Vector3(near, middle, far);
            int[] capacities = { -1, -1, -1, -1 };
            int[] offsets = { -1, -1, -1, -1 };

            settings.GetLodCapacity(capacities, offsets);

            Assert.That(capacities, Is.EqualTo(new[] { expectedNear, expectedMiddle, expectedFar, 0 }));
            Assert.That(offsets, Is.EqualTo(new[] { 0, expectedNear, expectedNear + expectedMiddle, 0 }));
            Assert.That(capacities[0] + capacities[1] + capacities[2], Is.EqualTo(settings.Capacity));
            for (int lod = 0; lod < 3; lod++)
            {
                Assert.That(capacities[lod], Is.GreaterThan(0));
                Assert.That(offsets[lod] + capacities[lod], Is.LessThanOrEqualTo(settings.Capacity));
            }
        }
        finally
        {
            Object.DestroyImmediate(gameObject);
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

    [Test]
    public void IncompatibleBladeMaterialReleasesAllCamerasAndWarnsOnceUntilRecovery()
    {
        using (var fixture = new RendererFixture())
        {
            Shader shader = Shader.Find("InfiniteGrass/Modifiers/GrassMaskShader");
            Assert.That(shader, Is.Not.Null);
            var incompatible = new Material(shader);
            try
            {
                Assert.That(incompatible.FindPass("GrassForward"), Is.LessThan(0));
                fixture.Owner.grassMaterial = incompatible;
                const string warning = "The grass material needs the GrassForward pass from the package blade shader.";

                LogAssert.Expect(LogType.Warning, warning);
                ((ScriptableRenderPass)fixture.Pass).RecordRenderGraph(null, null);
                ((ScriptableRenderPass)fixture.Pass).RecordRenderGraph(null, null);

                Assert.That(fixture.States.Count, Is.Zero);
                Assert.That(fixture.Histories.Count, Is.Zero);
                foreach (object state in fixture.CameraStates)
                    Assert.That(GetField(state, "Disposed"), Is.True);
                LogAssert.NoUnexpectedReceived();

                fixture.Owner.grassMaterial = AssetDatabase.LoadAssetAtPath<Material>(
                    "Packages/com.cone.grass/Runtime/Materials/Grass Blade.mat");
                Assert.That(fixture.Owner.grassMaterial, Is.Not.Null);
                Assert.That(fixture.Owner.grassMaterial.FindPass("GrassForward"), Is.GreaterThanOrEqualTo(0));
                // This fixture has no compute shader; a valid material exits at
                // that next prerequisite without constructing a graph or buffers.
                ((ScriptableRenderPass)fixture.Pass).RecordRenderGraph(null, null);

                fixture.Owner.grassMaterial = incompatible;
                LogAssert.Expect(LogType.Warning, warning);
                ((ScriptableRenderPass)fixture.Pass).RecordRenderGraph(null, null);
                Assert.That(fixture.States.Count, Is.Zero,
                    "An incompatible material must never reach camera allocation.");
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                Object.DestroyImmediate(incompatible);
            }
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

    [TestCase(false)]
    [TestCase(true)]
    public void ContextsWithoutGrassPassesReleaseInactiveFeaturesAndPruneIdleCameras(bool inactiveFeature)
    {
        using (var fixture = new RendererFixture())
        {
            ScriptableRendererFeature feature = fixture.Feature;
            feature.SetActive(!inactiveFeature);
            if (!inactiveFeature)
                SetField(fixture.CameraStates[0], "LastUsedTime", Time.realtimeSinceStartupAsDouble - 11.0);
            typeof(InfiniteGrassRenderer).GetProperty(nameof(InfiniteGrassRenderer.VisibleGrassCount))
                .SetValue(fixture.Owner, 123u);
            MethodInfo beginContext = typeof(GrassDataRendererFeature).GetMethod("OnBeginContextRendering",
                BindingFlags.Instance | BindingFlags.NonPublic);

            // Inactive features never receive AddRenderPasses. Exercise the
            // context callback even when no camera uses this renderer at all.
            beginContext.Invoke(feature, new object[] { default(UnityEngine.Rendering.ScriptableRenderContext), null });

            int remaining = inactiveFeature ? 0 : 1;
            Assert.That(fixture.States.Count, Is.EqualTo(remaining));
            Assert.That(fixture.Histories.Count, Is.EqualTo(remaining));
            Assert.That(GetField(fixture.CameraStates[0], "Disposed"), Is.True);
            Assert.That(GetField(fixture.CameraStates[1], "Disposed"), Is.EqualTo(inactiveFeature));
            Assert.That(GetField(fixture.Pass, "disposed"), Is.False,
                "Feature inactivity only releases camera resources; helper materials remain reusable.");
            Assert.That(fixture.Owner.VisibleGrassCount, Is.EqualTo(123u),
                "Cleanup cannot overwrite diagnostics another active renderer may have supplied.");

            feature.SetActive(true);
            beginContext.Invoke(feature, new object[] { default(UnityEngine.Rendering.ScriptableRenderContext), null });
            Assert.That(GetField(fixture.Feature, "grassPass"), Is.SameAs(fixture.Pass));
            Assert.That(fixture.States.Count, Is.EqualTo(remaining));
        }
    }

    [Test]
    public void DestroyingFeatureDisposesItsPassAndCameraState()
    {
        using (var fixture = new RendererFixture())
        {
            Object.DestroyImmediate(fixture.Feature);

            Assert.That(GetField(fixture.Pass, "disposed"), Is.True);
            Assert.That(fixture.States.Count, Is.Zero);
            Assert.That(fixture.Histories.Count, Is.Zero);
            foreach (object state in fixture.CameraStates)
                Assert.That(GetField(state, "Disposed"), Is.True);
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

[NonParallelizable]
public sealed class GrassCaptureTextureTests
{
    [Test]
    public void UncreatedWindTextureUsesNeutralFallbackWithoutAllocatingOrReplacingTheSource()
    {
        Shader shader = Shader.Find("InfiniteGrass/GrassBladeShader");
        Assert.That(shader, Is.Not.Null);
        var material = new Material(shader);
        var source = new RenderTexture(4, 4, 0);
        try
        {
            int wind = Shader.PropertyToID("_WindTexture");
            material.SetTexture(wind, source);
            Assert.That(source.IsCreated(), Is.False);
            Type passType = typeof(GrassDataRendererFeature).GetNestedType("GrassDataPass", BindingFlags.NonPublic);
            var resolve = (Func<Material, Texture>)passType.GetMethod("ResolveWindTexture",
                BindingFlags.Static | BindingFlags.NonPublic).CreateDelegate(typeof(Func<Material, Texture>));

            Assert.That(resolve(material), Is.SameAs(Texture2D.grayTexture));
            Assert.That(material.GetTexture(wind), Is.SameAs(source));
            Assert.That(source.IsCreated(), Is.False,
                "The consumer cannot restore pixels by allocating a producer's missing GPU storage.");

            material.SetTexture(wind, Texture2D.whiteTexture);
            Assert.That(resolve(material), Is.SameAs(Texture2D.whiteTexture));
            material.SetTexture(wind, null);
            Assert.That(resolve(material), Is.SameAs(Texture2D.grayTexture));
        }
        finally
        {
            Object.DestroyImmediate(material);
            Object.DestroyImmediate(source);
        }
    }

    [Test]
    public void CaptureDeclarationsIncludeRendererAndMaterialTextureOverridesWithoutStaleEntries()
    {
        Shader shader = Shader.Find("InfiniteGrass/Modifiers/GrassMaskShader");
        Assert.That(shader, Is.Not.Null);
        var material = new Material(shader);
        var gameObject = new GameObject("Grass capture texture overrides");
        var external = new RenderTexture(4, 4, 0);
        try
        {
            Renderer renderer = gameObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = new[] { material, material };
            int mainTexture = Shader.PropertyToID("_MainTex");
            material.SetTexture(mainTexture, Texture2D.whiteTexture);
            var block = new MaterialPropertyBlock();
            block.SetTexture(mainTexture, external);
            renderer.SetPropertyBlock(block);
            block.SetTexture(mainTexture, Texture2D.blackTexture);
            renderer.SetPropertyBlock(block, 1);

            var scratch = new MaterialPropertyBlock();
            var textures = new Texture[3];
            Type passType = typeof(GrassDataRendererFeature).GetNestedType("GrassDataPass", BindingFlags.NonPublic);
            var collect = (Func<Renderer, Material, int, MaterialPropertyBlock, Texture[], int>)passType.GetMethod(
                "CollectCaptureTextures", BindingFlags.Static | BindingFlags.NonPublic).CreateDelegate(
                typeof(Func<Renderer, Material, int, MaterialPropertyBlock, Texture[], int>));

            Assert.That(collect(renderer, material, 1, scratch, textures), Is.EqualTo(3));
            Assert.That(textures, Is.EqualTo(new Texture[] { Texture2D.whiteTexture, external, Texture2D.blackTexture }),
                "DrawRenderer may read a render texture supplied only through a property block.");
            Assert.That(collect(renderer, material, 0, scratch, textures), Is.EqualTo(2));
            Assert.That(textures, Is.EqualTo(new Texture[] { Texture2D.whiteTexture, external, null }),
                "Another material index must not retain a previous override in reused scratch storage.");

            block.SetTexture(mainTexture, external);
            renderer.SetPropertyBlock(block, 1);
            Assert.That(collect(renderer, material, 1, scratch, textures), Is.EqualTo(2),
                "The same physical texture in both override scopes needs only one declaration.");
            Assert.That(textures[2], Is.Null);

            block.SetTexture(mainTexture, Texture2D.grayTexture);
            renderer.SetPropertyBlock(block);
            renderer.SetPropertyBlock(null, 1);
            Assert.That(collect(renderer, material, 1, scratch, textures), Is.EqualTo(2));
            Assert.That(textures, Is.EqualTo(new Texture[] { Texture2D.whiteTexture, Texture2D.grayTexture, null }));

            renderer.SetPropertyBlock(null);
            material.SetTexture(mainTexture, Texture2D.blackTexture);
            Assert.That(collect(renderer, material, 1, scratch, textures), Is.EqualTo(1));
            Assert.That(textures, Is.EqualTo(new Texture[] { Texture2D.blackTexture, null, null }),
                "Removed overrides and replaced material textures must not remain in the next pass's dependencies.");

            Shader heightShader = Shader.Find("InfiniteGrass/GrassHeightMapShader");
            Assert.That(heightShader, Is.Not.Null);
            material.shader = heightShader;
            block.SetTexture(mainTexture, external);
            renderer.SetPropertyBlock(block);
            Assert.That(material.HasProperty(mainTexture), Is.False);
            Assert.That(collect(renderer, material, 1, scratch, textures), Is.Zero);
            Assert.That(textures, Is.EqualTo(new Texture[3]),
                "The height override material does not sample the renderer's modifier texture.");
        }
        finally
        {
            Object.DestroyImmediate(gameObject);
            Object.DestroyImmediate(material);
            Object.DestroyImmediate(external);
        }
    }
}
