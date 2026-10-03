using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

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
}
