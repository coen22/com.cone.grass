using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

public sealed class GrassInteractionMeshTests
{
    [Test]
    public void RetainedMaterialUsesThePremultipliedProducerAndOnlyOwnedClonesAreDestroyed()
    {
        Material template = Resources.Load<Material>("InfiniteGrassInteraction");
        Assert.That(template, Is.Not.Null, "The Resources asset retains the slope shader in players.");
        Assert.That(template.shader.name, Is.EqualTo("InfiniteGrass/Modifiers/GrassInteractor"));
        Assert.That(template.FindPass("GrassSlope"), Is.GreaterThanOrEqualTo(0));
        Assert.That(template.HasProperty("_MainTex"), Is.False, "The field producer has no sampled texture inputs.");

        var output = new GrassInteractionMesh();
        Mesh ownedMesh = output.Mesh;
        Material ownedMaterial = output.Material;
        try
        {
            Assert.That(ownedMesh, Is.Not.Null);
            Assert.That(ownedMesh.indexFormat, Is.EqualTo(IndexFormat.UInt16));
            Assert.That(ownedMaterial, Is.Not.Null);
            Assert.That(ownedMaterial, Is.Not.SameAs(template));
            Assert.That(ownedMaterial.shader, Is.SameAs(template.shader));
            Assert.That(ownedMesh.hideFlags, Is.EqualTo(HideFlags.HideAndDontSave));
            Assert.That(ownedMaterial.hideFlags, Is.EqualTo(HideFlags.HideAndDontSave));
            Assert.That(output.Bounds, Is.EqualTo(default(Bounds)));
            output.Dispose();
            output.Dispose();
            Assert.That(ownedMesh == null, Is.True, "The generated native mesh is owned by the helper.");
            Assert.That(ownedMaterial == null, Is.True, "The generated material clone is owned by the helper.");
            Assert.That(template != null, Is.True, "Disposing a helper must preserve the retained template.");
            Assert.That(output.Mesh, Is.Null);
            Assert.That(output.Material, Is.Null);
        }
        finally
        {
            output.Dispose();
        }
    }

    [Test]
    public void AssignedProducerShaderCreatesAnOwnedMaterialWithoutChangingTheTemplate()
    {
        Material template = Resources.Load<Material>("InfiniteGrassInteraction");
        Assert.That(template, Is.Not.Null);
        Shader shader = template.shader;
        Material owned;
        using (var output = new GrassInteractionMesh(shader))
        {
            owned = output.Material;
            Assert.That(owned, Is.Not.Null);
            Assert.That(owned, Is.Not.SameAs(template));
            Assert.That(owned.shader, Is.SameAs(shader));
        }
        Assert.That(owned == null, Is.True);
        Assert.That(shader != null, Is.True);
        Assert.That(template != null, Is.True);
        Assert.That(template.shader, Is.SameAs(shader));
    }

    [Test]
    public void AssignedShaderWithoutSlopePassDoesNotSilentlyUseTheDefaultProducer()
    {
        Shader shader = Resources.Load<Shader>("InfiniteGrassPlacement");
        Assert.That(shader, Is.Not.Null);
        var field = new GrassInteractionField(1f, 8);
        Stamp(field, Vector2.zero, 0.0, Vector2.right);
        using (var output = new GrassInteractionMesh(shader))
        {
            Assert.That(output.Material, Is.Null);
            Assert.That(output.Mesh, Is.Not.Null);
            Assert.That(output.Rebuild(field, 0.0, 1f, 1f), Is.False);
            Assert.That(output.Mesh.vertexCount, Is.Zero);
        }
        Assert.That(shader != null, Is.True, "Rejected shader assets are borrowed, never owned by the helper.");
    }

