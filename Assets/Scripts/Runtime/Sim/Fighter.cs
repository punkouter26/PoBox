using System;
using System.Collections.Generic;
using UnityEngine;

namespace PoBox.Sim
{
    [Serializable]
    public struct FighterStats
    {
        public int thrown;
        public int landed;
        public int clean;
        public int knockdowns;       // scored on the opponent
        public int blocked;          // the opponent's punches this fighter stopped on its gloves and arms
        public int downs;            // times this fighter went down
        public float peakImpulse;    // N s
        public float damageDealt;    // health points
        public float energyJ;        // joint work, sum of |torque x joint speed| dt
    }

    /// <summary>
    /// The fighter: a boxer on the MuJoCo plugin (<see cref="Mj.MjBoxer"/>, its body and its policy, stepped by
    /// <see cref="Mj.MjRing"/>), the health and control it has left, and the telemetry everything else reads
    /// (joint stress, balance, power, glove speed, hit impulses). There is no Unity physics in it.
    ///
    /// It decides nothing. The policy sets the joint targets, and this class turns what MuJoCo then did into
    /// numbers and the bout's events. That is the seam: swap the brain, keep the broadcast.
    ///
    /// What a punch does to a trained fighter is the rule its policy was trained under (training/envs/boxing.py):
    /// every hit adds its closing speed to the fighter's daze, in full to the head and a share to the body;
    /// the daze drains with a time constant of a couple of seconds; above a threshold the joint drives
    /// weaken, and at a higher one the legs go and the fighter is down for a count. It then has to get up
    /// by itself, with the policy trained for that, before the count reaches ten.
    /// </summary>
    public class Fighter : MonoBehaviour
    {
        [Header("Identity")]
        public string displayName = "RED";
        public Color cornerColor = new Color(0.9f, 0.2f, 0.2f);
        [Tooltip("0 red corner, 1 blue corner.")]
        public int corner;
        public League.PolicyProfile profile;
        public Fighter opponent;

        [Header("Body (the boxer's prefab, Assets/Boxers)")]
        public BodyPart pelvis;
        public BodyPart torso;
        public BodyPart head;
        public BodyPart upperArmL, forearmL, upperArmR, forearmR;
        public BodyPart thighL, shinL, footL, thighR, shinR, footR;
        [Tooltip("Every link, pelvis first.")]
        public BodyPart[] parts = new BodyPart[0];
        public float standingPelvisHeight = 0.97f;
        public float totalMass = 72f;
        [Tooltip("The boxer: its MuJoCo body and its policies.")]
        public Mj.MjBoxer boxer;
        [Tooltip("The ring that steps it.")]
        public Mj.MjRing ring;
        [Tooltip("Centre of the head (its shape on the torso link).")]
        public Transform headAnchor;

        [Header("Damage model")]
        [Tooltip("Health points per newton-second of a clean hit, before the zone weight.")]
        public float damagePerNs = 0.4f;
        [Tooltip("Impulse below this is a touch, not a punch.")]
        public float impulseFloor = 2.0f;
        [Tooltip("Stand-in only. Impulse x zone weight at which the fighter goes down.")]
        public float knockdownShock = 30f;
        [Tooltip("Stand-in only. Impulse x zone weight at which the fighter is visibly rocked.")]
        public float staggerShock = 15f;
        [Tooltip("Joint work that empties the tank, in joules.")]
        public float staminaJoules = 40000f;
        [Tooltip("Seconds after the bell or after getting up from a count during which a punch cannot put this fighter down; only running out of health can.")]
        public float graceSeconds = 0f;
        [Tooltip("Stand-in only. A single punch only puts down a fighter whose health is below this. 100 = any time.")]
        public float knockdownBelowHealth = 100f;
        [Tooltip("The ring, for standing a trained fighter back up inside it. Half-width 0 = not known.")]
        public Vector3 ringCentre;
        public float ringHalf;

        [Header("Daze (trained fighters): the rule the policies were trained under")]
        public bool dazeRule;
        [Tooltip("Seconds for the daze to drain to a third.")]
        public float dazeTau = 2.5f;
        [Tooltip("Daze at which the joint drives start to weaken, and at which the legs go.")]
        public float dazeLo = 14f, dazeHi = 42f;
        [Tooltip("Share of drive strength lost just below the upper threshold.")]
        public float dazeWeak = 0.45f;
        [Tooltip("What a body shot adds to the daze, as a share of what the same punch to the head would.")]
        public float dazeBody = 0.3f;
        [Tooltip("How long the drives stay slack after the legs go, seconds, least and most. Then the fighter starts to get up.")]
        public Vector2 slackSeconds = new Vector2(1.2f, 2.4f);
        [Tooltip("Seconds a fighter that has got up by itself stands before the match policy takes the body back.")]
        public float handOverSeconds = 1.5f;

