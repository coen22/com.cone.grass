using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

public partial class GrassDataRendererFeature
{
    // Small data types and render graph pass data.
    private sealed partial class GrassDataPass
    {
        private sealed class TextureWrapper
        {
            public RTHandle Handle;
            public double LastUsedTime;
        }

        private struct PlacementSource
        {
            public GrassPlacementArea Area;
            public GrassPlacementDrawData Data;
        }

        private readonly struct RendererDraw
        {
            public readonly Renderer Renderer;
            public readonly Material Material;
            public readonly int Submesh, Pass;
            public RendererDraw(Renderer renderer, Material material, int submesh, int pass)
            {
                Renderer = renderer;
                Material = material;
                Submesh = submesh;
                Pass = pass;
            }
        }

        private readonly struct InteractionDraw
        {
            public readonly Mesh Mesh;
            public readonly Material Material;
            public readonly int Pass;

            public InteractionDraw(Mesh mesh, Material material, int pass)
            {
                Mesh = mesh;
                Material = material;
                Pass = pass;
            }
        }

        private sealed class TerrainGroup
        {
            public EntityId Key;
            public double LastUsedTime;
            public ulong DensityVersion, CapturedDensityVersion;
            public bool UsesPrimaryDensity, DensityCaptured, DensityDirty;
            public Terrain Terrain;
            public RTHandle DensityMap;
            public TextureHandle DensityTexture;
            public readonly List<PlacementSource> Sources = new List<PlacementSource>();
            public readonly HashSet<Vector2Int> Tiles = new HashSet<Vector2Int>();
            public readonly List<Vector2Int> SortedTiles = new List<Vector2Int>();
        }

        private sealed class GroupKeyComparer : IComparer<TerrainGroup>
        {
            public static readonly GroupKeyComparer Instance = new GroupKeyComparer();
            public int Compare(TerrainGroup a, TerrainGroup b) => a.Key.CompareTo(b.Key);
        }

        private sealed class GroupAgeComparer : IComparer<TerrainGroup>
        {
            public static readonly GroupAgeComparer Instance = new GroupAgeComparer();
            public int Compare(TerrainGroup a, TerrainGroup b) => a.LastUsedTime.CompareTo(b.LastUsedTime);
        }

        private struct TerrainDraw
        {
            public Vector3 Origin, Size;
            public TextureHandle Height, Holes;
            public bool HasHoles;
            public Vector4 HeightTexelSize;
            public Matrix4x4 LocalToWorld;
        }

        private sealed class TileComparer : IComparer<Vector2Int>
        {
            public static readonly TileComparer Instance = new TileComparer();
            public int Compare(Vector2Int a, Vector2Int b)
            {
                int z = a.y.CompareTo(b.y);
                return z != 0 ? z : a.x.CompareTo(b.x);
            }
        }

        private sealed class DispatchData
        {
            public TerrainGroup Group;
            public readonly int[] Start = new int[2];
            public readonly int[] SizeInCells = new int[2];
            public bool UseTerrain, HasHoles;
            public Vector3 Origin, Size;
            public TextureHandle TerrainHeight, TerrainHoles, Density;
        }

        private sealed class RendererCapturePass
        {
            public List<RendererDraw> Draws;
            public List<InteractionDraw> Interactions;
            public Matrix4x4 CaptureVP;
            public bool HasDepth;
            public Color Clear;
        }

        private sealed class TerrainCapturePass
        {
            public List<TerrainDraw> Draws;
            public Mesh Quad;
            public Material Material;
            public int PassIndex;
            public Matrix4x4 CaptureVP;
        }

        private sealed class PlacementCapturePass
        {
            public List<PlacementSource> Sources;
            public Mesh Quad;
            public Material Material;
            public Matrix4x4 CaptureVP;
            public int PassIndex;
            public bool DrawSources;
        }

        private sealed class VariantCapturePass
        {
            public List<PlacementSource> Sources;
            public Mesh Quad;
            public Material Material;
            public Matrix4x4 CaptureVP;
            public int PassIndex;
        }

        private sealed class GenerationPass
        {
            public ComputeShader Shader;
            public int ResetKernel, GenerateKernel, FinalizeKernel;
            public GraphicsBuffer Positions, Counts, Arguments;
            public TextureHandle Height, Density, Mask, VariantWeights0, VariantWeights1;
            public GrassVariantGpuData Variants;
            public int[] HeadOffsets, HeadCapacities;
            public List<DispatchData> Dispatches;
            public int DispatchCount;
            public int[] LodOffsets, LodCapacities;
            public Vector4[] Frustum;
            public Vector2 Center;
            public Vector3 CameraPosition;
            public float Spacing, DrawDistance, CapturePadding, FullDensityDistance, DensityExponent,
                DensityTransition, LodTransition, BoundsRadius;
            public Vector4 LodDistances;
            public bool Authored, DensityShortens;
            public int ArgumentStride, ArgumentCountOffset;
        }

        private sealed class DrawPass
        {
            public GraphicsBuffer Arguments;
            public Mesh[] Meshes;
            public Material Material;
            public MaterialPropertyBlock[] Properties;
            public int ShaderPass, ArgumentStride;
            // Flower heads: drawn after the blades when a profile grows any.
            public Mesh[] HeadMeshes;
            public Material HeadMaterial;
            public MaterialPropertyBlock[] HeadProperties;
            public int[] HeadCapacities;
        }

        private sealed class ReadbackPass
        {
            public CameraState State;
            public GraphicsBuffer Counts;
        }
    }
}
