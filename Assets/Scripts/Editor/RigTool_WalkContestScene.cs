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
        private const string CONTEST_STYLESHEET_PATH = "Assets/UI/USS_Contest.uss";
        private const float RING_SIZE = 6.1f;      // matches SCN_TRAIN_WALK
        private const float EDGE_INSET = 0.25f;
        private const float SPAWN_HEIGHT = 0.03f;
        private const string BELL_CLIP_PATH = "Assets/Audio/SFX_BoxingBell.wav";
        private const string CROWD_AMBIENCE_PATH = "Assets/Audio/Contest/crowd_ambience.mp3";
        private const string CROWD_CHEER_PATH = "Assets/Audio/Contest/crowd_cheer_short.mp3";
        private static readonly string[] ThudClipPaths =
        {
            "Assets/Audio/Contest/impactSoft_heavy_000.ogg",
            "Assets/Audio/Contest/impactSoft_heavy_001.ogg",
            "Assets/Audio/Contest/impactSoft_heavy_002.ogg"
        };

        // Lane spacing is chosen for the FRAME, not for the ring: four racers
        // spread over the full 6.1 m canvas span 4.6 m, and a 36 degree
        // horizontal FOV cannot hold that at any distance a racer still reads as
        // a person. 1.1 m keeps the field 3.3 m wide - ample separation for
        // fighters walking a straight line - and lets the camera sit close
        // enough that a racer is about a quarter of the frame height at the
        // finish.
        private const float LANE_SPACING = 1.1f;
        // Half a shoulder width, so the framing check covers the body rather
        // than the centre line.
        private const float BODY_HALF_WIDTH = 0.35f;
        private const float CAMERA_HEIGHT = 3.4f;
        // What the camera aims at: lane centre, roughly chest height.
        private const float CAMERA_AIM_HEIGHT = 0.9f;
        // Portrait 9:16 is the shipping aspect (the WebGL template letterboxes
        // to it), and it is what the framing has to survive - a 60 degree
        // vertical FOV is only 36 degrees horizontal there.
        private const float DESIGN_ASPECT = 9f / 16f;
        private const float CAMERA_FOV = 60f;
        private const float MIN_CAMERA_SETBACK = 1.5f;
        private const float MAX_CAMERA_SETBACK = 30f;
        // Shared stand-and-walk brain; the walk race commands it to 1 m/s.
        // Was Locomotion_v01, which moved to Assets/Agents/_obsolete_125obs/
        // when the gait-phase observations took the layout from 125 to 127.
        // The path was left dangling, so ResolveBrain fell silently through to
        // the balance brain and every racer just stood there. Point this at
        // the CURRENT locomotion folder whenever a new model line lands.
        // gen 7 as of 2026-08-20: 127 observations and the first brain here built
        // against ground-relative height. Locomotion_v01/v02/v03 all predate that
        // and v03 is really gen 5, so rebuilding against them silently downgrades
        // the roster to a brain that cannot stand on the current rig.
        // Gen 15: the first genuine ALTERNATING gait. Verified in inference over
        // 31,901 earning steps -- Alternation 0.736 (a stance change every
        // ~0.95 s), SingleSupportMean 0.275, SpeedMatchMean 0.885 at 0.46 m/s.
        //
        // It replaced gen 13, which posted a higher SingleSupportMean (0.951)
        // and the same travel speed while standing on its LEFT leg with the
        // right held permanently in the air. Alternation counts stance CHANGES,
        // so unlike SingleSupportMean it cannot be collected by a one-legged
        // hop -- which is why gen 13 scores ~0 on it. Same distance, and this
        // one looks like walking.
        //
        // Neither finishes: gen 15 topples about every 1.7 s, roughly two steps.
        // Gen 7 -- which this scene shipped for five generations -- travelled
        // nowhere at all, so the race was unwinnable by construction.
        //
        // Per-character brains at Assets/Agents/{name}_Walk/Boxer.onnx still
        // take precedence -- see ResolveBrain.
        private const string LOCOMOTION_BRAIN_PATH = "Assets/Agents/Locomotion_gen15/Locomotion_gen15.onnx";

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
            float laneSpacing = LANE_SPACING;
            float laneOrigin = -laneSpacing * (Roster.Length - 1) * 0.5f;

            BuildGround();
            BuildLight();
            BuildCamera(goalZ, startZ, Mathf.Abs(laneOrigin));
            BuildFinishLine(goalZ);

            // The referee lives under an inactive root the spawner wakes, so
            // it discovers the racers in its own Start AFTER they exist.
            var systemsRoot = new GameObject("ContestSystems");
            BuildReferee(systemsRoot, goalDistance);
            BuildPresentation(systemsRoot);
            systemsRoot.SetActive(false);

            // Always-active: these have to be up before the racers spawn.
            BuildHudChrome();

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
        // locomotion observation layout those brains require. The size is
        // computed by Agent_FighterBoxing.ComputeObservationCount, so it
        // tracks the layout automatically (127 as of the gen-4 gait phase).
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

        /// <summary>
        /// Elevated finish-line camera looking back up the lane.
        ///
        /// Replaces a hard-coded side-on camera at (-7.5, 3, 0) that ignored the
        /// lane it was given (the old body ended with a literal `_ = goalZ;`).
        /// Side-on needs the full 5.6 m lane LENGTH to fit horizontally, and at
        /// portrait 9:16 the horizontal FOV is only 36 degrees: measured
        /// 2026-08-20, three of the four racers started outside the frame
        /// (viewport x 1.29, 1.12, 1.01) and the finish line sat off the other
        /// edge at -0.04, with the fighters squeezed into the right 15% of the
        /// picture under a half-frame of empty sky.
        ///
        /// Head-on from beyond the finish only has to fit the 4.6 m lane SPREAD,
        /// which portrait handles, and it uses the tall frame the way a real
        /// finish-line camera does: racers start small and grow as they close.
        /// The distance is solved from the aspect rather than guessed, and
        /// asserted below so a future lane change cannot silently re-crop it.
        /// </summary>
        private static void BuildCamera(float goalZ, float startZ, float laneHalfWidth)
        {
            var cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.fieldOfView = CAMERA_FOV;
            // The walk scene had no AudioListener at all, so the announcer bell
            // and crowd bed added below would have played to nobody.
            cameraObject.AddComponent<AudioListener>();

            var aim = new Vector3(0f, CAMERA_AIM_HEIGHT, (startZ + goalZ) * 0.5f);
            float back = SolveCameraSetback(goalZ, startZ, laneHalfWidth, aim);
            var position = new Vector3(0f, CAMERA_HEIGHT, goalZ + back);
            cameraObject.transform.position = position;
            cameraObject.transform.rotation = Quaternion.LookRotation(aim - position, Vector3.up);

            if (LaneCornersOutsideFrame(cameraObject.transform, position, goalZ, startZ, laneHalfWidth) > 0)
            {
                Debug.LogWarning("RigTool: walk contest camera still crops the lane at 9:16 even at the " +
                    $"maximum setback ({MAX_CAMERA_SETBACK:F1} m). Reduce LANE_SPACING or widen CAMERA_FOV.");
            }
            Debug.Log($"RigTool: walk camera {back:F2} m past the finish, framing a " +
                $"{laneHalfWidth * 2f:F2} m wide field end to end at 9:16.");
        }

        /// <summary>
        /// Smallest setback past the finish line at which every lane corner - at
        /// BOTH ends of the lane - sits inside a portrait frustum.
        ///
        /// The finish plane is what binds, not the start: racers are nearest the
        /// camera there, so that is where the frame is narrowest in world terms.
        /// The first pass at this fix framed the start line correctly and still
        /// lost the outer two racers off the sides as they arrived (viewport x
        /// 1.43 and -0.43) - exactly the moment the shot exists for.
        ///
        /// Solved by stepping rather than in closed form because the pitch
        /// depends on the setback (the camera aims at the lane centre) and the
        /// setback depends on the pitch. A 5 cm step over a bounded range is
        /// exact enough for a camera and reads better than an iteration.
        /// </summary>
        private static float SolveCameraSetback(float goalZ, float startZ, float laneHalfWidth, Vector3 aim)
        {
            var probe = new GameObject("RigToolCameraProbe").transform;
            try
            {
                for (float back = MIN_CAMERA_SETBACK; back <= MAX_CAMERA_SETBACK; back += 0.05f)
                {
                    var position = new Vector3(0f, CAMERA_HEIGHT, goalZ + back);
                    probe.SetPositionAndRotation(position, Quaternion.LookRotation(aim - position, Vector3.up));
                    if (LaneCornersOutsideFrame(probe, position, goalZ, startZ, laneHalfWidth) == 0)
                    {
                        // One step of headroom so a fighter leaning outward at the
                        // tape does not clip the edge.
                        return back + 0.4f;
                    }
                }
                return MAX_CAMERA_SETBACK;
            }
            finally
            {
                Object.DestroyImmediate(probe.gameObject);
            }
        }

        /// <summary>
        /// How many of the four lane corners (outer lanes by start/finish ends,
        /// at chest height) fall outside a 9:16 frustum for this camera pose.
        /// </summary>
        private static int LaneCornersOutsideFrame(Transform cameraTransform, Vector3 position,
            float goalZ, float startZ, float laneHalfWidth)
        {
            float halfVertical = Mathf.Tan(CAMERA_FOV * 0.5f * Mathf.Deg2Rad);
            float halfHorizontal = halfVertical * DESIGN_ASPECT;
            Vector3 forward = cameraTransform.forward;
            Vector3 right = cameraTransform.right;
            Vector3 up = cameraTransform.up;

            int outside = 0;
            for (int side = -1; side <= 1; side += 2)
            {
                for (int end = 0; end < 2; end++)
                {
                    var corner = new Vector3(
                        side * (laneHalfWidth + BODY_HALF_WIDTH), 1.2f, end == 0 ? startZ : goalZ);
                    Vector3 offset = corner - position;
                    float depth = Vector3.Dot(offset, forward);
                    if (depth <= 0.01f
                        || Mathf.Abs(Vector3.Dot(offset, right)) > depth * halfHorizontal
                        || Mathf.Abs(Vector3.Dot(offset, up)) > depth * halfVertical)
                    {
                        outside++;
                    }
                }
            }
            return outside;
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
            // Without the stylesheet every scoreboard class falls back to 14 px
            // black — the HUD is built and updating but unreadable. The balance
            // scene got this by hand; this tool has to do it or the walk scene
            // ships blind, which it did until 2026-08-20.
            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(CONTEST_STYLESHEET_PATH);
            if (styleSheet == null)
            {
                Debug.LogError($"Walk contest scene tool: no StyleSheet at {CONTEST_STYLESHEET_PATH}; " +
                    "the scoreboard will render unstyled.");
            }
            referee.EditorInitialize(Vector3.forward, goalDistance, styleSheet);

            // Both share the referee's document and find it with GetComponent,
            // so they must live on this object. The countdown freezes time until
            // GO so racers start upright instead of mid-topple; the banner is the
            // round-winner callout the walk scene never had.
            refereeObject.AddComponent<Systems_RoundCountdown>();
            refereeObject.AddComponent<Systems_WinnerBanner>();
        }

        /// <summary>
        /// The presentation stack the walk contest shipped without: announcer,
        /// crowd bed, fall impacts and — the important one — a match director.
        /// Without a director nothing ever sets HoldRestarts, so the race looped
        /// rounds forever with no champion and no way out (measured 2026-08-20:
        /// still going at round 14).
        ///
        /// These bind to Systems_ContestReferee, which Systems_WalkContest now
        /// derives from; while they each named Systems_BalanceContest outright,
        /// adding them here would have done nothing at all.
        /// </summary>
        private static void BuildPresentation(GameObject systemsRoot)
        {
            var announcerObject = new GameObject("ContestAnnouncer");
            announcerObject.transform.SetParent(systemsRoot.transform, false);
            var announcerDocument = announcerObject.AddComponent<UIDocument>();
            announcerDocument.panelSettings = RigTool_ContestScene.GetOrCreatePanelSettings();
            announcerObject.AddComponent<AudioSource>().playOnAwake = false;
            var announcer = announcerObject.AddComponent<Systems_Announcer>();
            AssignClip(announcer, "_bellClip", BELL_CLIP_PATH);

            var matchObject = new GameObject("MatchDirector");
            matchObject.transform.SetParent(systemsRoot.transform, false);
            matchObject.AddComponent<Systems_MatchDirector>();

            var fallObject = new GameObject("FallImpactFX");
            fallObject.transform.SetParent(systemsRoot.transform, false);
            var fallSource = fallObject.AddComponent<AudioSource>();
            fallSource.playOnAwake = false;
            fallSource.spatialBlend = 1f;
            var fallFx = fallObject.AddComponent<Systems_FallImpactFx>();
            var fallSo = new SerializedObject(fallFx);
            fallSo.FindProperty("_audioSource").objectReferenceValue = fallSource;
            SerializedProperty thuds = fallSo.FindProperty("_thudClips");
            thuds.arraySize = ThudClipPaths.Length;
            for (int clipIndex = 0; clipIndex < ThudClipPaths.Length; clipIndex++)
            {
                thuds.GetArrayElementAtIndex(clipIndex).objectReferenceValue =
                    LoadClip(ThudClipPaths[clipIndex]);
            }
            fallSo.ApplyModifiedPropertiesWithoutUndo();

            var crowdObject = new GameObject("CrowdAudio");
            crowdObject.transform.SetParent(systemsRoot.transform, false);
            crowdObject.AddComponent<AudioSource>().playOnAwake = false;
            var crowd = crowdObject.AddComponent<Systems_CrowdAudio>();
            var crowdSo = new SerializedObject(crowd);
            // _contest is a serialized reference and the referee is a sibling
            // built moments ago, so it can be wired at author time rather than
            // discovered; _fallFx likewise.
            crowdSo.FindProperty("_contest").objectReferenceValue =
                systemsRoot.GetComponentInChildren<Systems_WalkContest>(true);
            crowdSo.FindProperty("_fallFx").objectReferenceValue = fallFx;
            crowdSo.FindProperty("_ambienceLoop").objectReferenceValue = LoadClip(CROWD_AMBIENCE_PATH);
            crowdSo.FindProperty("_cheer").objectReferenceValue = LoadClip(CROWD_CHEER_PATH);
            crowdSo.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>Version stamp and FPS readout, on a document that outlives the race.</summary>
        private static void BuildHudChrome()
        {
            var chromeObject = new GameObject("HudChrome");
            var document = chromeObject.AddComponent<UIDocument>();
            document.panelSettings = RigTool_ContestScene.GetOrCreatePanelSettings();
            chromeObject.AddComponent<Systems_VersionStamp>();
            chromeObject.AddComponent<Systems_FpsCounter>();
        }

        private static AudioClip LoadClip(string path)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (clip == null)
            {
                Debug.LogWarning($"RigTool: walk contest audio missing at {path} — that cue will be silent.");
            }
            return clip;
        }

        private static void AssignClip(Object target, string propertyName, string path)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(propertyName).objectReferenceValue = LoadClip(path);
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
