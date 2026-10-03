using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>Opt-in, real-camera smoke checks for the generated standalone validation player.</summary>
public sealed class GrassValidationPlayerProbe : MonoBehaviour
{
    public InfiniteGrassRenderer settings;
    public GrassPlacementArea area;
    public UniversalRenderPipelineAsset pipeline;
    public Camera targetCamera;

    private const double StageTimeoutSeconds = 12.0;
    private const double RunTimeoutSeconds = 120.0;
    private const int MaximumScreenshotPixels = 4 * 1024 * 1024;
    private const int MaximumLogEntries = 16;
#if UNITY_ENABLE_CHECKS
    private const bool ChecksEnabled = true;
#else
    private const bool ChecksEnabled = false;
#endif
#if UNITY_INCLUDE_INSTRUMENTATION
    private const bool InstrumentationEnabled = true;
#else
    private const bool InstrumentationEnabled = false;
#endif
    private readonly WaitForEndOfFrame endOfFrame = new WaitForEndOfFrame();
    private SmokeReport report;
    private string outputDirectory;
    private double startedAt;
    private int renderedFrames;
    private bool running, finished, savedState, animateCamera, includeMotion;
    private bool originalSettingsEnabled, originalAreaEnabled, originalPreview, originalContacts;
    private bool originalPostProcessing, originalAllowMsaa;
    private int originalMsaa;
    private float originalScale;
    private float originalWindStrength;
    private GrassMotionVectors.Mode originalMotion;
    private AntialiasingMode originalAntialiasing;
    private UniversalAdditionalCameraData additionalCameraData;
    private Vector3 originalCameraPosition;
    private Vector3 stageCameraStart;
    private float maximumStageCameraDisplacement;
    private Texture originalDensity;
    private Terrain originalTerrain;
    private TerrainLayer originalGroundLayer;
    private Texture originalGroundColor;
    private Rect originalCoverageBounds;
    private Color32[] baselinePixels;
    private Color32[] windPixels, contactPixels;
    private int baselineWidth, baselineHeight;
    private AttachmentObservation attachments;

    public bool IsRunning => running && !finished;
    public int ActiveStage { get; private set; }
    public int RequestedMsaa => pipeline ? pipeline.msaaSampleCount : 1;

    private void Start()
    {
        // No automatic scene changes or application exit in the Editor, including Play Mode.
        string[] arguments = Environment.GetCommandLineArgs();
        if (Application.isEditor || Array.IndexOf(arguments, "-grassSmoke") < 0)
            return;

        includeMotion = Array.IndexOf(arguments, "-grassSmokeMotion") >= 0;
        startedAt = Time.realtimeSinceStartupAsDouble;
        report = new SmokeReport
        {
            unityVersion = Application.unityVersion,
            startedUtc = DateTime.UtcNow.ToString("O"),
            graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
            graphicsDevice = SystemInfo.graphicsDeviceName,
            optionalMotionRequested = includeMotion,
            supportsCompute = SystemInfo.supportsComputeShaders,
            supportsIndirectArguments = SystemInfo.supportsIndirectArgumentsBuffer,
            supportsAsyncReadback = SystemInfo.supportsAsyncGPUReadback,
            checksEnabled = ChecksEnabled, instrumentationEnabled = InstrumentationEnabled
        };
        running = true;
        Application.logMessageReceived += OnLog;
        RenderPipelineManager.endCameraRendering += OnCameraRendered;
        try
        {
            outputDirectory = Path.GetFullPath(OutputArgument(arguments));
            Directory.CreateDirectory(outputDirectory);
            // Invalidate a previous successful run before any scene/device check.
            // A terminated process must leave an incomplete current attempt.
            WriteReport();
            ValidateSceneAndDevice();
            SaveState();
            // The required image and attachment checks must stand on their own
            // with no temporal antialiasing, post processing or motion history.
            additionalCameraData.antialiasing = AntialiasingMode.None;
            additionalCameraData.renderPostProcessing = false;
            targetCamera.allowMSAA = true;
            settings.previewVisibleGrassCount = true;
            StartCoroutine(RunStages());
        }
        catch (Exception error)
        {
            Finish(false, error.ToString());
        }
    }

