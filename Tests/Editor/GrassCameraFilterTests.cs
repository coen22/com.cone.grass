using NUnit.Framework;
using UnityEngine;

/// <summary>
/// Which cameras draw the grass: game views and, when enabled, the scene view, that see the owner's layer. A utility
/// camera culling everything, such as a ground or depth capture submitting its own renders, draws none.
/// </summary>
public sealed class GrassCameraFilterTests
{
    GameObject ownerObject, cameraObject;
    InfiniteGrassRenderer owner;
    Camera camera;

    [SetUp]
    public void Create()
    {
        ownerObject = new GameObject("Grass camera filter owner") { hideFlags = HideFlags.HideAndDontSave };
        ownerObject.SetActive(false);
        owner = ownerObject.AddComponent<InfiniteGrassRenderer>();
        cameraObject = new GameObject("Grass camera filter camera") { hideFlags = HideFlags.HideAndDontSave };
        camera = cameraObject.AddComponent<Camera>();
        camera.enabled = false;
    }

    [TearDown]
    public void Destroy()
    {
        Object.DestroyImmediate(cameraObject);
        Object.DestroyImmediate(ownerObject);
    }

    [Test]
    public void AGameCameraThatSeesTheOwnersLayerDrawsTheGrass()
    {
        Assert.That(owner.DrawsFor(camera), Is.True);
    }

    [Test]
    public void ACameraThatCullsTheOwnersLayerDrawsNoGrass()
    {
        camera.cullingMask = 0;
        Assert.That(owner.DrawsFor(camera), Is.False, "A camera culling everything, like a ground capture's idle render.");

        ownerObject.layer = 5;
        camera.cullingMask = 1 << 0;
        Assert.That(owner.DrawsFor(camera), Is.False, "A camera that sees only another layer, like a preview portrait.");
        camera.cullingMask |= 1 << 5;
        Assert.That(owner.DrawsFor(camera), Is.True);
    }

    [Test]
    public void ARefusedCameraWithdrawsTheFieldTheLastCameraPublished()
    {
        var field = new RenderTexture(4, 4, 0) { hideFlags = HideFlags.HideAndDontSave };
        var refusedObject = new GameObject("Grass camera filter refused camera") { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            // Camera A draws the grass and publishes its interaction capture.
            Assert.That(owner.AdmitCamera(camera), Is.True);
            GrassInteractionPublisher.Publish(field, Vector2.zero, 10f);
            Assert.That(Shader.GetGlobalVector(GrassInteractionPublisher.ParamsId).x, Is.EqualTo(1f));

            // Camera B culls the grass's layer: ground fog on it must not read A's contacts.
            var refused = refusedObject.AddComponent<Camera>();
            refused.enabled = false;
            refused.cullingMask = 0;
            Assert.That(owner.AdmitCamera(refused), Is.False);
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
    public void OnlyGameViewsAndTheEnabledSceneViewDrawTheGrass()
    {
        camera.cameraType = CameraType.Preview;
        Assert.That(owner.DrawsFor(camera), Is.False);
        camera.cameraType = CameraType.Reflection;
        Assert.That(owner.DrawsFor(camera), Is.False);
        camera.cameraType = CameraType.SceneView;
        owner.renderInSceneView = true;
        Assert.That(owner.DrawsFor(camera), Is.True);
        owner.renderInSceneView = false;
        Assert.That(owner.DrawsFor(camera), Is.False);
        Assert.That(owner.DrawsFor(null), Is.False);
    }
}
