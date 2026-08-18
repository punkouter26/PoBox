using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Active-ragdoll boxing agent. Observations and action count are derived
    /// from the rig — never hand-typed (see ComputeObservationCount).
    /// Actions are buffered in OnActionReceived and applied in FixedUpdate
    /// only (project rule). Rewards are added externally by Reward_ components
    /// in the training phase.
    /// </summary>
    [DefaultExecutionOrder(-100)] // deterministically before the ML-Agents Academy stepper
    public sealed class Agent_FighterBoxing : Agent
    {
        private const float ANGULAR_VELOCITY_SCALE = 20f;
        private const int ROOT_OBSERVATIONS = 13;
        private const int PER_JOINT_OBSERVATIONS = 7;
        private const int FOOT_OBSERVATIONS = 8;
        private const int OPPONENT_OBSERVATIONS = 19;

        // Heuristic balance bot (project rule: every app has one code-driven
        // heuristic bot). Ankle strategy + counter hip strategy, PD on the
        // horizontal center-of-mass offset over the feet. DOF roles are
        // precomputed in Initialize — zero per-step allocations.
        private const float HEURISTIC_MAX_ACTION = 0.6f;
        private const byte DOF_ROLE_NONE = 0;
        private const byte DOF_ROLE_ANKLE_PITCH = 1;
        private const byte DOF_ROLE_ANKLE_ROLL = 2;
        private const byte DOF_ROLE_HIP_PITCH = 3;
        private const byte DOF_ROLE_HIP_ROLL = 4;

        [SerializeField] private Systems_FighterRig _rig;
        [SerializeField] private Systems_Stamina _stamina;
        [SerializeField] private Sensor_GroundContact _footLeft;
        [SerializeField] private Sensor_GroundContact _footRight;
        [SerializeField] private Systems_FighterRig _opponentRig;
        [SerializeField] private Systems_Stamina _opponentStamina;
        [SerializeField] private Transform _ringCenter;
        // False in balance-phase prefabs/scenes: drops the 19 opponent-relative
        // observations that are always zero without an opponent. Flip to true
        // (and re-run Prepare for Training) for the boxing phase.
        [SerializeField] private bool _observeOpponent = true;
        // Negative by calibration (2026-08-17 contest-scene A/B): with
        // _invertTargetRotation fixed, negative gains stabilize the capsule
        // and Grandma rigs; positive gains actively topple them. Grandpa's
        // bone axes differ — tune per-rig if his bot underperforms.
        [SerializeField] private float _heuristicKp = -4f;
        [SerializeField] private float _heuristicKd = -1.2f;

        private float[] _pendingActions;
        private bool _hasPendingActions;
        private Sensor_GroundContact[] _contactSensors;
        private byte[] _dofRoles;

        public Systems_FighterRig Rig => _rig;

        public static int ComputeObservationCount(int jointCount, bool observeOpponent)
        {
            int count = ROOT_OBSERVATIONS + PER_JOINT_OBSERVATIONS * jointCount + FOOT_OBSERVATIONS;
            return observeOpponent ? count + OPPONENT_OBSERVATIONS : count;
        }

        // Called by the editor rig tool while building the prefab.
        public void EditorInitialize(Systems_FighterRig rig, Systems_Stamina stamina, Sensor_GroundContact footLeft, Sensor_GroundContact footRight)
        {
            _rig = rig;
            _stamina = stamina;
            _footLeft = footLeft;
            _footRight = footRight;
        }

        public void SetOpponent(Systems_FighterRig opponentRig, Systems_Stamina opponentStamina)
        {
            _opponentRig = opponentRig;
            _opponentStamina = opponentStamina;
        }

        public override void Initialize()
        {
            _pendingActions = new float[_rig.DofCount];
            _contactSensors = GetComponentsInChildren<Sensor_GroundContact>(true);
            BuildDofRoles();
        }

        private void BuildDofRoles()
        {
            _dofRoles = new byte[_rig.DofCount];
            var joints = _rig.Joints;
            int dofIndex = 0;
            for (int jointIndex = 0; jointIndex < joints.Count; jointIndex++)
            {
                RigJointEntry entry = joints[jointIndex];
                string bodyName = entry.body.name;
                bool isAnkle = bodyName.IndexOf("foot", System.StringComparison.OrdinalIgnoreCase) >= 0;
                bool isHip = bodyName.IndexOf("thigh", System.StringComparison.OrdinalIgnoreCase) >= 0;
                if (entry.hasPitch)
                {
                    _dofRoles[dofIndex] = isAnkle ? DOF_ROLE_ANKLE_PITCH : isHip ? DOF_ROLE_HIP_PITCH : DOF_ROLE_NONE;
                    dofIndex++;
                }
                if (entry.hasRoll)
                {
                    _dofRoles[dofIndex] = isAnkle ? DOF_ROLE_ANKLE_ROLL : isHip ? DOF_ROLE_HIP_ROLL : DOF_ROLE_NONE;
                    dofIndex++;
                }
                if (entry.hasYaw)
                {
                    _dofRoles[dofIndex] = DOF_ROLE_NONE;
                    dofIndex++;
                }
            }
        }

        public override void OnEpisodeBegin()
        {
            // Rule: clear fatigue BEFORE restoring motors.
            if (_stamina != null)
            {
                _stamina.ResetFatigue();
            }
            _rig.ResetToStartPose();
            for (int sensorIndex = 0; sensorIndex < _contactSensors.Length; sensorIndex++)
            {
                _contactSensors[sensorIndex].ResetContacts();
            }
            _hasPendingActions = false;
        }

        public override void CollectObservations(VectorSensor sensor)
        {
            Rigidbody pelvis = _rig.Pelvis;
            Transform pelvisTransform = pelvis.transform;

            // Root state (13)
            sensor.AddObservation(pelvisTransform.position.y);
            sensor.AddObservation(pelvisTransform.InverseTransformDirection(pelvis.linearVelocity));
            sensor.AddObservation(pelvisTransform.InverseTransformDirection(pelvis.angularVelocity) / ANGULAR_VELOCITY_SCALE);
            sensor.AddObservation(pelvisTransform.up);
            sensor.AddObservation(pelvisTransform.forward);

            // Proprioception (7 per joint)
            var joints = _rig.Joints;
            for (int jointIndex = 0; jointIndex < joints.Count; jointIndex++)
            {
                RigJointEntry entry = joints[jointIndex];
                sensor.AddObservation(entry.body.transform.localRotation);
                sensor.AddObservation(entry.body.angularVelocity / ANGULAR_VELOCITY_SCALE);
            }

            // Foot contact (8)
            sensor.AddObservation(_footLeft != null && _footLeft.IsGrounded);
            sensor.AddObservation(_footLeft != null ? _footLeft.ContactNormal : Vector3.zero);
            sensor.AddObservation(_footRight != null && _footRight.IsGrounded);
            sensor.AddObservation(_footRight != null ? _footRight.ContactNormal : Vector3.zero);

            // Opponent-relative (19); omitted entirely in balance-phase rigs
            if (!_observeOpponent)
            {
                return;
            }
            if (_opponentRig != null)
            {
                Rigidbody opponentPelvis = _opponentRig.Pelvis;
                sensor.AddObservation(pelvisTransform.InverseTransformPoint(opponentPelvis.transform.position));
                sensor.AddObservation(pelvisTransform.InverseTransformDirection(opponentPelvis.linearVelocity - pelvis.linearVelocity));
                sensor.AddObservation(pelvisTransform.InverseTransformPoint(_opponentRig.Head.position));
                sensor.AddObservation(pelvisTransform.InverseTransformPoint(_opponentRig.GloveLeft.position));
                sensor.AddObservation(pelvisTransform.InverseTransformPoint(_opponentRig.GloveRight.position));
                sensor.AddObservation(_opponentStamina != null ? _opponentStamina.Anaerobic01 : 1f);
            }
            else
            {
                for (int zeroIndex = 0; zeroIndex < 15; zeroIndex++)
                {
                    sensor.AddObservation(0f);
                }
                sensor.AddObservation(1f);
            }
            sensor.AddObservation(_ringCenter != null
                ? pelvisTransform.InverseTransformPoint(_ringCenter.position)
                : Vector3.zero);
        }

        public override void Heuristic(in ActionBuffers actionsOut)
        {
            var continuous = actionsOut.ContinuousActions;
            for (int actionIndex = 0; actionIndex < continuous.Length; actionIndex++)
            {
                continuous[actionIndex] = 0f;
            }
            if (_dofRoles == null)
            {
                return;
            }

            Rigidbody pelvis = _rig.Pelvis;
            Vector3 support = (_footLeft.transform.position + _footRight.transform.position) * 0.5f;
            Vector3 lean = pelvis.worldCenterOfMass - support;
            Vector3 velocity = pelvis.linearVelocity;
            Transform pelvisTransform = pelvis.transform;
            Vector3 localLean = pelvisTransform.InverseTransformDirection(new Vector3(lean.x, 0f, lean.z));
            Vector3 localVelocity = pelvisTransform.InverseTransformDirection(new Vector3(velocity.x, 0f, velocity.z));

            float pitch = Mathf.Clamp(-(_heuristicKp * localLean.z + _heuristicKd * localVelocity.z),
                -HEURISTIC_MAX_ACTION, HEURISTIC_MAX_ACTION);
            float roll = Mathf.Clamp(-(_heuristicKp * localLean.x + _heuristicKd * localVelocity.x),
                -HEURISTIC_MAX_ACTION, HEURISTIC_MAX_ACTION);

            for (int dofIndex = 0; dofIndex < _dofRoles.Length; dofIndex++)
            {
                switch (_dofRoles[dofIndex])
                {
                    case DOF_ROLE_ANKLE_PITCH: continuous[dofIndex] = pitch; break;
                    case DOF_ROLE_ANKLE_ROLL: continuous[dofIndex] = roll; break;
                    case DOF_ROLE_HIP_PITCH: continuous[dofIndex] = -0.5f * pitch; break;
                    case DOF_ROLE_HIP_ROLL: continuous[dofIndex] = -0.5f * roll; break;
                }
            }
        }

        public override void OnActionReceived(ActionBuffers actions)
        {
            var continuous = actions.ContinuousActions;
            for (int actionIndex = 0; actionIndex < _pendingActions.Length; actionIndex++)
            {
                _pendingActions[actionIndex] = continuous[actionIndex];
            }
            _hasPendingActions = true;
        }

        private void FixedUpdate()
        {
            if (_hasPendingActions)
            {
                _rig.ApplyActions(_pendingActions, 0);
            }
        }
    }
}
