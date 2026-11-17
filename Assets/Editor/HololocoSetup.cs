// Project setup and builds that run from the command line:
//   Unity -batchmode -projectPath F:\Hololoco\HololocoApp -executeMethod HololocoSetup.<Method>
//
//   AddPackages      adds the packages the app needs, at the versions this editor recommends
//   Configure        Android settings, ARCore on, Geospatial, UI theme, glTF shaders, the scene
//   BuildAndroid     Configure, then builds F:\Hololoco\Builds\Hololoco.apk
//
// The ARCore API key is read from F:\Hololoco\secrets\arcore-api-key.txt at build time,
// so it never sits in the project's files. Without it, placement falls back to GPS and compass.

using System.IO;
using System.Linq;
using Google.XR.ARCoreExtensions;
using Google.XR.ARCoreExtensions.Internal;
using Hololoco;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.Management;

public static class HololocoSetup
{
    const string ScenePath = "Assets/Scenes/Main.unity";
    const string UiFolder = "Assets/Hololoco/UI";
    const string KeyFile = @"F:\Hololoco\secrets\arcore-api-key.txt";
    const string ApkPath = @"F:\Hololoco\Builds\Hololoco.apk";

    static bool HasKey => File.Exists(KeyFile) && File.ReadAllText(KeyFile).Trim().Length > 0;

    // ------------------------------------------------------------------ packages

    static readonly string[] Packages =
    {
        "com.unity.xr.arfoundation",
        "com.unity.xr.arcore",
        "com.unity.xr.management",
        "com.unity.cloud.gltfast",
        "com.unity.nuget.newtonsoft-json",
        "https://github.com/google-ar/arcore-unity-extensions.git",
    };

    static AddAndRemoveRequest request;

    public static void AddPackages()
    {
        request = Client.AddAndRemove(Packages);
        EditorApplication.update += WaitForPackages;
    }

    static void WaitForPackages()
    {
        if (!request.IsCompleted) return;
        EditorApplication.update -= WaitForPackages;
        if (request.Status == StatusCode.Success)
        {
            foreach (var p in request.Result.Where(p => Packages.Any(n => p.name == n || p.packageId.Contains(n))))
                Debug.Log($"HOLOLOCO package: {p.name} {p.version}");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError($"HOLOLOCO packages failed: {request.Error?.message}");
            EditorApplication.Exit(1);
        }
    }

    // ------------------------------------------------------------------ configure

