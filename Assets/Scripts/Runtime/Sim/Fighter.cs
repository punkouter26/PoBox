using System;
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
        public float peakImpulse;    // N s
        public float damageDealt;    // health points
        public float energyJ;        // joint work, sum of |torque x joint speed| dt
    }

    /// <summary>
    /// The physical fighter: thirteen articulation links, the health and control it has left, and the
    /// telemetry everything else reads (joint stress, balance, power, glove speed, hit impulses).
    ///
    /// It decides nothing. A brain sets the joint drive targets (<see cref="ScriptedBoxer"/> today; a
    /// MuJoCo-trained policy once there is a skinned mesh to train on), and this class turns what the
    /// physics then did into numbers. That is the seam: swap the brain, keep the broadcast.
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
        [Tooltip("Every link, pelvis first. The replay and the heat map index parts by this order.")]
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
        [Tooltip("Impulse x zone weight at which the fighter goes down.")]
        public float knockdownShock = 30f;
        [Tooltip("Impulse x zone weight at which the fighter is visibly rocked.")]
        public float staggerShock = 15f;
        [Tooltip("Joint work that empties the tank, in joules.")]
        public float staminaJoules = 40000f;
        [Tooltip("Seconds after the bell or after getting up from a count during which a punch cannot put this fighter down or stagger it; only running out of health can. Without it a fighter stood up after a count walks into the other's first punch, thrown with a step behind it, and is down again three seconds later, for the whole round.")]
        public float graceSeconds = 0f;
        [Tooltip("A single punch only puts down a fighter whose health is below this. 100 = any time.")]
        public float knockdownBelowHealth = 100f;
        [Tooltip("The ring, for standing a trained fighter back up inside it. Half-width 0 = not known.")]
        public Vector3 ringCentre;
        public float ringHalf;

        // ------------------------------------------------------------ state
        public float Health { get; private set; } = 100f;
        public float Stamina { get; private set; } = 1f;
        /// <summary>How much control the fighter has over its own body: 1 normal, about 0.5 rocked, 0 limp.</summary>
        public float Authority { get; private set; } = 1f;
        public bool IsDown { get; private set; }
        public bool IsRising { get; private set; }
        public bool IsOut => Health <= 0f;
        public bool Staggered => _stagger > 0f;
        public float TimeSinceHit => Time.time - _lastHitAt;

        [NonSerialized] public FighterStats stats;

        // ------------------------------------------------------------ telemetry
        public Vector3 CenterOfMass { get; private set; }
        /// <summary>1 with the centre of mass over the feet, 0 with it outside them (or on the canvas).</summary>
        public float BalanceMargin { get; private set; } = 1f;
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
        float _stagger, _downTimer, _lowTime, _lastHitAt = -99f, _floorY;
        const float HitWindow = 0.05f;
        const float HitCooldown = 0.22f;

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
            _stagger = 0f; _downTimer = 0f; _lowTime = 0f;
            IsDown = false; IsRising = false; Authority = 1f;
            foreach (BodyPart p in parts) p.stress = 0f;

            if (newBout)
            {
                Health = 100f; Stamina = 1f;
                stats = default;
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
            // Which part of them was touched. Usually the link's own kind; finer where one link carries
            // several (a trained fighter's head is a ball on its torso link).
            var zoneTag = otherCollider.GetComponent<Rl.HitZone>();
            PartKind touched = zoneTag != null ? zoneTag.kind : other.kind;

            // Was it the glove, or the forearm behind it?
            ContactPoint cp = default;
            bool glove = false;
            for (int i = 0; i < c.contactCount; i++)
            {
                cp = c.GetContact(i);
                if (cp.thisCollider == part.strike) { glove = true; break; }
            }
            if (!glove) return;

            bool left = part.side < 0;
            ref Pending p = ref (left ? ref _pendL : ref _pendR);
            float impulse = c.impulse.magnitude;

            if (p.active)
            {
                // A shadow's glove is stopped by MuJoCo over several steps, and on each of them Unity
                // reports the impulse it would take to stop what is left: the hardest of those is the
                // punch, their sum is several punches.
                p.e.impulse = mjcf != null && mjcf.Shadowed ? Mathf.Max(p.e.impulse, impulse) : p.e.impulse + impulse;
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
                gloveSpeed = c.relativeVelocity.magnitude,
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

        void TakeHit(ref HitEvent e, BodyPart part)
        {
            float zone = ZoneWeight(e.zone);
            e.clean = zone >= 0.8f;
            e.damage = Mathf.Max(0f, e.impulse - impulseFloor) * damagePerNs * zone;
            Health = Mathf.Max(0f, Health - e.damage);
            if (part != null) part.bruise = Mathf.Clamp01(part.bruise + e.damage / 35f);
            _lastHitAt = Time.time;
            LastHitTaken = e;

            if (IsDown) return;
            float shock = e.impulse * zone;
            bool fresh = Time.time - _upSince < graceSeconds;
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

        void GoDown()
        {
            IsDown = true;
            IsRising = false;
            // The less there is left, the longer the canvas holds on. Out cold never gets up.
            _downTimer = IsOut ? float.PositiveInfinity : Mathf.Lerp(2.0f, 4.2f, 1f - Health / 100f);
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
                    if (mjcf != null && mjcf.Shadowed)
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
            PowerW += (power - PowerW) * 0.1f;
            PeakStress = peak;
            PeakStressPart = peakPart;
            MeanStress = driven > 0 ? sum / driven : 0f;
            stats.energyJ += power * dt;

            // Tired fighters are slower fighters. The tank refills at rest, slowly.
            Stamina = Mathf.Clamp(Stamina - power * dt / staminaJoules + 0.004f * dt, 0.12f, 1f);

            if (forearmL != null && forearmL.strike != null)
                GloveSpeedL = forearmL.body.GetPointVelocity(forearmL.strike.bounds.center).magnitude;
            if (forearmR != null && forearmR.strike != null)
                GloveSpeedR = forearmR.body.GetPointVelocity(forearmR.strike.bounds.center).magnitude;

            // Balance: how far the centre of mass has wandered from between the feet, against how far
            // apart the feet are. Crude, and enough to tell a fighter who is planted from one who is going.
            if (footL != null && footR != null)
            {
                Vector3 l = footL.body.worldCenterOfMass, r = footR.body.worldCenterOfMass;
                Vector3 mid = (l + r) * 0.5f;
                float halfSpan = Vector3.Distance(new Vector3(l.x, 0f, l.z), new Vector3(r.x, 0f, r.z)) * 0.5f + 0.14f;
                float off = Vector2.Distance(new Vector2(CenterOfMass.x, CenterOfMass.z), new Vector2(mid.x, mid.z));
                float margin = 1f - Mathf.Clamp01(off / halfSpan);
                if (PelvisHeight < standingPelvisHeight * 0.6f) margin = 0f;
                BalanceMargin += (margin - BalanceMargin) * 0.2f;
            }
        }

        void UpdateControl(float dt)
        {
            if (IsDown)
            {
                if (!IsRising)
                {
                    Authority = Mathf.MoveTowards(Authority, 0f, dt * 10f);
                    _downTimer -= dt;
                    if (_downTimer <= 0f && !IsOut)
                    {
                        if (mjcf != null) { StandUp(); return; }
                        IsRising = true;
                    }
                }
                else
                {
                    Authority = Mathf.MoveTowards(Authority, 1f, dt * 0.9f);
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

            // On the canvas without having been put there by a rule: a trip, a shove into the ropes, or,
            // for a trained fighter, the only way it ever goes down. If a punch landed just before, the
            // punch gets the credit.
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

        /// <summary>
        /// A trained fighter has no get-up of its own yet (that is a separate policy to train), so when the
        /// count allows, it is stood back in its guard where it lies, facing the other fighter.
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
            IsDown = false; IsRising = false; Authority = 1f;
            SendMessage("ResetBrain", SendMessageOptions.DontRequireReceiver);
            SimBus.RaiseGotUp(this);
        }
    }
}
