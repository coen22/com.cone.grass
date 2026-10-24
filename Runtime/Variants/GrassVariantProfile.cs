using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>The head a kind carries at its blade tip; None grows a plain blade.</summary>
public enum GrassHeadShape
{
    None = 0,
    /// <summary>A ring of narrow petals round a disc, as a daisy or chamomile.</summary>
    Daisy = 1,
    /// <summary>Five broad petals in a shallow cup, as a buttercup or poppy.</summary>
    Cup = 2,
    /// <summary>A flat cluster of small florets, as yarrow or cow parsley.</summary>
    Umbel = 3,
    /// <summary>An upright spike of florets, as lavender or plantain.</summary>
    Spike = 4,
    /// <summary>A three-lobed leaf held flat, as clover; a herb rather than a flower.</summary>
    Clover = 5,
    /// <summary>The profile's own small mesh, tinted from the palette.</summary>
    Mesh = 6
}

/// <summary>One kind of plant in a profile: its blade, its colour and its optional head.</summary>
[Serializable]
public sealed class GrassVariantKind
{
    public string name = "Grass";

    [Header("Mix")]
    [Tooltip("Share of the roots that grow this kind, relative to the other kinds' weights.")]
    [Min(0f)] public float weight = 1f;
    [Tooltip("Zero sprinkles this kind evenly; one gathers all of it into drifts the size of the profile's Clump Size. Its share stays the same.")]
    [Range(0f, 1f)] public float clumping;
    [Tooltip("Fraction of the ground this kind's drifts cover. Smaller drifts are denser; a drift never holds more than every root.")]
    [Range(0.01f, 0.99f)] public float driftArea = 0.25f;

    [Header("Blade")]
    [Tooltip("Blade height relative to the material's Grass Height.")]
    [Min(0f)] public float heightScale = 1f;
    [Tooltip("Blade width relative to the material's Grass Width.")]
    [Min(0f)] public float widthScale = 1f;
    [Tooltip("Curvature relative to the material's Grass Curving.")]
    [Min(0f)] public float curvingScale = 1f;

    [Header("Blade colour")]
    [Tooltip("Multiplies the blade colour. The root still blends toward the matched ground colour.")]
    public Color tint = Color.white;
    [Tooltip("The colour variation mixes the tint toward this one.")]
    public Color variationTint = new Color(1.15f, 1.05f, 0.7f, 1f);
    [Tooltip("How far each plant's own random number moves its colour toward the variation tint.")]
    [Range(0f, 1f)] public float instanceVariation = 0.25f;
    [Tooltip("How far each clump's noise moves the colour toward the variation tint, so neighbouring plants drift together.")]
    [Range(0f, 1f)] public float clumpVariation = 0.25f;

    [Header("Head")]
    public GrassHeadShape head = GrassHeadShape.None;
    [Tooltip("Head diameter in metres.")]
    [Min(0.001f)] public float headSize = 0.04f;
    [Range(0f, 1f)] public float headSizeRandomness = 0.3f;
    [Tooltip("Petal colours; each head picks one.")]
    public Color[] palette = { Color.white };
    [Tooltip("Zero picks each head's colour independently; one gives a whole drift the same colour.")]
    [Range(0f, 1f)] public float paletteClumping = 0.6f;
    [Tooltip("The disc or floret centre.")]
    public Color centre = new Color(0.95f, 0.75f, 0.15f, 1f);

    public bool HasHead => head != GrassHeadShape.None;

    internal void Sanitize()
    {
        weight = Mathf.Max(0f, Finite(weight, 1f));
        clumping = Mathf.Clamp01(Finite(clumping, 0f));
        driftArea = Mathf.Clamp(Finite(driftArea, 0.25f), 0.01f, 0.99f);
        heightScale = Mathf.Clamp(Finite(heightScale, 1f), 0f, 16f);
        widthScale = Mathf.Clamp(Finite(widthScale, 1f), 0f, 16f);
        curvingScale = Mathf.Clamp(Finite(curvingScale, 1f), 0f, 16f);
        instanceVariation = Mathf.Clamp01(Finite(instanceVariation, 0f));
        clumpVariation = Mathf.Clamp01(Finite(clumpVariation, 0f));
        headSize = Mathf.Clamp(Finite(headSize, 0.04f), 0.001f, 2f);
        headSizeRandomness = Mathf.Clamp01(Finite(headSizeRandomness, 0f));
        paletteClumping = Mathf.Clamp01(Finite(paletteClumping, 0f));
        if (palette == null || palette.Length == 0)
            palette = new[] { Color.white };
    }