    public static void Configure()
    {
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

        // Android player settings. ARCore needs OpenGL ES 3 and 64-bit ARM.
        PlayerSettings.companyName = "Hololoco";
        PlayerSettings.productName = "Hololoco";
        PlayerSettings.bundleVersion = "0.1.0";
        PlayerSettings.Android.bundleVersionCode = 1;
        PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.hololoco.app");
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.Android.minSdkVersion = (AndroidSdkVersions)26;
        PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });
        PlayerSettings.Android.forceInternetPermission = true;
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;

        ConfigureXr();
        ConfigureArCoreExtensions();
        IncludeGltfShaders();
        CreateMaterials();
        var panel = CreateUiAssets();
        var (config, filter) = CreateArCoreAssets();
        BuildScene(panel, config, filter);
        AssetDatabase.SaveAssets();
        Debug.Log("HOLOLOCO configured");
    }

    /// <summary>XR Plug-in Management: ARCore for Android, started with the app.</summary>
    static void ConfigureXr()
    {
        const string path = "Assets/XR/XRGeneralSettingsPerBuildTarget.asset";
        Directory.CreateDirectory("Assets/XR");
        var perTarget = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(path);
        if (perTarget == null)
        {
            perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
            AssetDatabase.CreateAsset(perTarget, path);
        }
        EditorBuildSettings.AddConfigObject(XRGeneralSettings.settingsKey, perTarget, true);
        if (!perTarget.HasManagerSettingsForBuildTarget(BuildTargetGroup.Android))
            perTarget.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.Android);
        var general = perTarget.SettingsForBuildTarget(BuildTargetGroup.Android);
        general.InitManagerOnStart = true;
        var manager = perTarget.ManagerSettingsForBuildTarget(BuildTargetGroup.Android);
        if (!manager.activeLoaders.Any(l => l != null && l.GetType().FullName == "UnityEngine.XR.ARCore.ARCoreLoader"))
            XRPackageMetadataStore.AssignLoader(manager, "UnityEngine.XR.ARCore.ARCoreLoader", BuildTargetGroup.Android);
        EditorUtility.SetDirty(perTarget);
        EditorUtility.SetDirty(general);
        EditorUtility.SetDirty(manager);
    }

    /// <summary>Geospatial on, with the API key from outside the project if there is one.</summary>
    static void ConfigureArCoreExtensions()
    {
        var key = File.Exists(KeyFile) ? File.ReadAllText(KeyFile).Trim() : "";
        var settings = ARCoreExtensionsProjectSettings.Instance;
        // ARCore refuses to build Geospatial without a way to authorize it.
        settings.GeospatialEnabled = key.Length > 0;
        settings.AndroidAuthenticationStrategySetting =
            key.Length > 0 ? AndroidAuthenticationStrategy.ApiKey : AndroidAuthenticationStrategy.DoNotUse;
        settings.AndroidCloudServicesApiKey = key;
        settings.Save();
        Debug.Log(key.Length > 0 ? "HOLOLOCO ARCore key: set" : "HOLOLOCO ARCore key: none (GPS and compass only)");
    }

    /// <summary>glTFast builds materials at run time, so its shaders must be in the build.</summary>
    static void IncludeGltfShaders()
    {
        var graphics = AssetDatabase.LoadAssetAtPath<GraphicsSettings>("ProjectSettings/GraphicsSettings.asset");
        var so = new SerializedObject(graphics);
        var list = so.FindProperty("m_AlwaysIncludedShaders");
        foreach (var name in new[] { "glTF/PbrMetallicRoughness", "glTF/PbrSpecularGlossiness", "glTF/Unlit" })
        {
            var shader = Shader.Find(name);
            if (shader == null)
            {
                Debug.LogWarning($"HOLOLOCO shader not found: {name}");
                continue;
            }
            var present = false;
            for (var i = 0; i < list.arraySize; i++)
                if (list.GetArrayElementAtIndex(i).objectReferenceValue == shader) present = true;
            if (present) continue;
            list.InsertArrayElementAtIndex(list.arraySize);
            list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
        }
        so.ApplyModifiedProperties();
    }

    static PanelSettings CreateUiAssets()
    {
        Directory.CreateDirectory(UiFolder);
        var themePath = $"{UiFolder}/HololocoTheme.tss";
        if (!File.Exists(themePath))
        {
            File.WriteAllText(themePath, "@import url(\"unity-theme://default\");\n");
            AssetDatabase.ImportAsset(themePath);
        }
        var panelPath = $"{UiFolder}/PanelSettings.asset";
        var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(panelPath);
        if (panel == null)
        {
            panel = ScriptableObject.CreateInstance<PanelSettings>();
            AssetDatabase.CreateAsset(panel, panelPath);
        }
        panel.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(themePath);
        // About one unit per Android dp, so sizes in the style sheet mean the same on every phone.
        panel.scaleMode = PanelScaleMode.ConstantPhysicalSize;
        panel.referenceDpi = 160;
        panel.fallbackDpi = 160;
        EditorUtility.SetDirty(panel);
        return panel;
    }

    static (ARCoreExtensionsConfig, ARCoreExtensionsCameraConfigFilter) CreateArCoreAssets()
    {
        const string configPath = "Assets/Hololoco/ARCoreExtensionsConfig.asset";
        const string filterPath = "Assets/Hololoco/ARCoreExtensionsCameraConfigFilter.asset";
        var config = AssetDatabase.LoadAssetAtPath<ARCoreExtensionsConfig>(configPath);
        if (config == null)
        {
            config = ScriptableObject.CreateInstance<ARCoreExtensionsConfig>();
            AssetDatabase.CreateAsset(config, configPath);
        }
        config.GeospatialMode = HasKey ? GeospatialMode.Enabled : GeospatialMode.Disabled;
        EditorUtility.SetDirty(config);
        var filter = AssetDatabase.LoadAssetAtPath<ARCoreExtensionsCameraConfigFilter>(filterPath);
        if (filter == null)
        {
            filter = ScriptableObject.CreateInstance<ARCoreExtensionsCameraConfigFilter>();
            AssetDatabase.CreateAsset(filter, filterPath);
        }
        return (config, filter);
    }

    /// <summary>Materials for the map and gems, in Resources so the app can load them and the build keeps their shaders.</summary>
    static void CreateMaterials()
    {
        Directory.CreateDirectory("Assets/Resources/Materials");
        foreach (var (name, shader) in new[] { ("Gem", "Hololoco/Gem"), ("Building", "Hololoco/Building"), ("Glow", "Hololoco/Glow"), ("Tile", "Hololoco/Tile") })
        {
            var path = $"Assets/Resources/Materials/{name}.mat";
            var found = Shader.Find(shader);
            if (found == null)
            {
                Debug.LogError($"HOLOLOCO shader missing: {shader}");
                continue;
            }
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(found);
                AssetDatabase.CreateAsset(material, path);
            }
            material.shader = found;
            EditorUtility.SetDirty(material);
        }
    }

    /// <summary>
    /// The one scene. The map (home) and the AR encounter share it: the map lives on
    /// layer 8 and is seen only by the map camera; the bag's preview is on layer 9.
    /// The app switches between the map camera and the AR camera and session.
    /// </summary>
    static void BuildScene(PanelSettings panel, ARCoreExtensionsConfig config, ARCoreExtensionsCameraConfigFilter filter)
    {
        const int mapLayer = 8, previewLayer = 9;
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var light = new GameObject("Light").AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.1f;
        light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.55f);

        // AR: the session (off until a drop is opened) and the camera rig.
        var session = new GameObject("AR Session").AddComponent<ARSession>();
        session.enabled = false;
        // The input manager starts AR's input subsystem, which is what feeds the phone's
        // tracked pose to the camera's TrackedPoseDriver. Without it the AR camera never
        // moves, so anything placed in the world looks stuck to the screen.
        session.gameObject.AddComponent<ARInputManager>();
        var originObject = new GameObject("XR Origin");
