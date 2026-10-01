using UnityEngine;
using UnityEngine.Rendering;
using Unity.Cinemachine;
using PoBox.Fx;
using PoBox.Sim;

namespace PoBox.Broadcast
{
    public enum Shot { Wide, Orbit, HighCorner, LowRopes, ShoulderRed, ShoulderBlue, Impact, Overhead, Replay }

    /// <summary>
    /// The vision mixer. Nine Cinemachine cameras live in the scene; this decides which one is on air and
    /// where each of them stands.
    ///
    /// Which: an ambient rotation whose pace follows the excitement reading (long holds on the wide when
    /// nothing is landing, short ones and over-the-shoulder shots when it is), interrupted by the things
    /// that must be seen: a clean heavy hit cuts to a close-up of whoever took it and slows time for half a
    /// second, a knockdown goes to the close-up and then overhead for the count, and a replay takes the
    /// orbiting replay camera. Everything is a hard cut, as on a broadcast.
    ///
    /// Where: the game is portrait, and a lens angle is vertical, so a two-shot is framed from the
    /// <i>horizontal</i> angle the screen actually has. Each camera's distance is solved from how far apart
    /// the fighters are and the live aspect, every frame. The outside cameras sit above the top rope so the
    /// ropes do not cross the picture.
    /// </summary>
    public class BroadcastDirector : MonoBehaviour
    {
        public static BroadcastDirector Instance { get; private set; }

        public Bout bout;
        public Camera mainCamera;
        public CinemachineBrain brain;
        public FighterSkin redLive, blueLive, redPuppet, bluePuppet;
        public Volume replayVolume;

        [Header("Shots")]
        public CinemachineCamera wideCam;
        public CinemachineCamera orbitCam;
        public CinemachineCamera cornerCam;
        public CinemachineCamera lowCam;
        public CinemachineCamera shoulderRedCam;
        public CinemachineCamera shoulderBlueCam;
        public CinemachineCamera impactCam;
        public CinemachineCamera overheadCam;
        public CinemachineCamera replayCam;

        [Header("Framing")]
        public Vector3 ringCentre;
        public float ringHalf = 3.05f;
        [Tooltip("Height of the outside cameras: above the top rope's sight line.")]
        public float broadcastHeight = 3.0f;
        public float orbitDegreesPerSecond = 9f;
        [Tooltip("Half the width a two-shot keeps clear either side of the fighters, metres.")]
        public float sideRoom = 0.75f;
        [Tooltip("Furthest a camera backs off to hold both fighters, metres.")]
        public float maxDistance = 9.5f;
        [Tooltip("Ceiling for the outside cameras, metres above the canvas.")]
        public float maxHeight = 7.5f;

        [Header("Events")]
        [Tooltip("Impulse of a clean hit that earns the close-up and the slow motion, N s.")]
        public float bigHit = 13f;
        public float impactHold = 1.15f;
        public bool slowMotion = true;
        public float slowScale = 0.3f;
        public float slowSeconds = 0.5f;
        [Tooltip("Camera kick for an impulse of 25 N s, m/s.")]
        public float shakeVelocity = 0.35f;

        public Shot Current { get; private set; } = Shot.Wide;
        /// <summary>A shot chosen by hand in the menu. Null hands the cameras back to the director.</summary>
        public Shot? Pinned { get; private set; }
        public int CutCount { get; private set; }

        // The part of the screen the picture is actually seen through, as fractions of its height from
        // the top: the HUD's scoreboard covers what is above, its dock or the results card what is below.
        float _bandTop = 0.23f, _bandBottom = 0.81f, _bandTopWanted = 0.23f, _bandBottomWanted = 0.81f;
        float Band => Mathf.Clamp(_bandBottom - _bandTop, 0.2f, 1f);
        // Tipping a camera down by this fraction of its lens angle lifts the subject to the band's middle.
        float Bias => 0.5f - (_bandTop + _bandBottom) * 0.5f;

        /// <summary>
        /// Tells the cameras which band of the screen is clear of interface. They frame the fighters to
        /// fit it and aim so the action sits in its middle, so opening the results card over the lower
        /// half of the screen pushes the replay up into what is left instead of hiding it.
        /// </summary>
        public void SetPictureBand(float top01, float bottom01)
        {
            _bandTopWanted = Mathf.Clamp01(top01);
            _bandBottomWanted = Mathf.Clamp(bottom01, _bandTopWanted + 0.1f, 1f);
        }