    [Test]
    public void SingleNodeBuildsFourWorldCellsWithPremultipliedDirectionAndTransparentHalo()
    {
        var origin = new Vector2(10f, -6f);
        var field = new GrassInteractionField(0.5f, 8, origin);
        Stamp(field, origin, 2.0, Vector2.right);
        Assert.That(field.Count, Is.EqualTo(1));
        using (var output = new GrassInteractionMesh())
        {
            Assert.That(output.Rebuild(field, 2.0, 4f, 0.25f), Is.True);
            Assert.That(output.Mesh.vertexCount, Is.EqualTo(16));
            Assert.That(output.Mesh.GetIndexCount(0), Is.EqualTo(24u));
            Assert.That(output.Bounds.center, Is.EqualTo(new Vector3(10f, 0f, -6f)));
            Assert.That(output.Bounds.size, Is.EqualTo(new Vector3(1f, 0f, 1f)));
            Assert.That(output.Mesh.bounds, Is.EqualTo(output.Bounds));
            Vector3[] vertices = output.Mesh.vertices;
            Color[] colors = output.Mesh.colors;
            int activeVertices = 0;
            for (int i = 0; i < vertices.Length; i++)
            {
                Assert.That(output.Bounds.Contains(vertices[i]), Is.True);
                Assert.That(vertices[i].y, Is.Zero, "The capture projects world XZ and never writes support height.");
                if (vertices[i] == new Vector3(origin.x, 0f, origin.y))
                {
                    activeVertices++;
                    Assert.That(colors[i], Is.EqualTo(new Color(0.25f, 0.125f, 0f, 0.25f)),
                        "The field weights the encoded direction before the capture interpolates it.");
                }
                else
                {
                    Assert.That(colors[i], Is.EqualTo(Color.clear),
                        "A missing premultiplied vertex contributes neither coverage nor encoded direction.");
                }
            }
            Assert.That(activeVertices, Is.EqualTo(4));
            AssertUniqueCellsAndValidTriangles(output.Mesh, 4);
        }
    }

    [Test]
    public void AdjacentNodesShareCellsWithoutAccumulatingOverlappingOpacity()
    {
        var field = new GrassInteractionField(1f, 8);
        Stamp(field, Vector2.zero, 1.0, Vector2.right);
        Stamp(field, Vector2.right, 1.0, Vector2.up);
        using (var output = new GrassInteractionMesh())
        {
            Assert.That(output.Rebuild(field, 1.0, 2f, 1f), Is.True);
            Assert.That(output.Mesh.vertexCount, Is.EqualTo(24), "Two adjacent nodes have six distinct cells.");
            AssertUniqueCellsAndValidTriangles(output.Mesh, 6);

            var sharedColors = new Dictionary<Vector3, Color>();
            Vector3[] vertices = output.Mesh.vertices;
            Color[] colors = output.Mesh.colors;
            for (int i = 0; i < vertices.Length; i++)
            {
                if (sharedColors.TryGetValue(vertices[i], out Color prior))
                    Assert.That(colors[i], Is.EqualTo(prior), "Adjacent cells must agree along their common edge.");
                else
                    sharedColors.Add(vertices[i], colors[i]);
            }
            Assert.That(sharedColors[Vector3.zero], Is.EqualTo(new Color(1f, 0.5f, 0f, 1f)));
            Assert.That(sharedColors[Vector3.right], Is.EqualTo(new Color(0.5f, 1f, 0f, 1f)));
        }
    }

