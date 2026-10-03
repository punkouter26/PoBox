using System;
using Mujoco;
using UnityEngine;

namespace PoBox.Mj
{
    /// <summary>
    /// A bout on the MuJoCo plugin: two boxers, each the other's opponent, stepped as the trainer steps them
    /// (training/envs/boxing.py): MuJoCo every 0.005 s, and every fourth step each boxer observes, its policy
    /// answers and its targets go into mjData.ctrl. No Unity physics anywhere: the plugin moves the scene's
    /// objects to where MuJoCo's bodies are.
    ///
    /// Also what a bout needs around the policies, each done by writing mjData:
    ///   * a boxer can be held in its guard on its spot while the referee counts over the other (the first
    ///     moment of a training episode when it is let go), and stood up somewhere;
    ///   * a boxer that is down is passed through by the other (its shapes moved to a layer only the ring is on);
    ///   * punches are measured as the trainer measures them, once a control step: a glove arriving at the
    ///     other's head or body faster than 1 m/s along the surface's normal, having been 30 cm clear of it and a
    ///     quarter of a second since its last; and a punch coming at a head that meets a glove or a forearm on
    ///     the way is a punch stopped. Each is raised as an event, in Unity's frame.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public unsafe class MjRing : MonoBehaviour
    {
        public MjBoxer red, blue;
        [Tooltip("MuJoCo's step, seconds: the trainer's.")]
        public float physicsStep = 0.005f;
        [Tooltip("Physics runs only while this is set: the bout clears it between rounds and when the result is up.")]
        public bool running = true;
        [Tooltip("The middle of the ring, Unity's x and z (which are MuJoCo's x and y).")]
        public Vector2 centre;
        public float ringHalf = 3.05f;

        public const float HitCap = 9f;
        public bool Ready => red != null && red.Bound && blue != null && blue.Bound;
        public double PhysicsMs { get; private set; }
        public double PolicyMs { get; private set; }

        /// <summary>A punch landed: who threw it, which hand (0 left, 1 right), on the head or not, its closing speed (m/s, to 9), and where (Unity).</summary>
        public event Action<MjBoxer, int, bool, float, Vector3> PunchLanded;
        /// <summary>A punch stopped on a glove or a forearm on its way to the head: who stopped it, and how fast it was coming.</summary>
        public event Action<MjBoxer, float> PunchStopped;
        /// <summary>A foot has come down: whose, where (Unity), and how fast it was moving.</summary>
        public event Action<MjBoxer, Vector3, float> FootStep;
        /// <summary>
        /// A part of a boxer other than a foot has come down on the canvas: whose, where (Unity), and the impulse
        /// MuJoCo's solver gave it through the floor over that control step, N s.
        /// </summary>
        public event Action<MjBoxer, Vector3, float> BodyLanded;
        /// <summary>A control step is over: the boxers' states are the end of it.</summary>
        public event Action ControlStep;

        MujocoLib.mjModel_* _m;
        MujocoLib.mjData_* _d;
        MjBoxer[] _b;
        int _sub, _decimation = 4;
        double _dt;
        readonly bool[] _held = new bool[2], _ghost = new bool[2], _down = new bool[2], _has = new bool[2];
        readonly double[] _holdX = new double[2], _holdY = new double[2], _holdYaw = new double[2], _ghostFor = new double[2];
        int[][] _geoms = new int[2][];
        readonly bool[,] _armed = new bool[2, 2], _blocking = new bool[2, 2], _footDown = new bool[2, 2];
        readonly double[,] _cool = new double[2, 2];
        readonly double[,,] _closing = new double[2, 2, 2], _speed = new double[2, 2, 2], _prevGlove = new double[2, 2, 3], _gloveVel = new double[2, 2, 3];
        readonly double[,] _prevHead = new double[2, 3], _prevBody = new double[2, 3];
        readonly double[,,] _prevFoot = new double[2, 2, 3];
        readonly double[,] _slide = new double[2, 2];
        int[] _owner = new int[0];          // which boxer a shape belongs to, -1 for none; feet are -1 here
        double[] _load = new double[0], _clear = new double[0], _at = new double[0];
        readonly System.Diagnostics.Stopwatch _clock = new System.Diagnostics.Stopwatch();
        long _began;

        void Awake()
        {
            Time.fixedDeltaTime = physicsStep;
            _clock.Start();
            var scene = MjScene.Instance;
            scene.postInitEvent += OnModel;
            scene.preUpdateEvent += BeforePhysics;
            scene.postUpdateEvent += AfterPhysics;
        }

        void OnDestroy()
        {
            if (!MjScene.InstanceExists) return;
            var scene = MjScene.Instance;
            scene.postInitEvent -= OnModel;
            scene.preUpdateEvent -= BeforePhysics;
            scene.postUpdateEvent -= AfterPhysics;
        }

        void Update()
        {
            if (MjScene.InstanceExists) MjScene.Instance.enabled = running;
        }

        void OnModel(object sender, MjStepArgs a)
        {
            _m = a.model; _d = a.data;
            if (_m == null || red == null || blue == null) return;
            _b = new[] { red, blue };
            foreach (MjBoxer b in _b) b.ringCentre = centre;
            red.Bind(_m, _d);
            blue.Bind(_m, _d);
            red.opponent = blue;
            blue.opponent = red;
            _m->opt.timestep = 1.0 / red.Cfg.physics_hz;
            _decimation = Math.Max(1, red.Cfg.control_decimation);
            _dt = _m->opt.timestep * _decimation;
            for (int k = 0; k < 2; k++)
            {
                var shapes = _b[k].GetComponentsInChildren<MjGeom>();
                _geoms[k] = new int[shapes.Length];
                for (int i = 0; i < shapes.Length; i++) _geoms[k][i] = shapes[i].MujocoId;
                _b[k].SetEpisodeAt(MjBoxer.Match, 0, 0, 0, 0, 0);
            }
            _owner = new int[_m->ngeom]; _load = new double[_m->ngeom]; _clear = new double[_m->ngeom]; _at = new double[3 * _m->ngeom];
            for (int g = 0; g < _m->ngeom; g++) { _owner[g] = -1; _clear[g] = 1.0; }
            for (int k = 0; k < 2; k++)
                foreach (int g in _geoms[k])
                    if (g != _b[k].FootGeom(0) && g != _b[k].FootGeom(1)) _owner[g] = k;
            // Everything that is not a boxer (floor, ropes, cubes) goes on both layers, so that a boxer moved to
            // layer 2 while it is down still lies on the canvas, and only the other boxer passes through it.
            var mine = new System.Collections.Generic.HashSet<int>(_geoms[0]);
            mine.UnionWith(_geoms[1]);
            for (int g = 0; g < _m->ngeom; g++)
                if (!mine.Contains(g)) { _m->geom_contype[g] = 3; _m->geom_conaffinity[g] = 3; }
        }

        int Slot(MjBoxer b) => b == blue ? 1 : 0;

        static Vector3 ToUnity(double x, double y, double z) => new Vector3((float)x, (float)z, (float)y);

        // ---------------------------------------------------------------- what the bout asks

        /// <summary>Stands a boxer in its guard on a spot (Unity), facing a way, with nothing moving. The other is left as it is.</summary>
        public void Place(MjBoxer b, Vector3 floorPoint, Vector3 facing)
        {
            if (!Ready) return;
            int k = Slot(b);
            b.ResetToGuard(floorPoint.x, floorPoint.z, Math.Atan2(facing.z, facing.x));
            b.SetEpisodeAt(MjBoxer.Match, 0, 0, 0, 0, 0);
            b.DriveScale = 1.0;
            _held[k] = false;
            SetGhost(k, false);
            _has[0] = _has[1] = false;      // the other's target has just jumped too
            MjScene.Instance.SyncUnityToMjState();
        }

        /// <summary>
        /// Holds a boxer in its guard where it stands, looking a way (Unity), until it is let go: the boxer left
        /// standing while the referee counts over the other. Let go facing its opponent a step and a half away
        /// with nothing moving, it is at exactly the first moment of a training episode.
        /// </summary>
        public void Hold(MjBoxer b, Vector3 facing)
        {
            if (!Ready) return;
            int k = Slot(b);
            if (!_held[k])
            {
                _held[k] = true;
                double limit = ringHalf - 0.5;          // a boxer caught on the ropes is stood a step in
                var pelvis = b.transform.Find(b.prefix + "pelvis");
                Vector3 at = pelvis != null ? pelvis.position : b.transform.position;
                _holdX[k] = Math.Max(centre.x - limit, Math.Min(centre.x + limit, at.x));
                _holdY[k] = Math.Max(centre.y - limit, Math.Min(centre.y + limit, at.z));
            }
            if (facing.x * facing.x + facing.z * facing.z > 1e-4f) _holdYaw[k] = Math.Atan2(facing.z, facing.x);
        }

        public void Release(MjBoxer b)
        {
            _held[Slot(b)] = false;
            _has[0] = _has[1] = false;
        }

        public bool Held(MjBoxer b) => _held[Slot(b)];

        /// <summary>The bout says whether a boxer is down: one that is down is passed through by the other.</summary>
        public void SetDown(MjBoxer b, bool down) => _down[Slot(b)] = down;

        /// <summary>A glove's velocity over the last control step, Unity's frame.</summary>
        public Vector3 GloveVelocity(MjBoxer b, int hand)
        {
            int k = Slot(b);
            return ToUnity(_gloveVel[k, hand, 0], _gloveVel[k, hand, 1], _gloveVel[k, hand, 2]);
        }

        /// <summary>A glove's closing speed on the other's head or body over the last control step or the one before, m/s, 0 to 9.</summary>
        public float HitSpeed(MjBoxer b, int hand, bool head) => (float)_speed[Slot(b), hand, head ? 0 : 1];

        public bool FootDown(MjBoxer b, int foot) => _footDown[Slot(b), foot];

        /// <summary>How fast a boxer's planted foot is sliding over the canvas, m/s (the faster of the two; 0 with neither down), and where that foot is (Unity).</summary>
        public float FootSlide(MjBoxer b, out Vector3 at)
        {
            int k = Slot(b), foot = _slide[k, 1] > _slide[k, 0] ? 1 : 0;
            at = b.transform.position;
            if (!Ready) return 0f;
            double* f = Geom(b.FootGeom(foot));
            at = ToUnity(f[0], f[1], 0.0);
            return (float)_slide[k, foot];
        }

        // ---------------------------------------------------------------- stepping

        void SetGhost(int k, bool ghost)
        {
            if (_ghost[k] == ghost || _geoms[k] == null) return;
            _ghost[k] = ghost;
            int layer = ghost ? 2 : 1;          // the ring and the floor are on both; the other boxer only on 1
            foreach (int g in _geoms[k])
            {
                _m->geom_contype[g] = layer;
                _m->geom_conaffinity[g] = layer;
            }
        }

        void BeforePhysics(object sender, MjStepArgs a)
        {
            if (!Ready) return;
            for (int k = 0; k < 2; k++)
            {
                bool ghost = _down[k];
                // Just up, it stays a ghost until the two are a step apart (or three seconds have gone): given its
                // body back while the other stands inside it, the contact throws them both across the ring.
                if (!ghost && _ghost[k])
                {
                    _ghostFor[k] += _m->opt.timestep;
                    Vector3 apart = red.transform.position - blue.transform.position;
                    var pa = red.transform.Find(red.prefix + "pelvis"); var pb = blue.transform.Find(blue.prefix + "pelvis");
                    if (pa != null && pb != null) apart = pa.position - pb.position;
                    apart.y = 0f;
                    if (apart.magnitude < 0.6f && _ghostFor[k] < 3.0) ghost = true;
                }
                if (!ghost) _ghostFor[k] = 0.0;
                SetGhost(k, ghost);
                // A held boxer is put back in its guard on its spot before every step: it stands and waits.
                if (_held[k]) _b[k].ResetToGuard(_holdX[k], _holdY[k], _holdYaw[k]);
            }
            if (_sub == 0)
            {
                long t0 = _clock.ElapsedTicks;
                foreach (MjBoxer b in _b)
                {
                    b.Observe();
                    b.Infer();
                    b.Drive();
                }
                PolicyMs += 0.05 * ((_clock.ElapsedTicks - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency - PolicyMs);
            }
            else
            {
                red.HoldTargets();
                blue.HoldTargets();
            }
            _began = _clock.ElapsedTicks;
        }

        void AfterPhysics(object sender, MjStepArgs a)
        {
            if (!Ready) return;
            PhysicsMs += 0.05 * ((_clock.ElapsedTicks - _began) * 1000.0 / System.Diagnostics.Stopwatch.Frequency - PhysicsMs);
            _sub = (_sub + 1) % _decimation;
            if (_sub != 0) return;
            // As the trainer: the stand-in's (here the opponent's) velocity from where the bodies were left, then
            // the bodies brought up to date before anybody observes.
            red.AfterStep(_dt);
            blue.AfterStep(_dt);
            MujocoLib.mj_kinematics(_m, _d);
            TrackPunches();
            TrackCanvas();
            ControlStep?.Invoke();
        }

        // ---------------------------------------------------------------- punches and footsteps

        double* Geom(int g) => _d->geom_xpos + 3 * g;

        void TrackPunches()
        {
            for (int k = 0; k < 2; k++)
            {
                MjBoxer me = _b[k], them = _b[1 - k];
                double* head = Geom(them.HeadGeom), body = Geom(them.TorsoGeom), tm = _d->geom_xmat + 9 * them.TorsoGeom;
                double ax = tm[2], ay = tm[5], az = tm[8];                      // the body capsule's axis
                double half = _m->geom_size[3 * them.TorsoGeom + 1];
                double rHead = _m->geom_size[3 * them.HeadGeom], rBody = _m->geom_size[3 * them.TorsoGeom];
                for (int hand = 0; hand < 2; hand++)
                {
                    double* g = Geom(me.GloveGeom(hand));
                    double rGlove = _m->geom_size[3 * me.GloveGeom(hand)];
                    if (_has[k])
                    {
                        double vx = (g[0] - _prevGlove[k, hand, 0]) / _dt, vy = (g[1] - _prevGlove[k, hand, 1]) / _dt, vz = (g[2] - _prevGlove[k, hand, 2]) / _dt;
                        _gloveVel[k, hand, 0] = vx; _gloveVel[k, hand, 1] = vy; _gloveVel[k, hand, 2] = vz;
                        double hvx = (head[0] - _prevHead[k, 0]) / _dt, hvy = (head[1] - _prevHead[k, 1]) / _dt, hvz = (head[2] - _prevHead[k, 2]) / _dt;
                        double bvx = (body[0] - _prevBody[k, 0]) / _dt, bvy = (body[1] - _prevBody[k, 1]) / _dt, bvz = (body[2] - _prevBody[k, 2]) / _dt;
                        // Distance and direction to the nearest point of each zone: the head is a ball, the body a capsule.
                        double hx = head[0] - g[0], hy = head[1] - g[1], hz = head[2] - g[2];
                        double dHead = Math.Sqrt(hx * hx + hy * hy + hz * hz), ih = 1.0 / Math.Max(1e-4, dHead);
                        double along = Math.Max(-half, Math.Min(half, (g[0] - body[0]) * ax + (g[1] - body[1]) * ay + (g[2] - body[2]) * az));
                        double bx = body[0] + ax * along - g[0], by = body[1] + ay * along - g[1], bz = body[2] + az * along - g[2];
                        double dBody = Math.Sqrt(bx * bx + by * by + bz * bz), ib = 1.0 / Math.Max(1e-4, dBody);
                        // The smaller of "closing on the target" and "moving at all": a target that walks into a
                        // glove held still has not been punched.
                        double cHead = Math.Min(((vx - hvx) * hx + (vy - hvy) * hy + (vz - hvz) * hz) * ih, (vx * hx + vy * hy + vz * hz) * ih);
                        double cBody = Math.Min(((vx - bvx) * bx + (vy - bvy) * by + (vz - bvz) * bz) * ib, (vx * bx + vy * by + vz * bz) * ib);
                        _speed[k, hand, 0] = Math.Max(0.0, Math.Min(HitCap, Math.Max(cHead, _closing[k, hand, 0])));
                        _speed[k, hand, 1] = Math.Max(0.0, Math.Min(HitCap, Math.Max(cBody, _closing[k, hand, 1])));
                        _closing[k, hand, 0] = cHead; _closing[k, hand, 1] = cBody;

                        double gapHead = dHead - rGlove - rHead, gapBody = dBody - rGlove - rBody;
                        bool toHead = gapHead <= gapBody;
                        double nearest = toHead ? gapHead : gapBody, arriving = _speed[k, hand, toHead ? 0 : 1];
                        _cool[k, hand] = Math.Max(0.0, _cool[k, hand] - _dt);
                        bool landed = nearest < 0.012 && _armed[k, hand] && arriving > 1.0 && _cool[k, hand] <= 0.0;
                        _armed[k, hand] = (_armed[k, hand] && !landed) || nearest > 0.30;
                        if (landed)
                        {
                            _cool[k, hand] = 0.25;
                            PunchLanded?.Invoke(me, hand, toHead, (float)arriving, ToUnity(g[0], g[1], g[2]));
                        }
                    }
                    else _armed[k, hand] = true;
                    for (int c = 0; c < 3; c++) _prevGlove[k, hand, c] = g[c];

                    // A foot coming down.
                    double* f = Geom(me.FootGeom(hand));
                    bool down = me.FootDown(hand);
                    if (down && !_footDown[k, hand] && _has[k] && !_down[k])
                    {
                        double sx = f[0] - _prevFoot[k, hand, 0], sy = f[1] - _prevFoot[k, hand, 1], sz = f[2] - _prevFoot[k, hand, 2];
                        FootStep?.Invoke(me, ToUnity(f[0], f[1], 0.0), (float)(Math.Sqrt(sx * sx + sy * sy + sz * sz) / _dt));
                    }
                    // A foot that was down and still is, and has moved: it is being dragged or turned on.
                    double dx = f[0] - _prevFoot[k, hand, 0], dy = f[1] - _prevFoot[k, hand, 1];
                    _slide[k, hand] = down && _footDown[k, hand] && _has[k] && !_held[k] ? Math.Sqrt(dx * dx + dy * dy) / _dt : 0.0;
                    _footDown[k, hand] = down;
                    for (int c = 0; c < 3; c++) _prevFoot[k, hand, c] = f[c];
                }
                for (int c = 0; c < 3; c++) { _prevHead[k, c] = head[c]; _prevBody[k, c] = body[c]; }
            }
            for (int k = 0; k < 2; k++)
            {
                TrackBlocks(k);
                _has[k] = true;
            }
        }

        /// <summary>
        /// Bodies landing on the canvas, read from MuJoCo's own contacts: the force the solver put through the
        /// floor into each of a boxer's shapes (feet apart), and an event the control step a shape that has been
        /// clear of the canvas for a fifth of a second comes down on it.
        /// </summary>
        void TrackCanvas()
        {
            double* force = stackalloc double[6];
            for (int i = 0; i < _d->ncon; i++)
            {
                MujocoLib.mjContact_* c = _d->contact + i;
                int g = c->geom1, floor = c->geom2;
                if (_m->geom_type[g] == 0) { g = c->geom2; floor = c->geom1; }
                if (_m->geom_type[floor] != 0 || _owner[g] < 0 || c->efc_address < 0) continue;
                MujocoLib.mj_contactForce(_m, _d, i, force);
                _load[g] += force[0];
                for (int k = 0; k < 3; k++) _at[3 * g + k] = c->pos[k];
            }
            for (int g = 0; g < _load.Length; g++)
            {
                if (_owner[g] < 0) continue;
                if (_load[g] <= 0.0) { _clear[g] += _dt; continue; }
                // ponytail: the force at the one step in four that is looked at, times the control step; summing
                // every physics step would give the exact impulse if these numbers ever have to be compared.
                float impulse = (float)(_load[g] * _dt);
                if (_clear[g] >= 0.2 && impulse > 1f) BodyLanded?.Invoke(_b[_owner[g]], ToUnity(_at[3 * g], _at[3 * g + 1], 0.0), impulse);
                _clear[g] = 0.0;
                _load[g] = 0.0;
            }
        }

        /// <summary>
        /// A punch stopped (envs/boxing.py, the block): the other boxer's glove, coming at this one's head faster
        /// than 2 m/s and within 45 cm of it, touching this one's glove or forearm. Raised once when it begins.
        /// </summary>
        void TrackBlocks(int k)
        {
            MjBoxer me = _b[k], them = _b[1 - k];
            if (!_has[1 - k]) return;
            double* head = Geom(me.HeadGeom);
            double rHead = _m->geom_size[3 * me.HeadGeom];
            for (int hand = 0; hand < 2; hand++)
            {
                double* tg = Geom(them.GloveGeom(hand));
                double rTheirs = _m->geom_size[3 * them.GloveGeom(hand)];
                double coming = _speed[1 - k, hand, 0];
                double hx = head[0] - tg[0], hy = head[1] - tg[1], hz = head[2] - tg[2];
                bool near = Math.Sqrt(hx * hx + hy * hy + hz * hz) - rHead < 0.45;
                bool touching = false;
                for (int mine = 0; mine < 2 && !touching; mine++)
                {
                    double* mg = Geom(me.GloveGeom(mine));
                    double gx = tg[0] - mg[0], gy = tg[1] - mg[1], gz = tg[2] - mg[2];
                    if (Math.Sqrt(gx * gx + gy * gy + gz * gz) < rTheirs + _m->geom_size[3 * me.GloveGeom(mine)] + 0.03) touching = true;
                    int fa = me.ForearmGeom(mine);
                    double* fc = Geom(fa); double* fm = _d->geom_xmat + 9 * fa;
                    double fh = _m->geom_size[3 * fa + 1], fr = _m->geom_size[3 * fa];
                    double ox = tg[0] - fc[0], oy = tg[1] - fc[1], oz = tg[2] - fc[2];
                    double al = Math.Max(-fh, Math.Min(fh, ox * fm[2] + oy * fm[5] + oz * fm[8]));
                    double px = ox - fm[2] * al, py = oy - fm[5] * al, pz = oz - fm[8] * al;
                    if (Math.Sqrt(px * px + py * py + pz * pz) < rTheirs + fr + 0.03) touching = true;
                }
                bool blocked = touching && coming > 2.0 && near;
                if (blocked && !_blocking[k, hand]) PunchStopped?.Invoke(me, (float)coming);
                _blocking[k, hand] = blocked;
            }
        }
    }
}
