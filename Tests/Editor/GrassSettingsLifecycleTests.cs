using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
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

    [Test]
    public void NewSceneSettingsReplaceAPreviewOwnerBeforeItsNextUpdate()
    {
        InfiniteGrassRenderer previous = InfiniteGrassRenderer.Instance;
        bool previousEnabled = previous && previous.enabled;
        if (previous)
            previous.enabled = false;
        Scene preview = default;
        GameObject originalObject = null;
        GameObject replacementObject = null;
        try
        {
            originalObject = new GameObject("Grass owner leaving the scene");
            InfiniteGrassRenderer original = originalObject.AddComponent<InfiniteGrassRenderer>();
            Assert.That(original.IsReadyForRendering, Is.True);
            Mesh[] meshes = original.GetLodMeshes();
            preview = EditorSceneManager.NewPreviewScene();
            SceneManager.MoveGameObjectToScene(originalObject, preview);
            Assert.That(original.isActiveAndEnabled, Is.True);
            Assert.That(original.IsReadyForRendering, Is.False);
            Assert.That(InfiniteGrassRenderer.Instance, Is.SameAs(original),
                "Registration must encounter the stale owner before its deferred Update cleanup.");

            // Do not yield an Update between the scene move and registration.
            replacementObject = new GameObject("Replacement grass scene owner");
            InfiniteGrassRenderer replacement = replacementObject.AddComponent<InfiniteGrassRenderer>();

            Assert.That(replacement.enabled, Is.True);
            Assert.That(replacement.IsReadyForRendering, Is.True);
            Assert.That(InfiniteGrassRenderer.Instance, Is.SameAs(replacement));
            foreach (Mesh mesh in meshes)
                Assert.That(mesh == null, Is.True, "The invalidated owner's generated meshes must be released.");
            LogAssert.NoUnexpectedReceived();
        }
        finally
        {
            if (replacementObject)
                Object.DestroyImmediate(replacementObject);
            if (originalObject)
                Object.DestroyImmediate(originalObject);
            if (preview.IsValid())
                EditorSceneManager.ClosePreviewScene(preview);
            if (previous)
                previous.enabled = previousEnabled;
        }
    }

    [Test]
    public void EligibleCurrentOwnerStillRejectsAdditionalSceneSettings()
    {
        InfiniteGrassRenderer previous = InfiniteGrassRenderer.Instance;
        bool previousEnabled = previous && previous.enabled;
        if (previous)
            previous.enabled = false;
        GameObject originalObject = null;
        GameObject duplicateObject = null;
        try
        {
            originalObject = new GameObject("Eligible grass scene owner");
            InfiniteGrassRenderer original = originalObject.AddComponent<InfiniteGrassRenderer>();
            Mesh[] meshes = original.GetLodMeshes();
            duplicateObject = new GameObject("Duplicate grass scene owner");
            LogAssert.Expect(LogType.Warning, "Only one enabled Infinite Grass Renderer can own the scene settings.");

            InfiniteGrassRenderer duplicate = duplicateObject.AddComponent<InfiniteGrassRenderer>();

            Assert.That(duplicate.enabled, Is.False);
            Assert.That(InfiniteGrassRenderer.Instance, Is.SameAs(original));
            Assert.That(original.IsReadyForRendering, Is.True);
            foreach (Mesh mesh in meshes)
                Assert.That(mesh != null, Is.True, "Rejecting a duplicate must preserve the eligible owner's meshes.");
            LogAssert.NoUnexpectedReceived();
        }
        finally
        {
            if (duplicateObject)
                Object.DestroyImmediate(duplicateObject);
            if (originalObject)
                Object.DestroyImmediate(originalObject);
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

    [TestCase(1000f, -100f, -100f, 1000f)]
    [TestCase(float.NaN, float.NaN, -100f, 1000f)]
    [TestCase(float.NegativeInfinity, float.PositiveInfinity, -100f, 1000f)]
    [TestCase(5000f, float.NaN, 1000f, 5000f)]
    [TestCase(float.NaN, -500f, -500f, -100f)]
    [TestCase(10f, 11f, 10f, 11f)]
    public void EquivalentCaptureHeightRangesHaveFiniteStableCacheIdentity(float first, float second,
        float expectedMin, float expectedMax)
    {
        Type passType = typeof(GrassDataRendererFeature).GetNestedType("GrassDataPass", BindingFlags.NonPublic);
        var resolve = (Func<Vector2, Vector2>)passType.GetMethod("ResolveCaptureHeightRange",
            BindingFlags.Static | BindingFlags.NonPublic).CreateDelegate(typeof(Func<Vector2, Vector2>));
        Vector2 requested = new Vector2(first, second);
        Vector2 cachedRange = resolve(requested);

        Assert.That(cachedRange, Is.EqualTo(new Vector2(expectedMin, expectedMax)));
        Assert.That(cachedRange == resolve(requested), Is.True,
            "The next frame must reuse the same mapping after invalid endpoints resolve to defaults.");
        Assert.That(cachedRange == resolve(new Vector2(expectedMax, expectedMin)), Is.True,
            "Reversed endpoints describe the same capture volume.");
        Assert.That(cachedRange != resolve(new Vector2(expectedMin, expectedMax + 1f)), Is.True,
            "A changed rendered volume must still invalidate the capture.");
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

    [Test]
    public void InitiallyInactiveModifierBecomesDrawableFromTheSameInventory()
    {
        using (var fixture = new RendererFixture())
        {
            GameObject modifierObject = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Material material = null;
            try
            {
                modifierObject.SetActive(false);
                Shader shader = Shader.Find("InfiniteGrass/Modifiers/GrassMaskShader");
                Assert.That(shader, Is.Not.Null);
                material = new Material(shader);
                Renderer renderer = modifierObject.GetComponent<Renderer>();
                renderer.sharedMaterial = material;
                InvokePrivate(fixture.Pass, "EnsureInventory", fixture.Owner);
                object inventory = GetField(fixture.Pass, "modifierInventory");
                Assert.That(((IList)inventory).Contains(renderer), Is.True,
                    "Initial discovery must retain an existing inactive modifier for later activation.");
                object state = fixture.CameraStates[0];
                SetField(state, "ModifierRenderers", inventory);
                MethodInfo collect = fixture.Pass.GetType().GetMethod("CollectRendererDraws",
                    BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new[] { state.GetType(), typeof(Bounds), typeof(bool), typeof(bool), typeof(bool) }, null);
                object[] arguments = { state, new Bounds(Vector3.zero, Vector3.one * 20f), false, true, false };
                IList draws = (IList)GetField(state, "MaskDraws");
                uint inventoryRevision = (uint)GetField(fixture.Pass, "inventoryRevision");
                collect.Invoke(fixture.Pass, arguments);
                Assert.That(ContainsDraw(draws, renderer), Is.False);

                modifierObject.SetActive(true);
                collect.Invoke(fixture.Pass, arguments);
                Assert.That(ContainsDraw(draws, renderer), Is.True);
                Assert.That(GetField(fixture.Pass, "modifierInventory"), Is.SameAs(inventory));
                Assert.That(GetField(fixture.Pass, "inventoryRevision"), Is.EqualTo(inventoryRevision),
                    "Activation must be handled by capture eligibility without another scene search.");

                renderer.enabled = false;
                collect.Invoke(fixture.Pass, arguments);
                Assert.That(ContainsDraw(draws, renderer), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(modifierObject);
                Object.DestroyImmediate(material);
            }
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void InitiallyInactiveTerrainJoinsSurfaceCaptureWithoutInventoryRefresh(bool disabledComponent)
    {
        using (var fixture = new RendererFixture(1 << 31))
        {
            var terrainObject = new GameObject("Initially inactive grass surface terrain") { layer = 31 };
            TerrainData data = null;
            try
            {
                terrainObject.SetActive(false);
                data = new TerrainData { heightmapResolution = 33, size = new Vector3(10f, 2f, 10f) };
                Terrain terrain = terrainObject.AddComponent<Terrain>();
                terrain.terrainData = data;
                terrain.enabled = !disabledComponent;
                terrainObject.SetActive(disabledComponent);
                Assert.That(terrain.isActiveAndEnabled, Is.False);
                InvokePrivate(fixture.Pass, "EnsureInventory", fixture.Owner);
                object inventory = GetField(fixture.Pass, "terrainInventory");
                Assert.That(((IList)inventory).Contains(terrain), Is.True);
                object state = fixture.CameraStates[0];
                object[] arguments = { state, new Bounds(Vector3.zero, Vector3.one * 40f), false };
                IList surfaces = (IList)GetField(state, "SurfaceTerrains");
                uint inventoryRevision = (uint)GetField(fixture.Pass, "inventoryRevision");
                InvokePrivate(fixture.Pass, "CollectSurfaceTerrains", arguments);
                Assert.That(surfaces.Contains(terrain), Is.False);

                terrainObject.SetActive(true);
                terrain.enabled = true;
                InvokePrivate(fixture.Pass, "CollectSurfaceTerrains", arguments);
                Assert.That(surfaces.Contains(terrain), Is.True);
                Assert.That(GetField(fixture.Pass, "terrainInventory"), Is.SameAs(inventory));
                Assert.That(GetField(fixture.Pass, "inventoryRevision"), Is.EqualTo(inventoryRevision));

                terrain.enabled = false;
                InvokePrivate(fixture.Pass, "CollectSurfaceTerrains", arguments);
                Assert.That(surfaces.Contains(terrain), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(terrainObject);
                Object.DestroyImmediate(data);
            }
        }
    }

    [TestCase(HideFlags.None)]
    [TestCase(HideFlags.DontSave)]
    public void ActiveTerrainInventoryPreservesNativeSourcesWithoutDuplicates(HideFlags flags)
    {
        using (var fixture = new RendererFixture(1 << 31))
        {
            var terrainObject = new GameObject("Native active terrain inventory") { layer = 31 };
            TerrainData data = null;
            try
            {
                data = new TerrainData { heightmapResolution = 33, size = new Vector3(10f, 2f, 10f) };
                Terrain terrain = terrainObject.AddComponent<Terrain>();
                terrain.terrainData = data;
                terrain.hideFlags = flags;
                Assert.That(Array.IndexOf(Terrain.activeTerrains, terrain), Is.GreaterThanOrEqualTo(0));
                if (flags == HideFlags.DontSave)
                    Assert.That(Array.IndexOf(Object.FindObjectsByType<Terrain>(FindObjectsInactive.Include), terrain),
                        Is.LessThan(0), "The native active list covers sources normal object discovery excludes.");

                InvokePrivate(fixture.Pass, "EnsureInventory", fixture.Owner);
                IList inventory = (IList)GetField(fixture.Pass, "terrainInventory");
                int retained = 0;
                foreach (Terrain source in inventory)
                    if (source == terrain)
                        retained++;
                Assert.That(retained, Is.EqualTo(1));
                object state = fixture.CameraStates[0];
                InvokePrivate(fixture.Pass, "CollectSurfaceTerrains", state,
                    new Bounds(Vector3.zero, Vector3.one * 40f), false);
                Assert.That(((IList)GetField(state, "SurfaceTerrains")).Contains(terrain), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(terrainObject);
                Object.DestroyImmediate(data);
            }
        }
    }

    [Test]
    public void ExplicitMaterialSlotCountInvalidatesHeightWithUnchangedMeshAndBounds()
    {
        using (var fixture = new RendererFixture())
        {
            var surfaceObject = new GameObject("Grass surface with two height subsets");
            var areaObject = new GameObject("Grass area for subset cache");
            Mesh mesh = null;
            try
            {
                mesh = new Mesh
                {
                    vertices = new[]
                    {
                        Vector3.zero, Vector3.right, Vector3.forward,
                        Vector3.up * 2f, Vector3.right + Vector3.up * 2f, Vector3.forward + Vector3.up * 2f
                    },
                    subMeshCount = 2
                };
                mesh.SetIndices(new[] { 0, 1, 2 }, MeshTopology.Triangles, 0);
                mesh.SetIndices(new[] { 3, 4, 5 }, MeshTopology.Triangles, 1);
                surfaceObject.AddComponent<MeshFilter>().sharedMesh = mesh;
                Renderer renderer = surfaceObject.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = new Material[1];
                Collider collider = surfaceObject.AddComponent<BoxCollider>();
                GrassPlacementArea area = areaObject.AddComponent<GrassPlacementArea>();
                SetField(area, "paintSurface", collider);
                area.MarkDirty();
                Assert.That(area.TryGetCaptureData(out _), Is.True);
                Bounds bounds = renderer.bounds;
                uint surfaceRevision = area.SurfaceRevision;
                object state = fixture.CameraStates[0];
                object[] arguments = { state, new Bounds(Vector3.zero, Vector3.one * 20f), true };
                InvokePrivate(fixture.Pass, "CollectSources", arguments);
                ulong oneSlot = (ulong)GetField(state, "NextSurfaceVersion");

                renderer.sharedMaterials = new Material[2];
                Assert.That(area.TryGetCaptureData(out _), Is.True);
                Assert.That(area.SurfaceRevision, Is.EqualTo(surfaceRevision));
                Assert.That(renderer.bounds, Is.EqualTo(bounds));
                Assert.That(surfaceObject.GetComponent<MeshFilter>().sharedMesh, Is.SameAs(mesh));
                InvokePrivate(fixture.Pass, "CollectSources", arguments);
                ulong twoSlots = (ulong)GetField(state, "NextSurfaceVersion");
                Assert.That(twoSlots, Is.Not.EqualTo(oneSlot),
                    "The second height subset becomes drawable even though source readiness stayed true.");

                renderer.sharedMaterials = new[]
                {
                    AssetDatabase.LoadAssetAtPath<Material>("Packages/com.cone.grass/Runtime/Materials/Grass Blade.mat"), null
                };
                InvokePrivate(fixture.Pass, "CollectSources", arguments);
                Assert.That(GetField(state, "NextSurfaceVersion"), Is.EqualTo(twoSlots),
                    "Height capture overrides material values; only the number of slots affects its draw selection.");

                renderer.sharedMaterials = new Material[1];
                InvokePrivate(fixture.Pass, "CollectSources", arguments);
                Assert.That(GetField(state, "NextSurfaceVersion"), Is.EqualTo(oneSlot));
            }
            finally
            {
                Object.DestroyImmediate(areaObject);
                Object.DestroyImmediate(surfaceObject);
                Object.DestroyImmediate(mesh);
            }
        }
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void InPlaceBladeIndexRangeEditsRefreshArgumentsWithoutChangingTheMesh(int changedField)
    {
        using (var fixture = new RendererFixture())
        {
            Mesh[] meshes = fixture.Owner.GetLodMeshes();
            Mesh mesh = fixture.Owner.GetGrassMeshCache();
            Assert.That(mesh, Is.SameAs(meshes[0]));
            // Retain the original index buffer while restricting its first
            // submesh, leaving room to move that range without replacing it.
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, 6));
            object state = fixture.CameraStates[0];
            Assert.That(InvokePrivate(state, "UpdateArgumentData", (object)meshes), Is.True);
            Assert.That(InvokePrivate(state, "UpdateArgumentData", (object)meshes), Is.False,
                "An unchanged mesh layout must not request another CPU upload.");

            SubMeshDescriptor subset = mesh.GetSubMesh(0);
            if (changedField == 0)
                subset.indexCount = 3;
            else if (changedField == 1)
                subset.indexStart = 3;
            else
                subset.baseVertex = 1;
            mesh.SetSubMesh(0, subset);

            Assert.That(fixture.Owner.GetGrassMeshCache(), Is.SameAs(mesh));
            Assert.That(InvokePrivate(state, "UpdateArgumentData", (object)meshes), Is.True,
                "Changing a native index range must refresh arguments even when its Mesh identity is unchanged.");
            var arguments = (GraphicsBuffer.IndirectDrawIndexedArgs[])GetField(state, "argumentData");
            Assert.That(arguments[0].indexCountPerInstance, Is.EqualTo(mesh.GetIndexCount(0)));
            Assert.That(arguments[0].startIndex, Is.EqualTo(mesh.GetIndexStart(0)));
            Assert.That(arguments[0].baseVertexIndex, Is.EqualTo(mesh.GetBaseVertex(0)));
            foreach (GraphicsBuffer.IndirectDrawIndexedArgs argument in arguments)
            {
                Assert.That(argument.instanceCount, Is.Zero,
                    "Visible instance counts remain owned by the generation reset/finalize kernels.");
                Assert.That(argument.startInstance, Is.Zero);
            }
            Assert.That(InvokePrivate(state, "UpdateArgumentData", (object)meshes), Is.False);
            Assert.That(GetField(state, "Positions"), Is.Null);
            Assert.That(GetField(state, "Counts"), Is.Null);
            Assert.That(GetField(state, "Arguments"), Is.Null,
                "Refreshing index metadata does not allocate or recreate GPU buffers.");
        }
    }

    [Test]
    public void CompletedAndShorterDispatchPlansReleaseGroupsWhilePreservingResolvedInputs()
    {
        using (var fixture = new DispatchFixture(4f))
        {
            TextureHandle firstDensity = fixture.AddGroup();
            TextureHandle secondDensity = fixture.AddGroup();
            fixture.Build();
            Assert.That(GetField(fixture.State, "DispatchCount"), Is.EqualTo(2));
            Assert.That(GetField(fixture.State, "CandidateCount"), Is.EqualTo(50L));
            object first = fixture.Dispatches[0], second = fixture.Dispatches[1];
            int[] start = (int[])GetField(first, "Start");
            int[] size = (int[])GetField(first, "SizeInCells");
            Assert.That(start, Is.EqualTo(new[] { 0, 0 }));
            Assert.That(size, Is.EqualTo(new[] { 5, 5 }));
            Assert.That(GetField(first, "Density"), Is.EqualTo(firstDensity));
            Assert.That(GetField(second, "Density"), Is.EqualTo(secondDensity));
            foreach (object dispatch in fixture.Dispatches)
            {
                Assert.That(GetField(dispatch, "Group"), Is.Null,
                    "Completed plans cannot retain a group's terrain or sparse tile storage.");
                Assert.That(GetField(dispatch, "TerrainHeight"), Is.EqualTo(fixture.Black));
                Assert.That(GetField(dispatch, "TerrainHoles"), Is.EqualTo(fixture.White));
                Assert.That(GetField(dispatch, "UseTerrain"), Is.False);
                Assert.That(GetField(dispatch, "HasHoles"), Is.False);
                Assert.That(GetField(dispatch, "Origin"), Is.EqualTo(Vector3.zero));
                Assert.That(GetField(dispatch, "Size"), Is.EqualTo(Vector3.one));
            }

            fixture.Groups.Clear();
            TextureHandle replacementDensity = fixture.AddGroup();
            fixture.Build();
            Assert.That(GetField(fixture.State, "DispatchCount"), Is.EqualTo(1));
            Assert.That(GetField(fixture.State, "CandidateCount"), Is.EqualTo(25L));
            Assert.That(fixture.Dispatches.Count, Is.EqualTo(2), "The high-water pool remains reusable.");
            Assert.That(fixture.Dispatches[0], Is.SameAs(first));
            Assert.That(fixture.Dispatches[1], Is.SameAs(second));
            Assert.That(GetField(first, "Start"), Is.SameAs(start));
            Assert.That(GetField(first, "SizeInCells"), Is.SameAs(size));
            Assert.That(GetField(first, "Density"), Is.EqualTo(replacementDensity));
            Assert.That(GetField(first, "Group"), Is.Null);
            Assert.That(GetField(second, "Group"), Is.Null,
                "An unused tail record must not keep a group from the previous larger plan alive.");
            fixture.AssertNoGpuAllocation();
        }
    }

    [Test]
    public void RejectedDispatchPlanReleasesAlreadyPlannedGroupsBeforeResettingCounts()
    {
        // Each group covers exactly the full candidate budget. The second group
        // rejects the plan after the first has populated reusable dispatch records.
        using (var fixture = new DispatchFixture(8191f))
        {
            fixture.AddGroup();
            fixture.AddGroup();
            LogAssert.Expect(LogType.Warning,
                "Grass generation was skipped: the camera/source grid exceeds the safety budget of " +
                GrassDispatchMath.MaximumCandidates +
                " candidate cells. Increase spacing, reduce draw distance, or use smaller authored areas.");
            fixture.Build();
            Assert.That(fixture.Dispatches.Count, Is.GreaterThan(0),
                "The regression requires a partial plan that held group references before rejection.");
            Assert.That(GetField(fixture.State, "DispatchCount"), Is.Zero);
            Assert.That(GetField(fixture.State, "CandidateCount"), Is.Zero);
            Assert.That(GetField(fixture.State, "WarnedBudget"), Is.True);
            foreach (object dispatch in fixture.Dispatches)
                Assert.That(GetField(dispatch, "Group"), Is.Null);
            fixture.AssertNoGpuAllocation();
        }
    }

    [Test]
    public void DispatchImportFailureReleasesPlanningGroups()
    {
        using (var fixture = new DispatchFixture(4f))
        {
            fixture.AddGroup();
            TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
                fixture.Build(failImport: true));
            Assert.That(exception.InnerException, Is.TypeOf<NullReferenceException>());
            Assert.That(GetField(fixture.State, "DispatchCount"), Is.EqualTo(1),
                "Texture binding must fail after the source has produced a dispatch.");
            Assert.That(GetField(fixture.Dispatches[0], "Group"), Is.Null);
            fixture.AssertNoGpuAllocation();
        }
    }

    private static bool ContainsDraw(IList draws, Renderer renderer)
    {
        foreach (object draw in draws)
            if (ReferenceEquals(GetField(draw, "Renderer"), renderer))
                return true;
        return false;
    }

    private static object InvokePrivate(object owner, string name, params object[] arguments) =>
        owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(owner, arguments);

    private static object GetField(object owner, string name) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(owner);

    private static void SetField(object owner, string name, object value) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(owner, value);

    private sealed class DispatchFixture : IDisposable
    {
        public readonly object State;
        public readonly IList Groups, Dispatches;
        public readonly TextureHandle Black, White;
        private readonly RendererFixture renderer;
        private readonly RenderGraph graph;
        private readonly RenderTexture capture;
        private readonly Bounds bounds;

        public DispatchFixture(float extent)
        {
            try
            {
                renderer = new RendererFixture();
                State = renderer.CameraStates[0];
                Groups = (IList)GetField(State, "ActiveGroups");
                Dispatches = (IList)GetField(State, "Dispatches");
                bounds = new Bounds(new Vector3(extent * 0.5f, 0f, extent * 0.5f),
                    new Vector3(extent, 0f, extent));
                capture = new RenderTexture(128, 128, 0);
                SetField(State, "Density", RTHandles.Alloc(capture));
                SetField(State, "CaptureExtent", extent * 0.5f);
                // Record distinct handles without compiling or executing the graph.
                // The planner needs capture dimensions but never its GPU storage.
                graph = new RenderGraph("Grass Dispatch Lifetime Test");
                Black = RecordTexture();
                White = RecordTexture();
                var imported = (IDictionary)GetField(State, "ImportedTextures");
                imported.Add(Texture2D.blackTexture, Black);
                imported.Add(Texture2D.whiteTexture, White);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public TextureHandle AddGroup()
        {
            Type passType = renderer.Pass.GetType();
            object group = Activator.CreateInstance(passType.GetNestedType("TerrainGroup", BindingFlags.NonPublic), true);
            object source = Activator.CreateInstance(passType.GetNestedType("PlacementSource", BindingFlags.NonPublic));
            SetField(source, "Data", new GrassPlacementDrawData
            {
                WorldBounds = bounds,
                WorldToMask = Matrix4x4.TRS(bounds.min, Quaternion.identity,
                    new Vector3(bounds.size.x, 1f, bounds.size.z)).inverse,
                Shape = GrassPlacementShape.Box,
                Density = 1f
            });
            ((IList)GetField(group, "Sources")).Add(source);
            TextureHandle density = RecordTexture();
            SetField(group, "DensityTexture", density);
            Groups.Add(group);
            return density;
        }

        public void Build(bool failImport = false)
        {
            if (failImport)
                ((IDictionary)GetField(State, "ImportedTextures")).Remove(Texture2D.blackTexture);
            // A missing graph deliberately injects an import failure after CPU
            // planning, exercising the same cleanup as a real binding exception.
            InvokePrivate(renderer.Pass, "BuildDispatches", failImport ? null : graph,
                State, renderer.Owner, bounds, 1f, true, Black);
        }

        public void AssertNoGpuAllocation()
        {
            Assert.That(capture.IsCreated(), Is.False);
            Assert.That(GetField(State, "Positions"), Is.Null);
            Assert.That(GetField(State, "Counts"), Is.Null);
            Assert.That(GetField(State, "Arguments"), Is.Null);
        }

        private TextureHandle RecordTexture() => graph.CreateTexture(new TextureDesc(1, 1)
        {
            name = "Grass Dispatch Test Input",
            format = GraphicsFormat.R8G8B8A8_UNorm,
            dimension = TextureDimension.Tex2D,
            msaaSamples = MSAASamples.None
        });

        public void Dispose()
        {
            graph?.Cleanup();
            renderer?.Dispose();
            if (capture)
                Object.DestroyImmediate(capture);
        }
    }

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

        public RendererFixture(int surfaceLayerMask = 0)
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
                SetField(Feature, "heightMapLayer", (LayerMask)surfaceLayerMask);
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
    [Category("GrassGPU")]
    public void WindTextureReconfigurationRejectsOnlyUnresolvedMultisampledStorage()
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || SystemInfo.supportsMultisampledTextures == 0)
            Assert.Ignore("The wind input regression requires multisampled texture storage.");
        var descriptor = new RenderTextureDescriptor(4, 4)
        {
            graphicsFormat = GraphicsFormat.R8G8B8A8_UNorm,
            depthBufferBits = 0,
            msaaSamples = 4,
            bindMS = true,
            sRGB = false
        };
        if (SystemInfo.GetRenderTextureSupportedMSAASampleCount(descriptor) != 4)
            Assert.Ignore("The active graphics device does not support the requested 4x MSAA wind texture.");

        Shader shader = Shader.Find("InfiniteGrass/GrassBladeShader");
        Assert.That(shader, Is.Not.Null);
        var material = new Material(shader);
        var source = new RenderTexture(descriptor);
        try
        {
            int wind = Shader.PropertyToID("_WindTexture");
            material.SetTexture(wind, source);
            Type passType = typeof(GrassDataRendererFeature).GetNestedType("GrassDataPass", BindingFlags.NonPublic);
            var resolve = (Func<Material, Texture>)passType.GetMethod("ResolveWindTexture",
                BindingFlags.Static | BindingFlags.NonPublic).CreateDelegate(typeof(Func<Material, Texture>));
            var configurations = new[]
            {
                (Samples: 4, BindMS: true, Reject: true),
                (Samples: 4, BindMS: false, Reject: false),
                (Samples: 1, BindMS: true, Reject: false),
                (Samples: 1, BindMS: false, Reject: false),
                (Samples: 4, BindMS: true, Reject: true)
            };
            foreach (var configuration in configurations)
            {
                source.Release();
                source.antiAliasing = configuration.Samples;
                source.bindTextureMS = configuration.BindMS;
                Assert.That(source.Create(), Is.True);
                Assert.That(source.antiAliasing, Is.EqualTo(configuration.Samples));
                Assert.That(source.bindTextureMS, Is.EqualTo(configuration.BindMS));

                Assert.That(resolve(material), Is.SameAs(configuration.Reject ? Texture2D.grayTexture : (Texture)source));
                Assert.That(material.GetTexture(wind), Is.SameAs(source),
                    "Resolving wind compatibility must preserve the producer's material assignment.");
                Assert.That(source.IsCreated(), Is.True,
                    "The consumer must not release or replace the producer's storage.");
            }
        }
        finally
        {
            source.Release();
            Object.DestroyImmediate(material);
            Object.DestroyImmediate(source);
        }
    }

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
