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
        // Above this many fighters still upright, show the GROUP rather than
        // touring individuals.
        //
        // Measured 2026-08-22 on a live 8-fighter balance contest: only 2 of 8
        // fighters were inside the frustum and the camera was aimed 28.9 deg off
        // their centroid, because the tour branch below forced a close shot
        // whenever two or more were standing. Consecutive captures showed the
        // crowd stands and an empty corner of the canvas with no fighter in
        // either. The wide frame existed the whole time and nothing ever
        // selected it while anyone was upright.
        //
        // A balance contest is a COMPARISON -- who is still up -- so a full ring
        // has to be readable as a group. The close tour is right once the field
        // has thinned and each fall matters individually.
        private const int GROUP_SHOT_MIN_STANDING = 4;
        private const float ANGULAR_VELOCITY_DRAMA_SCALE = 0.15f;
        private const float WOBBLE_SMOOTH_RATE = 3f;
        private const float SHAKE_DECAY_SECONDS = 0.45f;
        private const float SHAKE_NOISE_SPEED = 11f;

        // Framing is specified as the world width and height the shot must
        // CONTAIN, not as a camera distance, because the distance that achieves
        // it depends on the aspect ratio and this game is portrait 9:16. A 60
        // degree vertical FOV is only 36 degrees horizontal at 9:16, and the old
        // fixed distances (2.8 m close, scaled by a hand-tuned 1.35 for
        // portrait) framed 1.8 m of width - narrower than a fallen fighter is
        // long, which is why close-ups rendered as an unreadable wall of limbs.
        // 2.4 m is a fallen fighter plus margin; 7 m is the whole eight-fighter
        // ring.
        [SerializeField] private float _closeFrameWidth = 2.4f;
        [SerializeField] private float _closeFrameHeight = 2.8f;
        // The ring canvas is 6.1 m square, so 7 m of width covers it corner to
        // corner with a little margin. Height was 4 m, which on a 9:16 window is
        // never the binding axis (see DistanceToFrame) -- raised to 5 m so the
        // group shot keeps some headroom above the fighters instead of cropping
        // at the shoulders when the camera is close to its minimum distance.
        [SerializeField] private float _wideFrameWidth = 7f;
        [SerializeField] private float _wideFrameHeight = 5f;
        // Never let a solved distance put the near plane inside the subject.
        [SerializeField] private float _minFollowDistance = 2.2f;
        // With 2+ fighters standing, the camera tours them, holding each
        // for this many seconds.
        [SerializeField] private float _tourSecondsPerFighter = 3f;
        // Eye height ABOVE THE FLOOR THE FIGHTERS STAND ON, not above world
        // zero. The distinction only started to matter when the ring moved onto
        // its 1 m platform (Systems_ContestSpawner.RING_FLOOR_Y): read as a
        // world Y, the tuned 1.9 put the lens at 1.9 m while the three rope
        // lines spanned y 1.05-2.05, 1.45-2.45 and 1.85-2.85, so the camera sat
        // inside the top rope and every shot came back with the ropes as hard
        // horizontal bars across the frame and the void under the ring filling
        // the bottom third. Resolved against the rigs' own ground probe below,
        // which keeps this correct for the walk lane at y 0 as well.
        //
        // 2.6 m is set by geometry, not taste. Clearing the top rope is not
        // enough on its own: for the near rope to sit BELOW the subject in
        // frame rather than across it, the lens has to out-climb the rope by
        // more than the ratio of their distances. Solved for the worst case in
        // this ring — a front-row fighter, whose close-up puts the camera ~5 m
        // back and the near rope ~3 m ahead of it — that needs an eye 2.24 m
        // above the canvas; the ring's own top rope tops out at 1.85. This
        // leaves a third of a metre in hand and looks down about 22 degrees.
        [SerializeField] private float _cameraHeight = 2.6f;
        // Group shots need their own height and FOV, both forced by the 9:16
        // window rather than by taste.
        //
        // Horizontal FOV is the vertical one scaled by the aspect, and the
        // aspect here is 0.466, so at the 55-60 degree base FOV covering the
        // 7 m ring needs about 13 m of distance. Measured 2026-08-22: the first
        // group shot put the camera at z = 14.2 and the ring came back a few
        // dark pixels across.
        //
        // Widening to 80 degrees brings that to ~9 m. At 9 m the ring ropes cut
        // the line to the ring centre at 2.6 m and 4 m of height and are clear
        // at 5.5 m -- raycast from the candidate positions to the ring centre,
        // same day -- so the group camera sits high and looks down over them.
        [SerializeField] private float _groupCameraHeight = 5.5f;
        [SerializeField] private float _groupFov = 80f;
        [SerializeField] private float _lateralFollowFraction = 0.6f;
        [SerializeField] private float _baseFov = 55f;
        [SerializeField] private float _dramaFov = 42f;
        [SerializeField] private float _positionSmoothTime = 0.7f;
        [SerializeField] private float _lookSmoothTime = 0.4f;
        [SerializeField] private float _fallShakeMeters = 0.2f;

        private Camera _camera;
        private Systems_ContestReferee _contest;
        private Systems_FighterRig _winnerFocus;
        private Systems_FighterRig[] _rigs;
        private float[] _smoothedWobble;
        private float[] _startHeadHeights;
        private bool[] _wasStanding;
        private Vector3 _lookPoint;
        private Vector3 _lookVelocity;
        private Vector3 _positionVelocity;
        private float _fovVelocity;
        private float _groundY;
        private float _shakeRemaining;
        private int _tourOrdinal;
        private float _tourTimer;

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
            // Every fighter probes the floor it spawned on; they all share one,
            // so the first is the ring floor (or the walk lane).
            _groundY = _rigs.Length > 0 ? _rigs[0].GroundY : 0f;
            _lookPoint = RingCenter() + Vector3.up;

            _contest = FindFirstObjectByType<Systems_ContestReferee>();
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
            int standingCount = 0;
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

                if (standing)
                {
                    standingCount++;
                    if (_smoothedWobble[rigIndex] > bestWobble)
                    {
                        bestWobble = _smoothedWobble[rigIndex];
                        bestIndex = rigIndex;
                    }
                }
            }

            Vector3 target;
            float drama;
            bool closeShot;
            if (_winnerFocus != null)
            {
                // Winner display: hold a close shot on the round's champion.
                target = _winnerFocus.Pelvis.position;
                drama = 0.85f;
                closeShot = true;
            }
            else if (standingCount >= GROUP_SHOT_MIN_STANDING)
            {
                // Group shot: frame everyone still upright. Drama stays at 0 so
                // the FOV opens to _baseFov and the wide frame is used, which is
                // what makes all of them fit.
                target = StandingCentroid();
                drama = 0f;
                closeShot = false;
            }
            else if (standingCount >= 2)
            {
                // Cinematic tour: hold each standing fighter for a few seconds.
                _tourTimer -= dt;
                if (_tourTimer <= 0f)
                {
                    _tourTimer = _tourSecondsPerFighter;
                    _tourOrdinal++;
                }
                int focusIndex = FindStandingByOrdinal(_tourOrdinal % standingCount);
                target = _rigs[focusIndex].Pelvis.position;
                drama = Mathf.Max(0.6f, Mathf.Clamp01(_smoothedWobble[focusIndex]));
                closeShot = true;
            }
            else if (bestIndex >= 0)
            {
                target = _rigs[bestIndex].Pelvis.position;
                drama = Mathf.Clamp01(bestWobble);
                closeShot = true;
            }
            else
            {
                // Everyone is down — pull back and frame the whole ring.
                target = RingCenter();
                drama = 0f;
                closeShot = false;
            }

            // Solve the distance from the FOV this shot is heading to, so the
            // framing holds at whatever aspect the window ends up with.
            float desiredFov = closeShot ? Mathf.Lerp(_baseFov, _dramaFov, drama) : _groupFov;
            float followDistance = closeShot
                ? DistanceToFrame(_closeFrameWidth, _closeFrameHeight, desiredFov)
                : DistanceToFrame(_wideFrameWidth, _wideFrameHeight, desiredFov);

            // Camera lives on the +Z side: fighters spawn facing +Z, so this
            // side shows their faces.
            Vector3 desiredPosition = new Vector3(
                target.x * _lateralFollowFraction,
                _groundY + (closeShot ? _cameraHeight : _groupCameraHeight),
                target.z + followDistance);
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

            _camera.fieldOfView = Mathf.SmoothDamp(
                _camera.fieldOfView, desiredFov, ref _fovVelocity, 0.5f);
        }

        /// <summary>
        /// Distance at which a <paramref name="widthMeters"/> x
        /// <paramref name="heightMeters"/> slab exactly fits the frustum, taking
        /// whichever of the two axes binds. Horizontal FOV is the vertical one
        /// scaled by the aspect, so on a portrait window the width is almost
        /// always what binds - the opposite of the landscape intuition the old
        /// hard-coded distances were tuned with.
        /// </summary>
        private float DistanceToFrame(float widthMeters, float heightMeters, float fovDegrees)
        {
            float halfVertical = Mathf.Tan(fovDegrees * 0.5f * Mathf.Deg2Rad);
            float halfHorizontal = halfVertical * Mathf.Max(0.01f, _camera.aspect);
            float forWidth = widthMeters * 0.5f / Mathf.Max(0.01f, halfHorizontal);
            float forHeight = heightMeters * 0.5f / Mathf.Max(0.01f, halfVertical);
            return Mathf.Max(_minFollowDistance, Mathf.Max(forWidth, forHeight));
        }

        // Maps "the n-th standing fighter" to a rig index; standing membership
        // changes frame to frame, so the ordinal is resolved fresh each call.
        private int FindStandingByOrdinal(int ordinal)
        {
            int seen = 0;
            for (int rigIndex = 0; rigIndex < _rigs.Length; rigIndex++)
            {
                if (!_wasStanding[rigIndex])
                {
                    continue;
                }
                if (seen == ordinal)
                {
                    return rigIndex;
                }
                seen++;
            }
            return 0;
        }

        // Centre of the fighters still upright, which is what the group shot
        // frames. Deliberately not RingCenter: once half the field is down, the
        // survivors are usually clustered away from the middle and framing the
        // geometric centre of the ring puts them at the edge.
        private Vector3 StandingCentroid()
        {
            Vector3 sum = Vector3.zero;
            int count = 0;
            for (int rigIndex = 0; rigIndex < _rigs.Length; rigIndex++)
            {
                if (!_wasStanding[rigIndex])
                {
                    continue;
                }
                sum += _rigs[rigIndex].Pelvis.position;
                count++;
            }
            return count == 0 ? RingCenter() : sum / count;
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
