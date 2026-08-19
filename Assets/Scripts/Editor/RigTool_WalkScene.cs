using PoBox;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PoBox.Editor
{
    /// <summary>
    /// Builds SCN_TRAIN_WALK: four fighters lined up along one edge of a
    /// ring-sized lane, all facing the far edge, each carrying the balance
    /// reward (uprightness + the fall terminal) plus <see cref="Reward_Walk"/>
    /// for travel. The balance-only curricula — shover and training cubes —
    /// are switched off so the only thing left to learn is forward travel.
    /// Zero cameras, HUD, or audio: training scenes run headless.
    /// </summary>
    internal static class RigTool_WalkScene
    {
        private const string SCENE_PATH = "Assets/Scenes/SCN_TRAIN_WALK.unity";
        private const string FIGHTER_PREFAB_PATH = "Assets/Prefabs/Fighters/Fighter_Capsule.prefab";
        private const int WALKER_COUNT = 4;
        private const float RING_SIZE = 6.1f;          // matches Assets/Art/BoxingRing.glb
        private const float EDGE_INSET = 0.25f;        // keep spawns off the ropes
        private const int WALK_MAX_STEP = 3000;        // 60 s at 50 Hz, same budget as balance
        private const float SPAWN_HEIGHT = 0.03f;      // small clearance so feet do not spawn in contact

        [MenuItem("Tools/ML Boxing/6. Create Walk Training Scene")]
        public static void CreateWalkScene()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(FIGHTER_PREFAB_PATH);
            if (prefab == null)
            {
                EditorUtility.DisplayDialog("Walk Scene",
                    "Fighter prefab not found at " + FIGHTER_PREFAB_PATH +
                    ".\nBuild/prepare that fighter prefab first (Tools > ML Boxing).", "OK");
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Load AFTER NewScene: the scene switch unloads unreferenced
            // assets, silently nulling a reference loaded before it.
            Systems_FighterVariation variation = RigTool_BalanceScene.GetOrCreateVariationAsset();

            RigTool_BalanceScene.BuildGround();

            float half = RING_SIZE * 0.5f;
            float startZ = -half + EDGE_INSET;
            float goalZ = half - EDGE_INSET;
            float goalDistance = goalZ - startZ;
            float laneSpacing = RING_SIZE / WALKER_COUNT;
            float laneOrigin = -laneSpacing * (WALKER_COUNT - 1) * 0.5f;

            for (int walkerIndex = 0; walkerIndex < WALKER_COUNT; walkerIndex++)
            {
                var position = new Vector3(laneOrigin + walkerIndex * laneSpacing, SPAWN_HEIGHT, startZ);
                SpawnWalker(prefab, position, walkerIndex, variation, goalDistance);
            }

            BuildGoalLine(goalZ);

            EditorSceneManager.SaveScene(scene, SCENE_PATH);
            Debug.Log($"RigTool: walk training scene saved to {SCENE_PATH} — {WALKER_COUNT} fighters, " +
                $"{goalDistance:F2} m to the far edge. Train: mlagents-learn Config/BoxerWalk01.yaml --run-id=boxer_walk01");
        }

        private static void SpawnWalker(GameObject prefab, Vector3 position, int index,
            Systems_FighterVariation variation, float goalDistance)
        {
            GameObject instance = RigTool_BalanceScene.SpawnFighter(prefab, position, index, variation);

            // All four line up on the same edge and walk straight across, so
            // one shared world axis serves every fighter — no per-agent goal
            // observation is needed, which keeps the observation vector
            // identical to Stage 1 and lets init_path load a balance brain.
            instance.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);

            var agent = instance.GetComponent<Agent_FighterBoxing>();
            var rig = instance.GetComponent<Systems_FighterRig>();
            agent.MaxStep = WALK_MAX_STEP;

            // Stage-1 curricula only muddy the walking signal: shoves knock the
            // walker off its line and cubes are a balance stressor.
            var shover = instance.GetComponent<Systems_Shover>();
            if (shover != null)
            {
                shover.enabled = false;
            }
            var trainingCubes = instance.GetComponent<Systems_TrainingCubes>();
            if (trainingCubes != null)
            {
                trainingCubes.enabled = false;
            }

            var walkReward = instance.AddComponent<Reward_Walk>();
            walkReward.EditorInitialize(agent, rig, Vector3.forward, goalDistance);
        }

        // Flat, non-colliding strip on the ground so the far edge is visible
        // when the scene is opened in the Editor for inspection.
        private static void BuildGoalLine(float goalZ)
        {
            GameObject goalLine = GameObject.CreatePrimitive(PrimitiveType.Cube);
            goalLine.name = "GoalLine";
            goalLine.transform.localScale = new Vector3(RING_SIZE, 0.02f, 0.1f);
            goalLine.transform.position = new Vector3(0f, 0.01f, goalZ);
            Object.DestroyImmediate(goalLine.GetComponent<BoxCollider>());
        }
    }
}