        // ------------------------------------------------------------ state
        public float Health { get; private set; } = 100f;
        public float Stamina { get; private set; } = 1f;
        /// <summary>How much control the fighter has over its own body: 1 normal, about 0.5 rocked, 0 limp.</summary>
        public float Authority { get; private set; } = 1f;
        /// <summary>The share of joint drive strength the fighter has left: what the brain multiplies its drives by.</summary>
        public float DriveScale { get; private set; } = 1f;
        /// <summary>Accumulated punishment, in m/s of punches taken; drains by itself. See the class comment.</summary>
        public float Daze { get; private set; }
        /// <summary>0 clear-headed, 1 about to go.</summary>
        public float Daze01 => dazeRule ? Mathf.Clamp01(Daze / Mathf.Max(1f, dazeHi)) : Mathf.Clamp01(_stagger);
        public bool IsDown { get; private set; }
        /// <summary>Down, and trying to get up.</summary>
        public bool IsRising { get; private set; }
        public bool IsOut => Health <= 0f;
        public bool Staggered => dazeRule ? Daze > dazeLo : _stagger > 0f;
        public float TimeSinceHit => Time.time - _lastHitAt;

        [NonSerialized] public FighterStats stats;

        /// <summary>Impulse taken this bout, N s, by where it landed: head, chest, belly, left arm, right arm.</summary>
        public readonly float[] taken = new float[5];
        public const int ZoneHead = 0, ZoneChest = 1, ZoneBelly = 2, ZoneArmL = 3, ZoneArmR = 4;

        /// <summary>A mark left where a punch landed: which link, where on it, how bad.</summary>
        public struct Bruise
        {
            public Transform bone;
            public Vector3 local;
            public float amount;
        }

        public const int MaxBruises = 8;
        public readonly List<Bruise> bruises = new List<Bruise>(MaxBruises);

        // ------------------------------------------------------------ telemetry
        public Vector3 CenterOfMass { get; private set; }
        public Vector3 CenterOfMassVelocity { get; private set; }
        /// <summary>
        /// 1 planted, 0 going over. Where the body will come to rest if nothing is done about it (the centre of
        /// mass carried on by its own speed: the "capture point") against the patch of canvas the feet cover:
        /// 1 with that point 10 cm or more inside the patch, a half with it on the edge, 0 with it 10 cm outside.
        /// </summary>
        public float BalanceMargin { get; private set; } = 1f;
        /// <summary>The capture point from the middle of the feet, metres, x to the fighter's right and y ahead of it.</summary>
        public Vector2 CaptureOffset { get; private set; }
        /// <summary>Each foot from the middle of the two, in the same frame (left, right), and whether it is on the canvas.</summary>
        public readonly Vector2[] footOffset = new Vector2[2];
        public readonly bool[] footDown = { true, true };
        /// <summary>The capture point on the canvas, world x and z.</summary>
        public Vector2 CapturePoint { get; private set; }
        /// <summary>The patch of canvas the planted feet cover, world x and z, anticlockwise. Empty when the fighter is not standing on anything.</summary>
        public IReadOnlyList<Vector2> Support => _hull;
        public float PowerW { get; private set; }
        public float PeakStress { get; private set; }
        public BodyPart PeakStressPart { get; private set; }
        public float MeanStress { get; private set; }
        public float GloveSpeedL { get; private set; }
        public float GloveSpeedR { get; private set; }
        public float PelvisHeight => pelvis != null ? pelvis.transform.position.y - _floorY : 0f;
        public Vector3 HeadPosition => headAnchor != null ? headAnchor.position : transform.position + Vector3.up * 1.6f;
        public Vector3 HeadVelocity { get; private set; }
        /// <summary>1 standing straight, 0 lying down: how far the pelvis's own up is from the world's.</summary>
        public float Upright => pelvis != null ? Vector3.Dot(pelvis.transform.up, Vector3.up) : 1f;
        public HitEvent LastHitTaken { get; private set; }

