using UnityEngine;
using PoBox.League;

namespace PoBox.Sim
{
    /// <summary>
    /// Sign between a Unity (left-handed) Euler angle about a link's own axis and the articulation drive
    /// target on that axis. Measured, not assumed: <c>PoBox/Dev/Probe Joint Signs</c> drives one joint and
    /// reads back which way the link went. The scene builder uses the same numbers for the joint limits, so
    /// changing one means rebuilding the scene.
    /// </summary>
    public static class RigSigns
    {
        public static readonly Vector3 S = new Vector3(1f, 1f, 1f);
    }

    /// <summary>
    /// A hand-written boxer, standing in for a trained policy.
    ///
    /// It does two jobs a policy would do in one. It picks joint targets (a guard, five punches, a shuffle),
    /// and it keeps the body upright with outside help: a spring holding the pelvis at standing height, a
    /// string pulling the chest over the pelvis, a small torque turning the hips to face the opponent and a
    /// push in the direction it wants to walk. That help is the part a policy trained in MuJoCo will not
    /// need, and it is all in <see cref="Assist"/> so it can be deleted in one piece. Everything it does to
    /// the opponent goes through real contacts: the glove is a collider on a driven arm with human torque
    /// limits, and a hit is whatever impulse the solver reports.
    ///
    /// When the fighter is knocked down the help is switched off and the drives go slack, so the fall, and
    /// the body on the canvas, are plain physics.
    /// </summary>
    [RequireComponent(typeof(Fighter))]
    public class ScriptedBoxer : MonoBehaviour
    {
        [Header("Balance assist (stand-in for a trained policy)")]
        public float heightKp = 9000f;
        public float heightKd = 900f;
        public float uprightKp = 3000f;
        public float uprightKd = 300f;
        public float yawKp = 60f;
        public float yawKd = 4f;
        [Tooltip("Seconds to reach the wanted walking speed. Shorter is a harder shove.")]
        public float moveTime = 0.3f;
        [Tooltip("The walk is done by the legs; this only makes up for what the feet slip.")]
        public float maxMoveForce = 400f;
        [Tooltip("How far the stance sits below straight legs, metres.")]
        public float crouch = 0.04f;
        [Tooltip("Height of the point on the torso the upright string is tied to, above the waist joint.")]
        public float chestHeight = 0.36f;
        [Tooltip("Degrees the hips are turned away from square, so the left side leads.")]
        public float stanceYaw = 22f;

        [Header("Ring")]
        public Vector3 ringCentre;
        public float ringHalf = 3.05f;
        [Tooltip("Where this fighter walks to when the referee is counting.")]
        public Vector3 neutralSpot;

        [Header("Debug")]
        [Tooltip("Hold the guard and throw nothing.")]
        public bool holdGuard;

        struct Arm { public float flex, abduct, elbow; }

        struct Punch
        {
            public bool active;
            public PunchType type;
            public int hand;
            public float t;
            public float aim;
        }

        const float Windup = 0.08f, Extend = 0.12f, Hold = 0.05f, Retract = 0.20f;

        Fighter _f;
        ArticulationDrive[] _bx, _by, _bz;
        float _driveScale = 1f;
        Punch _punch;
        bool _comboQueued;
        float _cooldown, _circleTimer, _circleDir, _gait, _swayPhase, _leanForward, _punchCrouch;
        // (_gait is the walk cycle's phase, radians)
        Vector3 _wantVelocity, _dir = Vector3.forward;

        // Style, with the numbers a profile would supply when there is none.
        float Aggression => _f.profile != null ? _f.profile.aggression : 0.5f;
        float Range => _f.profile != null ? _f.profile.range : 0.95f;
        float FootSpeed => _f.profile != null ? _f.profile.footSpeed : 0.9f;
        float Combo => _f.profile != null ? _f.profile.combo : 0.3f;
        float HookBias => _f.profile != null ? _f.profile.hookBias : 0.25f;
        float BodyBias => _f.profile != null ? _f.profile.bodyBias : 0.15f;
        float UppercutBias => _f.profile != null ? _f.profile.uppercutBias : 0.1f;
        float HeadMovement => _f.profile != null ? _f.profile.headMovement : 0.4f;
        float Guard => _f.profile != null ? _f.profile.guard : 0.5f;

