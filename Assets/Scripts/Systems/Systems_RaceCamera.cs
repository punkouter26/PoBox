using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Chase-from-the-front camera for the walk race: sits ahead of the pack on
    /// the finish side, backs up the lane as the racers advance, and keeps them
    /// at a constant size in frame.
    ///
    /// It replaces a camera bolted to the finish line at author time. That one
    /// was solved to hold the WHOLE lane — both ends, all four corners — inside
    /// a 9:16 frustum, which is a correct solve for the wrong shot: measured
    /// 2026-08-22, it parked at z = 9.0 with the start line 8.6 m away, so at
    /// the gun the four racers were about a tenth of the frame tall under half a
    /// screen of empty sky and grey ground, and they never got closer because
    /// they never travel (the whole field ends a round inside 0.5 m of the
    /// line). A shot that only has to hold the pack's LATERAL spread does not
    /// care how long the lane is, so it can sit close from the first frame and
    /// stay close for the whole race.
    ///
    /// Test-scene harness only — not used in training or the game loop.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class Systems_RaceCamera : MonoBehaviour
    {
        // Head-height fraction of the start pose below which a racer counts as
        // down. Matches Systems_WalkContest's fall rule.
        private const float STANDING_HEAD_FRACTION = 0.45f;

        [SerializeField] private Vector3 _goalDirection = Vector3.forward;
        /// <summary>
        /// Where the finish line sits, as a projection along
        /// <see cref="_goalDirection"/>. Recorded for diagnostics and for
        /// anything that wants to reason about the lane; the shot itself is
        /// framed on the pack, not on the tape.
        ///
        /// Pinning the camera AT the tape was tried and reverted. It does put
        /// the finish line on screen, but only by sitting closer to the pack
        /// than the framing solve asks for, and the first thing that costs is
        /// the outer two lanes: at the gun the field is in a T-pose 4.9 m across
        /// and the pinned shot holds 4.0 m of it, so Bot and Standard were
        /// clipped in half by the frame edges. Made safe against that it stops
        /// binding at all, and made to bind it walks the camera to within a
        /// metre of the pack by the time anyone finishes. A race whose outer
        /// racers are cut off is worse than one whose tape is off-camera, and
        /// the plates carry the distance to the metre either way.
        /// </summary>
        [SerializeField] private float _goalProjection = 2.8f;
        // What the shot must hold across the lane, at its widest: the field is
        // four racers at 1.1 m spacing = 3.3 m centre to centre, plus a body
        // width of margin each side. Anything tighter loses the outside lanes
        // the moment a racer strays; anything wider throws away frame on empty
        // ground.
        [SerializeField] private float _maxFrameWidth = 4.4f;
        /// <summary>
        /// Floor on the frame width, which is what the shot tightens to once the
        /// field has thinned.
        ///
        /// A race loses racers as it goes, and holding the full four-lane width
        /// for the one still upright wastes most of the picture: measured
        /// 2026-08-22, a round with only the Bot still walking framed 4.4 m of
        /// empty grey around a fighter a fifth of the frame tall. Sized to the
        /// racers actually left, the last survivor gets a close shot for free and
        /// the end of a round is the most watchable part of it rather than the
        /// emptiest.
        /// </summary>
        [SerializeField] private float _minFrameWidth = 2.4f;
        /// <summary>Clearance either side of the outermost racer still in the race.</summary>
        [SerializeField] private float _frameSideMargin = 0.55f;
        // Only ever the binding axis on a landscape editor window, but it has
        // to be right there too: a standing fighter plus headroom.
        [SerializeField] private float _frameHeight = 3.2f;
        [SerializeField] private float _fieldOfView = 60f;
        // Eye height above the lane, and what it looks at. Low and near-level:
        // the walk race has no ropes to clear (that constraint belongs to the
        // balance ring), and a flat angle is what makes a walking figure read
        // as walking rather than as a shape sliding around on a floor.
        [SerializeField] private float _cameraHeight = 1.6f;
        [SerializeField] private float _lookHeight = 0.95f;
        [SerializeField] private float _minFollowDistance = 3.2f;
        // The pack is framed a little below centre so the ground it walks on
        // fills the bottom of a tall frame instead of the sky filling the top.
        [SerializeField] private float _lookLift = -0.15f;
        [SerializeField] private float _positionSmoothTime = 0.45f;
        [SerializeField] private float _lookSmoothTime = 0.3f;

        private Camera _camera;
        private Systems_FighterRig[] _rigs;
        private float[] _startHeadHeights;
        private float _groundY;
        private Vector3 _lookPoint;
        private Vector3 _lookVelocity;
        private Vector3 _positionVelocity;
        private bool _ready;

        // Called by the walk contest scene builder.
        public void EditorInitialize(Vector3 goalDirection, float goalProjection)
        {
            _goalDirection = goalDirection.normalized;
            _goalProjection = goalProjection;
        }

        private void Awake()
        {
            _camera = GetComponent<Camera>();
            _camera.fieldOfView = _fieldOfView;
        }

        private void Start()
        {
            Bind();
        }

        /// <summary>
        /// Finds the racers. Deferred rather than done once in Start, because
        /// this camera is always active while the spawner creates the pack a
        /// frame or two later — binding to an empty scene once and giving up is
        /// how the old camera would have behaved had it tracked anything.
        /// </summary>
        private void Bind()
        {
            _rigs = FindObjectsByType<Systems_FighterRig>(FindObjectsSortMode.InstanceID);
            if (_rigs.Length == 0)
            {
                return;
            }
            _goalDirection = _goalDirection.normalized;
            _startHeadHeights = new float[_rigs.Length];
            for (int rigIndex = 0; rigIndex < _rigs.Length; rigIndex++)
            {
                _startHeadHeights[rigIndex] = _rigs[rigIndex].Head.position.y;
            }
            _groundY = _rigs[0].GroundY;
            _lookPoint = PackCentroid() + Vector3.up * _lookHeight;
            transform.position = SolvePosition(PackCentroid());
            _ready = true;
        }

        private void LateUpdate()
        {
            if (!_ready)
            {
                Bind();
                return;
            }

            // Unscaled, and passed to SmoothDamp explicitly: the round countdown
            // holds Time.timeScale at 0 and the winner banner dips it to 0.5, and
            // a spectator camera that stops moving whenever the game pauses gets
            // stuck mid-transition exactly when the player is looking at it.
            float dt = Time.unscaledDeltaTime;

            Vector3 pack = PackCentroid();
            Vector3 desiredPosition = SolvePosition(pack);
            transform.position = Vector3.SmoothDamp(
                transform.position, desiredPosition, ref _positionVelocity, _positionSmoothTime,
                Mathf.Infinity, dt);

            Vector3 lookTarget = pack;
            lookTarget.y = _groundY + _lookHeight + _lookLift;
            _lookPoint = Vector3.SmoothDamp(_lookPoint, lookTarget, ref _lookVelocity, _lookSmoothTime,
                Mathf.Infinity, dt);
            transform.rotation = Quaternion.LookRotation(_lookPoint - transform.position, Vector3.up);
        }

        /// <summary>
        /// Ahead of the pack along the goal direction, at the distance that
        /// frames <see cref="LateralFrameWidth"/> across at this window's aspect. The
        /// lateral offset follows the pack so a field that drifts off the lane
        /// centre does not walk out of the side of the shot.
        /// </summary>
        private Vector3 SolvePosition(Vector3 pack)
        {
            float distance = Systems_CameraFraming.DistanceToFrame(
                _camera, LateralFrameWidth(), _frameHeight, _fieldOfView, _minFollowDistance);
            Vector3 position = pack + _goalDirection * distance;
            position.y = _groundY + _cameraHeight;
            return position;
        }

        /// <summary>
        /// How wide the shot has to be to hold the racers still upright, across
        /// the lane. Measured perpendicular to the goal direction, because that
        /// is the axis the frame is short of; distance down the lane costs the
        /// framing nothing.
        ///
        /// Falls back to the full field once everyone is down, so the round's
        /// closing tableau is not a close-up of whoever happened to land last.
        /// </summary>
        private float LateralFrameWidth()
        {
            Vector3 lateral = Vector3.Cross(Vector3.up, _goalDirection).normalized;
            float min = float.MaxValue;
            float max = float.MinValue;
            int counted = 0;
            for (int rigIndex = 0; rigIndex < _rigs.Length; rigIndex++)
            {
                if (_rigs[rigIndex].Head.position.y <= _startHeadHeights[rigIndex] * STANDING_HEAD_FRACTION)
                {
                    continue;
                }
                float offset = Vector3.Dot(_rigs[rigIndex].Pelvis.position, lateral);
                min = Mathf.Min(min, offset);
                max = Mathf.Max(max, offset);
                counted++;
            }
            if (counted == 0)
            {
                return _maxFrameWidth;
            }
            return Mathf.Clamp(max - min + _frameSideMargin * 2f, _minFrameWidth, _maxFrameWidth);
        }

        /// <summary>
        /// Centre of the racers still upright, falling back to the whole field
        /// once everyone is down — a round ends on a mat full of fallen racers
        /// and that final tableau is what the winner banner plays over, so it
        /// has to stay framed rather than snapping to whoever is left.
        /// </summary>
        private Vector3 PackCentroid()
        {
            Vector3 standingSum = Vector3.zero;
            Vector3 allSum = Vector3.zero;
            int standingCount = 0;
            for (int rigIndex = 0; rigIndex < _rigs.Length; rigIndex++)
            {
                Vector3 pelvis = _rigs[rigIndex].Pelvis.position;
                allSum += pelvis;
                if (_rigs[rigIndex].Head.position.y > _startHeadHeights[rigIndex] * STANDING_HEAD_FRACTION)
                {
                    standingSum += pelvis;
                    standingCount++;
                }
            }
            return standingCount > 0 ? standingSum / standingCount : allSum / _rigs.Length;
        }
    }
}
