using PoBox;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace PoBox.Editor
{
    /// <summary>
    /// Builds SCN_TEST_WALK_CONTEST: the roster lined up along one edge of a
    /// ring-sized lane, racing straight to the far edge under
    /// <see cref="Systems_WalkContest"/>. Each fighter loads its walk brain
    /// from Assets/Agents/&lt;Name&gt;_Walk/Boxer.onnx and falls back to the
    /// balance brain, then to raw physics, so the scene is always playable.
    /// Visual test scene — camera and light included, no trainer needed:
    /// just press Play.
    /// </summary>
    internal static class RigTool_WalkContestScene
    {
        private const string SCENE_PATH = "Assets/Scenes/SCN_TEST_WALK_CONTEST.unity";
        private const float RING_SIZE = 6.1f;      // matches SCN_TRAIN_WALK
        private const float EDGE_INSET = 0.25f;
        private const float SPAWN_HEIGHT = 0.03f;
        // Shared stand-and-walk brain; the walk race commands it to 1 m/s.
        private const string LOCOMOTION_BRAIN_PATH = "Assets/Agents/Locomotion_v01/Boxer.onnx";

        // Mirrors the balance contest roster so the two mini-games field the
        // same line-up. forceHeuristic: the code-driven PD bot (project rule).
        private static readonly (string prefabPath, string display, bool forceHeuristic)[] Roster =
        {
            ("Assets/Prefabs/Fighters/Fighter_Capsule.prefab", "Standard", false),
            ("Assets/Prefabs/Fighters/Fighter_Grandma.prefab", "Grandma", false),
            ("Assets/Prefabs/Fighters/Fighter_Grandpa.prefab", "Grandpa", false),
            ("Assets/Prefabs/Fighters/Fighter_Capsule.prefab", "Bot", true)
        };

        [MenuItem("Tools/ML Boxing/8. Create Walk Contest Scene")]
        public static void Create()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            float half = RING_SIZE * 0.5f;
            float startZ = -half + EDGE_INSET;
            float goalZ = half - EDGE_INSET;
            float goalDistance = goalZ - startZ;
            float laneSpacing = RING_SIZE / Roster.Length;
            float laneOrigin = -laneSpacing * (Roster.Length - 1) * 0.5f;

            BuildGround();
            BuildLight();
            BuildCamera(goalZ);
            BuildFinishLine(goalZ);

            // The referee lives under an inactive root the spawner wakes, so
            // it discovers the racers in its own Start AFTER they exist.
            var systemsRoot = new GameObject("ContestSystems");
            BuildReferee(systemsRoot, goalDistance);
            systemsRoot.SetActive(false);

            var slots = new Vector3[Roster.Length];
            for (int slotIndex = 0; slotIndex < Roster.Length; slotIndex++)
            {
                slots[slotIndex] = new Vector3(laneOrigin + slotIndex * laneSpacing, SPAWN_HEIGHT, startZ);
            }
            BuildSpawner(systemsRoot, slots);

            EditorSceneManager.SaveScene(scene, SCENE_PATH);
            Debug.Log($"RigTool: walk contest scene saved to {SCENE_PATH} — {slots.Length} start slots, " +
                $"{goalDistance:F2} m to the finish. Launch from SCN_MENU, or press Play to race the default line-up.");
        }

        // Builds the spawner's roster: walk brain first, balance brain as a
        // fallback, heuristic bot when neither exists. Same order as the
        // dropdown list in SCN_MENU, because the pick travels as a roster index.
        private static void BuildSpawner(GameObject systemsRoot, Vector3[] slots)
        {
            var entries = new ContestRosterEntry[Roster.Length];
            for (int rosterIndex = 0; rosterIndex < Roster.Length; rosterIndex++)
            {
                (string prefabPath, string display, bool forceHeuristic) = Roster[rosterIndex];
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab == null)
                {
                    Debug.LogWarning($"RigTool: walk contest roster is missing prefab {prefabPath}");
                }
                bool isLocomotion = false;
                Unity.InferenceEngine.ModelAsset model = forceHeuristic
                    ? null
                    : ResolveBrain(display, out isLocomotion);
                entries[rosterIndex] = new ContestRosterEntry
                {
                    displayName = display,
                    prefab = prefab,
                    model = model,
                    forceHeuristic = forceHeuristic,
                    locomotionBrain = model != null && isLocomotion,
                    tint = forceHeuristic
                        ? AssetDatabase.LoadAssetAtPath<Material>("Assets/Art/M_BotRed.mat")
                        : null
                };
            }

            var spawnerObject = new GameObject("MiniGameLauncher");
            var spawner = spawnerObject.AddComponent<Systems_ContestSpawner>();
            spawner.EditorInitialize(entries, systemsRoot, null);
            // Identity faces +Z, the direction of travel.
            spawner.EditorSetSlots(slots, Vector3.zero);

            var launcher = spawnerObject.AddComponent<Systems_MiniGameLauncher>();
            launcher.EditorInitialize(spawner, RigTool_MenuScene.GetOrCreateSelectionAsset());
        }

        // Brain preference, best first:
        //   1. a per-fighter walk brain
        //   2. the shared locomotion brain — one model line drives both
        //      mini-games, so this is the normal case once training lands
        //   3. the old balance brain, which stands but will not race
        // isLocomotion tells the spawner to switch the fighter to the
        // 125-observation layout those brains require.
        private static Unity.InferenceEngine.ModelAsset ResolveBrain(string display, out bool isLocomotion)
        {
            isLocomotion = true;
            var walkModel = AssetDatabase.LoadAssetAtPath<Unity.InferenceEngine.ModelAsset>(
                $"Assets/Agents/{display}_Walk/Boxer.onnx");
            if (walkModel != null)
            {
                return walkModel;
            }
            var locomotionModel = AssetDatabase.LoadAssetAtPath<Unity.InferenceEngine.ModelAsset>(
                LOCOMOTION_BRAIN_PATH);
            if (locomotionModel != null)
            {
                return locomotionModel;
            }

            isLocomotion = false;
            var balanceModel = AssetDatabase.LoadAssetAtPath<Unity.InferenceEngine.ModelAsset>(
                $"Assets/Agents/{display}/Boxer.onnx");
            Debug.LogWarning(balanceModel == null
                ? $"RigTool: no brain for {display} — it races on raw physics."
                : $"RigTool: no walk or locomotion brain — {display} races on its balance brain and will just stand.");
            return balanceModel;
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

        // Side-on so the whole lane is visible end to end — a head-on camera
        // would hide which racer is actually ahead.
        private static void BuildCamera(float goalZ)
        {
            var cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.Skybox;
            cameraObject.transform.position = new Vector3(-7.5f, 3f, 0f);
            cameraObject.transform.rotation = Quaternion.Euler(12f, 90f, 0f);
            _ = goalZ;
        }

        private static void BuildFinishLine(float goalZ)
        {
            GameObject finishLine = GameObject.CreatePrimitive(PrimitiveType.Cube);
            finishLine.name = "FinishLine";
            finishLine.transform.localScale = new Vector3(RING_SIZE, 0.02f, 0.12f);
            finishLine.transform.position = new Vector3(0f, 0.01f, goalZ);
            Object.DestroyImmediate(finishLine.GetComponent<BoxCollider>());
        }

        private static void BuildReferee(GameObject systemsRoot, float goalDistance)
        {
            var refereeObject = new GameObject("WalkContestReferee");
            refereeObject.transform.SetParent(systemsRoot.transform, false);
            var document = refereeObject.AddComponent<UIDocument>();
            document.panelSettings = RigTool_ContestScene.GetOrCreatePanelSettings();
            var referee = refereeObject.AddComponent<Systems_WalkContest>();
            referee.EditorInitialize(Vector3.forward, goalDistance);
        }
    }
}
