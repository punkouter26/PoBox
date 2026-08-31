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
    /// up on one high-friction floor, each driven by the one shared locomotion
    /// brain (zero-action physics if it is missing, and the heuristic PD bot
    /// by choice). A referee times standing, crowns the longest, and
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
        private const string LOCOMOTION_BRAIN_PATH = "Assets/Agents/Locomotion_gen20/Locomotion_gen20.onnx";
        private const float LINE_SPACING = 2f;
        private const float SPAWN_HEIGHT = Systems_ContestSpawner.RING_FLOOR_Y + 0.03f;

        private const int JOINT_INDEX_SHIN_L = 3;
        private const int JOINT_INDEX_SHIN_R = 9;

        // The raptor is its own model line: a 13-joint rig emitting 114
        // observations, so the shared locomotion brain (127) can never load on
        // it and the retarget pass below leaves it alone. Its brain comes from
        // SCN_TRAIN_BALANCE_RAPTOR runs; until one is exported the spawner's
        // width check falls back to the heuristic bot on its own.
        private const string RAPTOR_PREFAB_PATH = "Assets/Prefabs/Fighters/Fighter_Raptor.prefab";
        private const string RAPTOR_BRAIN_PATH = "Assets/Agents/RaptorBalance01/RaptorBalance01.onnx";
        private const string RAPTOR_DISPLAY_NAME = "Raptor";

        // forceHeuristic: code-driven PD bot (project rule: the heuristic bot
        // competes in the game) — never loads a brain, never warns about one.
        private static readonly (string prefabPath, string display, bool forceHeuristic)[] Roster =
        {
            ("Assets/Prefabs/Fighters/Fighter_Capsule.prefab", "Standard", false),
            ("Assets/Prefabs/Fighters/Fighter_Grandma.prefab", "Grandma", false),
            ("Assets/Prefabs/Fighters/Fighter_Grandpa.prefab", "Grandpa", false),
            (RAPTOR_PREFAB_PATH, RAPTOR_DISPLAY_NAME, false),
            ("Assets/Prefabs/Fighters/Fighter_Capsule.prefab", "Bot", true)
        };

        /// <summary>
        /// The whole balance contest scene, in one idempotent pass.
        ///
        /// This used to be eight menu items (7, 7b, 7d..7i) that had to be
        /// clicked in the right order — each one a migration bolted onto the
        /// last, with 7c already lost to history. Running them out of order,
        /// or stopping half way, produced a scene that looked built and was
        /// not. The steps below are still individually public so they can be
        /// driven one at a time from the CLI or over MCP:
        ///
        ///   Unity.exe -batchmode -quit -projectPath . \
        ///     -executeMethod PoBox.Editor.RigTool_ContestScene.BuildAll
        ///
        /// Only this entry point carries a MenuItem.
        /// </summary>
        [MenuItem("Tools/ML Boxing/7. Build Balance Contest Scene")]
        public static void BuildAll()
        {
            Create();
            AddBotToOpenScene();
            AddPhysicsRopes();
            AddAnnouncerAndHazards();
            UpgradeContestPresentation();
            AddMatchDirector();
            RetargetRosterToLocomotionBrain();
            AddRaptorToRoster();
            CleanUpBalanceScene();
            Debug.Log("RigTool: balance contest scene built end to end.");
        }

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
            // One shared brain for the whole roster. There used to be a
            // per-character lookup at Assets/Agents/{display}/Boxer.onnx here,
            // but no per-character brain ever beat the shared locomotion line,
            // and RetargetRosterToLocomotionBrain overwrote whatever this
            // assigned two steps later anyway — so the per-character files sat
            // in the repo being loaded and then discarded, every build.
            var model = AssetDatabase.LoadAssetAtPath<Unity.InferenceEngine.ModelAsset>(LOCOMOTION_BRAIN_PATH);
            if (model != null)
            {
                behavior.Model = model;
                behavior.BehaviorType = BehaviorType.InferenceOnly;
            }
            else
            {
                behavior.BehaviorType = BehaviorType.HeuristicOnly; // zero actions: pure physics
                Debug.LogWarning($"RigTool: no brain at {LOCOMOTION_BRAIN_PATH} — {display} competes on raw physics.");
            }

            // Returned so the walk contest builder can re-aim and re-brand the
            // same contestant without duplicating this wiring.
            return instance;
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
        /// <summary>
        /// Removes what SCN_MENU made redundant, and re-homes what those objects
        /// were carrying. Idempotent — safe to re-run.
        ///
        /// The balance scene is built INCREMENTALLY by 7 / 7b..7g, so it cannot
        /// be regenerated wholesale the way the walk scene can; anything a later
        /// pass added would go with it. Hence a targeted pass, in the same shape
        /// as 7h.
        ///
        /// Two objects go:
        ///
        /// ContestSetupMenu — a whole second opening menu, title, dropdowns and
        /// all, living ACTIVE in the shipping scene and reachable only if the
        /// player somehow arrives without a selection. SCN_MENU (build index 0)
        /// has asked the same question before this scene loads since the
        /// mini-game picker landed, so on every real path through the game this
        /// screen built itself, read the selection, and hid again. Deleting it
        /// takes ~200 lines of runtime UI out of the build.
        ///
        /// CubeThrower — an unannounced second projectile hazard. It predates
        /// Systems_HazardDirector and now runs alongside it: BALL RAIN drops
        /// spheres from above while this lobbed cubes from the side, with
        /// nothing on screen explaining either, and its own ramp took throws to
        /// 20 m/s — hard enough to floor anything, which is not a difficulty
        /// curve so much as a timer. Hazards are the director's job.
        ///
        /// What has to be RE-HOMED is the reason this is a tool and not a
        /// delete. The version stamp and the FPS readout both rode the setup
        /// menu's UIDocument, because it was the one panel that outlived the
        /// match. Removing the menu without this takes the mandated opening
        /// version stamp (project rule) out of the scene with it, so a HudChrome
        /// object is added to carry them — exactly what the walk contest scene
        /// tool already builds.
        /// </summary>
        public static void CleanUpBalanceScene()
        {
            int removed = 0;
            removed += DestroyByName("ContestSetupMenu");
            removed += DestroyByName("CubeThrower");

            // The setup menu was not only a menu: it was also what read the
            // selection and called SpawnAndBegin. Deleting it without putting
            // something back left the balance contest with a full roster, a
            // referee and a camera, and nobody to start it — an empty ring, no
            // HUD, and a camera slowly orbiting it forever. Systems_MiniGameLauncher
            // is that job with the menu taken out; the walk contest has used it
            // since it was written.
            var spawner = UnityEngine.Object.FindFirstObjectByType<Systems_ContestSpawner>(
                FindObjectsInactive.Include);
            if (spawner == null)
            {
                Debug.LogError("RigTool: no Systems_ContestSpawner in this scene — is SCN_TEST_BALANCE_CONTEST open?");
                return;
            }
            var launcher = spawner.GetComponent<Systems_MiniGameLauncher>();
            if (launcher == null)
            {
                launcher = spawner.gameObject.AddComponent<Systems_MiniGameLauncher>();
                Debug.Log("RigTool: added Systems_MiniGameLauncher to the spawner — it starts the contest now.");
            }
            // Re-wired every run: the serialized references are what actually
            // make it work, and an already-present launcher with a null spawner
            // fails exactly as silently as no launcher at all.
            launcher.EditorInitialize(spawner, RigTool_MenuScene.GetOrCreateSelectionAsset());
            EditorUtility.SetDirty(launcher);

            // Only after the menu is gone, so what it carried is gone with it.
            GameObject chrome = GameObject.Find("HudChrome");
            if (chrome == null)
            {
                chrome = new GameObject("HudChrome");
                var document = chrome.AddComponent<UIDocument>();
                document.panelSettings = GetOrCreatePanelSettings();
                chrome.AddComponent<Systems_VersionStamp>();
                Debug.Log("RigTool: added HudChrome (version stamp) to carry what ContestSetupMenu used to.");
            }

            ResyncDramaCameraFraming();

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();
            Debug.Log($"RigTool: balance contest scene cleaned up — {removed} object(s) removed, HUD chrome in place, scene saved.");
        }

        /// <summary>
        /// Framing fields the drama camera itself is the authority on. Listed
        /// here only so the tool knows WHICH fields to re-sync; the values come
        /// from the component.
        /// </summary>
        private static readonly string[] DramaCameraFramingFields =
        {
            "_closeFrameWidth", "_closeFrameHeight",
            "_wideFrameWidth", "_wideFrameHeight",
            "_minFollowDistance", "_cameraHeight",
            "_groupCameraHeight", "_groupFov",
            "_closeLookLift", "_groupLookLift",
            "_aftermathFrameWidth", "_aftermathCameraHeight"
        };

        /// <summary>
        /// Copies the drama camera's own framing defaults over whatever the
        /// scene has serialized for them.
        ///
        /// Necessary because a [SerializeField] default is only ever read when
        /// the component is FIRST added. Every value the camera carries was
        /// baked into SCN_TEST_BALANCE_CONTEST when the component went on, so
        /// retuning the framing in C# changed precisely nothing about the
        /// scene — it went on framing 7 m of ring from 5.5 m up at 80 degrees
        /// while the source said otherwise. That silent divergence is exactly
        /// the trap this project's other stale-constant bugs came out of.
        ///
        /// Read off a throwaway instance rather than restated here, so the
        /// component stays the single source of truth and this list only has to
        /// name the fields.
        /// </summary>
        private static void ResyncDramaCameraFraming()
        {
            var camera = UnityEngine.Object.FindFirstObjectByType<Systems_DramaCamera>(FindObjectsInactive.Include);
            if (camera == null)
            {
                Debug.LogWarning("RigTool: no Systems_DramaCamera in this scene — framing not re-synced.");
                return;
            }
            // Inactive so nothing on it runs; RequireComponent brings a Camera
            // along, which is why this is not just a `new`.
            var scratch = new GameObject("RigToolDramaCameraDefaults");
            scratch.SetActive(false);
            try
            {
                var defaults = new SerializedObject(scratch.AddComponent<Systems_DramaCamera>());
                var target = new SerializedObject(camera);
                var changes = new System.Text.StringBuilder();
                for (int fieldIndex = 0; fieldIndex < DramaCameraFramingFields.Length; fieldIndex++)
                {
                    string field = DramaCameraFramingFields[fieldIndex];
                    SerializedProperty source = defaults.FindProperty(field);
                    SerializedProperty destination = target.FindProperty(field);
                    if (source == null || destination == null)
                    {
                        Debug.LogWarning($"RigTool: Systems_DramaCamera has no field '{field}' — skipped.");
                        continue;
                    }
                    if (!Mathf.Approximately(source.floatValue, destination.floatValue))
                    {
                        changes.Append($"{field} {destination.floatValue:F2}->{source.floatValue:F2}  ");
                        destination.floatValue = source.floatValue;
                    }
                }
                target.ApplyModifiedPropertiesWithoutUndo();
                Debug.Log(changes.Length > 0
                    ? $"RigTool: drama camera framing re-synced — {changes}"
                    : "RigTool: drama camera framing already matches the component defaults.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(scratch);
            }
        }

        /// <summary>
        /// Destroys every object with this name, active or not; returns how many
        /// went.
        ///
        /// Deliberately not GameObject.Find, which only searches ACTIVE objects.
        /// Everything worth removing here lives under ContestSystems, and that
        /// root is inactive at author time by design — the spawner wakes it once
        /// the fighters exist — so Find returned null for the cube thrower and
        /// reported success having removed nothing.
        /// </summary>
        private static int DestroyByName(string objectName)
        {
            GameObject[] all = UnityEngine.Object.FindObjectsByType<GameObject>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            int removed = 0;
            for (int objectIndex = 0; objectIndex < all.Length; objectIndex++)
            {
                GameObject candidate = all[objectIndex];
                // Null-checked because destroying a parent takes its children
                // with it, and they are still in this array.
                if (candidate == null || candidate.name != objectName)
                {
                    continue;
                }
                UnityEngine.Object.DestroyImmediate(candidate);
                removed++;
            }
            if (removed > 0)
            {
                Debug.Log($"RigTool: removed {removed} x {objectName} from the contest scene.");
            }
            return removed;
        }

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
                // The raptor is its own model line (114 obs vs the humanoids'
                // 127) — the shared brain can never fit it. See AddRaptorToRoster.
                if (entry.FindPropertyRelative("displayName").stringValue == RAPTOR_DISPLAY_NAME)
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
        /// Inserts the Raptor into the open contest scene's spawner roster,
        /// before the Bot so the Bot stays last (menu convention). Idempotent:
        /// an existing Raptor entry is re-pointed at the prefab and brain
        /// rather than duplicated. The balance spawner itself lives only in
        /// the saved scene (its creation step, 7c, is lost to history), so the
        /// roster is edited in place exactly like the retarget pass above.
        ///
        /// SCN_MENU's fighter list must stay index-aligned with this roster —
        /// RigTool_MenuScene owns that side.
        /// </summary>
        public static void AddRaptorToRoster()
        {
            var spawner = UnityEngine.Object.FindFirstObjectByType<Systems_ContestSpawner>();
            if (spawner == null)
            {
                Debug.LogWarning("RigTool: no ContestSpawner in the open scene — open SCN_TEST_BALANCE_CONTEST first.");
                return;
            }
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RAPTOR_PREFAB_PATH);
            if (prefab == null)
            {
                Debug.LogWarning($"RigTool: no raptor prefab at {RAPTOR_PREFAB_PATH} — " +
                    "run Tools > ML Boxing > 10. Build Raptor Fighter first. Roster left untouched.");
                return;
            }
            // Null until a raptor balance run is exported; the spawner then
            // falls back to the heuristic bot on its own, which is the honest
            // behaviour for a fighter whose brain has not landed yet.
            var brain = AssetDatabase.LoadAssetAtPath<Unity.InferenceEngine.ModelAsset>(RAPTOR_BRAIN_PATH);

            var serialized = new SerializedObject(spawner);
            SerializedProperty roster = serialized.FindProperty("_roster");
            int raptorIndex = -1;
            int botIndex = roster.arraySize;
            for (int entryIndex = 0; entryIndex < roster.arraySize; entryIndex++)
            {
                SerializedProperty existing = roster.GetArrayElementAtIndex(entryIndex);
                string display = existing.FindPropertyRelative("displayName").stringValue;
                if (display == RAPTOR_DISPLAY_NAME)
                {
                    raptorIndex = entryIndex;
                }
                else if (display == "Bot")
                {
                    botIndex = entryIndex;
                }
            }
            if (raptorIndex < 0)
            {
                raptorIndex = Mathf.Min(botIndex, roster.arraySize);
                roster.InsertArrayElementAtIndex(raptorIndex);
            }
            SerializedProperty entry = roster.GetArrayElementAtIndex(raptorIndex);
            entry.FindPropertyRelative("displayName").stringValue = RAPTOR_DISPLAY_NAME;
            entry.FindPropertyRelative("prefab").objectReferenceValue = prefab;
            entry.FindPropertyRelative("model").objectReferenceValue = brain;
            entry.FindPropertyRelative("forceHeuristic").boolValue = false;
            // The raptor's brain is a balance brain — never the locomotion layout.
            entry.FindPropertyRelative("locomotionBrain").boolValue = false;
            entry.FindPropertyRelative("tint").objectReferenceValue = null;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();
            Debug.Log($"RigTool: Raptor in roster slot {raptorIndex} " +
                $"(brain: {(brain != null ? RAPTOR_BRAIN_PATH : "none yet — heuristic bot")}) and scene saved.");
        }

        /// <summary>
        /// Adds the soft physics ropes (Systems_RingRopes builds the chains at
        /// runtime). Always-active: ropes exist before fighters spawn.
        /// </summary>
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

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();
            Debug.Log("RigTool: presentation upgraded (listener, lights, knockout FX, orbit, winner cam) and saved.");
        }

        /// <summary>
        /// Adds the match director (first to 3 round wins -> champion ->
        /// scene reload back to the menu) under the contest systems root.
        /// </summary>
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
