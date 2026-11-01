using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Cone.Grass.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Cone.Grass.MicroVerse
{
    /// <summary>Exercises saved native TerrainLit bakes through the optional bridge's public editor API.</summary>
    [Category("GrassGPU")]
    public sealed class GrassMicroVerseGroundBakeTests
    {
        private readonly List<Object> _owned = new List<Object>();
        private string _folder, _diffusePath, _outputPath;
        private Terrain _terrain;
        private TerrainData _data;
        private TerrainLayer _layer;
        private Texture2D _diffuse, _density;
        private GrassMicroVerseTestMaskTarget _target;
        private GrassMicroVerseBridge _bridge;
        private int _bakeCount;
        private Scene _savedScene;

        [SetUp]
        public void SetUp()
        {
            if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset) ||
                SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Assert.Ignore("Ground-bake bridge tests require a URP Editor project with a graphics device.");
            }

            Shader shader = Shader.Find(TerrainGrassAlbedoBaker.NativeTerrainShaderName);
            Assert.That(shader, Is.Not.Null, "The native URP Terrain/Lit shader must be installed.");
            GrassMicroVerseBridgeUtility.ClearCaches();
            string name = "GrassGroundBridgeTests_" + Guid.NewGuid().ToString("N");
            _folder = "Assets/" + name;
            AssetDatabase.CreateFolder("Assets", name);
            _diffusePath = _folder + "/Diffuse.asset";
            _outputPath = _folder + "/Ground.asset";

            _diffuse = Own(new Texture2D(2, 2, TextureFormat.RGBA32, false, true));
            _diffuse.name = "Terrain diffuse";
            _diffuse.wrapMode = TextureWrapMode.Repeat;
            SetDiffuse(Color.red);
            AssetDatabase.CreateAsset(_diffuse, _diffusePath);
            _layer = Own(new TerrainLayer
            {
                diffuseTexture = _diffuse,
                tileSize = new Vector2(64f, 64f),
                diffuseRemapMin = Vector4.zero,
                diffuseRemapMax = Vector4.one,
                maskMapRemapMin = Vector4.zero,
                maskMapRemapMax = Vector4.one
            });
            AssetDatabase.CreateAsset(_layer, _folder + "/Grass.terrainlayer");
            _data = Own(new TerrainData
            {
                heightmapResolution = 33,
                alphamapResolution = 16,
                size = new Vector3(64f, 8f, 64f)
            });
            AssetDatabase.CreateAsset(_data, _folder + "/Terrain.asset");
            _data.terrainLayers = new[] { _layer };
            float[,,] controls = new float[16, 16, 1];
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    controls[y, x, 0] = 1f;
                }
            }

            _data.SetAlphamaps(0, 0, controls);

            GameObject terrainObject = Own(Terrain.CreateTerrainGameObject(_data));
            _terrain = terrainObject.GetComponent<Terrain>();
            Material material = Own(new Material(shader));
            material.DisableKeyword("_TERRAIN_BLEND_HEIGHT");
            material.SetFloat("_EnableHeightBlend", 0f);
            AssetDatabase.CreateAsset(material, _folder + "/Terrain.mat");
            _terrain.materialTemplate = material;

            _target = Own(ScriptableObject.CreateInstance<GrassMicroVerseTestMaskTarget>());
            AssetDatabase.CreateAsset(_target, _folder + "/Mask.asset");
            _density = Own(new Texture2D(8, 8, TextureFormat.R8, false, true) { name = "Tile grass density" });
            byte[] pixels = new byte[64];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = 255;
            }

            _density.LoadRawTextureData(pixels);
            _density.Apply(false, false);
            AssetDatabase.AddObjectToAsset(_density, _target);
            foreach (Object source in new Object[] { _diffuse, _layer, material, _density, _target })
            {
                EditorUtility.SetDirty(source);
                AssetDatabase.SaveAssetIfDirty(source);
            }
            for (int i = 0; i < _data.alphamapTextureCount; i++)
            {
                EditorUtility.SetDirty(_data.GetAlphamapTexture(i));
            }

            EditorUtility.SetDirty(_data);
            AssetDatabase.SaveAssetIfDirty(_data);

            _bridge = AddBridge(terrainObject, 64, true);
            _bakeCount = 0;
            TerrainGrassAlbedoBaker.Baked += OnBaked;
        }

        [TearDown]
        public void TearDown()
        {
            TerrainGrassAlbedoBaker.Baked -= OnBaked;
            if (_savedScene.IsValid() && _savedScene.isLoaded)
            {
                EditorSceneManager.CloseScene(_savedScene, true);
            }

            _savedScene = default;
            for (int i = _owned.Count - 1; i >= 0; i--)
            {
                if (_owned[i] && !AssetDatabase.Contains(_owned[i]))
                {
                    Object.DestroyImmediate(_owned[i]);
                }
            }

            _owned.Clear();
            if (!string.IsNullOrEmpty(_folder))
            {
                AssetDatabase.DeleteAsset(_folder);
            }

            _folder = null;
            _terrain = null;
            GrassMicroVerseBridgeUtility.ClearCaches();
        }

        [Test]
        public void UnchangedSavedPollsReuseOutputAndRemainBuildValid()
        {
            Texture2D output = Refresh();
            Assert.That(_bakeCount, Is.EqualTo(1));
            Assert.That(_bridge.GroundBakeSourceKey, Is.Not.Null.And.Not.Empty);
            Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(_bridge, out string message), Is.True, message);
            string guid = AssetDatabase.AssetPathToGUID(_outputPath);
            uint revision = _bridge.PlacementArea.SourceRevision;

            for (int i = 0; i < 4; i++)
            {
                Assert.That(Refresh(), Is.SameAs(output));
            }

            Assert.That(_bakeCount, Is.EqualTo(1), "Unchanged polls must not render or read back another terrain bake.");
            Assert.That(_bridge.PlacementArea.SourceRevision, Is.EqualTo(revision));
            Assert.That(AssetDatabase.AssetPathToGUID(_outputPath), Is.EqualTo(guid));
            AssertColor(output.GetPixel(32, 32), Color.red);
        }

        [TestCase("pixels", false)]
        [TestCase("pixels", true)]
        [TestCase("wrapV", false)]
        [TestCase("wrapV", true)]
        [TestCase("format", false)]
        [TestCase("format", true)]
        public void EditedLiveOutputIsRebakedOnceWithOrWithoutSessionCache(string change, bool clearCache)
        {
            Texture2D output = Refresh();
            string guid = AssetDatabase.AssetPathToGUID(_outputPath);
            string sourceKey = _bridge.GroundBakeSourceKey;
            string outputKey = _bridge.GroundBakeOutputKey;
            Hash128 sourceHash = AssetDatabase.GetAssetDependencyHash(_diffusePath);
            Color[] sourcePixels = _diffuse.GetPixels();
            uint outputUpdateCount = output.updateCount;

            if (change == "wrapV")
            {
                output.wrapModeV = TextureWrapMode.Repeat;
                EditorUtility.ClearDirty(output);
                Assert.That(output.wrapModeU, Is.EqualTo(TextureWrapMode.Clamp));
                Assert.That(output.updateCount, Is.EqualTo(outputUpdateCount), "The sampler edit does not upload pixels.");
            }
            else
            {
                if (change == "format")
                {
                    Assert.That(output.Reinitialize(64, 64, TextureFormat.RGBA32, true), Is.True);
                }

                var pixels = new Color[64 * 64];
                for (int i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = Color.blue;
                }

                output.SetPixels(pixels);
                output.Apply(true, false);
                if (change == "pixels")
                {
                    EditorUtility.SetDirty(output);
                }
                else
                {
                    EditorUtility.ClearDirty(output);
                }
            }
            Assert.That(output.width, Is.EqualTo(64));
            Assert.That(output.height, Is.EqualTo(64));
            Assert.That(EditorUtility.IsDirty(output), Is.EqualTo(change == "pixels"));
            Assert.That(AssetDatabase.GetAssetDependencyHash(_outputPath).ToString(), Is.EqualTo(outputKey),
                "An unsaved live edit must not rely on a changed disk hash for invalidation.");
            if (clearCache)
            {
                GrassMicroVerseBridgeUtility.ClearCaches();
            }

            uint revision = _bridge.PlacementArea.SourceRevision;
            int dirtyCount = EditorUtility.GetDirtyCount(output);
            uint editedUpdateCount = output.updateCount;
            TextureFormat editedFormat = output.format;
            TextureWrapMode editedWrapV = output.wrapModeV;
            Color[] editedPixels = output.GetPixels();
            Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(_bridge, out _), Is.False,
                "A changed live output cannot certify the saved ground-albedo contract.");
            Assert.That(_bridge.BakedGroundColor, Is.SameAs(output));
            Assert.That(_bridge.PlacementArea.GroundColorTexture, Is.SameAs(output));
            Assert.That(_bridge.PlacementArea.DensityTexture, Is.SameAs(_density));
            Assert.That(_bridge.PlacementArea.SourceRevision, Is.EqualTo(revision));
            Assert.That(_bridge.GroundBakeSourceKey, Is.EqualTo(sourceKey));
            Assert.That(_bridge.GroundBakeOutputKey, Is.EqualTo(outputKey));
            Assert.That(EditorUtility.GetDirtyCount(output), Is.EqualTo(dirtyCount));
            Assert.That(output.updateCount, Is.EqualTo(editedUpdateCount));
            Assert.That(output.format, Is.EqualTo(editedFormat));
            Assert.That(output.wrapModeV, Is.EqualTo(editedWrapV));
            Assert.That(output.GetPixels(), Is.EqualTo(editedPixels), "Preflight must not bake, save or repair the output.");
            Assert.That(_bakeCount, Is.EqualTo(1));

            Assert.That(Refresh(), Is.SameAs(output));
            Assert.That(_bakeCount, Is.EqualTo(2));
            Assert.That(AssetDatabase.AssetPathToGUID(_outputPath), Is.EqualTo(guid));
            Assert.That(output.format, Is.EqualTo(TextureFormat.RGBAHalf));
            Assert.That(output.mipmapCount, Is.EqualTo(7));
            Assert.That(output.filterMode, Is.EqualTo(FilterMode.Trilinear));
            Assert.That(output.wrapModeU, Is.EqualTo(TextureWrapMode.Clamp));
            Assert.That(output.wrapModeV, Is.EqualTo(TextureWrapMode.Clamp));
            Assert.That(EditorUtility.IsDirty(output), Is.False);
            AssertColor(output.GetPixel(32, 32), Color.red);
            Assert.That(_bridge.GroundBakeSourceKey, Is.EqualTo(sourceKey));
            Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(_bridge, out string message), Is.True, message);
            Assert.That(AssetDatabase.GetAssetDependencyHash(_diffusePath), Is.EqualTo(sourceHash));
            Assert.That(_diffuse.GetPixels(), Is.EqualTo(sourcePixels));
            Assert.That(EditorUtility.IsDirty(_diffuse), Is.False);

            revision = _bridge.PlacementArea.SourceRevision;
            for (int i = 0; i < 3; i++)
            {
                Refresh();
            }

            Assert.That(_bakeCount, Is.EqualTo(2), "A repaired output must settle after one bake.");
            Assert.That(_bridge.PlacementArea.SourceRevision, Is.EqualTo(revision));
        }

        [Test]
        public void OutputUploadCounterInvalidatesTheKnownBakeWithoutADirtyFlag()
        {
            Texture2D output = Refresh();
            string outputKey = _bridge.GroundBakeOutputKey;
            uint updateCount = output.updateCount;
            int dirtyCount = EditorUtility.GetDirtyCount(output);
            output.SetPixel(32, 32, Color.blue);
            output.Apply(true, false);
            EditorUtility.ClearDirty(output);
            Assert.That(output.updateCount, Is.Not.EqualTo(updateCount));
            Assert.That(EditorUtility.GetDirtyCount(output), Is.EqualTo(dirtyCount));
            Assert.That(EditorUtility.IsDirty(output), Is.False);
            Assert.That(AssetDatabase.GetAssetDependencyHash(_outputPath).ToString(), Is.EqualTo(outputKey));
            AssertColor(output.GetPixel(32, 32), Color.blue);

            Assert.That(Refresh(), Is.SameAs(output));
            Assert.That(_bakeCount, Is.EqualTo(2), "Reported uploads must not be hidden by an unchanged saved hash.");
            AssertColor(output.GetPixel(32, 32), Color.red);
            Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(_bridge, out string message), Is.True, message);
            Refresh();
            Assert.That(_bakeCount, Is.EqualTo(2));
        }

        [Test]
        public void LegacyBakeKeyWithoutCaptureShaderFailsPreflightAndRebuildsAfterCacheReset()
        {
            Texture2D output = Refresh();
            string currentSourceKey = _bridge.GroundBakeSourceKey;
            string legacySourceKey = LegacySourceKeyWithoutBakeShader();
            string outputKey = _bridge.GroundBakeOutputKey;
            string guid = AssetDatabase.AssetPathToGUID(_outputPath);
            Hash128 sourceHash = AssetDatabase.GetAssetDependencyHash(_diffusePath);
            Color[] sourcePixels = _diffuse.GetPixels();
            _bridge.SetBakedGroundColor(output, _outputPath, legacySourceKey, outputKey);
            GrassMicroVerseBridgeUtility.ClearCaches();
            uint revision = _bridge.PlacementArea.SourceRevision;

            Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(_bridge, out string message), Is.False);
            StringAssert.Contains("stale", message);
            Assert.That(_bridge.GroundBakeSourceKey, Is.EqualTo(legacySourceKey));
            Assert.That(_bridge.GroundBakeOutputKey, Is.EqualTo(outputKey));
            Assert.That(_bridge.PlacementArea.DensityTexture, Is.SameAs(_density));
            Assert.That(_bridge.PlacementArea.GroundColorTexture, Is.SameAs(output));
            Assert.That(_bridge.PlacementArea.SourceRevision, Is.EqualTo(revision), "Build preflight must not repair the old record.");
            Assert.That(_bakeCount, Is.EqualTo(1));

            Assert.That(Refresh(), Is.SameAs(output));
            Assert.That(_bakeCount, Is.EqualTo(2), "A new session must not reuse an output whose key omitted the capture shader.");
            Assert.That(_bridge.GroundBakeSourceKey, Is.EqualTo(currentSourceKey).And.Not.EqualTo(legacySourceKey));
            Assert.That(AssetDatabase.AssetPathToGUID(_outputPath), Is.EqualTo(guid));
            Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(_bridge, out message), Is.True, message);
            Assert.That(AssetDatabase.GetAssetDependencyHash(_diffusePath), Is.EqualTo(sourceHash));
            Assert.That(_diffuse.GetPixels(), Is.EqualTo(sourcePixels));
            Assert.That(EditorUtility.IsDirty(_diffuse), Is.False);
            AssertColor(output.GetPixel(32, 32), Color.red);
            Refresh();
            Assert.That(_bakeCount, Is.EqualTo(2), "The corrected saved key must settle after one replacement bake.");
        }

        [Test]
        public void DirtyPreviewCannotCertifyOldDiskDataAfterCacheResetAndSourceRestore()
        {
            Texture2D output = Refresh();
            string savedSourceKey = _bridge.GroundBakeSourceKey;
            Hash128 savedDiffuseHash = AssetDatabase.GetAssetDependencyHash(_diffusePath);
            SetDiffuse(Color.blue);
            Assert.That(EditorUtility.IsDirty(_diffuse), Is.True);
            GrassMicroVerseBridgeUtility.ClearCaches();

            Assert.That(Refresh(), Is.SameAs(output));
            Assert.That(_bakeCount, Is.EqualTo(2), "An unknown session cache must still bake dirty GPU source content.");
            AssertColor(output.GetPixel(32, 32), Color.blue);
            Assert.That(_bridge.GroundBakeSourceKey, Is.Empty,
                "An image from unsaved source pixels cannot be certified by that asset's previous disk hash.");
            Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(_bridge, out _), Is.False);
            for (int i = 0; i < 3; i++)
            {
                Refresh();
            }

            Assert.That(_bakeCount, Is.EqualTo(2), "An unchanged dirty preview must still use the session cache.");

            // Force a reload of the saved red texture, leaving the blue baked output
            // in place. Clearing bridge caches models the lost state after a reload.
            AssetDatabase.ImportAsset(_diffusePath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            _diffuse = AssetDatabase.LoadAssetAtPath<Texture2D>(_diffusePath);
            Assert.That(_diffuse, Is.Not.Null);
            Assert.That(EditorUtility.IsDirty(_diffuse), Is.False);
            AssertColor(_diffuse.GetPixel(0, 0), Color.red);
            Assert.That(AssetDatabase.GetAssetDependencyHash(_diffusePath), Is.EqualTo(savedDiffuseHash));
            GrassMicroVerseBridgeUtility.ClearCaches();

            Assert.That(Refresh(), Is.SameAs(output));
            Assert.That(_bakeCount, Is.EqualTo(3), "The old image must be rebuilt even though the saved source key is unchanged.");
            AssertColor(output.GetPixel(32, 32), Color.red);
            Assert.That(_bridge.GroundBakeSourceKey, Is.EqualTo(savedSourceKey));
            Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(_bridge, out string message), Is.True, message);
        }

        [Test]
        public void SavingDirtySourcesCreatesOneDurableBakeThenPollingStops()
        {
            Refresh();
            SetDiffuse(Color.blue);
            Refresh();
            Assert.That(_bridge.GroundBakeSourceKey, Is.Empty);
            Assert.That(_bakeCount, Is.EqualTo(2));

            AssetDatabase.SaveAssetIfDirty(_diffuse);
            Refresh();
            Assert.That(_bakeCount, Is.EqualTo(3));
            Assert.That(_bridge.GroundBakeSourceKey, Is.Not.Null.And.Not.Empty);
            Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(_bridge, out string message), Is.True, message);
            for (int i = 0; i < 4; i++)
            {
                Refresh();
            }

            Assert.That(_bakeCount, Is.EqualTo(3));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SourceEditsDuringBakeNotificationRequireAnotherBake(bool saveSource)
        {
            bool changed = false;
            Action<Terrain, Texture2D> changeSource = (source, output) =>
            {
                if (source != _terrain || changed)
                {
                    return;
                }

                changed = true;
                SetDiffuse(Color.blue);
                if (saveSource)
                {
                    AssetDatabase.SaveAssetIfDirty(_diffuse);
                }
            };
            TerrainGrassAlbedoBaker.Baked += changeSource;
            try
            {
                Texture2D first = Refresh();
                Assert.That(changed, Is.True);
                Assert.That(_bakeCount, Is.EqualTo(1));
                AssertColor(first.GetPixel(32, 32), Color.red);
                Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(_bridge, out _), Is.False,
                    "The notification changed the source after the saved red image was rendered.");

                Assert.That(Refresh(), Is.SameAs(first));
                Assert.That(_bakeCount, Is.EqualTo(2),
                    "A post-bake source edit must not be mistaken for the source of the earlier image.");
                AssertColor(first.GetPixel(32, 32), Color.blue);
                Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(_bridge, out string message),
                    Is.EqualTo(saveSource), message);
                for (int i = 0; i < 3; i++)
                {
                    Refresh();
                }

                Assert.That(_bakeCount, Is.EqualTo(2), "Once the changed source is baked, polling must settle.");
            }
            finally
            {
                TerrainGrassAlbedoBaker.Baked -= changeSource;
            }
        }

        [TestCase("savedPixels", false)]
        [TestCase("savedPixels", true)]
        [TestCase("dirtyPixels", false)]
        [TestCase("dirtyPixels", true)]
        [TestCase("wrapV", false)]
        [TestCase("wrapV", true)]
        public void OutputEditsDuringNotificationRemainUntouchedButCannotCertifyTheBake(string change, bool fromBoundArea)
        {
            Texture2D original = Refresh();
            string guid = AssetDatabase.AssetPathToGUID(_outputPath);
            string sourceKey = _bridge.GroundBakeSourceKey;
            string outputKey = _bridge.GroundBakeOutputKey;
            Hash128 sourceHash = AssetDatabase.GetAssetDependencyHash(_diffusePath);
            Color[] sourcePixels = _diffuse.GetPixels();
            bool changed = false;
            Action<Terrain, Texture2D> changeBaked = (source, output) =>
            {
                if (source == _terrain)
                {
                    ChangeOutput(output);
                }
            };
            Action<GrassPlacementArea, Bounds, GrassPlacementChange> changeBoundArea = (area, bounds, changes) =>
            {
                if (area == _bridge.PlacementArea && (changes & GrassPlacementChange.GroundColor) != 0 &&
                    TerrainGrassAlbedoBaker.IsBakingOutput(_outputPath))
                {
                    ChangeOutput(original);
                }
            };
            if (fromBoundArea)
            {
                GrassPlacementArea.SourceChanged += changeBoundArea;
            }
            else
            {
                TerrainGrassAlbedoBaker.Baked += changeBaked;
            }

            try
            {
                Assert.That(GrassMicroVerseBridgeUtility.Refresh(_bridge, false, false, false, true), Is.False);
            }
            finally
            {
                GrassPlacementArea.SourceChanged -= changeBoundArea;
                TerrainGrassAlbedoBaker.Baked -= changeBaked;
            }

            Assert.That(changed, Is.True, "The selected notification must run inside the actual bake.");
            Assert.That(_bakeCount, Is.EqualTo(2), "Both notification paths must still complete before certification fails.");
            Assert.That(_bridge.LastRefreshSucceeded, Is.False);
            StringAssert.Contains("changed during a completion callback", _bridge.LastRefreshMessage);
            Assert.That(_bridge.BakedGroundColor, Is.SameAs(original));
            Assert.That(AssetDatabase.LoadMainAssetAtPath(_outputPath), Is.SameAs(original));
            Assert.That(AssetDatabase.AssetPathToGUID(_outputPath), Is.EqualTo(guid));
            Assert.That(_bridge.GroundBakeAssetPath, Is.EqualTo(_outputPath));
            Assert.That(_bridge.GroundBakeSourceKey, Is.EqualTo(sourceKey));
            Assert.That(_bridge.GroundBakeOutputKey, Is.EqualTo(outputKey), "Observer edits must not receive the completed bake's durable record.");
            Assert.That(_bridge.PlacementArea.DensityTexture, Is.Null);
            Assert.That(_bridge.PlacementArea.GroundColorTexture, Is.Null);
            Assert.That(TerrainGrassAlbedoBaker.IsBakingOutput(_outputPath), Is.False);
            Assert.That(EditorUtility.IsDirty(original), Is.EqualTo(change == "dirtyPixels"));
            Assert.That(original.wrapModeV, Is.EqualTo(change == "wrapV" ? TextureWrapMode.Repeat : TextureWrapMode.Clamp));
            AssertColor(original.GetPixel(32, 32), change == "wrapV" ? Color.red : Color.blue);
            Hash128 editedHash = AssetDatabase.GetAssetDependencyHash(_outputPath);
            if (change == "savedPixels")
            {
                Assert.That(editedHash.ToString(), Is.Not.EqualTo(outputKey), "The saved blue image must remain on disk without being certified.");
            }

            uint revision = _bridge.PlacementArea.SourceRevision;
            uint updateCount = original.updateCount;
            int dirtyCount = EditorUtility.GetDirtyCount(original);
            Color[] editedPixels = original.GetPixels();
            Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(_bridge, out _), Is.False);
            Assert.That(_bridge.PlacementArea.SourceRevision, Is.EqualTo(revision));
            Assert.That(_bridge.PlacementArea.DensityTexture, Is.Null);
            Assert.That(_bridge.PlacementArea.GroundColorTexture, Is.Null);
            Assert.That(_bridge.GroundBakeSourceKey, Is.EqualTo(sourceKey));
            Assert.That(_bridge.GroundBakeOutputKey, Is.EqualTo(outputKey));
            Assert.That(original.updateCount, Is.EqualTo(updateCount));
            Assert.That(EditorUtility.GetDirtyCount(original), Is.EqualTo(dirtyCount));
            Assert.That(EditorUtility.IsDirty(original), Is.EqualTo(change == "dirtyPixels"));
            Assert.That(original.wrapModeV, Is.EqualTo(change == "wrapV" ? TextureWrapMode.Repeat : TextureWrapMode.Clamp));
            Assert.That(original.GetPixels(), Is.EqualTo(editedPixels), "Preflight must neither undo nor save the observer's edits.");
            Assert.That(AssetDatabase.GetAssetDependencyHash(_outputPath), Is.EqualTo(editedHash));
            Assert.That(_bakeCount, Is.EqualTo(2));

            Assert.That(Refresh(), Is.SameAs(original));
            Assert.That(_bakeCount, Is.EqualTo(3), "A callback-free explicit refresh must regenerate the native image exactly once.");
            Assert.That(AssetDatabase.AssetPathToGUID(_outputPath), Is.EqualTo(guid));
            Assert.That(original.wrapModeV, Is.EqualTo(TextureWrapMode.Clamp));
            Assert.That(EditorUtility.IsDirty(original), Is.False);
            AssertColor(original.GetPixel(32, 32), Color.red);
            Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(_bridge, out string message), Is.True, message);
            Assert.That(AssetDatabase.GetAssetDependencyHash(_diffusePath), Is.EqualTo(sourceHash));
            Assert.That(_diffuse.GetPixels(), Is.EqualTo(sourcePixels));
            Assert.That(EditorUtility.IsDirty(_diffuse), Is.False);
            revision = _bridge.PlacementArea.SourceRevision;
            Assert.That(Refresh(), Is.SameAs(original));
            Assert.That(_bakeCount, Is.EqualTo(3));
            Assert.That(_bridge.PlacementArea.SourceRevision, Is.EqualTo(revision));

            void ChangeOutput(Texture2D output)
            {
                if (changed)
                {
                    return;
                }

                changed = true;
                Assert.That(output, Is.SameAs(original));
                uint beforeUpdate = output.updateCount;
                int beforeDirty = EditorUtility.GetDirtyCount(output);
                if (change == "wrapV")
                {
                    output.wrapModeV = TextureWrapMode.Repeat;
                    EditorUtility.ClearDirty(output);
                    Assert.That(output.updateCount, Is.EqualTo(beforeUpdate), "The sampler-only edit must not depend on a pixel upload.");
                    Assert.That(EditorUtility.GetDirtyCount(output), Is.EqualTo(beforeDirty));
                }
                else
                {
                    var pixels = new Color[output.width * output.height];
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        pixels[i] = Color.blue;
                    }

                    output.SetPixels(pixels);
                    output.Apply(true, false);
                    EditorUtility.SetDirty(output);
                    if (change == "savedPixels")
                    {
                        AssetDatabase.SaveAssetIfDirty(output);
                        Assert.That(EditorUtility.GetDirtyCount(output), Is.EqualTo(beforeDirty),
                            "A saved edit must be detected even after its dirty counter is cleared.");
                    }
                    Assert.That(output.updateCount, Is.Not.EqualTo(beforeUpdate));
                }
            }
        }

        [TestCase("missing")]
        [TestCase("replacement")]
        [TestCase("ambiguous")]
        public void MaskRegenerationDuringBakeNotificationCommitsOnlyTheCurrentMapping(string mutation)
        {
            Texture2D originalOutput = Refresh();
            Texture2D originalDensity = _density;
            string selectedName = _bridge.TextureSubAssetName;
            string outputGuid = AssetDatabase.AssetPathToGUID(_outputPath);
            Hash128 sourceHash = AssetDatabase.GetAssetDependencyHash(_diffusePath);
            Texture2D replacement = null, duplicate = null;
            bool changed = false, succeeded = false;
            Action<Terrain, Texture2D> regenerate = (source, output) =>
            {
                if (source != _terrain || changed)
                {
                    return;
                }

                changed = true;
                Object.DestroyImmediate(originalDensity, true);
                if (mutation != "missing")
                {
                    replacement = CreateSavedDensity(selectedName, 16);
                }

                if (mutation == "ambiguous")
                {
                    duplicate = CreateSavedDensity(selectedName, 32);
                }

                EditorUtility.SetDirty(_target);
                AssetDatabase.SaveAssetIfDirty(_target);
            };
            TerrainGrassAlbedoBaker.Baked += regenerate;
            try
            {
                Assert.DoesNotThrow(() => succeeded = GrassMicroVerseBridgeUtility.Refresh(_bridge, false, false, false, true),
                    "A producer replacing its saved mask must not leave a destroyed local reference in the success message.");
            }
            finally
            {
                TerrainGrassAlbedoBaker.Baked -= regenerate;
            }

            Assert.That(changed, Is.True, "The regression must cross the synchronous completion callback.");
            Assert.That(originalDensity == null, Is.True);
            Assert.That(succeeded, Is.EqualTo(mutation == "replacement"));
            Assert.That(_bridge.LastRefreshSucceeded, Is.EqualTo(succeeded));
            Assert.That(_bridge.TextureSubAssetName, Is.EqualTo(selectedName), "Regeneration must preserve the explicit mapping.");
            Assert.That(_bakeCount, Is.EqualTo(2));
            Assert.That(_bridge.BakedGroundColor, Is.SameAs(originalOutput));
            Assert.That(AssetDatabase.AssetPathToGUID(_outputPath), Is.EqualTo(outputGuid));
            Assert.That(AssetDatabase.GetAssetDependencyHash(_diffusePath), Is.EqualTo(sourceHash));
            AssertColor(originalOutput.GetPixel(32, 32), Color.red);
            if (succeeded)
            {
                Assert.That(_bridge.PlacementArea.DensityTexture, Is.SameAs(replacement));
                Assert.That(_bridge.PlacementArea.DensityTexture.width, Is.EqualTo(16));
                Assert.That(_bridge.PlacementArea.GroundColorTexture, Is.SameAs(originalOutput));
                Assert.That(_bridge.PlacementArea.TryGetCaptureData(out _), Is.True);
            }
            else
            {
                StringAssert.Contains(mutation == "missing" ? "missing" : "Several textures", _bridge.LastRefreshMessage);
                Assert.That(_bridge.PlacementArea.DensityTexture, Is.Null);
                Assert.That(_bridge.PlacementArea.GroundColorTexture, Is.Null);
                Assert.That(_bridge.PlacementArea.TryGetCaptureData(out _), Is.False);
            }
            uint revision = _bridge.PlacementArea.SourceRevision;
            Texture boundDensity = _bridge.PlacementArea.DensityTexture;
            Texture boundColor = _bridge.PlacementArea.GroundColorTexture;
            Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(_bridge, out string message), Is.EqualTo(succeeded), message);
            Assert.That(_bridge.PlacementArea.SourceRevision, Is.EqualTo(revision), "Preflight must remain read-only.");
            Assert.That(_bridge.PlacementArea.DensityTexture, Is.SameAs(boundDensity));
            Assert.That(_bridge.PlacementArea.GroundColorTexture, Is.SameAs(boundColor));

            if (!replacement)
            {
                replacement = CreateSavedDensity(selectedName, 16);
            }

            if (duplicate)
            {
                Object.DestroyImmediate(duplicate, true);
                EditorUtility.SetDirty(_target);
                AssetDatabase.SaveAssetIfDirty(_target);
            }
            _density = replacement;
            Assert.That(Refresh(), Is.SameAs(originalOutput));
            Assert.That(_bridge.PlacementArea.DensityTexture, Is.SameAs(replacement));
            Assert.That(_bridge.PlacementArea.Terrain, Is.SameAs(_terrain));
            Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(_bridge, out message), Is.True, message);
            for (int i = 0; i < 3; i++)
            {
                Refresh();
            }

            Assert.That(_bakeCount, Is.EqualTo(2), "Resolving the new mask must reuse the already completed, unchanged ground bake.");
        }

        [TestCase("terrain")]
        [TestCase("resolution")]
        [TestCase("output")]
        public void BakeConfigurationChangedByObserverIsPreservedUntilTheNextRefresh(string mutation)
        {
            Texture2D originalOutput = Refresh();
            Terrain originalTerrain = _terrain;
            string originalSourceKey = _bridge.GroundBakeSourceKey;
            string originalOutputKey = _bridge.GroundBakeOutputKey;
            string originalGuid = AssetDatabase.AssetPathToGUID(_outputPath);
            string replacementPath = _folder + "/NewGround.asset";
            Terrain replacementTerrain = null;
            if (mutation == "terrain")
            {
                GameObject other = Own(Terrain.CreateTerrainGameObject(_data));
                replacementTerrain = other.GetComponent<Terrain>();
                replacementTerrain.materialTemplate = _terrain.materialTemplate;
            }
            bool changed = false, succeeded = true;
            Action<Terrain, Texture2D> reconfigure = (source, output) =>
            {
                if (source != originalTerrain || changed)
                {
                    return;
                }

                changed = true;
                if (mutation == "output")
                {
                    _bridge.SetBakedGroundColor(null, replacementPath, string.Empty, string.Empty);
                }
                else
                {
                    var serialized = new SerializedObject(_bridge);
                    if (mutation == "terrain")
                    {
                        serialized.FindProperty("_terrain").objectReferenceValue = replacementTerrain;
                    }
                    else
                    {
                        serialized.FindProperty("_groundBakeResolution").intValue = 128;
                    }

                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
            };
            TerrainGrassAlbedoBaker.Baked += reconfigure;
            try
            {
                Assert.DoesNotThrow(() => succeeded = GrassMicroVerseBridgeUtility.Refresh(_bridge, false, false, false, true));
            }
            finally
            {
                TerrainGrassAlbedoBaker.Baked -= reconfigure;
            }

            Assert.That(changed, Is.True);
            Assert.That(succeeded, Is.False);
            StringAssert.Contains("configuration changed during the bake", _bridge.LastRefreshMessage);
            Assert.That(_bridge.Terrain, Is.SameAs(replacementTerrain ? replacementTerrain : originalTerrain));
            Assert.That(_bridge.GroundBakeResolution, Is.EqualTo(mutation == "resolution" ? 128 : 64));
            Assert.That(_bridge.GroundBakeAssetPath, Is.EqualTo(mutation == "output" ? replacementPath : _outputPath));
            Assert.That(_bridge.BakedGroundColor, Is.SameAs(mutation == "output" ? null : originalOutput));
            Assert.That(_bridge.GroundBakeSourceKey, Is.EqualTo(mutation == "output" ? string.Empty : originalSourceKey));
            Assert.That(_bridge.GroundBakeOutputKey, Is.EqualTo(mutation == "output" ? string.Empty : originalOutputKey));
            Assert.That(_bridge.PlacementArea.DensityTexture, Is.Null);
            Assert.That(_bridge.PlacementArea.GroundColorTexture, Is.Null);
            Assert.That(AssetDatabase.LoadMainAssetAtPath(_outputPath), Is.SameAs(originalOutput));
            Assert.That(AssetDatabase.AssetPathToGUID(_outputPath), Is.EqualTo(originalGuid));
            AssertColor(originalOutput.GetPixel(32, 32), Color.red);
            Assert.That(_bakeCount, Is.EqualTo(2));
            uint revision = _bridge.PlacementArea.SourceRevision;
            Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(_bridge, out _), Is.False);
            Assert.That(_bridge.PlacementArea.SourceRevision, Is.EqualTo(revision), "Preflight must not repair the changed configuration.");

            _terrain = _bridge.Terrain;
            Texture2D recovered = Refresh();
            Assert.That(recovered.width, Is.EqualTo(_bridge.GroundBakeResolution));
            Assert.That(_bridge.PlacementArea.Terrain, Is.SameAs(_terrain));
            Assert.That(_bridge.PlacementArea.DensityTexture, Is.SameAs(_density));
            Assert.That(_bridge.GroundBakeAssetPath, Is.EqualTo(mutation == "output" ? replacementPath : _outputPath));
            Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(_bridge, out string message), Is.True, message);
            Assert.That(_bakeCount, Is.EqualTo(3));
            for (int i = 0; i < 3; i++)
            {
                Refresh();
            }

            Assert.That(_bakeCount, Is.EqualTo(3), "A recovered configuration must settle after one new bake.");
        }

        [Test]
        public void ReentrantRefreshDoesNotClearOrRebindTheCommittedPlacement()
        {
            Texture2D original = Refresh();
            bool attempted = false, nestedSucceeded = true, keptBinding = false, keptRevision = false;
            string nestedMessage = null;
            Action<Terrain, Texture2D> observer = (source, output) =>
            {
                // An integration can observe a bake and request a bridge refresh.
                // Keep this regression bounded on versions without the guard.
                if (source != _terrain || attempted)
                {
                    return;
                }

                attempted = true;
                uint revision = _bridge.PlacementArea.SourceRevision;
                nestedSucceeded = GrassMicroVerseBridgeUtility.Refresh(_bridge, false, false, true, true);
                nestedMessage = _bridge.LastRefreshMessage;
                keptBinding = _bridge.PlacementArea.DensityTexture == _density &&
                    _bridge.PlacementArea.GroundColorTexture == original;
                keptRevision = _bridge.PlacementArea.SourceRevision == revision;
            };
            TerrainGrassAlbedoBaker.Baked += observer;
            try
            {
                Assert.That(GrassMicroVerseBridgeUtility.Refresh(_bridge, false, false, false, true),
                    Is.True, _bridge.LastRefreshMessage);
            }
            finally
            {
                TerrainGrassAlbedoBaker.Baked -= observer;
            }

            Assert.That(attempted, Is.True);
            Assert.That(nestedSucceeded, Is.False);
            StringAssert.Contains("already refreshing", nestedMessage);
            Assert.That(keptBinding, Is.True);
            Assert.That(keptRevision, Is.True, "A rejected nested refresh must not invalidate or clear the original placement.");
            Assert.That(_bridge.LastRefreshSucceeded, Is.True);
            Assert.That(_bakeCount, Is.EqualTo(2));
            Assert.That(Refresh(), Is.SameAs(original));
            Assert.That(_bakeCount, Is.EqualTo(2), "Ordinary refresh must resume after the callback returns.");
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void RefreshIterationSurvivesBakeObserversDeletingCurrentAndLaterOwners(bool saveScene, bool childBridges)
        {
            // Keep the shared Terrain alive when a callback removes a bridge owner.
            Object.DestroyImmediate(_bridge);
            var candidates = new GrassMicroVerseBridge[3];
            GameObject group = childBridges ? Own(new GameObject("Bridge group")) : null;
            for (int i = 0; i < candidates.Length; i++)
            {
                GameObject owner = Own(new GameObject("Bake consumer " + i));
                if (group)
                {
                    owner.transform.SetParent(group.transform);
                }

                candidates[i] = AddBridge(owner, 64, true);
                var serialized = new SerializedObject(candidates[i]);
                serialized.FindProperty("_groundBakeAssetPath").stringValue = _folder + "/Consumer" + i + ".asset";
                serialized.FindProperty("_autoRefresh").boolValue = !saveScene;
                serialized.FindProperty("_pollWhileEditing").boolValue = false;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            if (saveScene)
            {
                _savedScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                SceneManager.MoveGameObjectToScene(_terrain.gameObject, _savedScene);
                if (group)
                {
                    SceneManager.MoveGameObjectToScene(group, _savedScene);
                }
                else
                {
                    foreach (GrassMicroVerseBridge candidate in candidates)
                    {
                        SceneManager.MoveGameObjectToScene(candidate.gameObject, _savedScene);
                    }
                }
            }

            bool removed = false;
            GrassMicroVerseBridge survivor = null;
            Action<Terrain, Texture2D> removeOwners = (source, output) =>
            {
                if (source != _terrain || removed)
                {
                    return;
                }

                removed = true;
                GrassMicroVerseBridge current = null, later = null;
                string path = AssetDatabase.GetAssetPath(output);
                foreach (GrassMicroVerseBridge candidate in candidates)
                {
                    if (candidate.GroundBakeAssetPath == path)
                    {
                        current = candidate;
                    }
                    else if (!later)
                    {
                        later = candidate;
                    }
                    else
                    {
                        survivor = candidate;
                    }
                }
                Assert.That(current, Is.Not.Null);
                Assert.That(later, Is.Not.Null);
                Assert.That(survivor, Is.Not.Null);
                Object.DestroyImmediate(current.gameObject);
                Object.DestroyImmediate(later.gameObject);
            };
            TerrainGrassAlbedoBaker.Baked += removeOwners;
            try
            {
                if (saveScene)
                {
                    Assert.That(EditorSceneManager.SaveScene(_savedScene, _folder + "/Consumers.unity"), Is.True);
                }
                else
                {
                    // Invoke the real update handler once, without relying on a
                    // wall-clock debounce or an unrelated editor repaint sequence.
                    Type watcher = typeof(GrassMicroVerseBridgeWatcher);
                    FieldInfo debounce = watcher.GetField("refreshAfter", BindingFlags.Static | BindingFlags.NonPublic);
                    double previousDebounce = (double)debounce.GetValue(null);
                    try
                    {
                        debounce.SetValue(null, -1d);
                        MethodInfo update = watcher.GetMethod("Update", BindingFlags.Static | BindingFlags.NonPublic);
                        Assert.DoesNotThrow(() => update.Invoke(null, null));
                    }
                    finally
                    {
                        debounce.SetValue(null, previousDebounce);
                    }
                }
            }
            finally
            {
                TerrainGrassAlbedoBaker.Baked -= removeOwners;
            }

            Assert.That(removed, Is.True, "The regression must cross a real synchronous Baked callback.");
            Assert.That(_bakeCount, Is.EqualTo(2), "The deleted queued owner must be skipped, and the surviving owner must finish.");
            Assert.That(survivor, Is.Not.Null);
            Assert.That(survivor.LastRefreshSucceeded, Is.True, survivor.LastRefreshMessage);
            Assert.That(survivor.PlacementArea.DensityTexture, Is.SameAs(_density));
            Assert.That(survivor.PlacementArea.GroundColorTexture, Is.SameAs(survivor.BakedGroundColor));
            Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(survivor, out string message), Is.True, message);
        }

        [Test]
        public void RefreshingAnotherBridgeDuringASharedBakePreservesItsCommittedPlacement()
        {
            Texture2D original = Refresh();
            GameObject otherObject = Own(new GameObject("Shared ground-bake consumer"));
            GrassMicroVerseBridge other = AddBridge(otherObject, 64, true);
            Assert.That(GrassMicroVerseBridgeUtility.Refresh(other, false, false, false), Is.True, other.LastRefreshMessage);
            Assert.That(other.AutoRefresh, Is.False);
            int previousBakes = _bakeCount;
            bool attempted = false, nestedSucceeded = true, keptBinding = false, keptRevision = false;
            string nestedMessage = null;
            Action<Terrain, Texture2D> observer = (source, output) =>
            {
                if (source != _terrain || attempted)
                {
                    return;
                }

                attempted = true;
                uint revision = other.PlacementArea.SourceRevision;
                nestedSucceeded = GrassMicroVerseBridgeUtility.Refresh(other, false, false, true, true);
                nestedMessage = other.LastRefreshMessage;
                keptBinding = other.PlacementArea.DensityTexture == _density &&
                    other.PlacementArea.GroundColorTexture == original;
                keptRevision = other.PlacementArea.SourceRevision == revision;
            };
            TerrainGrassAlbedoBaker.Baked += observer;
            try
            {
                Assert.That(GrassMicroVerseBridgeUtility.Refresh(_bridge, false, false, false, true),
                    Is.True, _bridge.LastRefreshMessage);
            }
            finally
            {
                TerrainGrassAlbedoBaker.Baked -= observer;
            }

            Assert.That(attempted, Is.True);
            Assert.That(nestedSucceeded, Is.False);
            StringAssert.Contains("still being baked", nestedMessage);
            Assert.That(keptBinding, Is.True);
            Assert.That(keptRevision, Is.True);
            Assert.That(other.PlacementArea.DensityTexture, Is.SameAs(_density),
                "A shared-output observer with Auto Refresh disabled must not be left with cleared coverage.");
            Assert.That(other.PlacementArea.GroundColorTexture, Is.SameAs(original));
            Assert.That(_bakeCount, Is.EqualTo(previousBakes + 1));
            Assert.That(GrassMicroVerseBridgeUtility.Refresh(other, false, false, false), Is.True, other.LastRefreshMessage);
            Assert.That(other.LastRefreshSucceeded, Is.True);
        }

        [TestCase("delete")]
        [TestCase("replace")]
        public void LostOutputDuringNotificationKeepsItsConfiguredPathAndCanRecover(string mutation)
        {
            Texture2D original = Refresh();
            string sourceKey = _bridge.GroundBakeSourceKey;
            string outputKey = _bridge.GroundBakeOutputKey;
            Hash128 diffuseHash = AssetDatabase.GetAssetDependencyHash(_diffusePath);
            Texture2D replacement = null;
            bool changed = false;
            Action<Terrain, Texture2D> changeOutput = (source, output) =>
            {
                if (source != _terrain || changed)
                {
                    return;
                }

                changed = true;
                Assert.That(output, Is.SameAs(original));
                Assert.That(AssetDatabase.DeleteAsset(_outputPath), Is.True);
                if (mutation == "replace")
                {
                    replacement = Own(new Texture2D(2, 2, TextureFormat.RGBA32, false, true));
                    replacement.SetPixels(new[] { Color.blue, Color.blue, Color.blue, Color.blue });
                    replacement.Apply(false, false);
                    AssetDatabase.CreateAsset(replacement, _outputPath);
                    AssetDatabase.SaveAssetIfDirty(replacement);
                }
            };
            TerrainGrassAlbedoBaker.Baked += changeOutput;
            try
            {
                Assert.That(GrassMicroVerseBridgeUtility.Refresh(_bridge, false, false, false, true), Is.False);
            }
            finally
            {
                TerrainGrassAlbedoBaker.Baked -= changeOutput;
            }

            Assert.That(changed, Is.True);
            StringAssert.Contains("removed or replaced", _bridge.LastRefreshMessage);
            Assert.That(_bridge.GroundBakeAssetPath, Is.EqualTo(_outputPath), "A failed bake must retain its configured recovery path.");
            Assert.That(_bridge.GroundBakeSourceKey, Is.EqualTo(sourceKey));
            Assert.That(_bridge.GroundBakeOutputKey, Is.EqualTo(outputKey), "The lost output must not receive a new durable record.");
            Assert.That(_bridge.PlacementArea.DensityTexture, Is.Null);
            Assert.That(_bridge.PlacementArea.GroundColorTexture, Is.Null);
            Assert.That(TerrainGrassAlbedoBaker.IsBakingOutput(_outputPath), Is.False, "Failure must release output ownership.");
            uint revision = _bridge.PlacementArea.SourceRevision;
            Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(_bridge, out _), Is.False);
            Assert.That(_bridge.PlacementArea.SourceRevision, Is.EqualTo(revision));
            if (replacement)
            {
                Assert.That(AssetDatabase.LoadMainAssetAtPath(_outputPath), Is.SameAs(replacement));
                Assert.That(replacement.width, Is.EqualTo(2), "Rejecting the failed bake must leave its replacement untouched.");
                AssertColor(replacement.GetPixel(0, 0), Color.blue);
            }

            Texture2D recovered = Refresh();
            Assert.That(_bridge.GroundBakeAssetPath, Is.EqualTo(_outputPath));
            Assert.That(TerrainGrassAlbedoBaker.TryGetOutputAssetPath(recovered, out string recoveredPath), Is.True);
            Assert.That(recoveredPath, Is.EqualTo(_outputPath));
            Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(_bridge, out string message), Is.True, message);
            Assert.That(AssetDatabase.GetAssetDependencyHash(_diffusePath), Is.EqualTo(diffuseHash));
            AssertColor(recovered.GetPixel(32, 32), Color.red);
            Assert.That(_bakeCount, Is.EqualTo(3));
            Refresh();
            Assert.That(_bakeCount, Is.EqualTo(3), "Recovery must settle instead of repeatedly regenerating the asset.");
        }

        [Test]
        public void LiveNonpersistentOutputRecordRecoversAtItsConfiguredPath()
        {
            Texture2D savedOutput = Refresh();
            string guid = AssetDatabase.AssetPathToGUID(_outputPath);
            string sourceKey = _bridge.GroundBakeSourceKey;
            string outputKey = _bridge.GroundBakeOutputKey;
            Texture2D transient = Own(new Texture2D(64, 64, TextureFormat.RGBAHalf, false, true));
            transient.SetPixel(0, 0, Color.blue);
            transient.Apply(false, false);
            Assert.That(transient != null, Is.True);
            Assert.That(AssetDatabase.Contains(transient), Is.False);
            Assert.That(AssetDatabase.GetAssetPath(transient), Is.Empty);
            _bridge.SetBakedGroundColor(transient, _outputPath, sourceKey, outputKey);
            uint revision = _bridge.PlacementArea.SourceRevision;

            Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(_bridge, out _), Is.False);
            Assert.That(_bridge.BakedGroundColor, Is.SameAs(transient));
            Assert.That(_bridge.GroundBakeAssetPath, Is.EqualTo(_outputPath));
            Assert.That(_bridge.PlacementArea.GroundColorTexture, Is.SameAs(savedOutput));
            Assert.That(_bridge.PlacementArea.SourceRevision, Is.EqualTo(revision), "Preflight must not repair the unsaved record.");

            Assert.That(Refresh(), Is.SameAs(savedOutput), "An unsaved reference must not hide the configured existing output.");
            Assert.That(_bridge.BakedGroundColor, Is.SameAs(savedOutput));
            Assert.That(_bridge.GroundBakeAssetPath, Is.EqualTo(_outputPath));
            Assert.That(AssetDatabase.AssetPathToGUID(_outputPath), Is.EqualTo(guid));
            Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(_bridge, out string message), Is.True, message);
            Assert.That(AssetDatabase.Contains(transient), Is.False, "Recovery must not adopt or destroy the producer's transient object.");
            AssertColor(transient.GetPixel(0, 0), Color.blue);
            AssertColor(savedOutput.GetPixel(32, 32), Color.red);
            Assert.That(_bakeCount, Is.EqualTo(2));
            Refresh();
            Assert.That(_bakeCount, Is.EqualTo(2));
        }

        [Test]
        public void SameOutputMovedDuringNotificationRetainsIdentityAndUpdatesTheSavedPath()
        {
            Texture2D original = Refresh();
            string guid = AssetDatabase.AssetPathToGUID(_outputPath);
            string movedPath = _folder + "/MovedGround.asset";
            bool moved = false;
            Action<Terrain, Texture2D> moveOutput = (source, output) =>
            {
                if (source != _terrain || moved)
                {
                    return;
                }

                moved = true;
                Assert.That(output, Is.SameAs(original));
                Assert.That(AssetDatabase.MoveAsset(_outputPath, movedPath), Is.Empty);
            };
            TerrainGrassAlbedoBaker.Baked += moveOutput;
            try
            {
                Assert.That(GrassMicroVerseBridgeUtility.Refresh(_bridge, false, false, false, true),
                    Is.True, _bridge.LastRefreshMessage);
            }
            finally
            {
                TerrainGrassAlbedoBaker.Baked -= moveOutput;
            }

            Assert.That(moved, Is.True);
            Assert.That(_bridge.BakedGroundColor, Is.SameAs(original));
            Assert.That(_bridge.GroundBakeAssetPath, Is.EqualTo(movedPath));
            Assert.That(AssetDatabase.AssetPathToGUID(movedPath), Is.EqualTo(guid));
            Assert.That(TerrainGrassAlbedoBaker.TryGetOutputAssetPath(original, out string resolvedPath), Is.True);
            Assert.That(resolvedPath, Is.EqualTo(movedPath));
            Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(_bridge, out string message), Is.True, message);
            Assert.That(Refresh(), Is.SameAs(original));
            Assert.That(_bakeCount, Is.EqualTo(2), "A legitimate move must not lose the completed bake or require another one.");
        }

        [Test]
        public void SharedTerrainOutputAtAnotherResolutionClearsConflictingCoverage()
        {
            Texture2D original = Refresh();
            GameObject otherObject = Own(new GameObject("Conflicting ground bake"));
            GrassMicroVerseBridge other = AddBridge(otherObject, 128, false);
            Assert.That(GrassMicroVerseBridgeUtility.Refresh(other, false, false, false), Is.True, other.LastRefreshMessage);
            Assert.That(other.PlacementArea.DensityTexture, Is.SameAs(_density));
            var serialized = new SerializedObject(other);
            serialized.FindProperty("_bakeTerrainGroundColor").boolValue = true;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            Assert.That(GrassMicroVerseBridgeUtility.Refresh(other, false, false, false), Is.False);
            Assert.That(other.LastRefreshMessage, Does.Contain("Terrain and resolution"));
            Assert.That(other.PlacementArea.DensityTexture, Is.Null);
            Assert.That(original.width, Is.EqualTo(64));
            Assert.That(_bridge.BakedGroundColor, Is.SameAs(original));
            Assert.That(_bakeCount, Is.EqualTo(1), "Conflicting outputs must not alternate between resolutions on every poll.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SharedOutputAliasesCannotOverwriteAnotherResolution(bool changeCase)
        {
            Texture2D original = Refresh();
            string originalGuid = AssetDatabase.AssetPathToGUID(_outputPath);
            GameObject otherObject = Own(new GameObject("Aliased ground output"));
            GrassMicroVerseBridge other = AddBridge(otherObject, 128, true);
            var serialized = new SerializedObject(other);
            serialized.FindProperty("_groundBakeAssetPath").stringValue = changeCase
                ? _outputPath.Replace("Ground.asset", "GROUND.asset")
                : _outputPath.Replace('/', '\\');
            serialized.ApplyModifiedPropertiesWithoutUndo();

            Assert.That(GrassMicroVerseBridgeUtility.Refresh(other, false, false, false), Is.False);
            Assert.That(other.LastRefreshMessage, Does.Contain("Terrain and resolution"));
            Assert.That(other.PlacementArea.DensityTexture, Is.Null);
            Assert.That(original.width, Is.EqualTo(64));
            Assert.That(AssetDatabase.AssetPathToGUID(_outputPath), Is.EqualTo(originalGuid));
            Assert.That(_bakeCount, Is.EqualTo(1), "An alias must be rejected before overwriting the existing bake.");
        }

        [Test]
        public void WindowsOutputPathIsSavedCanonicallyAndReused()
        {
            var serialized = new SerializedObject(_bridge);
            serialized.FindProperty("_groundBakeAssetPath").stringValue = _outputPath.Replace('/', '\\');
            serialized.ApplyModifiedPropertiesWithoutUndo();

            Texture2D output = Refresh();
            Assert.That(_bridge.GroundBakeAssetPath, Is.EqualTo(AssetDatabase.GetAssetPath(output)));
            Assert.That(_bridge.GroundBakeAssetPath, Is.EqualTo(_outputPath));
            Assert.That(GrassMicroVerseBridgeUtility.TryValidateForBuild(_bridge, out string message), Is.True, message);
            uint revision = _bridge.PlacementArea.SourceRevision;
            for (int i = 0; i < 3; i++)
            {
                Assert.That(Refresh(), Is.SameAs(output));
            }

            Assert.That(_bakeCount, Is.EqualTo(1));
            Assert.That(_bridge.PlacementArea.SourceRevision, Is.EqualTo(revision));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DeactivatedOwnerKeepsItsSavedOutput(bool disableComponent)
        {
            Texture2D original = Refresh();
            if (disableComponent)
            {
                _bridge.enabled = false;
            }
            else
            {
                _bridge.gameObject.SetActive(false);
            }

            Assert.That(GrassMicroVerseBridge.ActiveBridges, Has.No.Member(_bridge));
            GameObject otherObject = Own(new GameObject("Conflicting inactive bake owner"));
            GrassMicroVerseBridge other = AddBridge(otherObject, 128, true);

            Assert.That(GrassMicroVerseBridgeUtility.Refresh(other, false, false, false), Is.False);
            Assert.That(other.LastRefreshMessage, Does.Contain("Terrain and resolution"));
            Assert.That(other.PlacementArea.DensityTexture, Is.Null);
            Assert.That(original.width, Is.EqualTo(64));
            Assert.That(_bridge.PlacementArea.GroundColorTexture, Is.SameAs(original));
            Assert.That(_bakeCount, Is.EqualTo(1), "An inactive saved scene binding still owns its baked texture.");
        }

        private GrassMicroVerseBridge AddBridge(GameObject gameObject, int resolution, bool enableBaking)
        {
            GrassMicroVerseBridge added = gameObject.AddComponent<GrassMicroVerseBridge>();
            var serialized = new SerializedObject(added);
            serialized.FindProperty("_terrain").objectReferenceValue = _terrain;
            serialized.FindProperty("_maskTarget").objectReferenceValue = _target;
            serialized.FindProperty("_textureSubAssetName").stringValue = _density.name;
            serialized.FindProperty("_autoRefresh").boolValue = false;
            serialized.FindProperty("_bakeTerrainGroundColor").boolValue = enableBaking;
            serialized.FindProperty("_groundBakeResolution").intValue = resolution;
            serialized.FindProperty("_groundBakeAssetPath").stringValue = _outputPath;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return added;
        }

        private Texture2D Refresh()
        {
            Assert.That(GrassMicroVerseBridgeUtility.Refresh(_bridge, false, false, false), Is.True, _bridge.LastRefreshMessage);
            Assert.That(_bridge.PlacementArea.TryGetCaptureData(out _), Is.True);
            Assert.That(_bridge.BakedGroundColor, Is.Not.Null);
            return _bridge.BakedGroundColor;
        }

        // Reconstruct the shipped v1 record format before the capture shader was
        // included. This models an upgraded project without modifying source assets
        // or relying on a new shader import changing unrelated Unity dirty counters.
        private string LegacySourceKeyWithoutBakeShader()
        {
            var state = new StringBuilder();
            state.Append("1|").Append(_bridge.GroundBakeResolution.ToString(CultureInfo.InvariantCulture)).Append('|');
            state.Append(((int)QualitySettings.activeColorSpace).ToString(CultureInfo.InvariantCulture)).Append('|');
            Value(_data.size.x); Value(_data.size.z);
            Asset(_data); Asset(_terrain.materialTemplate); Asset(_terrain.materialTemplate.shader);
            state.Append(_terrain.materialTemplate.IsKeywordEnabled("_TERRAIN_BLEND_HEIGHT") ? '1' : '0').Append('|');
            Value(_terrain.materialTemplate.GetFloat("_HeightTransition"));
            foreach (TerrainLayer source in _data.terrainLayers)
            {
                Asset(source); Asset(source.diffuseTexture); Asset(source.maskMapTexture);
                Value(source.tileSize.x); Value(source.tileSize.y);
                Value(source.tileOffset.x); Value(source.tileOffset.y);
                Vector(source.diffuseRemapMin); Vector(source.diffuseRemapMax);
                Vector(source.maskMapRemapMin); Vector(source.maskMapRemapMax);
            }
            return Hash128.Compute(state.ToString()).ToString();

            void Value(float value) => state.Append(value.ToString("R", CultureInfo.InvariantCulture)).Append('|');
            void Vector(Vector4 value) { Value(value.x); Value(value.y); Value(value.z); Value(value.w); }
            void Asset(Object asset)
            {
                if (!asset)
                {
                    state.Append("null|");
                    return;
                }
                Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string assetGuid, out long localId), Is.True);
                state.Append(assetGuid).Append(':').Append(localId.ToString(CultureInfo.InvariantCulture)).Append(':');
                state.Append(AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(asset))).Append('|');
            }
        }

        private void SetDiffuse(Color color)
        {
            _diffuse.SetPixels(new[] { color, color, color, color });
            _diffuse.Apply(false, false);
            EditorUtility.SetDirty(_diffuse);
        }

        private Texture2D CreateSavedDensity(string name, int resolution)
        {
            Texture2D texture = Own(new Texture2D(resolution, resolution, TextureFormat.R8, false, true) { name = name });
            byte[] pixels = new byte[resolution * resolution];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = 255;
            }

            texture.LoadRawTextureData(pixels);
            texture.Apply(false, false);
            AssetDatabase.AddObjectToAsset(texture, _target);
            EditorUtility.SetDirty(texture);
            EditorUtility.SetDirty(_target);
            AssetDatabase.SaveAssetIfDirty(_target);
            return texture;
        }

        private void OnBaked(Terrain source, Texture2D output)
        {
            if (source == _terrain)
            {
                _bakeCount++;
            }
        }

        private T Own<T>(T value) where T : Object { _owned.Add(value); return value; }

        private static void AssertColor(Color actual, Color expected)
        {
            Assert.That(actual.r, Is.EqualTo(expected.r).Within(0.02f));
            Assert.That(actual.g, Is.EqualTo(expected.g).Within(0.02f));
            Assert.That(actual.b, Is.EqualTo(expected.b).Within(0.02f));
        }
    }
}
