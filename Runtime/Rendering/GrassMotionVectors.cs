using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Per-feature, per-camera motion history for the indirect world-grid grass.
/// Record after the color draw in a pass requesting Depth | Motion, so URP has
/// already populated its motion/depth targets. No CPU position readback is used.
/// </summary>
public sealed class GrassMotionVectors : IDisposable
{
    public enum Mode { Auto, Always, Off }

    [Serializable]
    public sealed class Settings
    {
        [Tooltip("Optional motion history. Auto supplies grass motion for temporal AA and object motion blur on single-sample cameras. Always also supports custom motion consumers.")]
        public Mode mode = Mode.Off;
    }

    private static class Id
    {
        public static readonly int Positions = Shader.PropertyToID("_GrassHistoryPositions");
        public static readonly int Counts = Shader.PropertyToID("_GrassHistoryCounts");
        public static readonly int SnapshotCounts = Shader.PropertyToID("_GrassHistorySnapshotCounts");
        public static readonly int Keys = Shader.PropertyToID("_GrassHistoryKeys");
        public static readonly int Roots = Shader.PropertyToID("_GrassHistoryRoots");
        public static readonly int Dispatch = Shader.PropertyToID("_GrassHistoryDispatch");
        public static readonly int Offsets = Shader.PropertyToID("_GrassHistoryOffsets");
        public static readonly int Capacities = Shader.PropertyToID("_GrassHistoryCapacities");
        public static readonly int Rows = Shader.PropertyToID("_GrassHistoryRows");
        public static readonly int Mask = Shader.PropertyToID("_GrassHistoryMask");
        public static readonly int ClearWidth = Shader.PropertyToID("_GrassHistoryClearWidth");
        public static readonly int Lod = Shader.PropertyToID("_GrassHistoryLod");
        public static readonly int PreviousKeys = Shader.PropertyToID("_GrassPreviousRootKeys");
        public static readonly int PreviousRoots = Shader.PropertyToID("_GrassPreviousRoots");
        public static readonly int PreviousMask = Shader.PropertyToID("_GrassMotionHashMask");
        public static readonly int Valid = Shader.PropertyToID("_GrassMotionHistoryValid");
        public static readonly int ExplicitTime = Shader.PropertyToID("_GrassUseExplicitTime");
        public static readonly int Time = Shader.PropertyToID("_GrassTime");
        public static readonly int PreviousDimensions = Shader.PropertyToID("_GrassPreviousDimensions");
        public static readonly int PreviousShaping = Shader.PropertyToID("_GrassPreviousShaping");
        public static readonly int PreviousRanges = Shader.PropertyToID("_GrassPreviousRanges");
        public static readonly int PreviousWindST = Shader.PropertyToID("_GrassPreviousWindST");
        public static readonly int PreviousWindMotion = Shader.PropertyToID("_GrassPreviousWindMotion");
        public static readonly int PreviousCameraPosition = Shader.PropertyToID("_GrassPreviousCameraPosition");
        public static readonly int PreviousCameraForward = Shader.PropertyToID("_GrassPreviousCameraForward");
        public static readonly int PreviousCameraRight = Shader.PropertyToID("_GrassPreviousCameraRight");
        public static readonly int PreviousProjection = Shader.PropertyToID("_GrassPreviousProjection");
        public static readonly int PreviousMap = Shader.PropertyToID("_GrassPreviousMap");
        public static readonly int PreviousSlope = Shader.PropertyToID("_GrassPreviousSlope");
        public static readonly int PreviousWind = Shader.PropertyToID("_GrassPreviousWind");
        public static readonly int Source = Shader.PropertyToID("_GrassHistorySource");
        public static readonly int BlitScaleBias = Shader.PropertyToID("_BlitScaleBias");
        public static readonly int Slope = Shader.PropertyToID("_GrassSlopeRT");
        public static readonly int Wind = Shader.PropertyToID("_WindTexture");
        public static readonly int Center = Shader.PropertyToID("_CenterPos");
        public static readonly int Distance = Shader.PropertyToID("_DrawDistance");
        public static readonly int Padding = Shader.PropertyToID("_TextureUpdateThreshold");
    }

    private readonly Dictionary<Camera, CameraHistory> cameras = new Dictionary<Camera, CameraHistory>();
    private ComputeShader historyShader;
    private Material copyMaterial;
    private int clearKernel;
    private int buildKernel;
    private int captureKernel;
    private int storeKernel;
    private bool warnedResources;
    private bool warnedCapacity;
    private bool warnedSnapshotFormat;
    private bool disposed;
    private bool unormSnapshotsSupported;
    private bool srgbSnapshotsSupported;
    private bool rg32SnapshotsSupported;
    private bool rgba32SnapshotsSupported;

