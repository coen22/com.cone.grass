using NUnit.Framework;
using UnityEngine;

namespace Cone.Grass.Tests
{
    /// <summary>
    /// Which cameras draw the grass: game views and, when enabled, the scene view, that see the owner's layer. A utility
    /// camera culling everything, such as a ground or depth capture submitting its own renders, draws none.
    /// </summary>
    public sealed class GrassCameraFilterTests
    {
        private GameObject _ownerObject, _cameraObject;
        private InfiniteGrassRenderer _owner;
        private Camera _camera;

        [SetUp]
        public void Create()
        {
            _ownerObject = new GameObject("Grass camera filter owner") { hideFlags = HideFlags.HideAndDontSave };
            _ownerObject.SetActive(false);
            _owner = _ownerObject.AddComponent<InfiniteGrassRenderer>();
            _cameraObject = new GameObject("Grass camera filter camera") { hideFlags = HideFlags.HideAndDontSave };
            _camera = _cameraObject.AddComponent<Camera>();
            _camera.enabled = false;
        }

        [TearDown]
        public void Destroy()
        {
            Object.DestroyImmediate(_cameraObject);
            Object.DestroyImmediate(_ownerObject);
        }

        [Test]
        public void AGameCameraThatSeesTheOwnersLayerDrawsTheGrass()
        {
            Assert.That(_owner.DrawsFor(_camera), Is.True);
        }

        [Test]
        public void ACameraThatCullsTheOwnersLayerDrawsNoGrass()
        {
            _camera.cullingMask = 0;
            Assert.That(_owner.DrawsFor(_camera), Is.False, "A camera culling everything, like a ground capture's idle render.");

            _ownerObject.layer = 5;
            _camera.cullingMask = 1 << 0;
            Assert.That(_owner.DrawsFor(_camera), Is.False, "A camera that sees only another layer, like a preview portrait.");
            _camera.cullingMask |= 1 << 5;
            Assert.That(_owner.DrawsFor(_camera), Is.True);
        }

        [Test]
        public void ARefusedCameraWithdrawsTheFieldTheLastCameraPublished()
        {
            var field = new RenderTexture(4, 4, 0) { hideFlags = HideFlags.HideAndDontSave };
            var refusedObject = new GameObject("Grass camera filter refused camera") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                // Camera A draws the grass and publishes its interaction capture.
                Assert.That(_owner.AdmitCamera(_camera), Is.True);
                GrassInteractionPublisher.Publish(field, Vector2.zero, 10f);
                Assert.That(Shader.GetGlobalVector(GrassInteractionPublisher.ParamsId).x, Is.EqualTo(1f));

                // Camera B culls the grass's layer: ground fog on it must not read A's contacts.
                var refused = refusedObject.AddComponent<Camera>();
                refused.enabled = false;
                refused.cullingMask = 0;
                Assert.That(_owner.AdmitCamera(refused), Is.False);
                Assert.That(Shader.GetGlobalVector(GrassInteractionPublisher.ParamsId).x, Is.Zero, "B still sees A's interaction field.");
            }
            finally
            {
                GrassInteractionPublisher.Withdraw();
                Object.DestroyImmediate(refusedObject);
                field.Release();
                Object.DestroyImmediate(field);
            }
        }

        [Test]
        public void PlanarReflectionCamerasDrawTheGrassButProbeCapturesDoNot()
        {
            _camera.cameraType = CameraType.Reflection;
            _camera.aspect = 16f / 9f;
            _camera.fieldOfView = 60f;
            Assert.That(_owner.Reflections.Cameras, Is.EqualTo(GrassReflectionCameras.Planar), "Planar reflections draw grass by default.");
            Assert.That(_owner.DrawsFor(_camera), Is.True, "A planar reflection camera, such as Crest's.");

            _camera.fieldOfView = 90f;
            _camera.aspect = 1f;
            Assert.That(GrassReflectionSettings.IsProbeCapture(_camera), Is.True);
            Assert.That(_owner.DrawsFor(_camera), Is.False, "A reflection-probe cube face pays nothing for the grass.");
            _owner.Reflections.Cameras = GrassReflectionCameras.All;
            Assert.That(_owner.DrawsFor(_camera), Is.True);
            _camera.cullingMask = 0;
            Assert.That(_owner.DrawsFor(_camera), Is.False, "A reflection that culls the owner's layer draws none.");
        }

        [Test]
        public void ReflectionScalesAreClamped()
        {
            var settings = new GrassReflectionSettings { SpacingScale = 20f, DrawDistanceScale = 0f };
            Assert.That(settings.EffectiveSpacingScale, Is.EqualTo(8f));
            Assert.That(settings.EffectiveDrawDistanceScale, Is.EqualTo(0.05f));
            settings.SpacingScale = float.NaN;
            settings.DrawDistanceScale = float.PositiveInfinity;
            Assert.That(settings.EffectiveSpacingScale, Is.EqualTo(1f));
            Assert.That(settings.EffectiveDrawDistanceScale, Is.EqualTo(1f));
            Assert.That(new GrassReflectionSettings().EffectiveSpacingScale, Is.EqualTo(2f), "A quarter of the blades by default.");
        }

        [Test]
        public void OnlyGameViewsTheEnabledSceneViewAndAdmittedReflectionsDrawTheGrass()
        {
            _camera.cameraType = CameraType.Preview;
            Assert.That(_owner.DrawsFor(_camera), Is.False);
            _camera.cameraType = CameraType.Reflection;
            _owner.Reflections.Cameras = GrassReflectionCameras.None;
            Assert.That(_owner.DrawsFor(_camera), Is.False, "Reflections off.");
            _camera.cameraType = CameraType.SceneView;
            _owner.RenderInSceneView = true;
            Assert.That(_owner.DrawsFor(_camera), Is.True);
            _owner.RenderInSceneView = false;
            Assert.That(_owner.DrawsFor(_camera), Is.False);
            Assert.That(_owner.DrawsFor(null), Is.False);
        }
    }
}