        void Awake()
        {
            _f = GetComponent<Fighter>();
            int n = _f.parts.Length;
            _bx = new ArticulationDrive[n];
            _by = new ArticulationDrive[n];
            _bz = new ArticulationDrive[n];
            for (int i = 0; i < n; i++)
            {
                ArticulationBody ab = _f.parts[i].body;
                _bx[i] = ab.xDrive; _by[i] = ab.yDrive; _bz[i] = ab.zDrive;
            }
            _swayPhase = Random.value * 6.28f;
            _cooldown = 0.6f + Random.value;
        }

        /// <summary>Clears what the brain was in the middle of. Called when the fighter is put back on its feet.</summary>
        public void ResetBrain()
        {
            _punch = default;
            _comboQueued = false;
            _cooldown = 0.5f + Random.value * 0.8f;
            _wantVelocity = Vector3.zero;
            _leanForward = 0f;
            _punchCrouch = 0f;
        }

        void FixedUpdate()
        {
            if (!Bout.SimRunning || _f.pelvis == null || _f.opponent == null) return;
            float dt = Time.fixedDeltaTime;
            float a = _f.Authority;

            // Slack when out, soft while getting up, and a little softer when the tank is empty.
            float scale = a < 0.25f ? 0.05f : Mathf.Lerp(0.3f, 1f, Mathf.InverseLerp(0.25f, 1f, a)) * Mathf.Lerp(0.8f, 1f, _f.Stamina);
            SetDriveScale(scale);

            Think(dt, a);
            Assist(a);
            Pose(dt, a);
        }

        // ------------------------------------------------------------ deciding

        void Think(float dt, float authority)
        {
            Fighter o = _f.opponent;
            Vector3 me = _f.pelvis.body.worldCenterOfMass;
            Vector3 to = o.pelvis.body.worldCenterOfMass - me;
            to.y = 0f;
            float dist = to.magnitude;
            if (dist > 0.05f) _dir = to / dist;
            Vector3 side = Vector3.Cross(Vector3.up, _dir);

            bool fighting = Bout.Fighting && !holdGuard && !_f.IsDown && !o.IsDown && authority > 0.6f;
            Vector3 v;

            if (!Bout.Fighting || o.IsDown)
            {
                // Neutral corner while the referee counts, or between the bell and the fight.
                Vector3 home = neutralSpot - me;
                home.y = 0f;
                v = home.magnitude > 0.35f && o.IsDown ? home.normalized * FootSpeed * 0.8f : Vector3.zero;
            }
            else
            {
                float want = Range + (_f.Staggered ? 0.55f : 0f) + (_f.Stamina < 0.3f ? 0.2f : 0f);
                float forward = Mathf.Clamp((dist - want) * 2.5f, -1f, 1f) * FootSpeed;
                if (_punch.active && _punch.t < Windup + Extend) forward += 0.35f;   // step in behind the punch

                _circleTimer -= dt;
                if (_circleTimer <= 0f)
                {
                    _circleTimer = Random.Range(1.5f, 4f);
                    float r = Random.value;
                    _circleDir = r < 0.4f ? 1f : r < 0.8f ? -1f : 0f;
                }
                v = _dir * forward + side * (_circleDir * 0.35f * FootSpeed);
            }

            // The ropes: the nearer the edge, the harder the pull back to the middle.
            Vector3 fromCentre = me - ringCentre;
            fromCentre.y = 0f;
            float edge = Mathf.Max(Mathf.Abs(fromCentre.x), Mathf.Abs(fromCentre.z)) - (ringHalf - 0.9f);
            if (edge > 0f) v -= fromCentre.normalized * edge * 2.2f;

            _wantVelocity = Vector3.ClampMagnitude(v, 1.6f) * Mathf.Lerp(0.6f, 1f, _f.Stamina) * Mathf.Clamp01(authority);

            // Punches.
            if (_punch.active)
            {
                _punch.t += dt;
                if (_punch.t >= Windup + Extend + Hold + Retract)
                {
                    int last = _punch.hand;
                    _punch.active = false;
                    if (_comboQueued && fighting)
                    {
                        _comboQueued = false;
                        StartPunch(-last, dist);
                    }
                    else _cooldown = Mathf.Lerp(1.5f, 0.4f, Aggression) * Random.Range(0.7f, 1.4f) / Mathf.Lerp(0.6f, 1f, _f.Stamina);
                }
                return;
            }

            _cooldown -= dt;
            if (fighting && _cooldown <= 0f && dist < Range + 0.38f && _f.Stamina > 0.18f)
                StartPunch(0, dist);
        }