    /// <summary>
    /// URP does not expose its complete motion-consumer summary publicly. Auto
    /// checks the exposed built-in consumers; Always requests data for custom ones.
    /// </summary>
    public static bool IsRequested(Camera camera, bool postProcessEnabled,
        AntialiasingMode antialiasing, int msaaSamples, Settings settings)
    {
        if (!camera || settings == null || settings.mode == Mode.Off || camera.stereoEnabled ||
            !camera.TryGetComponent(out UniversalAdditionalCameraData additional) ||
            additional.renderType != CameraRenderType.Base || !(additional.scriptableRenderer is UniversalRenderer))
            return false;
        if (settings.mode == Mode.Always)
            return true;
        if (!postProcessEnabled || msaaSamples > 1)
            return false;
        if (antialiasing == AntialiasingMode.TemporalAntiAliasing && !camera.allowDynamicResolution &&
            (additional.cameraStack == null || additional.cameraStack.Count == 0))
            return true;

        MotionBlur blur = VolumeManager.instance.stack.GetComponent<MotionBlur>();
        return blur != null && blur.IsActive() && blur.mode.value == MotionBlurMode.CameraAndObjects;
    }

    /// <summary>
    /// Each root has at least two uint hash slots on average. Two compact float4
    /// snapshots preserve the previous frame through repeated camera renders.
    /// The total root memory is 4 * tableSize + 32 * capacity bytes, plus counts.
    /// Texture snapshots contain only wind and interaction, not the color maps.
    /// </summary>
    public static int HistoryTableSize(int rootCapacity)
    {
        if (rootCapacity < 1 || rootCapacity > 16000000)
            throw new ArgumentOutOfRangeException(nameof(rootCapacity));
        return Mathf.NextPowerOfTwo(rootCapacity * 2);
    }

    /// <summary>
    /// Check readiness before asking URP to produce motion/depth inputs. A custom
    /// forward-only material cannot use grass motion and must release old history.
    /// This creates shared helper resources, never per-camera history buffers.
    /// </summary>
    public bool PrepareCamera(Camera camera, Material bladeMaterial)
    {
        if (disposed || !camera || !bladeMaterial || bladeMaterial.FindPass("GrassMotionVectors") < 0 ||
            !EnsureResources())
        {
            Release(camera);
            return false;
        }
        return true;
    }

