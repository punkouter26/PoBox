using System.Collections.Generic;
using PoBox;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PoBox.Editor
{
    /// <summary>
    /// Builds SCN_TRAIN_BALANCE: 16 fighter instances on a shared
    /// high-friction ground box, each with balance reward + shover + fall
    /// sensors (torso/head/shins/gloves). Zero cameras, HUD, or audio —
    /// training scenes run headless (project rule). Instances are fully
    /// unpacked so training components never become prefab overrides.
    /// Registers the scene as build index 0 for the headless env build.
    /// </summary>
    internal static class RigTool_BalanceScene
    {
        private const string SCENE_PATH = "Assets/Scenes/SCN_TRAIN_BALANCE.unity";
        private const string FIGHTER_PREFAB_PATH = "Assets/Prefabs/Fighters/Fighter_Capsule.prefab";
        // Each morphology trains in its own scene with its own run id.
        private const string GRANDMA_SCENE_PATH = "Assets/Scenes/SCN_TRAIN_BALANCE_GRANDMA.unity";
        private const string GRANDMA_PREFAB_PATH = "Assets/Prefabs/Fighters/Fighter_Grandma.prefab";
        private const string GRANDPA_SCENE_PATH = "Assets/Scenes/SCN_TRAIN_BALANCE_GRANDPA.unity";
        private const string GRANDPA_PREFAB_PATH = "Assets/Prefabs/Fighters/Fighter_Grandpa.prefab";
        private const string VARIATION_ASSET_PATH = "Assets/Config/SO_FighterVariation.asset";
        private const int GRID_SIZE = 4;
        private const float GRID_SPACING = 8f;
        private const int BALANCE_MAX_STEP = 3000;   // 60 s at 50 Hz — matches acceptance criterion 5
        private const float SPAWN_HEIGHT = 0.03f;    // small clearance so feet don't spawn in contact

        // Joint list order (spec table, pelvis skipped): 0=Torso 1=Head
        // 2=ThighL 3=ShinL 4=FootL 5=UpperArmL 6=ForearmL 7=GloveL
        // 8=ThighR 9=ShinR 10=FootR 11=UpperArmR 12=ForearmR 13=GloveR.
        // Index 6 was wrongly used for ShinR before 2026-08-17 — that put a
        // fall sensor on the LEFT FOREARM and left the right shin untracked.
        private const int JOINT_INDEX_SHIN_L = 3;
        private const int JOINT_INDEX_SHIN_R = 9;

        [MenuItem("Tools/ML Boxing/5. Create Balance Training Scene")]
        public static void Create()
        {
            CreateFor(FIGHTER_PREFAB_PATH, SCENE_PATH, firstBuildScene: true,
                "Train with: .\\train.ps1 (then press Play).");
        }

        [MenuItem("Tools/ML Boxing/5b. Create Grandma Balance Scene")]
        public static void CreateGrandma()
        {
            CreateFor(GRANDMA_PREFAB_PATH, GRANDMA_SCENE_PATH, firstBuildScene: false,
                "Train with: .\\train.ps1 -Config GrandmaBalance01.yaml -RunId grandma_balance01 (then press Play).");
        }

        [MenuItem("Tools/ML Boxing/5c. Create Grandpa Balance Scene")]
        public static void CreateGrandpa()
        {
            CreateFor(GRANDPA_PREFAB_PATH, GRANDPA_SCENE_PATH, firstBuildScene: false,
                "Train with: .\\train.ps1 -Config GrandpaBalance01.yaml -RunId grandpa_balance01 (then press Play).");
        }

        private static void CreateFor(string prefabPath, string scenePath, bool firstBuildScene, string trainHint)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                EditorUtility.DisplayDialog("Balance Scene",
                    "Fighter prefab not found at " + prefabPath +
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
            Systems_FighterVariation variation = GetOrCreateVariationAsset();

            BuildGround();

            float origin = -GRID_SPACING * (GRID_SIZE - 1) * 0.5f;
            for (int gridX = 0; gridX < GRID_SIZE; gridX++)
            {
                for (int gridZ = 0; gridZ < GRID_SIZE; gridZ++)
                {
                    var position = new Vector3(origin + gridX * GRID_SPACING, SPAWN_HEIGHT, origin + gridZ * GRID_SPACING);
                    SpawnFighter(prefab, position, gridX * GRID_SIZE + gridZ, variation);
                }
            }

            EditorSceneManager.SaveScene(scene, scenePath);
            if (firstBuildScene)
            {
                RegisterAsFirstBuildScene();
            }
            Debug.Log($"RigTool: balance training scene saved to {scenePath} — {GRID_SIZE * GRID_SIZE} fighters. {trainHint}");
        }

        private static void BuildGround()
        {
            // Box, not a scaled plane: a thin MeshCollider invites tunneling
            // when a body gets flung at high angular velocity.
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(100f, 1f, 100f);
            ground.transform.position = new Vector3(0f, -0.5f, 0f); // top face at y = 0
            var highFriction = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(RigTool_Config.HIGH_FRICTION_MATERIAL_PATH);
            if (highFriction != null)
            {
                ground.GetComponent<Collider>().sharedMaterial = highFriction;
            }
        }

        private static Systems_FighterVariation GetOrCreateVariationAsset()
        {
            var variation = AssetDatabase.LoadAssetAtPath<Systems_FighterVariation>(VARIATION_ASSET_PATH);
            if (variation != null)
            {
                return variation;
            }
            if (!AssetDatabase.IsValidFolder("Assets/Config"))
            {
                AssetDatabase.CreateFolder("Assets", "Config");
            }
            variation = ScriptableObject.CreateInstance<Systems_FighterVariation>();
            AssetDatabase.CreateAsset(variation, VARIATION_ASSET_PATH);
            AssetDatabase.SaveAssets();
            Debug.Log($"RigTool: created fighter variation asset at {VARIATION_ASSET_PATH} — edit ranges there.");
            return variation;
        }

        /// <summary>
        /// One mass factor and one strength factor per fighter, seeded by grid
        /// index — rebuilding a scene reproduces the exact same roster.
        /// </summary>
        private static void ApplyVariation(GameObject instance, Systems_FighterRig rig, Systems_FighterVariation variation, int index)
        {
            if (variation == null || !variation.Enabled)
            {
                return;
            }
            var random = new System.Random(variation.Seed + index);
            float massScale = RandomScale(random, variation.MassRange);
            float strengthScale = RandomScale(random, variation.StrengthRange);

            Rigidbody[] bodies = instance.GetComponentsInChildren<Rigidbody>(true);
            for (int bodyIndex = 0; bodyIndex < bodies.Length; bodyIndex++)
            {
                bodies[bodyIndex].mass *= massScale;
            }
            for (int jointIndex = 0; jointIndex < rig.Joints.Count; jointIndex++)
            {
                RigJointEntry entry = rig.Joints[jointIndex];
                entry.baseSpring *= strengthScale;
                entry.baseDamper *= strengthScale;
                entry.baseMaxForce *= strengthScale;
                JointDrive drive = entry.joint.slerpDrive;
                drive.positionSpring = entry.baseSpring;
                drive.positionDamper = entry.baseDamper;
                drive.maximumForce = entry.baseMaxForce;
                entry.joint.slerpDrive = drive;
            }
        }

        private static float RandomScale(System.Random random, float range)
        {
            return 1f + ((float)random.NextDouble() * 2f - 1f) * range;
        }

        private static void SpawnFighter(GameObject prefab, Vector3 position, int index, Systems_FighterVariation variation)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            instance.name = $"Fighter_{index:D2}";
            instance.transform.position = position;

            var rig = instance.GetComponent<Systems_FighterRig>();
            var agent = instance.GetComponent<Agent_FighterBoxing>();
            var stamina = instance.GetComponent<Systems_Stamina>();
            agent.MaxStep = BALANCE_MAX_STEP;

            // Stage 1: constant motor strength. Stamina attenuation would make
            // the actuators time-varying while stamina is not in the (locked)
            // observation vector — non-Markov. Re-enabled in later stages.
            stamina.enabled = false;

            var fallContacts = new List<Sensor_GroundContact>
            {
                rig.Torso.gameObject.AddComponent<Sensor_GroundContact>(),
                rig.Head.gameObject.AddComponent<Sensor_GroundContact>(),
                rig.Joints[JOINT_INDEX_SHIN_L].body.gameObject.AddComponent<Sensor_GroundContact>(),
                rig.Joints[JOINT_INDEX_SHIN_R].body.gameObject.AddComponent<Sensor_GroundContact>(),
                rig.GloveLeft.gameObject.AddComponent<Sensor_GroundContact>(),
                rig.GloveRight.gameObject.AddComponent<Sensor_GroundContact>()
            };

            var reward = instance.AddComponent<Reward_Balance>();
            reward.EditorInitialize(agent, rig, stamina, fallContacts.ToArray());

            var shover = instance.AddComponent<Systems_Shover>();
            shover.EditorInitialize(rig.Torso, agent, index);

            ApplyVariation(instance, rig, variation, index);
        }

        private static void RegisterAsFirstBuildScene()
        {
            var scenes = new List<EditorBuildSettingsScene> { new(SCENE_PATH, true) };
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
