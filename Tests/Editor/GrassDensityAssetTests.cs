using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

public sealed class GrassDensityAssetTests
{
    private GrassDensityAsset asset;

    [SetUp]
    public void SetUp()
    {
        asset = ScriptableObject.CreateInstance<GrassDensityAsset>();
        asset.Resize(64, 64, false);
    }

    [TearDown]
    public void TearDown()
    {
        Undo.ClearUndo(asset);
        Object.DestroyImmediate(asset);
    }

    [Test]
    public void NewMapIsEmptyLinearDataAndNeverClampsCoverageOutsideItsExtent()
    {
        Assert.That(asset.HasCoverage, Is.False);
        Assert.That(asset.Sample(new Vector2(0.5f, 0.5f)), Is.Zero);
        Assert.That(GraphicsFormatUtility.IsSRGBFormat(asset.Texture.graphicsFormat), Is.False);

        asset.Fill(1f);
        Assert.That(asset.Sample(new Vector2(0f, 0.5f)), Is.EqualTo(1f));
        Assert.That(asset.Sample(new Vector2(-0.001f, 0.5f)), Is.Zero);
        Assert.That(asset.Sample(new Vector2(0.5f, 1.001f)), Is.Zero);
        Assert.That(asset.Sample(new Vector2(float.NaN, 0.5f)), Is.Zero);
        Assert.That(asset.HasCoverageIn(new Rect(1.1f, 0f, 0.1f, 1f)), Is.False);
    }

    [Test]
    public void EllipticalBrushPaintsOnlyItsFootprintAndEraseRemovesTheSameRegion()
    {
        var center = new Vector2(0.35f, 0.55f);
        var radii = new Vector2(0.2f, 0.1f);

        Assert.That(asset.Paint(center, radii, 0.6f, 1f, false), Is.True);
        Assert.That(asset.HasCoverage, Is.True);
        Assert.That(asset.Sample(center), Is.EqualTo(0.6f).Within(1f / 255f));
        Assert.That(asset.Sample(center + new Vector2(0.12f, 0f)), Is.GreaterThan(0.5f));
        Assert.That(asset.Sample(center + new Vector2(0f, 0.15f)), Is.Zero);
        Assert.That(asset.Sample(center + new Vector2(0.25f, 0f)), Is.Zero);

        Assert.That(asset.Paint(center, radii, 0.6f, 1f, true), Is.True);
        Assert.That(asset.HasCoverage, Is.False);
        Assert.That(asset.Sample(center), Is.Zero);
        Assert.That(asset.Paint(center, radii, 1f, 1f, true), Is.False);
    }

    [Test]
    public void OccupancyIncludesBilinearCoverageAcrossAQuantizedBlockBoundary()
    {
        // Place one occupied texel immediately before a 16-texel block boundary.
        var center = new Vector2(15.5f / 64f, 15.5f / 64f);
        asset.Paint(center, new Vector2(0.25f / 64f, 0.25f / 64f), 1f, 1f, false);

        var filteredPoint = new Vector2(16.25f / 64f, 15.5f / 64f);
        Assert.That(asset.Sample(filteredPoint), Is.GreaterThan(0f));
        Assert.That(asset.HasCoverageIn(new Rect(filteredPoint.x - 0.0001f,
            filteredPoint.y - 0.0001f, 0.0002f, 0.0002f)), Is.True,
            "A tile must not disappear while bilinear filtering still reaches an occupied neighbor.");
        Assert.That(asset.HasCoverageIn(new Rect(0.8f, 0.8f, 0.01f, 0.01f)), Is.False);

        asset.Fill(0f);
        Assert.That(asset.HasCoverageIn(new Rect(0f, 0f, 1f, 1f)), Is.False);
    }

    [Test]
    public void ResizePreservesAnOffCenterFeatureWithoutFlippingOrStretchingItsUvLocation()
    {
        asset.Resize(32, 64, false);
        var feature = new Vector2(0.21875f, 0.71875f);
        asset.Paint(feature, new Vector2(0.07f, 0.08f), 1f, 1f, false);
        Assert.That(asset.Sample(feature), Is.GreaterThan(0.9f));

        asset.Resize(96, 48, true);
        Assert.That(asset.Texture.width, Is.EqualTo(96));
        Assert.That(asset.Texture.height, Is.EqualTo(48));
        Assert.That(asset.Sample(feature), Is.GreaterThan(0.9f));
        Assert.That(asset.Sample(new Vector2(1f - feature.x, feature.y)), Is.Zero);
        Assert.That(asset.Sample(new Vector2(feature.y, feature.x)), Is.Zero);

        asset.Resize(16, 32, false);
        Assert.That(asset.HasCoverage, Is.False);
    }