        readonly PunchType[] _punchInHand = new PunchType[2];
        readonly float[] _punchAt = { -99f, -99f };
        readonly bool[] _punchSpent = new bool[2];
        /// <summary>How long after it leaves a punch can still land. After that a touch is a push.</summary>
        const float PunchLife = 0.5f;
        float _stagger, _downTimer, _lowTime, _lastHitAt = -99f, _floorY, _stoodFor;
        Vector3 _prevCom, _prevHead;
        bool _hasPrevCom, _canGetUp;
        readonly Vector3[] _sole = new Vector3[4];
        readonly List<Vector2> _hullPoints = new List<Vector2>(8), _hull = new List<Vector2>(9);
        /// <summary>The mass behind a glove: a punch's impulse is this times its closing speed (as training scores it), N s.</summary>
        public const float PunchMass = 2.2f;
        const float HitCap = 9f;

        void Awake()
        {
            _floorY = transform.position.y;
        }

        // ------------------------------------------------------------ lifecycle

        /// <summary>Puts the fighter back on its feet at a spot, in its guard, exactly as an episode starts in training.</summary>
        public void Respawn(Vector3 position, Quaternion rotation, bool newBout)
        {
            if (pelvis == null || ring == null) return;
            ring.Place(boxer, position, rotation * Vector3.forward);

            _floorY = position.y;
            _upSince = Time.time;
            _stagger = 0f; _downTimer = 0f; _lowTime = 0f; _stoodFor = 0f;
            _hasPrevCom = false;
            Daze = 0f;
            IsDown = false; IsRising = false; Authority = 1f; DriveScale = 1f;
            foreach (BodyPart p in parts) p.stress = 0f;

            if (newBout)
            {
                Health = 100f; Stamina = 1f;
                stats = default;
                Array.Clear(taken, 0, taken.Length);
                bruises.Clear();
                foreach (BodyPart p in parts) p.bruise = 0f;
            }
            else
            {
                // A minute on the stool.
                Health = Mathf.Min(100f, Health + 10f);
                Stamina = Mathf.Min(1f, Stamina + 0.4f);
            }
        }

        /// <summary>Called by the brain when a punch leaves, so a landing glove knows what it was.</summary>
        public void NotePunch(int hand, PunchType type)
        {
            int h = hand < 0 ? 0 : 1;
            _punchInHand[h] = type;
            _punchAt[h] = Time.fixedTime;
            _punchSpent[h] = false;
            stats.thrown++;
            SimBus.RaisePunch(this, type, hand);
        }

        // ------------------------------------------------------------ punches

        /// <summary>
        /// A punch of this fighter's landed, measured by the ring as the trainer measures one: which hand (-1 left,
        /// +1 right), on the head or the body, its closing speed, where, and the glove's velocity. Only a thrown
        /// punch scores, and only once; one the brain did not see thrown (it can be past the guard's speed for less
        /// than a control step) is counted as thrown here.
        /// </summary>
        public void OnPunchLanded(int side, bool toHead, float speed, Vector3 point, Vector3 gloveVelocity)
        {
            if (!Bout.Fighting || opponent == null || IsDown || opponent.IsDown) return;
            int h = side < 0 ? 0 : 1;
            if (_punchSpent[h] || Time.fixedTime - _punchAt[h] > PunchLife) NotePunch(side, side < 0 ? PunchType.Jab : PunchType.Cross);
            _punchSpent[h] = true;
            BodyPart part = opponent.torso;       // the head is a ball on the torso's link
            Vector3 centre = toHead ? opponent.HeadPosition : part.transform.position;
            var e = new HitEvent
            {
                attacker = this,
                victim = opponent,
                zone = toHead ? PartKind.Head : PartKind.Torso,
                punch = _punchInHand[h],
                hand = side,
                point = point,
                normal = (point - centre).normalized,
                impulse = PunchMass * Mathf.Min(HitCap, speed),
                gloveSpeed = speed,
                velocity = gloveVelocity,
                time = Bout.Clock,
            };
            if (e.impulse < impulseFloor) return;

            opponent.TakeHit(ref e, part);
            stats.landed++;
            if (e.clean) stats.clean++;
            stats.damageDealt += e.damage;
            if (e.impulse > stats.peakImpulse) stats.peakImpulse = e.impulse;

            SimBus.RaiseHit(e);
            if (opponent.IsDown && opponent._wentDownThisStep)
            {
                opponent._wentDownThisStep = false;
                stats.knockdowns++;
                SimBus.RaiseKnockdown(opponent, e);
            }
        }