    private void Update()
    {
        if (!running)
            return;
        if (report.errorCount > 0)
        {
            Finish(false, "Unity logged an error, assertion, or exception during the smoke run.");
            return;
        }
        if (Time.realtimeSinceStartupAsDouble - startedAt > RunTimeoutSeconds)
        {
            Finish(false, "The complete smoke run exceeded its wall-clock deadline.");
            return;
        }
        if (animateCamera && targetCamera)
            targetCamera.transform.position = originalCameraPosition + Vector3.right *
                (0.2f * Mathf.Sin((float)(Time.realtimeSinceStartupAsDouble - startedAt) * 2f));
    }

    private IEnumerator RunStages()
    {
        var stages = new List<Stage>
        {
            new Stage("01-grass-off", CountExpectation.Unchecked, 1, () =>
            {
                settings.enabled = false;
                settings.contactShadows.enabled = false;
                settings.motionVectors.mode = GrassMotionVectors.Mode.Off;
                pipeline.renderScale = 1f;
                area.enabled = true;
            }),
            new Stage("02-grass-on", CountExpectation.Positive, 1, () =>
            {
                settings.enabled = true;
                settings.RefreshGrassData();
            }),
            new Stage("03-wind-no-aa", CountExpectation.Positive, 1, () => { }),
            new Stage("04-camera-pan-no-aa", CountExpectation.Positive, 1, () => animateCamera = true),
            new Stage("05-contacts-off-no-aa", CountExpectation.Positive, 1, () =>
            {
                animateCamera = false;
                targetCamera.transform.position = originalCameraPosition;
                settings.grassMaterial.SetFloat("_WindStrength", 0f);
            }),
            new Stage("06-contacts-on-no-aa", CountExpectation.Positive, 1, () => settings.contactShadows.enabled = true),
            new Stage("07-render-scale-below-no-aa", CountExpectation.Positive, 1, () =>
            {
                settings.grassMaterial.SetFloat("_WindStrength", originalWindStrength);
                pipeline.renderScale = 0.75f;
            }),
            new Stage("08-render-scale-above-no-aa", CountExpectation.Positive, 1, () => pipeline.renderScale = 1.25f),
            new Stage("09-msaa2", CountExpectation.Positive, 2, () => pipeline.renderScale = 1f),
            new Stage("10-msaa4", CountExpectation.Positive, 4, () => pipeline.renderScale = 1f),
            new Stage("11-msaa8", CountExpectation.Positive, 8, () => pipeline.renderScale = 1f),
            new Stage("12-black-mask", CountExpectation.Zero, 1, () =>
                area.ConfigureTexture(originalTerrain, Texture2D.blackTexture, originalGroundLayer, originalGroundColor)),
            new Stage("13-no-sources", CountExpectation.Zero, 1, () => area.enabled = false),
            new Stage("14-restored", CountExpectation.Positive, 1, () =>
            {
                RestoreCoverage();
                area.enabled = true;
            })
        };
        if (includeMotion)
            stages.Add(new Stage("15-optional-motion", CountExpectation.Positive, 1, () =>
            {
                settings.motionVectors.mode = GrassMotionVectors.Mode.Always;
                animateCamera = true;
            }, true));

        foreach (Stage stage in stages)
        {
            ActiveStage++;
            attachments = null;
            stageCameraStart = targetCamera.transform.position;
            maximumStageCameraDisplacement = 0f;
            var observation = new StageObservation
            {
                name = stage.Name, countExpectation = stage.Count.ToString(),
                requestedMsaa = stage.Samples, optional = stage.Optional, status = "failed",
                activationFrame = Time.frameCount
            };
            report.stages.Add(observation);
            try
            {
                pipeline.msaaSampleCount = stage.Samples;
                stage.Activate();
            }
            catch (Exception error)
            {
                Finish(false, stage.Name + ": " + error);
            }
            if (finished)
                yield break;

            int firstFrame = renderedFrames;
            double began = Time.realtimeSinceStartupAsDouble;
            double matchingSince = -1.0;
            bool observed = false;
            while (!finished && Time.realtimeSinceStartupAsDouble - began < StageTimeoutSeconds)
            {
                // Count readbacks are throttled to four per second. Allow both
                // rendered frames and elapsed time, then retain the expected
                // population for at least two ordinary readback intervals.
                double now = Time.realtimeSinceStartupAsDouble;
                bool countMatches = CountMatches(stage.Count);
                if (renderedFrames - firstFrame >= 6 && now - began >= 1.0 && countMatches &&
                    attachments != null && attachments.requestedMsaa == stage.Samples &&
                    Time.frameCount - attachments.frame <= 1)
                {
                    if (matchingSince < 0.0)
                        matchingSince = now;
                    if (stage.Count == CountExpectation.Unchecked || now - matchingSince >= 0.5)
                    {
                        observed = true;
                        break;
                    }
                }
                else
                    matchingSince = -1.0;
                yield return null;
            }
            if (finished)
                yield break;
            if (!observed)
            {
                observation.visibleGrass = settings.VisibleGrassCount;
                observation.overflowGrass = settings.OverflowGrassCount;
                observation.cameraFrames = renderedFrames - firstFrame;
                observation.seconds = Time.realtimeSinceStartupAsDouble - began;
                Finish(false, stage.Name + " timed out waiting for rendered frames, current attachment evidence and " + stage.Count + " grass counts.");
                yield break;
            }

            // This captures the displayed player frame. The camera never renders
            // into a probe-owned RenderTexture, including the direct-output stage.
            yield return endOfFrame;
            if (finished)
                yield break;
            try
            {
                if (targetCamera.targetTexture != null)
                    throw new InvalidOperationException("The validation camera stopped using normal screen output.");
                observation.cameraFrames = renderedFrames - firstFrame;
                observation.seconds = Time.realtimeSinceStartupAsDouble - began;
                observation.visibleGrass = settings.VisibleGrassCount;
                observation.overflowGrass = settings.OverflowGrassCount;
                observation.requestedRenderScale = pipeline.renderScale;
                observation.requestedMsaa = pipeline.msaaSampleCount;
                observation.contactsRequested = settings.contactShadows.enabled;
                observation.postProcessingRequested = additionalCameraData.renderPostProcessing;
                observation.cameraPanRequested = animateCamera;
                observation.maximumCameraDisplacement = maximumStageCameraDisplacement;
                observation.windStrength = settings.grassMaterial.GetFloat("_WindStrength");
                observation.cameraPosition = targetCamera.transform.position;
                observation.cameraRotation = targetCamera.transform.rotation;
                observation.motionModeRequested = settings.motionVectors.mode.ToString();
                observation.cameraTargetsScreen = true;
                observation.capturedFrame = Time.frameCount;
                observation.attachments = attachments;
                if (attachments == null || Time.frameCount - attachments.frame > 1 ||
                    attachments.requestedMsaa != stage.Samples)
                    throw new InvalidOperationException("No current attachment evidence was executed for this stage.");
                if (attachments.antialiasing != AntialiasingMode.None.ToString() ||
                    additionalCameraData.renderPostProcessing ||
                    (!stage.Optional && settings.motionVectors.mode != GrassMotionVectors.Mode.Off))
                    throw new InvalidOperationException("Baseline checks require camera antialiasing None, post processing off and motion history off.");
                bool unsupported = VerifyAttachments(attachments, stage.Samples, pipeline.renderScale,
                    targetCamera.pixelWidth, targetCamera.pixelHeight);
                if (animateCamera && maximumStageCameraDisplacement < 0.08f)
                    throw new InvalidOperationException("The camera-pan stage did not observe sufficient camera travel while rendering.");
                if (!CountMatches(stage.Count))
                    throw new InvalidOperationException("Grass counts changed away from the expected state before capture.");
                if (observation.overflowGrass != 0 && stage.Count != CountExpectation.Unchecked)
                    throw new InvalidOperationException("The bounded validation scene overflowed its grass capacity.");
                Capture(observation);
                observation.passed = !unsupported;
                observation.status = unsupported ? "unsupported" : "passed";
                if (unsupported)
                {
                    observation.skipReason = "The graphics device supports " + attachments.hardwareSupportedMsaa +
                        " samples for the observed color/depth formats; " + stage.Samples + "x was requested.";
                    report.unsupportedMsaaStages++;
                }
            }
            catch (Exception error)
            {
                Finish(false, stage.Name + ": " + error);
            }
            if (finished)
                yield break;
        }
        report.msaaCoverageComplete = report.unsupportedMsaaStages == 0;
        Finish(report.errorCount == 0, report.errorCount == 0 ? null : "Unity reported rendering errors.");
    }