#pragma warning disable CS0618 // ARCore Extensions still asks for an ARSessionOrigin (an XROrigin).
        var origin = originObject.AddComponent<ARSessionOrigin>();
#pragma warning restore CS0618
        var offset = new GameObject("Camera Offset");
        offset.transform.SetParent(originObject.transform, false);
        var cameraObject = new GameObject("AR Camera") { tag = "MainCamera" };
        cameraObject.transform.SetParent(offset.transform, false);
        var arCamera = cameraObject.AddComponent<Camera>();
        arCamera.clearFlags = CameraClearFlags.SolidColor;
        arCamera.backgroundColor = Color.black;
        arCamera.nearClipPlane = 0.05f;
        arCamera.farClipPlane = 500f;
        arCamera.cullingMask = ~((1 << mapLayer) | (1 << previewLayer));
        arCamera.enabled = false;
        var cameraManager = cameraObject.AddComponent<ARCameraManager>();
        cameraObject.AddComponent<ARCameraBackground>();
        var pose = cameraObject.AddComponent<UnityEngine.InputSystem.XR.TrackedPoseDriver>();
        pose.positionInput = new InputActionProperty(new InputAction("Position", binding: "<HandheldARInputDevice>/devicePosition"));
        pose.rotationInput = new InputActionProperty(new InputAction("Rotation", binding: "<HandheldARInputDevice>/deviceRotation"));
        origin.Camera = arCamera;
        origin.CameraFloorOffsetObject = offset;
        // XR Origin lifts the camera by a default standing height (1.1176 m) in device
        // tracking mode, which phone AR reports. The planes ARCore finds aren't lifted, so
        // the floor would sit a metre below where the camera thinks it is. No lift.
        origin.CameraYOffset = 0f;
        // Floor finding for encounters: the drop stands on the lowest horizontal plane.
        var planes = originObject.AddComponent<ARPlaneManager>();
        planes.requestedDetectionMode = UnityEngine.XR.ARSubsystems.PlaneDetectionMode.Horizontal;
        // No ARCore Extensions (Geospatial) in the scene: encounters appear in front of the
        // player, and ARCore Extensions crashes when the AR session starts switched off.
        // The precise "explore" mode (Scripts/Later) will bring it back, enabled with the session.

        // The map and its camera.
        var mapObject = new GameObject("Map") { layer = mapLayer };
        var map = mapObject.AddComponent<MapView>();
        var mapCameraObject = new GameObject("Map Camera");
        var mapCamera = mapCameraObject.AddComponent<Camera>();
        mapCamera.clearFlags = CameraClearFlags.SolidColor;
        mapCamera.backgroundColor = new Color(0.035f, 0.06f, 0.11f, 1f);
        mapCamera.cullingMask = 1 << mapLayer;
        mapCamera.fieldOfView = 40f;
        mapCamera.nearClipPlane = 1f;
        mapCamera.farClipPlane = 4000f;
        mapCameraObject.AddComponent<AudioListener>();
        map.Camera = mapCamera;

        var encounter = new GameObject("Encounter").AddComponent<Encounter>();
        encounter.Camera = arCamera;
        encounter.Session = session;
        encounter.Planes = planes;

        var preview = new GameObject("Preview").AddComponent<Preview>();

        var appObject = new GameObject("Hololoco");
        var document = appObject.AddComponent<UIDocument>();
        document.panelSettings = panel;
        var location = appObject.AddComponent<Location>();
        encounter.Location = location;
        var app = appObject.AddComponent<App>();
        app.Document = document;
        app.Location = location;
        app.Map = map;
        app.MapCamera = mapCamera;
        app.ArCamera = arCamera;
        app.Session = session;
        app.Encounter = encounter;
        app.Preview = preview;

        Directory.CreateDirectory("Assets/Scenes");
        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
    }

    // ------------------------------------------------------------------ build

    public static void BuildAndroid()
    {
        Configure();
        Directory.CreateDirectory(Path.GetDirectoryName(ApkPath));
        EditorUserBuildSettings.buildAppBundle = false;
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = ApkPath,
            target = BuildTarget.Android,
            options = BuildOptions.None,
        });
        var summary = report.summary;
        Debug.Log($"HOLOLOCO build: {summary.result}, {summary.totalErrors} errors, {summary.totalSize / (1024 * 1024)} MB, {summary.totalTime}");
        EditorApplication.Exit(summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
