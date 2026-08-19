using Unity.MLAgents;
using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Stage-2 walking reward, layered on top of <see cref="Reward_Balance"/>.
    /// Balance keeps owning uprightness, head height and the fall terminal;
    /// this component only pays for travel along the ring's long axis and ends
    /// the episode with a bonus once the far side is reached. Per-step rewards
    /// are scaled by 1/MaxStep so the episode return stays commensurate with
    /// the -1 fall terminal. Wired by the walk scene builder.
    /// </summary>
    [DefaultExecutionOrder(-98)] // after Reward_Balance (-99) so a fall wins the tie
    public sealed class Reward_Walk : MonoBehaviour
    {
        // Comfortable human walking pace. Velocity toward the goal is divided
        // by this and clamped, so the agent gains nothing from sprinting into
        // a fall — steady walking already scores the maximum.
        private const float TARGET_SPEED = 1.0f;

        [SerializeField] private Agent_FighterBoxing _agent;
        [SerializeField] private Systems_FighterRig _rig;
        // World-space direction the fighter must travel. Set by the scene
        // builder; every fighter in the scene shares one axis because they all
        // line up on the same edge and walk straight across.
        [SerializeField] private Vector3 _goalDirection = Vector3.forward;
        [SerializeField] private float _goalDistance = 5.6f;
        [SerializeField] private float _progressWeight = 1f;
        [SerializeField] private float _goalBonus = 1f;

        private float _stepScale;
        private float _startProjection;
        private bool _reached;
        private int _lastStepCount;

        // Called by the editor scene builder.
        public void EditorInitialize(Agent_FighterBoxing agent, Systems_FighterRig rig,
            Vector3 goalDirection, float goalDistance)
        {
            _agent = agent;
            _rig = rig;
            _goalDirection = goalDirection.normalized;
            _goalDistance = goalDistance;
        }

        private void Start()
        {
            _stepScale = 1f / Mathf.Max(1, _agent.MaxStep);
            _goalDirection = _goalDirection.normalized;
            _startProjection = Vector3.Dot(_rig.Pelvis.position, _goalDirection);
        }

        private void FixedUpdate()
        {
            int stepCount = _agent.StepCount;
            if (stepCount < _lastStepCount)
            {
                // New episode began (fall reset, goal reached, or MaxStep rollover).
                // Record the miss here rather than at the goal: sampling only
                // successes would pin Walk/Reached at 1.0 and hide the failures.
                if (!_reached)
                {
                    Academy.Instance.StatsRecorder.Add("Walk/Reached", 0f);
                    Academy.Instance.StatsRecorder.Add("Walk/DistanceTravelled",
                        Vector3.Dot(_rig.Pelvis.position, _goalDirection) - _startProjection);
                }
                _startProjection = Vector3.Dot(_rig.Pelvis.position, _goalDirection);
                _reached = false;
            }
            _lastStepCount = stepCount;
            if (_reached)
            {
                return;
            }

            float travelled = Vector3.Dot(_rig.Pelvis.position, _goalDirection) - _startProjection;
            if (travelled >= _goalDistance)
            {
                _agent.AddReward(_goalBonus);
                Academy.Instance.StatsRecorder.Add("Walk/StepsToGoal", stepCount);
                Academy.Instance.StatsRecorder.Add("Walk/Reached", 1f);
                Academy.Instance.StatsRecorder.Add("Walk/DistanceTravelled", travelled);
                _reached = true;
                _agent.EndEpisode();
                return;
            }

            float speedTowardGoal = Vector3.Dot(_rig.Pelvis.linearVelocity, _goalDirection);
            float speedReward = Mathf.Clamp01(speedTowardGoal / TARGET_SPEED);
            _agent.AddReward(_stepScale * _progressWeight * speedReward);
        }
    }
}