        CinemachineImpulseSource _impulse;
        Fighter _initiative, _impactVictim;
        Shot _forced;
        float _forcedUntil, _nextCut, _lastImpactCut = -99f, _orbitAngle, _sepSmooth = 1.2f, _sepVelocity;
        Vector3 _mid, _midVelocity, _side = Vector3.back;
        bool _hasMid;

        void Awake()
        {
            Instance = this;
            _impulse = gameObject.AddComponent<CinemachineImpulseSource>();
            CinemachineImpulseDefinition def = _impulse.ImpulseDefinition;
            // Dissipating, not the default Legacy, which needs a signal asset and is silent without one.
            def.ImpulseType = CinemachineImpulseDefinition.ImpulseTypes.Dissipating;
            def.ImpulseShape = CinemachineImpulseDefinition.ImpulseShapes.Bump;
            def.ImpulseDuration = 0.22f;
            def.DissipationDistance = 40f;
            def.DissipationRate = 0.25f;
            def.ImpulseChannel = 1;
            CinemachineImpulseManager.Instance.IgnoreTimeScale = true;

            if (brain != null)
            {
                brain.IgnoreTimeScale = true;
                brain.DefaultBlend = new CinemachineBlendDefinition(CinemachineBlendDefinition.Styles.Cut, 0f);
            }
        }

        void OnEnable()
        {
            SimBus.Hit += OnHit;
            SimBus.Knockdown += OnKnockdown;
            SimBus.PunchThrown += OnPunch;
        }

        void OnDisable()
        {
            SimBus.Hit -= OnHit;
            SimBus.Knockdown -= OnKnockdown;
            SimBus.PunchThrown -= OnPunch;
            if (Instance == this) Instance = null;
        }

        void Start() => Apply(Shot.Orbit);

        // ------------------------------------------------------------ events

        void OnPunch(Fighter f, PunchType type, int hand) => _initiative = f;

        void OnHit(HitEvent e)
        {
            _initiative = e.attacker;
            float strength = Mathf.Clamp01(e.impulse / 25f);
            if (e.impulse > 3f && _impulse != null)
                _impulse.GenerateImpulseAtPositionWithVelocity(e.point, -e.normal * (strength * shakeVelocity));

            if (!e.clean || e.impulse < bigHit || e.victim == null || e.victim.IsDown) return;
            if (Time.unscaledTime - _lastImpactCut < 2.5f) return;
            _lastImpactCut = Time.unscaledTime;
            _impactVictim = e.victim;
            Force(Shot.Impact, impactHold);
            if (bout != null && slowMotion) bout.SlowMo(slowScale, slowSeconds);
        }

        void OnKnockdown(Fighter f, HitEvent cause)
        {
            _lastImpactCut = Time.unscaledTime;
            _impactVictim = f;
            Force(Shot.Impact, 1.3f);
            if (bout != null && slowMotion) bout.SlowMo(0.25f, 1.1f);
            if (_impulse != null) _impulse.GenerateImpulseAtPositionWithVelocity(f.pelvis.transform.position, Vector3.down * shakeVelocity);
        }

        void Force(Shot shot, float realSeconds)
        {
            _forced = shot;
            _forcedUntil = Time.unscaledTime + realSeconds;
        }

        /// <summary>Holds one shot until told otherwise. Null gives the cameras back.</summary>
        public void Pin(Shot? shot)
        {
            Pinned = shot;
            _nextCut = 0f;
        }

        // ------------------------------------------------------------ choosing

        void Update()
        {
            if (bout == null || redLive == null || blueLive == null) return;
            float now = Time.unscaledTime;
            bool replay = ReplaySystem.Instance != null && ReplaySystem.Instance.Playing;

            Shot want = Current;
            if (replay) want = Shot.Replay;
            else if (Pinned.HasValue) want = Pinned.Value;
            else if (now < _forcedUntil) want = _forced;
            else if (bout.Phase == BoutPhase.Count && bout.Downed != null) { want = Shot.Overhead; _impactVictim = bout.Downed; }
            else if (bout.Phase != BoutPhase.Fight) want = Shot.Orbit;
            else if (now >= _nextCut || Current == Shot.Impact || Current == Shot.Overhead || Current == Shot.Replay)
            {
                want = PickAmbient(out float hold);
                _nextCut = now + hold;
            }

            if (want != Current) Apply(want);
            PlaceAll(replay);

            if (replayVolume != null)
            {
                bool cinematic = replay || bout.SlowMotion;
                replayVolume.weight = Mathf.MoveTowards(replayVolume.weight, cinematic ? 1f : 0f, Time.unscaledDeltaTime * 5f);
            }
        }