    private static float Finite(float value, float fallback) =>
        float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;
}

/// <summary>
/// The kinds of plant an Infinite Grass Renderer grows: blades of several heights and widths, herbs and flowers with
/// heads, mixed by weight, gathered into drifts and varied in colour per plant and per clump. Placement Areas can
/// reweight the kinds locally. Without a profile the renderer grows its one material blade, as before.
/// </summary>
[CreateAssetMenu(menuName = "Cone/Grass/Variant Profile", fileName = "Grass Variant Profile")]
public sealed class GrassVariantProfile : ScriptableObject
{
    public const int MaximumPaletteColours = 4;

    [Tooltip("Changes every kind decision and drift without moving a root.")]
    public int seed = 1;
    [Tooltip("Diameter of a drift or colour clump, in metres.")]
    [Min(0.1f)] public float clumpSize = 4f;
    [Tooltip("Heads are drawn to this camera distance; beyond it a flower's blade tip carries its colour.")]
    [Min(1f)] public float headDrawDistance = 60f;
    [Tooltip("Fraction of the head distance over which heads shrink away.")]
    [Range(0.01f, 1f)] public float headFade = 0.3f;
    [Tooltip("How strongly a distant flower's tip takes its mean petal colour once its head has faded.")]
    [Range(0f, 1f)] public float distantTipColour = 0.8f;
    [Tooltip("The mesh used by kinds whose head is Mesh: about one unit across, pivot at the stem, +Y up.")]
    public Mesh headMesh;
    public List<GrassVariantKind> kinds = new List<GrassVariantKind>();

    [NonSerialized] private uint revision = 1;
    /// <summary>Advances on every inspector or script change reported through <see cref="MarkDirty"/>.</summary>
    public uint Revision => revision;

    public int KindCount => kinds == null ? 0 : Mathf.Min(kinds.Count, GrassVariantSelection.MaximumKinds);

    public bool HasHeads(GrassHeadShape shape)
    {
        for (int k = 0; k < KindCount; k++)
            if (kinds[k] != null && kinds[k].weight > 0f && (shape == GrassHeadShape.Mesh ? kinds[k].head == GrassHeadShape.Mesh :
                    kinds[k].HasHead && kinds[k].head != GrassHeadShape.Mesh))
                return true;
        return false;
    }

    private void OnValidate()
    {
        Sanitize();
        MarkDirty();
    }

    public void MarkDirty()
    {
        unchecked { revision++; }
    }

    internal void Sanitize()
    {
        clumpSize = Mathf.Clamp(Finite(clumpSize, 4f), 0.1f, 1000f);
        headDrawDistance = Mathf.Clamp(Finite(headDrawDistance, 60f), 1f, 10000f);
        headFade = Mathf.Clamp(Finite(headFade, 0.3f), 0.01f, 1f);
        distantTipColour = Mathf.Clamp01(Finite(distantTipColour, 0.8f));
        if (kinds == null)
            kinds = new List<GrassVariantKind>();
        foreach (GrassVariantKind kind in kinds)
            kind?.Sanitize();
    }

    /// <summary>Weights, clumping and drift areas, as the generation compute uses them, ignoring missing kinds.</summary>
    public void GetMix(float[] weights, float[] clumping, float[] driftArea)
    {
        for (int k = 0; k < GrassVariantSelection.MaximumKinds; k++)
        {
            GrassVariantKind kind = k < KindCount ? kinds[k] : null;
            weights[k] = kind != null ? Mathf.Max(0f, kind.weight) : 0f;
            clumping[k] = kind != null ? Mathf.Clamp01(kind.clumping) : 0f;
            driftArea[k] = kind != null ? Mathf.Clamp(kind.driftArea, 0.01f, 0.99f) : 0.25f;
        }
    }

    /// <summary>The mean blade area over the mix relative to the material's blade, for the canopy's leaf area.</summary>
    public float MeanBladeAreaScale()
    {
        float total = 0f, area = 0f;
        for (int k = 0; k < KindCount; k++)
        {
            GrassVariantKind kind = kinds[k];
            if (kind == null || kind.weight <= 0f)
                continue;
            total += kind.weight;
            area += kind.weight * kind.heightScale * kind.widthScale;
        }
        return total > 0f ? area / total : 1f;
    }

