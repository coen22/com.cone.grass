using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>Creates a reproducible real-rendering scene in the disposable project and builds it twice.</summary>
public static class GrassValidationBuild
{
    private const string Folder = "Assets/ValidationSettings";
    private const string ScenePath = Folder + "/GrassValidation.unity";

    [Serializable]
    private sealed class BuildResultRecord
    {
        public string unityVersion, target, scope = "Standalone player build; does not execute GPU acceptance";
        public bool succeeded;
        public List<BuildRecord> builds = new List<BuildRecord>();
    }

    [Serializable]
    private sealed class BuildRecord
    {
        public string kind, result, output;
        public uint errors, warnings;
        public double seconds;
    }

    [MenuItem("Tools/Cone/Grass Validation/Create Rendering Scene")]
    public static void PrepareScene()
    {
        RequireDisposableProject();
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            throw new InvalidOperationException("Save or discard the current scene before generating validation content.");
        GrassValidationProjectBootstrap.Configure();
        var pipeline = RequiredAsset<UniversalRenderPipelineAsset>(Folder + "/GrassValidationPipeline.asset");
        var renderer = RequiredAsset<UniversalRendererData>(Folder + "/GrassValidationRenderer.asset");
        Material blade = MaterialAsset("GrassBlade", "InfiniteGrass/GrassBladeShader");
        Material height = MaterialAsset("GrassHeight", "InfiniteGrass/GrassHeightMapShader");
        blade.SetColor("_Color", new Color(0.24f, 0.46f, 0.09f));
        blade.SetColor("_AOColor", new Color(0.10f, 0.24f, 0.035f));
        blade.SetFloat("_GrassWidth", 0.06f);
        blade.SetFloat("_GrassHeight", 0.85f);
        blade.SetFloat("_GrassCurving", 0.15f);
        blade.SetFloat("_WindStrength", 0.15f);
        blade.SetTexture("_WindTexture", WindAsset());
        blade.SetTextureScale("_WindTexture", Vector2.one * 0.13f);
        EditorUtility.SetDirty(blade);

        GrassDataRendererFeature feature = null;
        foreach (ScriptableRendererFeature candidate in renderer.rendererFeatures)
            if (candidate is GrassDataRendererFeature grassFeature)
                feature = grassFeature;
        if (!feature)
        {
            feature = ScriptableObject.CreateInstance<GrassDataRendererFeature>();
            feature.name = "Grass validation feature";
            AssetDatabase.AddObjectToAsset(feature, renderer);
            renderer.rendererFeatures.Add(feature);
        }
        var featureProperties = new SerializedObject(feature);
        featureProperties.FindProperty("computeShader").objectReferenceValue = RequiredAsset<ComputeShader>(
            "Packages/com.cone.grass/Runtime/Compute/GrassPositionsCompute.compute");
        featureProperties.FindProperty("heightMapMat").objectReferenceValue = height;
        featureProperties.FindProperty("heightMapLayer").intValue = 0;
        featureProperties.ApplyModifiedPropertiesWithoutUndo();
        feature.SetActive(true);
        renderer.SetDirty();
        EditorUtility.SetDirty(renderer);
        pipeline.msaaSampleCount = 1;
        pipeline.renderScale = 1f;
        pipeline.supportsHDR = false;
        pipeline.supportsCameraDepthTexture = false;
        pipeline.supportsCameraOpaqueTexture = false;
        EditorUtility.SetDirty(pipeline);

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        TerrainLayer layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(Folder + "/GrassLayer.terrainlayer");
        if (!layer)
        {
            layer = new TerrainLayer();
            AssetDatabase.CreateAsset(layer, Folder + "/GrassLayer.terrainlayer");
        }
        Texture2D diffuse = TextureAsset("GroundDiffuse", 2, TextureFormat.RGBA32, false);
        diffuse.SetPixels(new[] { new Color(0.20f, 0.34f, 0.07f), new Color(0.23f, 0.39f, 0.08f),
            new Color(0.23f, 0.39f, 0.08f), new Color(0.20f, 0.34f, 0.07f) });
        diffuse.Apply();
        EditorUtility.SetDirty(diffuse);
        layer.diffuseTexture = diffuse;
        layer.tileSize = new Vector2(3f, 3f);
        EditorUtility.SetDirty(layer);
        TerrainData data = AssetDatabase.LoadAssetAtPath<TerrainData>(Folder + "/Terrain.asset");
        if (!data)
        {
            data = new TerrainData();
            AssetDatabase.CreateAsset(data, Folder + "/Terrain.asset");
        }
        data.heightmapResolution = 33;
        data.alphamapResolution = 32;
        data.size = new Vector3(24f, 2f, 24f);
        data.terrainLayers = new[] { layer };
        var heights = new float[33, 33];
        for (int z = 0; z < 33; z++)
            for (int x = 0; x < 33; x++)
                heights[z, x] = 0.1f;
        data.SetHeights(0, 0, heights);
        var splats = new float[32, 32, 1];
        for (int z = 0; z < 32; z++)
            for (int x = 0; x < 32; x++)
                splats[z, x, 0] = 1f;
        data.SetAlphamaps(0, 0, splats);
        EditorUtility.SetDirty(data);
        GameObject terrainObject = Terrain.CreateTerrainGameObject(data);
        terrainObject.transform.position = new Vector3(-12f, 0f, -12f);
        Terrain terrain = terrainObject.GetComponent<Terrain>();
        terrain.materialTemplate = MaterialAsset("TerrainMaterial", "Universal Render Pipeline/Terrain/Lit");

        var settingsObject = new GameObject("Grass settings");
        InfiniteGrassRenderer settings = settingsObject.AddComponent<InfiniteGrassRenderer>();
        settings.grassMaterial = blade;
        settings.spacing = 0.28f;
        settings.drawDistance = 26f;
        settings.fullDensityDistance = 10f;
        settings.nearLodDistance = 8f;
        settings.subdivisionDistance = 17f;
        settings.captureResolution = 256;
        settings.maxBufferCount = 0.03f;
        settings.renderInSceneView = false;
        settings.previewVisibleGrassCount = true;
        settings.motionVectors.mode = GrassMotionVectors.Mode.Off;
        settings.contactShadows.enabled = false;
        settings.RefreshGrassData();

        var areaObject = new GameObject("Synthetic saved-mask bridge");
        GrassMicroVerseBridge bridge = areaObject.AddComponent<GrassMicroVerseBridge>();
        var bridgeProperties = new SerializedObject(bridge);
        bridgeProperties.FindProperty("maskTarget").objectReferenceValue = MaskAsset();
        bridgeProperties.FindProperty("terrain").objectReferenceValue = terrain;
        bridgeProperties.FindProperty("textureSubAssetName").stringValue = "ValidationDensity";
        bridgeProperties.FindProperty("groundLayer").objectReferenceValue = layer;
        bridgeProperties.FindProperty("autoRefresh").boolValue = false;
        bridgeProperties.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();
        if (!GrassMicroVerseBridgeUtility.Refresh(bridge) || !bridge.LastRefreshSucceeded)
            throw new InvalidOperationException("Synthetic saved-mask binding failed: " + bridge.LastRefreshMessage);

        GameObject obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
        obstacle.name = "Contact receiver and caster";
        obstacle.transform.position = new Vector3(-1.6f, 0.95f, 1f);
        obstacle.transform.localScale = new Vector3(1.5f, 1.5f, 1.5f);
        Material obstacleMaterial = MaterialAsset("Obstacle", "Universal Render Pipeline/Lit");
        obstacleMaterial.SetColor("_BaseColor", new Color(0.45f, 0.40f, 0.30f));
        EditorUtility.SetDirty(obstacleMaterial);
        obstacle.GetComponent<Renderer>().sharedMaterial = obstacleMaterial;
        var lightObject = new GameObject("Directional light");
        Light light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.2f;
        light.shadows = LightShadows.Soft;
        lightObject.transform.rotation = Quaternion.Euler(40f, -35f, 0f);
        RenderSettings.sun = light;
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.35f, 0.35f, 0.35f);
        var cameraObject = new GameObject("Validation camera");
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.tag = "MainCamera";
        camera.transform.position = new Vector3(0f, 4f, -10f);
        camera.transform.LookAt(new Vector3(0f, 0.7f, 2f));
        camera.nearClipPlane = 0.1f;
        camera.farClipPlane = 80f;
        camera.allowHDR = false;
        camera.allowMSAA = true;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.12f, 0.17f, 0.23f);
        UniversalAdditionalCameraData cameraData = cameraObject.AddComponent<UniversalAdditionalCameraData>();
        cameraData.renderPostProcessing = false;
        cameraData.antialiasing = AntialiasingMode.None;
        var probe = cameraObject.AddComponent<GrassValidationPlayerProbe>();
        probe.settings = settings;
        probe.area = bridge.PlacementArea;
        probe.pipeline = pipeline;
        probe.targetCamera = camera;
        AssetDatabase.SaveAssets();
        if (!EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(), ScenePath))
            throw new InvalidOperationException("Could not save the grass validation scene.");
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        Debug.Log("Created real-rendering validation scene with direct camera output and a synthetic saved-mask bridge.");
    }

    public static void BuildCurrent()
    {
        RequireDisposableProject();
        BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
        string extension = target == BuildTarget.StandaloneLinux64 ? ".x86_64" :
            target == BuildTarget.StandaloneWindows64 ? ".exe" :
            target == BuildTarget.StandaloneOSX ? ".app" : null;
        if (extension == null || !BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, target))
            throw new InvalidOperationException("Select an installed standalone target before invoking BuildCurrent (use -buildTarget on the command line).");
        PrepareScene();
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
        PlayerSettings.defaultScreenWidth = 960;
        PlayerSettings.defaultScreenHeight = 540;
        PlayerSettings.runInBackground = true;
        string buildsDirectory = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Builds");
        string output = Path.Combine(buildsDirectory, target.ToString(), "GrassValidation" + extension);
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        var record = new BuildResultRecord { unityVersion = Application.unityVersion, target = target.ToString() };
        try
        {
            for (int iteration = 0; iteration < 2; iteration++)
            {
                var options = new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath }, locationPathName = output, target = target,
                    targetGroup = BuildTargetGroup.Standalone, subtarget = (int)StandaloneBuildSubtarget.Player,
                    options = BuildOptions.Development | BuildOptions.DetailedBuildReport |
                        (iteration == 0 ? BuildOptions.CleanBuildCache : BuildOptions.None)
                };
                BuildReport report = BuildPipeline.BuildPlayer(options);
                if (!report)
                    throw new InvalidOperationException("Unity returned no build report.");
                record.builds.Add(new BuildRecord { kind = iteration == 0 ? "clean" : "incremental",
                    result = report.summary.result.ToString(), errors = report.summary.totalErrors,
                    warnings = report.summary.totalWarnings, seconds = report.summary.totalTime.TotalSeconds,
                    output = report.summary.outputPath });
                if (report.summary.result != BuildResult.Succeeded)
                    throw new InvalidOperationException("Grass " + record.builds[iteration].kind + " build failed.");
            }
            record.succeeded = true;
        }
        finally
        {
            Directory.CreateDirectory(buildsDirectory);
            File.WriteAllText(Path.Combine(buildsDirectory, "build-results.json"), JsonUtility.ToJson(record, true));
        }
        Debug.Log("Grass clean and incremental builds succeeded. Execute the player with -grassSmoke to run rendering checks.");
    }

    private static void RequireDisposableProject()
    {
        if (!File.Exists(Path.Combine(Directory.GetParent(Application.dataPath).FullName, ".grass-validation-project")))
            throw new InvalidOperationException("This command only operates inside the generated disposable validation project.");
    }

    private static T RequiredAsset<T>(string path) where T : UnityEngine.Object
    {
        T value = AssetDatabase.LoadAssetAtPath<T>(path);
        if (!value) throw new InvalidOperationException("Missing validation asset: " + path);
        return value;
    }

    private static Material MaterialAsset(string name, string shaderName)
    {
        Shader shader = Shader.Find(shaderName);
        if (!shader) throw new InvalidOperationException("Missing validation shader: " + shaderName);
        string path = Folder + "/" + name + ".mat";
        Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (!material) { material = new Material(shader); AssetDatabase.CreateAsset(material, path); }
        else material.shader = shader;
        return material;
    }

    private static Texture2D TextureAsset(string name, int size, TextureFormat format, bool linear)
    {
        string path = Folder + "/" + name + ".asset";
        Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        if (!texture)
        {
            texture = new Texture2D(size, size, format, false, linear) { name = name };
            AssetDatabase.CreateAsset(texture, path);
        }
        return texture;
    }

    private static Texture2D WindAsset()
    {
        Texture2D texture = TextureAsset("Wind", 16, TextureFormat.RGBA32, true);
        var colors = new Color[256];
        for (int i = 0; i < colors.Length; i++)
            colors[i] = new Color(0.5f + 0.3f * Mathf.Sin(i * 0.3f), 0.5f + 0.3f * Mathf.Cos(i * 0.2f), 0f, 1f);
        texture.SetPixels(colors); texture.Apply(); texture.wrapMode = TextureWrapMode.Repeat;
        EditorUtility.SetDirty(texture);
        return texture;
    }

    private static GrassValidationMaskAsset MaskAsset()
    {
        string path = Folder + "/SyntheticMask.asset";
        GrassValidationMaskAsset asset = AssetDatabase.LoadAssetAtPath<GrassValidationMaskAsset>(path);
        if (!asset)
        {
            asset = ScriptableObject.CreateInstance<GrassValidationMaskAsset>();
            AssetDatabase.CreateAsset(asset, path);
        }
        Texture2D density = null;
        foreach (UnityEngine.Object child in AssetDatabase.LoadAllAssetsAtPath(path))
            if (child is Texture2D texture && texture.name == "ValidationDensity") density = texture;
        if (!density)
        {
            density = new Texture2D(64, 64, TextureFormat.R8, false, true) { name = "ValidationDensity" };
            AssetDatabase.AddObjectToAsset(density, asset);
        }
        var values = new byte[64 * 64];
        for (int z = 0; z < 64; z++)
            for (int x = 0; x < 64; x++)
            {
                float distance = new Vector2((x + 0.5f) / 32f - 1f, (z + 0.5f) / 32f - 1f).magnitude;
                values[z * 64 + x] = (byte)Mathf.RoundToInt(Mathf.Clamp01((0.85f - distance) * 8f) * 255f);
            }
        density.SetPixelData(values, 0); density.Apply(); density.wrapMode = TextureWrapMode.Clamp;
        EditorUtility.SetDirty(density); EditorUtility.SetDirty(asset);
        return asset;
    }
}