        Shot PickAmbient(out float hold)
        {
            float e = Excitement.Value;
            Shot shoulder = _initiative != null && _initiative == bout.blue ? Shot.ShoulderBlue : Shot.ShoulderRed;
            Shot pick;
            float r = Random.value;

            if (e < 0.3f)
            {
                hold = Random.Range(5f, 8f);
                pick = r < 0.4f ? Shot.Wide : r < 0.65f ? Shot.Orbit : r < 0.85f ? Shot.HighCorner : Shot.LowRopes;
            }
            else if (e < 0.65f)
            {
                hold = Random.Range(3.5f, 5f);
                pick = r < 0.4f ? Shot.Wide : r < 0.7f ? shoulder : r < 0.85f ? Shot.LowRopes : Shot.Orbit;
            }
            else
            {
                hold = Random.Range(2.2f, 3.2f);
                pick = r < 0.5f ? Shot.Wide : r < 0.8f ? shoulder : Shot.LowRopes;
            }

            // Never "cut" to the shot that is already on air: it reads as a jump.
            if (pick == Current) pick = Current == Shot.Wide ? shoulder : Shot.Wide;
            return pick;
        }

        void Apply(Shot shot)
        {
            // Cutting to the wide is the moment to choose which side of the ring it shoots from: the side
            // with more canvas between the fighters and the ropes, so the ropes stay out of the picture.
            // It is only ever changed on a cut; on air, the side follows the fighters round.
            if (shot == Shot.Wide && _hasMid && RopeRoom(_mid, -_side) > RopeRoom(_mid, _side) * 1.25f) _side = -_side;

            Current = shot;
            CutCount++;
            Set(wideCam, Shot.Wide); Set(orbitCam, Shot.Orbit); Set(cornerCam, Shot.HighCorner);
            Set(lowCam, Shot.LowRopes); Set(shoulderRedCam, Shot.ShoulderRed); Set(shoulderBlueCam, Shot.ShoulderBlue);
            Set(impactCam, Shot.Impact); Set(overheadCam, Shot.Overhead); Set(replayCam, Shot.Replay);
        }

        void Set(CinemachineCamera cam, Shot shot)
        {
            if (cam != null) cam.Priority = Current == shot ? 30 : 10;
        }

        // ------------------------------------------------------------ placing

