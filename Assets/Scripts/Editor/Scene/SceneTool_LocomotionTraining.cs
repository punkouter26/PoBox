using PoBox;
using System.Collections.Generic;
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
    internal static class SceneTool_LocomotionTraining
    {
        private const string SCENE_PATH = "Assets/Scenes/SCN_TRAIN_LOCOMOTION.unity";
        /// <summary>
        /// Every body the shared locomotion brain has to drive, cycled across
        /// the training grid.
        ///
        /// This scene trained 16 copies of Fighter_Capsule and nothing else,
        /// and that is why two of the game's four fighters do not work. The
        /// three rigs weigh the same 75 kg but distribute it completely
        /// differently — the capsule's torso segment is 1.1 kg where Grandpa's
        /// is 24.3 kg — so the same normalised torque on the same joint produces
        /// a different response on each body. A policy fitted to one of them is
        /// not a walking policy, it is a walking-THAT-body policy. Measured
        /// 2026-08-22 in the live balance contest, Grandma and Grandpa collapsed
        /// 1.1 to 1.4 s into every single round while the capsule they were
        /// trained on lasted 12 s.
        ///
        /// Cycling the grid over all three is domain randomisation over body
        /// shape: the brain sees every rig within one batch and cannot overfit
        /// to a single mass distribution. It costs nothing at training time —
        /// the grid is the same 16 fighters — and it is the only way one brain
        /// can honestly serve a roster of three.
        ///
        /// Order matters only in that it is stable; 16 does not divide by 3, so
        /// the capsule gets the spare slots.
        /// </summary>
        private static readonly string[] FighterPrefabPaths =
        {
            "Assets/Prefabs/Fighters/Fighter_Capsule.prefab",
            "Assets/Prefabs/Fighters/Fighter_Grandma.prefab",
            "Assets/Prefabs/Fighters/Fighter_Grandpa.prefab"
        };

        private const int GRID_SIZE = 4;               // 16 fighters, same throughput as balance
        private const float GRID_SPACING = 8f;
        private const int LOCOMOTION_MAX_STEP = 3000;  // 60 s at 50 Hz
        private const float SPAWN_HEIGHT = 0.03f;

        // Torso, head, both lower legs, both gloves. Fewer than this means a
        // limb could not be resolved on some rig and the fall detector has
        // quietly lost a body part.
        private const int MIN_FALL_CONTACTS = 6;
        // CLI-ONLY. This regenerates a training scene WHOLESALE, discarding any
        // hand tuning, so it is deliberately not on a menu where it can be hit
        // by accident. Drive it from the command bridge or batch mode, and run
        // Tools/verify_train_scene.py afterwards.
        public static void Create()
        {
            var prefabs = new GameObject[FighterPrefabPaths.Length];
            for (int prefabIndex = 0; prefabIndex < FighterPrefabPaths.Length; prefabIndex++)
            {
                prefabs[prefabIndex] = AssetDatabase.LoadAssetAtPath<GameObject>(FighterPrefabPaths[prefabIndex]);
                if (prefabs[prefabIndex] == null)
                {
                    EditorUtility.DisplayDialog("Locomotion Scene",
                        "Fighter prefab not found at " + FighterPrefabPaths[prefabIndex] +
                        ".\nBuild/prepare that fighter prefab first (Tools > ML Boxing).", "OK");
                    return;
                }
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Load AFTER NewScene: the scene switch unloads unreferenced
            // assets, silently nulling a reference loaded before it.
            Systems_FighterVariation variation = SceneTool_BalanceTraining.GetOrCreateVariationAsset();

            SceneTool_BalanceTraining.BuildGround();

            float origin = -GRID_SPACING * (GRID_SIZE - 1) * 0.5f;
            for (int gridX = 0; gridX < GRID_SIZE; gridX++)
            {
                for (int gridZ = 0; gridZ < GRID_SIZE; gridZ++)
                {
                    var position = new Vector3(origin + gridX * GRID_SPACING, SPAWN_HEIGHT, origin + gridZ * GRID_SPACING);
                    // Cycles the three bodies across the grid — see FighterPrefabPaths.
                    int slot = gridX * GRID_SIZE + gridZ;
                    GameObject prefab = prefabs[slot % prefabs.Length];
                    ConvertToLocomotion(
                        SceneTool_BalanceTraining.SpawnFighter(prefab, position, slot, variation),
                        BodyNameOf(prefab));
                }
            }

            EditorSceneManager.SaveScene(scene, SCENE_PATH);
            Debug.Log($"RigTool: locomotion training scene saved to {SCENE_PATH} — {GRID_SIZE * GRID_SIZE} fighters " +
                $"across {prefabs.Length} bodies. All of them train ONE shared brain, so it has to walk on " +
                "each — a brain fitted to a single rig is what left Grandma and Grandpa unable to stand.");
        }

        // SpawnFighter builds a balance-phase fighter; swap the reward and the
        // observation layout over to the locomotion model line.
        /// <summary>
        /// "Fighter_Grandma" -> "Grandma". The spawned instances are all named
        /// Fighter_00..Fighter_15, so the body they were built from is not
        /// recoverable from the scene afterwards — this is the only point that
        /// still knows it, and Reward_Locomotion needs it to split its stats by
        /// rig. The prefab list is the source of truth, not the grid index:
        /// reordering FighterPrefabPaths must relabel the stats, never silently
        /// mislabel them.
        /// </summary>
        private static string BodyNameOf(GameObject prefab)
        {
            string prefabName = prefab.name;
            const string prefix = "Fighter_";
            return prefabName.StartsWith(prefix, System.StringComparison.Ordinal)
                ? prefabName.Substring(prefix.Length)
                : prefabName;
        }

        private static void ConvertToLocomotion(GameObject instance, string bodyName)
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
            locomotionReward.EditorInitialize(agent, rig, fallContacts, bodyName);
        }

        /// <summary>
        /// The fall sensors SpawnFighter already added — reused rather than
        /// re-added, which would double them up.
        ///
        /// A FALL IS ANY GROUND SENSOR EXCEPT A FOOT touching the floor. Stating
        /// it that way is the whole fix: this used to name the six parts by hand
        /// and reach two of them through hardcoded joint indices 3 and 9, which
        /// are positions in the CAPSULE's joint list. On Fighter_Grandma and
        /// Fighter_Grandpa those indices land on a body that carries no sensor,
        /// so the array came back with two nulls in it, and Reward_Locomotion
        /// dereferences every entry on every FixedUpdate.
        ///
        /// Ten of the sixteen fighters therefore threw
        /// NullReferenceException 50 times a second and earned zero reward for
        /// the entire run, while the six capsules trained normally and the
        /// run's aggregate statistics looked entirely plausible. The scene
        /// committed to git was built before the character rigs took their
        /// current bone names and does NOT have this defect, so the damage only
        /// appears the moment anyone regenerates the scene — which is the
        /// documented way to maintain it.
        ///
        /// Derived from the rig, it needs no index, no name convention and no
        /// per-rig special case, so it holds for the raptor too.
        /// </summary>
        private static Sensor_GroundContact[] CollectFallContacts(Systems_FighterRig rig)
        {
            var fallContacts = new List<Sensor_GroundContact>();
            Sensor_GroundContact[] all = rig.GetComponentsInChildren<Sensor_GroundContact>(true);
            for (int sensorIndex = 0; sensorIndex < all.Length; sensorIndex++)
            {
                Sensor_GroundContact sensor = all[sensorIndex];
                if (sensor == null || sensor == rig.FootLeftSensor || sensor == rig.FootRightSensor)
                {
                    continue;
                }
                fallContacts.Add(sensor);
            }
            // Loud, and at BUILD time. A fall detector that silently shrinks to
            // "torso and head only" is a fighter that can lie on its knees
            // forever and still be scored as standing.
            if (fallContacts.Count < MIN_FALL_CONTACTS)
            {
                Debug.LogError($"RigTool: {rig.name} has only {fallContacts.Count} non-foot ground sensors " +
                    $"(expected at least {MIN_FALL_CONTACTS}: torso, head, both lower legs, both gloves). " +
                    "Falls will go undetected for this fighter and it will be scored as upright while " +
                    "lying on the floor. Check that SpawnFighter could resolve this rig's limbs.");
            }
            return fallContacts.ToArray();
        }
    }
}
