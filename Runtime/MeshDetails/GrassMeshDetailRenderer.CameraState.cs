using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Cone.Grass
{
    internal sealed partial class GrassMeshDetailRenderer
    {
        /// <summary>One camera's GPU resources: instance data, visible lists, indirect arguments, counters and its Hi-Z.</summary>
        internal sealed class CameraState
        {
            public readonly Camera Camera;
            public int Frame = -1;
            public double LastUsed;
            public bool Valid;
            public BatchID Batch = BatchID.Null;
            public GraphicsBuffer Kinds, ListOffsets, ListCapacities, ListCounts, DrawLists, Args, Visible, Instances, Counters, Stats;
            public int[] ListOffset = Array.Empty<int>();
            public int[] ListCapacity = Array.Empty<int>();
            public int InstanceCapacity;
            public int VisibleBytes;
            public bool HasColor;
            public uint LayoutRevision;
            public float ReflectionScale = -1f;
            public KindData[] KindValues = Array.Empty<KindData>();
            public readonly Vector4[] Splits = new Vector4[MaxSplits];
            public int SplitCount;
            public Vector3 LightDirection = Vector3.up;

            public RenderTexture HiZ;
            public RenderTexture[] HiZMips = Array.Empty<RenderTexture>();
            public Matrix4x4 HiZViewProjection;
            public bool HiZValid;
            public Vector3 HiZPosition;
            public Quaternion HiZRotation;
            public int HiZWidth, HiZHeight;

            public bool ReadbackPending;
            public uint[] LastCounts;
            public uint[] LastCounters;
            public uint LastCountsLayout;
            private bool _grow;
            private int[] _needed = Array.Empty<int>();
            private int _neededInstances;

            public CameraState(Camera camera) => Camera = camera;

            public void Ensure(GrassMeshDetailRenderer renderer, Layout layout, BatchRendererGroup brg)
            {
                float reflectionScale = Camera.cameraType == CameraType.Reflection ? 1f : 0f;
                bool layoutChanged = LayoutRevision != layout.Revision || ListCapacity.Length != layout.ListCount;
                if (!layoutChanged && !_grow && Valid && ReflectionScale == reflectionScale)
                {
                    return;
                }

                if (layoutChanged || ListCapacity.Length != layout.ListCount)
                {
                    ListCapacity = new int[layout.ListCount];
                    for (int list = 0; list < layout.ListCount; list++)
                    {
                        GrassMeshDetails owner = layout.Kinds[layout.ListKind[list]].Owner;
                        int initial = Mathf.Clamp(owner.InitialListCapacity, 64, 1 << 22);
                        ListCapacity[list] = layout.ListShadow[list] ? Mathf.Max(64, initial / 2) : initial;
                    }

                    _needed = new int[layout.ListCount];
                    _neededInstances = 0;
                    InstanceCapacity = 0;
                    LastCounts = null;
                }

                if (_grow)
                {
                    for (int list = 0; list < ListCapacity.Length && list < _needed.Length; list++)
                    {
                        if (_needed[list] > ListCapacity[list])
                        {
                            ListCapacity[list] = Mathf.Min(Mathf.NextPowerOfTwo(Mathf.CeilToInt(_needed[list] * 1.25f)), 1 << 24);
                        }
                    }
                }

                int maxInstances = 1 << 24;
                foreach (GrassMeshDetails owner in layout.Owners)
                {
                    maxInstances = Mathf.Min(maxInstances, Mathf.Max(1024, Mathf.RoundToInt(owner.MaxInstances * 1e6f)));
                }

                long visible = 0;
                ListOffset = new int[ListCapacity.Length];
                for (int list = 0; list < ListCapacity.Length; list++)
                {
                    ListOffset[list] = (int)visible;
                    visible += ListCapacity[list];
                }

                int wantedInstances = Mathf.Max(InstanceCapacity, Mathf.CeilToInt(_neededInstances * 1.25f));
                if (wantedInstances == 0)
                {
                    // A first guess: every camera list full once (shadow lists mostly repeat camera instances).
                    long guess = 0;
                    for (int list = 0; list < ListCapacity.Length; list++)
                    {
                        guess += layout.ListShadow[list] ? ListCapacity[list] / 4 : ListCapacity[list];
                    }

                    wantedInstances = (int)Math.Min(guess, int.MaxValue);
                }

                InstanceCapacity = Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.Max(1024, wantedInstances)), 1024, Mathf.NextPowerOfTwo(maxInstances));
                InstanceCapacity = Mathf.Min(InstanceCapacity, maxInstances);
                ReleaseBuffers(brg);

                int lists = Mathf.Max(1, layout.ListCount);
                int draws = Mathf.Max(1, layout.Draws.Count);
                int kinds = Mathf.Max(1, layout.Kinds.Count);
                Kinds = new GraphicsBuffer(GraphicsBuffer.Target.Structured, kinds, KindDataStride) { name = "Grass Mesh Kinds" };
                ListOffsets = new GraphicsBuffer(GraphicsBuffer.Target.Structured, lists, sizeof(uint)) { name = "Grass Mesh List Offsets" };
                ListCapacities = new GraphicsBuffer(GraphicsBuffer.Target.Structured, lists, sizeof(uint)) { name = "Grass Mesh List Capacities" };
                ListCounts = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopySource, lists, sizeof(uint)) { name = "Grass Mesh List Counts" };
                DrawLists = new GraphicsBuffer(GraphicsBuffer.Target.Structured, draws, sizeof(uint)) { name = "Grass Mesh Draw Lists" };
                Args = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Raw, draws * 5, sizeof(uint)) { name = "Grass Mesh Indirect Args" };
                Visible = new GraphicsBuffer(GraphicsBuffer.Target.Raw, (int)Math.Max(1, visible), sizeof(uint)) { name = "Grass Mesh Visible Instances" };
                VisibleBytes = (int)Math.Max(1, visible) * sizeof(uint);
                Counters = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 4, sizeof(uint)) { name = "Grass Mesh Counters" };
                Stats = new GraphicsBuffer(GraphicsBuffer.Target.Structured, kinds, sizeof(uint)) { name = "Grass Mesh Statistics" };
                HasColor = layout.AnyColor;
                long instanceBytes = HeaderBytes + (long)InstanceCapacity * (HasColor ? InstanceBytes : 96);
                Instances = new GraphicsBuffer(GraphicsBuffer.Target.Raw, (int)(instanceBytes / 4), sizeof(uint)) { name = "Grass Mesh Instance Data" };
                Instances.SetData(new uint[HeaderBytes / 4]);

                var offsets = new uint[lists];
                var capacities = new uint[lists];
                for (int list = 0; list < layout.ListCount; list++)
                {
                    offsets[list] = (uint)ListOffset[list];
                    capacities[list] = (uint)ListCapacity[list];
                }

                ListOffsets.SetData(offsets);
                ListCapacities.SetData(capacities);
                ListCounts.SetData(new uint[lists]);
                Counters.SetData(new uint[4]);
                Stats.SetData(new uint[kinds]);
                var drawLists = new uint[draws];
                var args = new uint[draws * 5];
                for (int d = 0; d < layout.Draws.Count; d++)
                {
                    DrawEntry draw = layout.Draws[d];
                    drawLists[d] = (uint)draw.List;
                    args[d * 5] = draw.IndexCount;
                    args[d * 5 + 2] = draw.StartIndex;
                    args[d * 5 + 3] = draw.BaseVertex;
                }

                DrawLists.SetData(drawLists);
                Args.SetData(args);

                var metadata = new Unity.Collections.NativeArray<MetadataValue>(HasColor ? 3 : 2, Unity.Collections.Allocator.Temp);
                metadata[0] = new MetadataValue { NameID = s_objectToWorld, Value = 0x80000000u | (uint)ObjectToWorldOffset };
                metadata[1] = new MetadataValue { NameID = s_worldToObject, Value = 0x80000000u | (uint)WorldToObjectOffset };
                if (HasColor)
                {
                    metadata[2] = new MetadataValue { NameID = s_baseColor, Value = 0x80000000u | (uint)ColorOffset };
                }

                Batch = brg.AddBatch(metadata, Instances.bufferHandle);
                metadata.Dispose();

                ReflectionScale = reflectionScale;
                KindValues = new KindData[layout.Kinds.Count];
                for (int k = 0; k < layout.Kinds.Count; k++)
                {
                    KindEntry entry = layout.Kinds[k];
                    float scale = Camera.cameraType == CameraType.Reflection ? Mathf.Clamp(entry.Owner.ReflectionDistanceScale, 0.05f, 1f) : 1f;
                    KindValues[k] = BuildKindData(entry, scale);
                }

                if (KindValues.Length > 0)
                {
                    Kinds.SetData(KindValues);
                }

                LayoutRevision = layout.Revision;
                _grow = false;
                Valid = layout.Draws.Count > 0;
            }

            public int ObjectToWorldOffset => HeaderBytes;
            public int WorldToObjectOffset => HeaderBytes + InstanceCapacity * 48;
            public int ColorOffset => HasColor ? HeaderBytes + InstanceCapacity * 96 : 0;

            /// <summary>Asks for the list counts back, to grow lists that overflowed and for diagnostics.</summary>
            public void RequestReadback(CommandBuffer cmd)
            {
                if (ReadbackPending || ListCounts == null)
                {
                    return;
                }

                ReadbackPending = true;
                uint layout = LayoutRevision;
                GraphicsBuffer counts = ListCounts;
                cmd.RequestAsyncReadback(counts, request =>
                {
                    ReadbackPending = false;
                    if (request.hasError || counts != ListCounts || layout != LayoutRevision)
                    {
                        return;
                    }

                    uint[] data = request.GetData<uint>().ToArray();
                    LastCounts = data;
                    LastCountsLayout = layout;
                    for (int list = 0; list < data.Length && list < ListCapacity.Length && list < _needed.Length; list++)
                    {
                        _needed[list] = (int)Math.Min(data[list], int.MaxValue);
                        _grow |= data[list] > ListCapacity[list];
                    }
                });
                GraphicsBuffer counters = Counters;
                cmd.RequestAsyncReadback(counters, request =>
                {
                    if (request.hasError || counters != Counters || layout != LayoutRevision)
                    {
                        return;
                    }

                    uint[] data = request.GetData<uint>().ToArray();
                    LastCounters = data;
                    _neededInstances = (int)Math.Min(data[0], int.MaxValue);
                    _grow |= data[0] > InstanceCapacity;
                });
            }

            public CameraReadback ReadNow(Layout layout)
            {
                var result = new CameraReadback
                {
                    ListCounts = Read(ListCounts, layout.ListCount),
                    ListCapacities = Read(ListCapacities, layout.ListCount),
                    ListOffsets = Read(ListOffsets, layout.ListCount),
                    Visible = Read(Visible, VisibleBytes / 4),
                    Counters = Read(Counters, 4),
                    Stats = Read(Stats, layout.Kinds.Count),
                    InstanceCapacity = InstanceCapacity,
                    ListKind = (int[])layout.ListKind.Clone(),
                    ListShadow = (bool[])layout.ListShadow.Clone(),
                    KindListBase = new int[layout.Kinds.Count],
                    KindLodCount = new int[layout.Kinds.Count]
                };
                for (int k = 0; k < layout.Kinds.Count; k++)
                {
                    result.KindListBase[k] = layout.Kinds[k].ListBase;
                    result.KindLodCount[k] = layout.Kinds[k].LodCount;
                }

                uint[] raw = Read(Instances, Instances.count);
                result.InstanceData = new float[raw.Length];
                Buffer.BlockCopy(raw, 0, result.InstanceData, 0, raw.Length * 4);
                return result;
            }

            private static uint[] Read(GraphicsBuffer buffer, int count)
            {
                var data = new uint[Mathf.Max(0, Mathf.Min(count, buffer.count))];
                if (data.Length > 0)
                {
                    buffer.GetData(data);
                }

                return data;
            }

            public void EnsureHiZ(int width, int height)
            {
                int w0 = Mathf.Max(1, width / 2), h0 = Mathf.Max(1, height / 2);
                if (HiZ && HiZWidth == w0 && HiZHeight == h0)
                {
                    return;
                }

                ReleaseHiZ();
                HiZWidth = w0;
                HiZHeight = h0;
                int mips = 1;
                while ((w0 >> mips) > 0 || (h0 >> mips) > 0)
                {
                    mips++;
                }

                HiZ = new RenderTexture(new RenderTextureDescriptor(w0, h0, GraphicsFormat.R32_SFloat, GraphicsFormat.None, mips)
                {
                    useMipMap = true,
                    autoGenerateMips = false
                })
                {
                    name = "Grass Mesh Hi-Z",
                    filterMode = FilterMode.Point,
                    hideFlags = HideFlags.HideAndDontSave
                };
                HiZ.Create();
                HiZMips = new RenderTexture[mips];
                for (int m = 0; m < mips; m++)
                {
                    HiZMips[m] = new RenderTexture(new RenderTextureDescriptor(Mathf.Max(1, w0 >> m), Mathf.Max(1, h0 >> m), GraphicsFormat.R32_SFloat, GraphicsFormat.None, 1)
                    {
                        enableRandomWrite = true
                    })
                    {
                        name = "Grass Mesh Hi-Z Level " + m,
                        filterMode = FilterMode.Point,
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    HiZMips[m].Create();
                }

                HiZValid = false;
            }

            private void ReleaseHiZ()
            {
                DestroyTexture(HiZ);
                HiZ = null;
                foreach (RenderTexture mip in HiZMips)
                {
                    DestroyTexture(mip);
                }

                HiZMips = Array.Empty<RenderTexture>();
                HiZValid = false;
            }

            private void ReleaseBuffers(BatchRendererGroup brg)
            {
                if (Batch != BatchID.Null && brg != null)
                {
                    brg.RemoveBatch(Batch);
                }

                Batch = BatchID.Null;
                Kinds?.Release();
                ListOffsets?.Release();
                ListCapacities?.Release();
                ListCounts?.Release();
                DrawLists?.Release();
                Args?.Release();
                Visible?.Release();
                Instances?.Release();
                Counters?.Release();
                Stats?.Release();
                Kinds = ListOffsets = ListCapacities = ListCounts = DrawLists = Args = Visible = Instances = Counters = Stats = null;
                Valid = false;
            }

            public void Release(BatchRendererGroup brg)
            {
                ReleaseBuffers(brg);
                ReleaseHiZ();
            }

            private static void DestroyTexture(RenderTexture texture)
            {
                if (!texture)
                {
                    return;
                }

                texture.Release();
                if (Application.isPlaying)
                {
                    Object.Destroy(texture);
                }
                else
                {
                    Object.DestroyImmediate(texture);
                }
            }
        }
    }
}
