using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Contest hazard: after a calm grace period each round, lobs pooled cubes
    /// at random still-standing fighters to test their balance. Cubes carry
    /// rigidbodies, so fall sensors ignore them (they only count static
    /// contacts). Self-discovers fighters at Start — lives under the contest
    /// systems root, activated after the setup menu spawns the roster.
    /// Test-scene harness only.
    /// </summary>
    public sealed class Systems_CubeThrower : MonoBehaviour
    {
        private const float STANDING_HEAD_FRACTION = 0.45f;

        [SerializeField] private float _graceSeconds = 10f;
        [SerializeField] private float _minIntervalSeconds = 1.5f;
        [SerializeField] private float _maxIntervalSeconds = 3f;
        [SerializeField] private float _cubeSpeed = 7f;
        // Throws ramp linearly from _cubeSpeed (at the end of the grace
        // period) to _maxCubeSpeed at _rampEndSeconds into the round — by then
        // hits are hard enough to floor any fighter.
        [SerializeField] private float _maxCubeSpeed = 20f;
        [SerializeField] private float _rampEndSeconds = 120f;
        [SerializeField] private float _cubeSize = 0.25f;
        [SerializeField] private float _cubeMass = 3f;
        [SerializeField] private float _throwDistance = 3.5f;
        [SerializeField] private float _cubeLifeSeconds = 4f;
        [SerializeField] private int _poolSize = 8;
        // 3D impact thwacks (Kenney CC0), assigned by the editor scene tool.
        [SerializeField] private AudioClip[] _impactClips;
        // Asset material: runtime Shader.Find gets stripped from device builds.
        [SerializeField] private Material _sparkMaterial;

        private Systems_BalanceContest _contest;
        private Systems_FighterRig[] _rigs;
        private float[] _startHeadHeights;
        private Rigidbody[] _cubes;
        private float[] _cubeAges;
        private float _roundClock;
        private float _nextThrowTimer;
        private System.Random _random;

        private void Start()
        {
            _random = new System.Random(7919);
            _rigs = FindObjectsByType<Systems_FighterRig>(FindObjectsSortMode.InstanceID);
            _startHeadHeights = new float[_rigs.Length];
            for (int rigIndex = 0; rigIndex < _rigs.Length; rigIndex++)
            {
                _startHeadHeights[rigIndex] = _rigs[rigIndex].Head.position.y;
            }
            _contest = FindFirstObjectByType<Systems_BalanceContest>();
            if (_contest != null)
            {
                _contest.RoundStarted += OnRoundStarted;
            }
            BuildPool();
            _roundClock = 0f;
            ScheduleNext();
        }

        private void OnDestroy()
        {
            if (_contest != null)
            {
                _contest.RoundStarted -= OnRoundStarted;
            }
        }

        private void OnRoundStarted(int round)
        {
            _roundClock = 0f;
            ScheduleNext();
            for (int cubeIndex = 0; cubeIndex < _cubes.Length; cubeIndex++)
            {
                _cubes[cubeIndex].gameObject.SetActive(false);
            }
        }

        private void FixedUpdate()
        {
            if (_rigs == null || _rigs.Length == 0)
            {
                return;
            }
            float dt = Time.fixedDeltaTime;
            _roundClock += dt;

            for (int cubeIndex = 0; cubeIndex < _cubes.Length; cubeIndex++)
            {
                if (_cubes[cubeIndex].gameObject.activeSelf)
                {
                    _cubeAges[cubeIndex] += dt;
                    if (_cubeAges[cubeIndex] >= _cubeLifeSeconds)
                    {
                        _cubes[cubeIndex].gameObject.SetActive(false);
                    }
                }
            }

            if (_roundClock < _graceSeconds)
            {
                return;
            }
            _nextThrowTimer -= dt;
            if (_nextThrowTimer > 0f)
            {
                return;
            }
            ScheduleNext();
            ThrowAtRandomStanding();
        }

        private void BuildPool()
        {
            // One shared bright material so cubes read clearly against the
            // dark arena (default primitive material renders near-black here).
            var cubeMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            cubeMaterial.SetColor("_BaseColor", new Color(1f, 0.55f, 0.05f, 1f));

            ParticleSystem spark = BuildSparkSystem();

            _cubes = new Rigidbody[_poolSize];
            _cubeAges = new float[_poolSize];
            for (int cubeIndex = 0; cubeIndex < _poolSize; cubeIndex++)
            {
                GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.GetComponent<Renderer>().sharedMaterial = cubeMaterial;
                cube.name = "ThrownCube" + cubeIndex;
                cube.transform.SetParent(transform, false);
                cube.transform.localScale = Vector3.one * _cubeSize;
                var body = cube.AddComponent<Rigidbody>();
                body.mass = _cubeMass;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                cube.AddComponent<AudioSource>();
                cube.AddComponent<Systems_CubeImpactFx>().Initialize(_impactClips, spark, cubeIndex);
                cube.SetActive(false);
                _cubes[cubeIndex] = body;
            }
        }

        // One shared burst system: repositioned to the newest impact and
        // replayed — plenty for readability, zero per-hit allocation.
        private ParticleSystem BuildSparkSystem()
        {
            var sparkObject = new GameObject("CubeImpactSpark");
            sparkObject.transform.SetParent(transform, false);
            var spark = sparkObject.AddComponent<ParticleSystem>();
            spark.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); // must be stopped before configuring duration
            ParticleSystem.MainModule main = spark.main;
            main.loop = false;
            main.playOnAwake = false;
            main.duration = 0.3f;
            main.startLifetime = 0.35f;
            main.startSpeed = 4.5f;
            main.startSize = 0.06f;
            main.startColor = new Color(1f, 0.7f, 0.2f);
            main.gravityModifier = 1.5f;
            ParticleSystem.EmissionModule emission = spark.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 18) });
            ParticleSystem.ShapeModule shape = spark.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.05f;
            var renderer = spark.GetComponent<ParticleSystemRenderer>();
            if (_sparkMaterial != null)
            {
                renderer.sharedMaterial = _sparkMaterial;
            }
            else
            {
                renderer.material = new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit"));
                renderer.material.SetColor("_BaseColor", new Color(1f, 0.75f, 0.3f));
            }
            return spark;
        }

        private void ThrowAtRandomStanding()
        {
            int standingCount = 0;
            for (int rigIndex = 0; rigIndex < _rigs.Length; rigIndex++)
            {
                if (IsStanding(rigIndex))
                {
                    standingCount++;
                }
            }
            if (standingCount == 0)
            {
                return;
            }
            int pick = _random.Next(standingCount);
            Systems_FighterRig target = null;
            for (int rigIndex = 0; rigIndex < _rigs.Length; rigIndex++)
            {
                if (!IsStanding(rigIndex))
                {
                    continue;
                }
                if (pick == 0)
                {
                    target = _rigs[rigIndex];
                    break;
                }
                pick--;
            }
            if (target == null)
            {
                return;
            }

            Rigidbody cube = TakeCube();
            float angle = (float)(_random.NextDouble() * Mathf.PI * 2.0);
            Vector3 direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            Vector3 targetPoint = target.Torso.worldCenterOfMass;
            Vector3 spawn = targetPoint + direction * _throwDistance + Vector3.up * 0.6f;
            float rampProgress = Mathf.Clamp01((_roundClock - _graceSeconds) / Mathf.Max(1f, _rampEndSeconds - _graceSeconds));
            float speed = Mathf.Lerp(_cubeSpeed, _maxCubeSpeed, rampProgress);
            cube.transform.SetPositionAndRotation(spawn, Quaternion.identity);
            cube.gameObject.SetActive(true);
            cube.linearVelocity = (targetPoint - spawn).normalized * speed + Vector3.up * 1.2f;
            cube.angularVelocity = Vector3.zero;
            ResetCubeAge(cube);
        }

        private bool IsStanding(int rigIndex)
        {
            return _rigs[rigIndex].Head.position.y / _startHeadHeights[rigIndex] > STANDING_HEAD_FRACTION;
        }

        // Prefers an idle cube; falls back to recycling the oldest airborne one.
        private Rigidbody TakeCube()
        {
            int oldestIndex = 0;
            float oldestAge = -1f;
            for (int cubeIndex = 0; cubeIndex < _cubes.Length; cubeIndex++)
            {
                if (!_cubes[cubeIndex].gameObject.activeSelf)
                {
                    return _cubes[cubeIndex];
                }
                if (_cubeAges[cubeIndex] > oldestAge)
                {
                    oldestAge = _cubeAges[cubeIndex];
                    oldestIndex = cubeIndex;
                }
            }
            return _cubes[oldestIndex];
        }

        private void ResetCubeAge(Rigidbody cube)
        {
            for (int cubeIndex = 0; cubeIndex < _cubes.Length; cubeIndex++)
            {
                if (_cubes[cubeIndex] == cube)
                {
                    _cubeAges[cubeIndex] = 0f;
                    return;
                }
            }
        }

        private void ScheduleNext()
        {
            float range = _maxIntervalSeconds - _minIntervalSeconds;
            _nextThrowTimer = _minIntervalSeconds + (float)_random.NextDouble() * range;
        }
    }
}
