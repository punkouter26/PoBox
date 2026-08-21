using System;
using System.Collections.Generic;
using Unity.InferenceEngine;
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
        private const string OBSERVATION_INPUT_NAME = "obs_0";
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private const int JOINT_INDEX_SHIN_L = 3;
        private const int JOINT_INDEX_SHIN_R = 9;
        /// <summary>
        /// World Y of the ring canvas the fighters stand on. Assets/Art/BoxingRing.glb
        /// carries its canvas 1 m above the model origin, so a ring placed at y = 0
        /// puts its floor here and reads as the raised ring a real bout is fought in.
        /// Ground colliders, spawns and cameras all follow this number. Safe to change
        /// only because height observations are ground-relative (Systems_FighterRig.GroundY).
        /// </summary>
        public const float RING_FLOOR_Y = 1f;

        private const float SPAWN_HEIGHT = RING_FLOOR_Y + 0.03f;

        // 8 slots: two rows of four inside the 6.1 m canvas. Front row is on
        // the camera side (+Z) so single spawns stay filmable.
        private static readonly Vector3[] SlotPositions =
        {
            new(-2.1f, SPAWN_HEIGHT, 1f), new(-0.7f, SPAWN_HEIGHT, 1f),
            new(0.7f, SPAWN_HEIGHT, 1f), new(2.1f, SPAWN_HEIGHT, 1f),
            new(-2.1f, SPAWN_HEIGHT, -1f), new(-0.7f, SPAWN_HEIGHT, -1f),
            new(0.7f, SPAWN_HEIGHT, -1f), new(2.1f, SPAWN_HEIGHT, -1f)
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
                        nameCounts[rosterIndex] - 1);
                    spawned++;
                }
                if (spawned == 0 && _roster.Length > 0)
                {
                    // Never start an empty ring — fall back to one default fighter.
                    Spawn(_roster[0], holder.transform, slots[0], spawnRotation,
                        $"Contest_{_roster[0].displayName}", 0);
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
        /// Instantiates one fighter under <paramref name="holder"/> — which the caller
        /// keeps inactive — configures it, then reparents it to the scene root. That
        /// last step is what activates it, so Awake and OnEnable run against the
        /// finished BrainParameters rather than the prefab's.
        /// </summary>
        private static void Spawn(ContestRosterEntry entry, Transform holder,
            Vector3 position, Quaternion rotation, string instanceName, int copyIndex)
        {
            var instance = Instantiate(entry.prefab, holder);
            instance.name = instanceName;
            instance.transform.SetPositionAndRotation(position, rotation);
            Configure(instance, entry, copyIndex);
            instance.transform.SetParent(null, worldPositionStays: true);
        }

        private static void Configure(GameObject instance, ContestRosterEntry entry, int copyIndex)
        {
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
            TintCopy(instance, copyIndex);
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

        private static void TintCopy(GameObject instance, int copyIndex)
        {
            if (copyIndex <= 0)
            {
                return;
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
        }

        // Cached: a contest spawns up to eight fighters off the same two or three
        // ModelAssets, and deserializing one is not free.
        private static readonly Dictionary<ModelAsset, int> ModelObservationWidths = new();

        /// <summary>
        /// True when <paramref name="entry"/>'s brain was trained on the same
        /// observation width the fighter emits, or when the width cannot be read.
        /// Logs and returns false otherwise.
        ///
        /// ML-Agents runs this comparison only from the BehaviorParameters
        /// inspector; its runtime path checks the model version and nothing else,
        /// so a brain assigned from code — which is every brain in a contest —
        /// mismatches in total silence. Measured 2026-08-20: the balance roster ran
        /// 119-observation brains on 121-observation fighters without producing a
        /// single console line.
        ///
        /// Deliberately NOT [Conditional]: this decides whether the brain runs at
        /// all, so a player build has to make the same call an editor run does.
        /// The width is cached per ModelAsset, so a full ring costs one deserialize
        /// per distinct brain.
        /// </summary>
        private static bool AcceptBrain(ContestRosterEntry entry, string instanceName, int sensorSize)
        {
            int modelSize = ModelObservationWidth(entry.model);
            if (modelSize < 0 || modelSize == sensorSize)
            {
                return true;
            }
            Debug.LogError($"{instanceName}: brain '{entry.model.name}' expects {modelSize} observations but " +
                $"this fighter emits {sensorSize}, so it would read a shifted vector. Falling back to the " +
                "heuristic bot — export a brain trained on this layout, or point the roster entry at one " +
                "that matches.");
            return false;
        }

        /// <summary>Width of the obs_0 input of <paramref name="modelAsset"/>, or -1 when unreadable.</summary>
        private static int ModelObservationWidth(ModelAsset modelAsset)
        {
            if (ModelObservationWidths.TryGetValue(modelAsset, out int cached))
            {
                return cached;
            }
            int width = -1;
            Model model = ModelLoader.Load(modelAsset);
            for (int inputIndex = 0; inputIndex < model.inputs.Count; inputIndex++)
            {
                Model.Input input = model.inputs[inputIndex];
                if (input.name != OBSERVATION_INPUT_NAME || input.shape.isRankDynamic || input.shape.rank != 2)
                {
                    continue;
                }
                width = input.shape.Get(1);
                break;
            }
            ModelObservationWidths[modelAsset] = width;
            return width;
        }
    }
}
