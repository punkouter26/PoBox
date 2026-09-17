using System.IO;
using System.Text;
using System.Xml;
using Mujoco;
using PoBox.MuJoCoCreature;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace PoBox.Editor
{
    /// <summary>
    /// The Nick / MuJoCo Warp line's editor entry points.
    ///
    ///   BuildDemoScene  Nick alone on a floor, skinned mesh bound to the
    ///                   MuJoCo ragdoll, camera, HUD, and a driver that cycles
    ///                   BALANCE and WALK and logs NICK_DEMO lines.
    ///   ExportNickMjcf  Writes the MJCF that MjScene ACTUALLY generates from
    ///                   the demo scene to Tools/MuJoCo/nick_unity.xml. This,
    ///                   not the authored creature.xml, is what training must
    ///                   run on: the 2026-08-31 line found that a policy
    ///                   trained on the authored file collapses in Unity.
    ///   PlayDemo / StopDemo  drive the scene from outside the Editor.
    ///
    /// All reachable through Editor_CommandBridge while the Editor is open:
    ///
    ///   echo PoBox.Editor.RigTool_NickMuJoCo.BuildDemoScene > Temp/agent-command.txt
    ///
    /// Idempotent: re-running BuildDemoScene replaces the scene wholesale, so
    /// do not hand-edit it.
    /// </summary>
    internal static class RigTool_NickMuJoCo
    {
        private const string SOURCE_SCENE_PATH = "Assets/MuJoCoCreature/Scenes/MuJoCo_TestScene.unity";
        private const string SOURCE_ROOT_NAME = "Creature";
        private const string DEMO_SCENE_PATH = "Assets/MuJoCoCreature/Scenes/Nick_DemoScene.unity";
        private const string CONTEST_SCENE_PATH = "Assets/Scenes/SCN_TEST_BALANCE_CONTEST.unity";
        private const string NICK_GLB_PATH = "Assets/MuJoCoCreature/Model/RIGGED_Nick.glb";
        private const string BALANCE_BRAIN_PATH = "Assets/Agents/Nick_Balance002/nick_balance_002.onnx";
        // The torque-limited body's walker (loop 2): 127 obs / 30 actions,
        // trained at 0.02 s x decimation 1 on the nick_torque.xml body. The
        // scene's actuators must carry nick_torque.limits.json BEFORE this
        // brain is placed, or the body and the brain disagree.
        // Torque002 is nick_lB_torque/model_250: the sweep found 250 beats
        // 300 (Torque001) on every walk table — WALK 62% vs 48%, median 20 s
        // (= the full cap) vs 17.7 s.
        private const string TORQUE_BRAIN_PATH = "Assets/Agents/Nick_Torque002/nick_torque_002.onnx";
        private const string RING_SCENE_PATH = "Assets/MuJoCoCreature/Scenes/Nick_BalanceRing.unity";
        private const string GETUP_SCENE_PATH = "Assets/MuJoCoCreature/Scenes/Nick_GetUpPractice.unity";
        private const string MJCF_EXPORT_PATH = "Tools/MuJoCo/nick_unity.xml";
        private const string PANEL_SETTINGS_PATH = "Assets/UI/PS_Contest.asset";
        // Written by Tools/MuJoCo/export_onnx.py. A constant, so a stale brain
        // is visible here rather than buried in a scene file.
        private const string LOCOMOTION_BRAIN_PATH = "Assets/Agents/Nick_Locomotion/nick_locomotion.onnx";
        // Training env control rate: 4 physics steps of 0.005 s per decision.
        private const int LOCOMOTION_DECIMATION = 4;
        private const float TRAINING_TIMESTEP = 0.005f;

        [MenuItem("PoBox/Nick/Build Demo Scene")]
/// <summary>
        /// Nick's own balance ring. Same construction as the demo scene -- the
        /// creature cloned from the source scene, the same environment, the
        /// same HUD -- but driven by Systems_NickBalanceRing, which runs 30 s
        /// rounds under shoves that get 100 N harder each time and logs
        /// CONTEST_ROUND exactly as Systems_BalanceContest does.
        ///
        /// It is a SEPARATE scene from SCN_TEST_BALANCE_CONTEST because the two
        /// cannot share a timestep; the measurements are in the summary on
        /// Systems_NickBalanceRing.
        /// </summary>
        [MenuItem("PoBox/Nick/Build Balance Ring")]
        public static void BuildBalanceRing()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            GameObject nick = CloneCreatureFromSourceScene(scene);
            if (nick == null) { return; }

            BuildEnvironment(out Transform cameraTransform);
            ConfigureController(nick, out CreatureSentisController controller);
            UIDocument hud = BuildHud();

            // A balance ring keeps him on the spot, so the camera does not need
            // to follow -- which also sidesteps the demo camera's Y-up offset
            // being applied in the creature's Z-up frame.
            if (cameraTransform != null)
            {
                cameraTransform.position = new Vector3(3.0f, 1.6f, -3.0f);
                cameraTransform.LookAt(new Vector3(0f, 0.9f, 0f));
            }

            var ring = nick.AddComponent<Systems_NickBalanceRing>();
            var serialized = new SerializedObject(ring);
            serialized.FindProperty("_controller").objectReferenceValue = controller;
            serialized.FindProperty("_hud").objectReferenceValue = hud;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            Directory.CreateDirectory(Path.GetDirectoryName(RING_SCENE_PATH));
            EditorSceneManager.SaveScene(scene, RING_SCENE_PATH);
            Debug.Log($"RigTool: built {RING_SCENE_PATH}. Press Play, or run it and grep CONTEST_ROUND.");
        }

        /// <summary>Opens Nick's balance ring and plays it; CONTEST_ROUND lines follow.</summary>
        public static void PlayBalanceRing()
        {
            if (EditorApplication.isPlaying) { return; }
            EditorSceneManager.OpenScene(RING_SCENE_PATH, OpenSceneMode.Single);
            Debug.Log("RigTool: entering play mode on Nick's balance ring. Watch for CONTEST_ROUND lines.");
            EditorApplication.EnterPlaymode();
        }

        /// <summary>
        /// Nick's get-up practice ring. Same construction as the balance ring
        /// -- creature cloned from the source scene, same environment, same
        /// HUD -- but driven by Systems_NickGetUpRing: he stands, an automatic
        /// 1800 N shove puts him on the floor, and the ring times his rise.
        /// Success is head above 75% of standing height, HELD 4 s -- the same
        /// rule the get-up task in Tools/MuJoCo/nick_env.py rewards.
        ///
        /// The controller gets the TORQUE brain (the one the contests run) at
        /// decimation 1, and _fallHeight = -1: a fallen body STAYS fallen, or
        /// every "success" would be the controller teleporting him upright.
        /// The brain on disk today cannot get up at all, so the first runs of
        /// this scene are expected to log FAIL after FAIL -- that is the
        /// baseline the new training has to beat.
        /// </summary>
        [MenuItem("PoBox/Nick/Build Get-Up Practice Scene")]
        public static void BuildGetUpRing()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            GameObject nick = CloneCreatureFromSourceScene(scene);
            if (nick == null) { return; }

            BuildEnvironment(out Transform cameraTransform);
            ConfigureController(nick, out CreatureSentisController controller);
            UIDocument hud = BuildHud();

            // The brain must match the body AND the 0.02 s ring step, exactly
            // as in the contest scenes; see AddNickToContest for the reasoning
            // behind each line.
            var serialized = new SerializedObject(controller);
            var brain = AssetDatabase.LoadAssetAtPath<ModelAsset>(TORQUE_BRAIN_PATH);
            if (brain == null)
            {
                Debug.LogError($"RigTool: no brain at {TORQUE_BRAIN_PATH}; build refused.");
                return;
            }
            serialized.FindProperty("_onnxModelAsset").objectReferenceValue = brain;
            serialized.FindProperty("_observeLocomotionCommand").boolValue = true;
            serialized.FindProperty("_decimation").intValue = 1;
            serialized.FindProperty("_fixedTimestepOverride").floatValue = 0f;
            // NO AUTO-RESET: the whole point of this scene is that he is on
            // the floor and has to get up by himself.
            serialized.FindProperty("_fallHeight").floatValue = -1f;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            // He practices on the spot, so a fixed camera like the ring's.
            if (cameraTransform != null)
            {
                cameraTransform.position = new Vector3(3.0f, 1.6f, -3.0f);
                cameraTransform.LookAt(new Vector3(0f, 0.9f, 0f));
            }

            var ring = nick.AddComponent<Systems_NickGetUpRing>();
            var ringSerialized = new SerializedObject(ring);
            ringSerialized.FindProperty("_controller").objectReferenceValue = controller;
            ringSerialized.FindProperty("_hud").objectReferenceValue = hud;
            ringSerialized.ApplyModifiedPropertiesWithoutUndo();

            Directory.CreateDirectory(Path.GetDirectoryName(GETUP_SCENE_PATH));
            EditorSceneManager.SaveScene(scene, GETUP_SCENE_PATH);
            Debug.Log($"RigTool: built {GETUP_SCENE_PATH}. Press Play, or run it and grep GETUP_RESULT.");
        }

        /// <summary>Opens the get-up practice scene and plays it; GETUP_RESULT lines follow.</summary>
        [MenuItem("PoBox/Nick/Play Get-Up Practice Scene")]
        public static void PlayGetUpRing()
        {
            if (EditorApplication.isPlaying) { return; }
            if (!File.Exists(GETUP_SCENE_PATH))
            {
                Debug.LogError($"RigTool: {GETUP_SCENE_PATH} does not exist; run Build Get-Up Practice Scene first.");
                return;
            }
            EditorSceneManager.OpenScene(GETUP_SCENE_PATH, OpenSceneMode.Single);
            Debug.Log("RigTool: entering play mode on the get-up practice scene. Watch for GETUP_RESULT lines.");
            EditorApplication.EnterPlaymode();
        }

        /// <summary>
        /// Saves any open scenes and quits the Editor. For the long-run rule
        /// (AGENTS.md): a MuJoCo RL run of 30 minutes or more gets the whole
        /// machine, so the Editor is closed first — through here, cleanly,
        /// rather than killed. Run it through the command bridge just before
        /// launching the trainer.
        /// </summary>
        [MenuItem("PoBox/Nick/Quit Editor (for long runs)")]
        public static void QuitEditor()
        {
            EditorSceneManager.SaveOpenScenes();
            Debug.Log("RigTool: scenes saved; quitting the Editor for a headless training run.");
            EditorApplication.Exit(0);
        }

        /// <summary>
        /// Adds Nick to the shipped PhysX balance ring, or refreshes him if he
        /// is already there.
        ///
        /// He is placed AT AUTHOR TIME and the spawner adopts him: its roster
        /// holds prefabs while Nick is cloned from a scene.
        /// Systems_BalanceContest finds him through IContestFighter, so the
        /// referee needs nothing else.
        ///
        /// He runs at the RING's timestep, not his own: the controller's
        /// timestep pin is switched OFF here, because pinning 0.005 s would
        /// drop Standard from 30.0 s to 2.9 s.
        /// </summary>