        bool _wentDownThisStep;
        float _upSince = -99f;

        public static float ZoneWeight(PartKind zone)
        {
            switch (zone)
            {
                case PartKind.Head: return 1.6f;
                case PartKind.Torso: return 1.0f;
                case PartKind.Pelvis: return 0.8f;
                case PartKind.UpperArm:
                case PartKind.Forearm: return 0.25f;   // on the guard
                default: return 0.3f;
            }
        }

        static int ZoneIndex(PartKind zone, BodyPart part)
        {
            switch (zone)
            {
                case PartKind.Head: return ZoneHead;
                case PartKind.Torso: return ZoneChest;
                case PartKind.UpperArm:
                case PartKind.Forearm: return part != null && part.side > 0 ? ZoneArmR : ZoneArmL;
                default: return ZoneBelly;
            }
        }

        void TakeHit(ref HitEvent e, BodyPart part)
        {
            float zone = ZoneWeight(e.zone);
            e.clean = zone >= 0.8f;
            e.damage = Mathf.Max(0f, e.impulse - impulseFloor) * damagePerNs * zone;
            Health = Mathf.Max(0f, Health - e.damage);
            if (part != null) part.bruise = Mathf.Clamp01(part.bruise + e.damage / 35f);
            taken[ZoneIndex(e.zone, part)] += e.impulse;
            if (!e.clean) stats.blocked++;
            MarkBruise(part, e.point, e.clean ? e.damage : e.damage * 0.3f);
            _lastHitAt = Time.time;
            LastHitTaken = e;

            if (IsDown) return;
            bool fresh = Time.time - _upSince < graceSeconds;
            if (dazeRule)
            {
                float speed = Mathf.Min(HitCap, e.impulse / PunchMass);
                float share = e.zone == PartKind.Head ? 1f : e.zone == PartKind.Torso || e.zone == PartKind.Pelvis ? dazeBody : 0f;
                Daze += speed * share;
                // Just up from a count, the legs cannot go again straight away: the daze is held under the line.
                if (fresh) Daze = Mathf.Min(Daze, dazeHi * 0.9f);
                if (Health <= 0f || Daze >= dazeHi)
                {
                    GoDown();
                    _wentDownThisStep = true;
                }
                return;
            }

            float shock = e.impulse * zone;
            if (Health <= 0f || (shock >= knockdownShock && !fresh && Health < knockdownBelowHealth))
            {
                GoDown();
                _wentDownThisStep = true;
            }
            else if (shock >= staggerShock && !fresh)
            {
                _stagger = Mathf.Max(_stagger, Mathf.Lerp(0.35f, 1.1f, Mathf.InverseLerp(staggerShock, knockdownShock, shock)));
            }
        }

        /// <summary>Remembers where a punch landed, on the link it landed on, so the mark moves with the body.</summary>
        void MarkBruise(BodyPart part, Vector3 worldPoint, float damage)
        {
            if (part == null || damage <= 0f) return;
            float amount = Mathf.Clamp01(damage / 6f);
            Transform bone = part.transform;
            Vector3 local = bone.InverseTransformPoint(worldPoint);
            int weakest = 0;
            for (int i = 0; i < bruises.Count; i++)
            {
                Bruise b = bruises[i];
                if (b.bone == bone && (b.local - local).sqrMagnitude < 0.07f * 0.07f)
                {
                    b.amount = Mathf.Min(1f, b.amount + amount);
                    bruises[i] = b;
                    return;
                }
                if (b.amount < bruises[weakest].amount) weakest = i;
            }
            var mark = new Bruise { bone = bone, local = local, amount = amount };
            if (bruises.Count < MaxBruises) bruises.Add(mark);
            else if (bruises[weakest].amount < amount) bruises[weakest] = mark;
        }

        void GoDown()
        {
            IsDown = true;
            IsRising = false;
            _stoodFor = 0f;
            Daze = 0f;
            stats.downs++;
            if (IsOut) _downTimer = float.PositiveInfinity;       // out cold never gets up
            else if (dazeRule) _downTimer = UnityEngine.Random.Range(slackSeconds.x, slackSeconds.y);
            // The stand-in: the less there is left, the longer the canvas holds on.
            else _downTimer = Mathf.Lerp(2.0f, 4.2f, 1f - Health / 100f);
        }

