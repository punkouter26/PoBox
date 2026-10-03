using System;
using Mujoco;
using UnityEngine;

namespace PoBox.Mj
{
    /// <summary>
    /// Runs one boxer on the MuJoCo plugin the way the trainer runs it: MuJoCo steps every 0.005 s (the
    /// plugin steps it once a FixedUpdate), and once every `control_decimation` of those steps the boxer
    /// observes, its policy answers, and the answer is written to mjData.ctrl and held until the next.
    /// The order inside a control step is training/tools/footwork_c.py's: what comes from outside (a cube
    /// thrown), observation, action, targets, shove, cubes' clocks, then the physics.
    ///
    /// Also the testbed's own behaviour: shoves and cubes on demand, and standing the boxer up again when
    /// it has fallen, has stalled or has walked off, by writing mjData (nothing is destroyed or made).
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public unsafe class MjTestbed : MonoBehaviour
    {
        public MjBoxer boxer;
        public MjCubePool cubes;
        [Tooltip("Shove the boxer every few seconds: 10 to 30 N s, from any side, scaled by its strength.")]
        public bool shoves;
        [Tooltip("Stand the boxer up again two seconds after a fall, or when it is five metres from where it began.")]
        public bool autoReset = true;
        [Tooltip("MuJoCo's step, seconds: the trainer's (models/v2, 200 Hz).")]
        public float physicsStep = 0.005f;

        /// <summary>Seconds of simulation since the last reset.</summary>
        public double Now { get; private set; }
        public double ControlDt { get; private set; }
        public int Falls { get; private set; }
        /// <summary>Milliseconds of a physics step (MuJoCo and the plugin's copy to the transforms) and of a control step's observation, policy and targets; smoothed.</summary>
        public double PhysicsMs { get; private set; }
        public double PolicyMs { get; private set; }
        public bool Ready => boxer != null && boxer.Bound;
        /// <summary>Raised when a control step's physics is done; the boxer's state is that of the end of the step.</summary>
        public event Action ControlStepDone;
        /// <summary>Raised at the start of a control step, before the boxer observes: the place to throw something from a script.</summary>
        public event Action ControlStepBegins;

        MujocoLib.mjModel_* _m;
        MujocoLib.mjData_* _d;
        int _sub, _decimation = 4;
        double _shoveLeft, _nextShove, _downFor;
        readonly double[] _shove = new double[3], _chest = new double[3];
        readonly System.Random _rng = new System.Random(1);
        readonly System.Diagnostics.Stopwatch _clock = new System.Diagnostics.Stopwatch();
        long _stepBegan;

        static double Smooth(double was, long ticks) => was + 0.05 * (ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency - was);

        void Awake()
        {
            // The plugin steps MuJoCo once a FixedUpdate and takes MuJoCo's step from Unity's: both are the trainer's.
            Time.fixedDeltaTime = physicsStep;
            // The plugin makes its own MjScene; this only has to be listening before it compiles the model.
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

        void OnModel(object sender, MjStepArgs a)
        {
            _m = a.model; _d = a.data;
            if (_m == null) return;
            boxer.Bind(_m, _d);
            // Unity keeps its fixed step as 0.004999993, and that is what the plugin gave MuJoCo. The trainer's is exact.
            _m->opt.timestep = 1.0 / boxer.Cfg.physics_hz;
            if (cubes != null) cubes.Bind(_m, _d);
            _decimation = Math.Max(1, boxer.Cfg.control_decimation);
            ControlDt = _m->opt.timestep * _decimation;
            ResetBoxer();
        }

        /// <summary>Back in the guard at the origin, facing +x, the stand-in 1.3 m in front, every cube waiting, the clock at zero.</summary>
        public void ResetBoxer()
        {
            if (cubes != null) { cubes.ParkAll(); cubes.Tick(0.0); }
            boxer.Push(0, 0, 0);
            boxer.ResetToGuard(0.0, 0.0, 0.0);
            boxer.SetEpisode(MjBoxer.Stand, 0.0, 0.0, 0.0, 1.3, 0.0);
            Now = 0.0; _sub = 0; _shoveLeft = 0.0; _nextShove = 2.0; _downFor = 0.0;
            MjScene.Instance.SyncUnityToMjState();
        }

        /// <summary>A shove of so many newton seconds over so many seconds, from this angle (MuJoCo's frame, about the vertical).</summary>
        public void Shove(double newtonSeconds, double angle, double seconds)
        {
            double f = newtonSeconds / seconds;
            _shove[0] = Math.Cos(angle) * f; _shove[1] = Math.Sin(angle) * f; _shove[2] = 0.0;
            _shoveLeft = seconds;
        }

        /// <summary>A cube at the boxer's chest at 5 m/s from somewhere round it, or, one time in three, dropped on it.</summary>
        public bool ThrowCube()
        {
            if (cubes == null || !Ready) return false;
            boxer.Chest(_chest);
            if (_rng.NextDouble() < 0.3) return cubes.Throw(_chest, 0, 0, 0, 0, dropFrom: 1.2 + 0.8 * _rng.NextDouble());
            return cubes.Throw(_chest, 5.0, (_rng.NextDouble() * 2 - 1) * Math.PI, 2.0 + _rng.NextDouble(), -0.2 + 0.6 * _rng.NextDouble());
        }

        void BeforePhysics(object sender, MjStepArgs a)
        {
            if (!Ready) return;
            // The plugin's MjActuator copies its own Control field over mjData.ctrl after every step, so the
            // targets are written again before each one: they are held for the whole control step.
            if (_sub != 0) { boxer.HoldTargets(); _stepBegan = _clock.ElapsedTicks; return; }
            long began = _clock.ElapsedTicks;
            if (shoves && Now >= _nextShove)
            {
                _nextShove = Now + 2.0 + 2.0 * _rng.NextDouble();
                Shove((10.0 + 20.0 * _rng.NextDouble()) * Math.Min(1.0, boxer.Cfg.strength), (_rng.NextDouble() * 2 - 1) * Math.PI, 0.1 + 0.1 * _rng.NextDouble());
            }
            ControlStepBegins?.Invoke();
            boxer.Observe();
            boxer.Infer();
            boxer.Drive();
            if (_shoveLeft > 1e-6) boxer.Push(_shove[0], _shove[1], _shove[2]); else boxer.Push(0, 0, 0);
            _shoveLeft = Math.Max(0.0, _shoveLeft - ControlDt);
            if (cubes != null) cubes.Tick(ControlDt);
            _stepBegan = _clock.ElapsedTicks;
            PolicyMs = Smooth(PolicyMs, _stepBegan - began);
        }

        /// <summary>What the boxer is asked to do from where it stands: hold its guard, walk forward at 0.6 m/s, or turn to an opponent behind it.</summary>
        public void SetBehaviour(int kind)
        {
            if (kind == MjBoxer.Walk) boxer.SetEpisode(MjBoxer.Walk, 0.6 * boxer.Cfg.speed, 0.0, 0.0, 1.2, 0.0);
            else if (kind == MjBoxer.Turn) boxer.SetEpisode(MjBoxer.Turn, 0.0, 0.0, 0.0, 1.5, Math.PI);
            else boxer.SetEpisode(MjBoxer.Stand, 0.0, 0.0, 0.0, 1.3, 0.0);
        }

        void AfterPhysics(object sender, MjStepArgs a)
        {
            if (!Ready) return;
            PhysicsMs = Smooth(PhysicsMs, _clock.ElapsedTicks - _stepBegan);
            _sub = (_sub + 1) % _decimation;
            if (_sub != 0) return;
            Now += ControlDt;
            // mj_step leaves where the bodies are (xpos, geom_xpos) one step behind qpos. The trainer measures
            // the stand-in's velocity from them as they are left and then brings them up to date before it
            // observes; so does this, in that order.
            boxer.AfterStep(ControlDt);
            MujocoLib.mj_kinematics(_m, _d);
            ControlStepDone?.Invoke();
            if (!autoReset) return;
            // Once down it is down until it is stood up again: a body rolling on the floor is one fall, not several.
            if (boxer.Fallen && _downFor == 0.0) Falls++;
            if (boxer.Fallen || _downFor > 0.0) _downFor += ControlDt;
            if (_downFor >= 2.0 || boxer.DistanceFromOrigin > 5.0 || double.IsNaN(boxer.PelvisHeight)) ResetBoxer();
        }
    }
}
