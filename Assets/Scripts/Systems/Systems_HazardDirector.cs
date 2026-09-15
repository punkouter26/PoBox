using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Rolls one random hazard per round and runs it: wind gusts (one
    /// contestant per gust) or gravity lean (world gravity tilts gently and
    /// circles — restored on round end and teardown). Announced via
    /// HazardChosen. Lives under the contest systems root. Test-scene harness
    /// only.
    ///
    /// BALL RAIN WENT WITH THE PHYSX CAST (2026-09-14). It dropped PhysX
    /// spheres into the ring, and a PhysX sphere passes straight through a
    /// body that MuJoCo simulates: the hazard could be announced and could not
    /// touch anyone. Both hazards left reach every contestant through
    /// <see cref="IContestFighter"/> — a gust is a shove on the pelvis, a lean
    /// is handed to each contestant's own simulator.
    /// </summary>
    public sealed class Systems_HazardDirector : MonoBehaviour
    {
        private const int HAZARD_COUNT = 2;
        // One contestant per gust and modest forces: hazards should pick
        // fighters off one at a time, not flatten the whole roster in a wave.
        private const float WIND_MIN_INTERVAL = 2.5f;
        private const float WIND_MAX_INTERVAL = 5f;
        private const float WIND_GUST_SECONDS = 2f;
        private const float WIND_FORCE_NEWTONS = 55f;
        private const float GRAVITY_LEAN_DEGREES = 2.5f;
        private const float GRAVITY_LEAN_CYCLE_SECONDS = 13f;

        private static readonly string[] HazardNames = { "WIND GUSTS", "GRAVITY LEAN" };

        public event System.Action<string> HazardChosen;

        private Systems_ContestReferee _contest;
        private IContestFighter[] _fighters;
        private System.Random _random;
        private Vector3 _baseGravity;
        private int _activeHazard = -1;
        private float _hazardClock;
        private float _windTimer;
        private float _gustRemaining;
        private Vector3 _gustDirection;
        private int _gustTargetIndex;

        private void Start()
        {
            // Seeded from the clock, not from a constant.
            //
            // It was new System.Random(4241), which makes the entire hazard
            // sequence of the entire game byte-identical on every launch, for
            // every player, forever: measured 2026-08-22, a fresh session rolled
            // the same hazard for round 1 and every round after it. A fixed seed
            // is the right call in a training scene, where a run has to be
            // reproducible. This is the shipping contest, where the whole value
            // of a hazard is that you do not know which one is coming.
            _random = new System.Random(System.Environment.TickCount);
            _baseGravity = Physics.gravity;
            _fighters = Systems_Contestants.FindAll();
            _contest = FindFirstObjectByType<Systems_ContestReferee>();
            if (_contest != null)
            {
                _contest.RoundStarted += OnRoundStarted;
            }
            PickHazard();
        }

        private void OnDestroy()
        {
            RestoreGravity();
            if (_contest != null)
            {
                _contest.RoundStarted -= OnRoundStarted;
            }
        }

        private void OnDisable()
        {
            RestoreGravity();
        }

        private void OnRoundStarted(int round)
        {
            ResetActiveHazard();
            PickHazard();
        }

        private void ResetActiveHazard()
        {
            RestoreGravity();
            _gustRemaining = 0f;
        }

        /// <summary>
        /// Rolls the next hazard, never the one just played, so every round
        /// visibly changes something: a repeat reads as the game being stuck
        /// rather than as chance, because the announcer chip says the same
        /// words twice in a row.
        /// </summary>
        private void PickHazard()
        {
            if (_activeHazard < 0)
            {
                _activeHazard = _random.Next(HAZARD_COUNT);
            }
            else
            {
                _activeHazard = (_activeHazard + 1 + _random.Next(HAZARD_COUNT - 1)) % HAZARD_COUNT;
            }
            _hazardClock = 0f;
            _windTimer = WIND_MIN_INTERVAL;
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
            }
        }

        private void TickWind(float dt)
        {
            if (_fighters == null || _fighters.Length == 0)
            {
                return;
            }
            if (_gustRemaining > 0f)
            {
                _gustRemaining -= dt;
                // Half-sine envelope: gusts swell and fade instead of slamming.
                // Each gust hits ONE contestant — no synchronized mass knockdowns.
                // Re-issued every physics step for one step, so the envelope is
                // followed rather than the peak being held.
                float envelope = Mathf.Sin(Mathf.Clamp01(1f - _gustRemaining / WIND_GUST_SECONDS) * Mathf.PI);
                if (_gustTargetIndex < _fighters.Length)
                {
                    _fighters[_gustTargetIndex].Shove(_gustDirection * (WIND_FORCE_NEWTONS * envelope), dt);
                }
                return;
            }
            _windTimer -= dt;
            if (_windTimer <= 0f)
            {
                float angle = (float)(_random.NextDouble() * Mathf.PI * 2.0);
                _gustDirection = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                _gustTargetIndex = _random.Next(_fighters.Length);
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
            ApplyGravity(tilt * _baseGravity);
        }

        /// <summary>
        /// Sets world gravity for PhysX (the ring dressing, any ball or prop)
        /// AND tells every contestant, whose body may be simulated elsewhere.
        /// </summary>
        private void ApplyGravity(Vector3 gravity)
        {
            Physics.gravity = gravity;
            if (_fighters == null) { return; }
            for (int index = 0; index < _fighters.Length; index++)
            {
                _fighters[index].SetGravity(gravity);
            }
        }

        private void RestoreGravity()
        {
            if (_random == null) { return; } // never started, nothing to restore
            ApplyGravity(_baseGravity);
        }
    }
}
