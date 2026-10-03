using System;
using UnityEngine;

/// <summary>
/// Persistent, linear grass coverage. Zero means no grass; 255 means full authored density.
/// The serialized bytes are the source of truth so editor Undo also restores GPU coverage.
/// </summary>
[CreateAssetMenu(menuName = "Cone/Grass/Density Map", fileName = "GrassDensity")]
public sealed class GrassDensityAsset : ScriptableObject
{
    public const int MinimumResolution = 16;
    public const int MaximumResolution = 4096;

    [SerializeField, HideInInspector] private int width = 512;
    [SerializeField, HideInInspector] private int height = 512;
    [SerializeField, HideInInspector] private byte[] samples;

    [NonSerialized] private Texture2D texture;
    [NonSerialized] private bool textureDirty = true;
    [NonSerialized] private bool coverageDirty = true;
    [NonSerialized] private int coveredSampleCount;
    [NonSerialized] private byte[] occupiedBlocks;
    private const int CoverageBlockSize = 16;
    [NonSerialized] private uint revision = 1;

    public event Action Changed;

    public int Width => width;
    public int Height => height;
    public uint Revision => revision;

    public bool HasCoverage
    {
        get
        {
            EnsureStorage();
            RefreshCoverageCount();
            return coveredSampleCount != 0;
        }
    }