/// <summary>
        /// Swaps Nick's collision capsules for the skinned mesh from
        /// RIGGED_Nick.glb.
        ///
        /// MuJoCo never sees the skin: the MJCF carries capsules and inertias
        /// only, so physics runs on the rigid bodies while SkinnedRigBinder
        /// copies each body's world transform onto the matching bone every
        /// LateUpdate. The capsules stay -- they ARE the physics -- their
        /// renderers just stop drawing.
        ///
        /// Returns the number of bone->body links, which is the thing worth
        /// checking: a silent 0 means the bone names did not match and the mesh
        /// will hang in the bind pose while the capsules move underneath it.
        /// </summary>
        private static int AttachNickSkin(GameObject nick)
        {
            var glb = AssetDatabase.LoadAssetAtPath<GameObject>(NICK_GLB_PATH);
            if (glb == null)
            {
                Debug.LogError($"RigTool: no rigged mesh at {NICK_GLB_PATH}; Nick keeps his capsules.");
                return 0;
            }

            foreach (SkinnedRigBinder stale in nick.GetComponentsInChildren<SkinnedRigBinder>(true))
            {
                Object.DestroyImmediate(stale.gameObject);
            }

            var skin = (GameObject)PrefabUtility.InstantiatePrefab(glb, nick.transform);
            skin.name = "NickSkin";
            skin.transform.localPosition = Vector3.zero;
            skin.transform.localRotation = Quaternion.identity;

            var renderers = skin.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers.Length == 0)
            {
                Debug.LogError($"RigTool: {NICK_GLB_PATH} has no SkinnedMeshRenderer; Nick keeps his capsules.");
                Object.DestroyImmediate(skin);
                return 0;
            }

            var binder = skin.AddComponent<SkinnedRigBinder>();
            int links = binder.Rebind(skin.transform);
            if (links == 0)
            {
                Debug.LogError("RigTool: SkinnedRigBinder matched NO bones to bodies — " +
                               "the mesh would hang in its bind pose. Capsules kept.");
                Object.DestroyImmediate(skin);
                return 0;
            }

            // Hide the physics capsules now that there is something better to
            // look at. Only the g_* geom renderers: the cloned floor and
            // anything else in the hierarchy are left alone.
            int hidden = 0;
            foreach (MeshRenderer renderer in nick.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!renderer.name.StartsWith("g_")) { continue; }
                renderer.enabled = false;
                hidden++;
            }
            Debug.Log($"RigTool: Nick skinned — {links} bone links, {hidden} capsule renderer(s) hidden.");
            return links;
        }

        /// <summary>
        /// Places Nick in BOTH contest scenes on the torque-limited body with
        /// the Nick_Torque001 walker. The body comes from the source scene, so
        /// run PoBox/Nick/Apply Torque-Only Limits first — this entry refuses
        /// to place a brain on a body it was not trained on.
        /// </summary>
        [MenuItem("PoBox/Nick/Add To Contests (Torque001)")]
        public static void AddNickToContestsTorque001()
        {
            AddNickToContest(CONTEST_SCENE_PATH,
                             new Vector3(0.75f, Systems_ContestSpawner.RING_FLOOR_Y, -0.7f),
                             TORQUE_BRAIN_PATH);
            // 180 deg Y is the heading the walk race was validated with (the
            // committed scene carried it, and the creatures' tool turns every
            // walker the same way): the command is pelvis-local, so a clone
            // straight out of the source scene reads the goal direction
            // backwards and falls on the line.
            AddNickToContest("Assets/Scenes/SCN_TEST_WALK_CONTEST.unity",
                             new Vector3(2.75f, 0.03f, -2.8f),
                             TORQUE_BRAIN_PATH,
                             Quaternion.Euler(0f, 180f, 0f));
        }

        /// <summary>
        /// Opens a contest scene and enters play mode on it, so the placed
        /// roster can be WATCHED — the motion is part of how a policy is
        /// judged, and the referee logs CONTEST_ROUND lines while it runs.
        /// </summary>
        [MenuItem("PoBox/Nick/Play Balance Contest")]
        public static void PlayBalanceContest() => PlayContest(CONTEST_SCENE_PATH);

        /// <summary>Same, on the walk race.</summary>
        [MenuItem("PoBox/Nick/Play Walk Contest")]
        public static void PlayWalkContest() => PlayContest("Assets/Scenes/SCN_TEST_WALK_CONTEST.unity");

        /// <summary>Exits play mode from outside the Editor.</summary>
        [MenuItem("PoBox/Nick/Stop Contest")]
        public static void StopContest()
        {
            if (EditorApplication.isPlaying) { EditorApplication.ExitPlaymode(); }
            Debug.Log("RigTool: exiting play mode.");
        }

        private static void PlayContest(string scenePath)
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogError("RigTool: already in play mode; stop it first.");
                return;
            }
            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            Debug.Log($"RigTool: entering play mode on {Path.GetFileName(scenePath)}. Watch for CONTEST_ROUND lines.");
            EditorApplication.EnterPlaymode();
        }

                [MenuItem("PoBox/Nick/Add To Balance Ring")]
        public static void AddNickToBalanceContest() =>
            AddNickToContest(CONTEST_SCENE_PATH, new Vector3(0.75f, Systems_ContestSpawner.RING_FLOOR_Y, -0.7f));

        /// <summary>
        /// Nick on the walk race's start line. He runs the SAME 0.02 s balance
        /// brain -- it is the only one that works at the ring's step -- and it
        /// walks at 0.853 m/s with alternation 1.000 but stays up only ~2.2 s
        /// against a 5.6 m goal. Placing him is honest; expect him to fall short
        /// until a walk brain is trained at 0.02 s.
        /// </summary>
        [MenuItem("PoBox/Nick/Add To Walk Race")]
        public static void AddNickToWalkContest() =>
            AddNickToContest("Assets/Scenes/SCN_TEST_WALK_CONTEST.unity", new Vector3(2.75f, 0.03f, -2.8f));

        /// <summary>
        /// Places or refreshes Nick in one contest scene. The brain is a
        /// parameter because body and brain are a PAIR: Balance002 belongs on
        /// the human-limits body, Nick_Torque001 on the torque-limited one.
        /// Whichever brain is passed, it must have been trained at the ring's
        /// 0.02 s x decimation 1 and on the body the source scene now carries.
        /// </summary>
        private static void AddNickToContest(string scenePath, Vector3 position,
                                             string brainPath = BALANCE_BRAIN_PATH,
                                             Quaternion rotation = default)
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogError("RigTool: leave play mode before editing the contest scene.");
                return;
            }
            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name == "Nick") { Object.DestroyImmediate(root); }
            }

            GameObject nick = CloneCreatureFromSourceScene(scene);
            if (nick == null) { return; }
            ConfigureController(nick, out CreatureSentisController controller);

            var serialized = new SerializedObject(controller);
            // The brain must match the body the source scene carries AND the
            // ring's 0.02 s step: Balance002 on the human-limits body,
            // nick_torque_001 on the torque-limited one. nick_locomotion.onnx
            // must NOT be placed here under either body: it is the 0.005 s
            // walker and scores at the passive baseline at this step.
            var brain = AssetDatabase.LoadAssetAtPath<ModelAsset>(brainPath);
            if (brain == null)
            {
                Debug.LogError($"RigTool: no brain at {brainPath}; Nick not added.");
                return;
            }
            serialized.FindProperty("_onnxModelAsset").objectReferenceValue = brain;
            serialized.FindProperty("_observeLocomotionCommand").boolValue = true;
            serialized.FindProperty("_decimation").intValue = 1;
            // 0 disables the override: the ring keeps its own 0.02 s.
            serialized.FindProperty("_fixedTimestepOverride").floatValue = 0f;
            // NO AUTO-RESET IN A CONTEST. The controller restarts the creature
            // whenever the pelvis drops below _fallHeight, which is right for
            // TRAINING -- an episode ends on a fall -- and wrong here: Nick
            // popped back onto his feet while the other five stayed down, and a
            // racer that keeps standing up keeps the race alive so the round
            // never ends. A negative height can never be reached, so he now
            // falls and STAYS fallen, judged by head height like everyone else.
            serialized.FindProperty("_fallHeight").floatValue = -1f;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            var contestant = nick.GetComponent<Systems_NickContestant>();
            if (contestant == null) { contestant = nick.AddComponent<Systems_NickContestant>(); }
            var contestantSerialized = new SerializedObject(contestant);
            contestantSerialized.FindProperty("_controller").objectReferenceValue = controller;
            contestantSerialized.ApplyModifiedPropertiesWithoutUndo();

            // The caller passes a free slot. He was at x=2.25 in the ring
            // first, which is on the floor but 1.5 m outside the widest slot --
            // standing beyond the ropes, simulating and scoring correctly while
            // being invisible.
            // default(Quaternion) means "leave the clone's rotation alone";
            // any explicit rotation replaces it (the walk race's 180 Y).
            nick.transform.position = position;
            bool keepCloneRotation = rotation.x == 0f && rotation.y == 0f && rotation.z == 0f && rotation.w == 0f;
            if (!keepCloneRotation) { nick.transform.rotation = rotation; }
            Debug.Log($"RigTool: placed Nick at {nick.transform.position} rotation {nick.transform.rotation.eulerAngles} " +
                      $"(requested {(keepCloneRotation ? "clone-default" : rotation.eulerAngles.ToString())}).");

            AttachNickSkin(nick);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("RigTool: Nick added to the balance ring (decimation 1, timestep pin off, " +
                      $"brain {brainPath}). Play and grep CONTEST_ROUND.");
        }

                public static void BuildDemoScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            GameObject nick = CloneCreatureFromSourceScene(scene);
            if (nick == null) { return; }

            BuildEnvironment(out Transform cameraTransform);
            ConfigureController(nick, out CreatureSentisController controller);
            UIDocument hud = BuildHud();

            var demo = nick.AddComponent<Systems_NickDemo>();
            var serialized = new SerializedObject(demo);
            serialized.FindProperty("_controller").objectReferenceValue = controller;
            serialized.FindProperty("_hud").objectReferenceValue = hud;
            serialized.FindProperty("_camera").objectReferenceValue = cameraTransform;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            Directory.CreateDirectory(Path.GetDirectoryName(DEMO_SCENE_PATH));
            EditorSceneManager.SaveScene(scene, DEMO_SCENE_PATH);
            Debug.Log($"RigTool: built {DEMO_SCENE_PATH}. Press Play, or run it and grep NICK_DEMO.");
        }

        /// <summary>
        /// Exports the MJCF MjScene generates for the demo scene. Runs the
        /// plugin's own exporter path (CreateScene with compile skipped) so the
        /// file is byte-for-byte what the runtime would compile, except for the
        /// timestep: MjcfGenerationContext stamps Time.fixedDeltaTime, which in
        /// the Editor is the project's 0.02 s, so it is set to the 0.005 s the
        /// controller runs the creature at for the duration of the export.
        /// </summary>
        public static void ExportNickMjcf()
        {
            if (Application.isPlaying)
            {
                Debug.LogError("RigTool: MJCF export only works outside Play mode.");
                return;
            }
            if (!File.Exists(DEMO_SCENE_PATH))
            {
                BuildDemoScene();
            }
            EditorSceneManager.OpenScene(DEMO_SCENE_PATH, OpenSceneMode.Single);
            if (MjScene.InstanceExists)
            {
                Debug.LogError("RigTool: an MjScene already exists in the demo scene; export refused.");
                return;
            }

            float previousTimestep = Time.fixedDeltaTime;
            Time.fixedDeltaTime = TRAINING_TIMESTEP;
            XmlDocument mjcf;
            try
            {
                mjcf = MjScene.Instance.CreateScene(skipCompile: true);
            }
            finally
            {
                Time.fixedDeltaTime = previousTimestep;
                if (MjScene.InstanceExists)
                {
                    Object.DestroyImmediate(MjScene.Instance.gameObject);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(MJCF_EXPORT_PATH));
            using (var stream = File.Open(MJCF_EXPORT_PATH, FileMode.Create))
            using (var writer = new XmlTextWriter(stream, new UTF8Encoding(false)))
            {
                writer.Formatting = Formatting.Indented;
                mjcf.WriteContentTo(writer);
            }
            Debug.Log($"RigTool: exported MJCF to {MJCF_EXPORT_PATH} (timestep {TRAINING_TIMESTEP}).");
        }

        public static void PlayDemo()
        {
            if (EditorApplication.isPlaying)
            {
                Debug.Log("RigTool: already in play mode.");
                return;
            }
            if (!File.Exists(DEMO_SCENE_PATH))
            {
                Debug.LogError($"RigTool: {DEMO_SCENE_PATH} does not exist; run BuildDemoScene first.");
                return;
            }
            EditorSceneManager.OpenScene(DEMO_SCENE_PATH, OpenSceneMode.Single);
            EditorApplication.EnterPlaymode();
            Debug.Log("RigTool: entering play mode on the Nick demo. Watch for NICK_DEMO lines.");
        }

        public static void StopDemo()
        {
            if (EditorApplication.isPlaying) { EditorApplication.ExitPlaymode(); }
            Debug.Log("RigTool: exiting play mode.");
        }

        /// <summary>Harmless entry point, so a compile can be forced through the command bridge.</summary>
        public static void Ping()
        {
            Debug.Log("RigTool_NickMuJoCo: ping.");
        }

        /// <summary>
        /// Opens the demo scene with a Systems_NickParityProbe attached (in
        /// memory only) and enters play mode. Driven by Tools/MuJoCo/parity_check.py,
        /// which writes the state first and reads the observation vector back.
        /// </summary>
        public static void RunParityProbe()
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogError("RigTool: already in play mode; stop it first.");
                return;
            }
            if (!File.Exists(DEMO_SCENE_PATH))
            {
                BuildDemoScene();
            }
            EditorSceneManager.OpenScene(DEMO_SCENE_PATH, OpenSceneMode.Single);
            var controller = Object.FindFirstObjectByType<CreatureSentisController>();
            if (controller == null)
            {
                Debug.LogError("RigTool: no CreatureSentisController in the demo scene.");
                return;
            }
            var probe = controller.gameObject.AddComponent<Systems_NickParityProbe>();
            var serialized = new SerializedObject(probe);
            serialized.FindProperty("_controller").objectReferenceValue = controller;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            // The demo driver would reset the creature and overwrite the
            // probe's state; keep it out of this run.
            var demo = controller.GetComponentInParent<Systems_NickDemo>();
            if (demo != null) { demo.enabled = false; }
            EditorApplication.EnterPlaymode();
            Debug.Log("RigTool: parity probe entering play mode. Watch for NICK_PARITY.");
        }

        /// <summary>
        /// Nick is not a prefab: the MuJoCo importer built him straight into
        /// MuJoCo_TestScene, skinned mesh and all. Cloning that root is the
        /// one way to get the same bodies, joints, actuator gains and bone
        /// bindings the shipped balance brain was validated on.
        /// </summary>
        private static GameObject CloneCreatureFromSourceScene(Scene target)
        {
            Scene source = EditorSceneManager.OpenScene(SOURCE_SCENE_PATH, OpenSceneMode.Additive);
            GameObject creature = null;
            foreach (GameObject root in source.GetRootGameObjects())
            {
                if (root.name == SOURCE_ROOT_NAME) { creature = root; break; }
            }
            if (creature == null)
            {
                EditorSceneManager.CloseScene(source, true);
                Debug.LogError($"RigTool: no root named '{SOURCE_ROOT_NAME}' in {SOURCE_SCENE_PATH}.");
                return null;
            }

            GameObject nick = Object.Instantiate(creature);
            nick.name = "Nick";

            // MAKE HIM VISIBLE. MuJoCo_TestScene is a physics test bed where the
            // RaptorRig supplies the visual, so 15 of the creature's 16 geom
            // MeshRenderers are switched OFF in it -- and Instantiate copies that
            // faithfully. Cloned into a demo or a ring the result is a creature
            // that simulates perfectly and cannot be seen, which is exactly what
            // happened: every NICK_DEMO and CONTEST_ROUND number was correct
            // while the game view showed an empty floor. The renderers all have
            // materials; they just needed switching on.
            int shown = 0;
            foreach (MeshRenderer renderer in nick.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!renderer.enabled) { renderer.enabled = true; shown++; }
            }
            if (shown > 0) { Debug.Log($"RigTool: enabled {shown} geom renderer(s) on the clone."); }

            nick.transform.position = Vector3.zero;
            SceneManager.MoveGameObjectToScene(nick, target);
            EditorSceneManager.CloseScene(source, true);
            return nick;
        }

        private static void ConfigureController(GameObject nick, out CreatureSentisController controller)
        {
            controller = nick.GetComponentInChildren<CreatureSentisController>(true);
            if (controller == null)
            {
                Debug.LogError("RigTool: the cloned creature has no CreatureSentisController.");
                return;
            }

            var brain = AssetDatabase.LoadAssetAtPath<ModelAsset>(LOCOMOTION_BRAIN_PATH);
            var serialized = new SerializedObject(controller);
            if (brain != null)
            {
                serialized.FindProperty("_onnxModelAsset").objectReferenceValue = brain;
                serialized.FindProperty("_observeLocomotionCommand").boolValue = true;
                serialized.FindProperty("_decimation").intValue = LOCOMOTION_DECIMATION;
            }
            else
            {
                // Keep the 2026-08-31 balance brain the clone already carries,
                // with its 121-observation contract, so the scene still stands
                // up; it just cannot walk until the locomotion brain exists.
                Debug.LogWarning($"RigTool: no brain at {LOCOMOTION_BRAIN_PATH}. Run " +
                    "Tools/MuJoCo/export_onnx.py first; the scene keeps the old balance brain until then.");
                serialized.FindProperty("_observeLocomotionCommand").boolValue = false;
                serialized.FindProperty("_decimation").intValue = 1;
            }
            serialized.FindProperty("_commandedSpeed").floatValue = 0f;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void BuildEnvironment(out Transform cameraTransform)
        {
            // The MuJoCo floor plane comes with the creature (an MjGeom named
            // "floor" under the root); what is added here is only light and eye.
            var lightObject = new GameObject("Sun");
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            light.shadows = LightShadows.Soft;
            lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            var cameraObject = new GameObject("DemoCamera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.tag = "MainCamera";
            camera.nearClipPlane = 0.05f;
            cameraObject.transform.position = new Vector3(3.2f, 1.8f, -3.2f);
            cameraObject.transform.rotation = Quaternion.Euler(12f, -45f, 0f);
            cameraTransform = cameraObject.transform;
        }

        private static UIDocument BuildHud()
        {
            var hudObject = new GameObject("Hud");
            var document = hudObject.AddComponent<UIDocument>();
            document.panelSettings = AssetDatabase.LoadAssetAtPath<PanelSettings>(PANEL_SETTINGS_PATH);
            if (document.panelSettings == null)
            {
                Debug.LogWarning($"RigTool: no PanelSettings at {PANEL_SETTINGS_PATH}; " +
                    "the readout will not draw, but NICK_DEMO logging still works.");
            }
            // Project rule: the opening scene shows a version stamp, top-left.
            hudObject.AddComponent<Systems_VersionStamp>();
            return document;
        }
    }
}
