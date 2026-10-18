using NUnit.Framework;
using UnityEngine;

public class GrassInteractionPublisherTests
{
    Texture previousField;
    Vector4 previousScaleOffset, previousParams;

    [SetUp]
    public void SaveGlobals()
    {
        previousField = Shader.GetGlobalTexture(GrassInteractionPublisher.FieldId);
        previousScaleOffset = Shader.GetGlobalVector(GrassInteractionPublisher.ScaleOffsetId);
        previousParams = Shader.GetGlobalVector(GrassInteractionPublisher.ParamsId);
    }

    [TearDown]
    public void RestoreGlobals()
    {
        Shader.SetGlobalTexture(GrassInteractionPublisher.FieldId, previousField);
        Shader.SetGlobalVector(GrassInteractionPublisher.ScaleOffsetId, previousScaleOffset);
        Shader.SetGlobalVector(GrassInteractionPublisher.ParamsId, previousParams);
    }

    // GrassWorldToMapUV: uv = (xz - center) / (2 extent) + 0.5, so the centre is 0.5 and the capture's edges 0 and 1.
    [TestCase(0f, 0f, 30f)]
    [TestCase(12.5f, -40f, 37.5f)]
    public void ScaleOffsetMatchesTheCaptureMapping(float centerX, float centerZ, float extent)
    {
        var center = new Vector2(centerX, centerZ);
        Vector4 st = GrassInteractionPublisher.ScaleOffset(center, extent);
        Vector2[] world = { center, center - Vector2.one * extent, center + Vector2.one * extent, center + new Vector2(extent * 0.5f, -extent) };
        foreach (Vector2 xz in world)
        {
            Vector2 expected = (xz - center) / (2f * extent) + Vector2.one * 0.5f;
            Vector2 uv = new Vector2(xz.x * st.x + st.z, xz.y * st.y + st.w);
            Assert.That(uv.x, Is.EqualTo(expected.x).Within(1e-6f), $"{xz}");
            Assert.That(uv.y, Is.EqualTo(expected.y).Within(1e-6f), $"{xz}");
        }
    }

    [Test]
    public void PublishSetsTheContractAndWithdrawClearsTheFlag()
    {
        var field = new RenderTexture(4, 4, 0);
        // An uncreated render texture reads back as no global texture.
        field.Create();
        try
        {
            GrassInteractionPublisher.Publish(field, new Vector2(10f, -4f), 20f);
            Assert.That(Shader.GetGlobalTexture(GrassInteractionPublisher.FieldId), Is.SameAs(field));
            Assert.That(Shader.GetGlobalVector(GrassInteractionPublisher.ScaleOffsetId),
                Is.EqualTo(GrassInteractionPublisher.ScaleOffset(new Vector2(10f, -4f), 20f)));
            Assert.That(Shader.GetGlobalVector(GrassInteractionPublisher.ParamsId).x, Is.EqualTo(1f));

            GrassInteractionPublisher.Withdraw();
            Assert.That(Shader.GetGlobalVector(GrassInteractionPublisher.ParamsId).x, Is.EqualTo(0f));

            GrassInteractionPublisher.Publish(field, Vector2.zero, 20f);
            GrassInteractionPublisher.Publish(null, Vector2.zero, 20f);
            Assert.That(Shader.GetGlobalVector(GrassInteractionPublisher.ParamsId).x, Is.EqualTo(0f));
        }
        finally
        {
            Object.DestroyImmediate(field);
        }
    }
}
