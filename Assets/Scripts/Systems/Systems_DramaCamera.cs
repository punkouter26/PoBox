using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Spectator camera for the contest test scene: follows the wobbliest
    /// still-standing fighter, punches the FOV in as drama rises, and adds a
    /// short shake when someone goes down. Falls back to framing the whole
    /// ring when nobody is left standing. Self-discovers contestants at Start.
    /// Test-scene harness only — not used in training or the game loop.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class Systems_DramaCamera : MonoBehaviour
    {
        // Head-height fraction of the start pose below which a fighter counts as
        // down (matches Systems_BalanceContest's fall rule).
        private const float STANDING_HEAD_FRACTION = 0.45f;
        private const float ANGULAR_VELOCITY_DRAMA_SCALE = 0.15f;
        private const float WOBBLE_SMOOTH_RATE = 3f;
        private const float SHAKE_DECAY_SECONDS = 0.45f;
        private const float SHAKE_NOISE_SPEED = 11f;

        [SerializeField] private float _followDistance = 4.5f;
        [SerializeField] private float _cameraHeight = 1.7f;
        [SerializeField] private float _lateralFollowFraction = 0.6f;
        [SerializeField] private float _baseFov = 55f;
        [SerializeField] private float _dramaFov = 42f;
        [SerializeField] private float _positionSmoothTime = 0.7f;
        [SerializeField] private float _lookSmoothTime = 0.4f;
        [SerializeField] private float _fallShakeMeters = 0.2f;

        private Camera _camera;
        private Systems_BalanceContest _contest;
        private Systems_FighterRig _winnerFocus;
        private Systems_FighterRig[] _rigs;
        private float[] _smoothedWobble;
        private float[] _startHeadHeights;
        private bool[] _wasStanding;
        private Vector3 _lookPoint;
        private Vector3 _lookVelocity;
        private Vector3 _positionVelocity;
        private float _fovVelocity;
        private float _shakeRemaining;

        private void Awake()
        {
            _camera = GetComponent<Camera>();
        }

        private void Start()
        {
            _rigs = FindObjectsByType<Systems_FighterRig>(FindObjectsSortMode.InstanceID);
            _smoothedWobble = new float[_rigs.Length];
            _startHeadHeights = new float[_rigs.Length];
            _wasStanding = new bool[_rigs.Length];
            for (int rigIndex = 0; rigIndex < _rigs.Length; rigIndex++)
            {
                _startHeadHeights[rigIndex] = _rigs[rigIndex].Head.position.y;
                _wasStanding[rigIndex] = true;
            }
            _lookPoint = RingCenter() + Vector3.up;

            _contest = FindFirstObjectByType<Systems_BalanceContest>();
            if (_contest != null)
            {
                _contest.RoundEnded += OnRoundEnded;
                _contest.RoundStarted += OnRoundStarted;
            }
        }

        private void OnDestroy()
        {
            if (_contest != null)
            {
                _contest.RoundEnded -= OnRoundEnded;
                _contest.RoundStarted -= OnRoundStarted;
            }
        }

        private void OnRoundEnded(string winnerName)
        {
            _winnerFocus = null;
            for (int rigIndex = 0; rigIndex < _rigs.Length; rigIndex++)
            {
                if (_rigs[rigIndex].gameObject.name.Contains(winnerName))
                {
                    _winnerFocus = _rigs[rigIndex];
                    return;
                }
            }
        }

        private void OnRoundStarted(int round)
        {
            _winnerFocus = null;
        }

        private void LateUpdate()
        {
            if (_rigs == null || _rigs.Length == 0)
            {
                return;
            }

            float dt = Time.deltaTime;
            int bestIndex = -1;
            float bestWobble = -1f;
            for (int rigIndex = 0; rigIndex < _rigs.Length; rigIndex++)
            {
                Systems_FighterRig rig = _rigs[rigIndex];
                float headFraction = rig.Head.position.y / _startHeadHeights[rigIndex];
                bool standing = headFraction > STANDING_HEAD_FRACTION;
                float wobble = 1f - Mathf.Clamp01(headFraction)
                    + rig.Pelvis.angularVelocity.magnitude * ANGULAR_VELOCITY_DRAMA_SCALE;
                _smoothedWobble[rigIndex] = Mathf.Lerp(
                    _smoothedWobble[rigIndex], wobble, dt * WOBBLE_SMOOTH_RATE);

                if (_wasStanding[rigIndex] && !standing)
                {
                    _shakeRemaining = SHAKE_DECAY_SECONDS;
                }
                _wasStanding[rigIndex] = standing;

                if (standing && _smoothedWobble[rigIndex] > bestWobble)
                {
                    bestWobble = _smoothedWobble[rigIndex];
                    bestIndex = rigIndex;
                }
            }

            Vector3 target;
            float drama;
            if (_winnerFocus != null)
            {
                // Winner display: hold a close shot on the round's champion.
                target = _winnerFocus.Pelvis.position;
                drama = 0.85f;
            }
            else if (bestIndex >= 0)
            {
                target = _rigs[bestIndex].Pelvis.position;
                drama = Mathf.Clamp01(bestWobble);
            }
            else
            {
                // Everyone is down — pull back and frame the whole ring.
                target = RingCenter();
                drama = 0f;
            }

            Vector3 desiredPosition = new Vector3(
                target.x * _lateralFollowFraction,
                _cameraHeight,
                target.z - _followDistance - (bestIndex >= 0 ? 0f : 1.5f));
            Vector3 position = Vector3.SmoothDamp(
                transform.position, desiredPosition, ref _positionVelocity, _positionSmoothTime);

            if (_shakeRemaining > 0f)
            {
                _shakeRemaining -= dt;
                float strength = _fallShakeMeters * (_shakeRemaining / SHAKE_DECAY_SECONDS);
                float time = Time.time * SHAKE_NOISE_SPEED;
                position.x += (Mathf.PerlinNoise(time, 0.13f) - 0.5f) * 2f * strength;
                position.y += (Mathf.PerlinNoise(0.71f, time) - 0.5f) * 2f * strength;
            }
            transform.position = position;

            _lookPoint = Vector3.SmoothDamp(
                _lookPoint, target + Vector3.up * 0.8f, ref _lookVelocity, _lookSmoothTime);
            transform.rotation = Quaternion.LookRotation(_lookPoint - position, Vector3.up);

            float desiredFov = Mathf.Lerp(_baseFov, _dramaFov, drama);
            _camera.fieldOfView = Mathf.SmoothDamp(
                _camera.fieldOfView, desiredFov, ref _fovVelocity, 0.5f);
        }

        private Vector3 RingCenter()
        {
            Vector3 sum = Vector3.zero;
            for (int rigIndex = 0; rigIndex < _rigs.Length; rigIndex++)
            {
                sum += _rigs[rigIndex].Pelvis.position;
            }
            return sum / _rigs.Length;
        }
    }
}
