using UnityEngine;
using UnityEngine.Playables;
using Unity.Cinemachine;
using PoBox.Fx;
using PoBox.Sim;

namespace PoBox.Broadcast
{
    public enum Shot { Wide, Orbit, HighCorner, LowRopes, ShoulderRed, ShoulderBlue, Impact, Overhead, WalkRed, WalkBlue, Winner }

    /// <summary>
    /// The vision mixer. Eleven Cinemachine cameras live in the scene; this decides which one is on air and
    /// where each of them stands.
    ///
    /// Before the first bell there is the walk-on: a Timeline in the scene (Assets/Timeline/WalkOn.playable)
    /// cuts from one corner's close-up to the other's while its lighting tracks bring the house lights down
    /// and swing a follow-spot onto each fighter. This starts it and stays out of its way. After the last
    /// bell the winner gets a slow circling shot of their own.
    ///
    /// Which: an ambient rotation whose pace follows the excitement reading (long holds on the wide when
    /// nothing is landing, short ones and over-the-shoulder shots when it is), interrupted by the things
    /// that must be seen: a clean heavy hit cuts to a close-up of whoever took it, and a knockdown goes to
    /// the close-up and then overhead for the count. Everything is a hard cut, as on a broadcast.
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
        public FighterSkin redLive, blueLive;

        [Header("Shots")]
        public CinemachineCamera wideCam;
        public CinemachineCamera orbitCam;
        public CinemachineCamera cornerCam;
        public CinemachineCamera lowCam;
        public CinemachineCamera shoulderRedCam;
        public CinemachineCamera shoulderBlueCam;
        public CinemachineCamera impactCam;
        public CinemachineCamera overheadCam;
        [Tooltip("One fighter in its corner, for the walk-on. The Timeline cuts between them.")]
        public CinemachineCamera walkRedCam, walkBlueCam;
        [Tooltip("Circles whoever won.")]
        public CinemachineCamera winnerCam;
        [Tooltip("The walk-on sequence. Empty: the two corner shots are simply cut between.")]
        public PlayableDirector walkOn;
        [Tooltip("Not on air: it draws into a texture the HUD shows as an inset. On only during a count, on the boxer left standing.")]
        public Camera pipCam;

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
        [Tooltip("Impulse of a clean hit that earns the close-up, N s.")]
        public float bigHit = 13f;
        public float impactHold = 1.15f;
        [Tooltip("Camera kick for an impulse of 25 N s, m/s.")]
        public float shakeVelocity = 0.35f;

        public Shot Current { get; private set; } = Shot.Wide;
        /// <summary>A shot chosen by hand in the menu. Null hands the cameras back to the director.</summary>
        public Shot? Pinned { get; private set; }
        public int CutCount { get; private set; }
        /// <summary>The corner the close-up is locked on by hand: 0 red, 1 blue, -1 nobody.</summary>
        public int Focused => Pinned != Shot.Impact || bout == null ? -1 : _impactVictim == bout.blue ? 1 : 0;

        // The part of the screen the picture is actually seen through, as fractions of its height from
        // the top: the HUD's scoreboard covers what is above, its dock or the results card what is below.
        float _bandTop = 0.23f, _bandBottom = 0.81f, _bandTopWanted = 0.23f, _bandBottomWanted = 0.81f;
        float Band => Mathf.Clamp(_bandBottom - _bandTop, 0.2f, 1f);
        // Tipping a camera down by this fraction of its lens angle lifts the subject to the band's middle.
        float Bias => 0.5f - (_bandTop + _bandBottom) * 0.5f;

        /// <summary>
        /// Tells the cameras which band of the screen is clear of interface. They frame the fighters to
        /// fit it and aim so the action sits in its middle, so opening the results card over the lower
        /// half of the screen pushes the fighters up into what is left instead of hiding them.
        /// </summary>
        public void SetPictureBand(float top01, float bottom01)
        {
            _bandTopWanted = Mathf.Clamp01(top01);
            _bandBottomWanted = Mathf.Clamp(bottom01, _bandTopWanted + 0.1f, 1f);
        }

