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
        // What the group shot has to hold is the FIGHTERS, not the ring: two
        // abreast at 1.5 m, plus arm span and room to stagger.
        //
        // This is the number that decides how big a fighter is on a phone, and
        // it is the one that was wrong. It was 7 m — the ring canvas corner to
        // corner — because the eight fighters used to stand four abreast and a
        // shot has to hold the whole field. At 9:16 the frame's HEIGHT is then
        // that width over 0.5625, so 7 m of field meant a 12 m tall picture with
        // a 1.7 m fighter in it: measured 2026-08-22, the ring came back a
        // quarter of the frame under a third of a screen of unlit ceiling. No
        // FOV or distance could have rescued it.
        //
        // The fix was to turn the field through 90 degrees rather than to keep
        // tuning the camera — see Systems_ContestSpawner.SlotPositions. Four
        // ranks of two are 1.5 m wide instead of 4.2 m, so the frame this asks
        // for is barely wider than one fighter and the depth spreads the ranks
        // up the picture instead of costing width.
        [SerializeField] private float _wideFrameWidth = 3.4f;
        [SerializeField] private float _wideFrameHeight = 4f;
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
        // Horizontal FOV is the vertical one scaled by the aspect, so at the
        // 55-60 degree base FOV a wide frame needs a lot of distance; widening
        // to 70 degrees buys it back. 80 was the previous value and bought too
        // much: the extra 10 degrees are all VERTICAL, and at 9:16 vertical is
        // the axis with frame to spare, so they were spent entirely on sky and
        // floor either side of the ring.
        //
        // Height is set by the ropes, which is why it cannot simply be lowered
        // to taste. The three rope lines top out 1.85 m above the canvas, and
        // for the near rope to sit BELOW the fighters in frame rather than
        // barred across them the lens has to out-climb it by more than the
        // ratio of their distances. The tightened frame solves to about 4.3 m
        // out, which puts the near rope 1.3 m ahead of the camera and needs an
        // eye 2.6 m above the canvas; 2.8 leaves a little in hand. That is most
        // of three metres lower than the old 5.5, and looking down about 32
        // degrees over a field that now has DEPTH is what spreads the four ranks
        // up the frame instead of flattening them into one line.
        [SerializeField] private float _groupCameraHeight = 2.8f;
        [SerializeField] private float _groupFov = 70f;
        // How far above the pelvis each kind of shot aims.
        //
        // A close shot centres its subject. A group shot deliberately aims
        // LOWER, which pitches the camera down and slides the whole ring up the
        // frame: the waste at 9:16 is not symmetric, because the bottom of the
        // shot is ring apron and arena floor while the top is unlit ceiling
        // void. Trading a little more of the former for a lot less of the
        // latter is the whole of it.
        [SerializeField] private float _closeLookLift = 0.8f;
        [SerializeField] private float _groupLookLift = -0.35f;
        /// <summary>
        /// The aftermath shot — everybody down — gets its own, wider and higher
        /// framing than the group shot it used to share.
        ///
        /// Rope clearance depends on how HIGH the camera is looking, and a mat
        /// full of prone fighters is a much lower subject than a ring full of
        /// standing ones: the pelvis of a fallen fighter sits about 0.7 m below
        /// where it was. On the group shot's numbers that drops the line of sight
        /// far enough that the near ropes cut across the bodies — which is the
        /// tableau the winner banner and the crowd cheer play over, so it is the
        /// worst shot in the round to get wrong. Higher and wider looks down INTO
        /// the ring over the ropes, and holds a field that has scattered on the
        /// way down.
        /// </summary>
        [SerializeField] private float _aftermathFrameWidth = 4.4f;
        [SerializeField] private float _aftermathCameraHeight = 3.9f;
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

        /// <summary>
        /// Holds the round winner. Matched on the identity the spawner recorded
        /// rather than on GameObject.name.Contains(winnerName), which is what
        /// this used to do: "Standard" is a substring of "Contest_Standard2", so
        /// a ring holding two of a kind pointed the winner shot at whichever of
        /// them came first in the array — the loser, half the time.
        /// </summary>
        private void OnRoundEnded(string winnerName)
        {
            _winnerFocus = null;
            for (int rigIndex = 0; rigIndex < _rigs.Length; rigIndex++)
            {
                Systems_FighterIdentity.Resolve(_rigs[rigIndex], out string displayName, out _);
                if (displayName == winnerName)
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

            // Unscaled throughout, and every SmoothDamp below is handed this dt
            // explicitly rather than letting it read Time.deltaTime for itself.
            //
            // A spectator camera has to keep moving while the game does not. The
            // round countdown parks Time.timeScale at 0 for 2.9 s before every
            // round and the knockout FX dips it to 0.35, so on scaled time the
            // camera froze mid-transition and held whatever half-finished pose it
            // had — measured 2026-08-22, the whole of a 3-2-1 countdown played
            // over a shot looking through the ropes at the mat, because the reset
            // had just moved the fighters and the camera could not follow them
            // until GO.
            float dt = Time.unscaledDeltaTime;
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
            bool aftermath = false;
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
                aftermath = true;
            }

            // Solve the distance from the FOV this shot is heading to, so the
            // framing holds at whatever aspect the window ends up with.
            float desiredFov = closeShot ? Mathf.Lerp(_baseFov, _dramaFov, drama) : _groupFov;
            float wideWidth = aftermath ? _aftermathFrameWidth : _wideFrameWidth;
            float followDistance = closeShot
                ? DistanceToFrame(_closeFrameWidth, _closeFrameHeight, desiredFov)
                : DistanceToFrame(wideWidth, _wideFrameHeight, desiredFov);

            // Camera lives on the +Z side: fighters spawn facing +Z, so this
            // side shows their faces.
            Vector3 desiredPosition = new Vector3(
                target.x * _lateralFollowFraction,
                _groundY + (closeShot ? _cameraHeight : aftermath ? _aftermathCameraHeight : _groupCameraHeight),
                target.z + followDistance);
            Vector3 position = Vector3.SmoothDamp(
                transform.position, desiredPosition, ref _positionVelocity, _positionSmoothTime,
                Mathf.Infinity, dt);

            if (_shakeRemaining > 0f)
            {
                _shakeRemaining -= dt;
                float strength = _fallShakeMeters * (_shakeRemaining / SHAKE_DECAY_SECONDS);
                float time = Time.unscaledTime * SHAKE_NOISE_SPEED;
                position.x += (Mathf.PerlinNoise(time, 0.13f) - 0.5f) * 2f * strength;
                position.y += (Mathf.PerlinNoise(0.71f, time) - 0.5f) * 2f * strength;
            }
            transform.position = position;

            float lookLift = closeShot ? _closeLookLift : _groupLookLift;
            _lookPoint = Vector3.SmoothDamp(
                _lookPoint, target + Vector3.up * lookLift, ref _lookVelocity, _lookSmoothTime,
                Mathf.Infinity, dt);
            transform.rotation = Quaternion.LookRotation(_lookPoint - position, Vector3.up);

            _camera.fieldOfView = Mathf.SmoothDamp(
                _camera.fieldOfView, desiredFov, ref _fovVelocity, 0.5f, Mathf.Infinity, dt);
        }

        // Shared with Systems_RaceCamera — see Systems_CameraFraming for why
        // framing is specified as a slab to contain rather than as a distance.
        private float DistanceToFrame(float widthMeters, float heightMeters, float fovDegrees)
        {
            return Systems_CameraFraming.DistanceToFrame(
                _camera, widthMeters, heightMeters, fovDegrees, _minFollowDistance);
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
