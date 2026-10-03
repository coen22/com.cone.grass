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
            supportsAsyncReadback = SystemInfo.supportsAsyncGPUReadback
        };
        running = true;
        Application.logMessageReceived += OnLog;
        RenderPipelineManager.endCameraRendering += OnCameraRendered;
        try
        {
            outputDirectory = Path.GetFullPath(OutputArgument(arguments));
            Directory.CreateDirectory(outputDirectory);
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
            new Stage("03-camera-pan-no-aa", CountExpectation.Positive, 1, () => animateCamera = true),
            new Stage("04-contacts-no-aa", CountExpectation.Positive, 1, () =>
            {
                animateCamera = false;
                targetCamera.transform.position = originalCameraPosition;
                settings.contactShadows.enabled = true;
            }),
            new Stage("05-render-scale-below-no-aa", CountExpectation.Positive, 1, () => pipeline.renderScale = 0.75f),
            new Stage("06-render-scale-above-no-aa", CountExpectation.Positive, 1, () => pipeline.renderScale = 1.25f),
            new Stage("07-msaa2", CountExpectation.Positive, 2, () => pipeline.renderScale = 1f),
            new Stage("08-msaa4", CountExpectation.Positive, 4, () => pipeline.renderScale = 1f),
            new Stage("09-msaa8", CountExpectation.Positive, 8, () => pipeline.renderScale = 1f),
            new Stage("10-black-mask", CountExpectation.Zero, 1, () =>
                area.ConfigureTexture(originalTerrain, Texture2D.blackTexture, originalGroundLayer, originalGroundColor)),
            new Stage("11-no-sources", CountExpectation.Zero, 1, () => area.enabled = false),
            new Stage("12-restored", CountExpectation.Positive, 1, () =>
            {
                RestoreCoverage();
                area.enabled = true;
            })
        };
        if (includeMotion)
            stages.Add(new Stage("13-optional-motion", CountExpectation.Positive, 1, () =>
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
                bool unsupported = VerifyAttachments(attachments, stage.Samples);
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

    private static bool VerifyAttachments(AttachmentObservation sample, int requested)
    {
        if (sample.colorSamples < 1 || sample.colorSamples != sample.depthSamples ||
            sample.colorWidth < 1 || sample.colorHeight < 1 ||
            sample.colorWidth != sample.depthWidth || sample.colorHeight != sample.depthHeight)
            throw new InvalidOperationException("The grass color/depth attachments have incompatible dimensions or sample counts.");
        if (sample.hardwareSupportedMsaa < 1 || sample.hardwareSupportedMsaa > requested ||
            (sample.hardwareSupportedMsaa & (sample.hardwareSupportedMsaa - 1)) != 0)
            throw new InvalidOperationException("The device returned an invalid supported MSAA count.");
        if (sample.colorSamples != sample.hardwareSupportedMsaa)
            throw new InvalidOperationException("The grass attachments use " + sample.colorSamples +
                " samples, but the device supports " + sample.hardwareSupportedMsaa + " for the requested " + requested + "x stage.");
        return sample.hardwareSupportedMsaa < requested;
    }

    public void RecordAttachments(int stage, int frame, int requested, int supported, int descriptorSamples,
        int colorSamples, int depthSamples, int colorWidth, int colorHeight, int depthWidth, int depthHeight,
        string colorFormat, string depthFormat, string colorEvidence, string depthEvidence, bool backbuffer, string antialiasing)
    {
        // Ignore any callback recorded for an earlier stage. Values are published
        // only by the executed raster pass, never by a requested URP asset setting.
        if (!IsRunning || stage != ActiveStage)
            return;
        attachments = new AttachmentObservation
        {
            frame = frame, requestedMsaa = requested, hardwareSupportedMsaa = supported,
            cameraDescriptorSamples = descriptorSamples, colorSamples = colorSamples, depthSamples = depthSamples,
            colorWidth = colorWidth, colorHeight = colorHeight, depthWidth = depthWidth, depthHeight = depthHeight,
            colorFormat = colorFormat, depthFormat = depthFormat, colorEvidence = colorEvidence,
            depthEvidence = depthEvidence, backbuffer = backbuffer, antialiasing = antialiasing
        };
    }

    private bool CountMatches(CountExpectation expectation)
    {
        return expectation == CountExpectation.Unchecked ||
            (expectation == CountExpectation.Positive ? settings.VisibleGrassCount > 0 : settings.VisibleGrassCount == 0);
    }

    private void ValidateSceneAndDevice()
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || !report.supportsCompute ||
            !report.supportsIndirectArguments || !report.supportsAsyncReadback || !SystemInfo.supportsInstancing)
            throw new InvalidOperationException("Smoke checks require a real graphics device with compute, indirect draws, instancing, and async GPU readback. Do not use -nographics.");
        if (!settings || !area || !pipeline || !targetCamera || !targetCamera.isActiveAndEnabled ||
            targetCamera.targetTexture != null || targetCamera.stereoEnabled || targetCamera.targetDisplay != 0)
            throw new InvalidOperationException("Assign the generated settings, mask area, URP asset, and active single-view screen camera.");
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
                if (baselinePixels == null || image.width != baselineWidth || image.height != baselineHeight)
                    throw new InvalidOperationException("Off/on images must have the same dimensions.");
                // Keep async diagnostics enabled. Exclude their top-left GUI and
                // the window edges from the independent grass image comparison.
                int maxY = Math.Min(image.height * 3 / 4, image.height - 96);
                for (int y = image.height / 4; y < maxY; y++)
                for (int x = image.width / 4; x < image.width * 3 / 4; x++)
                {
                    int index = y * image.width + x;
                    Color32 before = baselinePixels[index], after = pixels[index];
                    int difference = Math.Max(Math.Abs(before.r - after.r),
                        Math.Max(Math.Abs(before.g - after.g), Math.Abs(before.b - after.b)));
                    report.comparedPixels++;
                    if (difference > 8)
                        report.changedGrassPixels++;
                }
                if (report.changedGrassPixels < 32)
                    throw new InvalidOperationException("Positive GPU counts did not produce a measurable off/on grass image difference.");
                baselinePixels = null;
            }
        }
        finally
        {
            if (image)
                Destroy(image);
        }
    }

    private void SaveState()
    {
        originalSettingsEnabled = settings.enabled;
        originalAreaEnabled = area.enabled;
        originalPreview = settings.previewVisibleGrassCount;
        originalContacts = settings.contactShadows.enabled;
        originalMotion = settings.motionVectors.mode;
        originalScale = pipeline.renderScale;
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
            File.WriteAllText(Path.Combine(outputDirectory, "grass-smoke-results.json"), JsonUtility.ToJson(report, true));
        }
        catch (Exception error)
        {
            report.passed = false;
            Debug.LogError("Could not write grass smoke results: " + error.Message);
        }
        baselinePixels = null;
        Debug.Log("Grass smoke " + (report.passed ? "passed" : "failed") + ": " + outputDirectory +
            (report.passed ? "" : "\n" + report.failure));
        if (!Application.isEditor)
            Application.Quit(report.passed ? 0 : 2);
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
        public int schemaVersion = 2;
        public string scope = "Standalone no-AA/MSAA baseline with executed color/depth attachment observations; optional motion coverage is separate. No frame-rate or analytical image-quality claim.";
        public string limitations = "Counts are asynchronous diagnostics without per-sample timestamps. Attachment samples come from allocated RenderTextures or imported target metadata when there is no RenderTexture. Contact/motion settings and captures do not certify shadow quality, alpha-to-coverage behavior or vector values. The saved mask is synthetic; proprietary MicroVerse execution is outside this run.";
        public string unityVersion, startedUtc, completedUtc, graphicsApi, graphicsDevice, failure;
        public bool passed, supportsCompute, supportsIndirectArguments, supportsAsyncReadback;
        public bool optionalMotionRequested, msaaCoverageComplete, windTextureVaries;
        public int cameraFrames, errorCount, warningCount, comparedPixels, changedGrassPixels, unsupportedMsaaStages;
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
        public float requestedRenderScale, maximumCameraDisplacement;
        public double seconds;
        public AttachmentObservation attachments;
    }

    [Serializable]
    private sealed class AttachmentObservation
    {
        public int frame, requestedMsaa, hardwareSupportedMsaa, cameraDescriptorSamples, colorSamples, depthSamples;
        public int colorWidth, colorHeight, depthWidth, depthHeight;
        public string colorFormat, depthFormat, colorEvidence, depthEvidence, antialiasing;
        public bool backbuffer;
    }
}