    [TestCase(0.1f, 0f, 0f, -20, -10)]
    [TestCase(0.1f, 0f, 0f, 10, 20)]
    [TestCase(0.1f, 7.3f, -12.7f, -20, -10)]
    [TestCase(0.1f, -1024.03f, 2048.1f, -10, 20)]
    [TestCase(0.15f, 0f, 0f, -100, -100)]
    public void TranslatedFieldBoundsContainEveryEmittedVertex(
        float spacing, float originX, float originZ, int first, int second)
    {
        var field = new GrassInteractionField(spacing, 8, new Vector2(originX, originZ));
        Stamp(field, field.GetPosition(new Vector2Int(first, first)), 0.0, Vector2.right);
        Stamp(field, field.GetPosition(new Vector2Int(second, second)), 0.0, Vector2.up);
        Assert.That(field.Count, Is.EqualTo(first == second ? 1 : 2));
        using (var output = new GrassInteractionMesh())
        {
            Assert.That(output.Rebuild(field, 0.0, 1f, 1f), Is.True);
            Assert.That(output.Mesh.vertexCount, Is.EqualTo(field.Count * 16));
            Assert.That(output.Mesh.bounds, Is.EqualTo(output.Bounds));
            foreach (Vector3 vertex in output.Mesh.vertices)
            {
                Assert.That(output.Bounds.Contains(vertex), Is.True,
                    "Rounding a translated center/extent must not exclude a mesh vertex.");
                Assert.That(vertex.x, Is.InRange(output.Bounds.min.x, output.Bounds.max.x));
                Assert.That(vertex.z, Is.InRange(output.Bounds.min.z, output.Bounds.max.z));
            }
        }
    }

    [Test]
    public void RecoveryChangesAlphaAndFinalExpiryRemovesOutputWithoutReplacingResources()
    {
        var field = new GrassInteractionField(1f, 8);
        Stamp(field, Vector2.zero, 0.0, Vector2.down);
        using (var output = new GrassInteractionMesh())
        {
            Mesh mesh = output.Mesh;
            Material material = output.Material;
            Assert.That(output.Rebuild(field, 1.0, 2f, 0.6f), Is.True);
            AssertNodeColor(output.Mesh, Vector3.zero, new Color(0.15f, 0f, 0f, 0.3f));
            Assert.That(output.Rebuild(field, 2.0, 2f, 0.6f), Is.False);
            Assert.That(mesh.vertexCount, Is.Zero);
            Assert.That(output.Bounds, Is.EqualTo(default(Bounds)));
            Assert.That(field.Count, Is.EqualTo(1), "Mesh evaluation must not mutate the field's lifetime.");

            Stamp(field, Vector2.zero, 3.0, Vector2.right);
            Assert.That(output.Rebuild(field, 3.0, 2f, 1f), Is.True);
            Assert.That(output.Mesh, Is.SameAs(mesh));
            Assert.That(output.Material, Is.SameAs(material));
            output.Clear();
            output.Clear();
            Assert.That(mesh.vertexCount, Is.Zero);
            Assert.That(output.Bounds, Is.EqualTo(default(Bounds)));
            Assert.That(output.Mesh, Is.SameAs(mesh));
            Assert.That(output.Material, Is.SameAs(material));
            Assert.That(output.Rebuild(field, 3.0, 2f, 1f), Is.True);
        }
    }

    [TestCase(1f)]
    [TestCase(0.4f)]
    public void NeighborDirectionsAreWeightedByTheirRecoveryBeforeInterpolation(float strength)
    {
        var field = new GrassInteractionField(1f, 8);
        Stamp(field, Vector2.right, 0.0, Vector2.up);
        Stamp(field, Vector2.zero, 1.0, Vector2.right);
        using (var output = new GrassInteractionMesh())
        {
            Assert.That(output.Rebuild(field, 1.0, 2f, strength), Is.True);
            // At t1 the origin node has weight1 and the neighbor has weight.5.
            // Their common-edge midpoint must retain weighted, rather than
            // equally weighted, encoded directions regardless of overall strength.
            Color midpoint = SampleSharedEdge(output.Mesh);
            AssertColor(midpoint, new Color(0.625f, 0.5f, 0f, 0.75f) * strength, 0.000001f);
            Assert.That(midpoint.r / midpoint.a * 2f - 1f, Is.EqualTo(2f / 3f).Within(0.000001f));
            Assert.That(midpoint.g / midpoint.a * 2f - 1f, Is.EqualTo(1f / 3f).Within(0.000001f));
        }
    }

