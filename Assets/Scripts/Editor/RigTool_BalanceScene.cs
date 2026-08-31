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
        private const string RAPTOR_SCENE_PATH = "Assets/Scenes/SCN_TRAIN_BALANCE_RAPTOR.unity";
        private const string RAPTOR_PREFAB_PATH = "Assets/Prefabs/Fighters/Fighter_Raptor.prefab";
        private const string VARIATION_ASSET_PATH = "Assets/Config/SO_FighterVariation.asset";
        private const int GRID_SIZE = 4;
        private const float GRID_SPACING = 8f;
        private const int BALANCE_MAX_STEP = 3000;   // 60 s at 50 Hz — matches acceptance criterion 5
        private const float SPAWN_HEIGHT = 0.03f;    // small clearance so feet don't spawn in contact

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

        [MenuItem("Tools/ML Boxing/5d. Create Raptor Balance Scene")]
        public static void CreateRaptor()
        {
            // applyGen2Rig false: the raptor prefab already bakes the 114-obs
            // foot-height layout, and its training dynamics must equal its
            // contest dynamics — the contest applies neither muscle-lag
            // smoothing nor the (humanoid-proportioned) realism profile, so
            // the training scene must not either.
            // headCollapseFraction 0.25: the raptor's head rides a horizontal
            // neck, so an exploratory crouch passes the humanoid 0.4 threshold
            // while still standing — measured killing every gen-1 episode in
            // 26.7 steps. 0.25 of 0.91 m = 0.23 m, genuinely collapsed.
            CreateFor(RAPTOR_PREFAB_PATH, RAPTOR_SCENE_PATH, firstBuildScene: false,
                "Train with: .\\train.ps1 -Config RaptorBalance01.yaml -RunId raptor_balance01 (then press Play).",
                applyGen2Rig: false, headCollapseFraction: 0.25f);
        }

        private static void CreateFor(string prefabPath, string scenePath, bool firstBuildScene, string trainHint,
            bool applyGen2Rig = true, float headCollapseFraction = 0.4f)
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
                    SpawnFighter(prefab, position, gridX * GRID_SIZE + gridZ, variation, applyGen2Rig,
                        headCollapseFraction);
                }
            }

            EditorSceneManager.SaveScene(scene, scenePath);
            if (firstBuildScene)
            {
                RegisterAsFirstBuildScene();
            }
            Debug.Log($"RigTool: balance training scene saved to {scenePath} — {GRID_SIZE * GRID_SIZE} fighters. {trainHint}");
        }

        internal static void BuildGround()
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

        internal static Systems_FighterVariation GetOrCreateVariationAsset()
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

        // Generation-2 realism (2026-08-18): foot-height observations (121
        // obs), muscle-lag action smoothing, human strength proportions, and
        // cloth-friction body colliders. Per-instance only — the shared
        // prefabs stay generation-1 so the contest scene keeps working with
        // deployed brains until the new generation's brains land.
        private static void ApplyGen2Settings(GameObject instance, Systems_FighterRig rig, Agent_FighterBoxing agent)
        {
            var agentSo = new SerializedObject(agent);
            agentSo.FindProperty("_observeFootHeight").boolValue = true;
            agentSo.ApplyModifiedPropertiesWithoutUndo();

            var behavior = instance.GetComponent<Unity.MLAgents.Policies.BehaviorParameters>();
            behavior.BrainParameters.VectorObservationSize =
                Agent_FighterBoxing.ComputeObservationCount(rig.JointCount, observeOpponent: false, observeFootHeight: true);

            var rigSo = new SerializedObject(rig);
            rigSo.FindProperty("_actionSmoothingSeconds").floatValue = 0.1f;
            rigSo.FindProperty("_realismProfile").boolValue = true;
            rigSo.ApplyModifiedPropertiesWithoutUndo();

            PhysicsMaterial bodyCloth = GetOrCreateBodyClothMaterial();
            var colliders = instance.GetComponentsInChildren<Collider>(true);
            for (int colliderIndex = 0; colliderIndex < colliders.Length; colliderIndex++)
            {
                if (colliders[colliderIndex].sharedMaterial == null)
                {
                    colliders[colliderIndex].sharedMaterial = bodyCloth; // feet keep PM_FootSole
                }
            }
        }

        private static PhysicsMaterial GetOrCreateBodyClothMaterial()
        {
            const string path = "Assets/Config/PM_BodyCloth.physicMaterial";
            var material = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(path);
            if (material != null)
            {
                return material;
            }
            material = new PhysicsMaterial("PM_BodyCloth")
            {
                staticFriction = 0.3f,
                dynamicFriction = 0.25f,
                frictionCombine = PhysicsMaterialCombine.Average
            };
            AssetDatabase.CreateAsset(material, path);
            AssetDatabase.SaveAssets();
            return material;
        }

        private static float RandomScale(System.Random random, float range)
        {
            return 1f + ((float)random.NextDouble() * 2f - 1f) * range;
        }

        internal static GameObject SpawnFighter(GameObject prefab, Vector3 position, int index,
            Systems_FighterVariation variation, bool applyGen2Rig = true, float headCollapseFraction = 0.4f)
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

            // Fall sensors by role, not joint index: the raptor's joint list
            // is a different shape than the humanoids', and gloves exist only
            // on rigs with arms. Same name convention Reward_Balance uses.
            var fallContacts = new List<Sensor_GroundContact>
            {
                rig.Torso.gameObject.AddComponent<Sensor_GroundContact>(),
                rig.Head.gameObject.AddComponent<Sensor_GroundContact>()
            };
            for (int jointIndex = 0; jointIndex < rig.Joints.Count; jointIndex++)
            {
                Rigidbody jointBody = rig.Joints[jointIndex].body;
                if (jointBody != null &&
                    jointBody.name.IndexOf("shin", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    fallContacts.Add(jointBody.gameObject.AddComponent<Sensor_GroundContact>());
                }
            }
            if (rig.GloveLeft != null)
            {
                fallContacts.Add(rig.GloveLeft.gameObject.AddComponent<Sensor_GroundContact>());
            }
            if (rig.GloveRight != null)
            {
                fallContacts.Add(rig.GloveRight.gameObject.AddComponent<Sensor_GroundContact>());
            }

            var reward = instance.AddComponent<Reward_Balance>();
            reward.EditorInitialize(agent, rig, stamina, fallContacts.ToArray());

            // Generation 3 (2026-08-19): product-form balance reward — the
            // agent must satisfy every criterion at once and per-step reward
            // stays positive, so "fell later" always beats "fell sooner".
            var rewardSo = new SerializedObject(reward);
            rewardSo.FindProperty("_productReward").boolValue = true;
            rewardSo.FindProperty("_headCollapseFraction").floatValue = headCollapseFraction;
            rewardSo.ApplyModifiedPropertiesWithoutUndo();

            var shover = instance.AddComponent<Systems_Shover>();
            shover.EditorInitialize(rig.Torso, agent, index);

            var strengthCurriculum = instance.AddComponent<Systems_StrengthCurriculum>();
            strengthCurriculum.EditorInitialize(rig, agent);

            var trainingCubes = instance.AddComponent<Systems_TrainingCubes>();
            trainingCubes.EditorInitialize(rig, agent, index);

            if (applyGen2Rig)
            {
                ApplyGen2Settings(instance, rig, agent);
            }

            ApplyVariation(instance, rig, variation, index);

            // Returned so the walk scene builder can layer Reward_Walk on top
            // and switch off the balance-only curricula.
            return instance;
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
