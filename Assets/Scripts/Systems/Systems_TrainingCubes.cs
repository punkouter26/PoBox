using Unity.MLAgents;
using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Training-scene projectile hazard: throws small cubes at the fighter's
    /// torso so the policy learns localized impacts, not just torso shoves —
    /// matching what the contest scene does to it later. Curriculum-driven via
    /// the environment parameter "cube_speed_max" (0 disables, the default).
    /// Deterministic per-fighter seed, episode-aware, pooled. Training tool.
    /// </summary>
    [DefaultExecutionOrder(-98)] // after agent/reward, before the Academy stepper
    public sealed class Systems_TrainingCubes : MonoBehaviour
    {
        private const string SPEED_ENV_PARAM = "cube_speed_max";
        private const int POOL_SIZE = 2;
        private const float MIN_INTERVAL_SECONDS = 3f;
        private const float MAX_INTERVAL_SECONDS = 6f;
        private const float MIN_SPEED = 3f;
        private const float CUBE_SIZE = 0.25f;
        private const float CUBE_MASS = 3f;
        private const float THROW_DISTANCE = 3f;
        private const float CUBE_LIFE_SECONDS = 3f;

        [SerializeField] private Systems_FighterRig _rig;
        [SerializeField] private Agent_FighterBoxing _agent;
        [SerializeField] private int _seed;

        private System.Random _random;
        private Rigidbody[] _cubes;
        private float[] _cubeAges;
        private float _nextThrowTimer;
        private int _lastStepCount;

        // Called by the editor scene builder.
        public void EditorInitialize(Systems_FighterRig rig, Agent_FighterBoxing agent, int seed)
        {
            _rig = rig;
            _agent = agent;
            _seed = seed;
        }

        private void Awake()
        {
            _random = new System.Random(_seed * 7919 + 13);
            BuildPool();
        }

        private void OnEnable()
        {
            ScheduleNext();
        }

        private void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;

            int stepCount = _agent.StepCount;
            if (stepCount < _lastStepCount)
            {
                // Episode reset — clear airborne cubes, never hit a fresh pose.
                for (int cubeIndex = 0; cubeIndex < _cubes.Length; cubeIndex++)
                {
                    _cubes[cubeIndex].gameObject.SetActive(false);
                }
                ScheduleNext();
            }
            _lastStepCount = stepCount;

            for (int cubeIndex = 0; cubeIndex < _cubes.Length; cubeIndex++)
            {
                if (_cubes[cubeIndex].gameObject.activeSelf)
                {
                    _cubeAges[cubeIndex] += dt;
                    if (_cubeAges[cubeIndex] >= CUBE_LIFE_SECONDS)
                    {
                        _cubes[cubeIndex].gameObject.SetActive(false);
                    }
                }
            }

            _nextThrowTimer -= dt;
            if (_nextThrowTimer > 0f)
            {
                return;
            }
            ScheduleNext();

            float maxSpeed = Academy.Instance.EnvironmentParameters.GetWithDefault(SPEED_ENV_PARAM, 0f);
            if (maxSpeed <= 0f)
            {
                return;
            }
            Throw(maxSpeed);
        }

        private void Throw(float maxSpeed)
        {
            Rigidbody cube = null;
            for (int cubeIndex = 0; cubeIndex < _cubes.Length; cubeIndex++)
            {
                if (!_cubes[cubeIndex].gameObject.activeSelf)
                {
                    cube = _cubes[cubeIndex];
                    _cubeAges[cubeIndex] = 0f;
                    break;
                }
            }
            if (cube == null)
            {
                return;
            }
            float speed = MIN_SPEED + (float)_random.NextDouble() * Mathf.Max(0f, maxSpeed - MIN_SPEED);
            float angle = (float)(_random.NextDouble() * Mathf.PI * 2.0);
            Vector3 direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            Vector3 targetPoint = _rig.Torso.worldCenterOfMass;
            Vector3 spawn = targetPoint + direction * THROW_DISTANCE + Vector3.up * 0.4f;
            cube.transform.SetPositionAndRotation(spawn, Quaternion.identity);
            cube.gameObject.SetActive(true);
            cube.linearVelocity = (targetPoint - spawn).normalized * speed;
            cube.angularVelocity = Vector3.zero;
        }

        private void BuildPool()
        {
            _cubes = new Rigidbody[POOL_SIZE];
            _cubeAges = new float[POOL_SIZE];
            for (int cubeIndex = 0; cubeIndex < POOL_SIZE; cubeIndex++)
            {
                GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = $"TrainCube{_seed}_{cubeIndex}";
                cube.transform.SetParent(transform, false);
                cube.transform.localScale = Vector3.one * CUBE_SIZE;
                var body = cube.AddComponent<Rigidbody>();
                body.mass = CUBE_MASS;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                cube.SetActive(false);
                _cubes[cubeIndex] = body;
            }
        }

        private void ScheduleNext()
        {
            float range = MAX_INTERVAL_SECONDS - MIN_INTERVAL_SECONDS;
            _nextThrowTimer = MIN_INTERVAL_SECONDS + (float)_random.NextDouble() * range;
        }
    }
}
