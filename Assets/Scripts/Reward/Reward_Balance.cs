using Unity.MLAgents;
using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Stage-1 balance reward: upright torso + head height + center-of-mass
    /// over the feet. Ends the episode with a fall penalty when any fall
    /// sensor (torso/head/shins/gloves — shins and gloves stop the kneeling
    /// local optimum) touches the ground or the head collapses. Per-step
    /// rewards are scaled by 1/MaxStep so the episode return stays ~[0,1] and
    /// the -1 terminal is commensurate. Wired by the balance scene builder.
    /// </summary>
    [DefaultExecutionOrder(-99)] // after the agent (-100), before the Academy stepper
    public sealed class Reward_Balance : MonoBehaviour
    {
        private const float FALL_PENALTY = -1f;
        private const float HEAD_COLLAPSE_FRACTION = 0.4f;
        private const float HEIGHT_KERNEL_SHARPNESS = 20f;

        [SerializeField] private Agent_FighterBoxing _agent;
        [SerializeField] private Systems_FighterRig _rig;
        [SerializeField] private Systems_Stamina _stamina;
        [SerializeField] private Sensor_GroundContact[] _fallContacts;
        [SerializeField] private float _uprightWeight = 0.4f;
        [SerializeField] private float _heightWeight = 0.4f;
        [SerializeField] private float _comWeight = 0.2f;
        [SerializeField] private float _energyWeight; // 0 for Stage 1; enable once standing works
        [SerializeField] private float _energyPowerScale = 20000f;

        private float _startHeadHeight;
        private float _totalMass;
        private float _stepScale;
        private bool _terminated;
        private int _lastStepCount;
        private float _uprightSum;
        private int _uprightSamples;

        // Called by the editor scene builder.
        public void EditorInitialize(Agent_FighterBoxing agent, Systems_FighterRig rig, Systems_Stamina stamina,
            Sensor_GroundContact[] fallContacts)
        {
            _agent = agent;
            _rig = rig;
            _stamina = stamina;
            _fallContacts = fallContacts;
        }

        private void Start()
        {
            _startHeadHeight = _rig.Head.position.y;
            _stepScale = 1f / Mathf.Max(1, _agent.MaxStep);
            _totalMass = _rig.Pelvis.mass;
            var joints = _rig.Joints;
            for (int jointIndex = 0; jointIndex < joints.Count; jointIndex++)
            {
                _totalMass += joints[jointIndex].body.mass;
            }
        }

        private void FixedUpdate()
        {
            int stepCount = _agent.StepCount;
            if (stepCount < _lastStepCount)
            {
                // New episode began (fall reset or MaxStep rollover).
                FlushEpisodeStats();
                _terminated = false;
            }
            _lastStepCount = stepCount;
            if (_terminated)
            {
                return;
            }

            if (IsFallen(out int fallCause))
            {
                _agent.AddReward(FALL_PENALTY);
                Academy.Instance.StatsRecorder.Add("Balance/FallCause", fallCause, StatAggregationMethod.Histogram);
                Academy.Instance.StatsRecorder.Add("Balance/HeadHeightAtEnd", _rig.Head.position.y);
                _terminated = true;
                _agent.EndEpisode();
                return;
            }

            Vector3 torsoUp = _rig.Torso.transform.up;
            float uprightDot = Mathf.Max(0f, Vector3.Dot(torsoUp, Vector3.up));
            float uprightReward = uprightDot * uprightDot;
            _uprightSum += uprightReward;
            _uprightSamples++;

            float headDelta = _rig.Head.position.y - _startHeadHeight;
            float heightReward = Mathf.Exp(-HEIGHT_KERNEL_SHARPNESS * headDelta * headDelta);

            float comReward = ComputeComReward();

            float energyPenalty = _energyWeight > 0f && _stamina != null
                ? Mathf.Clamp01(_stamina.LastPower / _energyPowerScale)
                : 0f;

            _agent.AddReward(_stepScale * (
                _uprightWeight * uprightReward +
                _heightWeight * heightReward +
                _comWeight * comReward -
                _energyWeight * energyPenalty));
        }

        private void FlushEpisodeStats()
        {
            if (_uprightSamples > 0)
            {
                Academy.Instance.StatsRecorder.Add("Balance/UprightMean", _uprightSum / _uprightSamples);
            }
            _uprightSum = 0f;
            _uprightSamples = 0;
        }

        private bool IsFallen(out int fallCause)
        {
            for (int contactIndex = 0; contactIndex < _fallContacts.Length; contactIndex++)
            {
                if (_fallContacts[contactIndex].IsGrounded)
                {
                    fallCause = contactIndex;
                    return true;
                }
            }
            if (_rig.Head.position.y < _startHeadHeight * HEAD_COLLAPSE_FRACTION)
            {
                fallCause = _fallContacts.Length; // collapse without registered contact
                return true;
            }
            fallCause = -1;
            return false;
        }

        private float ComputeComReward()
        {
            Vector3 weightedSum = _rig.Pelvis.worldCenterOfMass * _rig.Pelvis.mass;
            var joints = _rig.Joints;
            for (int jointIndex = 0; jointIndex < joints.Count; jointIndex++)
            {
                Rigidbody body = joints[jointIndex].body;
                weightedSum += body.worldCenterOfMass * body.mass;
            }
            Vector3 centerOfMass = weightedSum / _totalMass;

            Vector3 footLeft = _rig.FootLeftSensor.transform.position;
            Vector3 footRight = _rig.FootRightSensor.transform.position;
            Vector3 support = (footLeft + footRight) * 0.5f;

            float deltaX = centerOfMass.x - support.x;
            float deltaZ = centerOfMass.z - support.z;
            float squaredDistance = deltaX * deltaX + deltaZ * deltaZ;
            return Mathf.Exp(-2f * squaredDistance);
        }
    }
}
