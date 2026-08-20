using System;
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
            for (int slotIndex = 0; slotIndex < slots.Length && slotIndex < slotRosterIndices.Length; slotIndex++)
            {
                int rosterIndex = slotRosterIndices[slotIndex];
                if (rosterIndex < 0 || rosterIndex >= _roster.Length)
                {
                    continue;
                }
                ContestRosterEntry entry = _roster[rosterIndex];
                var instance = Instantiate(entry.prefab, slots[slotIndex], spawnRotation);
                nameCounts[rosterIndex]++;
                instance.name = nameCounts[rosterIndex] > 1
                    ? $"Contest_{entry.displayName}{nameCounts[rosterIndex]}"
                    : $"Contest_{entry.displayName}";
                Configure(instance, entry);
                spawned++;
            }
            if (spawned == 0 && _roster.Length > 0)
            {
                // Never start an empty ring — fall back to one default fighter.
                var instance = Instantiate(_roster[0].prefab, slots[0], spawnRotation);
                instance.name = $"Contest_{_roster[0].displayName}";
                Configure(instance, _roster[0]);
            }

            _systemsRoot.SetActive(true);
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

        private static void Configure(GameObject instance, ContestRosterEntry entry)
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
                // Must happen before the agent's Initialize reads the layout.
                agent.SetObserveLocomotionCommand(true);
                behavior.BrainParameters.VectorObservationSize =
                    Agent_FighterBoxing.ComputeObservationCount(rig.JointCount,
                        observeOpponent: false, observeFootHeight: true, observeLocomotionCommand: true);
            }
            if (entry.forceHeuristic || entry.model == null)
            {
                behavior.BehaviorType = BehaviorType.HeuristicOnly;
            }
            else
            {
                behavior.Model = entry.model;
                behavior.BehaviorType = BehaviorType.InferenceOnly;
            }

            if (entry.tint != null)
            {
                var renderers = instance.GetComponentsInChildren<Renderer>(true);
                for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
                {
                    renderers[rendererIndex].sharedMaterial = entry.tint;
                }
            }
        }
    }
}