        void StartPunch(int forcedHand, float dist)
        {
            float hook = HookBias, body = BodyBias, upper = UppercutBias;
            float straight = Mathf.Max(0.2f, 1f - hook - body - upper);
            float r = Random.value * (hook + body + upper + straight);

            PunchType type;
            int hand;
            if (r < straight)
            {
                bool jab = Random.value < 0.6f;
                type = jab ? PunchType.Jab : PunchType.Cross;
                hand = jab ? -1 : 1;
            }
            else if (r < straight + hook) { type = PunchType.Hook; hand = Random.value < 0.5f ? -1 : 1; }
            else if (r < straight + hook + body) { type = PunchType.Body; hand = Random.value < 0.5f ? -1 : 1; }
            else { type = PunchType.Uppercut; hand = Random.value < 0.7f ? 1 : -1; }

            if (forcedHand != 0)
            {
                hand = forcedHand;
                if (type == PunchType.Jab || type == PunchType.Cross) type = hand < 0 ? PunchType.Jab : PunchType.Cross;
            }

            Fighter o = _f.opponent;
            BodyPart shoulder = hand < 0 ? _f.upperArmL : _f.upperArmR;
            Vector3 target = type == PunchType.Body
                ? o.torso.body.worldCenterOfMass - Vector3.up * 0.06f
                : o.head.body.worldCenterOfMass;
            Vector3 d = target - shoulder.transform.position;
            float elevation = Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg;

            _punch = new Punch { active = true, type = type, hand = hand, t = 0f, aim = Mathf.Clamp(90f + elevation, 45f, 125f) };
            _comboQueued = Random.value < Combo;
            _f.NotePunch(hand, type);
        }

        // ------------------------------------------------------------ outside help

        void Assist(float a)
        {
            if (a <= 0.01f) return;
            ArticulationBody pb = _f.pelvis.body, tb = _f.torso.body;
            Vector3 p = pb.worldCenterOfMass;
            Vector3 v = pb.linearVelocity;

            // Height: a spring holding the pelvis at stance height. It never pulls down and never lifts
            // more than the fighter weighs. Standing, it does almost nothing: the legs carry the body.
            float weight = _f.totalMass * 9.81f;
            float targetH = _f.standingPelvisHeight - crouch - _punchCrouch;
            float fy = (targetH - _f.PelvisHeight) * heightKp - v.y * heightKd;
            fy = Mathf.Clamp(fy, 0f, weight) * a;

            // Walking: a push towards the wanted velocity.
            Vector3 flat = new Vector3(v.x, 0f, v.z);
            Vector3 move = Vector3.ClampMagnitude((_wantVelocity - flat) * (_f.totalMass / moveTime), maxMoveForce) * a;
            pb.AddForce(new Vector3(move.x, fy, move.z));

            // Upright: a string from a point on the chest to the spot above the pelvis, sideways and
            // upwards. The upward part is what stands a fighter back up the right way round after a
            // knockdown; without it the pelvis was hauled to standing height with the body still hanging
            // head-down underneath it.
            Vector3 chest = tb.transform.TransformPoint(new Vector3(0f, chestHeight, 0f));
            Vector3 chestVelocity = tb.GetPointVelocity(chest);
            Vector3 want = p + _dir * _leanForward;
            Vector3 err = want - chest;
            err.y = 0f;
            Vector3 rel = chestVelocity - v;
            rel.y = 0f;
            Vector3 pull = (err * uprightKp - rel * uprightKd) * a;

            float floorY = p.y - _f.PelvisHeight;
            float chestTarget = floorY + targetH + WaistRise + chestHeight - 0.02f;
            float lift = (chestTarget - chest.y) * heightKp * 0.7f - chestVelocity.y * heightKd * 0.7f;
            pull.y = Mathf.Clamp(lift, 0f, weight * 0.7f) * a;
            tb.AddForceAtPosition(pull, chest);

            // Facing: hips turned stanceYaw away from square on.
            Vector3 wantForward = Quaternion.AngleAxis(stanceYaw, Vector3.up) * _dir;
            Vector3 forward = pb.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude > 1e-4f)
            {
                float yawErr = Vector3.SignedAngle(forward, wantForward, Vector3.up) * Mathf.Deg2Rad;
                pb.AddTorque(Vector3.up * ((yawErr * yawKp - pb.angularVelocity.y * yawKd) * a));
            }
        }