    public void Record(RenderGraph graph, ContextContainer frameData,
        BufferHandle positionsHandle, BufferHandle countsHandle, BufferHandle argumentsHandle,
        GraphicsBuffer positions, GraphicsBuffer counts, GraphicsBuffer arguments,
        Mesh[] lodMeshes, Material bladeMaterial, MaterialPropertyBlock[] drawProperties,
        TextureHandle[] vertexTextureHandles, int[] lodOffsets, int[] lodCapacities, float spacing)
    {
        UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
        UniversalResourceData resources = frameData.Get<UniversalResourceData>();
        Camera camera = cameraData.camera;
        if (disposed || !camera || camera.stereoEnabled || !resources.motionVectorColor.IsValid() ||
            !resources.motionVectorDepth.IsValid() || positions == null || counts == null || arguments == null ||
            !positionsHandle.IsValid() || !countsHandle.IsValid() || !argumentsHandle.IsValid() ||
            !bladeMaterial || lodMeshes == null || lodMeshes.Length != 3 || drawProperties == null || drawProperties.Length != 3 ||
            lodOffsets == null || lodOffsets.Length < 4 || lodCapacities == null || lodCapacities.Length < 4 ||
            vertexTextureHandles == null || vertexTextureHandles.Length < 5)
        {
            Release(camera);
            return;
        }
        for (int lod = 0; lod < 3; lod++)
            if (!lodMeshes[lod] || drawProperties[lod] == null)
            {
                Release(camera);
                return;
            }

        if (!PrepareCamera(camera, bladeMaterial))
            return;
        int shaderPass = bladeMaterial.FindPass("GrassMotionVectors");
        Texture slope = drawProperties[0].GetTexture(Id.Slope);
        Texture wind = drawProperties[0].GetTexture(Id.Wind);
        if (!slope || !wind || slope.dimension != TextureDimension.Tex2D || wind.dimension != TextureDimension.Tex2D)
        {
            Release(camera);
            return;
        }
        if (!TryGetSnapshotFormats(camera, slope, wind, out GraphicsFormat slopeFormat,
                out GraphicsFormat windFormat))
            return;

        if (!cameras.TryGetValue(camera, out CameraHistory history))
        {
            history = new CameraHistory();
            cameras.Add(camera, history);
        }
        int capacity = lodCapacities[0] + lodCapacities[1] + lodCapacities[2];
        if (capacity < 1 || capacity > 16000000 ||
            (long)capacity * 16 > SystemInfo.maxGraphicsBufferSize ||
            (long)HistoryTableSize(capacity) * 4 > SystemInfo.maxGraphicsBufferSize)
        {
            if (!warnedCapacity)
                Debug.LogWarning("Grass motion history exceeds this device's maximum graphics-buffer size. Reduce Max Blade Count to enable grass motion vectors.");
            warnedCapacity = true;
            Release(camera);
            return;
        }
        history.EnsureAllocation(capacity, slope, wind, slopeFormat, windFormat);
        int frameNumber = Time.frameCount;
        bool newFrame = history.RenderedFrame != frameNumber;
        // A repeat uses the same previous snapshot and hash as the first render.
        // The latest snapshot already contains that first render's current roots.
        int previousIndex = newFrame ? history.LatestIndex : 1 - history.LatestIndex;
        int destinationIndex = 1 - previousIndex;
        RootSnapshot previousSnapshot = history.Snapshots[previousIndex];
        RootSnapshot destination = history.Snapshots[destinationIndex];
        bool valid = previousSnapshot.FrameNumber != -1 && previousSnapshot.FrameNumber == frameNumber - 1 &&
            previousSnapshot.Material == bladeMaterial && previousSnapshot.Spacing == spacing &&
            previousSnapshot.TargetWidth == cameraData.scaledWidth && previousSnapshot.TargetHeight == cameraData.scaledHeight &&
            previousSnapshot.CameraRect == camera.rect && (newFrame || history.PreviousValid);
        FrameState current = FrameState.Capture(cameraData, bladeMaterial, drawProperties[0]);
        FrameState previous = valid ? previousSnapshot.Frame : current;
        BufferHandle keysHandle = graph.ImportBuffer(history.Keys);
        BufferHandle rootsHandle = graph.ImportBuffer(previousSnapshot.Roots);
        BufferHandle dispatchHandle = graph.ImportBuffer(history.DispatchArguments);
        TextureHandle previousSlope = graph.ImportTexture(previousSnapshot.Slope);
        TextureHandle previousWind = graph.ImportTexture(previousSnapshot.Wind);

        if (newFrame && valid)
            RecordHashBuild(graph, history, previousSnapshot, rootsHandle, keysHandle, dispatchHandle);

        for (int lod = 0; lod < 3; lod++)
        {
            MaterialPropertyBlock properties = drawProperties[lod];
            properties.SetInteger(Id.ExplicitTime, 1);
            properties.SetFloat(Id.Time, current.WindMotion.w);
            properties.SetInteger(Id.Valid, valid ? 1 : 0);
            properties.SetInteger(Id.PreviousMask, history.Keys.count - 1);
            properties.SetBuffer(Id.PreviousKeys, history.Keys);
            properties.SetBuffer(Id.PreviousRoots, previousSnapshot.Roots);
            properties.SetTexture(Id.PreviousSlope, previousSnapshot.Slope.rt);
            properties.SetTexture(Id.PreviousWind, previousSnapshot.Wind.rt);
            previous.Bind(properties);
        }

        using (IRasterRenderGraphBuilder builder = graph.AddRasterRenderPass<MotionPass>("Grass Motion Vectors", out MotionPass pass))
        {
            pass.Meshes = lodMeshes;
            pass.Material = bladeMaterial;
            pass.Properties = drawProperties;
            pass.Arguments = arguments;
            pass.ShaderPass = shaderPass;
            builder.UseBuffer(positionsHandle, AccessFlags.Read);
            builder.UseBuffer(argumentsHandle, AccessFlags.Read);
            builder.UseBuffer(keysHandle, AccessFlags.Read);
            builder.UseBuffer(rootsHandle, AccessFlags.Read);
            builder.UseTexture(previousSlope, AccessFlags.Read);
            builder.UseTexture(previousWind, AccessFlags.Read);
            for (int i = 0; i < vertexTextureHandles.Length; i++)
                if (vertexTextureHandles[i].IsValid())
                    builder.UseTexture(vertexTextureHandles[i], AccessFlags.Read);
            builder.SetRenderAttachment(resources.motionVectorColor, 0, AccessFlags.ReadWrite);
            builder.SetRenderAttachmentDepth(resources.motionVectorDepth, AccessFlags.ReadWrite);
            builder.SetRenderFunc(static (MotionPass data, RasterGraphContext context) =>
            {
                for (int lod = 0; lod < 3; lod++)
                    context.cmd.DrawMeshInstancedIndirect(data.Meshes[lod], 0, data.Material, data.ShaderPass,
                        data.Arguments, lod * GraphicsBuffer.IndirectDrawIndexedArgs.size, data.Properties[lod]);
            });
        }

        if (!newFrame)
            return;
        RecordSnapshot(graph, vertexTextureHandles[2], graph.ImportTexture(destination.Slope), slope,
            slopeFormat, "Grass Store Interaction History");
        RecordSnapshot(graph, vertexTextureHandles[4], graph.ImportTexture(destination.Wind), wind,
            windFormat, "Grass Store Wind History");
        using (IComputeRenderGraphBuilder builder = graph.AddComputePass<HistoryPass>("Grass Store Root History", out HistoryPass pass))
        {
            pass.Shader = historyShader;
            pass.CaptureKernel = captureKernel;
            pass.StoreKernel = storeKernel;
            pass.Positions = positions;
            pass.Counts = counts;
            pass.Roots = destination.Roots;
            pass.SnapshotCounts = destination.Counts;
            pass.Dispatch = history.DispatchArguments;
            // Each pooled pass owns its layout until execution. A second record
            // for this camera cannot overwrite a pending pass's metadata, and
            // warmed passes need no new arrays on subsequent frames.
            pass.CaptureLayout(lodOffsets, lodCapacities, lodMeshes);
            pass.History = history;
            pass.Snapshot = destination;
            pass.SnapshotIndex = destinationIndex;
            pass.PreviousValid = valid;
            pass.Frame = current;
            pass.FrameNumber = frameNumber;
            pass.Material = bladeMaterial;
            pass.Spacing = spacing;
            pass.TargetWidth = cameraData.scaledWidth;
            pass.TargetHeight = cameraData.scaledHeight;
            pass.CameraRect = camera.rect;
            builder.UseBuffer(positionsHandle, AccessFlags.Read);
            builder.UseBuffer(countsHandle, AccessFlags.Read);
            builder.UseBuffer(graph.ImportBuffer(destination.Roots), AccessFlags.Write);
            builder.UseBuffer(graph.ImportBuffer(destination.Counts), AccessFlags.Write);
            builder.UseBuffer(dispatchHandle, AccessFlags.ReadWrite);
            // History writes are required for the next camera frame.
            builder.AllowPassCulling(false);
            builder.SetRenderFunc(static (HistoryPass data, ComputeGraphContext context) =>
            {
                ComputeCommandBuffer cmd = context.cmd;
                ComputeShader shader = data.Shader;
                cmd.SetComputeIntParams(shader, Id.Offsets, data.Offsets);
                cmd.SetComputeIntParams(shader, Id.Capacities, data.Capacities);
                cmd.SetComputeIntParams(shader, Id.Rows, data.Rows);
                cmd.SetComputeBufferParam(shader, data.CaptureKernel, Id.Counts, data.Counts);
                cmd.SetComputeBufferParam(shader, data.CaptureKernel, Id.SnapshotCounts, data.SnapshotCounts);
                cmd.SetComputeBufferParam(shader, data.CaptureKernel, Id.Dispatch, data.Dispatch);
                cmd.DispatchCompute(shader, data.CaptureKernel, 1, 1, 1);
                cmd.SetComputeBufferParam(shader, data.StoreKernel, Id.Positions, data.Positions);
                cmd.SetComputeBufferParam(shader, data.StoreKernel, Id.Counts, data.Counts);
                cmd.SetComputeBufferParam(shader, data.StoreKernel, Id.Roots, data.Roots);
                for (int lod = 0; lod < 3; lod++)
                {
                    cmd.SetComputeIntParam(shader, Id.Lod, lod);
                    cmd.DispatchCompute(shader, data.StoreKernel, data.Dispatch, (uint)(lod * 12));
                }
                data.Snapshot.Frame = data.Frame;
                data.Snapshot.FrameNumber = data.FrameNumber;
                data.Snapshot.Material = data.Material;
                data.Snapshot.Spacing = data.Spacing;
                data.Snapshot.TargetWidth = data.TargetWidth;
                data.Snapshot.TargetHeight = data.TargetHeight;
                data.Snapshot.CameraRect = data.CameraRect;
                Array.Copy(data.Offsets, data.Snapshot.Offsets, data.Offsets.Length);
                Array.Copy(data.Capacities, data.Snapshot.Capacities, data.Capacities.Length);
                data.History.RenderedFrame = data.FrameNumber;
                data.History.LatestIndex = data.SnapshotIndex;
                data.History.PreviousValid = data.PreviousValid;
            });
        }
    }

