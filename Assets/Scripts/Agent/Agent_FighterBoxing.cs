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

        [SerializeField] private Systems_FighterRig _rig;
        [SerializeField] private Systems_Stamina _stamina;
        [SerializeField] private Sensor_GroundContact _footLeft;
        [SerializeField] private Sensor_GroundContact _footRight;
        [SerializeField] private Systems_FighterRig _opponentRig;
        [SerializeField] private Systems_Stamina _opponentStamina;
        [SerializeField] private Transform _ringCenter;

        private float[] _pendingActions;
        private bool _hasPendingActions;
        private Sensor_GroundContact[] _contactSensors;

        public Systems_FighterRig Rig => _rig;

        public static int ComputeObservationCount(int jointCount)
        {
            return ROOT_OBSERVATIONS + PER_JOINT_OBSERVATIONS * jointCount + FOOT_OBSERVATIONS + OPPONENT_OBSERVATIONS;
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

            // Opponent-relative (19); zeros when no opponent (staging/balance scenes)
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