        // ------------------------------------------------------------ joint targets

        void Pose(float dt, float authority)
        {
            if (authority < 0.25f)
            {
                Relax();
                return;
            }

            // The guard. A tight one holds both gloves in front of the face and takes most straight
            // punches on the arms; a loose one carries the lead hand at chest height, which is quicker to
            // punch from and leaves the head open. How tight is the profile's call, and it sags as the
            // fighter tires.
            float guard = Guard * Mathf.Lerp(0.5f, 1f, _f.Stamina);
            Arm lead = new Arm { flex = Mathf.Lerp(30f, 62f, guard), abduct = 12f, elbow = Mathf.Lerp(84f, 120f, guard) };
            Arm rear = new Arm { flex = Mathf.Lerp(36f, 52f, guard), abduct = 8f, elbow = Mathf.Lerp(118f, 136f, guard) };
            if (_f.Staggered) { lead.flex += 14f; rear.flex += 14f; lead.elbow = 138f; rear.elbow = 140f; }

            float sway = Mathf.Sin(Time.time * 2.1f + _swayPhase) * HeadMovement * 7f;
            float waistYaw = -stanceYaw * 0.45f;
            float waistPitch = 6f;
            float punchCrouch = 0f;

            if (_punch.active)
            {
                float t = _punch.t;
                // u rises through the wind-up and falls away as the punch goes; k is the punch itself.
                float u = t < Windup ? Smooth(t / Windup) : 1f - Smooth((t - Windup) / Extend);
                float k = t < Windup ? 0f
                        : t < Windup + Extend ? Smooth((t - Windup) / Extend)
                        : t < Windup + Extend + Hold ? 1f
                        : 1f - Smooth((t - Windup - Extend - Hold) / Retract);
                u = Mathf.Clamp01(u);

                Arm wind, strike;
                float yaw, windYaw = 0f, pitch = 0f, dip = 0f;
                switch (_punch.type)
                {
                    case PunchType.Jab:
                        wind = new Arm { flex = 50f, abduct = 10f, elbow = 135f };
                        strike = new Arm { flex = _punch.aim, abduct = 6f, elbow = 6f };
                        yaw = 14f;
                        break;
                    case PunchType.Cross:
                        wind = new Arm { flex = 40f, abduct = 14f, elbow = 140f };
                        strike = new Arm { flex = _punch.aim, abduct = 6f, elbow = 6f };
                        yaw = 40f; pitch = 6f;
                        break;
                    case PunchType.Hook:
                        wind = new Arm { flex = 45f, abduct = 45f, elbow = 100f };
                        strike = new Arm { flex = 80f, abduct = 62f, elbow = 95f };
                        yaw = 55f; windYaw = -15f;
                        break;
                    case PunchType.Uppercut:
                        wind = new Arm { flex = 5f, abduct = 12f, elbow = 95f };
                        strike = new Arm { flex = 100f, abduct = 12f, elbow = 80f };
                        yaw = 25f; pitch = -6f; dip = 0.08f;
                        break;
                    default: // Body
                        wind = new Arm { flex = 35f, abduct = 12f, elbow = 130f };
                        strike = new Arm { flex = _punch.aim, abduct = 10f, elbow = 20f };
                        yaw = 35f; pitch = 16f; dip = 0.1f;
                        break;
                }

                // A left punch brings the left shoulder forward, which is a turn to the right.
                float turn = _punch.hand < 0 ? 1f : -1f;
                ref Arm arm = ref (_punch.hand < 0 ? ref lead : ref rear);
                Arm g = arm;
                arm.flex = g.flex + u * (wind.flex - g.flex) + k * (strike.flex - g.flex);
                arm.abduct = g.abduct + u * (wind.abduct - g.abduct) + k * (strike.abduct - g.abduct);
                arm.elbow = g.elbow + u * (wind.elbow - g.elbow) + k * (strike.elbow - g.elbow);
                waistYaw += turn * (u * windYaw + k * yaw);
                waistPitch += k * pitch;
                punchCrouch = dip * Mathf.Max(u, k);
            }

            _punchCrouch = punchCrouch;
            _leanForward = Mathf.Sin(waistPitch * Mathf.Deg2Rad) * chestHeight;

            // Arms. Flexion carries the hand forward (a negative turn about the link's X in Unity's
            // left-handed frame); abduction lifts it out to its own side.
            Sph(_f.upperArmL, -lead.flex, 0f, -lead.abduct);
            Hinge(_f.forearmL, -lead.elbow);
            Sph(_f.upperArmR, -rear.flex, 0f, rear.abduct);
            Hinge(_f.forearmR, -rear.elbow);

            // Trunk and head.
            Sph(_f.torso, waistPitch, waistYaw, sway);
            Sph(_f.head, 8f, -waistYaw * 0.5f, -sway * 0.5f);

            // Legs: a staggered stance, and a walk the legs actually do. Each leg spends half the cycle
            // planted, sweeping back under the hip, which is what moves the body (the foot is held by
            // friction), and half lifted, swinging forward. The sweep is sized from the speed wanted and
            // the step rate, forwards and sideways separately, so the same cycle walks in any direction.
            Vector3 local = _f.pelvis.transform.InverseTransformDirection(_wantVelocity);
            float want = new Vector2(local.x, local.z).magnitude;
            float hz = want > 0.08f ? Mathf.Lerp(1.3f, 2.3f, Mathf.Clamp01(want / 1.2f)) : 0f;
            _gait += dt * hz * 2f * Mathf.PI;
            float amp = Mathf.Clamp01(want / 0.4f);
            float sweepForward = 0f, sweepSide = 0f;
            if (hz > 0f)
            {
                // Two steps a cycle, each carrying the hip 2 L sin(sweep) over the foot.
                float perCycle = 4f * LegLength * hz;
                sweepForward = Mathf.Asin(Mathf.Clamp(local.z / perCycle, -0.35f, 0.35f)) * Mathf.Rad2Deg;
                sweepSide = Mathf.Asin(Mathf.Clamp(local.x / perCycle, -0.25f, 0.25f)) * Mathf.Rad2Deg;
            }

            // Hip-to-ankle height the stance is asking for: the knees are bent to exactly that, so the legs
            // agree with the height spring instead of pushing against it.
            float reach = _f.standingPelvisHeight - crouch - _punchCrouch - HipDrop - AnkleHeight;
            Leg(_f.thighL, _f.shinL, _f.footL, -1f, _gait, 20f, reach, sweepForward, sweepSide, amp);
            Leg(_f.thighR, _f.shinR, _f.footR, 1f, _gait + Mathf.PI, -8f, reach, sweepForward, sweepSide, amp);
        }

