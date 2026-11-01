using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Cone.Grass.Tests
{
    public sealed class GrassDensityAssetTests
    {
        private GrassDensityAsset _asset;

        [SetUp]
        public void SetUp()
        {
            _asset = ScriptableObject.CreateInstance<GrassDensityAsset>();
            _asset.Resize(64, 64, false);
        }

        [TearDown]
        public void TearDown()
        {
            Undo.ClearUndo(_asset);
            Object.DestroyImmediate(_asset);
        }

        [Test]
        public void NewMapIsEmptyLinearDataAndNeverClampsCoverageOutsideItsExtent()
        {
            Assert.That(_asset.HasCoverage, Is.False);
            Assert.That(_asset.Sample(new Vector2(0.5f, 0.5f)), Is.Zero);
            Assert.That(GraphicsFormatUtility.IsSRGBFormat(_asset.Texture.graphicsFormat), Is.False);

            _asset.Fill(1f);
            Assert.That(_asset.Sample(new Vector2(0f, 0.5f)), Is.EqualTo(1f));
            Assert.That(_asset.Sample(new Vector2(-0.001f, 0.5f)), Is.Zero);
            Assert.That(_asset.Sample(new Vector2(0.5f, 1.001f)), Is.Zero);
            Assert.That(_asset.Sample(new Vector2(float.NaN, 0.5f)), Is.Zero);
            Assert.That(_asset.HasCoverageIn(new Rect(1.1f, 0f, 0.1f, 1f)), Is.False);
        }

        [Test]
        public void EllipticalBrushPaintsOnlyItsFootprintAndEraseRemovesTheSameRegion()
        {
            var center = new Vector2(0.35f, 0.55f);
            var radii = new Vector2(0.2f, 0.1f);

            Assert.That(_asset.Paint(center, radii, 0.6f, 1f, false), Is.True);
            Assert.That(_asset.HasCoverage, Is.True);
            Assert.That(_asset.Sample(center), Is.EqualTo(0.6f).Within(1f / 255f));
            Assert.That(_asset.Sample(center + new Vector2(0.12f, 0f)), Is.GreaterThan(0.5f));
            Assert.That(_asset.Sample(center + new Vector2(0f, 0.15f)), Is.Zero);
            Assert.That(_asset.Sample(center + new Vector2(0.25f, 0f)), Is.Zero);

            Assert.That(_asset.Paint(center, radii, 0.6f, 1f, true), Is.True);
            Assert.That(_asset.HasCoverage, Is.False);
            Assert.That(_asset.Sample(center), Is.Zero);
            Assert.That(_asset.Paint(center, radii, 1f, 1f, true), Is.False);
        }

        [Test]
        public void OccupancyIncludesBilinearCoverageAcrossAQuantizedBlockBoundary()
        {
            // Place one occupied texel immediately before a 16-texel block boundary.
            var center = new Vector2(15.5f / 64f, 15.5f / 64f);
            _asset.Paint(center, new Vector2(0.25f / 64f, 0.25f / 64f), 1f, 1f, false);

            var filteredPoint = new Vector2(16.25f / 64f, 15.5f / 64f);
            Assert.That(_asset.Sample(filteredPoint), Is.GreaterThan(0f));
            Assert.That(_asset.HasCoverageIn(new Rect(filteredPoint.x - 0.0001f,
                filteredPoint.y - 0.0001f, 0.0002f, 0.0002f)), Is.True,
                "A tile must not disappear while bilinear filtering still reaches an occupied neighbor.");
            Assert.That(_asset.HasCoverageIn(new Rect(0.8f, 0.8f, 0.01f, 0.01f)), Is.False);

            _asset.Fill(0f);
            Assert.That(_asset.HasCoverageIn(new Rect(0f, 0f, 1f, 1f)), Is.False);
        }

        [Test]
        public void ResizePreservesAnOffCenterFeatureWithoutFlippingOrStretchingItsUvLocation()
        {
            _asset.Resize(32, 64, false);
            var feature = new Vector2(0.21875f, 0.71875f);
            _asset.Paint(feature, new Vector2(0.07f, 0.08f), 1f, 1f, false);
            Assert.That(_asset.Sample(feature), Is.GreaterThan(0.9f));

            _asset.Resize(96, 48, true);
            Assert.That(_asset.Texture.width, Is.EqualTo(96));
            Assert.That(_asset.Texture.height, Is.EqualTo(48));
            Assert.That(_asset.Sample(feature), Is.GreaterThan(0.9f));
            Assert.That(_asset.Sample(new Vector2(1f - feature.x, feature.y)), Is.Zero);
            Assert.That(_asset.Sample(new Vector2(feature.y, feature.x)), Is.Zero);

            _asset.Resize(16, 32, false);
            Assert.That(_asset.HasCoverage, Is.False);
        }

        [Test]
        public void UndoRestoresSerializedDensityAndTheDerivedTextureAfterInvalidation()
        {
            var center = new Vector2(0.5f, 0.5f);
            Texture2D texture = _asset.Texture;
            Assert.That(texture.GetPixel(32, 32).r, Is.Zero);

            Undo.RegisterCompleteObjectUndo(_asset, "Paint test grass");
            _asset.Paint(center, new Vector2(0.15f, 0.15f), 1f, 1f, false);
            Undo.FlushUndoRecordObjects();
            Assert.That(_asset.Texture.GetPixel(32, 32).r, Is.EqualTo(1f));

            Undo.PerformUndo();
            // This is the public invalidation contract used by editor Undo handlers.
            _asset.NotifyChanged();
            Assert.That(_asset.HasCoverage, Is.False);
            Assert.That(_asset.Sample(center), Is.Zero);
            Assert.That(_asset.Texture.GetPixel(32, 32).r, Is.Zero);

            Undo.PerformRedo();
            _asset.NotifyChanged();
            Assert.That(_asset.HasCoverage, Is.True);
            Assert.That(_asset.Sample(center), Is.EqualTo(1f));
            Assert.That(_asset.Texture.GetPixel(32, 32).r, Is.EqualTo(1f));
        }

        [Test]
        public void SmallDabsCoalesceAnExactDirtyRectangleAndNoOpEditsKeepTheRevision()
        {
            Texture2D texture = _asset.Texture;
            uint initialRevision = _asset.Revision;
            uint initialUploads = _asset.TextureUploadCount;
            RectInt reported = default;
            _asset.RegionChanged += region => reported = region;

            _asset.Fill(0f);
            Assert.That(_asset.Paint(new Vector2(-2f, -2f), Vector2.one * 0.1f, 1f, 1f, false), Is.False);
            Assert.That(_asset.Paint(Vector2.one * 0.5f, Vector2.one * 0.1f, 1f, 1f, true), Is.False);
            Assert.That(_asset.Revision, Is.EqualTo(initialRevision));
            Assert.That(_asset.PendingUploadRegion.width, Is.Zero);

            PaintTexel(7, 11, false);
            Assert.That(reported, Is.EqualTo(new RectInt(7, 11, 1, 1)));
            PaintTexel(9, 13, false);
            Assert.That(reported, Is.EqualTo(new RectInt(9, 13, 1, 1)));
            Assert.That(_asset.PendingUploadRegion, Is.EqualTo(new RectInt(7, 11, 3, 3)));
            Assert.That(_asset.TextureUploadCount, Is.EqualTo(initialUploads), "Dabs defer GPU uploads until the texture is consumed.");
            Assert.That(_asset.Texture, Is.SameAs(texture));
            Assert.That(_asset.TextureUploadCount, Is.EqualTo(initialUploads + 1));
            Assert.That(_asset.PendingUploadRegion.width, Is.Zero);
            Assert.That(_asset.TextureUploadCount, Is.EqualTo(initialUploads + 1), "Reading a clean texture does not upload again.");
        }

        [Test]
        public void OccupancyShrinksAfterErasingAnEdgeAndRejectsEmptyPixelsInsideAnOccupiedBlock()
        {
            _asset.Resize(65, 73, false);
            PaintTexel(0, 0, false);
            PaintTexel(15, 17, false);
            PaintTexel(64, 72, false);
            Assert.That(_asset.TryGetCoverageBounds(out Rect allBounds), Is.True);
            Assert.That(allBounds, Is.EqualTo(new Rect(0f, 0f, 1f, 1f)));

            PaintTexel(0, 0, true);
            Assert.That(_asset.TryGetCoverageBounds(out Rect smallerBounds), Is.True);
            Assert.That(smallerBounds.xMin, Is.EqualTo(14.5f / 65f).Within(0.00001f));
            Assert.That(smallerBounds.yMin, Is.EqualTo(16.5f / 73f).Within(0.00001f));
            var emptyPointInSameBlock = new Vector2(2.5f / 65f, 20.5f / 73f);
            Assert.That(_asset.HasCoverageIn(new Rect(emptyPointInSameBlock, Vector2.zero)), Is.False);

            PaintTexel(64, 72, true);
            Assert.That(_asset.TryGetCoverageBounds(out Rect oneTexelBounds), Is.True);
            Assert.That(oneTexelBounds.xMax, Is.EqualTo(16.5f / 65f).Within(0.00001f));
            Assert.That(oneTexelBounds.yMax, Is.EqualTo(18.5f / 73f).Within(0.00001f));
            Assert.That(_asset.HasCoverageIn(new Rect(64f / 65f, 72f / 73f, 0.01f, 0.01f)), Is.False);

            PaintTexel(15, 17, true);
            Assert.That(_asset.HasCoverage, Is.False);
            Assert.That(_asset.TryGetCoverageBounds(out _), Is.False);
        }

        [Test]
        public void LargeStagingBucketsUseASingleFullUpload()
        {
            Texture2D texture = _asset.Texture;
            _asset.Paint(Vector2.one * 0.5f, Vector2.one * (17f / 64f), 1f, 1f, false);
            Assert.That(_asset.PendingUploadRegion.width * _asset.PendingUploadRegion.height,
                Is.LessThan(_asset.Width * _asset.Height / 2));
            Assert.That(_asset.Texture, Is.SameAs(texture));
            Assert.That(_asset.LastUploadWasPartial, Is.False,
                "A staging rectangle rounded up to the complete map would upload as much data and add a redundant GPU copy.");
            Assert.That(_asset.LastUploadedTexelCount, Is.EqualTo(_asset.Width * _asset.Height));
        }

        [Test, Category("GrassGPU")]
        public void RegionalUploadPreservesPreviousCpuAndGpuTexelsAndUploadsLessThanTheMap()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ||
                (SystemInfo.copyTextureSupport & CopyTextureSupport.Basic) == 0 ||
                !SystemInfo.SupportsTextureFormat(TextureFormat.R8) ||
                !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGB32))
            {
                Assert.Ignore("A graphics device supporting regional R8 copies is required.");
            }

            _asset.Resize(256, 128, false);
            PaintTexel(40, 100, false);
            Texture2D originalTexture = _asset.Texture;
            Assert.That(_asset.LastUploadWasPartial, Is.False);
            PaintTexel(200, 20, false);
            Texture2D texture = _asset.Texture;
            Assert.That(texture, Is.SameAs(originalTexture));
            Assert.That(_asset.LastUploadWasPartial, Is.True);
            Assert.That(_asset.LastUploadedTexelCount, Is.LessThan(_asset.Width * _asset.Height / 4));
            Assert.That(texture.GetPixel(40, 100).r, Is.EqualTo(1f));
            Assert.That(texture.GetPixel(200, 20).r, Is.EqualTo(1f));
            Assert.That(texture.GetPixel(100, 50).r, Is.Zero);

            RenderTexture capture = RenderTexture.GetTemporary(_asset.Width, _asset.Height, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Texture2D readback = new Texture2D(_asset.Width, _asset.Height, TextureFormat.RGBA32, false, true);
            RenderTexture previous = RenderTexture.active;
            try
            {
                Graphics.Blit(texture, capture);
                RenderTexture.active = capture;
                readback.ReadPixels(new Rect(0, 0, _asset.Width, _asset.Height), 0, 0, false);
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

            _asset.NotifyChanged();
            Assert.That(_asset.Texture.GetPixel(40, 100).r, Is.EqualTo(1f));
            Assert.That(_asset.Texture.GetPixel(200, 20).r, Is.EqualTo(1f));
            Assert.That(_asset.LastUploadWasPartial, Is.False, "Serialized invalidation restores the authoritative full map.");
        }

        private void PaintTexel(int x, int y, bool erase)
        {
            Assert.That(_asset.Paint(new Vector2((x + 0.5f) / _asset.Width, (y + 0.5f) / _asset.Height),
                new Vector2(0.25f / _asset.Width, 0.25f / _asset.Height), 1f, 1f, erase), Is.True);
        }
    }
}