    private static bool VerifyAttachments(AttachmentObservation sample, int requested, float scale, int width, int height)
    {
        if (sample.colorSamples < 1 || sample.colorSamples != sample.depthSamples ||
            sample.colorWidth < 1 || sample.colorHeight < 1 ||
            sample.colorWidth != sample.depthWidth || sample.colorHeight != sample.depthHeight)
            throw new InvalidOperationException("The grass color/depth attachments have incompatible dimensions or sample counts.");
        int scaledWidth = Mathf.Max(1, (int)(width * scale));
        int scaledHeight = Mathf.Max(1, (int)(height * scale));
        if (sample.cameraWidth != width || sample.cameraHeight != height ||
            Mathf.Abs(sample.cameraRenderScale - scale) > 0.00001f ||
            sample.cameraScaledWidth != scaledWidth || sample.cameraScaledHeight != scaledHeight ||
            sample.cameraDescriptorWidth != scaledWidth || sample.cameraDescriptorHeight != scaledHeight ||
            sample.colorViewportWidth != scaledWidth || sample.colorViewportHeight != scaledHeight ||
            sample.depthViewportWidth != scaledWidth || sample.depthViewportHeight != scaledHeight)
            throw new InvalidOperationException("The executed camera/attachment viewports do not match the requested render scale.");
        if (sample.hardwareSupportedMsaa < 1 || sample.hardwareSupportedMsaa > requested ||
            (sample.hardwareSupportedMsaa & (sample.hardwareSupportedMsaa - 1)) != 0)
            throw new InvalidOperationException("The device returned an invalid supported MSAA count.");
        if (sample.colorSamples != sample.hardwareSupportedMsaa)
            throw new InvalidOperationException("The grass attachments use " + sample.colorSamples +
                " samples, but the device supports " + sample.hardwareSupportedMsaa + " for the requested " + requested + "x stage.");
        return sample.hardwareSupportedMsaa < requested;
    }