    private void RecordHashBuild(RenderGraph graph, CameraHistory history, RootSnapshot snapshot,
        BufferHandle roots, BufferHandle keys, BufferHandle dispatch)
    {
        using (IComputeRenderGraphBuilder builder = graph.AddComputePass<HashPass>("Grass Index Previous Roots", out HashPass pass))
        {
            pass.Shader = historyShader;
            pass.ClearKernel = clearKernel;
            pass.BuildKernel = buildKernel;
            pass.Roots = snapshot.Roots;
            pass.Counts = snapshot.Counts;
            pass.Keys = history.Keys;
            pass.Dispatch = history.DispatchArguments;
            pass.Offsets = snapshot.Offsets;
            pass.Capacities = snapshot.Capacities;
            builder.UseBuffer(roots, AccessFlags.Read);
            builder.UseBuffer(graph.ImportBuffer(snapshot.Counts), AccessFlags.Read);
            builder.UseBuffer(keys, AccessFlags.Write);
            builder.UseBuffer(dispatch, AccessFlags.ReadWrite);
            builder.SetRenderFunc(static (HashPass data, ComputeGraphContext context) =>
            {
                ComputeCommandBuffer cmd = context.cmd;
                ComputeShader shader = data.Shader;
                int groups = (data.Keys.count + 127) / 128;
                int groupsX = Mathf.Min(groups, 65535);
                int groupsY = (groups + groupsX - 1) / groupsX;
                cmd.SetComputeIntParam(shader, Id.Mask, data.Keys.count - 1);
                cmd.SetComputeIntParam(shader, Id.ClearWidth, groupsX * 128);
                cmd.SetComputeIntParams(shader, Id.Offsets, data.Offsets);
                cmd.SetComputeIntParams(shader, Id.Capacities, data.Capacities);
                cmd.SetComputeBufferParam(shader, data.ClearKernel, Id.Keys, data.Keys);
                cmd.SetComputeBufferParam(shader, data.ClearKernel, Id.Counts, data.Counts);
                cmd.SetComputeBufferParam(shader, data.ClearKernel, Id.Dispatch, data.Dispatch);
                cmd.DispatchCompute(shader, data.ClearKernel, groupsX, groupsY, 1);
                cmd.SetComputeBufferParam(shader, data.BuildKernel, Id.Positions, data.Roots);
                cmd.SetComputeBufferParam(shader, data.BuildKernel, Id.Counts, data.Counts);
                cmd.SetComputeBufferParam(shader, data.BuildKernel, Id.Keys, data.Keys);
                for (int lod = 0; lod < 3; lod++)
                {
                    cmd.SetComputeIntParam(shader, Id.Lod, lod);
                    cmd.DispatchCompute(shader, data.BuildKernel, data.Dispatch, (uint)(lod * 12));
                }
            });
        }
    }

