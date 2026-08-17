using Unity.MLAgents;
using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Applies random horizontal force bursts to a body so the balance policy
    /// learns to absorb shoves. Deterministic per-agent (seeded System.Random),
    /// episode-aware (no shove carries across a reset), and curriculum-ready:
    /// the trainer can override the max force via the environment parameter
    /// "shove_force_max" (0 disables shoving). Training-scene tool only.
    /// </summary>
    [DefaultExecutionOrder(-98)] // after agent/reward, before the Academy stepper
    public sealed class Systems_Shover : MonoBehaviour
    {
        private const string FORCE_ENV_PARAM = "shove_force_max";

        [SerializeField] private Rigidbody _target;
        [SerializeField] private Agent_FighterBoxing _agent;
        [SerializeField] private int _seed;
        [SerializeField] private float _minIntervalSeconds = 2f;
        [SerializeField] private float _maxIntervalSeconds = 5f;
        [SerializeField] private float _minForceNewtons = 100f;
        [SerializeField] private float _maxForceNewtons = 300f;
        [SerializeField] private float _burstDurationSeconds = 0.2f;

        private System.Random _random;
        private float _nextShoveTimer;
        private float _burstRemaining;
        private Vector3 _burstForce;
        private int _lastStepCount;

        // Called by the editor scene builder.
        public void EditorInitialize(Rigidbody target, Agent_FighterBoxing agent, int seed)
        {
            _target = target;
            _agent = agent;
            _seed = seed;
        }

        private void Awake()
        {
            _random = new System.Random(_seed);
        }

        private void OnEnable()
        {
            ScheduleNext();
        }

        private void FixedUpdate()
        {
            if (_agent != null)
            {
                int stepCount = _agent.StepCount;
                if (stepCount < _lastStepCount)
                {
                    // Episode reset — never shove a settling pose.
                    _burstRemaining = 0f;
                    ScheduleNext();
                }
                _lastStepCount = stepCount;
            }

            float dt = Time.fixedDeltaTime;

            if (_burstRemaining > 0f)
            {
                _target.AddForce(_burstForce, ForceMode.Force);
                _burstRemaining -= dt;
                return;
            }

            _nextShoveTimer -= dt;
            if (_nextShoveTimer <= 0f)
            {
                float maxForce = Academy.Instance.EnvironmentParameters.GetWithDefault(FORCE_ENV_PARAM, _maxForceNewtons);
                if (maxForce > 0f)
                {
                    float angle = (float)(_random.NextDouble() * Mathf.PI * 2.0);
                    float range = Mathf.Max(0f, maxForce - _minForceNewtons);
                    float magnitude = Mathf.Min(maxForce, _minForceNewtons + (float)_random.NextDouble() * range);
                    _burstForce = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * magnitude;
                    _burstRemaining = _burstDurationSeconds;
                }
                ScheduleNext();
            }
        }

        private void ScheduleNext()
        {
            float range = _maxIntervalSeconds - _minIntervalSeconds;
            _nextShoveTimer = _minIntervalSeconds + (float)_random.NextDouble() * range;
        }
    }
}