        // ------------------------------------------------------------ per step

        void FixedUpdate()
        {
            if (!Bout.SimRunning || parts.Length == 0) return;
            float dt = Time.fixedDeltaTime;
            SampleTelemetry(dt);
            UpdateControl(dt);
        }

        void SampleTelemetry(float dt)
        {
            float power = 0f, peak = 0f, sum = 0f;
            int driven = 0;
            BodyPart peakPart = null;
            double[] limits = boxer.Cfg.force_limit;
            for (int i = 0; i < parts.Length; i++)
            {
                BodyPart part = parts[i];
                if (part.joints == null || part.joints.Length == 0) continue;
                // The joint above this link, a chain of hinges (a hip is three): its stress is its hardest-working hinge.
                float s = 0f, p = 0f;
                foreach (int j in part.joints)
                {
                    float torque = (float)boxer.Torque(j);
                    p += Mathf.Abs(torque * (float)boxer.JointVelocity(j));
                    s = Mathf.Max(s, Mathf.Clamp01(Mathf.Abs(torque) / Mathf.Max(1f, limits != null && j < limits.Length ? (float)limits[j] : 100f)));
                }
                part.stress += (s - part.stress) * (s > part.stress ? 0.5f : 0.05f);
                power += p;
                sum += part.stress;
                driven++;
                if (part.stress > peak) { peak = part.stress; peakPart = part; }
            }

            CenterOfMass = boxer.CentreOfMass;
            if (_hasPrevCom)
            {
                CenterOfMassVelocity = Vector3.Lerp(CenterOfMassVelocity, (CenterOfMass - _prevCom) / dt, 0.3f);
                HeadVelocity = Vector3.Lerp(HeadVelocity, (HeadPosition - _prevHead) / dt, 0.5f);
            }
            else { CenterOfMassVelocity = Vector3.zero; HeadVelocity = Vector3.zero; }
            _prevCom = CenterOfMass;
            _prevHead = HeadPosition;
            _hasPrevCom = true;
            PowerW += (power - PowerW) * 0.1f;
            PeakStress = peak;
            PeakStressPart = peakPart;
            MeanStress = driven > 0 ? sum / driven : 0f;
            stats.energyJ += power * dt;

            // Tired fighters are slower fighters. The tank refills at rest, slowly.
            Stamina = Mathf.Clamp(Stamina - power * dt / staminaJoules + 0.004f * dt, 0.12f, 1f);

            GloveSpeedL = ring.GloveVelocity(boxer, 0).magnitude;
            GloveSpeedR = ring.GloveVelocity(boxer, 1).magnitude;
            SampleBalance();
        }

        /// <summary>
        /// Balance, as a person standing on a moving floor would feel it. The capture point is where the
        /// centre of mass would end up over if the body kept its present speed and nothing caught it:
        /// its position plus its velocity times sqrt(height / g). While that point is inside the patch of
        /// canvas the feet cover, standing still is possible; once it is outside, only a step saves it.
        /// </summary>
        void SampleBalance()
        {
            if (footL == null || footR == null) return;
            float height = Mathf.Max(0.2f, CenterOfMass.y - _floorY);
            float reach = Mathf.Sqrt(height / 9.81f);
            var capture = new Vector2(CenterOfMass.x + CenterOfMassVelocity.x * reach, CenterOfMass.z + CenterOfMassVelocity.z * reach);
            CapturePoint = capture;

            _hullPoints.Clear();
            Vector2 mid = Vector2.zero;
            for (int i = 0; i < 2; i++)
            {
                boxer.Sole(i, _sole);
                footDown[i] = ring.FootDown(boxer, i);
                Vector2 c = Vector2.zero;
                foreach (Vector3 corner in _sole) c += new Vector2(corner.x, corner.z) * 0.25f;
                mid += c * 0.5f;
                if (!footDown[i]) continue;
                foreach (Vector3 corner in _sole) _hullPoints.Add(new Vector2(corner.x, corner.z));
            }

            // A boxer on the move has its capture point near the edge of its feet most of the time, and
            // over it for a moment in every step: that is what stepping is. So the scale runs from 10 cm
            // outside the feet (0: going, unless a step is found) to 10 cm inside them (1: planted).
            float margin = 0f;
            if (_hullPoints.Count >= 3 && PelvisHeight >= standingPelvisHeight * 0.6f)
            {
                ConvexHull(_hullPoints, _hull);
                margin = Mathf.Clamp01((InsideDistance(_hull, capture) + 0.10f) / 0.20f);
            }
            else _hull.Clear();
            BalanceMargin += (margin - BalanceMargin) * 0.25f;

            // The same picture in the fighter's own frame, for the gauge on the HUD.
            Vector3 fwd = boxer.Forward;
            var f2 = new Vector2(fwd.x, fwd.z);
            if (f2.sqrMagnitude < 1e-6f) f2 = Vector2.up;
            f2.Normalize();
            var r2 = new Vector2(f2.y, -f2.x);
            Vector2 Local(Vector2 world) => new Vector2(Vector2.Dot(world - mid, r2), Vector2.Dot(world - mid, f2));
            CaptureOffset = Vector2.Lerp(CaptureOffset, Local(capture), 0.25f);
            for (int i = 0; i < 2; i++)
            {
                Vector3 c = (i == 0 ? footL : footR).transform.position;
                footOffset[i] = Local(new Vector2(c.x, c.z));
            }
        }