    [Test]
    public void UndoRestoresSerializedDensityAndTheDerivedTextureAfterInvalidation()
    {
        var center = new Vector2(0.5f, 0.5f);
        Texture2D texture = asset.Texture;
        Assert.That(texture.GetPixel(32, 32).r, Is.Zero);

        Undo.RegisterCompleteObjectUndo(asset, "Paint test grass");
        asset.Paint(center, new Vector2(0.15f, 0.15f), 1f, 1f, false);
        Undo.FlushUndoRecordObjects();
        Assert.That(asset.Texture.GetPixel(32, 32).r, Is.EqualTo(1f));

        Undo.PerformUndo();
        // This is the public invalidation contract used by editor Undo handlers.
        asset.NotifyChanged();
        Assert.That(asset.HasCoverage, Is.False);
        Assert.That(asset.Sample(center), Is.Zero);
        Assert.That(asset.Texture.GetPixel(32, 32).r, Is.Zero);

        Undo.PerformRedo();
        asset.NotifyChanged();
        Assert.That(asset.HasCoverage, Is.True);
        Assert.That(asset.Sample(center), Is.EqualTo(1f));
        Assert.That(asset.Texture.GetPixel(32, 32).r, Is.EqualTo(1f));
    }

    [Test]
    public void SmallDabsCoalesceAnExactDirtyRectangleAndNoOpEditsKeepTheRevision()
    {
        Texture2D texture = asset.Texture;
        uint initialRevision = asset.Revision;
        uint initialUploads = asset.TextureUploadCount;
        RectInt reported = default;
        asset.RegionChanged += region => reported = region;

        asset.Fill(0f);
        Assert.That(asset.Paint(new Vector2(-2f, -2f), Vector2.one * 0.1f, 1f, 1f, false), Is.False);
        Assert.That(asset.Paint(Vector2.one * 0.5f, Vector2.one * 0.1f, 1f, 1f, true), Is.False);
        Assert.That(asset.Revision, Is.EqualTo(initialRevision));
        Assert.That(asset.PendingUploadRegion.width, Is.Zero);

        PaintTexel(7, 11, false);
        Assert.That(reported, Is.EqualTo(new RectInt(7, 11, 1, 1)));
        PaintTexel(9, 13, false);
        Assert.That(reported, Is.EqualTo(new RectInt(9, 13, 1, 1)));
        Assert.That(asset.PendingUploadRegion, Is.EqualTo(new RectInt(7, 11, 3, 3)));
        Assert.That(asset.TextureUploadCount, Is.EqualTo(initialUploads), "Dabs defer GPU uploads until the texture is consumed.");
        Assert.That(asset.Texture, Is.SameAs(texture));
        Assert.That(asset.TextureUploadCount, Is.EqualTo(initialUploads + 1));
        Assert.That(asset.PendingUploadRegion.width, Is.Zero);
        Assert.That(asset.TextureUploadCount, Is.EqualTo(initialUploads + 1), "Reading a clean texture does not upload again.");
    }

    [Test]
    public void OccupancyShrinksAfterErasingAnEdgeAndRejectsEmptyPixelsInsideAnOccupiedBlock()
    {
        asset.Resize(65, 73, false);
        PaintTexel(0, 0, false);
        PaintTexel(15, 17, false);
        PaintTexel(64, 72, false);
        Assert.That(asset.TryGetCoverageBounds(out Rect allBounds), Is.True);
        Assert.That(allBounds, Is.EqualTo(new Rect(0f, 0f, 1f, 1f)));

        PaintTexel(0, 0, true);
        Assert.That(asset.TryGetCoverageBounds(out Rect smallerBounds), Is.True);
        Assert.That(smallerBounds.xMin, Is.EqualTo(14.5f / 65f).Within(0.00001f));
        Assert.That(smallerBounds.yMin, Is.EqualTo(16.5f / 73f).Within(0.00001f));
        var emptyPointInSameBlock = new Vector2(2.5f / 65f, 20.5f / 73f);
        Assert.That(asset.HasCoverageIn(new Rect(emptyPointInSameBlock, Vector2.zero)), Is.False);

        PaintTexel(64, 72, true);
        Assert.That(asset.TryGetCoverageBounds(out Rect oneTexelBounds), Is.True);
        Assert.That(oneTexelBounds.xMax, Is.EqualTo(16.5f / 65f).Within(0.00001f));
        Assert.That(oneTexelBounds.yMax, Is.EqualTo(18.5f / 73f).Within(0.00001f));
        Assert.That(asset.HasCoverageIn(new Rect(64f / 65f, 72f / 73f, 0.01f, 0.01f)), Is.False);

        PaintTexel(15, 17, true);
        Assert.That(asset.HasCoverage, Is.False);
        Assert.That(asset.TryGetCoverageBounds(out _), Is.False);
    }