        const float WaistRise = 0.08f;                 // waist joint above the pelvis origin
        const float SegmentLength = 0.42f;            // thigh and shin are the same length
        const float LegLength = SegmentLength * 2f;
        const float HipDrop = 0.05f;                   // hip joint below the pelvis origin
        const float AnkleHeight = 0.08f;               // ankle joint above the sole

        static void Leg(BodyPart thigh, BodyPart shin, BodyPart foot, float side, float phase,
                        float baseFlex, float reach, float sweepForward, float sweepSide, float amp)
        {
            float c = Mathf.Cos(phase), s = Mathf.Sin(phase);
            // sin >= 0 is the planted half (the hip angle runs from +sweep to -sweep); the rest is the swing.
            float lift = Mathf.Max(0f, -s) * 34f * amp;
            float flex = baseFlex + sweepForward * c;
            // A foot placed towards +x is abduction for the right leg and adduction for the left.
            float abduct = 9f + side * sweepSide * c;

            // Two-bone reach: with the thigh at `flex` from vertical, the shin angle that puts the ankle
            // `reach` below the hip. The knee angle is the difference between the two.
            float h = reach / (SegmentLength * Mathf.Cos(abduct * Mathf.Deg2Rad));
            float shinAngle = Mathf.Acos(Mathf.Clamp(h - Mathf.Cos(flex * Mathf.Deg2Rad), -1f, 1f)) * Mathf.Rad2Deg;
            float knee = flex + shinAngle + lift;
            flex += lift * 0.5f;

            Sph(thigh, -flex, 0f, side * abduct);
            Hinge(shin, knee);
            Sph(foot, -Mathf.Clamp(knee - flex, -20f, 22f), 0f, 0f);
        }

