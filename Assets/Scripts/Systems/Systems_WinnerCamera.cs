using System.Collections.Generic;
using Unity.Cinemachine;
using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Cinemachine winner shot: when a round ends, a virtual camera slowly
    /// orbits the winner while the ordinary spectator camera stands down; when
    /// the next round starts, control returns to it. The Cinemachine brain only
    /// drives the camera while the virtual camera is live.
    /// Test-scene harness only.
    ///
    /// IT WORKS IN BOTH CONTESTS, and had to be taught one thing to do so. The
    /// ring assigns it a virtual camera and a drama camera in the scene; the walk
    /// race has neither, so it builds its own rig and stands down whichever
    /// spectator camera it finds — the race camera in the lane, the drama camera
    /// in the ring. The one genuinely ring-specific thing it did was clamp its
    /// orbit to stay inside the ropes, which in a lane would have dragged the
    /// shot back to the START LINE, five metres behind the racer it is supposed
    /// to be celebrating. The clamp is rope clearance; no ropes, no clamp.
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
        /// <summary>
        /// True when this arena has ropes to stay inside. False in the walk lane,
        /// where the clamp would pull the shot back down the track.
        /// </summary>
        private bool _clampInsideRopes;
        /// <summary>Spectator cameras this shot stood down, and what it owes them.</summary>
        private List<Behaviour> _suspended;
        /// <summary>The camera it drives, when it had to build its own rig.</summary>
        private Camera _drivenCamera;

        // Called by the editor scene tool.
        public void EditorInitialize(CinemachineCamera virtualCamera, Systems_DramaCamera dramaCamera)
        {
            _virtualCamera = virtualCamera;
            _dramaCamera = dramaCamera;
        }

        private void Start()
        {
            _rigs = FindObjectsByType<Systems_FighterRig>(FindObjectsInactive.Exclude);
            // Both sampled while everyone is still on their spawn mark and
            // upright — see RingCentre.
            _ringCentre = RingCentre();
            _groundY = _rigs.Length > 0 ? _rigs[0].GroundY : 0f;
            // Ropes are the only reason to clamp the orbit at all. Asked of the
            // scene rather than assumed, so the same shot works in the lane.
            _clampInsideRopes = FindAnyObjectByType<Systems_RingRopes>() != null;
            _contest = FindAnyObjectByType<Systems_ContestReferee>();
            if (_contest != null)
            {
                _contest.RoundEnded += OnRoundEnded;
                _contest.RoundStarted += OnRoundStarted;
            }
            EnsureVirtualCamera();
            if (_virtualCamera != null)
            {
                _virtualCamera.gameObject.SetActive(false);
            }
        }

        /// <summary>
        /// Builds the rig when the scene did not provide one, which is the walk
        /// race: no virtual camera, no brain, no drama camera — the race camera
        /// writes the transform directly. Built in code for the same reason
        /// <see cref="Systems_DramaCamera"/> builds its own, that a scene needing
        /// a new component is a scene that has to be regenerated, and these
        /// scenes are hand-authored (CLAUDE.md).
        /// </summary>
        private void EnsureVirtualCamera()
        {
            if (_virtualCamera != null)
            {
                return;
            }
            _drivenCamera = GetComponent<Camera>() != null ? GetComponent<Camera>() : Camera.main;
            if (_drivenCamera == null)
            {
                Debug.LogWarning($"{name}: no virtual camera assigned and no camera to drive — " +
                    "winners will be crowned with nothing to watch.");
                return;
            }
            if (_drivenCamera.GetComponent<CinemachineBrain>() == null)
            {
                _drivenCamera.gameObject.AddComponent<CinemachineBrain>();
            }
            var host = new GameObject("CM_WinnerCamera_Runtime");
            host.transform.SetParent(transform, false);
            _virtualCamera = host.AddComponent<CinemachineCamera>();
            _virtualCamera.Lens = LensSettings.Default;
            _virtualCamera.Lens.FieldOfView = ORBIT_FOV;
            // Above 0: the spectator cameras sit at -10 and -20 so this one
            // outranks them simply by being enabled, which is how the ring's
            // scene assigns it too.
            _virtualCamera.Priority = 0;
            host.AddComponent<CinemachineImpulseListener>();
        }

        /// <summary>
        /// Stands the ordinary spectator camera down for the winner shot and puts
        /// it back afterwards — WHICHEVER ONE THE SCENE HAS. The ring has a
        /// <see cref="Systems_DramaCamera"/> and the lane has a
        /// <see cref="Systems_RaceCamera"/>, and both write the transform of the
        /// camera the brain is driving. Two things steering one camera is a fight
        /// the winner shot loses on the frames it matters most.
        /// </summary>
        private void SuspendSpectators(bool suspend)
        {
            if (suspend)
            {
                if (_suspended != null)
                {
                    return;
                }
                _suspended = new List<Behaviour>();
                foreach (Systems_DramaCamera drama in FindObjectsByType<Systems_DramaCamera>(FindObjectsInactive.Exclude))
                {
                    if (drama.enabled && drama != _dramaCamera) { _suspended.Add(drama); }
                }
                foreach (Systems_RaceCamera race in FindObjectsByType<Systems_RaceCamera>(FindObjectsInactive.Exclude))
                {
                    if (race.enabled) { _suspended.Add(race); }
                }
                if (_dramaCamera != null && _dramaCamera.enabled)
                {
                    _suspended.Add(_dramaCamera);
                }
                for (int index = 0; index < _suspended.Count; index++)
                {
                    _suspended[index].enabled = false;
                }
                return;
            }
            if (_suspended == null)
            {
                return;
            }
            for (int index = 0; index < _suspended.Count; index++)
            {
                if (_suspended[index] != null) { _suspended[index].enabled = true; }
            }
            _suspended = null;
        }

        private void OnDestroy()
        {
            if (_contest != null)
            {
                _contest.RoundEnded -= OnRoundEnded;
                _contest.RoundStarted -= OnRoundStarted;
            }
            SuspendSpectators(false);
            if (_drivenCamera != null && _virtualCamera != null)
            {
                // Only the rig this built; an assigned camera belongs to the scene.
                Destroy(_virtualCamera.gameObject);
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
            if (_focus == null)
            {
                // A fighter that is not a PhysX rig cannot be found by a rig
                // sweep, and it can still win — the creature races in the lane.
                // Without this a round it won was crowned with no winner shot at
                // all, which reads as the camera having lost interest.
                foreach (MonoBehaviour behaviour in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude))
                {
                    if (behaviour is IContestFighter fighter
                        && string.Equals(fighter.DisplayName, winnerName, System.StringComparison.OrdinalIgnoreCase))
                    {
                        _focus = behaviour.transform;
                        break;
                    }
                }
            }
            if (_focus == null || _virtualCamera == null)
            {
                return;
            }
            _active = true;
            _angleDegrees = ORBIT_START_DEGREES;
            _virtualCamera.Lens.FieldOfView = ORBIT_FOV;
            SuspendSpectators(true);
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
            SuspendSpectators(false);
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
            //
            // ONLY WHERE THERE ARE ROPES. This clamp is rope clearance and
            // nothing else, and in the walk lane the "centre" it pulls toward is
            // the start line — so applying it there dragged the camera five
            // metres back down the track, away from the racer it was celebrating.
            if (_clampInsideRopes)
            {
                Vector3 fromCentre = position - _ringCentre;
                fromCentre.y = 0f;
                float distance = fromCentre.magnitude;
                if (distance > MAX_ORBIT_DISTANCE_FROM_CENTRE)
                {
                    Vector3 pulled = _ringCentre + fromCentre * (MAX_ORBIT_DISTANCE_FROM_CENTRE / distance);
                    position = new Vector3(pulled.x, position.y, pulled.z);
                }
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
