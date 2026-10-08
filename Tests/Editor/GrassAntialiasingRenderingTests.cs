using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

/// <summary>Renders the shipped blade shader without temporal accumulation or a replacement fragment program.</summary>
[Category("GrassGPU")]
public sealed class GrassAntialiasingRenderingTests
{
    private const int Size = 128;
    private const float BladePixelWidth = 4f;
    private const float BladePixelHeight = Size * 0.5f;
    private const string ShaderPath = "Packages/com.cone.grass/Runtime/Shaders/GrassBladeShader.shader";
    private Material material;
    private GraphicsBuffer positions;
    private Texture2D empty;

    [SetUp]
    public void SetUp()
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || !SystemInfo.supportsComputeShaders ||
            !SystemInfo.IsFormatSupported(GraphicsFormat.R8G8B8A8_UNorm, GraphicsFormatUsage.Render))
            Assert.Ignore("Blade coverage tests require a graphics device, structured buffers, and an RGBA8 render target.");

        Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
        Assert.That(shader, Is.Not.Null);
        Assert.That(shader.isSupported, Is.True, "The shipped blade shader must be supported on the tested graphics API.");
        material = new Material(shader) { enableInstancing = true };
        positions = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(float) * 4);
        empty = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
        empty.SetPixel(0, 0, Color.clear);
        empty.Apply(false, false);

        material.SetColor("_Color", Color.white);
        material.SetColor("_AOColor", Color.white);
        material.SetFloat("_GrassHeight", 1f);
        material.SetFloat("_GrassWidthRandomness", 0f);
        material.SetFloat("_GrassHeightRandomness", 0f);
        material.SetFloat("_GrassCurving", 0f);
        material.SetFloat("_ExpandDistantGrassWidth", 0f);
        material.SetFloat("_SubdivisionHeightBoost", 0f);
        material.SetFloat("_WindStrength", 0f);
        material.SetFloat("_GroundBlendStrength", 0f);
        int pass = material.FindPass("GrassForward");
        Assert.That(pass, Is.GreaterThanOrEqualTo(0));
        ShaderUtil.CompilePass(material, pass, true);
        Assert.That(ShaderUtil.IsPassCompiled(material, pass), Is.True,
            "Compile the actual forward pass synchronously before comparing its rendered coverage.");
    }

    [TearDown]
    public void TearDown()
    {
        positions?.Dispose();
        if (material) Object.DestroyImmediate(material);
        if (empty) Object.DestroyImmediate(empty);
    }

    [TestCase(0)]
    [TestCase(2)]
    [TestCase(5)]
    public void FullBladeCoveragePreservesNativeMsaaSilhouetteArea(int subdivisions)
    {
        Color[] pixels = Render(subdivisions, 4, 1f, BladePixelWidth, 0f);
        float geometricArea = BladePixelWidth * BladePixelHeight * 0.5f;
        Assert.That(CoveredArea(pixels), Is.EqualTo(geometricArea).Within(4f),
            "A fully covered blade must preserve the rasterizer's triangle coverage. " +
            "Applying analytic edge alpha through A2C a second time erodes this thin silhouette.");
    }

    [TestCase(0.5f, 2f)]
    [TestCase(0.25f, 1f)]
    public void DensityAndMinimumWidthCompensationAttenuateTheSameExpandedGeometry(
        float densityCoverage, float originalPixelWidth)
    {
        Color[] full = Render(0, 4, 1f, BladePixelWidth, 0f);
        Color[] thinned = Render(0, 4, densityCoverage, BladePixelWidth, 0f);
        Color[] widened = Render(0, 4, 1f, originalPixelWidth, BladePixelWidth);
        float ratio = CoveredArea(thinned) / CoveredArea(full);
        // A2C quantization and sample placement are device-dependent. Require
        // real attenuation while allowing different valid hardware masks.
        Assert.That(ratio, Is.InRange(densityCoverage - 0.2f, densityCoverage + 0.2f));
        AssertSamePixels(thinned, widened,
            "Minimum-width expansion must keep its proportional coverage compensation in the MSAA color pass.");
    }

    [Test]
    public void SingleSampleCoverageDoesNotChangeWhenOnlyTimeAdvances()
    {
        Color[] first = Render(0, 1, 0.5f, BladePixelWidth, 0f, 0f);
        Color[] later = Render(0, 1, 0.5f, BladePixelWidth, 0f, 37.123f);
        Assert.That(CoveredArea(first), Is.GreaterThan(0f).And.LessThan(BladePixelWidth * BladePixelHeight * 0.5f));
        AssertSamePixels(first, later,
            "With wind disabled, the single-sample coverage pattern must stay fixed without temporal accumulation.");

        Color[] full = Render(0, 1, 1f, BladePixelWidth, 0f);
        Color[] widened = Render(0, 1, 1f, .25f, BladePixelWidth);
        Assert.That(CoveredArea(full), Is.GreaterThan(0f));
        AssertSamePixels(full, widened,
            "An opaque projected-width floor must retain its expanded silhouette instead of dithering it back into holes.");
        Assert.That(CoveredArea(first), Is.LessThan(CoveredArea(full)),
            "Retaining expanded width must not remove the independent fractional-density fade.");

        material.SetFloat("_RandomNormal", 0f);
        Color[] unresolvedBase = Render(0, 1, 1f, .25f, BladePixelWidth, directionalAmbient: true);
        Color[] resolvedBase = Render(0, 1, 1f, BladePixelWidth, 0f, directionalAmbient: true);
        material.SetFloat("_RandomNormal", 1f);
        Color[] unresolvedDetail = Render(0, 1, 1f, .25f, BladePixelWidth, directionalAmbient: true);
        Color[] resolvedDetail = Render(0, 1, 1f, BladePixelWidth, 0f, directionalAmbient: true);
        Assert.That(CoveredArea(unresolvedBase), Is.GreaterThan(0f));
        AssertSamePixels(unresolvedBase, unresolvedDetail,
            "A widened quarter-pixel physical blade must filter its unresolved normal detail, rather than use the expanded width.");
        float resolvedDifference = 0f;
        for (int index = 0; index < resolvedBase.Length; index++)
            if (resolvedBase[index].a > .5f && resolvedDetail[index].a > .5f)
                resolvedDifference = Mathf.Max(resolvedDifference,
                    Mathf.Abs(resolvedBase[index].r - resolvedDetail[index].r));
        Assert.That(resolvedDifference, Is.GreaterThan(1f / 255f),
            "Resolved physical blades must retain measurable seeded detail; globally disabling normal randomness is not filtering.");
    }

    [TestCase(1, 4f, 0f, 1f / 16384f)]
    [TestCase(4, 4f, 0f, 1f / 16384f)]
    [TestCase(1, 2f, 4f, 1f / 16384f)]
    [TestCase(4, 2f, 4f, 1f / 16384f)]
    [TestCase(1, 4f, 0f, 16384f)]
    [TestCase(4, 4f, 0f, 16384f)]
    [TestCase(1, 1.5f, 3f, 16384f)]
    [TestCase(4, 1.5f, 3f, 16384f)]
    public void ProjectedCoverageIsInvariantToOrthographicWorldScale(
        int samples, float originalPixelWidth, float minimumPixelWidth, float worldScale)
    {
        Color[] reference = Render(0, samples, 1f, originalPixelWidth, minimumPixelWidth);
        // Scale blade dimensions and the orthographic viewport together, keeping
        // root XZ (and therefore its seed), depth and all projected vertices fixed.
        // A power of two avoids introducing different floating-point rounding.
        Color[] scaled = Render(0, samples, 1f, originalPixelWidth, minimumPixelWidth,
            orthographicScale: worldScale);
        Assert.That(CoveredArea(reference), Is.GreaterThan(0f),
            "The unscaled blade is a positive control for coverage and shader execution.");
        AssertSamePixels(reference, scaled,
            "Equivalent projected blades must keep the same density and width compensation at small and large world scales.");
    }

    [TestCase(0f)]
    [TestCase(4f)]
    public void ZeroWidthBladesStayInvisibleAtSmallWorldScale(float minimumPixelWidth)
    {
        Color[] positive = Render(0, 1, 1f, BladePixelWidth, minimumPixelWidth,
            orthographicScale: 1f / 16384f);
        Color[] zeroWidth = Render(0, 1, 1f, 0f, minimumPixelWidth,
            orthographicScale: 1f / 16384f);
        Assert.That(CoveredArea(positive), Is.GreaterThan(0f));
        Assert.That(CoveredArea(zeroWidth), Is.Zero,
            "The zero-width fallback must not create grass, including when a pixel minimum expands its mesh.");
    }

    [TestCase(0)]
    [TestCase(2)]
    [TestCase(5)]
    public void DownwardPerspectiveViewKeepsVisibleBladesOnBothSidesOfTheCamera(int subdivisions)
    {
        Matrix4x4 cameraToWorld = Matrix4x4.TRS(new Vector3(0f, 4f, 0f),
            Quaternion.Euler(80f, 0f, 0f), Vector3.one);
        Color[] inFront = Render(subdivisions, 4, 1f, 32f, 0f,
            rootPosition: new Vector3(0f, 0f, 1f), cameraToWorld: cameraToWorld, perspective: true);
        Color[] behind = Render(subdivisions, 4, 1f, 32f, 0f,
            rootPosition: new Vector3(0f, 0f, -1f), cameraToWorld: cameraToWorld, perspective: true);

        // Both one-unit blades are fully inside the 60-degree view. The rear
        // root is behind the camera only in world XZ, not in its viewing volume.
        // A central-ray billboard points its winding away from this camera and
        // loses that blade to Cull Back despite its substantial projected area.
        Assert.That(CoveredArea(inFront), Is.GreaterThan(16f), "The forward root is the camera/projection positive control.");
        Assert.That(CoveredArea(behind), Is.GreaterThan(16f),
            "Perspective blades must face their own viewing ray, including visible roots behind the camera in world XZ.");
    }

    [TestCase(0)]
    [TestCase(2)]
    [TestCase(5)]
    public void WindBentBladesRemainVisibleWhenTheirTrianglesTurnAwayFromTheCamera(int subdivisions)
    {
        Matrix4x4 cameraToWorld = Matrix4x4.TRS(new Vector3(0f, 4f, 0f),
            Quaternion.Euler(80f, 0f, 0f), Vector3.one);
        Color[] calm = Render(subdivisions, 4, 1f, BladePixelWidth, 0f,
            rootPosition: Vector3.zero, cameraToWorld: cameraToWorld);
        Assert.That(CoveredArea(calm), Is.GreaterThan(4f), "Calm blades are the camera and lighting positive control.");

        var wind = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
        try
        {
            wind.SetPixel(0, 0, new Color(0.5f, 0f, 0f, 1f));
            wind.Apply(false, false);
            material.SetFloat("_WindStrength", 0.5f);
            Color[] bent = Render(subdivisions, 4, 1f, BladePixelWidth, 0f,
                rootPosition: Vector3.zero, cameraToWorld: cameraToWorld, windTexture: wind);
            Assert.That(CoveredArea(bent), Is.GreaterThan(4f),
                "A thin blade must remain visible when wind bends its triangles away from the camera.");

            if (subdivisions == 0)
            {
                // The far LOD is one triangle with its base span on world X.
                // Its bent tip projects below the root in this downward view;
                // it has reversed winding but still covers about 36.5 pixels.
                // Use the quantized source texel to include its small X error.
                Color encoded = wind.GetPixel(0, 0);
                Vector3 tip = new Vector3((encoded.r * 2f - 1f) * 0.5f,
                    1f, (encoded.g * 2f - 1f) * 0.5f).normalized;
                float projectedHeight = Mathf.Abs(Vector3.Dot(tip, cameraToWorld.GetColumn(1))) * Size * 0.5f;
                float geometricArea = BladePixelWidth * projectedHeight * 0.5f;
                Assert.That(CoveredArea(bent), Is.EqualTo(geometricArea).Within(4f),
                    "The far triangle's projected area must not disappear at a geometry LOD boundary.");
            }
        }
        finally
        {
            material.SetFloat("_WindStrength", 0f);
            Object.DestroyImmediate(wind);
        }
    }

    [Test]
    public void GroundMatchedRootsUseNativeTerrainDielectricDiffuseEnergy()
    {
        material.SetFloat("_GroundBlendStrength", 1f);
        material.SetFloat("_GroundBlendHeight", 1f);
        Color[] pixels = Render(0, 1, 1f, BladePixelWidth, 0f, groundTexture: Texture2D.whiteTexture);
        float darkest = float.PositiveInfinity;
        foreach (Color pixel in pixels)
            if (pixel.a > .5f) darkest = Mathf.Min(darkest, pixel.r);
        // URP BRDF.hlsl uses dielectric reflectance .04. The first raster row
        // is at most 1/64 of this blade's height. Allow its gradient, one RGBA8
        // readback code and one half-float rounding step, rather than a fitted limit.
        const float reflectance = .04f;
        float tolerance = 1f / 255f + 1f / 2048f +
            reflectance * Mathf.SmoothStep(0f, 1f, 1f / BladePixelHeight);
        Assert.That(darkest, Is.EqualTo(1f - reflectance).Within(tolerance),
            "A ground-matched root must not use the full albedo where TerrainLit uses dielectric diffuse energy.");

        Assert.That(material.GetFloat("_GroundBlendFloor"), Is.Zero,
            "Existing materials must retain the original root-to-tip transition by default.");
        material.SetColor("_Color", Color.black);
        material.SetColor("_AOColor", Color.black);
        const float lightingWidth = 32f;
        Color[] legacy = Render(0, 1, 1f, lightingWidth, 0f, groundTexture: Texture2D.whiteTexture);
        Vector2 legacyRange = CoveredColorRange(legacy);
        Assert.That(legacyRange.x, Is.LessThan(1f / BladePixelHeight),
            "The wide blade must include covered tip pixels where the legacy ground fade approaches zero.");

        const float floor = .9f;
        material.SetFloat("_GroundBlendFloor", floor);
        Vector2 bodyRange = CoveredColorRange(Render(0, 1, 1f, lightingWidth, 0f,
            groundTexture: Texture2D.whiteTexture));
        // Black authored albedo isolates ground weight. Dielectric diffuse energy
        // is 1 - reflectance * weight; thus the minimum body response is derived
        // from floor * (1 - reflectance * floor), rather than a fitted image bound.
        float bodyMinimum = floor * (1f - reflectance * floor);
        const float readbackTolerance = 1f / 255f + 1f / 2048f;
        Assert.That(bodyRange.x, Is.EqualTo(bodyMinimum).Within(readbackTolerance),
            "The body/tip must retain the requested ground colour and its matching diffuse energy.");
        Assert.That(bodyRange.y, Is.EqualTo(1f - reflectance).Within(tolerance));
        Assert.That(bodyRange.y - bodyRange.x,
            Is.GreaterThan(((1f - reflectance) - bodyMinimum) * .5f),
            "The nonempty root and tip domains must still retain the height transition above the floor.");

        material.SetFloat("_GroundBlendFloor", 1f);
        AssertConstantCoveredColor(Render(0, 1, 1f, lightingWidth, 0f,
            groundTexture: Texture2D.whiteTexture), 1f - reflectance, readbackTolerance);
        material.SetFloat("_GroundBlendStrength", .25f);
        AssertConstantCoveredColor(Render(0, 1, 1f, lightingWidth, 0f,
            groundTexture: Texture2D.whiteTexture), .25f * (1f - reflectance * .25f), readbackTolerance);
        material.SetFloat("_GroundBlendStrength", 0f);
        AssertConstantCoveredColor(Render(0, 1, 1f, lightingWidth, 0f,
            groundTexture: Texture2D.whiteTexture), 0f, readbackTolerance);
        material.SetFloat("_GroundBlendStrength", 1f);
        AssertConstantCoveredColor(Render(0, 1, 1f, lightingWidth, 0f), 0f, readbackTolerance);
        material.SetFloat("_GroundBlendHeight", 0f);
        AssertConstantCoveredColor(Render(0, 1, 1f, lightingWidth, 0f,
            groundTexture: Texture2D.whiteTexture), 0f, readbackTolerance);
    }

    private static Vector2 CoveredColorRange(Color[] pixels)
    {
        float darkest = float.PositiveInfinity, brightest = float.NegativeInfinity;
        int covered = 0;
        foreach (Color pixel in pixels)
        {
            if (pixel.a <= .5f) continue;
            covered++;
            darkest = Mathf.Min(darkest, pixel.r);
            brightest = Mathf.Max(brightest, pixel.r);
        }
        Assert.That(covered, Is.GreaterThan(0), "Lighting assertions require actual covered blade pixels.");
        return new Vector2(darkest, brightest);
    }

    private static void AssertConstantCoveredColor(Color[] pixels, float expected, float tolerance)
    {
        Vector2 range = CoveredColorRange(pixels);
        Assert.That(range.x, Is.EqualTo(expected).Within(tolerance));
        Assert.That(range.y, Is.EqualTo(expected).Within(tolerance));
    }

    private Color[] Render(int subdivisions, int samples, float coverage, float originalPixelWidth,
        float minimumPixelWidth, float time = 0f, Vector3? rootPosition = null,
        Matrix4x4? cameraToWorld = null, bool perspective = false, Texture windTexture = null,
        float orthographicScale = 1f, Texture groundTexture = null, bool directionalAmbient = false)
    {
        var descriptor = new RenderTextureDescriptor(Size, Size)
        {
            graphicsFormat = GraphicsFormat.R8G8B8A8_UNorm,
            depthBufferBits = 0, msaaSamples = samples, sRGB = false, bindMS = false
        };
        if (SystemInfo.GetRenderTextureSupportedMSAASampleCount(descriptor) != samples)
            Assert.Ignore("The active graphics device does not support the requested " + samples + "x MSAA target.");

        Mesh mesh = null;
        RenderTexture target = null;
        RenderTexture resolved = null;
        Texture2D readback = null;
        RenderTexture previousTarget = RenderTexture.active;
        bool previousSRGBWrite = GL.sRGBWrite;
        var commands = new CommandBuffer { name = "Grass Native Coverage Regression" };
        try
        {
            mesh = InfiniteGrassRenderer.CreateBladeMesh(subdivisions);
            target = new RenderTexture(descriptor);
            Assert.That(target.Create(), Is.True);
            Assert.That(target.antiAliasing, Is.EqualTo(samples), "Inspect the created target, not only the requested sample count.");
            descriptor.msaaSamples = 1;
            resolved = new RenderTexture(descriptor);
            Assert.That(resolved.Create(), Is.True);
            readback = new Texture2D(Size, Size, TextureFormat.RGBA32, false, true);

            Vector3 root = rootPosition ?? new Vector3(0f, -0.5f * orthographicScale, 2f);
            positions.SetData(new[] { new Vector4(root.x, root.y, root.z, coverage) });
            // The shipped mesh's full base width is half of _GrassWidth. An
            // orthographic two-unit viewport gives Size/2 pixels per world unit.
            material.SetFloat("_GrassWidth", originalPixelWidth * 4f / Size * orthographicScale);
            material.SetFloat("_GrassHeight", orthographicScale);
            material.SetFloat("_MinimumPixelWidth", minimumPixelWidth);
            material.SetFloat("_GrassAlphaToCoverage", samples > 1 ? 1f : 0f);

            MaterialPropertyBlock properties = CreateDrawProperties(positions, empty, windTexture, groundTexture, time);
            if (directionalAmbient)
            {
                // Identical grayscale SH channels isolate normal response with
                // no main light, fog, ground blend or albedo-contrast change.
                var ambient = new Vector4(.5f, 0f, 0f, .5f);
                properties.SetVector("_GrassSHAr", ambient);
                properties.SetVector("_GrassSHAg", ambient);
                properties.SetVector("_GrassSHAb", ambient);
            }

            Matrix4x4 cameraWorld = cameraToWorld ?? Matrix4x4.identity;
            Matrix4x4 view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * cameraWorld.inverse;
            Matrix4x4 projection = GL.GetGPUProjectionMatrix(perspective
                ? Matrix4x4.Perspective(60f, 1f, 0.1f, 10f)
                : Matrix4x4.Ortho(-orthographicScale, orthographicScale,
                    -orthographicScale, orthographicScale, 0.1f, 10f), true);
            System.Action restoreGlobals = RecordViewGlobals(commands, cameraWorld, view, projection, orthographicScale, perspective, time);
            commands.SetRenderTarget(target);
            commands.SetViewport(new Rect(0f, 0f, Size, Size));
            commands.ClearRenderTarget(false, true, Color.clear);
            RenderingUtils.SetViewAndProjectionMatrices(commands, view, projection, false);
            int pass = material.FindPass("GrassForward");
            Assert.That(pass, Is.GreaterThanOrEqualTo(0));
            commands.DrawMesh(mesh, Matrix4x4.identity, material, 0, pass, properties);
            if (samples > 1)
                commands.ResolveAntiAliasedSurface(target, resolved);
            else
                commands.CopyTexture(target, resolved);
            restoreGlobals();
            GL.sRGBWrite = false;
            Graphics.ExecuteCommandBuffer(commands);

            RenderTexture.active = resolved;
            readback.ReadPixels(new Rect(0f, 0f, Size, Size), 0, 0, false);
            readback.Apply(false, false);
            Color[] pixels = readback.GetPixels();
            foreach (Color pixel in pixels)
            {
                Assert.That(pixel.g, Is.EqualTo(pixel.r).Within(1f / 255f),
                    "The configured blade must be grayscale, including at covered samples; an error shader is not a valid mask.");
                Assert.That(pixel.b, Is.EqualTo(pixel.r).Within(1f / 255f));
            }
            return pixels;
        }
        finally
        {
            GL.sRGBWrite = previousSRGBWrite;
            RenderTexture.active = previousTarget;
            commands.Release();
            if (mesh) Object.DestroyImmediate(mesh);
            if (target) Object.DestroyImmediate(target);
            if (resolved) Object.DestroyImmediate(resolved);
            if (readback) Object.DestroyImmediate(readback);
        }
    }

    private static MaterialPropertyBlock CreateDrawProperties(GraphicsBuffer positions, Texture empty,
        Texture windTexture, Texture groundTexture, float time)
    {
        var properties = new MaterialPropertyBlock();
        properties.SetBuffer("_GrassPositions", positions);
        properties.SetInteger("_GrassInstanceOffset", 0);
        properties.SetInteger("_GrassUseExplicitTime", 1);
        properties.SetFloat("_GrassTime", time);
        properties.SetFloat("_DrawDistance", 100f);
        properties.SetFloat("_TextureUpdateThreshold", 1f);
        properties.SetVector("_CenterPos", Vector4.zero);
        properties.SetTexture("_GrassColorRT", empty);
        properties.SetTexture("_GrassGroundColorRT", groundTexture ? groundTexture : empty);
        properties.SetTexture("_GrassSlopeRT", empty);
        properties.SetTexture("_GrassHeightMapRT", empty);
        properties.SetTexture("_WindTexture", windTexture ? windTexture : Texture2D.grayTexture);
        properties.SetVector("_GrassSHAr", new Vector4(0f, 0f, 0f, 1f));
        properties.SetVector("_GrassSHAg", new Vector4(0f, 0f, 0f, 1f));
        properties.SetVector("_GrassSHAb", new Vector4(0f, 0f, 0f, 1f));
        properties.SetVector("_GrassSHBr", Vector4.zero);
        properties.SetVector("_GrassSHBg", Vector4.zero);
        properties.SetVector("_GrassSHBb", Vector4.zero);
        properties.SetVector("_GrassSHC", Vector4.zero);

        return properties;
    }

    private static System.Action RecordViewGlobals(CommandBuffer commands, Matrix4x4 cameraWorld,
        Matrix4x4 view, Matrix4x4 projection, float orthographicScale, bool perspective, float time)
    {
        string[] matrixNames = { "unity_MatrixV", "glstate_matrix_projection", "unity_MatrixVP" };
        var previousMatrices = new Matrix4x4[matrixNames.Length];
        for (int index = 0; index < matrixNames.Length; index++)
            previousMatrices[index] = Shader.GetGlobalMatrix(matrixNames[index]);
        string[] vectorNames =
        {
            "_WorldSpaceCameraPos", "_ScaledScreenParams", "unity_OrthoParams", "_ProjectionParams",
            "_MainLightColor", "_MainLightPosition", "unity_FogColor", "unity_FogParams", "_Time", "_TimeParameters"
        };
        Vector4[] vectors =
        {
            cameraWorld.GetColumn(3), new Vector4(Size, Size, 1f + 1f / Size, 1f + 1f / Size),
            new Vector4(2f * orthographicScale, 2f * orthographicScale, 0f, perspective ? 0f : 1f),
            new Vector4(1f, 0.1f, 10f, 0.1f),
            Vector4.zero, new Vector4(0f, 1f, 0f, 0f), Vector4.one, Vector4.zero,
            new Vector4(time / 20f, time, time * 2f, time * 3f),
            new Vector4(time, Mathf.Sin(time), Mathf.Cos(time), 0f)
        };
        var previousVectors = new Vector4[vectorNames.Length];
        for (int index = 0; index < vectorNames.Length; index++)
        {
            previousVectors[index] = Shader.GetGlobalVector(vectorNames[index]);
            commands.SetGlobalVector(vectorNames[index], vectors[index]);
        }
        return () =>
        {
            for (int index = 0; index < matrixNames.Length; index++)
                commands.SetGlobalMatrix(matrixNames[index], previousMatrices[index]);
            for (int index = 0; index < vectorNames.Length; index++)
                commands.SetGlobalVector(vectorNames[index], previousVectors[index]);
        };
    }

    private static float CoveredArea(Color[] pixels)
    {
        float area = 0f;
        foreach (Color pixel in pixels)
            area += pixel.r;
        return area;
    }

    private static void AssertSamePixels(Color[] expected, Color[] actual, string message)
    {
        Assert.That(actual.Length, Is.EqualTo(expected.Length));
        for (int index = 0; index < actual.Length; index++)
            Assert.That(actual[index].r, Is.EqualTo(expected[index].r).Within(1f / 255f), message + " Pixel " + index);
    }
}
