using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using MysterySort;

// Builds the two standalone Quest APKs (room A and room B). Lives only in the UnityQuest project.
public static class QuestSortBuilder
{
    const string WorkRoot = "../../../work/";

    [MenuItem("Mystery Sort/Build Quest A and B")]
    public static void BuildBoth()
    {
#if UNITY_EDITOR_WIN
        // Windows packaged-app temp redirection can break JDK Unix-domain wakeup sockets.
        string javaTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Codex", "work", "quest-java");
        Directory.CreateDirectory(javaTemp);
        string javaOptions = Environment.GetEnvironmentVariable("JAVA_TOOL_OPTIONS") ?? "";
        if (!javaOptions.Contains("jdk.net.unixdomain.tmpdir"))
            Environment.SetEnvironmentVariable("JAVA_TOOL_OPTIONS", javaOptions + " \"-Djdk.net.unixdomain.tmpdir=" + javaTemp + "\"");
#endif
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.Android, ManagedStrippingLevel.Low);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
        PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
        PlayerSettings.Android.forceInternetPermission = true;
        PlayerSettings.Android.useCustomKeystore = false;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
        EditorUserBuildSettings.buildAppBundle = false;
        // Export first so Gradle's Java environment can be controlled explicitly.
        EditorUserBuildSettings.exportAsGoogleAndroidProject = true;
        EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC;
        QualitySettings.shadows = ShadowQuality.Disable;
        QualitySettings.antiAliasing = 4;
        ConfigureXR();
        var config = OVRProjectConfig.CachedProjectConfig;
        config.handTrackingSupport = OVRProjectConfig.HandTrackingSupport.ControllersAndHands;
        EditorUtility.SetDirty(config);
        Directory.CreateDirectory("../Quest-APK");

        SortDemoBuilder.Generate(); // scene, Mixamo controller, material
        PlayerSettings.companyName = "LocalVR";
        PlayerSettings.colorSpace = ColorSpace.Linear;