    [Test]
    public void NeighborExpiryPreservesTheSurvivingEdgesDirectionContinuously()
    {
        var field = CreateExpiryField();
        using (var output = new GrassInteractionMesh())
        {
            Assert.That(output.Rebuild(field, 1.0 - 0.000001, 1f, 1f), Is.True);
            Color before = SampleSharedEdge(output.Mesh);
            Assert.That(output.Rebuild(field, 1.0, 1f, 1f), Is.True);
            Color atExpiry = SampleSharedEdge(output.Mesh);
            AssertColor(atExpiry, new Color(0.421875f, 0.2109375f, 0f, 0.421875f), 0.000001f);
            AssertColor(before, atExpiry, 0.000002f);
            Assert.That(before.r / before.a * 2f - 1f, Is.EqualTo(1f).Within(0.00001f));
            Assert.That(before.g / before.a * 2f - 1f, Is.EqualTo(0f).Within(0.00001f),
                "An almost-expired neighbor must not retain an equally weighted direction until its exact expiry.");
        }
    }

    [TestCase(1f)]
    [TestCase(0.4f)]
    [Category("GrassGPU")]
    public void ShippedCapturePreservesWeightedDirectionAndCoverage(float strength)
    {
        RequireCaptureDevice();
        var field = new GrassInteractionField(1f, 8);
        Stamp(field, Vector2.right, 0.0, Vector2.up);
        Stamp(field, Vector2.zero, 1.0, Vector2.right);
        using (var output = new GrassInteractionMesh())
        {
            Assert.That(output.Rebuild(field, 1.0, 2f, strength), Is.True);
            Color pixel = RenderSharedEdge(output);
            AssertColor(pixel, new Color(0.625f, 0.5f, 0f, 0.75f) * strength, 0.00001f);
        }
    }

    [Test]
    [Category("GrassGPU")]
    public void ShippedCaptureDoesNotJumpWhenANeighborExpires()
    {
        RequireCaptureDevice();
        var field = CreateExpiryField();
        using (var output = new GrassInteractionMesh())
        {
            Assert.That(output.Rebuild(field, 1.0 - 0.000001, 1f, 1f), Is.True);
            Color before = RenderSharedEdge(output);
            Assert.That(output.Rebuild(field, 1.0, 1f, 1f), Is.True);
            Color atExpiry = RenderSharedEdge(output);
            AssertColor(atExpiry, new Color(0.421875f, 0.2109375f, 0f, 0.421875f), 0.00001f);
            AssertColor(before, atExpiry, 0.00001f);
        }
    }

    [TestCase(0f)]
    [TestCase(-1f)]
    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    public void InvalidOrZeroStrengthClearsPreviousOutput(float strength)
    {
        var field = new GrassInteractionField(1f, 8);
        Stamp(field, Vector2.zero, 0.0, Vector2.right);
        using (var output = new GrassInteractionMesh())
        {
            Assert.That(output.Rebuild(field, 0.0, 1f, 1f), Is.True);
            Assert.That(output.Rebuild(field, 0.0, 1f, strength), Is.False);
            Assert.That(output.Mesh.vertexCount, Is.Zero);
            Assert.That(output.Bounds, Is.EqualTo(default(Bounds)));
            Assert.That(field.Count, Is.EqualTo(1));
        }
    }

    [Test]
    public void UnrepresentableFloatCellsAreSkippedWithoutRetainingPreviousGeometry()
    {
        var ordinary = new GrassInteractionField(1f, 8);
        Stamp(ordinary, Vector2.zero, 0.0, Vector2.right);
        var distant = new GrassInteractionField(0.25f, 8, new Vector2(16777216f, 16777216f));
        Stamp(distant, distant.Origin, 0.0, Vector2.right);
        Assert.That(distant.Count, Is.EqualTo(1));
        Assert.That(distant.GetPosition(Vector2Int.one), Is.EqualTo(distant.Origin),
            "The positive halo collapses because the cell is smaller than a float ULP at this origin.");
        using (var output = new GrassInteractionMesh())
        {
            Assert.That(output.Rebuild(ordinary, 0.0, 1f, 1f), Is.True);
            Assert.That(output.Rebuild(distant, 0.0, 1f, 1f), Is.False);
            Assert.That(output.Mesh.vertexCount, Is.Zero);
            Assert.That(output.Bounds, Is.EqualTo(default(Bounds)));
        }
    }

