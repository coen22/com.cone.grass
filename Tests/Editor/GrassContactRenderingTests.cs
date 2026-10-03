using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

/// <summary>
/// Renders the shipped contact shader against known depth planes. These tests
/// exercise its actual receiver/caster classification, ray search and blending;
/// camera depth production and RenderGraph scheduling require separate coverage.
/// </summary>
[Category("GrassGPU")]
[NonParallelizable]
public sealed class GrassContactRenderingTests
{
    private const int Size = 64;
    private static readonly Color InitialColor = new Color(0.8f, 0.6f, 0.4f, 0.37f);
    private const float ColorTolerance = 0.006f;
    private Material material;

    public enum ContactLayout
    {
        SceneToGrass,
        GrassToScene,
        SceneOnly,
        HiddenGrass,
        ZeroCoverage,
        GrassOnly,
        GrassToGrass,
        SceneBehindGrass
    }

    [SetUp]
    public void SetUp()
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ||
            !SystemInfo.SupportsTextureFormat(TextureFormat.RGBAFloat) ||
            !SystemInfo.IsFormatSupported(GraphicsFormat.R8G8B8A8_UNorm, GraphicsFormatUsage.Render) ||
            !SystemInfo.IsFormatSupported(GraphicsFormat.R8G8B8A8_UNorm, GraphicsFormatUsage.Blend))
            Assert.Ignore("Contact rendering tests require a graphics device, floating-point depth samples, and color blending.");

        Shader shader = Resources.Load<Shader>("InfiniteGrassContactShadows");
        Assert.That(shader, Is.Not.Null, "The installed contact resource shader must import successfully.");
        Assert.That(shader.isSupported, Is.True, "The contact shader must support the active graphics API.");
        material = new Material(shader);
        Assert.That(material.FindPass("GrassContactShadows"), Is.EqualTo(0));
        ShaderUtil.CompilePass(material, 0, true);
        Assert.That(ShaderUtil.IsPassCompiled(material, 0), Is.True);
    }

    [TearDown]
    public void TearDown()
    {
        if (material) Object.DestroyImmediate(material);
    }

    [TestCase(false, ContactLayout.SceneToGrass)]
    [TestCase(false, ContactLayout.GrassToScene)]
    [TestCase(true, ContactLayout.SceneToGrass)]
    [TestCase(true, ContactLayout.GrassToScene)]
    [TestCase(false, ContactLayout.GrassToGrass)]
    [TestCase(true, ContactLayout.GrassToGrass)]
    public void VisibleSurfacesCastContactsWhenGrassParticipates(bool orthographic, ContactLayout layout)
    {
        Color[] pixels = Render(orthographic, layout);
        // The ray starts left of a depth discontinuity and travels toward it.
        // Contact requires the other surface, so a blank/error shader cannot pass.
        Color receiver = pixels[Size / 2 * Size + 26];
        Assert.That(receiver.r, Is.LessThan(InitialColor.r - 0.15f));
        Assert.That(receiver.g / InitialColor.g,
            Is.EqualTo(receiver.r / InitialColor.r).Within(ColorTolerance * 2f));
        Assert.That(receiver.b / InitialColor.b,
            Is.EqualTo(receiver.r / InitialColor.r).Within(ColorTolerance * 2f));
        AssertAlphaUnchanged(pixels);
    }

    [TestCase(false, ContactLayout.SceneOnly)]
    [TestCase(false, ContactLayout.HiddenGrass)]
    [TestCase(false, ContactLayout.ZeroCoverage)]
    [TestCase(true, ContactLayout.SceneOnly)]
    [TestCase(true, ContactLayout.HiddenGrass)]
    [TestCase(true, ContactLayout.ZeroCoverage)]
    public void UnrelatedSceneSurfacesRemainUnchanged(bool orthographic, ContactLayout layout)
    {
        // The scene has the same depth discontinuity as the positive controls.
        // Grass is absent, behind opaque geometry, or has no coverage.
        Color[] pixels = Render(orthographic, layout);
        for (int index = 0; index < pixels.Length; index++)
            AssertColor(pixels[index], InitialColor, "Scene-only pixel " + index);
    }

    [TestCase(false, ContactLayout.SceneToGrass)]
    [TestCase(false, ContactLayout.GrassToScene)]
    [TestCase(true, ContactLayout.SceneToGrass)]
    [TestCase(true, ContactLayout.GrassToScene)]
    [TestCase(false, ContactLayout.GrassToGrass)]
    [TestCase(true, ContactLayout.GrassToGrass)]
    public void DifferentDepthAllocationScalesPreserveContactLocations(bool orthographic, ContactLayout layout)
    {
        Color[] reference = Render(orthographic, layout);
        Color[] scaled = Render(orthographic, layout, sceneScale: 0.5f, grassScale: 0.75f);
        // Both depth textures contain the same normalized viewport geometry.
        // Their unused allocation texels must never alter ray reconstruction.
        for (int index = 0; index < reference.Length; index++)
            AssertColor(scaled[index], reference[index], "Scaled contact pixel " + index);
    }

    [TestCase(ContactLayout.SceneToGrass)]
    [TestCase(ContactLayout.GrassToScene)]
    public void FractionalGrassCoverageSoftensContactWithoutChangingAlpha(ContactLayout layout)
    {
        Color solid = Render(false, layout)[Size / 2 * Size + 26];
        Color fractional = Render(false, layout, grassCoverage: 0.25f)[Size / 2 * Size + 26];
        Assert.That(solid.r, Is.LessThan(InitialColor.r - 0.15f));
        Assert.That(fractional.r, Is.GreaterThan(solid.r + 0.1f));
        Assert.That(fractional.r, Is.LessThan(InitialColor.r - 0.03f));
        Assert.That(InitialColor.r - fractional.r,
            Is.EqualTo((InitialColor.r - solid.r) * 0.25f).Within(ColorTolerance));
        Assert.That(fractional.a, Is.EqualTo(InitialColor.a).Within(ColorTolerance));
    }

    [TestCase(ContactLayout.SceneToGrass)]
    [TestCase(ContactLayout.GrassToScene)]
    public void OutOfRangeReceiversRemainUnchanged(ContactLayout layout)
    {
        Color[] pixels = Render(false, layout, maximumDistance: 1f);
        for (int index = 0; index < pixels.Length; index++)
            AssertColor(pixels[index], InitialColor, "Out-of-range pixel " + index);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ReceiverDepthTexelCannotCastAContactOnItself(bool orthographic)
    {
        Color[] pixels = Render(orthographic, ContactLayout.GrassOnly, lightDirection: Vector3.back);
        AssertColor(pixels[Size / 2 * Size + Size / 2], InitialColor, "Origin grass texel");
        if (orthographic)
            for (int index = 0; index < pixels.Length; index++)
                AssertColor(pixels[index], InitialColor, "Parallel origin ray " + index);
        AssertAlphaUnchanged(pixels);
    }

    [TestCase(false, 1f)]
    [TestCase(true, 1f)]
    [TestCase(true, 0.25f)]
    public void SceneBehindTheOriginGrassTexelRemainsAnAvailableCaster(bool orthographic, float coverage)
    {
        Color[] pixels = Render(orthographic, ContactLayout.SceneBehindGrass,
            grassCoverage: coverage, lightDirection: Vector3.back);
        Color receiver = pixels[Size / 2 * Size + Size / 2];
        // A known self grass sample must not hide the separately stored scene
        // layer at eye depth 5.08. Only the receiver contributes grass coverage.
        float attenuation = 1f - 0.6f * coverage;
        AssertColor(receiver, new Color(InitialColor.r * attenuation, InitialColor.g * attenuation,
            InitialColor.b * attenuation, InitialColor.a), "Scene behind grass");
        AssertAlphaUnchanged(pixels);
    }

    [Test]
    public void SceneBehindTheOriginGrassTexelMustStillBeWithinRayReach()
    {
        Color[] pixels = Render(true, ContactLayout.SceneBehindGrass,
            lightDirection: Vector3.back, rayLength: 0.03f);
        for (int index = 0; index < pixels.Length; index++)
            AssertColor(pixels[index], InitialColor, "Scene beyond short origin ray " + index);
    }

    [TestCase(0.5f, 0.25f, 26)]
    [TestCase(0.75f, 0.125f, 25)]
    public void AngledOriginRayUsesTheScaledAndShiftedGrassTexel(float scale, float offset, int pixelX)
    {
        var direction = new Vector3(0.8660254f, 0f, -0.5f);
        float receiverUV = (pixelX + 0.5f) / Size;
        float lastTravel = 0.03f + 7.5f * 0.09f / 8f;
        float lastUV = receiverUV + direction.x * lastTravel / 6f;
        Assert.That(Mathf.FloorToInt(lastUV * Size), Is.GreaterThan(pixelX),
            "The ray must leave the unscaled screen pixel to exercise allocation-space identity.");
        Assert.That(Mathf.FloorToInt((lastUV * scale + offset) * Size),
            Is.EqualTo(Mathf.FloorToInt((receiverUV * scale + offset) * Size)));

        Color[] pixels = Render(true, ContactLayout.GrassOnly, grassScale: scale,
            grassOffset: Vector2.one * offset, lightDirection: direction, rayLength: 0.09f);
        AssertColor(pixels[Size / 2 * Size + pixelX], InitialColor, "Angled ray inside one grass texel");
        AssertAlphaUnchanged(pixels);
    }

    [TestCase(0.5f, 0.25f)]
    [TestCase(0.75f, 0.125f)]
    public void ShiftedGrassAllocationPreservesDistinctCasterContacts(float scale, float offset)
    {
        Color[] reference = Render(true, ContactLayout.GrassToGrass);
        Color[] shifted = Render(true, ContactLayout.GrassToGrass, grassScale: scale,
            grassOffset: Vector2.one * offset);
        Assert.That(reference[Size / 2 * Size + 26].r, Is.LessThan(InitialColor.r - 0.15f));
        for (int index = 0; index < reference.Length; index++)
            AssertColor(shifted[index], reference[index], "Shifted grass contact pixel " + index);
    }

    private Color[] Render(bool orthographic, ContactLayout layout, float grassCoverage = 1f,
        float maximumDistance = 50f, float sceneScale = 1f, float grassScale = 1f,
        Vector2 grassOffset = default, Vector3? lightDirection = null, float rayLength = 0.75f)
    {
        Matrix4x4 projection = GL.GetGPUProjectionMatrix(orthographic
            ? Matrix4x4.Ortho(-3f, 3f, -3f, 3f, 0.1f, 100f)
            : Matrix4x4.Perspective(60f, 1f, 0.1f, 100f), true);
        Assert.That(GrassContactShadows.TryGetProjectionMatrices(projection, Matrix4x4.identity,
            out Matrix4x4 viewProjection, out Matrix4x4 inverseViewProjection,
            out Matrix4x4 inverseProjection), Is.True);
        var settings = new GrassContactShadows.Settings
        {
            enabled = true, strength = 0.6f, rayLength = rayLength, bias = 0.03f,
            thickness = 0.15f, steps = 8, maxDistance = maximumDistance
        };
        Assert.That(GrassContactShadows.TryGetParameters(settings, out Vector4 parameters,
            out Vector4 limits, out _), Is.True);

        Texture2D scene = null;
        Texture2D grass = null;
        Texture2D readback = null;
        RenderTexture target = null;
        RenderTexture previousTarget = RenderTexture.active;
        var commands = new CommandBuffer { name = "Grass Contact Shader Regression" };
        try
        {
            scene = CreateDepthTexture(projection, layout, false, sceneScale, grassCoverage);
            grass = CreateDepthTexture(projection, layout, true, grassScale, grassCoverage, grassOffset);
            target = new RenderTexture(new RenderTextureDescriptor(Size, Size)
            {
                graphicsFormat = GraphicsFormat.R8G8B8A8_UNorm,
                depthBufferBits = 0, msaaSamples = 1, sRGB = false
            });
            Assert.That(target.Create(), Is.True);
            readback = new Texture2D(Size, Size, TextureFormat.RGBA32, false, true);

            var properties = new MaterialPropertyBlock();
            properties.SetTexture("_GrassContactSceneDepth", scene);
            properties.SetTexture("_GrassContactGrassDepth", grass);
            properties.SetVector("_BlitScaleBias", new Vector4(1f, 1f, 0f, 0f));
            properties.SetVector("_GrassContactDepthScale",
                GrassContactShadows.GetUVScaleBias(Vector2.one * sceneScale, false));
            properties.SetVector("_GrassContactGrassDepthScale",
                GrassContactShadows.GetUVScaleBias(Vector2.one * grassScale, false) +
                new Vector4(0f, 0f, grassOffset.x, grassOffset.y));
            properties.SetVector("_GrassContactParameters", parameters);
            properties.SetVector("_GrassContactLimits", limits);
            properties.SetMatrix("_GrassContactViewProjection", viewProjection);
            properties.SetMatrix("_GrassContactInverseViewProjection", inverseViewProjection);
            properties.SetMatrix("_GrassContactInverseProjection", inverseProjection);
            properties.SetMatrix("_GrassContactViewMatrix", Matrix4x4.identity);

            // Restore the global camera/light state in the same command buffer.
            // The contact program consumes these URP globals, not a test shader.
            Vector4 cameraPosition = Shader.GetGlobalVector("_WorldSpaceCameraPos");
            Vector4 lightPosition = Shader.GetGlobalVector("_MainLightPosition");
            commands.SetGlobalVector("_WorldSpaceCameraPos", Vector4.zero);
            Vector3 direction = lightDirection ?? Vector3.right;
            commands.SetGlobalVector("_MainLightPosition", new Vector4(direction.x, direction.y, direction.z, 0f));
            commands.SetRenderTarget(target);
            commands.SetViewport(new Rect(0f, 0f, Size, Size));
            commands.ClearRenderTarget(false, true, InitialColor);
            commands.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3, 1, properties);
            commands.SetGlobalVector("_WorldSpaceCameraPos", cameraPosition);
            commands.SetGlobalVector("_MainLightPosition", lightPosition);
            Graphics.ExecuteCommandBuffer(commands);

            RenderTexture.active = target;
            readback.ReadPixels(new Rect(0f, 0f, Size, Size), 0, 0, false);
            readback.Apply(false, false);
            return readback.GetPixels();
        }
        finally
        {
            RenderTexture.active = previousTarget;
            commands.Release();
            if (target) Object.DestroyImmediate(target);
            if (readback) Object.DestroyImmediate(readback);
            if (scene) Object.DestroyImmediate(scene);
            if (grass) Object.DestroyImmediate(grass);
        }
    }

    private static Texture2D CreateDepthTexture(Matrix4x4 projection, ContactLayout layout,
        bool grass, float scale, float coverage, Vector2 offset = default)
    {
        var texture = new Texture2D(Size, Size, TextureFormat.RGBAFloat, false, true)
        {
            filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp
        };
        int viewportSize = Mathf.RoundToInt(Size * scale);
        int viewportX = Mathf.RoundToInt(Size * offset.x);
        int viewportY = Mathf.RoundToInt(Size * offset.y);
        float farDepth = SystemInfo.usesReversedZBuffer ? 0f : 1f;
        var pixels = new Color[Size * Size];
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            float eyeDepth = 0f;
            float pixelCoverage = 0f;
            if (x >= viewportX && y >= viewportY &&
                x < viewportX + viewportSize && y < viewportY + viewportSize)
            {
                bool left = x - viewportX < viewportSize / 2;
                if (!grass)
                    eyeDepth = layout == ContactLayout.GrassOnly || layout == ContactLayout.GrassToGrass ? 0f
                        : layout == ContactLayout.SceneBehindGrass ? 5.08f
                        : layout == ContactLayout.SceneToGrass
                        ? (left ? 6f : 4.92f)
                        : layout == ContactLayout.GrassToScene ? 5f : (left ? 5f : 4.92f);
                else if (layout == ContactLayout.GrassOnly || layout == ContactLayout.SceneBehindGrass)
                    eyeDepth = 5f;
                else if (layout == ContactLayout.GrassToGrass)
                    eyeDepth = left ? 5f : 4.92f;
                else if (layout == ContactLayout.SceneToGrass && left)
                    eyeDepth = 5f;
                else if (!left && layout == ContactLayout.GrassToScene)
                    eyeDepth = 4.92f;
                else if (!left && layout == ContactLayout.HiddenGrass)
                    eyeDepth = 5.1f;
                else if (!left && layout == ContactLayout.ZeroCoverage)
                    eyeDepth = 4.8f;
                if (grass && eyeDepth > 0f && layout != ContactLayout.ZeroCoverage)
                    pixelCoverage = coverage;
            }
            pixels[y * Size + x] = new Color(eyeDepth > 0f ? RawDepth(projection, eyeDepth) : farDepth,
                pixelCoverage, 0f, 0f);
        }
        texture.SetPixels(pixels);
        texture.Apply(false, false);
        return texture;
    }

    private static float RawDepth(Matrix4x4 projection, float eyeDepth)
    {
        Vector4 clip = projection * new Vector4(0f, 0f, -eyeDepth, 1f);
        float depth = clip.z / clip.w;
        // GL's clip depth is [-1, 1]; all other supported Unity backends use [0, 1].
        GraphicsDeviceType api = SystemInfo.graphicsDeviceType;
        return api == GraphicsDeviceType.OpenGLCore || api == GraphicsDeviceType.OpenGLES3
            ? depth * 0.5f + 0.5f : depth;
    }

    private static void AssertAlphaUnchanged(Color[] pixels)
    {
        for (int index = 0; index < pixels.Length; index++)
            Assert.That(pixels[index].a, Is.EqualTo(InitialColor.a).Within(ColorTolerance),
                "The contact overlay must preserve destination alpha at pixel " + index);
    }

    private static void AssertColor(Color actual, Color expected, string message)
    {
        Assert.That(actual.r, Is.EqualTo(expected.r).Within(ColorTolerance), message + " red");
        Assert.That(actual.g, Is.EqualTo(expected.g).Within(ColorTolerance), message + " green");
        Assert.That(actual.b, Is.EqualTo(expected.b).Within(ColorTolerance), message + " blue");
        Assert.That(actual.a, Is.EqualTo(expected.a).Within(ColorTolerance), message + " alpha");
    }
}
