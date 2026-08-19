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
        private const float SPAWN_HEIGHT = 0.03f;

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

        public ContestRosterEntry[] Roster => _roster;
        public int SlotCount => SlotPositions.Length;

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
            var nameCounts = new int[_roster.Length];
            for (int slotIndex = 0; slotIndex < SlotPositions.Length && slotIndex < slotRosterIndices.Length; slotIndex++)
            {
                int rosterIndex = slotRosterIndices[slotIndex];
                if (rosterIndex < 0 || rosterIndex >= _roster.Length)
                {
                    continue;
                }
                ContestRosterEntry entry = _roster[rosterIndex];
                var instance = Instantiate(entry.prefab, SlotPositions[slotIndex], Quaternion.identity);
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
                var instance = Instantiate(_roster[0].prefab, SlotPositions[0], Quaternion.identity);
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