    [Test]
    public void MaximumFieldCapacityHasABoundedMeshWithSixteenBitIndices()
    {
        var field = new GrassInteractionField(1f, GrassInteractionField.MaximumCapacity);
        for (int i = 0; i < field.Capacity; i++)
            Stamp(field, new Vector2((i % 64) * 3f, (i / 64) * 3f), 0.0, Vector2.right);
        Assert.That(field.Count, Is.EqualTo(GrassInteractionField.MaximumCapacity));
        using (var output = new GrassInteractionMesh())
        {
            Assert.That(output.Rebuild(field, 0.0, 1f, 1f), Is.True);
            Assert.That(output.Mesh.indexFormat, Is.EqualTo(IndexFormat.UInt16));
            Assert.That(output.Mesh.vertexCount, Is.EqualTo(32768));
            Assert.That(output.Mesh.GetIndexCount(0), Is.EqualTo(49152u));
            AssertUniqueCellsAndValidTriangles(output.Mesh, field.Count * 4);
        }
    }

    private static GrassInteractionField CreateExpiryField()
    {
        var field = new GrassInteractionField(1f, 8);
        Stamp(field, Vector2.right, 0.0, Vector2.up);
        Stamp(field, Vector2.zero, 0.75, Vector2.right);
        return field;
    }

    private static Color SampleSharedEdge(Mesh mesh)
    {
        Vector3[] vertices = mesh.vertices;
        Color[] colors = mesh.colors;
        Color left = default, right = default;
        bool foundLeft = false, foundRight = false;
        for (int i = 0; i < vertices.Length; i++)
        {
            if (vertices[i] == Vector3.zero)
            {
                left = colors[i];
                foundLeft = true;
            }
            if (vertices[i] == Vector3.right)
            {
                right = colors[i];
                foundRight = true;
            }
        }
        Assert.That(foundLeft && foundRight, Is.True,
            "The still-live origin node must keep this common edge present across its neighbor's expiry.");
        return Color.Lerp(left, right, 0.5f);
    }

