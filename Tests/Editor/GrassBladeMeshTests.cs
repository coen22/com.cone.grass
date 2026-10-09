using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public sealed class GrassBladeMeshTests
{
    [TestCase(5, 14, 12)]
    [TestCase(2, 8, 6)]
    [TestCase(0, 4, 2)]
    public void LodMeshHasSharedVerticesValidWindingAndBladeAlignedUvs(
        int subdivisions, int expectedVertices, int expectedTriangles)
    {
        Mesh mesh = InfiniteGrassRenderer.CreateBladeMesh(subdivisions);
        try
        {
            Vector3[] positions = mesh.vertices;
            Vector2[] uv = mesh.uv;
            int[] indices = mesh.triangles;
            Assert.That(positions.Length, Is.EqualTo(expectedVertices));
            Assert.That(indices.Length / 3, Is.EqualTo(expectedTriangles));
            Assert.That(uv.Length, Is.EqualTo(positions.Length));
            Assert.That(new HashSet<Vector3>(positions).Count, Is.EqualTo(positions.Length),
                "Adjacent blade sections must share their boundary vertices.");

            var referenced = new HashSet<int>();
            for (int i = 0; i < indices.Length; i += 3)
            {
                for (int corner = 0; corner < 3; corner++)
                {
                    Assert.That(indices[i + corner], Is.InRange(0, positions.Length - 1));
                    referenced.Add(indices[i + corner]);
                }

                Vector3 normal = Vector3.Cross(positions[indices[i + 1]] - positions[indices[i]],
                    positions[indices[i + 2]] - positions[indices[i]]);
                Assert.That(normal.sqrMagnitude, Is.GreaterThan(0.00000001f),
                    "Collapsed triangles still consume vertex work and can break contact depth.");
                Assert.That(normal.z, Is.LessThan(0f), "All triangles must retain the original blade winding.");
            }

            Assert.That(referenced.Count, Is.EqualTo(positions.Length));
            Assert.That(new HashSet<Vector3>(positions), Does.Contain(new Vector3(-0.25f, 0f, 0f)));
            Assert.That(new HashSet<Vector3>(positions), Does.Contain(new Vector3(0.25f, 0f, 0f)));
            Assert.That(new HashSet<Vector3>(positions), Does.Contain(new Vector3(-0.25f, 1f, 0f)));
            Assert.That(new HashSet<Vector3>(positions), Does.Contain(new Vector3(0.25f, 1f, 0f)),
                "Both cap vertices are required; the shader can continuously collapse their span.");
            for (int i = 0; i < positions.Length; i++)
            {
                Assert.That(positions[i].z, Is.Zero);
                Assert.That(positions[i].y, Is.InRange(0f, 1f));
                Assert.That(mesh.bounds.Contains(positions[i]), Is.True);
                Assert.That(uv[i].y, Is.EqualTo(positions[i].y).Within(0.000001f),
                    "Root blending and deformation use the true normalized blade height.");
                Assert.That(uv[i].x, Is.EqualTo(positions[i].x * 2f + 0.5f).Within(0.000001f));
            }
        }
        finally
        {
            Object.DestroyImmediate(mesh);
        }
    }

    [Test]
    public void LodCachesAreReusedAndQualityChangesReleaseObsoleteGeometry()
    {
        var gameObject = new GameObject("Grass mesh cache test");
        // Mesh creation does not need the scene singleton or a rendering camera.
        gameObject.SetActive(false);
        var settings = gameObject.AddComponent<InfiniteGrassRenderer>();
        Mesh[] original = null;
        Mesh[] rebuilt = null;
        try
        {
            settings.grassMeshSubdivision = 5;
            original = settings.GetLodMeshes();
            Assert.That(original.Length, Is.EqualTo(3));
            Assert.That(original[0].vertexCount, Is.EqualTo(14));
            Assert.That(original[1].vertexCount, Is.EqualTo(8));
            Assert.That(original[2].vertexCount, Is.EqualTo(4));
            Assert.That(original[0], Is.Not.SameAs(original[1]));
            Assert.That(original[1], Is.Not.SameAs(original[2]));
            Assert.That(settings.GetLodMeshes(), Is.SameAs(original));
            Assert.That(settings.GetGrassMeshCache(), Is.SameAs(original[0]));

            settings.grassMeshSubdivision = 3;
            rebuilt = settings.GetLodMeshes();
            Assert.That(rebuilt[0].vertexCount, Is.EqualTo(10));
            Assert.That(rebuilt[1].vertexCount, Is.EqualTo(8));
            Assert.That(rebuilt[2].vertexCount, Is.EqualTo(4));
            foreach (Mesh oldMesh in original)
                Assert.That(oldMesh == null, Is.True, "An obsolete native mesh must be destroyed when quality changes.");
        }
        finally
        {
            // Explicit cleanup also covers inactive objects which Unity has not awakened.
            DestroyRemaining(original);
            DestroyRemaining(rebuilt);
            Object.DestroyImmediate(gameObject);
        }
    }

    private static void DestroyRemaining(Mesh[] meshes)
    {
        if (meshes == null)
            return;
        foreach (Mesh mesh in meshes)
            if (mesh)
                Object.DestroyImmediate(mesh);
    }
}