    public void RecordAttachments(int stage, AttachmentObservation observation)
    {
        // Ignore any callback recorded for an earlier stage. Values are published
        // only by the executed raster pass, never by a requested URP asset setting.
        if (!IsRunning || stage != ActiveStage || observation == null)
            return;
        attachments = observation;
    }

    private bool CountMatches(CountExpectation expectation)
    {
        return expectation == CountExpectation.Unchecked ||
            (expectation == CountExpectation.Positive ? settings.VisibleGrassCount > 0 : settings.VisibleGrassCount == 0);
    }

    private void ValidateSceneAndDevice()
    {
        if (!ChecksEnabled || !InstrumentationEnabled)
            throw new InvalidOperationException("The diagnostic smoke player requires the Checked managed code variant with checks and instrumentation enabled.");
        var renderGraph = GraphicsSettings.GetRenderPipelineSettings<RenderGraphGlobalSettings>();
        report.renderGraphValidityChecks = renderGraph != null && renderGraph.enableValidityChecks;
        if (!report.renderGraphValidityChecks)
            throw new InvalidOperationException("RenderGraph validity checks must also be enabled in Graphics settings.");
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || !report.supportsCompute ||
            !report.supportsIndirectArguments || !report.supportsAsyncReadback || !SystemInfo.supportsInstancing)
            throw new InvalidOperationException("Smoke checks require a real graphics device with compute, indirect draws, instancing, and async GPU readback. Do not use -nographics.");
        if (!settings || !area || !pipeline || !targetCamera || !targetCamera.isActiveAndEnabled ||
            targetCamera.targetTexture != null || targetCamera.stereoEnabled || targetCamera.targetDisplay != 0)
            throw new InvalidOperationException("Assign the generated settings, mask area, URP asset, and active single-view screen camera.");
        if (targetCamera.rect != new Rect(0f, 0f, 1f, 1f) || targetCamera.allowDynamicResolution)
            throw new InvalidOperationException("Smoke comparisons require a full-screen camera with dynamic resolution disabled.");
        if (GraphicsSettings.currentRenderPipeline != pipeline ||
            !targetCamera.TryGetComponent(out additionalCameraData) ||
            additionalCameraData.renderType != CameraRenderType.Base || !(additionalCameraData.scriptableRenderer is UniversalRenderer))
            throw new InvalidOperationException("The supplied asset and camera must use the active URP Universal Renderer.");
        if (!settings.grassMaterial || settings.grassMaterial.FindPass("GrassForward") < 0 ||
            settings.grassMaterial.FindPass("GrassContactDepth") < 0 || settings.grassMaterial.FindPass("GrassMotionVectors") < 0 ||
            settings.contactShadows == null || settings.motionVectors == null)
            throw new InvalidOperationException("The scene needs the package forward, contact-depth, and motion-vector blade passes.");
        Texture2D wind = settings.grassMaterial.GetTexture("_WindTexture") as Texture2D;
        Vector4 windScroll = settings.grassMaterial.GetVector("_WindScroll");
        report.windStrength = settings.grassMaterial.GetFloat("_WindStrength");
        report.windScrollSpeed = new Vector2(windScroll.x, windScroll.y).magnitude;
        report.windTextureWidth = wind ? wind.width : 0;
        report.windTextureHeight = wind ? wind.height : 0;
        if (wind && wind.isReadable && (long)wind.width * wind.height <= 65536)
        {
            Color32[] windPixels = wind.GetPixels32();
            for (int i = 1; i < windPixels.Length; i++)
                if (windPixels[i].r != windPixels[0].r || windPixels[i].g != windPixels[0].g)
                {
                    report.windTextureVaries = true;
                    break;
                }
        }
        if (!wind || wind.width < 2 || wind.height < 2 || !report.windTextureVaries ||
            !(report.windStrength > 0f) || float.IsInfinity(report.windStrength) ||
            !(report.windScrollSpeed > 0f) || float.IsInfinity(report.windScrollSpeed))
            throw new InvalidOperationException("Smoke checks need a bounded readable wind texture with changing RG values, positive strength and nonzero scroll speed.");
        if (area.Shape != GrassPlacementShape.Texture || !area.UsesTerrainBounds || area.DensityAsset ||
            !area.Terrain || !area.DensityTexture)
            throw new InvalidOperationException("The generated smoke scene must use a saved external Terrain coverage mask.");
        if (Screen.width < 320 || Screen.height < 240 || (long)Screen.width * Screen.height > MaximumScreenshotPixels)
            throw new InvalidOperationException("Use a bounded player window of at least 320x240 and at most 4,194,304 pixels (the generated default is 960x540).");
    }

    private void Capture(StageObservation observation)
    {
        Texture2D image = null;
        try
        {
            if ((long)Screen.width * Screen.height > MaximumScreenshotPixels)
                throw new InvalidOperationException("The player window exceeded the screenshot size limit.");
            image = ScreenCapture.CaptureScreenshotAsTexture(1);
            if (!image || image.width < 1 || image.height < 1)
                throw new InvalidOperationException("Screen capture returned no image.");
            observation.imageWidth = image.width;
            observation.imageHeight = image.height;
            Color32[] pixels = image.GetPixels32();
            byte[] png = image.EncodeToPNG();
            if (png == null || png.Length == 0)
                throw new InvalidOperationException("PNG encoding returned no data.");
            observation.screenshot = observation.name + ".png";
            File.WriteAllBytes(Path.Combine(outputDirectory, observation.screenshot), png);

            if (observation.name == "01-grass-off")
            {
                baselinePixels = pixels;
                baselineWidth = image.width;
                baselineHeight = image.height;
            }
            else if (observation.name == "02-grass-on")
            {
                ImageDifference difference = ComparePixels(baselinePixels, pixels, image.width, image.height, 8);
                report.comparedPixels = difference.compared;
                report.changedGrassPixels = difference.changed;
                if (report.changedGrassPixels < 32)
                    throw new InvalidOperationException("Positive GPU counts did not produce a measurable off/on grass image difference.");
                windPixels = pixels;
            }
            else if (observation.name == "03-wind-no-aa")
            {
                report.changedWindPixels = ComparePixels(windPixels, pixels, image.width, image.height, 8).changed;
                windPixels = null;
                if (report.changedWindPixels < 8)
                    throw new InvalidOperationException("The fixed-camera wind stage produced no measurable blade motion.");
            }
            else if (observation.name == "05-contacts-off-no-aa")
                contactPixels = pixels;
            else if (observation.name == "06-contacts-on-no-aa")
            {
                ImageDifference difference = ComparePixels(contactPixels, pixels, image.width, image.height, 2);
                report.darkenedContactPixels = difference.darkened;
                report.brightenedContactPixels = difference.brightened;
                contactPixels = null;
                if (report.darkenedContactPixels < 8)
                    throw new InvalidOperationException("Enabling contacts with fixed camera and frozen wind produced no measurable darkening.");
                if (report.brightenedContactPixels != 0)
                    throw new InvalidOperationException("The multiplicative contact blend unexpectedly brightened the controlled image.");
            }
            else if (observation.name == "12-black-mask" || observation.name == "13-no-sources")
            {
                int changed = ComparePixels(baselinePixels, pixels, image.width, image.height, 2).changed;
                if (observation.name == "12-black-mask") report.changedBlackMaskPixels = changed;
                else report.changedNoSourcesPixels = changed;
                if (changed != 0)
                    throw new InvalidOperationException("Zero grass counts did not restore the grass-disabled image.");
            }
        }
        finally
        {
            if (image)
                Destroy(image);
        }
    }

    private ImageDifference ComparePixels(Color32[] before, Color32[] after, int width, int height, int threshold)
    {
        if (before == null || width != baselineWidth || height != baselineHeight || before.Length != after.Length)
            throw new InvalidOperationException("Controlled image comparisons must have the same dimensions.");
        var result = new ImageDifference();
        // Exclude diagnostics GUI and window edges. GetPixels32 starts at the
        // bottom; the external reporter translates this same ROI to PNG rows.
        for (int y = height / 4; y < Math.Min(height * 3 / 4, height - 96); y++)
        for (int x = width / 4; x < width * 3 / 4; x++)
        {
            int index = y * width + x;
            int r = after[index].r - before[index].r;
            int g = after[index].g - before[index].g;
            int b = after[index].b - before[index].b;
            result.compared++;
            if (Math.Max(Math.Abs(r), Math.Max(Math.Abs(g), Math.Abs(b))) > threshold) result.changed++;
            if (Math.Max(-r, Math.Max(-g, -b)) > threshold) result.darkened++;
            if (Math.Max(r, Math.Max(g, b)) > threshold) result.brightened++;
        }
        return result;
    }

    private struct ImageDifference
    {
        public int compared, changed, darkened, brightened;
    }

    private void SaveState()
    {
        originalSettingsEnabled = settings.enabled;
        originalAreaEnabled = area.enabled;
        originalPreview = settings.previewVisibleGrassCount;
        originalContacts = settings.contactShadows.enabled;
        originalMotion = settings.motionVectors.mode;
        originalScale = pipeline.renderScale;
        originalWindStrength = settings.grassMaterial.GetFloat("_WindStrength");
        originalMsaa = pipeline.msaaSampleCount;
        originalAntialiasing = additionalCameraData.antialiasing;
        originalPostProcessing = additionalCameraData.renderPostProcessing;
        originalAllowMsaa = targetCamera.allowMSAA;
        originalCameraPosition = targetCamera.transform.position;
        originalDensity = area.DensityTexture;
        originalTerrain = area.Terrain;
        originalGroundLayer = area.GroundLayer;
        originalGroundColor = area.GroundColorTexture;
        originalCoverageBounds = area.TextureCoverageBounds;
        savedState = true;
    }

    private void RestoreCoverage()
    {
        area.ConfigureTexture(originalTerrain, originalDensity, originalGroundLayer, originalGroundColor);
        area.SetTextureCoverageBounds(originalCoverageBounds);
    }

    private void Finish(bool passed, string failure)
    {
        if (finished)
            return;
        finished = true;
        running = false;
        animateCamera = false;
        Application.logMessageReceived -= OnLog;
        RenderPipelineManager.endCameraRendering -= OnCameraRendered;
        report.passed = passed;
        report.failure = Clip(failure, 6000);
        report.completedUtc = DateTime.UtcNow.ToString("O");
        report.cameraFrames = renderedFrames;
        report.seconds = Time.realtimeSinceStartupAsDouble - startedAt;
        try
        {
            if (savedState)
            {
                settings.contactShadows.enabled = originalContacts;
                settings.motionVectors.mode = originalMotion;
                settings.previewVisibleGrassCount = originalPreview;
                settings.enabled = originalSettingsEnabled;
                pipeline.renderScale = originalScale;
                pipeline.msaaSampleCount = originalMsaa;
                settings.grassMaterial.SetFloat("_WindStrength", originalWindStrength);
                additionalCameraData.antialiasing = originalAntialiasing;
                additionalCameraData.renderPostProcessing = originalPostProcessing;
                targetCamera.allowMSAA = originalAllowMsaa;
                RestoreCoverage();
                area.enabled = originalAreaEnabled;
                targetCamera.transform.position = originalCameraPosition;
            }
        }
        catch (Exception error)
        {
            report.passed = false;
            report.failure = Clip((report.failure ?? "") + "\nRestore failed: " + error, 6000);
        }
        try
        {
            if (string.IsNullOrEmpty(outputDirectory))
                outputDirectory = Path.Combine(Application.persistentDataPath, "GrassValidation");
            Directory.CreateDirectory(outputDirectory);
            report.status = report.passed ? "passed" : "failed";
            WriteReport();
        }
        catch (Exception error)
        {
            report.passed = false;
            Debug.LogError("Could not write grass smoke results: " + error.Message);
        }
        baselinePixels = null;
        windPixels = null;
        contactPixels = null;
        Debug.Log("Grass smoke " + (report.passed ? "passed" : "failed") + ": " + outputDirectory +
            (report.passed ? "" : "\n" + report.failure));
        if (!Application.isEditor)
            Application.Quit(report.passed ? 0 : 2);
    }

    private void WriteReport()
    {
        File.WriteAllText(Path.Combine(outputDirectory, "grass-smoke-results.json"), JsonUtility.ToJson(report, true));
    }

    private void OnCameraRendered(ScriptableRenderContext context, Camera camera)
    {
        if (running && camera == targetCamera)
        {
            renderedFrames++;
            maximumStageCameraDisplacement = Mathf.Max(maximumStageCameraDisplacement,
                Vector3.Distance(targetCamera.transform.position, stageCameraStart));
        }
    }

    private void OnLog(string message, string stackTrace, LogType type)
    {
        if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
        {
            report.errorCount++;
            if (report.errors.Count < MaximumLogEntries)
                report.errors.Add(Clip(message + "\n" + stackTrace, 4000));
        }
        else if (type == LogType.Warning)
        {
            report.warningCount++;
            if (report.warnings.Count < MaximumLogEntries)
                report.warnings.Add(Clip(message, 2000));
        }
    }

    private static string OutputArgument(string[] arguments)
    {
        for (int index = 0; index < arguments.Length; index++)
        {
            if (arguments[index].StartsWith("-grassSmokeOutput=", StringComparison.Ordinal))
                return arguments[index].Substring("-grassSmokeOutput=".Length);
            if (arguments[index] == "-grassSmokeOutput")
            {
                if (index + 1 >= arguments.Length || string.IsNullOrWhiteSpace(arguments[index + 1]) ||
                    arguments[index + 1].StartsWith("-", StringComparison.Ordinal))
                    throw new ArgumentException("-grassSmokeOutput requires an output directory.");
                return arguments[index + 1];
            }
        }
        return Path.Combine(Application.persistentDataPath, "GrassValidation");
    }

    private static string Clip(string value, int length) =>
        string.IsNullOrEmpty(value) || value.Length <= length ? value : value.Substring(0, length);

    private enum CountExpectation { Unchecked, Positive, Zero }

    private sealed class Stage
    {
        public readonly string Name;
        public readonly CountExpectation Count;
        public readonly int Samples;
        public readonly bool Optional;
        public readonly Action Activate;
        public Stage(string name, CountExpectation count, int samples, Action activate, bool optional = false)
        { Name = name; Count = count; Samples = samples; Activate = activate; Optional = optional; }
    }

    [Serializable]
    private sealed class SmokeReport
    {
        public int schemaVersion = 3;
        public string status = "running", attemptId = Guid.NewGuid().ToString("N");
        public string scope = "Checked standalone no-AA/MSAA baseline with executed attachment samples/viewports and controlled grass, wind, contact and empty-coverage image comparisons; optional motion coverage is separate.";
        public string limitations = "Counts are asynchronous diagnostics without per-sample timestamps. Imported targets use their declared metadata when no RenderTexture exists. Functional pixel changes do not certify shadow quality, alpha-to-coverage silhouettes, temporal vector values or performance. The saved mask is synthetic; proprietary MicroVerse execution is outside this run.";
        public string unityVersion, startedUtc, completedUtc, graphicsApi, graphicsDevice, failure;
        public bool passed, supportsCompute, supportsIndirectArguments, supportsAsyncReadback;
        public bool checksEnabled, instrumentationEnabled, renderGraphValidityChecks;
        public bool optionalMotionRequested, msaaCoverageComplete, windTextureVaries;
        public int cameraFrames, errorCount, warningCount, comparedPixels, changedGrassPixels, unsupportedMsaaStages;
        public int changedWindPixels, darkenedContactPixels, brightenedContactPixels, changedBlackMaskPixels, changedNoSourcesPixels;
        public int windTextureWidth, windTextureHeight;
        public float windStrength, windScrollSpeed;
        public double seconds;
        public List<StageObservation> stages = new List<StageObservation>();
        public List<string> errors = new List<string>();
        public List<string> warnings = new List<string>();
    }

    [Serializable]
    private sealed class StageObservation
    {
        public string name, status, skipReason, countExpectation, screenshot, motionModeRequested;
        public bool passed, cameraTargetsScreen, contactsRequested, postProcessingRequested, cameraPanRequested, optional;
        public uint visibleGrass, overflowGrass;
        public int cameraFrames, imageWidth, imageHeight, requestedMsaa, activationFrame, capturedFrame;
        public float requestedRenderScale, maximumCameraDisplacement, windStrength;
        public Vector3 cameraPosition;
        public Quaternion cameraRotation;
        public double seconds;
        public AttachmentObservation attachments;
    }

    [Serializable]
    public sealed class AttachmentObservation
    {
        public int frame, requestedMsaa, hardwareSupportedMsaa, cameraDescriptorSamples, colorSamples, depthSamples;
        public int colorWidth, colorHeight, depthWidth, depthHeight;
        public int colorViewportWidth, colorViewportHeight, depthViewportWidth, depthViewportHeight;
        public int cameraWidth, cameraHeight, cameraScaledWidth, cameraScaledHeight, cameraDescriptorWidth, cameraDescriptorHeight;
        public float cameraRenderScale;
        public string colorFormat, depthFormat, colorEvidence, depthEvidence, antialiasing;
        public bool backbuffer;
    }
}
