using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using MysterySort;

public static class SortDemoBuilder
{
    const string ScenePath = "Assets/Scenes/MysterySort.unity";
    const string PrefabPath = "Assets/Generated/Collaborator.prefab";
    const string ControllerPath = "Assets/Generated/Actor.controller";

    static AnimationClip LoopingClip(string path)
    {
        var importer = (ModelImporter)AssetImporter.GetAtPath(path);
        if (importer == null) throw new Exception("Missing animation: " + path);
        var clips = importer.clipAnimations.Length > 0 ? importer.clipAnimations : importer.defaultClipAnimations;
        if (clips.Any(c => !c.loopTime))
        {
            foreach (var clip in clips) clip.loopTime = true;
            importer.clipAnimations = clips; importer.SaveAndReimport();
        }
        var loaded = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview__"));
        if (loaded == null) throw new Exception("No clip in " + path);
        return loaded;
    }

    [MenuItem("Mystery Sort/Generate scene and settings")]
    public static void Generate()
    {
        Directory.CreateDirectory("Assets/Scenes"); Directory.CreateDirectory("Assets/Generated");
        PlayerSettings.companyName = "LocalVR"; PlayerSettings.productName = "Mystery Sort Demo";
        PlayerSettings.runInBackground = true;
        PlayerSettings.defaultScreenWidth = 1000; PlayerSettings.defaultScreenHeight = 800;
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.colorSpace = ColorSpace.Linear;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });

        // Mixamo idle + walk on one blend tree, driven by a single Speed float.
        AnimationClip idle = LoopingClip("Assets/MixamoAnim/Idle.fbx"), walk = LoopingClip("Assets/MixamoAnim/Walking.fbx");
        AssetDatabase.DeleteAsset(ControllerPath);
        var controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
        controller.CreateBlendTreeInController("Locomotion", out BlendTree tree);
        tree.blendParameter = "Speed"; tree.AddChild(idle, 0f); tree.AddChild(walk, 1f);

        var surface = new Material(Shader.Find("Standard")); surface.SetFloat("_Glossiness", 0.15f);
        AssetDatabase.DeleteAsset("Assets/Generated/Surface.mat");
        AssetDatabase.CreateAsset(surface, "Assets/Generated/Surface.mat");

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null) throw new Exception("Missing avatar prefab: " + PrefabPath);
        var animator = prefab.GetComponentInChildren<Animator>();
        if (animator == null || animator.avatar == null || !animator.avatar.isHuman) throw new Exception("Avatar prefab is not humanoid");

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var cam = new GameObject("Desktop Camera", typeof(Camera), typeof(AudioListener)).GetComponent<Camera>();
        cam.fieldOfView = 62; cam.nearClipPlane = 0.05f;
        cam.backgroundColor = new Color(0.08f, 0.12f, 0.18f); cam.clearFlags = CameraClearFlags.SolidColor;
        var demo = new GameObject("Mystery Sort Demo").AddComponent<SortDemo>();
        demo.cam = cam; demo.avatarPrefab = prefab; demo.controller = controller; demo.surface = surface;
        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        AssetDatabase.SaveAssets();
        Debug.Log("SORT_DEMO_GENERATED");
    }

    public static void Build()
    {
        Generate();
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath }, locationPathName = "../Windows/MysterySort.exe",
            target = BuildTarget.StandaloneWindows64, options = BuildOptions.Development
        });
        if (report.summary.result != BuildResult.Succeeded) throw new Exception("Build failed: " + report.summary.result);
        Debug.Log("SORT_DEMO_BUILD_OK");
    }
}