    /// <summary>The largest blade height, width and curvature scale, and the largest head, over kinds that grow.</summary>
    public Vector4 MaximumScales()
    {
        var result = new Vector4(1f, 1f, 1f, 0f);
        bool any = false;
        for (int k = 0; k < KindCount; k++)
        {
            GrassVariantKind kind = kinds[k];
            if (kind == null || kind.weight <= 0f)
                continue;
            if (!any)
                result = new Vector4(0f, 0f, 0f, 0f);
            any = true;
            result.x = Mathf.Max(result.x, kind.heightScale);
            result.y = Mathf.Max(result.y, kind.widthScale);
            result.z = Mathf.Max(result.z, kind.curvingScale);
            if (kind.HasHead)
                result.w = Mathf.Max(result.w, kind.headSize * (kind.head == GrassHeadShape.Spike ? 2f : 1f));
        }
        return result;
    }

    /// <summary>A meadow to start from: short and tall blades, a herb, clover and four flowers.</summary>
    public void ResetToMeadow()
    {
        seed = 1;
        clumpSize = 4f;
        headDrawDistance = 60f;
        headFade = 0.3f;
        distantTipColour = 0.8f;
        kinds = new List<GrassVariantKind>
        {
            new GrassVariantKind { name = "Short grass", weight = 0.5f, heightScale = 0.8f, widthScale = 1f,
                clumpVariation = 0.3f },
            new GrassVariantKind { name = "Tall grass", weight = 0.26f, clumping = 0.5f, driftArea = 0.35f, heightScale = 1.45f,
                widthScale = 0.8f, curvingScale = 1.4f, variationTint = new Color(1.2f, 1.1f, 0.65f), clumpVariation = 0.5f },
            new GrassVariantKind { name = "Broad herb", weight = 0.06f, clumping = 0.4f, driftArea = 0.3f, heightScale = 0.5f,
                widthScale = 2.6f, curvingScale = 2f, tint = new Color(0.82f, 0.95f, 0.85f) },
            new GrassVariantKind { name = "Clover", weight = 0.05f, clumping = 0.8f, driftArea = 0.2f, heightScale = 0.25f, widthScale = 0.5f,
                tint = new Color(0.8f, 0.95f, 0.8f), head = GrassHeadShape.Clover, headSize = 0.045f,
                palette = new[] { new Color(0.28f, 0.5f, 0.2f), new Color(0.32f, 0.55f, 0.24f) },
                centre = new Color(0.55f, 0.7f, 0.45f) },
            new GrassVariantKind { name = "Daisy", weight = 0.05f, clumping = 0.9f, driftArea = 0.12f, heightScale = 1.05f, widthScale = 0.3f,
                head = GrassHeadShape.Daisy, headSize = 0.045f,
                palette = new[] { new Color(0.97f, 0.96f, 0.92f), new Color(0.98f, 0.9f, 0.92f) },
                centre = new Color(0.98f, 0.78f, 0.12f) },
            new GrassVariantKind { name = "Buttercup", weight = 0.04f, clumping = 0.85f, driftArea = 0.15f, heightScale = 1.3f, widthScale = 0.25f,
                head = GrassHeadShape.Cup, headSize = 0.035f, palette = new[] { new Color(1f, 0.82f, 0.08f) },
                centre = new Color(0.95f, 0.65f, 0.05f) },
            new GrassVariantKind { name = "Yarrow", weight = 0.025f, clumping = 0.8f, driftArea = 0.12f, heightScale = 1.5f, widthScale = 0.25f,
                head = GrassHeadShape.Umbel, headSize = 0.08f,
                palette = new[] { new Color(0.96f, 0.95f, 0.9f), new Color(0.95f, 0.8f, 0.85f) },
                centre = new Color(0.9f, 0.88f, 0.8f) },
            new GrassVariantKind { name = "Knapweed", weight = 0.02f, clumping = 0.8f, driftArea = 0.12f, heightScale = 1.45f, widthScale = 0.25f,
                head = GrassHeadShape.Spike, headSize = 0.035f,
                palette = new[] { new Color(0.62f, 0.32f, 0.78f), new Color(0.75f, 0.38f, 0.7f) },
                centre = new Color(0.5f, 0.25f, 0.6f) }
        };
        Sanitize();
        MarkDirty();
    }

    private void Reset() => ResetToMeadow();

    private static float Finite(float value, float fallback) =>
        float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;
}
