using Unity.MLAgents;
using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Unified stand-and-walk reward. The agent is handed a commanded speed
    /// each episode and paid for matching it while staying upright, so
    /// standing still is simply the 0 m/s case of walking and no separate
    /// balance phase or brain hand-off is needed. The curriculum raises
    /// <c>speed_command_max</c> from 0 to walking pace; the command is also an
    /// observation, so the finished brain obeys whatever speed the game asks
    /// for at runtime.
    ///
    /// Reward shape, deliberately different from <see cref="Reward_Balance"/>:
    /// there is NO terminal fall penalty. Per-step reward is positive-definite
    /// and ending early already forfeits every remaining step, which is
    /// punishment enough. The old -1 terminal was ~40x anything a 1.5 s
    /// episode could earn and drowned the signal (observed 2026-08-19:
    /// mean reward pinned at -0.98, episodes stuck at 76 steps).
    /// </summary>
    [DefaultExecutionOrder(-99)] // after the agent (-100), before the Academy stepper
    public sealed class Reward_Locomotion : MonoBehaviour
    {
        private const float HEAD_COLLAPSE_FRACTION = 0.4f;
        // Speed error at which the match term decays to ~e^-1. Wide enough
        // that a first clumsy step still scores something.
        private const float SPEED_KERNEL = 0.6f;
        // Never let a [0,1] criterion reach exactly 0: one zero would wipe the
        // whole product and erase every other criterion's gradient.
        private const float PRODUCT_FACTOR_FLOOR = 0.05f;

        [SerializeField] private Agent_FighterBoxing _agent;
        [SerializeField] private Systems_FighterRig _rig;
        [SerializeField] private Sensor_GroundContact[] _fallContacts;
        [SerializeField] private float _uprightWeight = 0.3f;
        [SerializeField] private float _heightWeight = 0.3f;
        [SerializeField] private float _speedMatchWeight = 0.4f;
        // Penalizes jerky tick-to-tick command changes so calm motion wins.
        [SerializeField] private float _smoothnessWeight = 0.05f;
        // Upper end of the commanded-speed range. Driven by the curriculum
        // parameter "speed_command_max": 0 in the standing lesson, walking
        // pace in the last one.
        [SerializeField] private float _speedCommandMax;

        private float _stepScale;
        private float _startHeadHeight;
        private float _commandedSpeed;
        private Vector3 _commandedDirection = Vector3.forward;
        private bool _terminated;
        private int _lastStepCount;
        private float _speedMatchSum;
        private int _speedMatchSamples;

        // Called by the editor scene builder.
        public void EditorInitialize(Agent_FighterBoxing agent, Systems_FighterRig rig,
            Sensor_GroundContact[] fallContacts)
        {
            _agent = agent;
            _rig = rig;
            _fallContacts = fallContacts;
        }

        private void Start()
        {
            // Scaled so a full-length episode at perfect score returns ~1.
            _stepScale = 1f / Mathf.Max(1, _agent.MaxStep);
            _startHeadHeight = _rig.Head.position.y;
            RollCommand();
        }

        private void FixedUpdate()
        {
            int stepCount = _agent.StepCount;
            if (stepCount < _lastStepCount)
            {
                // New episode began (fall reset or MaxStep rollover).
                FlushEpisodeStats();
                _startHeadHeight = _rig.Head.position.y;
                RollCommand();
                _terminated = false;
            }
            _lastStepCount = stepCount;
            if (_terminated)
            {
                return;
            }

            if (IsFallen(out int fallCause))
            {
                // No penalty added: forfeiting the rest of the episode is the
                // punishment. See the class summary.
                Academy.Instance.StatsRecorder.Add("Locomotion/FallCause", fallCause, StatAggregationMethod.Histogram);
                Academy.Instance.StatsRecorder.Add("Locomotion/StepsSurvived", stepCount);
                _terminated = true;
                _agent.EndEpisode();
                return;
            }

            Vector3 torsoUp = _rig.Torso.transform.up;
            float uprightDot = Mathf.Max(0f, Vector3.Dot(torsoUp, Vector3.up));
            float uprightReward = uprightDot * uprightDot;

            float headDelta = _rig.Head.position.y - _startHeadHeight;
            float heightReward = Mathf.Exp(-20f * headDelta * headDelta);

            // Signed: travelling backwards scores worse than standing still,
            // which stops "fall away from the goal" from looking neutral.
            float speedAlongGoal = Vector3.Dot(_rig.Pelvis.linearVelocity, _commandedDirection);
            float speedError = speedAlongGoal - _commandedSpeed;
            float speedMatchReward = Mathf.Exp(-(speedError * speedError) / (SPEED_KERNEL * SPEED_KERNEL));
            _speedMatchSum += speedMatchReward;
            _speedMatchSamples++;

            float locomotionReward =
                ProductFactor(uprightReward, _uprightWeight) *
                ProductFactor(heightReward, _heightWeight) *
                ProductFactor(speedMatchReward, _speedMatchWeight);

            _agent.AddReward(_stepScale * (locomotionReward - _smoothnessWeight * _agent.LastActionDelta01));
        }

        // Clamps a [0,1] criterion away from zero, then applies its weight as
        // an exponent so the terms form a weighted geometric mean.
        private static float ProductFactor(float value, float weight)
        {
            return Mathf.Pow(Mathf.Max(PRODUCT_FACTOR_FLOOR, value), weight);
        }

        // One command per episode: a constant target is far easier to learn
        // than one that changes mid-episode, and the game only ever sets it
        // once per round anyway.
        private void RollCommand()
        {
            _speedCommandMax = Academy.Instance.EnvironmentParameters
                .GetWithDefault("speed_command_max", _speedCommandMax);
            _commandedSpeed = Random.Range(0f, _speedCommandMax);
            _commandedDirection = Vector3.forward;
            _agent.SetLocomotionCommand(_commandedSpeed, _commandedDirection);
        }

        private void FlushEpisodeStats()
        {
            if (_speedMatchSamples > 0)
            {
                Academy.Instance.StatsRecorder.Add("Locomotion/SpeedMatchMean", _speedMatchSum / _speedMatchSamples);
                Academy.Instance.StatsRecorder.Add("Locomotion/CommandedSpeed", _commandedSpeed);
            }
            _speedMatchSum = 0f;
            _speedMatchSamples = 0;
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
                fallCause = _fallContacts.Length;
                return true;
            }
            fallCause = -1;
            return false;
        }
    }
}