    public Texture2D Texture
    {
        get
        {
            EnsureStorage();
            if (!texture || texture.width != width || texture.height != height)
            {
                ReleaseTexture();
                texture = new Texture2D(width, height, TextureFormat.R8, false, true)
                {
                    name = name + " (Density)",
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
                textureDirty = true;
            }

            if (textureDirty)
            {
                texture.LoadRawTextureData(samples);
                texture.Apply(false, false);
                textureDirty = false;
            }

            return texture;
        }
    }

    /// <summary>Resize the map. New maps and newly cleared regions contain no grass.</summary>
    public void Resize(int newWidth, int newHeight, bool preserveCoverage = true)
    {
        EnsureStorage();
        newWidth = Mathf.Clamp(newWidth, MinimumResolution, MaximumResolution);
        newHeight = Mathf.Clamp(newHeight, MinimumResolution, MaximumResolution);
        if (newWidth == width && newHeight == height)
            return;

        byte[] previous = samples;
        int previousWidth = width;
        int previousHeight = height;
        samples = new byte[newWidth * newHeight];
        width = newWidth;
        height = newHeight;
        if (preserveCoverage)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float value = SampleBilinear(previous, previousWidth, previousHeight,
                        new Vector2((x + 0.5f) / width, (y + 0.5f) / height));
                    samples[y * width + x] = (byte)Mathf.RoundToInt(value * 255f);
                }
            }
        }

        NotifyChanged();
    }

    public void Fill(float density)
    {
        EnsureStorage();
        if (!IsFinite(density))
            density = 0f;
        byte value = (byte)Mathf.RoundToInt(Mathf.Clamp01(density) * 255f);
        for (int i = 0; i < samples.Length; i++)
            samples[i] = value;
        NotifyChanged();
    }

    /// <summary>
    /// Apply a round world-space brush represented as an ellipse in map UVs.
    /// Hardness is the radius fraction with full brush strength. Callers record Undo first.
    /// </summary>
    public bool Paint(Vector2 centerUV, Vector2 radiusUV, float strength, float hardness, bool erase)
    {
        if (!IsFinite(centerUV.x) || !IsFinite(centerUV.y) ||
            !IsFinite(radiusUV.x) || !IsFinite(radiusUV.y) ||
            !IsFinite(strength) || !IsFinite(hardness) ||
            radiusUV.x <= 0f || radiusUV.y <= 0f || strength <= 0f)
            return false;

        EnsureStorage();
        strength = Mathf.Clamp01(strength);
        hardness = Mathf.Clamp01(hardness);
        int xMin = Mathf.Clamp(Mathf.FloorToInt((centerUV.x - radiusUV.x) * width), 0, width - 1);
        int xMax = Mathf.Clamp(Mathf.CeilToInt((centerUV.x + radiusUV.x) * width), 0, width - 1);
        int yMin = Mathf.Clamp(Mathf.FloorToInt((centerUV.y - radiusUV.y) * height), 0, height - 1);
        int yMax = Mathf.Clamp(Mathf.CeilToInt((centerUV.y + radiusUV.y) * height), 0, height - 1);
        bool changed = false;

        for (int y = yMin; y <= yMax; y++)
        {
            float dy = ((y + 0.5f) / height - centerUV.y) / radiusUV.y;
            for (int x = xMin; x <= xMax; x++)
            {
                float dx = ((x + 0.5f) / width - centerUV.x) / radiusUV.x;
                float squaredDistance = dx * dx + dy * dy;
                if (squaredDistance > 1f)
                    continue;

                float distance = Mathf.Sqrt(squaredDistance);
                float falloff = hardness >= 1f ? 1f :
                    1f - Mathf.SmoothStep(0f, 1f, (distance - hardness) / (1f - hardness));
                int index = y * width + x;
                float previous = samples[index] / 255f;
                float next = erase ? previous - strength * falloff : previous + strength * falloff;
                byte quantized = (byte)Mathf.RoundToInt(Mathf.Clamp01(next) * 255f);
                if (samples[index] == quantized)
                    continue;
                samples[index] = quantized;
                changed = true;
            }
        }

        if (changed)
            NotifyChanged();
        return changed;
    }

    public float Sample(Vector2 uv)
    {
        if (!IsFinite(uv.x) || !IsFinite(uv.y) || uv.x < 0f || uv.x > 1f || uv.y < 0f || uv.y > 1f)
            return 0f;
        EnsureStorage();
        return SampleBilinear(samples, width, height, uv);
    }

    /// <summary>Conservative occupancy query including the bilinear filter footprint.</summary>
    public bool HasCoverageIn(Rect uvBounds)
    {
        if (!IsFinite(uvBounds.xMin) || !IsFinite(uvBounds.xMax) ||
            !IsFinite(uvBounds.yMin) || !IsFinite(uvBounds.yMax) ||
            uvBounds.xMax < 0f || uvBounds.yMax < 0f || uvBounds.xMin > 1f || uvBounds.yMin > 1f || !HasCoverage)
            return false;

        int xMin = Mathf.Clamp(Mathf.FloorToInt(uvBounds.xMin * width - 0.5f), 0, width - 1);
        int xMax = Mathf.Clamp(Mathf.CeilToInt(uvBounds.xMax * width - 0.5f), 0, width - 1);
        int yMin = Mathf.Clamp(Mathf.FloorToInt(uvBounds.yMin * height - 0.5f), 0, height - 1);
        int yMax = Mathf.Clamp(Mathf.CeilToInt(uvBounds.yMax * height - 0.5f), 0, height - 1);
        int blockWidth = (width + CoverageBlockSize - 1) / CoverageBlockSize;
        for (int y = yMin / CoverageBlockSize; y <= yMax / CoverageBlockSize; y++)
            for (int x = xMin / CoverageBlockSize; x <= xMax / CoverageBlockSize; x++)
                if (occupiedBlocks[y * blockWidth + x] != 0)
                    return true;
        return false;
    }

    /// <summary>Call after a serialized edit or Undo to invalidate all dependent captures.</summary>
    public void NotifyChanged()
    {
        textureDirty = true;
        coverageDirty = true;
        unchecked { revision++; }
        Changed?.Invoke();
    }

    private void OnEnable() => NotifyChanged();
    private void OnValidate() => NotifyChanged();
    private void OnDisable() => ReleaseTexture();

    private void EnsureStorage()
    {
        width = Mathf.Clamp(width, MinimumResolution, MaximumResolution);
        height = Mathf.Clamp(height, MinimumResolution, MaximumResolution);
        if (samples != null && samples.Length == width * height)
            return;
        samples = new byte[width * height];
        textureDirty = true;
        coverageDirty = true;
    }

    private void RefreshCoverageCount()
    {
        if (!coverageDirty)
            return;
        coveredSampleCount = 0;
        int blockWidth = (width + CoverageBlockSize - 1) / CoverageBlockSize;
        int blockHeight = (height + CoverageBlockSize - 1) / CoverageBlockSize;
        if (occupiedBlocks == null || occupiedBlocks.Length != blockWidth * blockHeight)
            occupiedBlocks = new byte[blockWidth * blockHeight];
        else
            Array.Clear(occupiedBlocks, 0, occupiedBlocks.Length);
        for (int y = 0; y < height; y++)
        {
            int blockRow = y / CoverageBlockSize * blockWidth;
            for (int x = 0; x < width; x++)
            {
                if (samples[y * width + x] == 0)
                    continue;
                coveredSampleCount++;
                occupiedBlocks[blockRow + x / CoverageBlockSize] = 1;
            }
        }
        coverageDirty = false;
    }

    private void ReleaseTexture()
    {
        if (!texture)
            return;
        if (Application.isPlaying)
            Destroy(texture);
        else
            DestroyImmediate(texture);
        texture = null;
    }

    private static float SampleBilinear(byte[] values, int sampleWidth, int sampleHeight, Vector2 uv)
    {
        float x = Mathf.Clamp(uv.x * sampleWidth - 0.5f, 0f, sampleWidth - 1);
        float y = Mathf.Clamp(uv.y * sampleHeight - 0.5f, 0f, sampleHeight - 1);
        int x0 = Mathf.FloorToInt(x);
        int y0 = Mathf.FloorToInt(y);
        int x1 = Mathf.Min(x0 + 1, sampleWidth - 1);
        int y1 = Mathf.Min(y0 + 1, sampleHeight - 1);
        float lower = Mathf.Lerp(values[y0 * sampleWidth + x0], values[y0 * sampleWidth + x1], x - x0);
        float upper = Mathf.Lerp(values[y1 * sampleWidth + x0], values[y1 * sampleWidth + x1], x - x0);
        return Mathf.Lerp(lower, upper, y - y0) / 255f;
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