        /// <summary>The velocity the brain is asking the legs for, world space. Diagnostics read it.</summary>
        public Vector3 WantVelocity => _wantVelocity;

        void Relax()
        {
            foreach (BodyPart p in _f.parts)
            {
                if (p == _f.pelvis) continue;
                if (p.body.jointType == ArticulationJointType.SphericalJoint) Sph(p, 0f, 0f, 0f);
                else Hinge(p, p.kind == PartKind.Forearm ? -25f : p.kind == PartKind.Shin ? 20f : 0f);
            }
        }

        static float Smooth(float x)
        {
            x = Mathf.Clamp01(x);
            return x * x * (3f - 2f * x);
        }

        static void Sph(BodyPart p, float pitch, float yaw, float roll)
        {
            ArticulationBody ab = p.body;
            ab.SetDriveTarget(ArticulationDriveAxis.X, RigSigns.S.x * pitch);
            ab.SetDriveTarget(ArticulationDriveAxis.Y, RigSigns.S.y * yaw);
            ab.SetDriveTarget(ArticulationDriveAxis.Z, RigSigns.S.z * roll);
        }

        static void Hinge(BodyPart p, float pitch)
        {
            p.body.SetDriveTarget(ArticulationDriveAxis.X, RigSigns.S.x * pitch);
        }

        void SetDriveScale(float s)
        {
            if (Mathf.Abs(s - _driveScale) < 0.04f) return;
            _driveScale = s;
            float damp = Mathf.Lerp(0.35f, 1f, s);
            for (int i = 0; i < _f.parts.Length; i++)
            {
                BodyPart part = _f.parts[i];
                if (part == _f.pelvis) continue;
                ArticulationBody ab = part.body;

                ArticulationDrive d = _bx[i];
                d.target = ab.xDrive.target; d.stiffness *= s; d.damping *= damp;
                ab.xDrive = d;
                if (ab.jointType != ArticulationJointType.SphericalJoint) continue;

                d = _by[i];
                d.target = ab.yDrive.target; d.stiffness *= s; d.damping *= damp;
                ab.yDrive = d;
                d = _bz[i];
                d.target = ab.zDrive.target; d.stiffness *= s; d.damping *= damp;
                ab.zDrive = d;
            }
        }
    }
}