        void PlaceAll(bool replay)
        {
            FighterSkin red = replay && redPuppet != null ? redPuppet : redLive;
            FighterSkin blue = replay && bluePuppet != null ? bluePuppet : blueLive;

            Vector3 rc = red.ChestPoint, bc = blue.ChestPoint;
            Vector3 mid = (rc + bc) * 0.5f;
            mid.y = Mathf.Max(0.55f, mid.y - 0.2f);
            Vector3 line = bc - rc;
            line.y = 0f;
            float sep = line.magnitude;
            Vector3 dir = sep > 0.05f ? line / sep : Vector3.right;

            float dt = Time.unscaledDeltaTime;
            _bandTop = Mathf.MoveTowards(_bandTop, _bandTopWanted, dt * 1.5f);
            _bandBottom = Mathf.MoveTowards(_bandBottom, _bandBottomWanted, dt * 1.5f);
            if (!_hasMid) { _mid = mid; _sepSmooth = sep; _hasMid = true; }
            _mid = Vector3.SmoothDamp(_mid, mid, ref _midVelocity, 0.25f, 50f, dt);
            _sepSmooth = Mathf.SmoothDamp(_sepSmooth, sep, ref _sepVelocity, 0.4f, 50f, dt);

            // The side-on camera stays on one side of the line between them. The side follows the line
            // round as they circle, slowly, rather than flipping when they swap places.
            Vector3 side = Vector3.Cross(Vector3.up, dir);
            if (Vector3.Dot(side, _side) < 0f) side = -side;
            _side = Vector3.Slerp(_side, side, Mathf.Clamp01(dt * 1.5f)).normalized;

            float aspect = mainCamera != null ? mainCamera.aspect : 0.5625f;
            float halfWidth = _sepSmooth * 0.5f + sideRoom;

            // Wide: side on, from above the top rope.
            if (wideCam != null)
            {
                float d = TwoShotDistance(wideCam, aspect, halfWidth, 1.25f);
                Place(wideCam, Raise(_mid + _side * d, OverTheRopes(_side, d, 0f)), _mid);
            }

            // Orbit: the same framing, walking round the ring.
            if (orbitCam != null)
            {
                _orbitAngle += orbitDegreesPerSecond * dt;
                Vector3 around = Quaternion.AngleAxis(_orbitAngle, Vector3.up) * Vector3.forward;
                float d = TwoShotDistance(orbitCam, aspect, halfWidth + 0.2f, 1.3f);
                Place(orbitCam, Raise(_mid + around * d, OverTheRopes(around, d, 0.3f)), _mid);
            }

            // High corner: fixed bearing from the red corner's side of the hall, looking down the ring.
            if (cornerCam != null)
            {
                Vector3 bearing = new Vector3(-0.72f, 0f, -0.69f).normalized;
                float d = TwoShotDistance(cornerCam, aspect, halfWidth + 0.5f, 1.4f);
                Place(cornerCam, Raise(_mid + bearing * d, OverTheRopes(bearing, d, 1.0f)), _mid);
            }

            // Low: inside the ropes, near the canvas, a wide lens looking up at them.
            if (lowCam != null)
            {
                float d = TwoShotDistance(lowCam, aspect, halfWidth, 1.2f);
                Vector3 p = Inside(_mid - _side * d, 0.25f);
                p.y = ringCentre.y + 0.45f;
                Place(lowCam, p, _mid + Vector3.up * 0.15f);
            }

            PlaceShoulder(shoulderRedCam, red, blue, aspect, halfWidth);
            PlaceShoulder(shoulderBlueCam, blue, red, aspect, halfWidth);

            // Impact: tight on whoever took it.
            FighterSkin victim = _impactVictim != null && bout != null && _impactVictim == bout.blue ? blue : red;
            if (impactCam != null)
            {
                // Head and body in the clear band of a portrait screen needs about three metres on this
                // lens; closer and the screen is one shoulder.
                Vector3 head = victim.HeadPoint;
                const float impactDistance = 3.2f;
                Vector3 p = head + _side * impactDistance;
                bool outside = Mathf.Abs(p.x - ringCentre.x) > ringHalf - 0.2f || Mathf.Abs(p.z - ringCentre.z) > ringHalf - 0.2f;
                p.y = outside ? OverTheRopes(_side, impactDistance, 0f) : head.y + 0.2f;
                Place(impactCam, p, Vector3.Lerp(head, victim.PelvisPoint, 0.4f));
            }

            // Overhead: straight down on the fighter on the canvas.
            if (overheadCam != null)
            {
                Vector3 centre = Vector3.Lerp(victim.PelvisPoint, victim.ChestPoint, 0.5f);
                Vector3 p = centre + Vector3.up * 5.2f - dir * 0.6f;
                overheadCam.transform.SetPositionAndRotation(p, Quaternion.LookRotation(centre - p, dir));
            }

            // Replay: a slow walk round the moment itself.
            if (replayCam != null)
            {
                Vector3 focus = _mid;
                ReplaySystem rs = ReplaySystem.Instance;
                float turn = 0f;
                if (replay && rs.Current != null)
                {
                    focus = Vector3.Lerp(_mid, rs.Current.focus, 0.6f);
                    turn = (rs.Position - 0.5f) * 70f + rs.ClipIndex * 55f;
                }
                Vector3 around = Quaternion.AngleAxis(turn, Vector3.up) * _side;
                float d = TwoShotDistance(replayCam, aspect, Mathf.Min(halfWidth, 1.1f), 1.1f);
                Vector3 p = focus + around * d;
                // In the ring it shoots from chest height; once it has to stand outside, it goes up over the ropes.
                bool outside = Mathf.Abs(p.x - ringCentre.x) > ringHalf - 0.2f || Mathf.Abs(p.z - ringCentre.z) > ringHalf - 0.2f;
                p.y = outside ? OverTheRopes(around, d, 0f) : ringCentre.y + 1.55f;
                Place(replayCam, p, new Vector3(focus.x, ringCentre.y + 1.0f, focus.z));
            }
        }

