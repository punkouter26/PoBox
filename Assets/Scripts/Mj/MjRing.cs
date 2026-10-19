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
    ///
    /// A free-for-all hands it more than two (<see cref="boxers"/>): each boxer's punches are measured against its
    /// own opponent (MjBoxer.opponent, which the free-for-all keeps pointed at the nearest boxer standing), and a
    /// boxer that is down is passed through by everyone standing.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public unsafe class MjRing : MonoBehaviour
    {
        public MjBoxer red, blue;
        /// <summary>More than two boxers (a free-for-all), set before the model is compiled. None: red and blue, each the other's opponent.</summary>
        [NonSerialized] public MjBoxer[] boxers;
        /// <summary>
        /// Every body solid, down or not: boxers on the canvas are tripped over, piled on and pushed about (the
        /// free-for-all). Off, a boxer down is passed through by the one standing, as in a pair's bout.
        /// </summary>
        [NonSerialized] public bool solidBodies;
        [Tooltip("MuJoCo's step, seconds: the trainer's.")]
        public float physicsStep = 0.005f;
        [Tooltip("Physics runs only while this is set: the bout clears it between rounds and when the result is up.")]
        public bool running = true;
        [Tooltip("The middle of the ring, Unity's x and z (which are MuJoCo's x and y).")]
        public Vector2 centre;
        public float ringHalf = 3.05f;

        public const float HitCap = 9f;
        /// <summary>The mass behind a glove, as envs/boxing.py's PUNCH_MASS (Sim's Fighter.PunchMass).</summary>
        const double PunchMass = 2.2;
        public bool Ready => _bound;
        public double PhysicsMs { get; private set; }
        public double PolicyMs { get; private set; }

        /// <summary>A punch landed: who threw it, which hand (0 left, 1 right), on the head or not, its closing speed (m/s, to 9), and where (Unity).</summary>
        public event Action<MjBoxer, int, bool, float, Vector3> PunchLanded;
        /// <summary>A punch stopped on a glove or a forearm on its way to the head: who stopped it, and how fast it was coming.</summary>
        public event Action<MjBoxer, float, Vector3> PunchStopped;   // defender, closing speed m/s, where the glove was stopped
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
        Transform[] _pelvis;
        bool _bound;
        int _sub, _decimation = 4;
        MjCubePool _cubes;
        long _policyTicks;
        double _dt;
        // Per boxer (the first index), and per hand or foot.
        bool[] _held, _ghost, _down, _has, _rising;
        int[] _body;                        // solid bodies: 0 standing, 1 lying, 2 getting up (-1 not yet set)
        double[] _holdX, _holdY, _holdYaw, _ghostFor;
        int[] _target;                      // which boxer each one's punches were last measured against
        int[][] _geoms;
        bool[,] _armed, _blocking, _footDown;   // _blocking is the attacker's hand
        double[,] _cool;
        double[,,] _closing, _speed, _prevGlove, _gloveVel;
        double[,] _prevHead, _prevBody;
        double[,,] _prevFoot;
        double[,] _slide, _spin, _prevFootYaw;
        double[,] _push;                    // force and torque on each boxer's trunk for the next control step
        bool[] _pushed;
        int[] _owner = new int[0];          // which boxer a shape belongs to, -1 for none; feet are -1 here
        double[] _load = new double[0], _clear = new double[0], _at = new double[0];
        bool[] _rope = new bool[0];
        float[] _ropeLoad;
        Vector3[] _ropeAt;
        readonly System.Diagnostics.Stopwatch _clock = new System.Diagnostics.Stopwatch();
        long _began;

        void Allocate(int n)
        {
            _held = new bool[n]; _ghost = new bool[n]; _down = new bool[n]; _has = new bool[n]; _rising = new bool[n];
            _body = new int[n];
            for (int k = 0; k < n; k++) _body[k] = -1;
            _holdX = new double[n]; _holdY = new double[n]; _holdYaw = new double[n]; _ghostFor = new double[n];
            _target = new int[n];
            _geoms = new int[n][];
            _armed = new bool[n, 2]; _blocking = new bool[n, 2]; _footDown = new bool[n, 2];
            _cool = new double[n, 2];
            _push = new double[n, 6]; _pushed = new bool[n];
            _closing = new double[n, 2, 2]; _speed = new double[n, 2, 2]; _prevGlove = new double[n, 2, 3]; _gloveVel = new double[n, 2, 3];
            _prevHead = new double[n, 3]; _prevBody = new double[n, 3];
            _prevFoot = new double[n, 2, 3];
            _slide = new double[n, 2];
            _spin = new double[n, 2]; _prevFootYaw = new double[n, 2];
            _ropeLoad = new float[n]; _ropeAt = new Vector3[n];
        }

        void Awake()
        {
            Allocate(2);
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
            _bound = false;
            bool many = boxers != null && boxers.Length > 2;
            if (_m == null || (!many && (red == null || blue == null))) return;
            _b = many ? boxers : new[] { red, blue };
            int n = _b.Length;
            // A recompile of the same cast keeps who is held and where, who is down and who is rising: only the
            // collision layers start again, because the new model's geoms have theirs from the file.
            if (_held == null || _held.Length != n) Allocate(n);
            else for (int k = 0; k < n; k++) { _ghost[k] = false; _body[k] = -1; _has[k] = false; }
            _pelvis = new Transform[n];
            foreach (MjBoxer b in _b) b.ringCentre = centre;
            foreach (MjBoxer b in _b) b.Bind(_m, _d);
            // The ring's 8 cubes (the trainer's pool), parked where the trainer parks them until something throws one.
            _cubes = FindAnyObjectByType<MjCubePool>();
            if (_cubes != null) { _cubes.Bind(_m, _d); _cubes.ParkAll(); }
            if (!many)
            {
                red.opponent = blue;
                blue.opponent = red;
            }
            _m->opt.timestep = 1.0 / _b[0].Config.physics_hz;
            _decimation = Math.Max(1, _b[0].Config.control_decimation);
            _dt = _m->opt.timestep * _decimation;
            for (int k = 0; k < n; k++)
            {
                var shapes = _b[k].GetComponentsInChildren<MjGeom>();
                _geoms[k] = new int[shapes.Length];
                for (int i = 0; i < shapes.Length; i++) _geoms[k][i] = shapes[i].MujocoId;
                _b[k].SetEpisodeAt(MjBoxer.Match, 0, 0, 0, 0, 0);
                _pelvis[k] = _b[k].transform.Find(_b[k].prefix + "pelvis") ?? _b[k].transform;
                _target[k] = Target(k);
            }
            _owner = new int[_m->ngeom]; _load = new double[_m->ngeom]; _clear = new double[_m->ngeom]; _at = new double[3 * _m->ngeom];
            for (int g = 0; g < _m->ngeom; g++) { _owner[g] = -1; _clear[g] = 1.0; }
            for (int k = 0; k < n; k++)
                foreach (int g in _geoms[k])
                    if (g != _b[k].FootGeom(0) && g != _b[k].FootGeom(1)) _owner[g] = k;
            _rope = new bool[_m->ngeom];
            foreach (MjGeom s in FindObjectsByType<MjGeom>(FindObjectsInactive.Exclude))
                if (s.name.StartsWith("rope") && s.MujocoId >= 0 && s.MujocoId < _rope.Length) _rope[s.MujocoId] = true;
            // Everything that is not a boxer (floor, ropes, cubes) goes on both layers, so that a boxer moved to
            // layer 2 while it is down still lies on the canvas, and only the boxers standing pass through it.
            var mine = new System.Collections.Generic.HashSet<int>();
            for (int k = 0; k < n; k++) mine.UnionWith(_geoms[k]);
            for (int g = 0; g < _m->ngeom; g++)
                if (!mine.Contains(g)) { _m->geom_contype[g] = 3; _m->geom_conaffinity[g] = 3; }
            _bound = true;
        }

        int Slot(MjBoxer b)
        {
            if (_b == null) return b == blue ? 1 : 0;
            int k = Array.IndexOf(_b, b);
            return k < 0 ? 0 : k;
        }

        /// <summary>Whose head and body boxer k's gloves are measured against: its opponent (in a pair, the other one).</summary>
        int Target(int k)
        {
            int t = _b[k].opponent != null ? Array.IndexOf(_b, _b[k].opponent) : -1;
            return t >= 0 && t != k ? t : (k == 0 ? 1 : 0);
        }

        /// <summary>A boxer's place has jumped: it, and whoever is measured against it, start their speeds again.</summary>
        void Forget(int k)
        {
            _has[k] = false;
            for (int j = 0; j < _b.Length; j++) if (_target[j] == k) _has[j] = false;
        }

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
            // The reserve is left as it is: a boxer stood up by the referee is as tired as it was. The bout fills it (Fighter.Respawn).
            _held[k] = false;
            SetGhost(k, false);
            Forget(k);                      // the other's target has just jumped too
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
            int k = Slot(b);
            _held[k] = false;
            if (_b != null) Forget(k);
        }

        public bool Held(MjBoxer b) => _held[Slot(b)];

        /// <summary>The bout says whether a boxer is down: one that is down is passed through by the other.</summary>
        public void SetDown(MjBoxer b, bool down) => _down[Slot(b)] = down;

        /// <summary>The bout says whether a boxer down is getting up (its get-up policy has the body).</summary>
        public void SetRising(MjBoxer b, bool rising) => _rising[Slot(b)] = rising;

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

        /// <summary>How fast a boxer's planted foot is turning on the canvas, rad/s (the faster of the two; 0 with neither down), and where that foot is (Unity).</summary>
        public float FootSpin(MjBoxer b, out Vector3 at)
        {
            int k = Slot(b), foot = _spin[k, 1] > _spin[k, 0] ? 1 : 0;
            at = b.transform.position;
            if (!Ready) return 0f;
            double* f = Geom(b.FootGeom(foot));
            at = ToUnity(f[0], f[1], 0.0);
            return (float)_spin[k, foot];
        }

        /// <summary>How hard a boxer is pressed into the ropes, N (the solver's normal force at the last control step; 0 off them), and where (Unity).</summary>
        public float RopeLoad(MjBoxer b, out Vector3 at)
        {
            int k = Slot(b);
            at = _ropeAt[k];
            return _ropeLoad[k];
        }

        // ---------------------------------------------------------------- stepping

        /// <summary>
        /// Solid bodies (a free-for-all): who meets whom, by MuJoCo's contype and conaffinity bits. Standing (1, 1)
        /// meets standing and lying; lying (2, 3) meets standing and lying; getting up (4, 5) meets standing but
        /// not lying, so a boxer can climb out from under a pile as a person would roll out from under a limb. The
        /// ring and the floor are 3 and meet them all.
        /// </summary>
        void SetBody(int k, int state)
        {
            if (_body[k] == state || _geoms[k] == null) return;
            _body[k] = state;
            int type = state == 0 ? 1 : state == 1 ? 2 : 4, affinity = state == 0 ? 1 : state == 1 ? 3 : 5;
            foreach (int g in _geoms[k])
            {
                _m->geom_contype[g] = type;
                _m->geom_conaffinity[g] = affinity;
            }
        }

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
            for (int k = 0; k < _b.Length; k++)
            {
                // A pair's ring lets the one standing pass through the one down; a free-for-all's keeps every body solid.
                if (solidBodies)
                {
                    // Held in its guard over a count it is put back on its spot every step, so it goes on the getting-up
                    // layer: put back inside a body lying across its feet, the contact would throw that body about.
                    int state = !_down[k] ? (_held[k] ? 2 : 0) : _rising[k] ? 2 : 1;
                    // Just up through a pile, it passes through those still lying until it is clear of them (or three
                    // seconds have gone): given its body back inside another's, the contact throws them apart.
                    if (state == 0 && _body[k] == 2 && _ghostFor[k] < 3.0)
                    {
                        _ghostFor[k] += _m->opt.timestep;
                        for (int j = 0; j < _b.Length && state == 0; j++)
                        {
                            if (j == k || !_down[j]) continue;
                            Vector3 apart = _pelvis[k].position - _pelvis[j].position;
                            apart.y = 0f;
                            if (apart.magnitude < 0.7f) state = 2;
                        }
                    }
                    if (state != 2) _ghostFor[k] = 0.0;
                    SetBody(k, state);
                    if (_held[k]) _b[k].ResetToGuard(_holdX[k], _holdY[k], _holdYaw[k]);
                    continue;
                }
                bool ghost = _down[k];
                // Just up, it stays a ghost until it is a step from everybody (or three seconds have gone): given its
                // body back while another stands inside it, the contact throws them both across the ring.
                if (!ghost && _ghost[k])
                {
                    _ghostFor[k] += _m->opt.timestep;
                    float nearest = float.MaxValue;
                    for (int j = 0; j < _b.Length; j++)
                    {
                        if (j == k) continue;
                        Vector3 apart = _pelvis[k].position - _pelvis[j].position;
                        apart.y = 0f;
                        nearest = Mathf.Min(nearest, apart.magnitude);
                    }
                    if (nearest < 0.6f && _ghostFor[k] < 3.0) ghost = true;
                }
                if (!ghost) _ghostFor[k] = 0.0;
                SetGhost(k, ghost);
                // A held boxer is put back in its guard on its spot before every step: it stands and waits.
                if (_held[k]) _b[k].ResetToGuard(_holdX[k], _holdY[k], _holdYaw[k]);
            }
            if (_sub == 0 && _cubes != null) _cubes.Tick(_dt);
            if (solidBodies)
            {
                // A free-for-all spreads its brains over the control step: boxer k decides on substep k mod 4. Each
                // still observes, acts and holds its targets for 4 steps exactly as in training; only the phase
                // differs, so eight inferences are not all paid on one physics step.
                long t0 = _clock.ElapsedTicks;
                for (int k = 0; k < _b.Length; k++)
                {
                    if (k % _decimation != _sub) { _b[k].HoldTargets(); continue; }
                    _b[k].Observe();
                    _b[k].Infer();
                    _b[k].Drive();
                }
                _policyTicks += _clock.ElapsedTicks - t0;
                if (_sub == _decimation - 1)
                {
                    PolicyMs += 0.05 * (_policyTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency - PolicyMs);
                    _policyTicks = 0;
                }
            }
            else if (_sub == 0)
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
                foreach (MjBoxer b in _b) b.HoldTargets();
            _began = _clock.ElapsedTicks;
        }

        void AfterPhysics(object sender, MjStepArgs a)
        {
            if (!Ready) return;
            PhysicsMs += 0.05 * ((_clock.ElapsedTicks - _began) * 1000.0 / System.Diagnostics.Stopwatch.Frequency - PhysicsMs);
            _sub = (_sub + 1) % _decimation;
            if (solidBodies)
            {
                // Staggered: the boxers whose turn comes next have finished their control step now.
                for (int k = _sub; k < _b.Length; k += _decimation) { _b[k].AfterStep(_dt); if (!_rising[k]) _b[k].Tire(_dt); }
                if (_sub < _b.Length) MujocoLib.mj_kinematics(_m, _d);
                if (_sub != 0) return;
                TrackPunches();
                TrackCanvas();
                ControlStep?.Invoke();
                return;
            }
            if (_sub != 0) return;
            // As the trainer: the stand-in's (here the opponent's) velocity from where the bodies were left, then
            // the bodies brought up to date before anybody observes.
            foreach (MjBoxer b in _b) b.AfterStep(_dt);
            // Getting up costs nothing from the reserve: the get-up brains were trained so (envs/boxing.py's _tire was
            // skipped by the get-up stage), and spending at a get-up's ~12 kW empties it in under a second, after which
            // no brain can stand (seen: every get-up in the game counted out, against 28 of 30 in the exam).
            for (int k = 0; k < _b.Length; k++) if (!_rising[k]) _b[k].Tire(_dt);
            MujocoLib.mj_kinematics(_m, _d);
            TrackPunches();
            TrackCanvas();
            ControlStep?.Invoke();
        }

        // ---------------------------------------------------------------- punches and footsteps

        double* Geom(int g) => _d->geom_xpos + 3 * g;

        void TrackPunches()
        {
            Array.Clear(_push, 0, _push.Length);
            for (int k = 0; k < _b.Length; k++)
            {
                // A new opponent: the speeds measured against the last one mean nothing for this one.
                int t = Target(k);
                bool retargeted = t != _target[k];
                if (retargeted) { _target[k] = t; _has[k] = false; }
                MjBoxer me = _b[k], them = _b[t];
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
                            if (them.Config.hit_push > 0.0)
                            {
                                // envs/boxing.py's hit_push: along the line from the glove to the zone, at its surface.
                                double d = toHead ? dHead : dBody, inv = toHead ? ih : ib, reach = d - (toHead ? rHead : rBody);
                                double nx = (toHead ? hx : bx) * inv, ny = (toHead ? hy : by) * inv, nz = (toHead ? hz : bz) * inv;
                                double j = them.Config.hit_push * PunchMass * arriving / _dt;
                                double* com = _d->xipos + 3 * them.TorsoBody;
                                double rx = g[0] + nx * reach - com[0], ry = g[1] + ny * reach - com[1], rz = g[2] + nz * reach - com[2];
                                double fx = nx * j, fy = ny * j, fz = nz * j;
                                _push[t, 0] += fx; _push[t, 1] += fy; _push[t, 2] += fz;
                                _push[t, 3] += ry * fz - rz * fy; _push[t, 4] += rz * fx - rx * fz; _push[t, 5] += rx * fy - ry * fx;
                            }
                            PunchLanded?.Invoke(me, hand, toHead, (float)arriving, ToUnity(g[0], g[1], g[2]));
                        }
                    }
                    else
                    {
                        // A fresh start is armed, as an episode starts. A new target starts the glove's cool-down too:
                        // in a scrum the glove may already be resting on the new one's head, and touching is not a
                        // punch. (Waiting for it to come 30 cm clear instead silenced most of a free-for-all's punches.)
                        _armed[k, hand] = true;
                        if (retargeted) _cool[k, hand] = 0.25;
                    }
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
                    // And turned on: the sole's heading (its own x axis on the floor) against the last step's.
                    double* fm = _d->geom_xmat + 9 * me.FootGeom(hand);
                    double yaw = Math.Atan2(fm[3], fm[0]), turn = yaw - _prevFootYaw[k, hand];
                    turn = Math.Atan2(Math.Sin(turn), Math.Cos(turn));
                    _spin[k, hand] = down && _footDown[k, hand] && _has[k] && !_held[k] ? Math.Abs(turn) / _dt : 0.0;
                    _prevFootYaw[k, hand] = yaw;
                    _footDown[k, hand] = down;
                    for (int c = 0; c < 3; c++) _prevFoot[k, hand, c] = f[c];
                }
                for (int c = 0; c < 3; c++) { _prevHead[k, c] = head[c]; _prevBody[k, c] = body[c]; }
            }
            for (int k = 0; k < _b.Length; k++)
            {
                TrackBlocks(k);
                _has[k] = true;
                // This step's punches on the trunk for the next four physics steps, as the trainer applies them; then off.
                bool any = false;
                for (int c = 0; c < 6; c++) any |= _push[k, c] != 0.0;
                if (any || _pushed[k]) _b[k].PushTrunk(_push[k, 0], _push[k, 1], _push[k, 2], _push[k, 3], _push[k, 4], _push[k, 5]);
                _pushed[k] = any;
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
            float* most = stackalloc float[_b.Length];
            for (int k = 0; k < _b.Length; k++) { _ropeLoad[k] = 0f; most[k] = 0f; }
            for (int i = 0; i < _d->ncon; i++)
            {
                MujocoLib.mjContact_* c = _d->contact + i;
                int g = c->geom1, floor = c->geom2;
                // A boxer against the ropes: the push summed, placed where it is hardest.
                int rope = _rope[g] ? g : _rope[floor] ? floor : -1;
                if (rope >= 0)
                {
                    int part = rope == g ? floor : g;
                    if (_owner[part] < 0 || c->efc_address < 0) continue;
                    MujocoLib.mj_contactForce(_m, _d, i, force);
                    int k = _owner[part];
                    _ropeLoad[k] += (float)force[0];
                    if ((float)force[0] > most[k]) { most[k] = (float)force[0]; _ropeAt[k] = ToUnity(c->pos[0], c->pos[1], c->pos[2]); }
                    continue;
                }
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
        /// A punch stopped (envs/boxing.py, the block): a glove of a boxer whose opponent this one is, coming at this
        /// one's head or body faster than 2 m/s and within 45 cm of it, touching this one's glove or forearm. Raised once
        /// when it begins. (The trainer's block looks at the head only; the body's is for the game's counts and judges.)
        /// </summary>
        void TrackBlocks(int k)
        {
            MjBoxer me = _b[k];
            for (int j = 0; j < _b.Length; j++)
                if (j != k && _target[j] == k && _has[j]) TrackBlocks(k, j, me, _b[j]);
        }

        void TrackBlocks(int k, int j, MjBoxer me, MjBoxer them)
        {
            double* head = Geom(me.HeadGeom), body = Geom(me.TorsoGeom);
            double rHead = _m->geom_size[3 * me.HeadGeom], rBody = _m->geom_size[3 * me.TorsoGeom];
            for (int hand = 0; hand < 2; hand++)
            {
                double* tg = Geom(them.GloveGeom(hand));
                double rTheirs = _m->geom_size[3 * them.GloveGeom(hand)];
                double coming = Math.Max(_speed[j, hand, 0], _speed[j, hand, 1]);
                double hx = head[0] - tg[0], hy = head[1] - tg[1], hz = head[2] - tg[2];
                double bx = body[0] - tg[0], by = body[1] - tg[1], bz = body[2] - tg[2];
                bool near = Math.Sqrt(hx * hx + hy * hy + hz * hz) - rHead < 0.45 || Math.Sqrt(bx * bx + by * by + bz * bz) - rBody < 0.45;
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
                if (blocked && !_blocking[j, hand]) PunchStopped?.Invoke(me, (float)coming, ToUnity(tg[0], tg[1], tg[2]));
                _blocking[j, hand] = blocked;
            }
        }
    }
}
