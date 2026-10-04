using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed class GrassInteractorTests
{
    [TestCase(30)] [TestCase(60)] [TestCase(120)]
    public void AttackAndRecoveryAreTimeBased(int rate)
    {
        float response = 0f;
        for (int i = 0; i < rate; i++) response = GrassInteractionMath.Response(response, .9f, 1f / rate, .06f);
        Assert.That(response, Is.EqualTo(.9f * (1f - Mathf.Exp(-1f / .06f))).Within(.00001f));
        Assert.That(GrassInteractionMath.Recovery(.9f, 1f, .55f),
            Is.EqualTo(.9f * Mathf.Exp(-1f / .55f)).Within(.00001f));
    }

    [Test]
    public void VariableCadenceResponseMatchesElapsedTime()
    {
        float response = 0f, elapsed = 0f;
        foreach (float dt in new[] { .008f, .035f, .012f, .1f, .017f })
        { response = GrassInteractionMath.Response(response, 1f, dt, .06f); elapsed += dt; }
        Assert.That(response, Is.EqualTo(1f - Mathf.Exp(-elapsed / .06f)).Within(.00001f));
    }

    [Test]
    public void SamplingRejectsDiscontinuitiesAndAirborneVolumes()
    {
        Assert.That(GrassInteractionMath.SegmentSamples(.3f, .5f), Is.EqualTo(2));
        Assert.That(GrassInteractionMath.SegmentSamples(40f, .5f), Is.Zero);
        Assert.That(GrassInteractionMath.SegmentSamples(float.NaN, .5f), Is.Zero);
        Assert.That(GrassInteractionMath.HeightContact(0f, 0f, .5f), Is.EqualTo(1f));
        Assert.That(GrassInteractionMath.HeightContact(.5f, 0f, .5f), Is.Zero);
        Assert.That(GrassInteractionMath.HeightContact(.25f, 0f, .5f), Is.EqualTo(.5f).Within(.000001f));
    }

    [Test]
    public void ActualColliderUpdatesWithoutCameraAndReleasesCapture()
    {
        GameObject floor = new GameObject("Interactor floor"), actor = new GameObject("Interactor body");
        Mesh ownedMesh = null; Material ownedMaterial = null; GameObject helper = null;
        try
        {
            var ground = floor.AddComponent<BoxCollider>(); ground.size = new Vector3(20, .2f, 20); floor.transform.position = Vector3.down * .1f;
            var body = actor.AddComponent<CapsuleCollider>(); body.center = Vector3.up; body.height = 2f; body.radius = .3f;
            var interactor = actor.AddComponent<GrassInteractor>(); interactor.body = body;
            interactor.interactionShader = Shader.Find("InfiniteGrass/Modifiers/GrassInteractor");
            Assert.That(interactor.interactionShader, Is.Not.Null, "The package shader is required; no missing-shader skip.");
            Physics.SyncTransforms(); interactor.Sample(0); interactor.Sample(.1);
            Assert.That(interactor.ContactStrength, Is.GreaterThan(.5f));
            Assert.That(interactor.CaptureRenderer.enabled, Is.True);
            helper = interactor.CaptureRenderer.gameObject;
            ownedMesh = helper.GetComponent<MeshFilter>().sharedMesh; ownedMaterial = interactor.CaptureRenderer.sharedMaterial;
            actor.transform.position = new Vector3(1, 0, 0); Physics.SyncTransforms(); interactor.Sample(.2);
            Assert.That(interactor.RetainedStampCount, Is.GreaterThan(0));
            actor.transform.position = new Vector3(1, 1, 0); Physics.SyncTransforms(); interactor.Sample(.3);
            Assert.That(interactor.CaptureRenderer.enabled, Is.True, "Departed ground contact must recover instead of disappearing.");
            for (int i = 4; i < 50; i++) interactor.Sample(i * .1);
            Assert.That(interactor.RetainedStampCount, Is.Zero);
            Assert.That(interactor.CaptureRenderer.enabled, Is.False);
            interactor.enabled = false;
            Assert.That(helper == null && ownedMesh == null && ownedMaterial == null, Is.True);
        }
        finally { Object.DestroyImmediate(actor); Object.DestroyImmediate(floor); }
    }

    [Test]
    public void SupportChangeRetainsRecoveringContactOnPreviousCollider()
    {
        var first = new GameObject("First support"); var second = new GameObject("Second support");
        var actor = new GameObject("Support crossing body");
        try
        {
            first.transform.position = new Vector3(-1, -.1f, 0); second.transform.position = new Vector3(1, -.1f, 0);
            first.AddComponent<BoxCollider>().size = second.AddComponent<BoxCollider>().size = new Vector3(2, .2f, 4);
            var body = actor.AddComponent<CapsuleCollider>(); body.center = Vector3.up; body.height = 2f; body.radius = .3f;
            var interactor = actor.AddComponent<GrassInteractor>(); interactor.body = body;
            interactor.interactionShader = Shader.Find("InfiniteGrass/Modifiers/GrassInteractor");
            Assert.That(interactor.interactionShader, Is.Not.Null);
            actor.transform.position = Vector3.left * .5f; Physics.SyncTransforms(); interactor.Sample(0); interactor.Sample(.1);
            actor.transform.position = Vector3.right * .5f; Physics.SyncTransforms(); interactor.Sample(.2);
            Assert.That(interactor.RetainedStampCount, Is.EqualTo(1), "A collider boundary must retain the old live footprint.");
            Vector3[] vertices = interactor.CaptureRenderer.GetComponent<MeshFilter>().sharedMesh.vertices;
            Vector3 oldCentre = Vector3.zero;
            for (int i = 4; i < 8; i++) oldCentre += actor.transform.TransformPoint(vertices[i]) * .25f;
            Assert.That(oldCentre.x, Is.EqualTo(-.5f).Within(.0001f));
            first.transform.position += Vector3.back;
            Physics.SyncTransforms(); interactor.Sample(.3);
            vertices = interactor.CaptureRenderer.GetComponent<MeshFilter>().sharedMesh.vertices;
            oldCentre = Vector3.zero;
            for (int i = 4; i < 8; i++) oldCentre += actor.transform.TransformPoint(vertices[i]) * .25f;
            Assert.That(oldCentre.z, Is.EqualTo(-1f).Within(.0001f), "Recovery stays in its supporting collider's coordinates.");
        }
        finally { Object.DestroyImmediate(actor); Object.DestroyImmediate(first); Object.DestroyImmediate(second); }
    }

    [Test]
    public void InteractionDirtyDoesNotInvalidateHeightOrGround()
    {
        InfiniteGrassRenderer previous = InfiniteGrassRenderer.Instance;
        if (previous) previous.enabled = false;
        var actor = new GameObject("Interaction settings");
        try
        {
            var settings = actor.AddComponent<InfiniteGrassRenderer>(); uint revision = settings.Revision;
            uint interaction = settings.InteractionRevision;
            settings.RefreshGrassInteraction();
            Assert.That(settings.Revision, Is.EqualTo(revision));
            Assert.That(settings.InteractionRevision, Is.EqualTo(interaction + 1));
        }
        finally { Object.DestroyImmediate(actor); if (previous) previous.enabled = true; }
    }
}