        /// <summary>
        /// The shot that favours one fighter: from behind and to one side of them, about fifty degrees
        /// round from the side-on wide, so they are the nearer figure and the other is seen over their
        /// shoulder. A true over-the-shoulder from a metre and a half does not work in portrait: the near
        /// fighter's back is half the screen.
        /// </summary>
        void PlaceShoulder(CinemachineCamera cam, FighterSkin self, FighterSkin other, float aspect, float halfWidth)
        {
            if (cam == null) return;
            Vector3 to = other.ChestPoint - self.ChestPoint;
            to.y = 0f;
            Vector3 dir = to.sqrMagnitude > 0.01f ? to.normalized : Vector3.forward;
            Vector3 from = (-dir * 0.77f + _side * 0.64f).normalized;
            // Seen from this angle the pair is narrower than side on.
            float d = TwoShotDistance(cam, aspect, halfWidth * 0.8f, 1.25f);
            Vector3 p = _mid + from * d;
            bool outside = Mathf.Abs(p.x - ringCentre.x) > ringHalf - 0.2f || Mathf.Abs(p.z - ringCentre.z) > ringHalf - 0.2f;
            p.y = outside ? OverTheRopes(from, d, 0f) : ringCentre.y + 2.0f;
            Place(cam, p, _mid);
        }

        /// <summary>
        /// How far back a camera has to stand to hold a subject of the given half-width and half-height.
        /// The width is nearly always the one that binds in portrait.
        /// </summary>
        float TwoShotDistance(CinemachineCamera cam, float aspect, float halfWidth, float halfHeight)
        {
            float tanV = Mathf.Tan(cam.Lens.FieldOfView * 0.5f * Mathf.Deg2Rad);
            float tanH = tanV * aspect;
            // The height available is only the clear band of the screen, not all of it.
            float d = Mathf.Max(halfWidth / Mathf.Max(0.01f, tanH), halfHeight / Mathf.Max(0.01f, tanV * Band)) * 1.05f;
            // With the fighters in opposite corners a true two-shot would be shot from the back of the
            // hall. Past this distance, let the edges of the frame go instead.
            return Mathf.Min(d, maxDistance);
        }

        /// <summary>
        /// How high a camera outside the ring has to be for its line to the fighters' feet to clear the top
        /// rope. It depends on how much canvas there is between the fighters and the ropes on the camera's
        /// side: with plenty, a modest height does; with the fighters against those ropes it has to look
        /// almost straight down, and the ceiling stops it there.
        /// </summary>
        float OverTheRopes(Vector3 towardsCamera, float distance, float extra)
        {
            float room = RopeRoom(_mid, towardsCamera);
            float h = distance > room ? TopRope * 1.1f * distance / room : broadcastHeight;
            return ringCentre.y + Mathf.Clamp(h, broadcastHeight, maxHeight) + extra;
        }

        const float TopRope = 1.35f;

        /// <summary>Canvas between a point and the rope line, going in a horizontal direction.</summary>
        float RopeRoom(Vector3 from, Vector3 direction)
        {
            float x = from.x - ringCentre.x, z = from.z - ringCentre.z;
            float tx = direction.x > 1e-3f ? (ringHalf - x) / direction.x : direction.x < -1e-3f ? (-ringHalf - x) / direction.x : float.MaxValue;
            float tz = direction.z > 1e-3f ? (ringHalf - z) / direction.z : direction.z < -1e-3f ? (-ringHalf - z) / direction.z : float.MaxValue;
            return Mathf.Max(0.3f, Mathf.Min(tx, tz));
        }

        static Vector3 Raise(Vector3 p, float height) => new Vector3(p.x, height, p.z);

        /// <summary>Clamps a point to stay inside the ropes, for the cameras that live in the ring.</summary>
        Vector3 Inside(Vector3 p, float margin)
        {
            float limit = ringHalf - margin;
            p.x = Mathf.Clamp(p.x, ringCentre.x - limit, ringCentre.x + limit);
            p.z = Mathf.Clamp(p.z, ringCentre.z - limit, ringCentre.z + limit);
            return p;
        }

        void Place(CinemachineCamera cam, Vector3 position, Vector3 lookAt)
        {
            Vector3 forward = lookAt - position;
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            Quaternion aim = Quaternion.LookRotation(forward, Vector3.up);
            aim *= Quaternion.Euler(Bias * cam.Lens.FieldOfView, 0f, 0f);
            cam.transform.SetPositionAndRotation(position, aim);
        }
    }
}
