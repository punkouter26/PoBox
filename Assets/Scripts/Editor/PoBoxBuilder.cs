using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;
using PoBox.UI;

namespace PoBox.EditorTools
{
    /// <summary>
    /// The project's settings, the arena's lighting bake and the first screen.
    ///
    /// The arena (<c>Assets/Scenes/Arena.unity</c>) is no longer built by code: it is an authored scene, its
    /// boxers prefabs of the MuJoCo plugin's components (<c>Assets/Boxers</c>, made by <see cref="MjRetrofit"/>),
    /// and it is edited in the editor like any other. What is still made here: the project settings, the
    /// lighting bake, and the menu scene's cards, which are a list of the boxers there are.
    /// </summary>
    public static class PoBoxBuilder
    {
        public const string ScenePath = "Assets/Scenes/Arena.unity";
        public const string MenuScenePath = "Assets/Scenes/Menu.unity";

        static readonly Color Red = new Color(0.89f, 0.24f, 0.24f);
        static readonly Color Blue = new Color(0.20f, 0.49f, 0.94f);

        [MenuItem("PoBox/Apply Project Settings", priority = 1)]
        public static void ProjectSetup()
        {
            PlayerSettings.companyName = "Po";
            PlayerSettings.productName = "PoBox";
            PlayerSettings.bundleVersion = "0.1.0";
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;
            PlayerSettings.runInBackground = true;
            PlayerSettings.defaultScreenWidth = 540;
            PlayerSettings.defaultScreenHeight = 960;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;

            // The editor must keep ticking when it is not the focused window, or anything driving it from
            // outside stalls. Interaction Mode: No Throttling.
            EditorPrefs.SetInt("InteractionMode", 1);
            EditorPrefs.SetInt("ApplicationIdleTime", 0);
            // Script Changes While Playing: Recompile After Finished Playing. Reloading scripts under a
            // running bout empties every static and drops both fighters' policies; they fall over and stay there.
            EditorPrefs.SetInt("ScriptCompilationDuringPlay", 1);

            var pc = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(AssetBakery.SettingsDir + "/PC_RPAsset.asset");
            var mobile = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(AssetBakery.SettingsDir + "/Mobile_RPAsset.asset");
            var global = AssetDatabase.LoadAssetAtPath<RenderPipelineGlobalSettings>(AssetBakery.SettingsDir + "/UniversalRenderPipelineGlobalSettings.asset");
            if (pc == null || mobile == null) throw new Exception("URP assets missing from Assets/Settings.");

            // A boxing ring is small: shadows only need to reach across the hall, not across a rooftop.
            pc.shadowDistance = 30f;
            mobile.shadowDistance = 20f;
            EditorUtility.SetDirty(pc);
            EditorUtility.SetDirty(mobile);

            if (GraphicsSettings.defaultRenderPipeline != pc) GraphicsSettings.defaultRenderPipeline = pc;
            if (global != null) EditorGraphicsSettings.SetRenderPipelineGlobalSettingsAsset<UniversalRenderPipeline>(global);

            // Two quality levels, as copied from PoDecath: Mobile (0) and PC (1), each with its own pipeline asset.
            int current = QualitySettings.GetQualityLevel();
            string[] names = QualitySettings.names;
            bool switched = false;
            for (int i = 0; i < names.Length; i++)
            {
                // Only where it is not already so: switching the pipeline asset changes the scripting
                // defines, and that queues a recompile every time this runs.
                RenderPipelineAsset want = names[i] == "Mobile" ? mobile : pc;
                if (QualitySettings.GetRenderPipelineAssetAt(i) == want) continue;
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = want;
                switched = true;
            }
            if (switched) QualitySettings.SetQualityLevel(Mathf.Clamp(current, 0, names.Length - 1), false);

            // The fight is MuJoCo's (the MuJoCo plugin, Packages/org.mujoco). Unity's physics steps only when a
            // script asks it to, and none does.
            Physics.simulationMode = SimulationMode.Script;

            // Light cookies and the decal renderer feature, on both tiers.
            StageBakery.PreparePipeline();

            AssetDatabase.SaveAssets();
            Debug.Log("[PoBox] project settings applied (Linear, portrait, URP, run in background).");
        }

        public static bool BakeOnBuild
        {
            get => EditorPrefs.GetBool("pobox.bakeOnBuild", true);
            set => EditorPrefs.SetBool("pobox.bakeOnBuild", value);
        }

        [MenuItem("PoBox/Bake Arena Lighting", priority = 5)]
        public static void BakeNow()
        {
            if (EditorSceneManager.GetActiveScene().path != ScenePath) EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (StageBakery.BakeLighting()) EditorSceneManager.SaveOpenScenes();
        }

        /// <summary>The menu is the scene the app starts in, when there are boxers to choose between.</summary>
        static void SetBuildScenes(bool withMenu)
        {
            EditorBuildSettings.scenes = withMenu
                ? new[] { new EditorBuildSettingsScene(MenuScenePath, true), new EditorBuildSettingsScene(ScenePath, true) }
                : new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }

        // ---------------------------------------------------------------- menu scene

        /// <summary>
        /// The first screen: a camera with nothing to look at and one interface document, with a card for
        /// every trained boxer in each corner's column. Not built when there are no trained boxers; the app
        /// then starts in the arena with the stand-ins.
        /// </summary>
        [MenuItem("PoBox/Build Menu Scene", priority = 3)]
        public static void BuildMenuScene()
        {
            string[] entrants = MjRetrofit.Boxers();
            if (entrants.Length == 0)
            {
                SetBuildScenes(false);
                Debug.Log("[PoBox] no boxers in Assets/Boxers, so no menu scene: the app starts in the arena.");
                return;
            }

            AssetBakery.EnsureFolder("Assets/Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            PanelSettings panel = AssetBakery.Panel();
            var cards = new List<MenuView.Boxer>();
            foreach (string entrant in entrants) cards.Add(MjRetrofit.Card(entrant));

            var cameraGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var camera = cameraGo.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.008f, 0.010f, 0.016f);
            camera.cullingMask = 0;
            cameraGo.AddComponent<AudioListener>();

            // Switched off while it is put together: the menu draws itself in the editor too, and should
            // first do so with its boxers already in hand.
            var menuGo = new GameObject("Menu");
            menuGo.SetActive(false);
            var document = menuGo.AddComponent<UIDocument>();
            document.panelSettings = panel;
            document.visualTreeAsset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(AssetBakery.MenuUxml);
            if (document.visualTreeAsset == null) Debug.LogError("[PoBox] Menu.uxml did not import; the menu will be empty.");
            var view = menuGo.AddComponent<MenuView>();
            view.boxers = cards.ToArray();
            view.redColor = Red;
            view.blueColor = Blue;
            menuGo.SetActive(true);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, MenuScenePath);
            SetBuildScenes(true);
            AssetDatabase.SaveAssets();
            Debug.Log($"[PoBox] built {MenuScenePath} with {cards.Count} boxer(s) to choose from.");
        }
    }
}
