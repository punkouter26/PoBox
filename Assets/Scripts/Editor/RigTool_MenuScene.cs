using PoBox;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace PoBox.Editor
{
    /// <summary>
    /// Builds SCN_MENU and registers it as build index 0: the game's opening
    /// scene, where the player picks a mini-game (balance contest or walk
    /// race), fills each slot with a fighter, and starts. The picks travel to
    /// the mini-game scene in SO_MiniGameSelection.
    ///
    /// Reinstates a standalone menu scene, which was folded into the contest
    /// scene on 2026-08-18. With two mini-games to choose between, the menu is
    /// no longer specific to the contest, and the contest's own menu now defers
    /// to this one whenever a selection has been made.
    /// </summary>
    internal static class RigTool_MenuScene
    {
        private const string SCENE_PATH = "Assets/Scenes/SCN_MENU.unity";
        private const string SELECTION_PATH = "Assets/Config/SO_MiniGameSelection.asset";

        [MenuItem("Tools/ML Boxing/1. Create Menu Scene")]
        public static void Create()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Load AFTER NewScene: the scene switch unloads unreferenced assets.
            Systems_MiniGameSelection selection = GetOrCreateSelectionAsset();

            var cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.05f, 0.05f, 0.07f);

            var menuObject = new GameObject("MiniGameMenu");
            var document = menuObject.AddComponent<UIDocument>();
            document.panelSettings = RigTool_ContestScene.GetOrCreatePanelSettings();
            var menu = menuObject.AddComponent<Systems_MiniGameMenu>();
            menu.EditorInitialize(selection);

            EditorSceneManager.SaveScene(scene, SCENE_PATH);
            RegisterAsFirstBuildScene();
            Debug.Log($"RigTool: menu scene saved to {SCENE_PATH} and registered as build index 0.");
        }

        /// <summary>Shared by the mini-game scene builders so every scene points at one selection asset.</summary>
        internal static Systems_MiniGameSelection GetOrCreateSelectionAsset()
        {
            var selection = AssetDatabase.LoadAssetAtPath<Systems_MiniGameSelection>(SELECTION_PATH);
            if (selection != null)
            {
                return selection;
            }
            if (!AssetDatabase.IsValidFolder("Assets/Config"))
            {
                AssetDatabase.CreateFolder("Assets", "Config");
            }
            selection = ScriptableObject.CreateInstance<Systems_MiniGameSelection>();
            AssetDatabase.CreateAsset(selection, SELECTION_PATH);
            AssetDatabase.SaveAssets();
            return selection;
        }

        private static void RegisterAsFirstBuildScene()
        {
            var scenes = new System.Collections.Generic.List<EditorBuildSettingsScene>
            {
                new(SCENE_PATH, true)
            };
            EditorBuildSettingsScene[] existing = EditorBuildSettings.scenes;
            for (int sceneIndex = 0; sceneIndex < existing.Length; sceneIndex++)
            {
                if (existing[sceneIndex].path != SCENE_PATH)
                {
                    scenes.Add(existing[sceneIndex]);
                }
            }
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
