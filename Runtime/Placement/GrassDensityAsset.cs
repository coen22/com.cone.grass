using System;
using UnityEngine;
using UnityEngine.Rendering;

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
    [NonSerialized] private Texture2D uploadTexture;
    [NonSerialized] private byte[] uploadSamples;
    [NonSerialized] private bool textureDirty = true;
    [NonSerialized] private bool fullTextureUpload = true;
    [NonSerialized] private bool regionalCopyUnavailable;
    [NonSerialized] private RectInt dirtyPixels;
    [NonSerialized] private bool coverageDirty = true;
    [NonSerialized] private bool coverageBoundsDirty;
    [NonSerialized] private int coveredSampleCount;
    [NonSerialized] private ushort[] occupiedBlocks;
    [NonSerialized] private RectInt occupiedPixels;
    private const int CoverageBlockSize = 16;
    [NonSerialized] private uint revision = 1;

    public event Action Changed;
    /// <summary>Changed pixels in map coordinates. Undo and resizing report the complete map.</summary>
    public event Action<RectInt> RegionChanged;

    public int Width => width;
    public int Height => height;
    public uint Revision => revision;
    /// <summary>Texels uploaded by the last texture refresh, including staging texture padding.</summary>
    public int LastUploadedTexelCount { get; private set; }
    public uint TextureUploadCount { get; private set; }
    public bool LastUploadWasPartial { get; private set; }

    public RectInt PendingUploadRegion => !textureDirty ? default : fullTextureUpload
        ? new RectInt(0, 0, width, height) : dirtyPixels;

    public bool HasCoverage
    {
        get
        {
            EnsureStorage();
            RefreshCoverageState();
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
                fullTextureUpload = true;
            }

            if (textureDirty)
                UploadChanges();

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
        bool changed = false;
        for (int i = 0; i < samples.Length; i++)
        {
            changed |= samples[i] != value;
            samples[i] = value;
        }
        if (!changed)
            return;

        EnsureCoverageBlocks();
        int blockWidth = (width + CoverageBlockSize - 1) / CoverageBlockSize;
        int blockHeight = (height + CoverageBlockSize - 1) / CoverageBlockSize;
        for (int y = 0; y < blockHeight; y++)
            for (int x = 0; x < blockWidth; x++)
                occupiedBlocks[y * blockWidth + x] = value == 0 ? (ushort)0 : (ushort)(
                    Mathf.Min(CoverageBlockSize, width - x * CoverageBlockSize) *
                    Mathf.Min(CoverageBlockSize, height - y * CoverageBlockSize));
        coveredSampleCount = value == 0 ? 0 : samples.Length;
        occupiedPixels = value == 0 ? default : new RectInt(0, 0, width, height);
        coverageDirty = coverageBoundsDirty = false;
        RecordChanges(new RectInt(0, 0, width, height));
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
        if (centerUV.x + radiusUV.x < 0f || centerUV.y + radiusUV.y < 0f ||
            centerUV.x - radiusUV.x > 1f || centerUV.y - radiusUV.y > 1f)
            return false;
        RefreshCoverageState();
        strength = Mathf.Clamp01(strength);
        hardness = Mathf.Clamp01(hardness);
        int xMin = Mathf.Clamp(Mathf.FloorToInt(Mathf.Clamp01(centerUV.x - radiusUV.x) * width), 0, width - 1);
        int xMax = Mathf.Clamp(Mathf.CeilToInt(Mathf.Clamp01(centerUV.x + radiusUV.x) * width), 0, width - 1);
        int yMin = Mathf.Clamp(Mathf.FloorToInt(Mathf.Clamp01(centerUV.y - radiusUV.y) * height), 0, height - 1);
        int yMax = Mathf.Clamp(Mathf.CeilToInt(Mathf.Clamp01(centerUV.y + radiusUV.y) * height), 0, height - 1);
        int changedXMin = width, changedYMin = height, changedXMax = -1, changedYMax = -1;
        int blockWidth = (width + CoverageBlockSize - 1) / CoverageBlockSize;

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
                byte previousValue = samples[index];
                float previous = previousValue / 255f;
                float next = erase ? previous - strength * falloff : previous + strength * falloff;
                byte quantized = (byte)Mathf.RoundToInt(Mathf.Clamp01(next) * 255f);
                if (previousValue == quantized)
                    continue;
                samples[index] = quantized;
                if (previousValue == 0 || quantized == 0)
                {
                    int block = y / CoverageBlockSize * blockWidth + x / CoverageBlockSize;
                    if (previousValue == 0)
                    {
                        occupiedBlocks[block]++;
                        coveredSampleCount++;
                        occupiedPixels = coveredSampleCount == 1 ? new RectInt(x, y, 1, 1) :
                            Union(occupiedPixels, new RectInt(x, y, 1, 1));
                    }
                    else
                    {
                        occupiedBlocks[block]--;
                        coveredSampleCount--;
                        if (x == occupiedPixels.xMin || x == occupiedPixels.xMax - 1 ||
                            y == occupiedPixels.yMin || y == occupiedPixels.yMax - 1)
                            coverageBoundsDirty = true;
                    }
                }
                changedXMin = Mathf.Min(changedXMin, x);
                changedYMin = Mathf.Min(changedYMin, y);
                changedXMax = Mathf.Max(changedXMax, x);
                changedYMax = Mathf.Max(changedYMax, y);
            }
        }

        if (changedXMax < changedXMin)
            return false;
        if (coveredSampleCount == 0)
        {
            occupiedPixels = default;
            coverageBoundsDirty = false;
        }
        RecordChanges(new RectInt(changedXMin, changedYMin,
            changedXMax - changedXMin + 1, changedYMax - changedYMin + 1));
        return true;
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
            uvBounds.xMax < uvBounds.xMin || uvBounds.yMax < uvBounds.yMin ||
            uvBounds.xMax < 0f || uvBounds.yMax < 0f || uvBounds.xMin > 1f || uvBounds.yMin > 1f || !HasCoverage)
            return false;

        int xMin = Mathf.Clamp(Mathf.FloorToInt(Mathf.Clamp01(uvBounds.xMin) * width - 0.5f), 0, width - 1);
        int xMax = Mathf.Clamp(Mathf.CeilToInt(Mathf.Clamp01(uvBounds.xMax) * width - 0.5f), 0, width - 1);
        int yMin = Mathf.Clamp(Mathf.FloorToInt(Mathf.Clamp01(uvBounds.yMin) * height - 0.5f), 0, height - 1);
        int yMax = Mathf.Clamp(Mathf.CeilToInt(Mathf.Clamp01(uvBounds.yMax) * height - 0.5f), 0, height - 1);
        int blockWidth = (width + CoverageBlockSize - 1) / CoverageBlockSize;
        for (int blockY = yMin / CoverageBlockSize; blockY <= yMax / CoverageBlockSize; blockY++)
        {
            for (int blockX = xMin / CoverageBlockSize; blockX <= xMax / CoverageBlockSize; blockX++)
            {
                if (occupiedBlocks[blockY * blockWidth + blockX] == 0)
                    continue;
                int left = Mathf.Max(xMin, blockX * CoverageBlockSize);
                int right = Mathf.Min(xMax, (blockX + 1) * CoverageBlockSize - 1);
                int bottom = Mathf.Max(yMin, blockY * CoverageBlockSize);
                int top = Mathf.Min(yMax, (blockY + 1) * CoverageBlockSize - 1);
                if (left == blockX * CoverageBlockSize && right == Mathf.Min(width - 1, (blockX + 1) * CoverageBlockSize - 1) &&
                    bottom == blockY * CoverageBlockSize && top == Mathf.Min(height - 1, (blockY + 1) * CoverageBlockSize - 1))
                    return true;
                for (int y = bottom; y <= top; y++)
                    for (int x = left; x <= right; x++)
                        if (samples[y * width + x] != 0)
                            return true;
            }
        }
        return false;
    }

    /// <summary>Tight occupied UV bounds, expanded for bilinear sampling and clipped to this map.</summary>
    public bool TryGetCoverageBounds(out Rect uvBounds)
    {
        uvBounds = default;
        if (!HasCoverage)
            return false;
        RefreshCoverageBounds();
        uvBounds = Rect.MinMaxRect(Mathf.Max(0f, (occupiedPixels.xMin - 0.5f) / width),
            Mathf.Max(0f, (occupiedPixels.yMin - 0.5f) / height),
            Mathf.Min(1f, (occupiedPixels.xMax + 0.5f) / width),
            Mathf.Min(1f, (occupiedPixels.yMax + 0.5f) / height));
        return true;
    }

    /// <summary>Call after a serialized edit or Undo to invalidate all dependent captures.</summary>
    public void NotifyChanged()
    {
        textureDirty = true;
        fullTextureUpload = true;
        coverageDirty = true;
        unchecked { revision++; }
        Changed?.Invoke();
        RegionChanged?.Invoke(new RectInt(0, 0, width, height));
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
        fullTextureUpload = true;
        coverageDirty = true;
    }

    private void RecordChanges(RectInt region)
    {
        dirtyPixels = textureDirty ? Union(dirtyPixels, region) : region;
        textureDirty = true;
        unchecked { revision++; }
        Changed?.Invoke();
        RegionChanged?.Invoke(region);
    }

    private void EnsureCoverageBlocks()
    {
        int blockCount = ((width + CoverageBlockSize - 1) / CoverageBlockSize) *
            ((height + CoverageBlockSize - 1) / CoverageBlockSize);
        if (occupiedBlocks == null || occupiedBlocks.Length != blockCount)
            occupiedBlocks = new ushort[blockCount];
    }

    private void RefreshCoverageState()
    {
        if (!coverageDirty)
            return;
        coveredSampleCount = 0;
        occupiedPixels = default;
        int blockWidth = (width + CoverageBlockSize - 1) / CoverageBlockSize;
        EnsureCoverageBlocks();
        Array.Clear(occupiedBlocks, 0, occupiedBlocks.Length);
        int xMin = width, yMin = height, xMax = -1, yMax = -1;
        for (int y = 0; y < height; y++)
        {
            int blockRow = y / CoverageBlockSize * blockWidth;
            for (int x = 0; x < width; x++)
            {
                if (samples[y * width + x] == 0)
                    continue;
                coveredSampleCount++;
                occupiedBlocks[blockRow + x / CoverageBlockSize]++;
                xMin = Mathf.Min(xMin, x);
                yMin = Mathf.Min(yMin, y);
                xMax = Mathf.Max(xMax, x);
                yMax = Mathf.Max(yMax, y);
            }
        }
        if (coveredSampleCount != 0)
            occupiedPixels = new RectInt(xMin, yMin, xMax - xMin + 1, yMax - yMin + 1);
        coverageDirty = false;
        coverageBoundsDirty = false;
    }

    private void RefreshCoverageBounds()
    {
        if (!coverageBoundsDirty)
            return;
        int blockWidth = (width + CoverageBlockSize - 1) / CoverageBlockSize;
        int blockHeight = (height + CoverageBlockSize - 1) / CoverageBlockSize;
        int minBlockX = blockWidth, minBlockY = blockHeight, maxBlockX = -1, maxBlockY = -1;
        for (int y = 0; y < blockHeight; y++)
            for (int x = 0; x < blockWidth; x++)
                if (occupiedBlocks[y * blockWidth + x] != 0)
                {
                    minBlockX = Mathf.Min(minBlockX, x);
                    minBlockY = Mathf.Min(minBlockY, y);
                    maxBlockX = Mathf.Max(maxBlockX, x);
                    maxBlockY = Mathf.Max(maxBlockY, y);
                }

        // Only boundary block strips need a texel scan when an erase shrinks the occupied extent.
        int left = FindOccupiedColumn(minBlockX, false);
        int right = FindOccupiedColumn(maxBlockX, true);
        int bottom = FindOccupiedRow(minBlockY, false);
        int top = FindOccupiedRow(maxBlockY, true);
        occupiedPixels = new RectInt(left, bottom, right - left + 1, top - bottom + 1);
        coverageBoundsDirty = false;
    }

    private int FindOccupiedColumn(int blockX, bool reverse)
    {
        int blockWidth = (width + CoverageBlockSize - 1) / CoverageBlockSize;
        int start = blockX * CoverageBlockSize;
        int end = Mathf.Min(start + CoverageBlockSize, width) - 1;
        for (int step = 0; step <= end - start; step++)
        {
            int x = reverse ? end - step : start + step;
            for (int blockY = 0; blockY < (height + CoverageBlockSize - 1) / CoverageBlockSize; blockY++)
            {
                if (occupiedBlocks[blockY * blockWidth + blockX] == 0)
                    continue;
                int top = Mathf.Min((blockY + 1) * CoverageBlockSize, height);
                for (int y = blockY * CoverageBlockSize; y < top; y++)
                    if (samples[y * width + x] != 0)
                        return x;
            }
        }
        return start;
    }

    private int FindOccupiedRow(int blockY, bool reverse)
    {
        int blockWidth = (width + CoverageBlockSize - 1) / CoverageBlockSize;
        int start = blockY * CoverageBlockSize;
        int end = Mathf.Min(start + CoverageBlockSize, height) - 1;
        for (int step = 0; step <= end - start; step++)
        {
            int y = reverse ? end - step : start + step;
            for (int blockX = 0; blockX < blockWidth; blockX++)
            {
                if (occupiedBlocks[blockY * blockWidth + blockX] == 0)
                    continue;
                int right = Mathf.Min((blockX + 1) * CoverageBlockSize, width);
                for (int x = blockX * CoverageBlockSize; x < right; x++)
                    if (samples[y * width + x] != 0)
                        return y;
            }
        }
        return start;
    }

    private void UploadChanges()
    {
        int stagingWidth = Mathf.Min(width, Mathf.Max(16, Mathf.NextPowerOfTwo(dirtyPixels.width)));
        int stagingHeight = Mathf.Min(height, Mathf.Max(16, Mathf.NextPowerOfTwo(dirtyPixels.height)));
        bool partial = !fullTextureUpload && dirtyPixels.width > 0 && dirtyPixels.height > 0 &&
            !regionalCopyUnavailable && stagingWidth * stagingHeight < samples.Length / 2 &&
            (SystemInfo.copyTextureSupport & CopyTextureSupport.Basic) != 0;
        LastUploadWasPartial = false;
        LastUploadedTexelCount = 0;
        if (partial)
        {
            if (!uploadTexture || uploadTexture.width != stagingWidth || uploadTexture.height != stagingHeight)
            {
                Release(uploadTexture);
                uploadTexture = new Texture2D(stagingWidth, stagingHeight, TextureFormat.R8, false, true)
                {
                    name = name + " (Density upload)",
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp
                };
                uploadSamples = new byte[stagingWidth * stagingHeight];
            }
            for (int y = 0; y < dirtyPixels.height; y++)
                Array.Copy(samples, (dirtyPixels.y + y) * width + dirtyPixels.x,
                    uploadSamples, y * stagingWidth, dirtyPixels.width);
            uploadTexture.LoadRawTextureData(uploadSamples);
            uploadTexture.Apply(false, false);
            LastUploadedTexelCount = stagingWidth * stagingHeight;
            try
            {
                // Both R8 textures stay readable: CopyTexture updates their CPU copies as well.
                // Do not Apply the destination after this copy; it would upload the complete map.
                Graphics.CopyTexture(uploadTexture, 0, 0, 0, 0, dirtyPixels.width, dirtyPixels.height,
                    texture, 0, 0, dirtyPixels.x, dirtyPixels.y);
                LastUploadWasPartial = true;
            }
            catch (UnityException)
            {
                // Some backends expose CopyTexture but cannot copy this format regionally.
                // Reloading authoritative bytes also repairs the CPU copy after a failed copy.
                partial = false;
                regionalCopyUnavailable = true;
            }
        }
        if (!partial)
        {
            texture.LoadRawTextureData(samples);
            texture.Apply(false, false);
            LastUploadedTexelCount += samples.Length;
        }
        unchecked { TextureUploadCount++; }
        textureDirty = fullTextureUpload = false;
        dirtyPixels = default;
    }

    private void ReleaseTexture()
    {
        Release(texture);
        Release(uploadTexture);
        texture = null;
        uploadTexture = null;
        uploadSamples = null;
        textureDirty = fullTextureUpload = true;
    }

    private static void Release(Texture2D value)
    {
        if (!value)
            return;
        if (Application.isPlaying)
            Destroy(value);
        else
            DestroyImmediate(value);
    }

    private static RectInt Union(RectInt left, RectInt right)
    {
        if (left.width <= 0 || left.height <= 0)
            return right;
        if (right.width <= 0 || right.height <= 0)
            return left;
        int x = Mathf.Min(left.xMin, right.xMin), y = Mathf.Min(left.yMin, right.yMin);
        return new RectInt(x, y, Mathf.Max(left.xMax, right.xMax) - x, Mathf.Max(left.yMax, right.yMax) - y);
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