    private void RecordSnapshot(RenderGraph graph, TextureHandle source, TextureHandle destination,
        Texture sourceTexture, GraphicsFormat format, string name)
    {
        if (format == GraphicsFormat.R8G8B8A8_SRGB)
        {
            // Retain the encoded bytes as well as the sampler. Converting sRGB
            // texels to float changes format-dependent hardware filtering even
            // when every texel-center value survives the conversion exactly.
            using (IUnsafeRenderGraphBuilder builder = graph.AddUnsafePass<EncodedSnapshotPass>(name, out var copyPass))
            {
                copyPass.Source = source;
                copyPass.Destination = destination;
                copyPass.Width = sourceTexture.width;
                copyPass.Height = sourceTexture.height;
                builder.UseTexture(source, AccessFlags.Read);
                builder.UseTexture(destination, AccessFlags.WriteAll);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (EncodedSnapshotPass data, UnsafeGraphContext context) =>
                {
                    // The region API adjusts logical source dimensions for its
                    // active mip limit. Mip 0 denotes the currently loaded GPU mip.
                    context.cmd.CopyTexture(data.Source, 0, 0, 0, 0, data.Width, data.Height,
                        data.Destination, 0, 0, 0, 0);
                });
            }
            return;
        }
        using (IRasterRenderGraphBuilder builder = graph.AddRasterRenderPass<SnapshotPass>(name, out SnapshotPass pass))
        {
            pass.Source = source;
            pass.Material = copyMaterial;
            builder.UseTexture(source, AccessFlags.Read);
            builder.SetRenderAttachment(destination, 0, AccessFlags.WriteAll);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc(static (SnapshotPass data, RasterGraphContext context) =>
            {
                MaterialPropertyBlock properties = context.renderGraphPool.GetTempMaterialPropertyBlock();
                properties.SetTexture(Id.Source, data.Source);
                properties.SetVector(Id.BlitScaleBias, new Vector4(1f, 1f, 0f, 0f));
                context.cmd.DrawProcedural(Matrix4x4.identity, data.Material, 0, MeshTopology.Triangles, 3, 1, properties);
            });
        }
    }

    private bool EnsureResources()
    {
        if (historyShader && copyMaterial)
            return true;
        historyShader = Resources.Load<ComputeShader>("InfiniteGrassMotionHistory");
        Shader shader = Resources.Load<Shader>("InfiniteGrassMotionCopy");
        unormSnapshotsSupported = SupportsSnapshotFormat(GraphicsFormat.R8G8B8A8_UNorm);
        srgbSnapshotsSupported = SupportsSnapshotFormat(GraphicsFormat.R8G8B8A8_SRGB);
        rg32SnapshotsSupported = SupportsSnapshotFormat(GraphicsFormat.R32G32_SFloat);
        rgba32SnapshotsSupported = SupportsSnapshotFormat(GraphicsFormat.R32G32B32A32_SFloat);
        if (!historyShader || !shader || !shader.isSupported || !historyShader.HasKernel("ClearHistory") ||
            !historyShader.HasKernel("BuildHistory") || !historyShader.HasKernel("CaptureCounts") || !historyShader.HasKernel("StoreRoots") ||
            !(unormSnapshotsSupported || srgbSnapshotsSupported || rg32SnapshotsSupported || rgba32SnapshotsSupported))
        {
            if (!warnedResources)
                Debug.LogWarning("Grass motion vectors require the InfiniteGrassMotionHistory compute shader, InfiniteGrassMotionCopy shader, and renderable snapshot textures supporting linear sampling.");
            warnedResources = true;
            return false;
        }
        clearKernel = historyShader.FindKernel("ClearHistory");
        buildKernel = historyShader.FindKernel("BuildHistory");
        captureKernel = historyShader.FindKernel("CaptureCounts");
        storeKernel = historyShader.FindKernel("StoreRoots");
        copyMaterial = CoreUtils.CreateEngineMaterial(shader);
        return true;
    }

    private static bool SupportsSnapshotFormat(GraphicsFormat format) =>
        SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Render) &&
        SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Sample) &&
        SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Linear);

    private bool TryGetSnapshotFormats(Camera camera, Texture slope, Texture wind,
        out GraphicsFormat slopeFormat, out GraphicsFormat windFormat)
    {
        slopeFormat = SelectSnapshotFormat(slope.graphicsFormat, false,
            unormSnapshotsSupported, rg32SnapshotsSupported, rgba32SnapshotsSupported, srgbSnapshotsSupported);
        windFormat = SelectSnapshotFormat(wind.graphicsFormat, true,
            unormSnapshotsSupported, rg32SnapshotsSupported, rgba32SnapshotsSupported, srgbSnapshotsSupported);
        if (slopeFormat == GraphicsFormat.R8G8B8A8_SRGB && !CanCopyEncodedSnapshot(slope, SystemInfo.copyTextureSupport))
            slopeFormat = GraphicsFormat.None;
        if (windFormat == GraphicsFormat.R8G8B8A8_SRGB && !CanCopyEncodedSnapshot(wind, SystemInfo.copyTextureSupport))
            windFormat = GraphicsFormat.None;
        if (slopeFormat != GraphicsFormat.None && windFormat != GraphicsFormat.None)
            return true;
        if (!warnedSnapshotFormat)
            Debug.LogWarning("Grass motion history cannot preserve these deformation textures on this device. It requires matching RGBA8 UNorm or sRGB, or 32-bit floating-point snapshot formats supporting rendering and linear sampling. RGBA8 sRGB additionally requires an exact texture copy from a single-sample source.");
        warnedSnapshotFormat = true;
        Release(camera);
        return false;
    }

    private static GraphicsFormat SelectSnapshotFormat(GraphicsFormat sourceFormat, bool wind,
        bool unormSupported, bool rg32Supported, bool rgba32Supported, bool srgbSupported)
    {
        // Match RGBA8 encoding to preserve its format-dependent filtering too.
        // Other inputs retain their sampled texel values in float storage.
        // Half storage changes even ordinary 8-bit UNorm values and creates
        // motion on stationary blades. Wind needs only its two sampled channels.
        if (sourceFormat == GraphicsFormat.R8G8B8A8_SRGB)
            return srgbSupported ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.None;
        if (sourceFormat == GraphicsFormat.R8G8B8A8_UNorm && unormSupported)
            return GraphicsFormat.R8G8B8A8_UNorm;
        if (wind && rg32Supported)
            return GraphicsFormat.R32G32_SFloat;
        return rgba32Supported ? GraphicsFormat.R32G32B32A32_SFloat : GraphicsFormat.None;
    }

    private static bool CanCopyEncodedSnapshot(Texture source, CopyTextureSupport support)
    {
        if (!source || source.dimension != TextureDimension.Tex2D || (support & CopyTextureSupport.Basic) == 0)
            return false;
        if (source is RenderTexture rendered)
            return rendered.IsCreated() && rendered.antiAliasing == 1;
        return source is Texture2D && (support & CopyTextureSupport.TextureToRT) != 0;
    }

    public void ResetHistory(Camera camera)
    {
        if (camera && cameras.TryGetValue(camera, out CameraHistory history))
            history.ResetHistory();
    }

    public void Release(Camera camera)
    {
        if (ReferenceEquals(camera, null) || !cameras.TryGetValue(camera, out CameraHistory history))
            return;
        history.Dispose();
        cameras.Remove(camera);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        foreach (CameraHistory history in cameras.Values)
            history.Dispose();
        cameras.Clear();
        CoreUtils.Destroy(copyMaterial);
        copyMaterial = null;
        historyShader = null;
    }

    private struct FrameState
    {
        public Vector4 Dimensions, Shaping, Ranges, WindST, WindMotion;
        public Vector4 CameraPosition, CameraForward, CameraRight, Projection, Map;

        public static FrameState Capture(UniversalCameraData cameraData, Material material, MaterialPropertyBlock properties)
        {
            Camera camera = cameraData.camera;
            Matrix4x4 view = cameraData.GetViewMatrix();
            Vector4 widthRange = material.GetVector("_ExpandDistantGrassRange");
            Vector4 windScroll = material.GetVector("_WindScroll");
            Vector2 windScale = material.GetTextureScale("_WindTexture");
            Vector2 windOffset = material.GetTextureOffset("_WindTexture");
            Vector4 center = properties.GetVector(Id.Center);
            float extent = Mathf.Max(0.001f, properties.GetFloat(Id.Distance) + properties.GetFloat(Id.Padding));
            float time = Application.isPlaying ? Time.time : Time.realtimeSinceStartup;
            // Match ScriptableRenderer.SetPerCameraShaderVariables exactly;
            // scaledHeight alone omits hardware dynamic resolution.
            float pixelHeight = cameraData.cameraTargetDescriptor.height *
                (camera.allowDynamicResolution ? ScalableBufferManager.heightScaleFactor : 1f);
            return new FrameState
            {
                Dimensions = new Vector4(material.GetFloat("_GrassWidth"), material.GetFloat("_GrassHeight"), material.GetFloat("_GrassWidthRandomness"), material.GetFloat("_GrassHeightRandomness")),
                Shaping = new Vector4(material.GetFloat("_GrassCurving"), material.GetFloat("_ExpandDistantGrassWidth"), material.GetFloat("_SubdivisionHeightBoost"), material.GetFloat("_SubdivisionBumpWidth")),
                Ranges = new Vector4(widthRange.x, widthRange.y, material.GetFloat("_SubdivisionDistance"), material.GetFloat("_MinimumPixelWidth")),
                WindST = new Vector4(windScale.x, windScale.y, windOffset.x, windOffset.y),
                WindMotion = new Vector4(windScroll.x, windScroll.y, material.GetFloat("_WindStrength"), time),
                CameraPosition = camera.transform.position,
                CameraForward = -view.GetRow(2),
                CameraRight = view.GetRow(0),
                Projection = new Vector4(pixelHeight, Mathf.Abs(cameraData.GetProjectionMatrix().m11), camera.orthographic ? 1f : 0f, 0f),
                Map = new Vector4(center.x, center.y, 1f / (2f * extent), 0f)
            };
        }

        public void Bind(MaterialPropertyBlock properties)
        {
            properties.SetVector(Id.PreviousDimensions, Dimensions);
            properties.SetVector(Id.PreviousShaping, Shaping);
            properties.SetVector(Id.PreviousRanges, Ranges);
            properties.SetVector(Id.PreviousWindST, WindST);
            properties.SetVector(Id.PreviousWindMotion, WindMotion);
            properties.SetVector(Id.PreviousCameraPosition, CameraPosition);
            properties.SetVector(Id.PreviousCameraForward, CameraForward);
            properties.SetVector(Id.PreviousCameraRight, CameraRight);
            properties.SetVector(Id.PreviousProjection, Projection);
            properties.SetVector(Id.PreviousMap, Map);
        }
    }

    private sealed class CameraHistory : IDisposable
    {
        public GraphicsBuffer Keys, DispatchArguments;
        public readonly RootSnapshot[] Snapshots = { new RootSnapshot(), new RootSnapshot() };
        public int RenderedFrame = -1;
        public int LatestIndex;
        public bool PreviousValid;

        public bool EnsureAllocation(int capacity, Texture slope, Texture wind,
            GraphicsFormat slopeFormat, GraphicsFormat windFormat)
        {
            int size = HistoryTableSize(capacity);
            bool changed = Keys == null || !Keys.IsValid() || Keys.count != size;
            if (changed)
            {
                Keys?.Dispose();
                Keys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, size, sizeof(uint));
            }
            if (DispatchArguments == null || !DispatchArguments.IsValid())
            {
                DispatchArguments?.Dispose();
                DispatchArguments = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 9, sizeof(uint));
                changed = true;
            }
            for (int i = 0; i < Snapshots.Length; i++)
                changed |= Snapshots[i].EnsureAllocation(capacity, slope, wind, slopeFormat, windFormat);
            if (changed)
                ResetHistory();
            return changed;
        }

        public void ResetHistory()
        {
            RenderedFrame = -1;
            LatestIndex = 0;
            PreviousValid = false;
            foreach (RootSnapshot snapshot in Snapshots)
                snapshot.FrameNumber = -1;
        }

        public void Dispose()
        {
            Keys?.Dispose();
            DispatchArguments?.Dispose();
            foreach (RootSnapshot snapshot in Snapshots)
                snapshot.Dispose();
            Keys = null;
            DispatchArguments = null;
        }
    }

    private sealed class RootSnapshot : IDisposable
    {
        public GraphicsBuffer Roots, Counts;
        public RTHandle Slope, Wind;
        public int FrameNumber = -1;
        public Material Material;
        public float Spacing;
        public int TargetWidth, TargetHeight;
        public Rect CameraRect;
        public FrameState Frame;
        public readonly int[] Offsets = new int[4];
        public readonly int[] Capacities = new int[4];

        public bool EnsureAllocation(int capacity, Texture slope, Texture wind,
            GraphicsFormat slopeFormat, GraphicsFormat windFormat)
        {
            bool changed = Roots == null || !Roots.IsValid() || Roots.count != capacity;
            if (changed)
            {
                Roots?.Dispose();
                Roots = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, sizeof(float) * 4);
            }
            if (Counts == null || !Counts.IsValid())
            {
                Counts?.Dispose();
                Counts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 3, sizeof(uint));
                changed = true;
            }
            changed |= AllocateTexture(ref Slope, slope, slopeFormat, "Grass Interaction History");
            changed |= AllocateTexture(ref Wind, wind, windFormat, "Grass Wind History");
            return changed;
        }

        private static bool AllocateTexture(ref RTHandle handle, Texture source, GraphicsFormat format, string name)
        {
            // Matching descriptors do not prove that an RT still has GPU contents.
            // Recover from lost/released textures and invalidate both frame snapshots.
            bool lost = handle != null && (!handle.rt || !handle.rt.IsCreated());
            if (lost)
            {
                handle.Release();
                handle = null;
            }
            bool samplerChanged = handle != null && (handle.rt.wrapModeU != source.wrapModeU ||
                handle.rt.wrapModeV != source.wrapModeV || handle.rt.filterMode != source.filterMode);
            // Texture.width/height retain the asset dimensions when quality
            // settings omit high-resolution mips. Match the actual GPU mip 0:
            // upscaling it with a point copy changes later bilinear wind samples.
            int mipLimit = source is Texture2D texture ? texture.activeMipmapLimit : 0;
            var descriptor = new RenderTextureDescriptor(Mathf.Max(1, source.width >> mipLimit),
                Mathf.Max(1, source.height >> mipLimit))
            {
                // Keep graphicsFormat authoritative: the legacy sRGB setter
                // can replace an encoded format in a Gamma project.
                graphicsFormat = format,
                depthStencilFormat = GraphicsFormat.None,
                msaaSamples = 1,
                volumeDepth = 1,
                dimension = TextureDimension.Tex2D,
                useMipMap = false,
                autoGenerateMips = false
            };
            bool changed = RenderingUtils.ReAllocateHandleIfNeeded(ref handle, descriptor, source.filterMode, source.wrapMode, name: name);
            handle.rt.wrapModeU = source.wrapModeU;
            handle.rt.wrapModeV = source.wrapModeV;
            return lost || changed || samplerChanged;
        }

        public void Dispose()
        {
            Roots?.Dispose();
            Counts?.Dispose();
            Slope?.Release();
            Wind?.Release();
            Roots = null;
            Counts = null;
            Slope = null;
            Wind = null;
        }
    }

    private sealed class MotionPass
    {
        public Mesh[] Meshes;
        public Material Material;
        public MaterialPropertyBlock[] Properties;
        public GraphicsBuffer Arguments;
        public int ShaderPass;
    }

    private sealed class SnapshotPass
    {
        public TextureHandle Source;
        public Material Material;
    }

    private sealed class EncodedSnapshotPass
    {
        public TextureHandle Source, Destination;
        public int Width, Height;
    }

    private sealed class HistoryPass
    {
        public ComputeShader Shader;
        public int CaptureKernel, StoreKernel;
        public GraphicsBuffer Positions, Counts, Roots, SnapshotCounts, Dispatch;
        public readonly int[] Offsets = new int[4];
        public readonly int[] Capacities = new int[4];
        public readonly int[] Rows = new int[4];
        public CameraHistory History;
        public RootSnapshot Snapshot;
        public int SnapshotIndex;
        public bool PreviousValid;
        public FrameState Frame;
        public int FrameNumber;
        public Material Material;
        public float Spacing;
        public int TargetWidth, TargetHeight;
        public Rect CameraRect;

        public void CaptureLayout(int[] offsets, int[] capacities, Mesh[] meshes)
        {
            Array.Copy(offsets, Offsets, Offsets.Length);
            Array.Copy(capacities, Capacities, Capacities.Length);
            for (int lod = 0; lod < 3; lod++)
                Rows[lod] = (meshes[lod].vertexCount - 1) / 2;
        }
    }

    private sealed class HashPass
    {
        public ComputeShader Shader;
        public int ClearKernel, BuildKernel;
        public GraphicsBuffer Roots, Counts, Keys, Dispatch;
        public int[] Offsets, Capacities;
    }
}
