using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed partial class TerrainGrassAlbedoBakerTests
{
    [Test]
    public void TransientBakeMatchesSavedAlbedoWithoutAssetsOrCompletionCallbacks()
    {
        SetLayers(new[] { Layer(Color.red, true), Layer(Color.blue, true) }, new[] { .25f, .75f });
        int terrainDirty = EditorUtility.GetDirtyCount(terrain.terrainData);
        int materialDirty = EditorUtility.GetDirtyCount(terrain.materialTemplate);
        string[] assets = AssetDatabase.FindAssets("", new[] { folder });
        int notifications = 0;
        void OnBaked(Terrain _, Texture2D __) => notifications++;
        TerrainGrassAlbedoBaker.Baked += OnBaked;
        Texture2D transient = null;
        try
        {
            Assert.That(TerrainGrassAlbedoBaker.TryBakeTransient(terrain, 16, out transient, out string error), Is.True, error);
            Own(transient);
            Assert.That(transient.hideFlags, Is.EqualTo(HideFlags.DontSave));
            Assert.That(AssetDatabase.Contains(transient), Is.False);
            Assert.That(transient.format, Is.EqualTo(TextureFormat.RGBAHalf));
            Assert.That(transient.isDataSRGB, Is.False);
            Assert.That(transient.mipmapCount, Is.GreaterThan(1));
            Assert.That(AssetDatabase.FindAssets("", new[] { folder }), Is.EqualTo(assets));
            Assert.That(EditorUtility.GetDirtyCount(terrain.terrainData), Is.EqualTo(terrainDirty));
            Assert.That(EditorUtility.GetDirtyCount(terrain.materialTemplate), Is.EqualTo(materialDirty));
            Assert.That(notifications, Is.Zero);
            Assert.That(transient.GetPixels(), Is.EqualTo(Bake().GetPixels()));
            Object.DestroyImmediate(transient);
            Assert.That(transient == null, Is.True, "The caller owns and can release the unsaved output.");
        }
        finally { TerrainGrassAlbedoBaker.Baked -= OnBaked; }
    }
}