        foreach (var role in new[] { SortDemo.Role.A, SortDemo.Role.B })
        {
            var scene = EditorSceneManager.OpenScene("Assets/Scenes/MysterySort.unity");
            var demo = UnityEngine.Object.FindFirstObjectByType<SortDemo>();
            demo.role = role; demo.vr = true;
            demo.port = role == SortDemo.Role.A ? SortDemo.RelayPort : SortDemo.RoomBPort;

            var rigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Packages/com.meta.xr.sdk.core/Prefabs/OVRCameraRig.prefab");
            var rig = (GameObject)PrefabUtility.InstantiatePrefab(rigPrefab); rig.name = "Quest Rig";
            foreach (var emulator in rig.GetComponentsInChildren<OVRHeadsetEmulator>(true)) UnityEngine.Object.DestroyImmediate(emulator);
            var cameraRig = rig.GetComponent<OVRCameraRig>();
            rig.GetComponent<OVRManager>().trackingOriginType = OVRManager.TrackingOrigin.FloorLevel;
            var handPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Packages/com.meta.xr.sdk.core/Prefabs/OVRHandPrefab.prefab");
            var right = (GameObject)PrefabUtility.InstantiatePrefab(handPrefab); right.name = "Right tracked hand";
            right.transform.SetParent(cameraRig.rightHandAnchor, false);
            var hand = right.GetComponent<OVRHand>(); SetEnum(hand, "HandType", 1);
            SetEnum(right.GetComponent<OVRSkeleton>(), "_skeletonType", 1);
            var mesh = right.GetComponent<OVRMesh>(); if (mesh != null) SetEnum(mesh, "_meshType", 1);
            demo.questRig = rig; demo.rightHand = hand; rig.SetActive(false);

            string scenePath = "Assets/Scenes/QuestSort" + role + ".unity";
            EditorSceneManager.SaveScene(scene, scenePath);
            PlayerSettings.productName = "Mystery Sort " + role;
            PlayerSettings.bundleVersion = "0.1.0";
            PlayerSettings.Android.bundleVersionCode = 1;
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.localvr.mysterysort." + role.ToString().ToLowerInvariant());
            AssetDatabase.SaveAssets();
            string gradlePath = Path.GetFullPath(WorkRoot + "quest-gradle-sort/" + role);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { scenePath }, locationPathName = gradlePath,
                target = BuildTarget.Android, options = BuildOptions.Development | BuildOptions.AcceptExternalModificationsToPlayer
            });
            if (report.summary.result != BuildResult.Succeeded) throw new Exception("Quest " + role + " build failed: " + report.summary.result);
            RunGradle(gradlePath, role.ToString());
            Debug.Log("SORT_QUEST_APK_OK " + role);
        }
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene("Assets/Scenes/QuestSortA.unity", true) };
        AssetDatabase.SaveAssets();
        Debug.Log("SORT_QUEST_BUILD_OK");
    }

    static void SetEnum(UnityEngine.Object obj, string property, int value)
    {
        var so = new SerializedObject(obj); var p = so.FindProperty(property);
        if (p == null) throw new Exception("Missing property: " + property);
        p.intValue = value; so.ApplyModifiedPropertiesWithoutUndo();
    }

    static void RunGradle(string directory, string role)
    {
        string android = Path.Combine(EditorApplication.applicationContentsPath, "PlaybackEngines", "AndroidPlayer");
        string java = Path.Combine(android, "OpenJDK", "bin", "java.exe");
        string launcher = Path.Combine(android, "Tools", "gradle", "lib", "gradle-launcher-8.11.jar");
        var info = new System.Diagnostics.ProcessStartInfo(java,
            "-classpath \"" + launcher + "\" org.gradle.launcher.GradleMain --no-daemon assembleDebug")
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        string temp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Codex", "work", "quest-java");
        info.EnvironmentVariables["JAVA_TOOL_OPTIONS"] = "\"-Djdk.net.unixdomain.tmpdir=" + temp + "\" -Djava.net.preferIPv4Stack=true";
        string logPath = Path.GetFullPath(WorkRoot + "quest-gradle-sort-" + role + ".log");
        using (var log = new StreamWriter(logPath))
        using (var process = new System.Diagnostics.Process { StartInfo = info })
        {
            process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (log) { log.WriteLine(e.Data); log.Flush(); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (log) { log.WriteLine(e.Data); log.Flush(); } };
            process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine(); process.WaitForExit();
            if (process.ExitCode != 0) throw new Exception("Gradle failed; inspect " + logPath);
        }
        File.Copy(Path.Combine(directory, "launcher", "build", "outputs", "apk", "debug", "launcher-debug.apk"), "../Quest-APK/MysterySort-" + role + ".apk", true);
    }

    static void ConfigureXR()
    {
        EditorBuildSettings.TryGetConfigObject<XRGeneralSettingsPerBuildTarget>(XRGeneralSettings.k_SettingsKey, out var container);
        if (container == null) throw new Exception("XR settings missing");
        if (!container.HasSettingsForBuildTarget(BuildTargetGroup.Android)) container.CreateDefaultSettingsForBuildTarget(BuildTargetGroup.Android);
        if (!container.HasManagerSettingsForBuildTarget(BuildTargetGroup.Android)) container.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.Android);
        var settings = container.SettingsForBuildTarget(BuildTargetGroup.Android);
        settings.InitManagerOnStart = false; // SortDemo initializes once before enabling the OVR rig.
        if (!XRPackageMetadataStore.AssignLoader(settings.Manager, "UnityEngine.XR.OpenXR.OpenXRLoader", BuildTargetGroup.Android)) throw new Exception("OpenXR loader unavailable");
        var xr = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
        xr.renderMode = OpenXRSettings.RenderMode.SinglePassInstanced;
        foreach (var feature in xr.GetFeatures<UnityEngine.XR.OpenXR.Features.OpenXRFeature>())
        {
            string name = feature.GetType().Name;
            if (name == "OculusQuestFeature") feature.enabled = false;
            if (name == "MetaXRFeature" || name == "OculusTouchControllerProfile" || name == "MetaQuestFeature") feature.enabled = true;
        }
        EditorUtility.SetDirty(container); EditorUtility.SetDirty(settings); EditorUtility.SetDirty(settings.Manager); EditorUtility.SetDirty(xr);
        AssetDatabase.SaveAssets();
    }
}