        CinemachineImpulseSource _impulse;
        Fighter _initiative, _impactVictim;
        Shot _forced;
        float _forcedUntil, _nextCut, _lastImpactCut = -99f, _orbitAngle, _orbitHeldUntil, _sepSmooth = 1.2f, _sepVelocity;
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
            SimBus.PhaseChanged += OnPhase;
        }

        void OnDisable()
        {
            SimBus.Hit -= OnHit;
            SimBus.Knockdown -= OnKnockdown;
            SimBus.PunchThrown -= OnPunch;
            SimBus.PhaseChanged -= OnPhase;
            if (Instance == this) Instance = null;
        }

        void OnPhase(BoutPhase from, BoutPhase to)
        {
            if (walkOn == null) return;
            if (to == BoutPhase.WalkOn)
            {
                walkOn.time = 0.0;
                walkOn.Play();
                walkOn.Evaluate();
            }
            else if (from == BoutPhase.WalkOn) walkOn.Stop();
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

            // A close-up locked on one boxer by hand stays on that boxer.
            if (Pinned == Shot.Impact) return;
            if (!e.clean || e.impulse < bigHit || e.victim == null || e.victim.IsDown) return;
            if (Time.unscaledTime - _lastImpactCut < 2.5f) return;
            _lastImpactCut = Time.unscaledTime;
            _impactVictim = e.victim;
            Force(Shot.Impact, impactHold);
        }

        void OnKnockdown(Fighter f, HitEvent cause)
        {
            _lastImpactCut = Time.unscaledTime;
            if (Pinned != Shot.Impact) _impactVictim = f;
            Force(Shot.Impact, 1.3f);
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

        /// <summary>Locks the close-up on one boxer (0 red, 1 blue) until the cameras are given back.</summary>
        public void Focus(int corner)
        {
            if (bout == null) return;
            _impactVictim = corner == 1 ? bout.blue : bout.red;
            Pin(Shot.Impact);
        }

        /// <summary>Turns the orbit camera by hand, taking it on air if it is not; it stays where it is put for a few seconds, then walks on.</summary>
        public void TurnOrbit(float degrees)
        {
            _orbitAngle += degrees;
            _orbitHeldUntil = Time.unscaledTime + 3f;
            if (Pinned != Shot.Orbit) Pin(Shot.Orbit);
        }

        // ------------------------------------------------------------ choosing

        void Update()
        {
            if (bout == null || redLive == null || blueLive == null) return;
            float now = Time.unscaledTime;
            // The walk-on Timeline keeps the bout's clock, not its own: it is moved to the phase's age and
            // evaluated by hand, so the pictures and the name cards cannot drift apart.
            if (walkOn != null && bout.Phase == BoutPhase.WalkOn)
            {
                walkOn.time = bout.PhaseAge;
                walkOn.Evaluate();
            }

            Shot want = Current;
            if (Pinned.HasValue) want = Pinned.Value;
            // The walk-on: the Timeline's camera track has the picture. Underneath it, and on their own if
            // there is no Timeline, the two corner shots, a half each.
            else if (bout.Phase == BoutPhase.WalkOn) want = bout.PhaseAge < bout.walkOnSeconds * 0.5f ? Shot.WalkRed : Shot.WalkBlue;
            else if (bout.Phase == BoutPhase.Results) want = bout.Winner != null ? Shot.Winner : Shot.Orbit;
            else if (now < _forcedUntil) want = _forced;
            else if (bout.Phase == BoutPhase.Count && bout.Downed != null) { want = Shot.Overhead; _impactVictim = bout.Downed; }
            else if (bout.Phase != BoutPhase.Fight) want = Shot.Orbit;
            else if (now >= _nextCut || Current == Shot.Impact || Current == Shot.Overhead)
            {
                want = PickAmbient(out float hold);
                _nextCut = now + hold;
            }

            if (want != Current) Apply(want);
            PlaceAll();
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
            Set(impactCam, Shot.Impact); Set(overheadCam, Shot.Overhead);
            Set(walkRedCam, Shot.WalkRed); Set(walkBlueCam, Shot.WalkBlue); Set(winnerCam, Shot.Winner);
        }

        void Set(CinemachineCamera cam, Shot shot)
        {
            if (cam != null) cam.Priority = Current == shot ? 30 : 10;
        }

        // ------------------------------------------------------------ placing

        void PlaceAll()
        {
            FighterSkin red = redLive, blue = blueLive;

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
                if (Time.unscaledTime >= _orbitHeldUntil) _orbitAngle += orbitDegreesPerSecond * dt;
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
                // Locked on a boxer by hand it is a shot to watch for minutes, not a second: a step further back.
                float impactDistance = Pinned == Shot.Impact ? 4.4f : 3.2f;
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

            // The inset, during a count: the boxer left standing, head to hips, from in front and to one side.
            if (pipCam != null)
            {
                bool on = bout.Phase == BoutPhase.Count && bout.Downed != null;
                if (pipCam.enabled != on) pipCam.enabled = on;
                if (on)
                {
                    FighterSkin up = bout.Downed == bout.red ? blue : red, down = bout.Downed == bout.red ? red : blue;
                    Vector3 chest = up.ChestPoint;
                    Vector3 facing = down.ChestPoint - chest;
                    facing.y = 0f;
                    facing = facing.sqrMagnitude > 0.01f ? facing.normalized : Vector3.forward;
                    Vector3 p = Inside(chest + Quaternion.AngleAxis(35f, Vector3.up) * facing * 2.6f, 0.3f);
                    p.y = ringCentre.y + 1.5f;
                    pipCam.transform.SetPositionAndRotation(p, Quaternion.LookRotation(Vector3.Lerp(up.HeadPoint, up.PelvisPoint, 0.4f) - p, Vector3.up));
                }
            }

            PlaceWalk(walkRedCam, red, blue);
            PlaceWalk(walkBlueCam, blue, red);

            // Winner: round and round whoever won, close enough to be about them.
            if (winnerCam != null && bout != null && Announcer.TeethAnnouncer.Current != null && Announcer.TeethAnnouncer.Current.OnStage)
            {
                // While the announcer is down to name the winner the shot is the introduction's: the
                // announcer in front, the winner beyond it.
                PlaceIntroduction(winnerCam, bout.Winner == bout.blue ? blue : red, Announcer.TeethAnnouncer.Current);
            }
            else if (winnerCam != null && bout != null)
            {
                // From the side of the ring that has room, swinging slowly to and fro across it: a winner
                // standing against the ropes is not circled from a hand's width away.
                FighterSkin winner = bout.Winner == bout.blue ? blue : red;
                _winnerAngle += dt;
                Vector3 chest = winner.ChestPoint;
                Vector3 inward = ringCentre - chest;
                inward.y = 0f;
                inward = inward.sqrMagnitude > 0.04f ? inward.normalized : Vector3.forward;
                Vector3 from = Quaternion.AngleAxis(Mathf.Sin(_winnerAngle * 0.35f) * 38f, Vector3.up) * inward;
                float tanV = Mathf.Tan(winnerCam.Lens.FieldOfView * 0.5f * Mathf.Deg2Rad);
                float distance = Mathf.Clamp(0.95f / Mathf.Max(0.01f, tanV * Band), 2.6f, Mathf.Max(2.6f, RopeRoom(chest, from) - 0.3f));
                Vector3 p = chest + from * distance;
                p.y = ringCentre.y + 1.6f;
                Place(winnerCam, p, Vector3.Lerp(chest, winner.PelvisPoint, 0.4f));
            }
        }

        float _winnerAngle;

        /// <summary>
        /// One fighter before the bell, head to hips. The two start a couple of metres apart, face to face,
        /// so the camera stands well off the line between them, to the fighter's front and side: the other
        /// one is out of the way, at the edge of the picture or off it.
        /// </summary>
        void PlaceWalk(CinemachineCamera cam, FighterSkin who, FighterSkin other)
        {
            if (cam == null) return;
            if (Announcer.TeethAnnouncer.Current != null) { PlaceIntroduction(cam, who, Announcer.TeethAnnouncer.Current); return; }
            Vector3 chest = who.ChestPoint;
            Vector3 facing = other.ChestPoint - chest;
            facing.y = 0f;
            facing = facing.sqrMagnitude > 0.01f ? facing.normalized : Vector3.forward;
            // Sixty degrees round from where the fighter is looking, on whichever side has more ring.
            Vector3 left = Quaternion.AngleAxis(-62f, Vector3.up) * facing, right = Quaternion.AngleAxis(62f, Vector3.up) * facing;
            Vector3 from = RopeRoom(chest, left) >= RopeRoom(chest, right) ? left : right;
            // Far enough back for head to hips to fit the band of screen the interface leaves clear.
            float tanV = Mathf.Tan(cam.Lens.FieldOfView * 0.5f * Mathf.Deg2Rad);
            float distance = Mathf.Clamp(0.62f / Mathf.Max(0.01f, tanV * Band), 2.4f, 5.5f);
            Vector3 p = Inside(chest + from * distance, 0.3f);
            p.y = ringCentre.y + 1.45f;
            Place(cam, p, Vector3.Lerp(who.HeadPoint, who.PelvisPoint, 0.45f));
        }

        [Header("The introductions, with the announcer in the picture")]
        [Tooltip("Metres from the announcer to the camera, on the far side of it from the boxer being introduced.")]
        public float introBack = 3.3f;
        [Tooltip("Degrees the camera stands off the line from the boxer through the announcer: it keeps the other boxer, who is in the corner behind the camera, out of the lens, and the announcer off the boxer.")]
        public float introTurn = 18f;
        public float introCameraHeight = 1.3f;
        public float introFieldOfView = 50f;
        [Tooltip("0 aims at the boxer, 1 at the announcer.")]
        [Range(0f, 1f)] public float introAim = 0.62f;
        [Tooltip("Half the announcer's height, metres: its top is put at the top of the clear band of the screen.")]
        public float introAnnouncerHalf = 0.42f;

        /// <summary>
        /// A boxer being introduced: the announcer in the foreground over the middle of the ring, the boxer
        /// in its corner beyond it, to one side and lower in the picture. The camera stands on the far side
        /// of the announcer, turned off the diagonal so that the other boxer, in the corner at its back, is
        /// not in front of the lens.
        /// </summary>
        void PlaceIntroduction(CinemachineCamera cam, FighterSkin who, Announcer.TeethAnnouncer announcer)
        {
            Vector3 teeth = announcer.StagePoint;
            Vector3 centre = new Vector3(teeth.x, ringCentre.y, teeth.z);
            Vector3 away = centre - who.ChestPoint;
            away.y = 0f;
            away = away.sqrMagnitude > 0.01f ? away.normalized : Vector3.forward;
            Vector3 left = Quaternion.AngleAxis(-introTurn, Vector3.up) * away, right = Quaternion.AngleAxis(introTurn, Vector3.up) * away;
            Vector3 from = RopeRoom(centre, left) >= RopeRoom(centre, right) ? left : right;
            Vector3 p = Inside(centre + from * introBack, 0.3f);
            p.y = ringCentre.y + introCameraHeight;

            LensSettings lens = cam.Lens;
            lens.FieldOfView = introFieldOfView;
            cam.Lens = lens;
            // Across: between the boxer and the announcer. Up: so that the announcer's top is at the top of
            // the band of screen the interface leaves clear.
            Vector3 toTeeth = teeth - p, toBoxer = who.HeadPoint - p;
            Vector3 flat = Vector3.Slerp(new Vector3(toBoxer.x, 0f, toBoxer.z).normalized, new Vector3(toTeeth.x, 0f, toTeeth.z).normalized, introAim);
            float tanV = Mathf.Tan(introFieldOfView * 0.5f * Mathf.Deg2Rad);
            float top = Mathf.Atan2(teeth.y + introAnnouncerHalf - p.y, new Vector2(toTeeth.x, toTeeth.z).magnitude);
            float above = Mathf.Atan((1f - 2f * (_bandTop + 0.02f)) * tanV);
            float pitch = top - above;
            cam.transform.SetPositionAndRotation(p, Quaternion.LookRotation(flat + Vector3.up * Mathf.Tan(pitch), Vector3.up));
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