        /// <summary>Andrew's monotone chain. The points come back anticlockwise, without the first repeated.</summary>
        static void ConvexHull(List<Vector2> points, List<Vector2> hull)
        {
            points.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            hull.Clear();
            for (int pass = 0; pass < 2; pass++)
            {
                int start = hull.Count;
                for (int i = 0; i < points.Count; i++)
                {
                    Vector2 p = pass == 0 ? points[i] : points[points.Count - 1 - i];
                    while (hull.Count - start >= 2)
                    {
                        Vector2 a = hull[hull.Count - 2], b = hull[hull.Count - 1];
                        if ((b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x) > 0f) break;
                        hull.RemoveAt(hull.Count - 1);
                    }
                    hull.Add(p);
                }
                hull.RemoveAt(hull.Count - 1);
            }
        }

        /// <summary>How far inside a convex polygon a point is, metres. Negative outside.</summary>
        static float InsideDistance(List<Vector2> hull, Vector2 p)
        {
            float least = float.MaxValue;
            for (int i = 0; i < hull.Count; i++)
            {
                Vector2 a = hull[i], b = hull[(i + 1) % hull.Count];
                Vector2 edge = b - a;
                float len = edge.magnitude;
                if (len < 1e-5f) continue;
                // Anticlockwise: the inside is to the left of every edge.
                float d = (edge.x * (p.y - a.y) - edge.y * (p.x - a.x)) / len;
                if (d < least) least = d;
            }
            return least == float.MaxValue ? 0f : least;
        }

        void UpdateControl(float dt)
        {
            if (dazeRule) Daze *= Mathf.Exp(-dt / Mathf.Max(0.1f, dazeTau));

            if (IsDown)
            {
                if (!IsRising)
                {
                    Authority = Mathf.MoveTowards(Authority, 0f, dt * 10f);
                    DriveScale = 0.04f;
                    _downTimer -= dt;
                    if (_downTimer <= 0f && !IsOut)
                    {
                        // A boxer gets up by itself if it has been taught to; if it has not, the referee stands
                        // it back in its guard.
                        SendMessage("BeginGetUp", SendMessageOptions.DontRequireReceiver);
                        if (!_canGetUp) { StandUp(); return; }
                        IsRising = true;
                        _stoodFor = 0f;
                    }
                }
                else
                {
                    // The drives are back and the get-up policy has them. Up is on its feet and upright (the
                    // test the policy was trained to pass, after half a second of it), and the policy keeps
                    // the body for a second more before the match policy has it back: handed over at half a
                    // second, with the body still settling into its guard, the match policy fell over in a
                    // third of the trials (training/tools/exam.py, HANDOVER_S, which is the same number).
                    Authority = Mathf.MoveTowards(Authority, 1f, dt * 10f);
                    DriveScale = 1f;
                    bool standing = PelvisHeight > standingPelvisHeight * 0.88f && Upright > 0.85f;
                    _stoodFor = standing ? _stoodFor + dt : 0f;
                    if (_stoodFor >= handOverSeconds) FinishGetUp();
                }
                return;
            }

            float target = 1f;
            if (_stagger > 0f) { _stagger -= dt; target = 0.5f; }
            Authority = Mathf.MoveTowards(Authority, target, dt * 3f);
            DriveScale = dazeRule
                ? 1f - dazeWeak * Mathf.Clamp01((Daze - dazeLo) / Mathf.Max(1e-3f, dazeHi - dazeLo))
                : Authority < 0.25f ? 0.04f : Mathf.Lerp(0.45f, 1f, Mathf.InverseLerp(0.25f, 1f, Authority));

            // On the canvas without having been put there by a rule: a trip, a shove into the ropes, legs
            // that were too weak to hold. If a punch landed just before, the punch gets the credit.
            if (PelvisHeight < standingPelvisHeight * 0.6f || Upright < 0.4f)
            {
                _lowTime += dt;
                if (_lowTime > 0.4f)
                {
                    _lowTime = 0f;
                    GoDown();
                    HitEvent cause = default;
                    if (TimeSinceHit < 1.5f && LastHitTaken.attacker != null)
                    {
                        cause = LastHitTaken;
                        cause.attacker.stats.knockdowns++;
                    }
                    else _downTimer = Mathf.Min(_downTimer, 1.6f);
                    SimBus.RaiseKnockdown(this, cause);
                }
            }
            else _lowTime = 0f;
        }

