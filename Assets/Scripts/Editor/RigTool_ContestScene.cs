using System.Collections.Generic;
using Unity.MLAgents.Policies;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace PoBox.Editor
{
    /// <summary>
    /// Builds SCN_TEST_BALANCE_CONTEST: every available fighter prefab lined
    /// up on one high-friction floor, each driven by its brain from
    /// Assets/Agents/&lt;Name&gt;/Boxer.onnx when one exists (zero-action
    /// physics otherwise). A referee times standing, crowns the longest, and
    /// restarts rounds. Visual test scene — has a camera and light, no
    /// trainer needed: just press Play.
    /// </summary>
    internal static class RigTool_ContestScene
    {
        private const string SCENE_PATH = "Assets/Scenes/SCN_TEST_BALANCE_CONTEST.unity";
        private const string PANEL_SETTINGS_PATH = "Assets/UI/PS_Contest.asset";
        private const string THEME_PATH = "Assets/UI/TSS_Contest.tss";
        private const float LINE_SPACING = 2f;
        private const float SPAWN_HEIGHT = 0.03f;

        private const int JOINT_INDEX_SHIN_L = 3;
        private const int JOINT_INDEX_SHIN_R = 9;

        private static readonly (string prefabPath, string display)[] Roster =
        {
            ("Assets/Prefabs/Fighters/Fighter_Capsule.prefab", "Standard"),
            ("Assets/Prefabs/Fighters/Fighter_Grandma.prefab", "Grandma"),
            ("Assets/Prefabs/Fighters/Fighter_Grandpa.prefab", "Grandpa")
        };

        [MenuItem("Tools/ML Boxing/7. Create Balance Contest Scene")]
        public static void Create()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            BuildGround();
            BuildLight();
            BuildCamera();

            int placed = 0;
            float origin = -LINE_SPACING * (Roster.Length - 1) * 0.5f;
            for (int rosterIndex = 0; rosterIndex < Roster.Length; rosterIndex++)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Roster[rosterIndex].prefabPath);
                if (prefab == null)
                {
                    Debug.LogWarning($"RigTool: contest skips missing prefab {Roster[rosterIndex].prefabPath}");
                    continue;
                }
                SpawnContestant(prefab, Roster[rosterIndex].display,
                    new Vector3(origin + rosterIndex * LINE_SPACING, SPAWN_HEIGHT, 0f));
                placed++;
            }

            BuildReferee();

            EditorSceneManager.SaveScene(scene, SCENE_PATH);
            Debug.Log($"RigTool: contest scene saved to {SCENE_PATH} — {placed} contestants. Press Play (no trainer needed).");
        }

        private static void SpawnContestant(GameObject prefab, string display, Vector3 position)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            instance.name = "Contest_" + display;
            instance.transform.position = position;

            var rig = instance.GetComponent<Systems_FighterRig>();
            var agent = instance.GetComponent<Agent_FighterBoxing>();
            var stamina = instance.GetComponent<Systems_Stamina>();
            agent.MaxStep = 0; // the referee owns the round lifecycle
            stamina.enabled = false;

            rig.Torso.gameObject.AddComponent<Sensor_GroundContact>();
            rig.Head.gameObject.AddComponent<Sensor_GroundContact>();
            rig.Joints[JOINT_INDEX_SHIN_L].body.gameObject.AddComponent<Sensor_GroundContact>();
            rig.Joints[JOINT_INDEX_SHIN_R].body.gameObject.AddComponent<Sensor_GroundContact>();
            rig.GloveLeft.gameObject.AddComponent<Sensor_GroundContact>();
            rig.GloveRight.gameObject.AddComponent<Sensor_GroundContact>();

            var behavior = instance.GetComponent<BehaviorParameters>();
            var model = AssetDatabase.LoadAssetAtPath<Unity.InferenceEngine.ModelAsset>($"Assets/Agents/{display}/Boxer.onnx");
            if (model != null)
            {
                behavior.Model = model;
                behavior.BehaviorType = BehaviorType.InferenceOnly;
            }
            else
            {
                behavior.BehaviorType = BehaviorType.HeuristicOnly; // zero actions: pure physics
                Debug.LogWarning($"RigTool: no brain at Assets/Agents/{display}/Boxer.onnx — {display} competes on raw physics.");
            }
        }

        private static void BuildGround()
        {
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(40f, 1f, 40f);
            ground.transform.position = new Vector3(0f, -0.5f, 0f);
            var highFriction = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(RigTool_Config.HIGH_FRICTION_MATERIAL_PATH);
            if (highFriction != null)
            {
                ground.GetComponent<Collider>().sharedMaterial = highFriction;
            }
        }

        private static void BuildLight()
        {
            var lightObject = new GameObject("Directional Light");
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            lightObject.transform.rotation = Quaternion.Euler(45f, -35f, 0f);
        }

        private static void BuildCamera()
        {
            var cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.Skybox;
            cameraObject.transform.position = new Vector3(0f, 1.5f, -4.5f);
            cameraObject.transform.rotation = Quaternion.Euler(5f, 0f, 0f);
        }

        private static void BuildReferee()
        {
            var refereeObject = new GameObject("ContestReferee");
            var document = refereeObject.AddComponent<UIDocument>();
            document.panelSettings = GetOrCreatePanelSettings();
            refereeObject.AddComponent<Systems_BalanceContest>();
        }

        private static PanelSettings GetOrCreatePanelSettings()
        {
            var settings = AssetDatabase.LoadAssetAtPath<PanelSettings>(PANEL_SETTINGS_PATH);
            if (settings != null)
            {
                return settings;
            }
            if (!AssetDatabase.IsValidFolder("Assets/UI"))
            {
                AssetDatabase.CreateFolder("Assets", "UI");
            }
            var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(THEME_PATH);
            if (theme == null)
            {
                theme = ScriptableObject.CreateInstance<ThemeStyleSheet>();
                AssetDatabase.CreateAsset(theme, THEME_PATH);
            }
            settings = ScriptableObject.CreateInstance<PanelSettings>();
            settings.themeStyleSheet = theme;
            AssetDatabase.CreateAsset(settings, PANEL_SETTINGS_PATH);
            AssetDatabase.SaveAssets();
            return settings;
        }
    }
}
