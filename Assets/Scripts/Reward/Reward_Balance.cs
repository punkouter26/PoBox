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
        // Under _productReward a single factor of exactly 0 zeroes the whole
        // step and erases the signal from every other criterion. Observed on
        // grandma_balance04: flat -1.000 mean reward, 0.000 spread, 1.85 M
        // steps, while Balance/UprightMean sat at 0.87. Flooring each factor
        // makes a failing criterion damp the step instead of deleting it.
        private const float PRODUCT_FACTOR_FLOOR = 0.05f;

        [SerializeField] private Agent_FighterBoxing _agent;
        [SerializeField] private Systems_FighterRig _rig;
        [SerializeField] private Systems_Stamina _stamina;
        [SerializeField] private Sensor_GroundContact[] _fallContacts;
        [SerializeField] private float _uprightWeight = 0.3f;
        [SerializeField] private float _heightWeight = 0.3f;
        [SerializeField] private float _comWeight = 0.2f;
        // Rewards vertical thighs/shins so the legs form a load-bearing column
        // (standing-phase trick from the WalkerAgent reference project).
        // Deserializes to 0 in scenes built before 2026-08-18 — no change to
        // in-flight runs; newly built balance scenes get 0.2.
        [SerializeField] private float _legUprightWeight = 0.2f;
        // Penalizes jerky tick-to-tick command changes so calm motion wins.
        // 0 in scenes built before 2026-08-18; new balance scenes get 0.05.
        [SerializeField] private float _smoothnessWeight = 0.05f;
        // Multiplies the [0,1] balance terms instead of summing them
        // (WalkerAgent reference project): the agent must satisfy EVERY
        // criterion at once, and the per-step reward stays positive-definite,
        // so "fell slower" always scores better than "fell fast". Penalties
        // still subtract. False keeps the original weighted sum.
        [SerializeField] private bool _productReward;
        [SerializeField] private float _energyWeight; // 0 for Stage 1; enable once standing works
        [SerializeField] private float _energyPowerScale = 20000f;

        private float _startHeadHeight;
        private float _totalMass;
        private float _stepScale;
        private Transform[] _legTransforms;
        private Vector3[] _legLocalUpAxes;
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
            CacheLegSegments();
        }

        // Bone local axes differ between rigs (capsule vs imported skeletons),
        // so the "up" of each leg segment is calibrated from the start pose:
        // whatever local direction pointed at world up scores 1 when restored.
        private void CacheLegSegments()
        {
            var joints = _rig.Joints;
            int legCount = 0;
            for (int jointIndex = 0; jointIndex < joints.Count; jointIndex++)
            {
                if (IsLegSegment(joints[jointIndex].body.name))
                {
                    legCount++;
                }
            }
            _legTransforms = new Transform[legCount];
            _legLocalUpAxes = new Vector3[legCount];
            int legIndex = 0;
            for (int jointIndex = 0; jointIndex < joints.Count; jointIndex++)
            {
                if (!IsLegSegment(joints[jointIndex].body.name))
                {
                    continue;
                }
                Transform legTransform = joints[jointIndex].body.transform;
                _legTransforms[legIndex] = legTransform;
                _legLocalUpAxes[legIndex] = legTransform.InverseTransformDirection(Vector3.up);
                legIndex++;
            }
        }

        private static bool IsLegSegment(string bodyName)
        {
            return bodyName.IndexOf("thigh", System.StringComparison.OrdinalIgnoreCase) >= 0
                || bodyName.IndexOf("shin", System.StringComparison.OrdinalIgnoreCase) >= 0;
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
            float legUprightReward = ComputeLegUprightReward();

            float energyPenalty = _energyWeight > 0f && _stamina != null
                ? Mathf.Clamp01(_stamina.LastPower / _energyPowerScale)
                : 0f;

            float balanceReward;
            if (_productReward)
            {
                // Every factor is already in [0,1]. Weights become exponents:
                // weight 0 drops a term out (pow -> 1), higher weight makes it
                // matter more. A zero in any weighted factor zeroes the step.
                balanceReward =
                    ProductFactor(uprightReward, _uprightWeight) *
                    ProductFactor(heightReward, _heightWeight) *
                    ProductFactor(comReward, _comWeight) *
                    ProductFactor(legUprightReward, _legUprightWeight);
            }
            else
            {
                balanceReward =
                    _uprightWeight * uprightReward +
                    _heightWeight * heightReward +
                    _comWeight * comReward +
                    _legUprightWeight * legUprightReward;
            }

            _agent.AddReward(_stepScale * (
                balanceReward -
                _energyWeight * energyPenalty -
                _smoothnessWeight * _agent.LastActionDelta01));
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

        // Clamps a [0,1] criterion away from zero before it enters the product,
        // then applies its weight as an exponent.
        private static float ProductFactor(float value, float weight)
        {
            return Mathf.Pow(Mathf.Max(PRODUCT_FACTOR_FLOOR, value), weight);
        }

        private float ComputeLegUprightReward()
        {
            if (_legUprightWeight <= 0f)
            {
                return 0f;
            }
            if (_legTransforms.Length == 0)
            {
                // Imported skeletons (grandma/grandpa) name no body "thigh" or
                // "shin", so no segments are found. Neutral, never 0 — a 0 here
                // multiplied out to a dead reward on those two rigs.
                return 1f;
            }
            float sum = 0f;
            for (int legIndex = 0; legIndex < _legTransforms.Length; legIndex++)
            {
                Vector3 worldAxis = _legTransforms[legIndex].TransformDirection(_legLocalUpAxes[legIndex]);
                sum += Mathf.Max(0f, worldAxis.y);
            }
            float mean = sum / _legTransforms.Length;
            return mean * mean;
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