        /// <summary>For looking at a knockdown without waiting for one: the legs go, now, as if the daze had reached the line.</summary>
        public void ForceDown()
        {
            if (IsDown) return;
            GoDown();
            SimBus.RaiseKnockdown(this, default);
        }

        /// <summary>The brain's answer to BeginGetUp: it has a policy for this.</summary>
        public void AcceptGetUp() => _canGetUp = true;

        void FinishGetUp()
        {
            IsDown = false;
            IsRising = false;
            _upSince = Time.time;
            _stagger = 0f; _lowTime = 0f; _stoodFor = 0f;
            Daze = 0f;
            Authority = 1f; DriveScale = 1f;
            SendMessage("EndGetUp", SendMessageOptions.DontRequireReceiver);
            SimBus.RaiseGotUp(this);
        }

        /// <summary>
        /// A trained fighter with no get-up policy is stood back in its guard where it lies, facing the
        /// other fighter, when the count allows.
        /// </summary>
        void StandUp()
        {
            Vector3 at = pelvis.transform.position;
            at.y = _floorY;
            if (ringHalf > 0f)
            {
                // Back inside the ropes if the fall took it through them, and clear of the other fighter:
                // out of reach of an arm at full stretch. Stood up nearer than that, its guard appears with
                // the other's glove already inside it, and what the physics does about two bodies in one
                // place knocks the other one down (seen at 0.9 m: down within half a second, every time).
                const float Clear = 1.3f;
                float reach = ringHalf - 0.6f;
                // Stood up where it lies, clear of the other fighter: the one left standing has been held in its
                // guard facing this one, and sees it step back up in front of it.
                at.x = Mathf.Clamp(at.x, ringCentre.x - reach, ringCentre.x + reach);
                at.z = Mathf.Clamp(at.z, ringCentre.z - reach, ringCentre.z + reach);
                if (opponent != null)
                {
                    Vector3 theirs = opponent.pelvis.transform.position;
                    Vector3 away = at - theirs;
                    away.y = 0f;
                    if (away.magnitude < Clear)
                    {
                        // Away from them, or if the ropes are that way, towards the middle of the ring.
                        Vector3 dir = away.sqrMagnitude > 1e-4f ? away.normalized : -transform.forward;
                        Vector3 spot = new Vector3(theirs.x, at.y, theirs.z) + dir * Clear;
                        if (Mathf.Abs(spot.x - ringCentre.x) > reach || Mathf.Abs(spot.z - ringCentre.z) > reach)
                        {
                            Vector3 inward = ringCentre - theirs;
                            inward.y = 0f;
                            spot = new Vector3(theirs.x, at.y, theirs.z) + (inward.sqrMagnitude > 1e-4f ? inward.normalized : -dir) * Clear;
                        }
                        at = spot;
                    }
                }
            }
            Vector3 facing = opponent != null ? opponent.pelvis.transform.position - at : transform.forward;
            ring.Place(boxer, at, facing);
            _upSince = Time.time;
            _stagger = 0f; _lowTime = 0f;
            Daze = 0f;
            _hasPrevCom = false;
            IsDown = false; IsRising = false; Authority = 1f; DriveScale = 1f;
            SendMessage("ResetBrain", SendMessageOptions.DontRequireReceiver);
            SimBus.RaiseGotUp(this);
        }
    }
}
