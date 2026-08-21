using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Rolls one random hazard per round and runs it: wind gusts (one fighter
    /// per gust), gravity lean (world gravity tilts gently and circles —
    /// restored on round end and teardown), or ball rain (pooled bouncy
    /// spheres dropping into the ring). Announced via HazardChosen.
    /// Lives under the contest systems root. Test-scene harness only.
    /// </summary>
    public sealed class Systems_HazardDirector : MonoBehaviour
    {
        private const int HAZARD_COUNT = 3;
        // One fighter per gust and modest forces: hazards should pick fighters
        // off one at a time, not flatten the whole roster in a single wave.
        private const float WIND_MIN_INTERVAL = 2.5f;
        private const float WIND_MAX_INTERVAL = 5f;
        private const float WIND_GUST_SECONDS = 2f;
        private const float WIND_FORCE_NEWTONS = 55f;
        private const float GRAVITY_LEAN_DEGREES = 2.5f;
        private const float GRAVITY_LEAN_CYCLE_SECONDS = 13f;
        private const int BALL_POOL_SIZE = 10;
        private const float BALL_MIN_INTERVAL = 1.2f;
        private const float BALL_MAX_INTERVAL = 2.4f;
        private const float BALL_DROP_HEIGHT = 6f;
        private const float BALL_LIFE_SECONDS = 8f;

        private static readonly string[] HazardNames = { "WIND GUSTS", "GRAVITY LEAN", "BALL RAIN" };

        public event System.Action<string> HazardChosen;

        private Systems_ContestReferee _contest;
        private Systems_FighterRig[] _rigs;
        private System.Random _random;
        private Vector3 _baseGravity;
        private int _activeHazard = -1;
        private float _hazardClock;
        private float _windTimer;
        private float _gustRemaining;
        private Vector3 _gustDirection;
        private int _gustTargetIndex;
        private Rigidbody[] _balls;
        private float[] _ballAges;
        private float _ballTimer;

        private void Start()
        {
            _random = new System.Random(4241);
            _baseGravity = Physics.gravity;
            _rigs = FindObjectsByType<Systems_FighterRig>(FindObjectsSortMode.InstanceID);
            _contest = FindFirstObjectByType<Systems_ContestReferee>();
            if (_contest != null)
            {
                _contest.RoundStarted += OnRoundStarted;
            }
            BuildBallPool();
            PickHazard();
        }

        private void OnDestroy()
        {
            Physics.gravity = _baseGravity;
            if (_contest != null)
            {
                _contest.RoundStarted -= OnRoundStarted;
            }
        }

        private void OnDisable()
        {
            Physics.gravity = _baseGravity;
        }

        private void OnRoundStarted(int round)
        {
            ResetActiveHazard();
            PickHazard();
        }

        private void ResetActiveHazard()
        {
            Physics.gravity = _baseGravity;
            for (int ballIndex = 0; ballIndex < _balls.Length; ballIndex++)
            {
                _balls[ballIndex].gameObject.SetActive(false);
            }
            _gustRemaining = 0f;
        }

        private void PickHazard()
        {
            _activeHazard = _random.Next(HAZARD_COUNT);
            _hazardClock = 0f;
            _windTimer = WIND_MIN_INTERVAL;
            _ballTimer = BALL_MIN_INTERVAL;
            HazardChosen?.Invoke(HazardNames[_activeHazard]);
        }

        private void FixedUpdate()
        {
            if (_activeHazard < 0)
            {
                return;
            }
            float dt = Time.fixedDeltaTime;
            _hazardClock += dt;

            switch (_activeHazard)
            {
                case 0: TickWind(dt); break;
                case 1: TickGravityLean(); break;
                case 2: TickBallRain(dt); break;
            }
        }

        private void TickWind(float dt)
        {
            if (_gustRemaining > 0f)
            {
                _gustRemaining -= dt;
                // Half-sine envelope: gusts swell and fade instead of slamming.
                // Each gust hits ONE fighter — no synchronized mass knockdowns.
                float envelope = Mathf.Sin(Mathf.Clamp01(1f - _gustRemaining / WIND_GUST_SECONDS) * Mathf.PI);
                if (_gustTargetIndex < _rigs.Length)
                {
                    _rigs[_gustTargetIndex].Torso.AddForce(_gustDirection * (WIND_FORCE_NEWTONS * envelope), ForceMode.Force);
                }
                return;
            }
            _windTimer -= dt;
            if (_windTimer <= 0f)
            {
                float angle = (float)(_random.NextDouble() * Mathf.PI * 2.0);
                _gustDirection = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                _gustTargetIndex = _random.Next(_rigs.Length);
                _gustRemaining = WIND_GUST_SECONDS;
                float range = WIND_MAX_INTERVAL - WIND_MIN_INTERVAL;
                _windTimer = WIND_MIN_INTERVAL + (float)_random.NextDouble() * range;
            }
        }

        private void TickGravityLean()
        {
            float phase = _hazardClock / GRAVITY_LEAN_CYCLE_SECONDS * Mathf.PI * 2f;
            float ramp = Mathf.Clamp01(_hazardClock / 6f); // ease in over 6 s
            float lean = GRAVITY_LEAN_DEGREES * ramp;
            Quaternion tilt = Quaternion.Euler(Mathf.Sin(phase) * lean, 0f, Mathf.Cos(phase) * lean);
            Physics.gravity = tilt * _baseGravity;
        }

        private void TickBallRain(float dt)
        {
            for (int ballIndex = 0; ballIndex < _balls.Length; ballIndex++)
            {
                if (_balls[ballIndex].gameObject.activeSelf)
                {
                    _ballAges[ballIndex] += dt;
                    if (_ballAges[ballIndex] >= BALL_LIFE_SECONDS)
                    {
                        _balls[ballIndex].gameObject.SetActive(false);
                    }
                }
            }
            _ballTimer -= dt;
            if (_ballTimer > 0f)
            {
                return;
            }
            float range = BALL_MAX_INTERVAL - BALL_MIN_INTERVAL;
            _ballTimer = BALL_MIN_INTERVAL + (float)_random.NextDouble() * range;
            DropBall();
        }

        private void DropBall()
        {
            for (int ballIndex = 0; ballIndex < _balls.Length; ballIndex++)
            {
                if (_balls[ballIndex].gameObject.activeSelf)
                {
                    continue;
                }
                Rigidbody ball = _balls[ballIndex];
                float x = ((float)_random.NextDouble() * 2f - 1f) * 2.4f;
                float z = ((float)_random.NextDouble() * 2f - 1f) * 2.4f;
                ball.transform.position = new Vector3(x, BALL_DROP_HEIGHT, z);
                ball.gameObject.SetActive(true);
                ball.linearVelocity = Vector3.zero;
                ball.angularVelocity = Vector3.zero;
                _ballAges[ballIndex] = 0f;
                return;
            }
        }

        private void BuildBallPool()
        {
            var bounceMaterial = new PhysicsMaterial("PM_HazardBall")
            {
                bounciness = 0.85f,
                bounceCombine = PhysicsMaterialCombine.Maximum
            };
            var ballMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            ballMaterial.SetColor("_BaseColor", new Color(0.2f, 0.8f, 0.9f, 1f));

            _balls = new Rigidbody[BALL_POOL_SIZE];
            _ballAges = new float[BALL_POOL_SIZE];
            for (int ballIndex = 0; ballIndex < BALL_POOL_SIZE; ballIndex++)
            {
                GameObject ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                ball.name = "HazardBall" + ballIndex;
                ball.transform.SetParent(transform, false);
                ball.transform.localScale = Vector3.one * 0.35f;
                ball.GetComponent<Renderer>().sharedMaterial = ballMaterial;
                ball.GetComponent<Collider>().sharedMaterial = bounceMaterial;
                var body = ball.AddComponent<Rigidbody>();
                body.mass = 2f;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                ball.SetActive(false);
                _balls[ballIndex] = body;
            }
        }
    }
}
