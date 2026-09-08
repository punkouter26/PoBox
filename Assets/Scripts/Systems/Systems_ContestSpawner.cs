using System;
using System.Collections.Generic;
using Unity.InferenceEngine;
using Unity.MLAgents;
using Unity.MLAgents.Policies;
using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// One selectable fighter kind for the contest setup menu: prefab plus its
    /// brain (null or forceHeuristic = code-driven PD bot) and an optional
    /// tint that marks it in the ring.
    /// </summary>
    [Serializable]
    public sealed class ContestRosterEntry
    {
        public string displayName;
        public GameObject prefab;
        public ModelAsset model;
        public bool forceHeuristic;
        public Material tint;
        // True when `model` belongs to the locomotion model line, which adds
        // the commanded-speed observations. Those brains will not load onto a
        // fighter left in the older layout — the observation vector is a
        // different size — so the spawner must switch the flag on before the
        // agent initializes.
        public bool locomotionBrain;
    }

    /// <summary>
    /// Spawns the fighters chosen in the setup menu into the contest slots,
    /// then wakes the sleeping contest systems root (referee, FX, cube
    /// thrower) and the drama camera — those discover fighters in their own
    /// Start. Test-scene harness only.
    /// </summary>
    public sealed class Systems_ContestSpawner : MonoBehaviour
    {
        // ML-Agents names the single vector-observation input of an exported brain
        // obs_0; the contest rigs have exactly one, so this is the tensor to measure.
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        /// <summary>
        /// World Y of the ring canvas the fighters stand on. Assets/Art/BoxingRing.glb
        /// carries its canvas 1 m above the model origin, so a ring placed at y = 0
        /// puts its floor here and reads as the raised ring a real bout is fought in.
        /// Ground colliders, spawns and cameras all follow this number. Safe to change
        /// only because height observations are ground-relative (Systems_FighterRig.GroundY).
        /// </summary>
        public const float RING_FLOOR_Y = 1f;

        /// <summary>Decision interval every shipped brain was trained at.</summary>
        private const float TRAINED_DECISION_SECONDS = 0.02f;

        private const float SPAWN_HEIGHT = RING_FLOOR_Y + 0.03f;

        /// <summary>
        /// 8 slots inside the 6.1 m canvas: four ranks of two, running AWAY from
        /// the camera. Nearest rank first, so a part-filled ring fills from the
        /// front and a single spawn stays filmable.
        ///
        /// It was the other way round — two ranks of four — and that layout is
        /// what made the balance contest unwatchable on a phone, for a reason no
        /// amount of camera tuning could fix. A camera has to frame the whole
        /// field, a portrait 9:16 frame is 1.78x taller than it is wide, and the
        /// vertical span the shot ends up with is therefore the horizontal span
        /// it needs DIVIDED BY 0.5625. Four fighters abreast are 4.2 m wide, so
        /// the shot was always going to be 9.2 m tall whatever the FOV or the
        /// distance, and a 1.7 m fighter is 18% of that. Measured 2026-08-22:
        /// the ring came back a quarter of the frame with the arena's unlit
        /// ceiling filling the top third.
        ///
        /// Turned through 90 degrees the field is 1.5 m wide and 4.2 m deep. The
        /// frame it needs is now barely wider than one fighter, and the depth
        /// does not cost frame width at all — perspective spreads the four ranks
        /// UP the picture, which is the one axis a portrait frame has to spare.
        /// The near rank comes out about a third of the frame tall against the
        /// old 18%, and the ranks behind it recede instead of lining up.
        /// </summary>
        private static readonly Vector3[] SlotPositions =
        {
            new(-0.75f, SPAWN_HEIGHT, 2.1f), new(0.75f, SPAWN_HEIGHT, 2.1f),
            new(-0.75f, SPAWN_HEIGHT, 0.7f), new(0.75f, SPAWN_HEIGHT, 0.7f),
            new(-0.75f, SPAWN_HEIGHT, -0.7f), new(0.75f, SPAWN_HEIGHT, -0.7f),
            new(-0.75f, SPAWN_HEIGHT, -2.1f), new(0.75f, SPAWN_HEIGHT, -2.1f)
        };

        [SerializeField] private ContestRosterEntry[] _roster;
        [SerializeField] private GameObject _systemsRoot;
        [SerializeField] private Systems_DramaCamera _dramaCamera;
        [SerializeField] private Systems_MenuOrbitCamera _menuOrbit;
        // Overrides the two-row ring layout above. Left empty by the balance
        // contest so scenes built before this field deserialize unchanged; the
        // walk contest sets a 4-wide start line.
        [SerializeField] private Vector3[] _slotPositionsOverride = System.Array.Empty<Vector3>();
        // Facing for spawned fighters. Identity faces +Z, which is the walk
        // race's direction of travel and the balance ring's camera side.
        [SerializeField] private Vector3 _spawnEuler = Vector3.zero;

        public ContestRosterEntry[] Roster => _roster;
        public int SlotCount => ActiveSlots.Length;

        /// <summary>Where slot <paramref name="index"/> stands, for author-time placement.</summary>
        public Vector3 SlotPosition(int index)
        {
            Vector3[] slots = ActiveSlots;
            return slots[Mathf.Clamp(index, 0, slots.Length - 1)];
        }

        /// <summary>Facing every fighter starts a round in.</summary>
        public Quaternion SpawnRotation => Quaternion.Euler(_spawnEuler);

        private Vector3[] ActiveSlots =>
            _slotPositionsOverride != null && _slotPositionsOverride.Length > 0
                ? _slotPositionsOverride
                : SlotPositions;

        // Called by the editor scene tool.
        public void EditorSetSlots(Vector3[] slotPositions, Vector3 spawnEuler)
        {
            _slotPositionsOverride = slotPositions;
            _spawnEuler = spawnEuler;
        }

        // Called by the editor scene tool.
        public void EditorInitialize(ContestRosterEntry[] roster, GameObject systemsRoot, Systems_DramaCamera dramaCamera)
        {
            _roster = roster;
            _systemsRoot = systemsRoot;
            _dramaCamera = dramaCamera;
        }

        /// <summary>
        /// Contest scenes must never open a trainer connection.
        ///
        /// The Academy initialises lazily the first time an Agent wakes, and it
        /// tries port 5004 when it does, logging "Couldn't connect to trainer on
        /// port 5004 ... Will perform inference instead" in every contest.
        /// Harmless-looking, and it is not: a contest scene played while
        /// mlagents-learn is listening CONNECTS to it and hands the trainer an
        /// environment with no trainable behaviours, which is why training has
        /// had to be stopped by hand for every in-scene test. In a shipped build
        /// it is a pointless socket attempt on startup.
        ///
        /// Awake is early enough because no fighter exists yet -- SpawnAndBegin
        /// is what creates them, so the Academy has had nothing to initialise
        /// for.
        /// </summary>
        private void Awake()
        {
            Unity.MLAgents.CommunicatorFactory.Enabled = false;

            // HERE AS WELL AS IN SpawnAndBegin, BECAUSE HALF THE CONTEST SCENES
            // NEVER SPAWN ANYTHING. `Tools/ML Boxing/15` places the roster into
            // the scene at author time and disables Systems_MiniGameLauncher, so
            // SCN_TEST_BALANCE_CONTEST ships with five Contest_* fighters and
            // Nick already in it and SpawnAndBegin is never called. Hooking only
            // the spawn path meant the spectator systems were silently absent
            // from exactly the scene that ships — the contest ran, the HUD and
            // referee worked, and nothing announced that three systems had never
            // been created.
            //
            // Both call sites are needed and neither is redundant. In an
            // author-placed scene the systems root is already active, so
            // components added now get their Start next frame with the fighters
            // already in the scene. In a spawned scene the root is still asleep
            // here and the fighters do not exist yet, so this adds them and the
            // call in SpawnAndBegin is the no-op — but that call is what
            // guarantees the ordering when the root reference is only resolved
            // by then. EnsureSpectatorSystems is idempotent, so running twice
            // costs one GetComponentInChildren each.
            EnsureSpectatorSystems(_systemsRoot);
        }

        /// <summary>Spawns one fighter per slot (roster index, -1 = empty slot), then starts the contest.</summary>
        public void SpawnAndBegin(int[] slotRosterIndices)
        {
            int spawned = 0;
            Vector3[] slots = ActiveSlots;
            Quaternion spawnRotation = Quaternion.Euler(_spawnEuler);
            var nameCounts = new int[_roster.Length];
            // Fighters are built under an INACTIVE holder so Awake and OnEnable are
            // deferred until Configure has run. Instantiating straight into the scene
            // runs the Agent's LazyInitialize on the spot, which snapshots
            // BrainParameters and builds the VectorSensor from it — so the locomotion
            // observation-size fix in Configure arrived one frame too late and every
            // step logged "More observations (127) made than vector observation size
            // (121). The observations will be truncated." Measured 2026-08-20: 44,429
            // of them in one editor session, with the walking brain reading 6 junk
            // inputs. A GameObject parented to an inactive object never fires Awake,
            // so releasing it afterwards is what finally starts the agent.
            var holder = new GameObject("ContestSpawnHolder");
            holder.SetActive(false);
            try
            {
                for (int slotIndex = 0; slotIndex < slots.Length && slotIndex < slotRosterIndices.Length; slotIndex++)
                {
                    int rosterIndex = slotRosterIndices[slotIndex];
                    if (rosterIndex < 0 || rosterIndex >= _roster.Length)
                    {
                        continue;
                    }
                    ContestRosterEntry entry = _roster[rosterIndex];
                    nameCounts[rosterIndex]++;
                    string instanceName = nameCounts[rosterIndex] > 1
                        ? $"Contest_{entry.displayName}{nameCounts[rosterIndex]}"
                        : $"Contest_{entry.displayName}";
                    Spawn(entry, holder.transform, slots[slotIndex], spawnRotation, instanceName,
                        nameCounts[rosterIndex] - 1, rosterIndex);
                    spawned++;
                }
                if (spawned == 0 && _roster.Length > 0)
                {
                    // Never start an empty ring — fall back to one default fighter.
                    Spawn(_roster[0], holder.transform, slots[0], spawnRotation,
                        $"Contest_{_roster[0].displayName}", 0, 0);
                }
            }
            finally
            {
                // Without this a throw inside Configure strands the inactive holder
                // — and the half-built fighter inside it — in the scene for good.
                Destroy(holder);
            }

            if (_systemsRoot != null)
            {
                // Added while the root is still asleep, so their Start runs when
                // it wakes — with a full ring to discover, exactly like the
                // announcer and the hazard director beside them.
                EnsureSpectatorSystems(_systemsRoot);
                _systemsRoot.SetActive(true);
            }
            if (_menuOrbit != null)
            {
                _menuOrbit.enabled = false;
            }
            if (_dramaCamera != null)
            {
                _dramaCamera.enabled = true;
            }
        }

        // Called by the editor scene tool.
        public void EditorSetMenuOrbit(Systems_MenuOrbitCamera menuOrbit)
        {
            _menuOrbit = menuOrbit;
        }

        /// <summary>
        /// Attaches the spectator systems that carry no scene state of their
        /// own: the joint-stress heatmap, the impulse-scaled impact FX and the
        /// pre-round tale of the tape.
        ///
        /// WIRED AT RUNTIME RATHER THAN PLACED IN THE SCENE, ON PURPOSE. The
        /// contest scenes are generated artifacts and the tool that generates
        /// this one is the documented way to destroy it — `BuildAll` opens an
        /// empty scene and never re-adds the spawner, so six of its nine steps
        /// bail while it logs success anyway (CLAUDE.md). Anything that has to
        /// be dragged into SCN_TEST_BALANCE_CONTEST by hand is therefore one
        /// regeneration away from being silently absent, with a scene that still
        /// runs and simply shows less. These three need no serialized
        /// references — they discover fighters themselves and load their assets
        /// from <see cref="Systems_SpectatorKit"/> — so there is nothing to be
        /// gained by putting them in the scene and a whole failure mode to be
        /// avoided by not.
        ///
        /// Idempotent: adding a second copy would double every thud and every
        /// glow, and this runs once per contest start.
        /// </summary>
        private static void EnsureSpectatorSystems(GameObject systemsRoot)
        {
            if (systemsRoot == null)
            {
                // Worth a line rather than a silent return: with no systems root
                // the entire spectator layer is absent, and its absence looks
                // exactly like it working badly.
                Debug.LogWarning("Systems_ContestSpawner: no systems root — joint stress, impact FX " +
                                 "and the tale of the tape will not be attached.");
                return;
            }
            int added = 0;
            if (systemsRoot.GetComponentInChildren<Systems_ImpactFx>(true) == null)
            {
                AddSpectatorSystem<Systems_ImpactFx>(systemsRoot, "ImpactFx");
                added++;
            }
            if (systemsRoot.GetComponentInChildren<Systems_JointStressView>(true) == null)
            {
                AddSpectatorSystem<Systems_JointStressView>(systemsRoot, "JointStressView");
                added++;
            }
            if (systemsRoot.GetComponentInChildren<Systems_TaleOfTheTape>(true) == null)
            {
                AddSpectatorSystem<Systems_TaleOfTheTape>(systemsRoot, "TaleOfTheTape");
                added++;
            }
            if (added > 0)
            {
                // One line, once per contest. These systems are created rather
                // than placed, so this is the only way to tell from a log
                // whether they exist at all — which is the question that took a
                // play session to answer the first time.
                Debug.Log($"Systems_ContestSpawner: attached {added} spectator system(s) to " +
                          $"{systemsRoot.name}.");
            }
        }

        private static void AddSpectatorSystem<T>(GameObject systemsRoot, string objectName)
            where T : Component
        {
            var host = new GameObject(objectName);
            host.transform.SetParent(systemsRoot.transform, false);
            host.AddComponent<T>();
        }

        /// <summary>
        /// Instantiates one fighter under <paramref name="holder"/> — which the caller
        /// keeps inactive — configures it, then reparents it to the scene root. That
        /// last step is what activates it, so Awake and OnEnable run against the
        /// finished BrainParameters rather than the prefab's.
        /// </summary>
        private static void Spawn(ContestRosterEntry entry, Transform holder,
            Vector3 position, Quaternion rotation, string instanceName, int copyIndex, int rosterIndex)
        {
            var instance = Instantiate(entry.prefab, holder);
            instance.name = instanceName;
            instance.transform.SetPositionAndRotation(position, rotation);
            Configure(instance, entry, copyIndex, rosterIndex);
            instance.transform.SetParent(null, worldPositionStays: true);
        }

        /// <summary>
        /// PUBLIC so the editor tool that places the roster at author time
        /// calls this exact code rather than a second copy of it. The
        /// observation size is set here, and a scene whose fighters were
        /// configured by a divergent copy would mis-size its sensors in total
        /// silence -- see the observation-size contract in CLAUDE.md.
        /// Safe to call from the Editor: components do not Awake until play
        /// begins, so anything set here is serialized before the sensor exists.
        /// </summary>
        public static void Configure(GameObject instance, ContestRosterEntry entry, int copyIndex,
            int rosterIndex)
        {
            var rig = instance.GetComponent<Systems_FighterRig>();
            var agent = instance.GetComponent<Agent_FighterBoxing>();
            var stamina = instance.GetComponent<Systems_Stamina>();
            agent.MaxStep = 0; // the referee owns the round lifecycle
            stamina.enabled = false;

            // Fall sensors by role, not by joint index: the raptor's joint
            // list is a different shape than the humanoids', and gloves exist
            // only on rigs with arms. Shins are found by name — the same
            // convention Reward_Balance and the heuristic bot already rely on.
            rig.Torso.gameObject.AddComponent<Sensor_GroundContact>();
            rig.Head.gameObject.AddComponent<Sensor_GroundContact>();
            for (int jointIndex = 0; jointIndex < rig.Joints.Count; jointIndex++)
            {
                var jointBody = rig.Joints[jointIndex].body;
                if (jointBody != null &&
                    jointBody.name.IndexOf("shin", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    jointBody.gameObject.AddComponent<Sensor_GroundContact>();
                }
            }
            if (rig.GloveLeft != null)
            {
                rig.GloveLeft.gameObject.AddComponent<Sensor_GroundContact>();
            }
            if (rig.GloveRight != null)
            {
                rig.GloveRight.gameObject.AddComponent<Sensor_GroundContact>();
            }

            // DECISION CADENCE IS THE CONTRACT; THE PHYSICS STEP IS NOT.
            // Every brain in this roster was trained deciding once per 0.02 s
            // with DecisionPeriod 1. Sharing a scene with a MuJoCo creature
            // costs a finer step -- CreatureSentisController pins
            // Time.fixedDeltaTime for the whole scene -- and at 0.005 s a
            // DecisionPeriod of 1 drives these policies FOUR TIMES too fast.
            // Holding each action for proportionally more physics steps is
            // exactly MuJoCo's decimation under another name, and it keeps the
            // 50 Hz the policy learned. The gait clock needs no help: it is
            // StepCount * Time.fixedDeltaTime, i.e. elapsed seconds already.
            var requester = instance.GetComponent<DecisionRequester>();
            if (requester != null)
            {
                requester.DecisionPeriod = Mathf.Max(1, Mathf.RoundToInt(
                    TRAINED_DECISION_SECONDS / Mathf.Max(1e-5f, Time.fixedDeltaTime)));
            }

            var behavior = instance.GetComponent<BehaviorParameters>();
            if (entry.locomotionBrain)
            {
                agent.SetObserveLocomotionCommand(true);
            }
            // Size the sensor from what this agent will actually emit, on every
            // fighter rather than only the locomotion ones. Restating the flags here
            // is what let the walk contest ship a 121-wide sensor to a 127-observation
            // agent; asking the agent removes the chance to disagree. Correct at this
            // point only because Spawn keeps the instance inactive until Configure
            // returns, so the sensor has not been built from this value yet.
            behavior.BrainParameters.VectorObservationSize = agent.ExpectedObservationCount;

            int sensorSize = behavior.BrainParameters.VectorObservationSize;
            if (entry.forceHeuristic || entry.model == null)
            {
                behavior.BehaviorType = BehaviorType.HeuristicOnly;
            }
            else if (AcceptBrain(entry, instance.name, sensorSize))
            {
                behavior.Model = entry.model;
                behavior.BehaviorType = BehaviorType.InferenceOnly;
            }
            else
            {
                // Refused, not merely reported. A brain whose obs_0 is a
                // different width than this fighter emits reads a vector that
                // is shifted from the first differing observation onward, so
                // every number after it means something else than it did in
                // training. The heuristic PD bot is a worse fighter but an
                // honest one, and it is the project's mandated fallback.
                behavior.BehaviorType = BehaviorType.HeuristicOnly;
            }

            if (entry.tint != null)
            {
                var renderers = instance.GetComponentsInChildren<Renderer>(true);
                for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
                {
                    renderers[rendererIndex].sharedMaterial = entry.tint;
                }
            }
            Color wash = TintCopy(instance, copyIndex);

            // Recorded here because this is the only code that knows all three
            // inputs: which roster entry the fighter came from, what tint that
            // entry carried, and which copy wash the duplicate got. Everything
            // downstream — scoreboard plates, the match tally, the drama
            // camera's winner focus — reads it back off the instance instead of
            // guessing from the GameObject name.
            instance.AddComponent<Systems_FighterIdentity>().Initialize(
                instance.name.Replace("Contest_", ""),
                IdentityColor(entry, rosterIndex) * wash,
                rosterIndex);
        }

        // Fallback swatches, used for roster entries with no tint material.
        // Grandma and Grandpa are the reason this exists: they wear their own
        // textures and are deliberately left untinted, so there is no material
        // to read a colour off and the scoreboard would otherwise show them
        // both as plain white.
        private static readonly Color[] RosterSwatches =
        {
            new(0.35f, 0.55f, 1f),   // 0
            new(1f, 0.45f, 0.72f),   // 1
            new(0.45f, 0.85f, 0.55f),// 2
            new(1f, 0.35f, 0.30f)    // 3
        };

        /// <summary>
        /// The colour that identifies this fighter on the scoreboard: its tint
        /// material's albedo where it has one, otherwise a swatch from the
        /// palette above.
        /// </summary>
        private static Color IdentityColor(ContestRosterEntry entry, int rosterIndex)
        {
            if (entry.tint != null && entry.tint.HasProperty(BaseColorId))
            {
                Color tint = entry.tint.GetColor(BaseColorId);
                tint.a = 1f;
                return tint;
            }
            return RosterSwatches[rosterIndex % RosterSwatches.Length];
        }

        // An eight-slot ring is filled from a four-entry roster, so it normally
        // holds two of each kind wearing exactly the same material — the
        // scoreboard called them Grandma and Grandma2 while the ring showed no
        // way to tell which was which. The first of each kind is left exactly as
        // authored and only the copies are washed, through a
        // MaterialPropertyBlock: URP's _BaseColor multiplies the albedo, so a
        // pale wash still reads over Grandma's and Grandpa's textures, and no
        // material is instantiated and no shader looked up at runtime — a
        // Shader.Find material here would strip out of the Android build.
        private static readonly Color[] CopyWashes =
        {
            new(0.62f, 0.78f, 1f),   // copy 2: cool
            new(1f, 0.80f, 0.55f),   // copy 3: warm
            new(0.70f, 1f, 0.72f)    // copy 4: green
        };

        /// <summary>Applies the copy wash and returns it, so the identity swatch can match the body.</summary>
        private static Color TintCopy(GameObject instance, int copyIndex)
        {
            if (copyIndex <= 0)
            {
                return Color.white;
            }
            Color wash = CopyWashes[(copyIndex - 1) % CopyWashes.Length];
            var block = new MaterialPropertyBlock();
            var renderers = instance.GetComponentsInChildren<Renderer>(true);
            for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
            {
                Renderer renderer = renderers[rendererIndex];
                Material material = renderer.sharedMaterial;
                if (material == null)
                {
                    continue;
                }
                // Multiply rather than replace, so a material that already
                // carries a colour keeps it.
                Color baseColor = material.HasProperty(BaseColorId)
                    ? material.GetColor(BaseColorId)
                    : Color.white;
                renderer.GetPropertyBlock(block);
                block.SetColor(BaseColorId, baseColor * wash);
                renderer.SetPropertyBlock(block);
            }
            return wash;
        }

        /// <summary>
        /// True when <paramref name="entry"/>'s brain reads the vector this
        /// fighter emits. The check itself lives in
        /// <see cref="Systems_BrainCompatibility"/>, so the offline evaluation
        /// harness refuses exactly the brains this spawner refuses.
        /// </summary>
        private static bool AcceptBrain(ContestRosterEntry entry, string instanceName, int sensorSize)
        {
            return Systems_BrainCompatibility.Accept(entry.model, instanceName, sensorSize);
        }
    }
}