    private static void RequireCaptureDevice()
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ||
            !SystemInfo.SupportsTextureFormat(TextureFormat.RGBAFloat) ||
            !SystemInfo.IsFormatSupported(GraphicsFormat.R32G32B32A32_SFloat, GraphicsFormatUsage.Render) ||
            !SystemInfo.IsFormatSupported(GraphicsFormat.R32G32B32A32_SFloat, GraphicsFormatUsage.Blend))
            Assert.Ignore("Interaction capture tests require a graphics device and a blendable RGBA float target.");
    }

    private static Color RenderSharedEdge(GrassInteractionMesh output)
    {
        Assert.That(output.Material.shader.isSupported, Is.True);
        int pass = output.Material.FindPass("GrassSlope");
        Assert.That(pass, Is.GreaterThanOrEqualTo(0));
        ShaderUtil.CompilePass(output.Material, pass, true);
        Assert.That(ShaderUtil.IsPassCompiled(output.Material, pass), Is.True);
        RenderTexture previousTarget = RenderTexture.active;
        RenderTexture target = null;
        Texture2D readback = null;
        CommandBuffer commands = null;
        try
        {
            target = new RenderTexture(new RenderTextureDescriptor(1, 1)
            {
                graphicsFormat = GraphicsFormat.R32G32B32A32_SFloat,
                depthBufferBits = 0, msaaSamples = 1, sRGB = false
            });
            Assert.That(target.Create(), Is.True);
            readback = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);

            // The single sample's clip center maps exactly to world(.5,0,0),
            // halfway along the two nodes' common edge, on every clip-origin convention.
            Matrix4x4 capture = Matrix4x4.zero;
            capture.m00 = 1f;
            capture.m03 = -0.5f;
            capture.m12 = 1f;
            capture.m33 = 1f;
            var properties = new MaterialPropertyBlock();
            properties.SetMatrix("_GrassCaptureVP", capture);
            commands = new CommandBuffer { name = "Grass interaction producer regression" };
            commands.SetRenderTarget(target);
            commands.SetViewport(new Rect(0f, 0f, 1f, 1f));
            commands.ClearRenderTarget(false, true, Color.clear);
            commands.DrawMesh(output.Mesh, Matrix4x4.identity, output.Material, 0, pass, properties);
            Graphics.ExecuteCommandBuffer(commands);
            RenderTexture.active = target;
            readback.ReadPixels(new Rect(0f, 0f, 1f, 1f), 0, 0, false);
            readback.Apply(false, false);
            return readback.GetPixel(0, 0);
        }
        finally
        {
            commands?.Release();
            RenderTexture.active = previousTarget;
            if (readback) Object.DestroyImmediate(readback);
            if (target)
            {
                target.Release();
                Object.DestroyImmediate(target);
            }
        }
    }

    private static void AssertColor(Color actual, Color expected, float tolerance)
    {
        Assert.That(actual.r, Is.EqualTo(expected.r).Within(tolerance), "Encoded R");
        Assert.That(actual.g, Is.EqualTo(expected.g).Within(tolerance), "Encoded G");
        Assert.That(actual.b, Is.EqualTo(expected.b).Within(tolerance), "Encoded B");
        Assert.That(actual.a, Is.EqualTo(expected.a).Within(tolerance), "Coverage A");
    }

    private static void Stamp(GrassInteractionField field, Vector2 position, double time, Vector2 direction)
    {
        float radius = field.CellSize * 0.125f;
        Assert.That(field.AdvanceSweptCircle(position, radius, time, position, radius, time, direction), Is.True);
    }

    private static void AssertNodeColor(Mesh mesh, Vector3 position, Color expected)
    {
        Vector3[] vertices = mesh.vertices;
        Color[] colors = mesh.colors;
        int found = 0;
        for (int i = 0; i < vertices.Length; i++)
        {
            if (vertices[i] != position)
                continue;
            found++;
            Assert.That(colors[i], Is.EqualTo(expected));
        }
        Assert.That(found, Is.GreaterThan(0));
    }

    private static void AssertUniqueCellsAndValidTriangles(Mesh mesh, int expectedCells)
    {
        Vector3[] vertices = mesh.vertices;
        int[] triangles = mesh.triangles;
        var cells = new HashSet<Vector2>();
        Assert.That(vertices.Length, Is.EqualTo(expectedCells * 4));
        Assert.That(triangles.Length, Is.EqualTo(expectedCells * 6));
        for (int i = 0; i < vertices.Length; i += 4)
            Assert.That(cells.Add(new Vector2(vertices[i].x, vertices[i].z)), Is.True,
                "Every world grid cell must be emitted once to avoid alpha accumulation.");
        for (int i = 0; i < triangles.Length; i += 3)
        {
            for (int j = 0; j < 3; j++)
                Assert.That(triangles[i + j], Is.InRange(0, vertices.Length - 1));
            Vector3 normal = Vector3.Cross(vertices[triangles[i + 1]] - vertices[triangles[i]],
                vertices[triangles[i + 2]] - vertices[triangles[i]]);
            Assert.That(normal.y, Is.GreaterThan(0f), "Cell triangles must have consistent non-degenerate winding.");
        }
        Assert.That(cells.Count, Is.EqualTo(expectedCells));
    }
}
