using Unity.Cinemachine;
using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Spectator camera for the contest test scene: follows the wobbliest
    /// still-standing fighter, punches the FOV in as drama rises, and shakes
    /// when bodies land. Falls back to framing the whole ring when nobody is
    /// left standing. Self-discovers contestants at Start.
    /// Test-scene harness only — not used in training or the game loop.
    ///
    /// IT DIRECTS, CINEMACHINE FRAMES. This used to write Main Camera's
    /// transform directly, and everything it did was a SmoothDamp — including
    /// changing subject, which meant that every time the shot moved from one
    /// fighter to another the lens SLID across the ring to get there. A cut is
    /// the most basic verb in broadcast grammar and the old camera could not
    /// perform one. So the shot geometry below is unchanged, every solved
    /// constant is kept, and the result is written to one of two
    /// CinemachineCameras instead of to the Camera; changing shot swaps which
    /// one is live, and CinemachineBrain does the cut or the blend. Two of
    /// them, rather than one, precisely because a cut needs an outgoing and an
    /// incoming camera to cut between — a single vcam that teleports is a
    /// glitch, not an edit.
    ///
    /// THE PRIORITIES ARE NEGATIVE ON PURPOSE. The scene already contains a
    /// CinemachineBrain and a winner-orbit vcam that
    /// <see cref="Systems_WinnerCamera"/> activates at round end, and that vcam
    /// carries the default priority of 0. Anything at or above 0 here would
    /// outrank the winner shot and the round would never celebrate. Sitting
    /// below it means the winner orbit still takes over simply by being
    /// enabled, and now BLENDS in rather than snapping, which is a better shot
    /// than it used to get for free.
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

        /// <summary>
        /// Priority of the live and the parked shot camera. Both below the
        /// winner orbit's 0 — see the class comment.
        /// </summary>
        private const int LIVE_PRIORITY = -10;
        private const int PARKED_PRIORITY = -20;

        /// <summary>
        /// The house blend, used for anything this director did not explicitly
        /// choose — most importantly the transition INTO the winner orbit,
        /// which <see cref="Systems_WinnerCamera"/> triggers by activating its
        /// own vcam while this one is being disabled. Restored every frame that
        /// is not a deliberate edit, so a cut chosen for a subject change can
        /// never leak onto an unrelated transition later.
        /// </summary>
        private static readonly CinemachineBlendDefinition HouseBlend =
            new(CinemachineBlendDefinition.Styles.EaseInOut, 0.6f);

        /// <summary>A change of subject is a cut. That is the whole point.</summary>
        private static readonly CinemachineBlendDefinition CutBlend =
            new(CinemachineBlendDefinition.Styles.Cut, 0f);

        /// <summary>
        /// Pulling back off the fighters — into the aftermath tableau, or back
        /// out to the group once a round resets — is the one move that should
        /// NOT be a cut: it is the camera relaxing, and cutting to a wide shot
        /// of the same subject reads as a mistake rather than as an edit.
        /// </summary>
        private static readonly CinemachineBlendDefinition SettleBlend =
            new(CinemachineBlendDefinition.Styles.EaseInOut, 0.85f);

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
        /// <summary>
        /// Shake amplitude, as the impulse velocity handed to Cinemachine for a
        /// full-strength impact. Metres per second, not metres: an impulse is a
        /// kick the listener decays, not a fixed offset.
        /// </summary>
        [SerializeField] private float _maxShakeVelocity = 0.55f;

        /// <summary>Which kind of shot is on air. A change of kind is an edit.</summary>
        private enum ShotKind { Group, Close, Aftermath }

        private Camera _camera;
        private CinemachineBrain _brain;
        private CinemachineCamera[] _shotCameras;
        private CinemachineImpulseSource _impulseSource;
        private int _liveShot;
        private ShotKind _shotKind = ShotKind.Group;
        private int _shotSubject = -1;
        private bool _shotInitialized;

        private Systems_ContestReferee _contest;
        private Systems_FighterRig _winnerFocus;
        private Systems_FighterRig[] _rigs;
        private float[] _smoothedWobble;
        private float[] _startHeadHeights;
        private bool[] _wasStanding;
        private Vector3 _lookPoint;
        private Vector3 _lookVelocity;
        private Vector3 _positionVelocity;
        private float _fov;
        private float _fovVelocity;
        private float _groundY;
        private int _tourOrdinal;
        private float _tourTimer;
        private bool _discovered;

        private void Awake()
        {
            _camera = GetComponent<Camera>();
        }

        private void Start()
        {
            Discover();
        }

        /// <summary>
        /// Finds the fighters and builds the camera rig, once.
        ///
        /// Retried from LateUpdate rather than assumed to have worked, because
        /// this component is enabled by <see cref="Systems_ContestSpawner"/>
        /// after it spawns — so Start normally runs with a full ring, but a
        /// scene that enables it earlier would otherwise latch an empty roster
        /// forever and never draw a shot.
        /// </summary>
        private void Discover()
        {
            if (_discovered)
            {
                return;
            }
            _rigs = FindObjectsByType<Systems_FighterRig>(FindObjectsSortMode.InstanceID);
            if (_rigs.Length == 0)
            {
                return;
            }
            _discovered = true;

            _smoothedWobble = new float[_rigs.Length];
            _startHeadHeights = new float[_rigs.Length];
            _wasStanding = new bool[_rigs.Length];
            for (int rigIndex = 0; rigIndex < _rigs.Length; rigIndex++)
            {
                _startHeadHeights[rigIndex] = _rigs[rigIndex].Head.position.y - _rigs[rigIndex].GroundY;
                _wasStanding[rigIndex] = true;
            }
            // Every fighter probes the floor it spawned on; they all share one,
            // so the first is the ring floor (or the walk lane).
            _groundY = _rigs.Length > 0 ? _rigs[0].GroundY : 0f;
            _lookPoint = RingCenter() + Vector3.up;
            _fov = _baseFov;

            BuildCameraRig();

            _contest = FindFirstObjectByType<Systems_ContestReferee>();
            if (_contest != null)
            {
                _contest.RoundEnded += OnRoundEnded;
                _contest.RoundStarted += OnRoundStarted;
            }
        }

        /// <summary>
        /// The brain, the two shot cameras and the impulse source, all built in
        /// code so that no scene has to be regenerated to gain them —
        /// regenerating a contest scene is the documented way to destroy one
        /// (see CLAUDE.md on <c>BuildAll</c>).
        /// </summary>
        private void BuildCameraRig()
        {
            _brain = GetComponent<CinemachineBrain>();
            if (_brain == null)
            {
                _brain = gameObject.AddComponent<CinemachineBrain>();
            }
            _brain.DefaultBlend = HouseBlend;

            _impulseSource = gameObject.AddComponent<CinemachineImpulseSource>();
            // Reset() supplies these in the editor and is never called on a
            // runtime AddComponent, so an unset definition would default to the
            // Custom shape with an empty curve and generate silence.
            _impulseSource.ImpulseDefinition = new CinemachineImpulseDefinition
            {
                ImpulseChannel = 1,
                ImpulseShape = CinemachineImpulseDefinition.ImpulseShapes.Bump,
                CustomImpulseShape = new AnimationCurve(),
                ImpulseDuration = 0.32f,
                ImpulseType = CinemachineImpulseDefinition.ImpulseTypes.Uniform,
                DissipationDistance = 100f,
                DissipationRate = 0.25f,
                PropagationSpeed = 343f
            };

            _shotCameras = new CinemachineCamera[2];
            _shotCameras[0] = BuildShotCamera("CM_Drama_A");
            _shotCameras[1] = BuildShotCamera("CM_Drama_B");
            _liveShot = 0;
            _shotCameras[0].Priority = LIVE_PRIORITY;
            _shotCameras[1].Priority = PARKED_PRIORITY;
        }

        /// <summary>
        /// One shot camera: a bare CinemachineCamera, which in Cinemachine 3
        /// simply publishes its own transform when it carries no procedural
        /// components — that is what lets the solved geometry below keep being
        /// the thing that aims the shot — plus an impulse listener so it feels
        /// the shakes this director generates.
        /// </summary>
        private CinemachineCamera BuildShotCamera(string cameraName)
        {
            var host = new GameObject(cameraName);
            host.transform.SetPositionAndRotation(transform.position, transform.rotation);
            var shotCamera = host.AddComponent<CinemachineCamera>();
            shotCamera.Lens = LensSettings.Default;
            shotCamera.Lens.FieldOfView = _baseFov;
            host.AddComponent<CinemachineImpulseListener>();
            return shotCamera;
        }

        private void OnDestroy()
        {
            if (_contest != null)
            {
                _contest.RoundEnded -= OnRoundEnded;
                _contest.RoundStarted -= OnRoundStarted;
            }
            if (_shotCameras == null)
            {
                return;
            }
            for (int shotIndex = 0; shotIndex < _shotCameras.Length; shotIndex++)
            {
                if (_shotCameras[shotIndex] != null)
                {
                    Destroy(_shotCameras[shotIndex].gameObject);
                }
            }
        }

        /// <summary>
        /// A body just landed: kick the camera, scaled by how hard.
        ///
        /// Called by <see cref="Systems_ImpactFx"/> from the contact itself,
        /// which is why this director no longer generates its own shake when it
        /// notices a fighter's head drop below the fall line. That test fired
        /// on a HEIGHT crossing — some tens of milliseconds after or before the
        /// body actually arrived, and at the same strength for a topple as for
        /// a faceplant. Both systems shaking would also have double-counted
        /// every fall.
        /// </summary>
        public void ShakeAt(Vector3 position, float strength01)
        {
            if (_impulseSource == null)
            {
                return;
            }
            float velocity = Mathf.Lerp(0.12f, _maxShakeVelocity, Mathf.Clamp01(strength01));
            _impulseSource.GenerateImpulseAtPositionWithVelocity(position, Vector3.down * velocity);
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
            Discover();
            if (_rigs == null || _rigs.Length == 0 || _shotCameras == null)
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

            // Restored every frame, so a Cut chosen for one edit cannot leak on
            // to the next transition — including the winner orbit's, which this
            // director does not perform and must not accidentally style.
            _brain.DefaultBlend = HouseBlend;

            int bestIndex = -1;
            float bestWobble = -1f;
            int standingCount = 0;
            for (int rigIndex = 0; rigIndex < _rigs.Length; rigIndex++)
            {
                Systems_FighterRig rig = _rigs[rigIndex];
            // Ground-relative. A fraction of an ABSOLUTE head height rescales with
            // altitude, and the ring canvas sits 1 m up: 45% of a 2.6 m head is
            // 1.17 m, which is 17 cm above the canvas, so a fighter counted as
            // standing until its head was practically on the floor and this never
            // fired in the contest at all.
                float headFraction =
                    (rig.Head.position.y - rig.GroundY) / _startHeadHeights[rigIndex];
                bool standing = headFraction > STANDING_HEAD_FRACTION;
                float wobble = 1f - Mathf.Clamp01(headFraction)
                    + rig.Pelvis.angularVelocity.magnitude * ANGULAR_VELOCITY_DRAMA_SCALE;
                _smoothedWobble[rigIndex] = Mathf.Lerp(
                    _smoothedWobble[rigIndex], wobble, dt * WOBBLE_SMOOTH_RATE);
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
            // The subject this shot is ABOUT, so that touring from one fighter
            // to the next registers as an edit even though the shot kind has
            // not changed. -1 for shots that are about the field rather than
            // about a fighter.
            int subject = -1;
            if (_winnerFocus != null)
            {
                // Winner display: hold a close shot on the round's champion.
                target = _winnerFocus.Pelvis.position;
                drama = 0.85f;
                closeShot = true;
                subject = IndexOf(_winnerFocus);
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
                subject = focusIndex;
            }
            else if (bestIndex >= 0)
            {
                target = _rigs[bestIndex].Pelvis.position;
                drama = Mathf.Clamp01(bestWobble);
                closeShot = true;
                subject = bestIndex;
            }
            else
            {
                // Everyone is down — pull back and frame the whole ring.
                target = RingCenter();
                drama = 0f;
                closeShot = false;
                aftermath = true;
            }

            ShotKind kind = aftermath ? ShotKind.Aftermath : closeShot ? ShotKind.Close : ShotKind.Group;

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
            float lookLift = closeShot ? _closeLookLift : _groupLookLift;
            Vector3 desiredLook = target + Vector3.up * lookLift;

            bool cutting = !_shotInitialized || kind != _shotKind || subject != _shotSubject;
            if (cutting)
            {
                PerformEdit(kind);
                _shotKind = kind;
                _shotSubject = subject;
                _shotInitialized = true;
                // The incoming camera is placed EXACTLY on the solved pose, with
                // its smoothing state cleared. Anything else and the new shot
                // starts by sliding out of wherever the old one happened to be,
                // which is the slide this whole rewrite exists to remove — the
                // brain's blend, not a SmoothDamp, is what handles the
                // transition now.
                _lookPoint = desiredLook;
                _lookVelocity = Vector3.zero;
                _positionVelocity = Vector3.zero;
                _fov = desiredFov;
                _fovVelocity = 0f;
            }

            CinemachineCamera live = _shotCameras[_liveShot];
            Vector3 position = cutting
                ? desiredPosition
                : Vector3.SmoothDamp(live.transform.position, desiredPosition, ref _positionVelocity,
                    _positionSmoothTime, Mathf.Infinity, dt);
            _lookPoint = cutting
                ? desiredLook
                : Vector3.SmoothDamp(_lookPoint, desiredLook, ref _lookVelocity, _lookSmoothTime,
                    Mathf.Infinity, dt);
            _fov = cutting
                ? desiredFov
                : Mathf.SmoothDamp(_fov, desiredFov, ref _fovVelocity, 0.5f, Mathf.Infinity, dt);

            live.transform.SetPositionAndRotation(
                position, Quaternion.LookRotation(_lookPoint - position, Vector3.up));
            live.Lens.FieldOfView = _fov;
        }

        /// <summary>
        /// Makes the cut: chooses how this transition should look, then swaps
        /// which of the two shot cameras is live so the brain has something to
        /// cut FROM and something to cut TO.
        /// </summary>
        private void PerformEdit(ShotKind incoming)
        {
            if (_shotInitialized)
            {
                _brain.DefaultBlend = ChooseBlend(_shotKind, incoming);
                _liveShot = 1 - _liveShot;
            }
            else
            {
                // First shot of the contest: there is nothing to blend from, and
                // easing in from the menu orbit's pose would read as a drift.
                _brain.DefaultBlend = CutBlend;
            }
            _shotCameras[_liveShot].Priority = LIVE_PRIORITY;
            _shotCameras[1 - _liveShot].Priority = PARKED_PRIORITY;
        }

        private static CinemachineBlendDefinition ChooseBlend(ShotKind outgoing, ShotKind incoming)
        {
            // Settling onto the aftermath tableau, or opening back out to the
            // field when a round resets, is a move rather than an edit.
            if (incoming == ShotKind.Aftermath)
            {
                return SettleBlend;
            }
            if (incoming == ShotKind.Group && outgoing == ShotKind.Close)
            {
                return SettleBlend;
            }
            // Everything else — tightening onto a fighter as the ring thins,
            // touring from one to the next, coming out of the aftermath into a
            // fresh round — is a cut.
            return CutBlend;
        }

        private int IndexOf(Systems_FighterRig rig)
        {
            for (int rigIndex = 0; rigIndex < _rigs.Length; rigIndex++)
            {
                if (_rigs[rigIndex] == rig)
                {
                    return rigIndex;
                }
            }
            return -1;
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
