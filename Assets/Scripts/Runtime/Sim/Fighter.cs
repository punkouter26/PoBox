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
    /// The physical fighter: thirteen articulation links, the health and control it has left, and the
    /// telemetry everything else reads (joint stress, balance, power, glove speed, hit impulses).
    ///
    /// It decides nothing. A brain sets the joint drive targets (<see cref="ScriptedBoxer"/> for the stand-in,
    /// a MuJoCo-trained policy for an entrant), and this class turns what the physics then did into numbers.
    /// That is the seam: swap the brain, keep the broadcast.
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

        [Header("Rig (set by the scene builder)")]
        public BodyPart pelvis;
        public BodyPart torso;
        public BodyPart head;
        public BodyPart upperArmL, forearmL, upperArmR, forearmR;
        public BodyPart thighL, shinL, footL, thighR, shinR, footR;
        [Tooltip("Every link, pelvis first.")]
        public BodyPart[] parts = new BodyPart[0];
        public float standingPelvisHeight = 0.97f;
        public float totalMass = 72f;
        [Tooltip("Set on a trained fighter: the body built from its MuJoCo model. Empty on the scripted stand-in.")]
        public Rl.MjcfRig mjcf;
        [Tooltip("Centre of the head, where the head is not a link of its own.")]
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
        public float PowerW { get; private set; }
        public float PeakStress { get; private set; }
        public BodyPart PeakStressPart { get; private set; }
        public float MeanStress { get; private set; }
        public float GloveSpeedL { get; private set; }
        public float GloveSpeedR { get; private set; }
        public float PelvisHeight => pelvis != null ? pelvis.transform.position.y - _floorY : 0f;
        public Vector3 HeadPosition => headAnchor != null ? headAnchor.position
                                     : head != null ? head.body.worldCenterOfMass : transform.position + Vector3.up * 1.6f;
        public Vector3 HeadVelocity => headAnchor != null && torso != null ? torso.body.GetPointVelocity(headAnchor.position)
                                     : head != null ? head.body.linearVelocity : Vector3.zero;
        /// <summary>1 standing straight, 0 lying down: how far the pelvis's own up is from the world's.</summary>
        public float Upright => pelvis != null ? Vector3.Dot(pelvis.transform.up, Vector3.up) : 1f;
        public HitEvent LastHitTaken { get; private set; }

        struct Pending
        {
            public bool active;
            public float start;
            public HitEvent e;
            public BodyPart victimPart;
        }

        Pending _pendL, _pendR;
        float _nextHitL, _nextHitR;
        readonly PunchType[] _punchInHand = new PunchType[2];
        readonly float[] _punchAt = { -99f, -99f };
        readonly bool[] _punchSpent = new bool[2];
        /// <summary>How long after it leaves a punch can still land. After that a touch is a push.</summary>
        const float PunchLife = 0.5f;
        Vector3[] _restPos;
        Quaternion[] _restRot;
        float _stagger, _downTimer, _lowTime, _lastHitAt = -99f, _floorY, _stoodFor;
        Vector3 _prevCom;
        bool _hasPrevCom, _canGetUp;
        readonly List<Vector2> _hullPoints = new List<Vector2>(8), _hull = new List<Vector2>(9);
        public const float PunchMass = 2.2f;
        const float HitWindow = 0.05f;
        const float HitCooldown = 0.22f;
        const float HitCap = 9f;

        void Awake()
        {
            _floorY = transform.position.y;
            _restPos = new Vector3[parts.Length];
            _restRot = new Quaternion[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                _restPos[i] = parts[i].transform.localPosition;
                _restRot[i] = parts[i].transform.localRotation;
            }
        }

        // ------------------------------------------------------------ lifecycle

        /// <summary>
        /// Puts the fighter back on its feet at a spot. The articulation is switched off while its links are
        /// moved back to the pose they were built in: an articulation rebuilds itself from its transforms
        /// when it is enabled, which is the one reset that cannot leave a joint with stale velocity.
        /// </summary>
        public void Respawn(Vector3 position, Quaternion rotation, bool newBout)
        {
            if (pelvis == null) return;
            if (mjcf != null)
            {
                // A trained body is stood in its guard exactly as an episode starts in training.
                mjcf.ResetPose(position, rotation * Vector3.forward);
            }
            else
            {
                GameObject root = pelvis.gameObject;
                root.SetActive(false);
                transform.SetPositionAndRotation(position, rotation);
                for (int i = 0; i < parts.Length; i++)
                    parts[i].transform.SetLocalPositionAndRotation(_restPos[i], _restRot[i]);
                root.SetActive(true);
            }

            _floorY = position.y;
            _upSince = Time.time;
            _pendL = default; _pendR = default;
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

        // ------------------------------------------------------------ contacts

        public void OnPartTouched(BodyPart part, Collision c, bool entered)
        {
            if (!Bout.SimRunning || c.contactCount == 0) return;

            Collider otherCollider = c.collider;
            BodyPart other = otherCollider != null ? otherCollider.GetComponentInParent<BodyPart>() : null;

            if (other == null)
            {
                // The canvas or the ropes. Only a body landing is worth telling anybody about.
                if (entered && (part.kind == PartKind.Torso || part.kind == PartKind.Head || part.kind == PartKind.Pelvis))
                {
                    float j = c.impulse.magnitude;
                    if (j > 6f) SimBus.RaiseFloorImpact(c.GetContact(0).point, j);
                }
                return;
            }

            if (other.owner == null || other.owner == this || part.strike == null) return;
            // Nothing scores before the bell or while the referee is counting.
            if (!Bout.Fighting) return;
            // Was it the glove, or the forearm behind it? And which part of them did the glove touch:
            // usually the link's own kind, finer where one link carries several shapes (a trained fighter's
            // head is a ball on its torso link, beside the chest and the body). Each contact says which
            // shape it was on; of the ones the glove made, the most telling counts.
            ContactPoint cp = default;
            bool glove = false;
            PartKind touched = other.kind;
            float telling = -1f;
            for (int i = 0; i < c.contactCount; i++)
            {
                ContactPoint at = c.GetContact(i);
                if (at.thisCollider != part.strike) continue;
                var zoneTag = at.otherCollider != null ? at.otherCollider.GetComponent<Rl.HitZone>() : null;
                PartKind kind = zoneTag != null ? zoneTag.kind : other.kind;
                if (ZoneWeight(kind) <= telling) continue;
                telling = ZoneWeight(kind);
                touched = kind;
                cp = at;
                glove = true;
            }
            if (!glove) return;

            bool left = part.side < 0;
            ref Pending p = ref (left ? ref _pendL : ref _pendR);
            float impulse = c.impulse.magnitude;
            Vector3 gloveVelocity = c.relativeVelocity;
            bool shadowed = mjcf != null && mjcf.Shadowed;
            if (shadowed)
            {
                // The body in the scene is a shadow of the one MuJoCo is simulating, and what Unity's solver
                // says it took to stop a shadow's glove is not a measurement of anything. A punch here is what
                // it is in training: how fast the glove was closing on what it hit, as MuJoCo's own state
                // has it, over the control step it landed in. Times the mass behind a glove (2.2 kg, measured
                // as impulse over speed when Unity did simulate the arm), newton-seconds.
                int hand = left ? 0 : 1;
                float speed = touched == PartKind.Head ? mjcf.ring.HitSpeed(mjcf.slot, hand, true)
                            : touched == PartKind.Torso || touched == PartKind.Pelvis ? mjcf.ring.HitSpeed(mjcf.slot, hand, false)
                            : Mathf.Max(mjcf.ring.HitSpeed(mjcf.slot, hand, true), mjcf.ring.HitSpeed(mjcf.slot, hand, false));
                // A layout from before the ring measured punches has nothing to say; fall back on the contact.
                if (speed <= 0f && mjcf.ring.L.fighters[mjcf.slot].r_glove <= 0f)
                    speed = Mathf.Min(HitCap, Mathf.Abs(Vector3.Dot(c.relativeVelocity, cp.normal)));
                impulse = PunchMass * speed;
                gloveVelocity = mjcf.ring.GloveVelocity(mjcf.slot, hand);
            }

            if (p.active)
            {
                // A shadow's glove is stopped by MuJoCo over several steps: the hardest reading is the punch,
                // their sum would be several punches.
                p.e.impulse = shadowed ? Mathf.Max(p.e.impulse, impulse) : p.e.impulse + impulse;
                // A punch that comes off the guard and still reaches the head is a head shot: within the
                // one touch, the hit is scored on the most telling part the glove got to.
                if (other.owner == p.e.victim && ZoneWeight(touched) > ZoneWeight(p.e.zone))
                {
                    p.e.zone = touched;
                    p.e.point = cp.point;
                    p.e.normal = cp.normal;
                    p.victimPart = other;
                }
                return;
            }

            if (Time.fixedTime < (left ? _nextHitL : _nextHitR)) return;
            // Only a thrown punch scores, and only once: a glove leaning on the other fighter, or
            // brushing them on the way back to the guard, is a push, however hard the solver says it was.
            int h = left ? 0 : 1;
            if (_punchSpent[h] || Time.fixedTime - _punchAt[h] > PunchLife) return;
            _punchSpent[h] = true;

            p.active = true;
            p.start = Time.fixedTime;
            p.victimPart = other;
            p.e = new HitEvent
            {
                attacker = this,
                victim = other.owner,
                zone = touched,
                punch = _punchInHand[left ? 0 : 1],
                hand = part.side,
                point = cp.point,
                normal = cp.normal,
                impulse = impulse,
                gloveSpeed = shadowed ? impulse / PunchMass : c.relativeVelocity.magnitude,
                velocity = gloveVelocity,
                time = Bout.Clock,
            };
        }

        void Resolve(ref Pending p, bool left)
        {
            if (!p.active || Time.fixedTime - p.start < HitWindow) return;
            p.active = false;
            if (left) _nextHitL = Time.fixedTime + HitCooldown; else _nextHitR = Time.fixedTime + HitCooldown;
            if (p.e.impulse < impulseFloor || p.e.victim == null) return;

            p.e.victim.TakeHit(ref p.e, p.victimPart);

            stats.landed++;
            if (p.e.clean) stats.clean++;
            stats.damageDealt += p.e.damage;
            if (p.e.impulse > stats.peakImpulse) stats.peakImpulse = p.e.impulse;

            SimBus.RaiseHit(p.e);
            if (p.e.victim.IsDown && p.e.victim._wentDownThisStep)
            {
                p.e.victim._wentDownThisStep = false;
                stats.knockdowns++;
                SimBus.RaiseKnockdown(p.e.victim, p.e);
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

            Resolve(ref _pendL, true);
            Resolve(ref _pendR, false);
            SampleTelemetry(dt);
            UpdateControl(dt);
        }

        void SampleTelemetry(float dt)
        {
            Vector3 com = Vector3.zero;
            float mass = 0f, power = 0f, peak = 0f, sum = 0f;
            int driven = 0;
            BodyPart peakPart = null;
            bool shadowed = mjcf != null && mjcf.Shadowed;

            for (int i = 0; i < parts.Length; i++)
            {
                BodyPart part = parts[i];
                ArticulationBody ab = part.body;
                com += ab.worldCenterOfMass * ab.mass;
                mass += ab.mass;

                // The joint above this link: one articulation joint on the stand-in, a chain of hinges on
                // a trained body (a hip is three). The part's stress is its hardest-working hinge.
                float s = 0f, p = 0f;
                bool any = false;
                int links = part.DriveCount;
                for (int l = 0; l < links; l++)
                {
                    ArticulationBody link = part.Drive(l);
                    if (shadowed)
                    {
                        // The body that is doing the work is the one in MuJoCo; its motors report their own torque.
                        int j = mjcf.IndexOf(link);
                        if (j < 0) continue;
                        any = true;
                        float torque1 = mjcf.ring.Torque(mjcf.slot, j);
                        p += Mathf.Abs(torque1 * mjcf.ring.Speed(mjcf.slot, j));
                        s = Mathf.Max(s, Mathf.Clamp01(Mathf.Abs(torque1) / Mathf.Max(1f, mjcf.ring.TorqueLimit(mjcf.slot, j))));
                        continue;
                    }
                    int dof = link.dofCount;
                    if (link.isRoot || dof == 0) continue;
                    any = true;
                    // driveForce is what the PD drive itself produced, which is exactly what forceLimit caps.
                    ArticulationReducedSpace torque = link.driveForce;
                    ArticulationReducedSpace speed = link.jointVelocity;
                    float t2 = 0f;
                    for (int d = 0; d < dof; d++)
                    {
                        t2 += torque[d] * torque[d];
                        p += Mathf.Abs(torque[d] * speed[d]);
                    }
                    s = Mathf.Max(s, Mathf.Clamp01(Mathf.Sqrt(t2) / Mathf.Max(1f, part.DriveLimit(l))));
                }
                if (!any) continue;
                part.stress += (s - part.stress) * (s > part.stress ? 0.5f : 0.05f);
                power += p;
                sum += part.stress;
                driven++;
                if (part.stress > peak) { peak = part.stress; peakPart = part; }
            }

            CenterOfMass = mass > 0f ? com / mass : transform.position;
            if (_hasPrevCom) CenterOfMassVelocity = Vector3.Lerp(CenterOfMassVelocity, (CenterOfMass - _prevCom) / dt, 0.3f);
            else CenterOfMassVelocity = Vector3.zero;
            _prevCom = CenterOfMass;
            _hasPrevCom = true;
            PowerW += (power - PowerW) * 0.1f;
            PeakStress = peak;
            PeakStressPart = peakPart;
            MeanStress = driven > 0 ? sum / driven : 0f;
            stats.energyJ += power * dt;

            // Tired fighters are slower fighters. The tank refills at rest, slowly.
            Stamina = Mathf.Clamp(Stamina - power * dt / staminaJoules + 0.004f * dt, 0.12f, 1f);

            if (shadowed)
            {
                GloveSpeedL = mjcf.ring.GloveVelocity(mjcf.slot, 0).magnitude;
                GloveSpeedR = mjcf.ring.GloveVelocity(mjcf.slot, 1).magnitude;
            }
            else
            {
                if (forearmL != null && forearmL.strike != null)
                    GloveSpeedL = forearmL.body.GetPointVelocity(forearmL.strike.bounds.center).magnitude;
                if (forearmR != null && forearmR.strike != null)
                    GloveSpeedR = forearmR.body.GetPointVelocity(forearmR.strike.bounds.center).magnitude;
            }

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

            _hullPoints.Clear();
            Vector2 mid = Vector2.zero;
            for (int i = 0; i < 2; i++)
            {
                BodyPart foot = i == 0 ? footL : footR;
                BoxCollider box = mjcf != null ? (i == 0 ? mjcf.footL : mjcf.footR) : null;
                Transform t = box != null ? box.transform : foot.transform;
                Vector3 centre = box != null ? t.TransformPoint(box.center) : foot.body.worldCenterOfMass;
                Vector3 half = box != null ? Vector3.Scale(box.size, t.lossyScale) * 0.5f : new Vector3(0.045f, 0.03f, 0.13f);
                Vector3 ax = t.right * half.x, ay = t.up * half.y, az = t.forward * half.z;
                float lift = centre.y - (Mathf.Abs(ax.y) + Mathf.Abs(ay.y) + Mathf.Abs(az.y)) - _floorY;
                footDown[i] = mjcf != null && mjcf.Shadowed ? mjcf.ring.FootDown(mjcf.slot, i) : lift < 0.03f;
                mid += new Vector2(centre.x, centre.z) * 0.5f;
                if (!footDown[i]) continue;
                // The sole is the box's two longest axes; the shortest is its thickness.
                Vector3 u = ax, v = az;
                if (half.x <= half.y && half.x <= half.z) u = ay;
                else if (half.z <= half.x && half.z <= half.y) v = ay;
                for (int sx = -1; sx <= 1; sx += 2)
                    for (int sz = -1; sz <= 1; sz += 2)
                    {
                        Vector3 corner = centre + u * sx + v * sz;
                        _hullPoints.Add(new Vector2(corner.x, corner.z));
                    }
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
            BalanceMargin += (margin - BalanceMargin) * 0.25f;

            // The same picture in the fighter's own frame, for the gauge on the HUD.
            Vector3 fwd = mjcf != null ? mjcf.Forward : pelvis.transform.forward;
            var f2 = new Vector2(fwd.x, fwd.z);
            if (f2.sqrMagnitude < 1e-6f) f2 = Vector2.up;
            f2.Normalize();
            var r2 = new Vector2(f2.y, -f2.x);
            Vector2 Local(Vector2 world) => new Vector2(Vector2.Dot(world - mid, r2), Vector2.Dot(world - mid, f2));
            CaptureOffset = Vector2.Lerp(CaptureOffset, Local(capture), 0.25f);
            for (int i = 0; i < 2; i++)
            {
                BodyPart foot = i == 0 ? footL : footR;
                Vector3 c = foot.body.worldCenterOfMass;
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
                        if (mjcf != null)
                        {
                            // A trained fighter gets up by itself if it has been taught to; if it has not,
                            // it is stood back in its guard, as it was before there was a policy for this.
                            SendMessage("BeginGetUp", SendMessageOptions.DontRequireReceiver);
                            if (!_canGetUp) { StandUp(); return; }
                        }
                        IsRising = true;
                        _stoodFor = 0f;
                    }
                }
                else if (mjcf != null)
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
                else
                {
                    Authority = Mathf.MoveTowards(Authority, 1f, dt * 0.9f);
                    DriveScale = Authority;
                    if (Authority >= 1f && PelvisHeight > standingPelvisHeight * 0.85f)
                    {
                        IsDown = false;
                        IsRising = false;
                        SimBus.RaiseGotUp(this);
                    }
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
            _pendL = default; _pendR = default;
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
                // The fighter left standing has been boxing a stand-in on the spot where this one stood.
                // Stood up anywhere else, this one appears off to its side or behind it, and a policy that
                // has never had to turn round falls over doing it. So: on the line it is already looking
                // along, a step further back. All it sees is its opponent step away.
                var theirBrain = opponent != null ? opponent.GetComponent<Rl.PolicyBrain>() : null;
                if (theirBrain != null && theirBrain.StandInSpot.HasValue)
                {
                    Vector3 from = opponent.pelvis.transform.position;
                    Vector3 line = theirBrain.StandInSpot.Value - from;
                    line.y = 0f;
                    if (line.sqrMagnitude > 0.01f) at = new Vector3(from.x, at.y, from.z) + line.normalized * Clear;
                }
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
            mjcf.ResetPose(at, facing);
            _upSince = Time.time;
            _pendL = default; _pendR = default;
            _stagger = 0f; _lowTime = 0f;
            Daze = 0f;
            _hasPrevCom = false;
            IsDown = false; IsRising = false; Authority = 1f; DriveScale = 1f;
            SendMessage("ResetBrain", SendMessageOptions.DontRequireReceiver);
            SimBus.RaiseGotUp(this);
        }
    }
}
