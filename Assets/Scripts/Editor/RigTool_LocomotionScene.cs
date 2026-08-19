using PoBox;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PoBox.Editor
{
    /// <summary>
    /// Builds SCN_TRAIN_LOCOMOTION: one model line that learns standing and
    /// walking together. Each fighter carries <see cref="Reward_Locomotion"/>
    /// and observes its commanded speed, so the "speed_command_max" curriculum
    /// walks the task from stand-still (0 m/s) up to walking pace, and the
    /// finished brain obeys whichever speed the game asks for.
    ///
    /// Replaces the split SCN_TRAIN_BALANCE / SCN_TRAIN_WALK pair. Those
    /// scenes and their brains stay valid but belong to the older model line —
    /// the extra command observations make the two layouts incompatible.
    /// Zero cameras, HUD, or audio: training scenes run headless.
    /// </summary>
    internal static class RigTool_LocomotionScene
    {
        private const string SCENE_PATH = "Assets/Scenes/SCN_TRAIN_LOCOMOTION.unity";
        private const string FIGHTER_PREFAB_PATH = "Assets/Prefabs/Fighters/Fighter_Capsule.prefab";
        private const int GRID_SIZE = 4;               // 16 fighters, same throughput as balance
        private const float GRID_SPACING = 8f;
        private const int LOCOMOTION_MAX_STEP = 3000;  // 60 s at 50 Hz
        private const float SPAWN_HEIGHT = 0.03f;

        private const int JOINT_INDEX_SHIN_L = 3;
        private const int JOINT_INDEX_SHIN_R = 9;

        [MenuItem("Tools/ML Boxing/9. Create Locomotion Training Scene")]
        public static void Create()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(FIGHTER_PREFAB_PATH);
            if (prefab == null)
            {
                EditorUtility.DisplayDialog("Locomotion Scene",
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

            float origin = -GRID_SPACING * (GRID_SIZE - 1) * 0.5f;
            for (int gridX = 0; gridX < GRID_SIZE; gridX++)
            {
                for (int gridZ = 0; gridZ < GRID_SIZE; gridZ++)
                {
                    var position = new Vector3(origin + gridX * GRID_SPACING, SPAWN_HEIGHT, origin + gridZ * GRID_SPACING);
                    ConvertToLocomotion(
                        RigTool_BalanceScene.SpawnFighter(prefab, position, gridX * GRID_SIZE + gridZ, variation));
                }
            }

            EditorSceneManager.SaveScene(scene, SCENE_PATH);
            Debug.Log($"RigTool: locomotion training scene saved to {SCENE_PATH} — {GRID_SIZE * GRID_SIZE} fighters. " +
                "Train: mlagents-learn Config/BoxerLocomotion01.yaml --run-id=boxer_locomotion01");
        }

        // SpawnFighter builds a balance-phase fighter; swap the reward and the
        // observation layout over to the locomotion model line.
        private static void ConvertToLocomotion(GameObject instance)
        {
            var agent = instance.GetComponent<Agent_FighterBoxing>();
            var rig = instance.GetComponent<Systems_FighterRig>();
            agent.MaxStep = LOCOMOTION_MAX_STEP;

            // Reward_Balance owns a -1 fall terminal that drowns the per-step
            // signal; Reward_Locomotion replaces it outright rather than
            // layering, so only one component may write reward.
            var balanceReward = instance.GetComponent<Reward_Balance>();
            Sensor_GroundContact[] fallContacts = CollectFallContacts(rig);
            if (balanceReward != null)
            {
                Object.DestroyImmediate(balanceReward);
            }

            // Shoves and cubes are balance stressors — they fight the walking
            // signal while the speed curriculum is still coming up.
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

            var agentSo = new SerializedObject(agent);
            agentSo.FindProperty("_observeLocomotionCommand").boolValue = true;
            agentSo.ApplyModifiedPropertiesWithoutUndo();

            var behavior = instance.GetComponent<Unity.MLAgents.Policies.BehaviorParameters>();
            behavior.BrainParameters.VectorObservationSize = Agent_FighterBoxing.ComputeObservationCount(
                rig.JointCount, observeOpponent: false, observeFootHeight: true, observeLocomotionCommand: true);

            var locomotionReward = instance.AddComponent<Reward_Locomotion>();
            locomotionReward.EditorInitialize(agent, rig, fallContacts);
        }

        // The fall sensors SpawnFighter already added to torso, head, shins
        // and gloves — reused rather than re-added, which would double them up.
        private static Sensor_GroundContact[] CollectFallContacts(Systems_FighterRig rig)
        {
            return new[]
            {
                rig.Torso.GetComponent<Sensor_GroundContact>(),
                rig.Head.GetComponent<Sensor_GroundContact>(),
                rig.Joints[JOINT_INDEX_SHIN_L].body.GetComponent<Sensor_GroundContact>(),
                rig.Joints[JOINT_INDEX_SHIN_R].body.GetComponent<Sensor_GroundContact>(),
                rig.GloveLeft.GetComponent<Sensor_GroundContact>(),
                rig.GloveRight.GetComponent<Sensor_GroundContact>()
            };
        }
    }
}
