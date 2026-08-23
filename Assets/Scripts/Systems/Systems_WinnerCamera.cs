using Unity.Cinemachine;
using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Cinemachine winner shot: when a round ends, a virtual camera slowly
    /// orbits the winner while the drama camera stands down; when the next
    /// round starts, control returns to the drama camera. The Cinemachine
    /// brain only drives the camera while the virtual camera is live.
    /// Test-scene harness only.
    /// </summary>
    public sealed class Systems_WinnerCamera : MonoBehaviour
    {
        private const float ORBIT_DEGREES_PER_SECOND = 30f;
        /// <summary>
        /// HORIZONTAL orbit radius. Deliberately small, and deliberately not the
        /// thing that sets the shot's distance — see
        /// <see cref="ORBIT_HEIGHT_ABOVE_FLOOR"/>.
        /// </summary>
        private const float ORBIT_RADIUS = 2.4f;
        /// <summary>
        /// Eye height ABOVE THE RING FLOOR, not above the fighter it is
        /// watching — and the number that actually sets how big the winner is
        /// in frame.
        ///
        /// Two separate bugs met here. First, this used to be added to the
        /// winner's PELVIS, and a winner is usually lying down by the time this
        /// shot plays, so the lens ended up around 2.5 m — inside the ring's rope
        /// band (1.05 to 2.85 m) — looking out through the ropes at the crowd
        /// stands with the fighter off the bottom of the frame.
        ///
        /// Second, once it was pointing at the winner the shot was far too
        /// tight: at 9:16 a 55 degree vertical FOV is only about 32 degrees
        /// horizontal, so holding the ~2.6 m a sprawled fighter occupies needs
        /// roughly 4.5 m of distance, and the orbit was 2.2 m out. Measured
        /// 2026-08-22, the round-2 winner shot was Grandma's torso filling the
        /// entire frame with her head cropped off the side.
        ///
        /// A 6.1 m ring cannot give 4.5 m of HORIZONTAL radius without putting
        /// the camera through the ropes, so the distance is taken vertically
        /// instead: 2.4 m out and 4.2 m up is 4.6 m from a fallen fighter and
        /// 4.1 m from a standing one, both of which frame a whole body, while
        /// the footprint stays comfortably inside the ring. It reads as the shot
        /// a referee standing over the winner would have.
        /// </summary>
        private const float ORBIT_HEIGHT_ABOVE_FLOOR = 4.2f;
        /// <summary>
        /// Vertical FOV for the winner shot. Explicit because the default is
        /// tuned for landscape: at 9:16 the horizontal FOV is only 0.5625 of
        /// this, and the shot has to hold a fighter who may be lying down.
        /// </summary>
        private const float ORBIT_FOV = 55f;
        /// <summary>
        /// How far from the ring centre the camera may get before it is pulled
        /// back in. The canvas is 6.1 m square, so the ropes are at 3.05 m; this
        /// keeps the lens a comfortable margin inside them.
        ///
        /// Without it the orbit simply walked out of the ring. The winner can be
        /// standing 2.1 m off centre and the orbit adds another 2.2 m on top, so
        /// for most of every revolution the camera was outside the ropes with a
        /// corner post or the crowd stand filling the frame — measured
        /// 2026-08-22, the round-1 winner shot came back as a grey pillar and
        /// six unlit crowd blocks, with the fighter nowhere in it.
        /// </summary>
        private const float MAX_ORBIT_DISTANCE_FROM_CENTRE = 2.7f;
        /// <summary>
        /// Where the orbit starts, as a compass bearing in the orbit's own
        /// parametrisation: 90 degrees puts the camera on +Z, the side the
        /// fighters face and the side the drama camera shoots from. Starting at
        /// 0 put the first — and most-watched — second of the shot side-on.
        /// </summary>
        private const float ORBIT_START_DEGREES = 90f;
        /// <summary>
        /// Aim just above the winner's pelvis. Small, because the shot now looks
        /// steeply DOWN: lifting the aim point on a top-down shot pushes the
        /// subject toward the bottom of the frame rather than raising it.
        /// </summary>
        private const float LOOK_LIFT = 0.1f;

        [SerializeField] private CinemachineCamera _virtualCamera;
        [SerializeField] private Systems_DramaCamera _dramaCamera;

        private Systems_ContestReferee _contest;
        private Systems_FighterRig[] _rigs;
        private Transform _focus;
        private float _angleDegrees;
        private Vector3 _ringCentre;
        private float _groundY;
        private bool _active;

        // Called by the editor scene tool.
        public void EditorInitialize(CinemachineCamera virtualCamera, Systems_DramaCamera dramaCamera)
        {
            _virtualCamera = virtualCamera;
            _dramaCamera = dramaCamera;
        }

        private void Start()
        {
            _rigs = FindObjectsByType<Systems_FighterRig>(FindObjectsSortMode.InstanceID);
            // Both sampled while everyone is still on their spawn mark and
            // upright — see RingCentre.
            _ringCentre = RingCentre();
            _groundY = _rigs.Length > 0 ? _rigs[0].GroundY : 0f;
            _contest = FindFirstObjectByType<Systems_ContestReferee>();
            if (_contest != null)
            {
                _contest.RoundEnded += OnRoundEnded;
                _contest.RoundStarted += OnRoundStarted;
            }
            if (_virtualCamera != null)
            {
                _virtualCamera.gameObject.SetActive(false);
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
            _focus = null;
            for (int rigIndex = 0; rigIndex < _rigs.Length; rigIndex++)
            {
                // Matched on the recorded identity, not on name.Contains: a ring
                // holding "Standard" and "Standard2" made that test true for both
                // and celebrated whichever came first in the array.
                Systems_FighterIdentity.Resolve(_rigs[rigIndex], out string displayName, out _);
                if (displayName == winnerName)
                {
                    _focus = _rigs[rigIndex].Pelvis.transform;
                    break;
                }
            }
            if (_focus == null || _virtualCamera == null)
            {
                return;
            }
            _active = true;
            _angleDegrees = ORBIT_START_DEGREES;
            _virtualCamera.Lens.FieldOfView = ORBIT_FOV;
            if (_dramaCamera != null)
            {
                _dramaCamera.enabled = false;
            }
            // Assigned for the inspector's benefit and in case an aim component
            // is ever added; LateUpdate is what actually points the camera.
            _virtualCamera.LookAt = _focus;
            _virtualCamera.gameObject.SetActive(true);
        }

        private void OnRoundStarted(int round)
        {
            _active = false;
            if (_virtualCamera != null)
            {
                _virtualCamera.gameObject.SetActive(false);
            }
            if (_dramaCamera != null)
            {
                _dramaCamera.enabled = true;
            }
        }

        private void LateUpdate()
        {
            if (!_active || _focus == null || _virtualCamera == null)
            {
                return;
            }
            // Unscaled: the winner shot plays through the knockout slow-mo.
            _angleDegrees += ORBIT_DEGREES_PER_SECOND * Time.unscaledDeltaTime;
            float radians = _angleDegrees * Mathf.Deg2Rad;
            Vector3 offset = new Vector3(Mathf.Cos(radians), 0f, Mathf.Sin(radians)) * ORBIT_RADIUS;
            Vector3 position = _focus.position + offset;
            position.y = _groundY + ORBIT_HEIGHT_ABOVE_FLOOR;

            // Reeled back in toward the ring centre when the orbit would take it
            // through the ropes. Clamping the RADIUS rather than refusing to move
            // keeps the shot going round — it just tightens on the near side
            // instead of stepping outside.
            Vector3 fromCentre = position - _ringCentre;
            fromCentre.y = 0f;
            float distance = fromCentre.magnitude;
            if (distance > MAX_ORBIT_DISTANCE_FROM_CENTRE)
            {
                Vector3 pulled = _ringCentre + fromCentre * (MAX_ORBIT_DISTANCE_FROM_CENTRE / distance);
                position = new Vector3(pulled.x, position.y, pulled.z);
            }

            // Rotation is driven here, not left to LookAt.
            //
            // This CinemachineCamera carries NO procedural aim component — the
            // scene tool adds a bare CinemachineCamera and nothing else — and in
            // Cinemachine 3 that means the vcam simply publishes its own
            // transform. LookAt is only ever read BY an aim component, so with
            // none present it did nothing at all and the shot kept the identity
            // rotation it was created with: pointing along +Z at the far crowd
            // stand, whatever the position said. Measured 2026-08-22, every
            // round-winner shot in the game was six unlit crowd blocks with the
            // winner off-camera behind the lens.
            Vector3 lookTarget = _focus.position + Vector3.up * LOOK_LIFT;
            _virtualCamera.transform.SetPositionAndRotation(
                position, Quaternion.LookRotation(lookTarget - position, Vector3.up));
        }

        /// <summary>
        /// Centre of the ring, taken from where the fighters SPAWN rather than
        /// from a constant, so this holds for any arena the harness is pointed
        /// at.
        ///
        /// Sampled in Start and never again. Sampling it per round — at round
        /// END, which is when this shot runs — averages the positions of eight
        /// fighters who have just fallen over and scattered, so the "centre" it
        /// found drifted toward whichever side the pile-up happened on and took
        /// the clamp below with it, straight back out through the ropes.
        /// </summary>
        private Vector3 RingCentre()
        {
            if (_rigs == null || _rigs.Length == 0)
            {
                return Vector3.zero;
            }
            Vector3 sum = Vector3.zero;
            for (int rigIndex = 0; rigIndex < _rigs.Length; rigIndex++)
            {
                sum += _rigs[rigIndex].Pelvis.position;
            }
            return sum / _rigs.Length;
        }
    }
}