    [Test]
    public void LargeStagingBucketsUseASingleFullUpload()
    {
        Texture2D texture = asset.Texture;
        asset.Paint(Vector2.one * 0.5f, Vector2.one * (17f / 64f), 1f, 1f, false);
        Assert.That(asset.PendingUploadRegion.width * asset.PendingUploadRegion.height,
            Is.LessThan(asset.Width * asset.Height / 2));
        Assert.That(asset.Texture, Is.SameAs(texture));
        Assert.That(asset.LastUploadWasPartial, Is.False,
            "A staging rectangle rounded up to the complete map would upload as much data and add a redundant GPU copy.");
        Assert.That(asset.LastUploadedTexelCount, Is.EqualTo(asset.Width * asset.Height));
    }

    [Test, Category("GrassGPU")]
    public void RegionalUploadPreservesPreviousCpuAndGpuTexelsAndUploadsLessThanTheMap()
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ||
            (SystemInfo.copyTextureSupport & CopyTextureSupport.Basic) == 0 ||
            !SystemInfo.SupportsTextureFormat(TextureFormat.R8) ||
            !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGB32))
            Assert.Ignore("A graphics device supporting regional R8 copies is required.");

        asset.Resize(256, 128, false);
        PaintTexel(40, 100, false);
        Texture2D originalTexture = asset.Texture;
        Assert.That(asset.LastUploadWasPartial, Is.False);
        PaintTexel(200, 20, false);
        Texture2D texture = asset.Texture;
        Assert.That(texture, Is.SameAs(originalTexture));
        Assert.That(asset.LastUploadWasPartial, Is.True);
        Assert.That(asset.LastUploadedTexelCount, Is.LessThan(asset.Width * asset.Height / 4));
        Assert.That(texture.GetPixel(40, 100).r, Is.EqualTo(1f));
        Assert.That(texture.GetPixel(200, 20).r, Is.EqualTo(1f));
        Assert.That(texture.GetPixel(100, 50).r, Is.Zero);

        RenderTexture capture = RenderTexture.GetTemporary(asset.Width, asset.Height, 0,
            RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        Texture2D readback = new Texture2D(asset.Width, asset.Height, TextureFormat.RGBA32, false, true);
        RenderTexture previous = RenderTexture.active;
        try
        {
            Graphics.Blit(texture, capture);
            RenderTexture.active = capture;
            readback.ReadPixels(new Rect(0, 0, asset.Width, asset.Height), 0, 0, false);
            readback.Apply(false, false);
            Assert.That(readback.GetPixel(40, 100).r, Is.GreaterThan(0.99f));
            Assert.That(readback.GetPixel(200, 20).r, Is.GreaterThan(0.99f));
            Assert.That(readback.GetPixel(100, 50).r, Is.LessThan(0.01f));
        }
        finally
        {
            RenderTexture.active = previous;
            Object.DestroyImmediate(readback);
            RenderTexture.ReleaseTemporary(capture);
        }

        asset.NotifyChanged();
        Assert.That(asset.Texture.GetPixel(40, 100).r, Is.EqualTo(1f));
        Assert.That(asset.Texture.GetPixel(200, 20).r, Is.EqualTo(1f));
        Assert.That(asset.LastUploadWasPartial, Is.False, "Serialized invalidation restores the authoritative full map.");
    }

    private void PaintTexel(int x, int y, bool erase)
    {
        Assert.That(asset.Paint(new Vector2((x + 0.5f) / asset.Width, (y + 0.5f) / asset.Height),
            new Vector2(0.25f / asset.Width, 0.25f / asset.Height), 1f, 1f, erase), Is.True);
    }
}
