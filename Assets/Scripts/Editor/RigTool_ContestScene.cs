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
        private const string BOT_MATERIAL_PATH = "Assets/Art/M_BotRed.mat";
        private const string THEME_PATH = "Assets/UI/TSS_Contest.tss";
        // Shared stand-and-walk brain. The balance contest is the 0 m/s end of
        // the command this model line was trained against, so the same file
        // serves both mini-games. Keep in step with
        // RigTool_WalkContestScene.LOCOMOTION_BRAIN_PATH.
        // Gen 9, not gen 7: 25.8 s mean upright against gen 7's 17.2 s, a 46%
        // improvement, on the same 127-observation ground-relative contract.
        // Checked 2026-08-22 by running the balance contest and reading the
        // assigned asset back off the spawned fighters -- the scene had been
        // shipping gen 7 for five generations.
        //
        // This is deliberately NOT the newest brain. Gen 13 travels but hops on
        // one leg and topples every 1.7 s, which loses a contest scored on
        // staying upright. Newest and best are different questions per scene.
        private const string LOCOMOTION_BRAIN_PATH = "Assets/Agents/Locomotion_gen9/Locomotion_gen9.onnx";
        private const float LINE_SPACING = 2f;
        private const float SPAWN_HEIGHT = Systems_ContestSpawner.RING_FLOOR_Y + 0.03f;

        private const int JOINT_INDEX_SHIN_L = 3;
        private const int JOINT_INDEX_SHIN_R = 9;

        // forceHeuristic: code-driven PD bot (project rule: the heuristic bot
        // competes in the game) — never loads a brain, never warns about one.
        private static readonly (string prefabPath, string display, bool forceHeuristic)[] Roster =
        {
            ("Assets/Prefabs/Fighters/Fighter_Capsule.prefab", "Standard", false),
            ("Assets/Prefabs/Fighters/Fighter_Grandma.prefab", "Grandma", false),
            ("Assets/Prefabs/Fighters/Fighter_Grandpa.prefab", "Grandpa", false),
            ("Assets/Prefabs/Fighters/Fighter_Capsule.prefab", "Bot", true)
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
                    new Vector3(origin + rosterIndex * LINE_SPACING, SPAWN_HEIGHT, 0f),
                    Roster[rosterIndex].forceHeuristic);
                placed++;
            }

            BuildReferee();

            EditorSceneManager.SaveScene(scene, SCENE_PATH);
            Debug.Log($"RigTool: contest scene saved to {SCENE_PATH} — {placed} contestants. Press Play (no trainer needed).");
        }

        /// <summary>
        /// Adds the code-driven heuristic bot to the currently open contest
        /// scene without rebuilding it (a rebuild would discard presentation
        /// objects added after scene creation). The referee, drama camera and
        /// wobble meters discover fighters at runtime, so spawning is enough.
        /// </summary>
        [MenuItem("Tools/ML Boxing/7b. Add Heuristic Bot To Contest Scene")]
        public static void AddBotToOpenScene()
        {
            if (GameObject.Find("Contest_Bot") != null)
            {
                Debug.Log("RigTool: Contest_Bot already present — nothing to do.");
                return;
            }
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Roster[0].prefabPath);
            if (prefab == null)
            {
                Debug.LogWarning($"RigTool: missing prefab {Roster[0].prefabPath} — bot not added.");
                return;
            }
            // Second row, ring center line: extending the X line puts the bot
            // outside the 6.1 m canvas (edge ≈ ±3 m) and into the ropes.
            SpawnContestant(prefab, "Bot", new Vector3(0f, SPAWN_HEIGHT, LINE_SPACING), forceHeuristic: true);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();
            Debug.Log("RigTool: Contest_Bot added (heuristic PD, no brain) and scene saved.");
        }

        internal static GameObject SpawnContestant(GameObject prefab, string display, Vector3 position, bool forceHeuristic)
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
            if (forceHeuristic)
            {
                behavior.BehaviorType = BehaviorType.HeuristicOnly; // code-driven PD bot
                TintRenderers(instance, BOT_MATERIAL_PATH); // red = code bot, tell it apart at a glance
                return instance;
            }
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

            // Returned so the walk contest builder can re-aim and re-brand the
            // same contestant without duplicating this wiring.
            return instance;
        }

        /// <summary>
        /// Converts the open contest scene to the setup-menu flow: removes the
        /// baked fighters (the menu spawns fighters at runtime), parks the
        /// contest systems under an inactive root the spawner activates after
        /// spawning, adds the cube thrower, and creates the spawner + menu.
        /// One-off migration — safe to re-run (no-ops when already converted).
        /// </summary>
        [MenuItem("Tools/ML Boxing/7c. Convert Contest Scene To Setup Menu")]
        public static void ConvertToSetupMenu()
        {
            if (GameObject.Find("ContestSpawner") != null)
            {
                Debug.Log("RigTool: contest scene already uses the setup-menu flow — nothing to do.");
                return;
            }

            Systems_FighterRig[] rigs = UnityEngine.Object.FindObjectsByType<Systems_FighterRig>(FindObjectsSortMode.None);
            for (int rigIndex = 0; rigIndex < rigs.Length; rigIndex++)
            {
                UnityEngine.Object.DestroyImmediate(rigs[rigIndex].gameObject);
            }

            var systemsRoot = new GameObject("ContestSystems");
            string[] systemRootNames = { "ContestReferee", "WobbleMeterUI", "FallImpactFX", "CrowdAudio", "DebugOverlay" };
            for (int nameIndex = 0; nameIndex < systemRootNames.Length; nameIndex++)
            {
                GameObject systemObject = GameObject.Find(systemRootNames[nameIndex]);
                if (systemObject != null)
                {
                    systemObject.transform.SetParent(systemsRoot.transform, true);
                }
            }
            var throwerObject = new GameObject("CubeThrower");
            throwerObject.transform.SetParent(systemsRoot.transform, false);
            throwerObject.AddComponent<Systems_CubeThrower>();
            systemsRoot.SetActive(false);

            var dramaCamera = UnityEngine.Object.FindFirstObjectByType<Systems_DramaCamera>();
            if (dramaCamera != null)
            {
                dramaCamera.enabled = false; // the spawner enables it post-spawn
            }

            var rosterEntries = new ContestRosterEntry[Roster.Length];
            for (int rosterIndex = 0; rosterIndex < Roster.Length; rosterIndex++)
            {
                (string prefabPath, string display, bool forceHeuristic) = Roster[rosterIndex];
                rosterEntries[rosterIndex] = new ContestRosterEntry
                {
                    displayName = display,
                    prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath),
                    model = forceHeuristic
                        ? null
                        : AssetDatabase.LoadAssetAtPath<Unity.InferenceEngine.ModelAsset>($"Assets/Agents/{display}/Boxer.onnx"),
                    forceHeuristic = forceHeuristic,
                    tint = forceHeuristic ? AssetDatabase.LoadAssetAtPath<Material>(BOT_MATERIAL_PATH) : null
                };
            }
            var spawnerObject = new GameObject("ContestSpawner");
            var spawner = spawnerObject.AddComponent<Systems_ContestSpawner>();
            spawner.EditorInitialize(rosterEntries, systemsRoot, dramaCamera);

            var menuObject = new GameObject("ContestSetupMenu");
            var menuDocument = menuObject.AddComponent<UIDocument>();
            menuDocument.panelSettings = GetOrCreatePanelSettings();
            var menu = menuObject.AddComponent<Systems_ContestSetupMenu>();
            menu.EditorInitialize(spawner);

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();
            Debug.Log("RigTool: contest scene converted to setup-menu flow (8 slots, random defaults, cube thrower) and saved.");
        }

        /// <summary>
        /// Points every brain-driven roster entry at the shared locomotion brain
        /// and flags it as such.
        ///
        /// The balance scene is built INCREMENTALLY by 7 / 7b..7g, so it cannot
        /// be regenerated wholesale without losing everything the later passes
        /// added — hence a targeted, idempotent pass over the spawner's roster.
        ///
        /// Why it is needed: Assets/Agents/{Standard,Grandma,Grandpa}/Boxer.onnx
        /// are all 119-observation brains from before _observeFootHeight, while
        /// the current prefabs emit 121. Every ML fighter in the ring was reading
        /// a shifted vector — six console errors per spawn, measured 2026-08-20.
        /// Locomotion_gen7 is the only brain in the project trained against the
        /// rig as it stands today (127 observations, ground-relative height), and
        /// `locomotionBrain` is what tells the spawner to switch the fighter into
        /// that layout before its sensor is built.
        /// </summary>
        [MenuItem("Tools/ML Boxing/7h. Retarget Balance Roster To Locomotion Brain")]
        public static void RetargetRosterToLocomotionBrain()
        {
            var spawner = UnityEngine.Object.FindFirstObjectByType<Systems_ContestSpawner>();
            if (spawner == null)
            {
                Debug.LogWarning("RigTool: no ContestSpawner in the open scene — open SCN_TEST_BALANCE_CONTEST first.");
                return;
            }
            var brain = AssetDatabase.LoadAssetAtPath<Unity.InferenceEngine.ModelAsset>(LOCOMOTION_BRAIN_PATH);
            if (brain == null)
            {
                Debug.LogError($"RigTool: no brain at {LOCOMOTION_BRAIN_PATH} — roster left untouched.");
                return;
            }

            var serialized = new SerializedObject(spawner);
            SerializedProperty roster = serialized.FindProperty("_roster");
            int retargeted = 0;
            for (int entryIndex = 0; entryIndex < roster.arraySize; entryIndex++)
            {
                SerializedProperty entry = roster.GetArrayElementAtIndex(entryIndex);
                // The heuristic bot never loads a brain (project rule) — leave it.
                if (entry.FindPropertyRelative("forceHeuristic").boolValue)
                {
                    continue;
                }
                entry.FindPropertyRelative("model").objectReferenceValue = brain;
                entry.FindPropertyRelative("locomotionBrain").boolValue = true;
                retargeted++;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();
            Debug.Log($"RigTool: {retargeted} roster entries retargeted to {LOCOMOTION_BRAIN_PATH} and scene saved.");
        }

        /// <summary>
        /// Adds the soft physics ropes (Systems_RingRopes builds the chains at
        /// runtime). Always-active: ropes exist before fighters spawn.
        /// </summary>
        [MenuItem("Tools/ML Boxing/7d. Add Physics Ropes To Contest Scene")]
        public static void AddPhysicsRopes()
        {
            if (GameObject.Find("RingRopes") != null)
            {
                Debug.Log("RigTool: RingRopes already present — nothing to do.");
                return;
            }
            var ropesObject = new GameObject("RingRopes");
            ropesObject.AddComponent<Systems_RingRopes>();
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();
            Debug.Log("RigTool: RingRopes added and scene saved.");
        }

        /// <summary>
        /// Adds the announcer and hazard director under the sleeping contest
        /// systems root (they self-discover fighters in Start, which runs when
        /// the spawner activates the root after spawning).
        /// </summary>
        [MenuItem("Tools/ML Boxing/7e. Add Announcer And Hazards To Contest Scene")]
        public static void AddAnnouncerAndHazards()
        {
            var spawner = UnityEngine.Object.FindFirstObjectByType<Systems_ContestSpawner>();
            if (spawner == null)
            {
                Debug.LogWarning("RigTool: no ContestSpawner — run 7c first.");
                return;
            }
            var spawnerSo = new SerializedObject(spawner);
            var systemsRoot = spawnerSo.FindProperty("_systemsRoot").objectReferenceValue as GameObject;
            if (systemsRoot == null)
            {
                Debug.LogWarning("RigTool: spawner has no systems root reference.");
                return;
            }
            if (systemsRoot.transform.Find("ContestAnnouncer") != null)
            {
                Debug.Log("RigTool: announcer already present — nothing to do.");
                return;
            }

            var announcerObject = new GameObject("ContestAnnouncer");
            announcerObject.transform.SetParent(systemsRoot.transform, false);
            var announcerDocument = announcerObject.AddComponent<UIDocument>();
            announcerDocument.panelSettings = GetOrCreatePanelSettings();
            announcerObject.AddComponent<AudioSource>().playOnAwake = false;
            var announcer = announcerObject.AddComponent<Systems_Announcer>();
            var bell = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX_BoxingBell.wav");
            if (bell != null)
            {
                var announcerSo = new SerializedObject(announcer);
                announcerSo.FindProperty("_bellClip").objectReferenceValue = bell;
                announcerSo.ApplyModifiedPropertiesWithoutUndo();
            }
            else
            {
                Debug.LogWarning("RigTool: Assets/Audio/SFX_BoxingBell.wav not found — announcer will be silent.");
            }

            var hazardObject = new GameObject("HazardDirector");
            hazardObject.transform.SetParent(systemsRoot.transform, false);
            hazardObject.AddComponent<Systems_HazardDirector>();

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();
            Debug.Log("RigTool: announcer + hazard director added and scene saved.");
        }

        /// <summary>
        /// Presentation upgrade pass: audio listener (the scene had NONE — all
        /// audio was silent), stadium spotlights + dimmed key light, knockout
        /// vignette/slow-mo FX, menu orbit camera, Cinemachine winner orbit,
        /// and Kenney impact clips on the cube thrower. Idempotent.
        /// </summary>
        [MenuItem("Tools/ML Boxing/7f. Upgrade Contest Presentation")]
        public static void UpgradeContestPresentation()
        {
            var cameraObject = GameObject.Find("Main Camera");
            var spawner = UnityEngine.Object.FindFirstObjectByType<Systems_ContestSpawner>();
            if (cameraObject == null || spawner == null)
            {
                Debug.LogWarning("RigTool: need Main Camera and ContestSpawner — run 7c first.");
                return;
            }
            if (cameraObject.GetComponent<Systems_MenuOrbitCamera>() != null)
            {
                Debug.Log("RigTool: presentation upgrade already applied — nothing to do.");
                return;
            }
            var spawnerSo = new SerializedObject(spawner);
            var systemsRoot = spawnerSo.FindProperty("_systemsRoot").objectReferenceValue as GameObject;

            // Audio listener (project had none — everything was silent).
            if (cameraObject.GetComponent<AudioListener>() == null)
            {
                cameraObject.AddComponent<AudioListener>();
            }

            // Stadium lighting: dim warm key light + three cool spots.
            var keyLight = UnityEngine.Object.FindFirstObjectByType<Light>();
            Light[] lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
            for (int lightIndex = 0; lightIndex < lights.Length; lightIndex++)
            {
                if (lights[lightIndex].type == LightType.Directional)
                {
                    lights[lightIndex].intensity = 0.45f;
                    lights[lightIndex].color = new Color(1f, 0.93f, 0.82f);
                }
            }
            var stadium = new GameObject("StadiumLights");
            Vector3[] spotPositions =
            {
                new(-4f, 7f, -4f), new(4f, 7f, -4f), new(0f, 7.5f, 4.5f)
            };
            for (int spotIndex = 0; spotIndex < spotPositions.Length; spotIndex++)
            {
                var spotObject = new GameObject("Spot" + spotIndex);
                spotObject.transform.SetParent(stadium.transform, false);
                spotObject.transform.position = spotPositions[spotIndex];
                spotObject.transform.rotation = Quaternion.LookRotation(Vector3.zero - spotPositions[spotIndex]);
                var spot = spotObject.AddComponent<Light>();
                spot.type = LightType.Spot;
                spot.range = 16f;
                spot.spotAngle = 55f;
                spot.intensity = 40f;
                spot.color = new Color(0.95f, 0.97f, 1f);
                spot.shadows = LightShadows.Soft;
            }

            // Knockout FX (vignette pulse + round-end slow-mo).
            var volume = UnityEngine.Object.FindFirstObjectByType<UnityEngine.Rendering.Volume>();
            if (systemsRoot != null)
            {
                var knockoutObject = new GameObject("KnockoutFx");
                knockoutObject.transform.SetParent(systemsRoot.transform, false);
                knockoutObject.AddComponent<Systems_KnockoutFx>().EditorInitialize(volume);
            }

            // Menu orbit camera (spawner disables it when the fight starts).
            var menuOrbit = cameraObject.AddComponent<Systems_MenuOrbitCamera>();
            spawner.EditorSetMenuOrbit(menuOrbit);
            EditorUtility.SetDirty(spawner);

            // Cinemachine winner orbit: brain + one virtual camera.
            if (cameraObject.GetComponent<Unity.Cinemachine.CinemachineBrain>() == null)
            {
                cameraObject.AddComponent<Unity.Cinemachine.CinemachineBrain>();
            }
            var winnerCamObject = new GameObject("CM_WinnerCamera");
            var virtualCamera = winnerCamObject.AddComponent<Unity.Cinemachine.CinemachineCamera>();
            winnerCamObject.SetActive(false);
            if (systemsRoot != null)
            {
                var winnerObject = new GameObject("WinnerCamera");
                winnerObject.transform.SetParent(systemsRoot.transform, false);
                winnerObject.AddComponent<Systems_WinnerCamera>().EditorInitialize(
                    virtualCamera, cameraObject.GetComponent<Systems_DramaCamera>());
            }

            // Cube impact clips (Kenney CC0).
            var thrower = UnityEngine.Object.FindFirstObjectByType<Systems_CubeThrower>(FindObjectsInactive.Include);
            if (thrower != null)
            {
                string[] clipPaths =
                {
                    "Assets/Audio/Kenney/impactGeneric_light_000.ogg",
                    "Assets/Audio/Kenney/impactGeneric_light_001.ogg",
                    "Assets/Audio/Kenney/impactGeneric_light_002.ogg",
                    "Assets/Audio/Kenney/impactGeneric_light_003.ogg",
                    "Assets/Audio/Kenney/impactGeneric_light_004.ogg"
                };
                var throwerSo = new SerializedObject(thrower);
                var clipsProperty = throwerSo.FindProperty("_impactClips");
                clipsProperty.arraySize = clipPaths.Length;
                for (int clipIndex = 0; clipIndex < clipPaths.Length; clipIndex++)
                {
                    clipsProperty.GetArrayElementAtIndex(clipIndex).objectReferenceValue =
                        AssetDatabase.LoadAssetAtPath<AudioClip>(clipPaths[clipIndex]);
                }
                throwerSo.ApplyModifiedPropertiesWithoutUndo();
            }

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();
            Debug.Log("RigTool: presentation upgraded (listener, lights, knockout FX, orbit, winner cam, cube audio) and saved.");
        }

        /// <summary>
        /// Adds the match director (first to 3 round wins -> champion ->
        /// scene reload back to the menu) under the contest systems root.
        /// </summary>
        [MenuItem("Tools/ML Boxing/7g. Add Match Director To Contest Scene")]
        public static void AddMatchDirector()
        {
            var spawner = UnityEngine.Object.FindFirstObjectByType<Systems_ContestSpawner>();
            if (spawner == null)
            {
                Debug.LogWarning("RigTool: no ContestSpawner — run 7c first.");
                return;
            }
            var spawnerSo = new SerializedObject(spawner);
            var systemsRoot = spawnerSo.FindProperty("_systemsRoot").objectReferenceValue as GameObject;
            if (systemsRoot == null || systemsRoot.transform.Find("MatchDirector") != null)
            {
                Debug.Log("RigTool: match director already present or no systems root — nothing to do.");
                return;
            }
            var matchObject = new GameObject("MatchDirector");
            matchObject.transform.SetParent(systemsRoot.transform, false);
            var document = matchObject.AddComponent<UIDocument>();
            document.panelSettings = GetOrCreatePanelSettings();
            matchObject.AddComponent<Systems_MatchDirector>();
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();
            Debug.Log("RigTool: match director added and scene saved.");
        }

        private static void TintRenderers(GameObject root, string materialPath)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                Debug.LogWarning($"RigTool: no material at {materialPath} — bot keeps default look.");
                return;
            }
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
            {
                renderers[rendererIndex].sharedMaterial = material;
            }
        }

        private static void BuildGround()
        {
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "Ground";
            // Sized to the 6.1 m canvas, not the old 40 m slab: at ring height a
            // wide slab becomes an invisible floor hanging in mid-air around the
            // ring, and it would hide the ring's own platform sides.
            ground.transform.localScale = new Vector3(6.1f, 1f, 6.1f);
            ground.transform.position = new Vector3(0f, Systems_ContestSpawner.RING_FLOOR_Y - 0.5f, 0f);
            ground.GetComponent<MeshRenderer>().enabled = false; // the ring model is the visual floor
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
            cameraObject.transform.position = new Vector3(0f, Systems_ContestSpawner.RING_FLOOR_Y + 1.5f, -4.5f);
            cameraObject.transform.rotation = Quaternion.Euler(5f, 0f, 0f);
        }

        private static void BuildReferee()
        {
            var refereeObject = new GameObject("ContestReferee");
            var document = refereeObject.AddComponent<UIDocument>();
            document.panelSettings = GetOrCreatePanelSettings();
            refereeObject.AddComponent<Systems_BalanceContest>();
        }

        internal static PanelSettings GetOrCreatePanelSettings()
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
