using System;
using Mujoco;
using Unity.InferenceEngine;
using UnityEngine;

namespace PoBox.Mj
{
    /// <summary>What the trainer wrote about a boxer's body (models/v2/NAME_policy_config.json).</summary>
    [Serializable]
    public class BoxerConfig
    {
        public string name;
        public string[] joint_order;
        public double[] default_joint_pos, lower, upper, velocity_limit, force_limit;
        /// <summary>The two rules of envs/boxing.py a policy may have been trained under; a config without them has neither.</summary>
        public bool speed_limit;
        public double fatigue_j, fatigue_rec_s = 20.0, fatigue_weak = 0.4;
        /// <summary>Two more (2026-10-16): the share of a punch's impulse that pushes this boxer's trunk when it is hit (MjRing), and whether its policy reads its own state in the command's place.</summary>
        public double hit_push;
        public bool obs_state;
        /// <summary>models/v3: each joint's radians per unit of action (none: action_scale for all), and a tired drive's torque capped at what is left of its force range instead of its spring weakened (envs/boxing.py).</summary>
        public double[] action_scale_j;
        public bool fatigue_ceiling;
        public double stand_height, total_mass_kg, action_scale = 0.5, action_clip = 3.0, ring_half = 3.05, strength = 1.0, speed = 1.0;
        public int control_decimation = 4, physics_hz = 200;
    }

    /// <summary>
    /// A boxer whose body is MuJoCo's and nothing else's: it reads its observation out of mjData, asks its
    /// policy, and writes joint targets to mjData.ctrl. No transform, collider or rigid body of Unity's is
    /// read or written here; the MuJoCo plugin moves the GameObjects to where the bodies are.
    ///
    /// The observation is training/tools/footwork_c.py's, number for number and in MuJoCo's own frame (z up):
    /// the match's hundred and a three-number command. So is the stand-in opponent it is shown.
    /// </summary>
    public unsafe class MjBoxer : MonoBehaviour
    {
        /// <summary>GetUp: off the canvas in a bout. The get-up stage is the trainer's sparring env (envs/getup.py, a
        /// BoxingEnv with the opponent frozen in it), so it is shown the real opponent and the bout's ring, as Match is.</summary>
        public const int Stand = 0, Walk = 1, Turn = 2, Match = 3, GetUp = 4;
        public const int ObservationSize = 103;

        [Tooltip("In front of every name of this boxer in the model: a_ for the red corner, b_ for the blue.")]
        public string prefix = "a_";
        [Tooltip("models/v2/NAME_policy_config.json")]
        public TextAsset config;
        [Tooltip("The policy. None is the zero action: the guard, held by the joint drives.")]
        public ModelAsset policy;
        [Tooltip("The policy for a bout (training/envs/match.py). None: the one above.")]
        public ModelAsset matchPolicy;
        [Tooltip("The policy for getting up off the canvas (envs/getup.py on v2). None: the referee stands it up.")]
        public ModelAsset getUpPolicy;
        [Tooltip("The other boxer, in a bout. Episodes of kind Match show it, not a stand-in.")]
        public MjBoxer opponent;
        [Tooltip("Where the middle of the ring is, MuJoCo's x and y: the observation's place in the ring is measured from it.")]
        public Vector2 ringCentre;

        /// <summary>The share of joint drive strength the boxer has: 1 unhurt, less when dazed, 0.04 with its legs gone (envs/boxing.py).</summary>
        [NonSerialized] public double DriveScale = 1.0;
        /// <summary>What a tiring boxer has left, 0 to 1 (envs/boxing.py's reserve). Stays at 1 unless its config has fatigue_j.</summary>
        [NonSerialized] public double Reserve = 1.0;
        /// <summary>Its daze and the opponent's, as shares of where the legs go: what a policy with obs_state reads (set by the game's brain).</summary>
        [NonSerialized] public double DazeShare, OpponentDazeShare;

        public BoxerConfig Config { get; private set; }
        public bool Bound { get; private set; }
        public int Joints { get; private set; }

        /// <summary>What kind of episode it is in (Stand, Walk, Turn), the command, and where the stand-in opponent stands.</summary>
        [NonSerialized] public int Kind = Stand;
        public readonly double[] Cmd = new double[3];
        public readonly double[] Opp = new double[2];

        public readonly double[] Obs = new double[ObservationSize];
        public double[] Action { get; private set; }
        public float Value { get; private set; }
        /// <summary>Seconds every boxer's policy has spent thinking since the last <see cref="TakeInferSeconds"/>: for the telemetry.</summary>
        static double _inferSeconds;
        public static double TakeInferSeconds() { double s = _inferSeconds; _inferSeconds = 0; return s; }

        MujocoLib.mjModel_* _m;
        MujocoLib.mjData_* _d;
        int[] _jq, _jv, _act;
        int _pelvis, _torsoBody, _head, _torso, _rootQ, _rootV;
        readonly int[] _glove = new int[2], _foot = new int[2], _forearm = new int[2], _upperBody = new int[2], _foreBody = new int[2];

        // Where the things a punch is measured against are in the model (for MjRing).
        public int HeadGeom => _head;
        public int TorsoGeom => _torso;
        public int GloveGeom(int hand) => _glove[hand];
        public int ForearmGeom(int hand) => _forearm[hand];
        public int FootGeom(int foot) => _foot[foot];
        readonly double[][] _guard = { new double[3], new double[3], new double[3], new double[3] };   // head, body, gloves
        readonly double[][] _standIn = { new double[3], new double[3], new double[3], new double[3] };
        double _theirs;
        readonly double[] _headVel = new double[3], _prevHead = new double[3];
        Worker _worker;
        Tensor<float> _input;
        float[] _obsF;

        void Awake()
        {
            Config = JsonUtility.FromJson<BoxerConfig>(config.text);
            Joints = Config.joint_order.Length;
            Action = new double[Joints];
            _obsF = new float[ObservationSize];
            SetPolicy(policy);
        }

        readonly System.Collections.Generic.Dictionary<ModelAsset, Worker> _workers = new System.Collections.Generic.Dictionary<ModelAsset, Worker>();

        /// <summary>Another policy from here on. None is the zero action. Each policy's worker is made once and kept.</summary>
        public void SetPolicy(ModelAsset asset)
        {
            policy = asset;
            _worker = null;
            if (asset == null) return;
            if (!_workers.TryGetValue(asset, out _worker))
            {
                _worker = new Worker(ModelLoader.Load(asset), BackendType.CPU);
                _workers[asset] = _worker;
            }
            if (_input == null) _input = new Tensor<float>(new TensorShape(1, ObservationSize), false);
        }

        /// <summary>The policy the boxer now runs.</summary>
        public ModelAsset Running => policy;

        void OnDestroy()
        {
            _input?.Dispose();
            foreach (var w in _workers.Values) w.Dispose();
            _workers.Clear();
        }

        int Id(MujocoLib.mjtObj kind, string name)
        {
            int id = MujocoLib.mj_name2id(_m, (int)kind, prefix + name);
            if (id < 0) throw new InvalidOperationException($"the model has no {prefix}{name}");
            return id;
        }

        /// <summary>Find this boxer's joints, drives and shapes in the compiled model, and stand it in its guard at the origin.</summary>
        public void Bind(MujocoLib.mjModel_* model, MujocoLib.mjData_* data)
        {
            _m = model; _d = data;
            _jq = new int[Joints]; _jv = new int[Joints]; _act = new int[Joints];
            for (int i = 0; i < Joints; i++)
            {
                int j = Id(MujocoLib.mjtObj.mjOBJ_JOINT, Config.joint_order[i]);
                _jq[i] = _m->jnt_qposadr[j];
                _jv[i] = _m->jnt_dofadr[j];
                _act[i] = Id(MujocoLib.mjtObj.mjOBJ_ACTUATOR, Config.joint_order[i]);
                // envs/boxing.py's speed limit: a drive pulling its hardest has nothing left over at the limit.
                // The model is compiled afresh from the scene each time, so this is added once.
                if (Config.speed_limit) _m->dof_damping[_jv[i]] += Config.force_limit[i] / Config.velocity_limit[i];
                // The fatigue ceiling's numbers, from the compiled model as the trainer reads them: a position drive's force is
                // kp (target - q) - kv qdot, held inside its force range (models/v3's are not the same both ways).
                _kp[i] = _m->actuator_gainprm[_act[i] * MujocoLib.mjNGAIN];
                _kv[i] = -_m->actuator_biasprm[_act[i] * MujocoLib.mjNBIAS + 2];
                _fLo[i] = _m->actuator_forcerange[_act[i] * 2];
                _fHi[i] = _m->actuator_forcerange[_act[i] * 2 + 1];
            }
            int root = Id(MujocoLib.mjtObj.mjOBJ_JOINT, "root");
            _rootQ = _m->jnt_qposadr[root]; _rootV = _m->jnt_dofadr[root];
            _pelvis = Id(MujocoLib.mjtObj.mjOBJ_BODY, "pelvis"); _torsoBody = Id(MujocoLib.mjtObj.mjOBJ_BODY, "torso");
            _head = Id(MujocoLib.mjtObj.mjOBJ_GEOM, "head_geom"); _torso = Id(MujocoLib.mjtObj.mjOBJ_GEOM, "torso_geom");
            _glove[0] = Id(MujocoLib.mjtObj.mjOBJ_GEOM, "glove_l"); _glove[1] = Id(MujocoLib.mjtObj.mjOBJ_GEOM, "glove_r");
            _foot[0] = Id(MujocoLib.mjtObj.mjOBJ_GEOM, "foot_l_geom"); _foot[1] = Id(MujocoLib.mjtObj.mjOBJ_GEOM, "foot_r_geom");
            _forearm[0] = Id(MujocoLib.mjtObj.mjOBJ_GEOM, "forearm_l_geom"); _forearm[1] = Id(MujocoLib.mjtObj.mjOBJ_GEOM, "forearm_r_geom");
            for (int h = 0; h < 2; h++)
            {
                _upperBody[h] = Id(MujocoLib.mjtObj.mjOBJ_BODY, h == 0 ? "upper_arm_l" : "upper_arm_r");
                _foreBody[h] = Id(MujocoLib.mjtObj.mjOBJ_BODY, h == 0 ? "forearm_l" : "forearm_r");
            }
            Bound = true;

            // Where the head, the body and the gloves are from the pelvis in the guard: the stand-in's shape.
            ResetToGuard(0.0, 0.0, 0.0);
            int[] parts = { _head, _torso, _glove[0], _glove[1] };
            double yaw = Yaw(_d->xquat + 4 * _pelvis);
            for (int p = 0; p < 4; p++) Heading(_d->geom_xpos + 3 * parts[p], _d->xpos + 3 * _pelvis, yaw, _guard[p]);
            SetEpisode(Stand, 0.0, 0.0, 0.0, 1.3, 0.0);
        }

        /// <summary>Standing in the guard at (x, y), facing yaw: the first moment of every training episode, without its noise.</summary>
        public void ResetToGuard(double x, double y, double yaw)
        {
            double* q = _d->qpos + _rootQ;
            q[0] = x; q[1] = y; q[2] = Config.stand_height + 0.002;
            q[3] = Math.Cos(yaw / 2); q[4] = 0; q[5] = 0; q[6] = Math.Sin(yaw / 2);
            for (int k = 0; k < 6; k++) _d->qvel[_rootV + k] = 0;
            for (int i = 0; i < Joints; i++)
            {
                _d->qpos[_jq[i]] = Config.default_joint_pos[i];
                _d->qvel[_jv[i]] = 0;
                _d->ctrl[_act[i]] = _held[i] = Config.default_joint_pos[i];
                Action[i] = 0;
            }
            MujocoLib.mj_forward(_m, _d);
        }

        /// <summary>The body put in a given state, in MuJoCo's own numbers: the root's position, turn and velocity, and the joints, at rest.</summary>
        public void SetState(double[] rootPos, double[] rootQuat, double[] rootLinVel, double[] jointPos)
        {
            for (int k = 0; k < 3; k++) { _d->qpos[_rootQ + k] = rootPos[k]; _d->qvel[_rootV + k] = rootLinVel[k]; _d->qvel[_rootV + 3 + k] = 0; }
            for (int k = 0; k < 4; k++) _d->qpos[_rootQ + 3 + k] = rootQuat[k];
            for (int i = 0; i < Joints; i++)
            {
                _d->qpos[_jq[i]] = jointPos[i];
                _d->qvel[_jv[i]] = 0;
                _d->ctrl[_act[i]] = _held[i] = jointPos[i];
                Action[i] = 0;
            }
            MujocoLib.mj_forward(_m, _d);
        }

        /// <summary>What is asked of it from here: the kind of episode, the command, and the stand-in, given as a bearing and a distance from where it now stands.</summary>
        public void SetEpisode(int kind, double vx, double vy, double wz, double distance, double bearing)
        {
            double* pos = _d->xpos + 3 * _pelvis;
            double yaw = Yaw(_d->xquat + 4 * _pelvis);
            SetEpisodeAt(kind, vx, vy, wz, pos[0] + Math.Cos(yaw + bearing) * distance, pos[1] + Math.Sin(yaw + bearing) * distance);
        }

        /// <summary>The same, with the stand-in at a place in the world.</summary>
        public void SetEpisodeAt(int kind, double vx, double vy, double wz, double oppX, double oppY)
        {
            Kind = kind; Cmd[0] = vx; Cmd[1] = vy; Cmd[2] = wz;
            Opp[0] = oppX; Opp[1] = oppY;
            for (int k = 0; k < 3; k++) _headVel[k] = 0;
            StandIn();
            for (int k = 0; k < 3; k++) _prevHead[k] = _standIn[0][k];
        }

        /// <summary>
        /// A free-for-all turns the boxer on another opponent. Its episode goes on; the stand-in's head speed starts
        /// again from the new one, as at the start of an episode, rather than jumping across the ring.
        /// </summary>
        public void Face(MjBoxer other)
        {
            if (other == opponent) return;
            opponent = other;
            if (Bound) SetEpisodeAt(Kind, Cmd[0], Cmd[1], Cmd[2], Opp[0], Opp[1]);
        }

        static double Yaw(double* q) =>Math.Atan2(2.0 * (q[0] * q[3] + q[1] * q[2]), 1.0 - 2.0 * (q[2] * q[2] + q[3] * q[3]));

        /// <summary>The vector from `from` to `p`, in the frame that has turned yaw about the vertical.</summary>
        static void Heading(double* p, double* from, double yaw, double[] o, int at = 0)
        {
            double x = p[0] - from[0], y = p[1] - from[1], c = Math.Cos(yaw), s = Math.Sin(yaw);
            o[at] = c * x + s * y; o[at + 1] = -s * x + c * y; o[at + 2] = p[2] - from[2];
        }

        /// <summary>The opponent's stand-in: its head, body and gloves in the world, facing the boxer. A walk's is carried dead ahead.</summary>
        void StandIn()
        {
            double* pos = _d->xpos + 3 * _pelvis;
            double yaw = Yaw(_d->xquat + 4 * _pelvis);
            if ((Kind == Match || Kind == GetUp) && opponent != null && opponent.Bound)
            {
                // A bout: the other boxer, as it is.
                _theirs = Yaw(_d->xquat + 4 * opponent._pelvis);
                for (int p = 0; p < 4; p++)
                {
                    int geom = p == 0 ? opponent._head : p == 1 ? opponent._torso : opponent._glove[p - 2];
                    for (int k = 0; k < 3; k++) _standIn[p][k] = _d->geom_xpos[3 * geom + k];
                }
                return;
            }
            double ox = Kind == Walk ? pos[0] + Math.Cos(yaw) * 1.2 : Opp[0], oy = Kind == Walk ? pos[1] + Math.Sin(yaw) * 1.2 : Opp[1];
            _theirs = Math.Atan2(pos[1] - oy, pos[0] - ox);
            double c = Math.Cos(_theirs), s = Math.Sin(_theirs);
            for (int p = 0; p < 4; p++)
            {
                double[] g = _guard[p], o = _standIn[p];
                o[0] = ox + c * g[0] - s * g[1]; o[1] = oy + s * g[0] + c * g[1]; o[2] = Config.stand_height + g[2];
            }
        }

        /// <summary>The 103 numbers, from mjData as it stands. Same order and same arithmetic as footwork_c.py's observe().</summary>
        public void Observe()
        {
            double* pos = _d->xpos + 3 * _pelvis, R = _d->xmat + 9 * _pelvis, lin = _d->qvel + _rootV;
            double yaw = Yaw(_d->xquat + 4 * _pelvis);
            double[] o = Obs;
            for (int i = 0; i < 3; i++)
            {
                o[i] = R[i] * lin[0] + R[3 + i] * lin[1] + R[6 + i] * lin[2];      // the pelvis's velocity, as it feels it
                o[3 + i] = lin[3 + i];
                o[6 + i] = -R[6 + i];                                              // which way is down
            }
            int n = Joints;
            for (int i = 0; i < n; i++)
            {
                o[9 + i] = _d->qpos[_jq[i]] - Config.default_joint_pos[i];
                o[9 + n + i] = _d->qvel[_jv[i]];
                o[9 + 2 * n + i] = Action[i];
            }
            int at = 9 + 3 * n;
            for (int f = 0; f < 2; f++)
            {
                double* gp = _d->geom_xpos + 3 * _foot[f], gm = _d->geom_xmat + 9 * _foot[f], size = _m->geom_size + 3 * _foot[f];
                double sole = gp[2] - (Math.Abs(gm[6]) * size[0] + Math.Abs(gm[7]) * size[1] + Math.Abs(gm[8]) * size[2]);
                o[at++] = sole < 0.005 ? 1.0 : 0.0;
            }
            o[at++] = pos[2];
            StandIn();
            fixed (double* head = _standIn[0], body = _standIn[1], gl = _standIn[2], gr = _standIn[3])
            {
                Heading(head, pos, yaw, o, at); at += 3;
                Heading(body, pos, yaw, o, at); at += 3;
                double vx = _headVel[0] - lin[0], vy = _headVel[1] - lin[1], c = Math.Cos(yaw), s = Math.Sin(yaw);
                o[at++] = c * vx + s * vy; o[at++] = -s * vx + c * vy; o[at++] = _headVel[2] - lin[2];
                Heading(_d->geom_xpos + 3 * _glove[0], pos, yaw, o, at); at += 3;
                Heading(_d->geom_xpos + 3 * _glove[1], pos, yaw, o, at); at += 3;
                Heading(gl, pos, yaw, o, at); at += 3;
                Heading(gr, pos, yaw, o, at); at += 3;
            }
            o[at++] = Math.Cos(_theirs - yaw); o[at++] = Math.Sin(_theirs - yaw);
            double cy = Math.Cos(yaw), sy = Math.Sin(yaw);
            double rx = ringCentre.x - pos[0], ry = ringCentre.y - pos[1];
            // A bout's ring is the trainer's (envs/boxing.py: not held to its size); the footwork stage holds it to one.
            double reach = Kind == Match || Kind == GetUp ? 100.0 : 1.0;
            o[at++] = Clamp((cy * rx + sy * ry) / Config.ring_half, -reach, reach);
            o[at++] = Clamp((-sy * rx + cy * ry) / Config.ring_half, -reach, reach);
            // In a bout the command is zeros; a policy trained with obs_state reads its own state there (envs/match.py).
            bool state = Kind == Match && Config.obs_state;
            o[at++] = state ? Reserve : Cmd[0]; o[at++] = state ? DazeShare : Cmd[1]; o[at++] = state ? OpponentDazeShare : Cmd[2];
            for (int i = 0; i < ObservationSize; i++) o[i] = Clamp(o[i], -100.0, 100.0);
        }

        static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

        /// <summary>The policy's answer to <see cref="Obs"/>, into <see cref="Action"/>. With no policy, zeros.</summary>
        public void Infer()
        {
            if (_worker == null) { Array.Clear(Action, 0, Joints); return; }
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 0; i < ObservationSize; i++) _obsF[i] = (float)Obs[i];
            _input.Upload(_obsF);
            _worker.Schedule(_input);
            if (_worker.PeekOutput("value") is Tensor<float> value)
            {
                value.CompleteAllPendingOperations();
                Value = value.AsReadOnlySpan()[0];
            }
            if (_worker.PeekOutput("actions") is Tensor<float> output)
            {
                output.CompleteAllPendingOperations();
                ReadOnlySpan<float> span = output.AsReadOnlySpan();
                for (int i = 0; i < Joints; i++) Action[i] = float.IsFinite(span[i]) ? span[i] : 0.0;
            }
            _inferSeconds += (System.Diagnostics.Stopwatch.GetTimestamp() - start) / (double)System.Diagnostics.Stopwatch.Frequency;
        }

        /// <summary>The policy's answer to any 103 numbers, without touching the body: for checking it against the trainer's.</summary>
        public void Infer(float[] observation, float[] actionsOut)
        {
            using var input = new Tensor<float>(new TensorShape(1, ObservationSize), observation);
            _worker.Schedule(input);
            var output = _worker.PeekOutput("actions") as Tensor<float>;
            output.CompleteAllPendingOperations();
            output.AsReadOnlySpan().CopyTo(actionsOut);
        }

        /// <summary>Joint targets from <see cref="Action"/>, into mjData.ctrl: the guard plus the action, held inside the joints' ranges.</summary>
        public void Drive()
        {
            for (int i = 0; i < Joints; i++)
            {
                Action[i] = Clamp(Action[i], -Config.action_clip, Config.action_clip);
                double perUnit = Config.action_scale_j != null && Config.action_scale_j.Length > i ? Config.action_scale_j[i] : Config.action_scale;
                double want = Clamp(Config.default_joint_pos[i] + Action[i] * perUnit, Config.lower[i], Config.upper[i]);
                double q = _d->qpos[_jq[i]];
                // Hurt or tired: the spring to the target is weakened, the damping is not. Under the ceiling, tiredness caps
                // the drive's torque instead, and only the daze weakens the spring.
                double tired = 1.0 - Config.fatigue_weak * Clamp(1.0 - Reserve / 0.5, 0.0, 1.0);
                bool ceiling = Config.fatigue_ceiling && Config.fatigue_j > 0.0;
                double scale = DriveScale * (ceiling ? 1.0 : tired);
                if (scale < 0.999) want = q + scale * (want - q);
                if (ceiling && tired < 0.999)
                {
                    double at = q + _kv[i] * _d->qvel[_jv[i]] / _kp[i];
                    want = Math.Min(Math.Max(want, at + tired * _fLo[i] / _kp[i]), at + tired * _fHi[i] / _kp[i]);
                }
                _d->ctrl[_act[i]] = _held[i] = want;
            }
        }

        readonly double[] _held = new double[64];
        readonly double[] _kp = new double[64], _kv = new double[64], _fLo = new double[64], _fHi = new double[64];

        /// <summary>
        /// The targets of this control step written again. The plugin's MjActuator copies its own Control field
        /// over mjData.ctrl after every physics step; the trainer holds its targets for the whole control step.
        /// </summary>
        public void HoldTargets()
        {
            for (int i = 0; i < Joints; i++) _d->ctrl[_act[i]] = _held[i];
        }

        /// <summary>Once a control step of a bout is over: what its drives put out comes off the reserve, which fills again over fatigue_rec_s.</summary>
        public void Tire(double dt)
        {
            if (Config.fatigue_j <= 0.0) return;
            double watts = 0.0;
            for (int i = 0; i < Joints; i++) watts += Math.Abs(_d->actuator_force[_act[i]] * _d->qvel[_jv[i]]);
            Reserve = Clamp(Reserve + dt * ((1.0 - Reserve) / Config.fatigue_rec_s - watts / (Config.fatigue_j * Config.strength)), 0.0, 1.0);
        }

        public int TorsoBody => _torsoBody;

        /// <summary>A force and a torque about its centre of mass on the trunk, in MuJoCo's frame: a punch's push (MjRing). Zeros take it off.</summary>
        public void PushTrunk(double fx, double fy, double fz, double tx, double ty, double tz)
        {
            double* f = _d->xfrc_applied + 6 * _torsoBody;
            f[0] = fx; f[1] = fy; f[2] = fz; f[3] = tx; f[4] = ty; f[5] = tz;
        }

        /// <summary>A force on the trunk, in MuJoCo's frame; zero takes it off.</summary>
        public void Push(double fx, double fy, double fz)
        {
            double* f = _d->xfrc_applied + 6 * _torsoBody;
            f[0] = fx; f[1] = fy; f[2] = fz;
        }

        /// <summary>Once a control step is over: how fast the stand-in's head moved, which the next observation holds.</summary>
        public void AfterStep(double dt)
        {
            StandIn();
            for (int k = 0; k < 3; k++)
            {
                _headVel[k] = (_standIn[0][k] - _prevHead[k]) / dt;
                _prevHead[k] = _standIn[0][k];
            }
        }

        public double PelvisHeight => _d->xpos[3 * _pelvis + 2];
        /// <summary>Whether a foot (0 left, 1 right) is on the floor, as the observation counts it: its lowest corner under 5 mm.</summary>
        public bool FootDown(int f)
        {
            double* gp = _d->geom_xpos + 3 * _foot[f], gm = _d->geom_xmat + 9 * _foot[f], size = _m->geom_size + 3 * _foot[f];
            return gp[2] - (Math.Abs(gm[6]) * size[0] + Math.Abs(gm[7]) * size[1] + Math.Abs(gm[8]) * size[2]) < 0.005;
        }
        public double Upright => _d->xmat[9 * _pelvis + 8];

        /// <summary>
        /// One arm's kinetic chain this instant (hand 0 left, 1 right): how fast the hip (pelvis), the shoulder, the
        /// elbow and the glove are moving, m/s, each MuJoCo's own velocity of that point (mj_objectVelocity on the
        /// pelvis, the upper arm's and the forearm's frames, which sit on the joints, and the glove); and the floor's
        /// push up through both feet, N, from the solver's contact forces.
        /// </summary>
        public void Chain(int hand, float[] speeds, out float floorN)
        {
            double* v = stackalloc double[6];
            for (int i = 0; i < 3; i++)
            {
                MujocoLib.mj_objectVelocity(_m, _d, (int)MujocoLib.mjtObj.mjOBJ_BODY, i == 0 ? _pelvis : i == 1 ? _upperBody[hand] : _foreBody[hand], v, 0);
                speeds[i] = (float)Math.Sqrt(v[3] * v[3] + v[4] * v[4] + v[5] * v[5]);
            }
            MujocoLib.mj_objectVelocity(_m, _d, (int)MujocoLib.mjtObj.mjOBJ_GEOM, _glove[hand], v, 0);
            speeds[3] = (float)Math.Sqrt(v[3] * v[3] + v[4] * v[4] + v[5] * v[5]);
            floorN = 0f;
            for (int i = 0; i < _d->ncon; i++)
            {
                MujocoLib.mjContact_* c = _d->contact + i;
                int foot = c->geom1 == _foot[0] || c->geom1 == _foot[1] ? c->geom1 : c->geom2 == _foot[0] || c->geom2 == _foot[1] ? c->geom2 : -1;
                if (foot < 0 || c->efc_address < 0 || _m->geom_type[foot == c->geom1 ? c->geom2 : c->geom1] != 0) continue;
                MujocoLib.mj_contactForce(_m, _d, i, v);
                floorN += (float)v[0];
            }
        }
        public bool Fallen => PelvisHeight < Config.stand_height * 0.6 || Upright < 0.4;
        /// <summary>Where a glove is (0 left, 1 right), Unity's frame.</summary>
        public Vector3 GlovePosition(int hand)
        {
            double* g = _d->geom_xpos + 3 * _glove[hand];
            return new Vector3((float)g[0], (float)g[2], (float)g[1]);
        }

        /// <summary>The whole body's centre of mass, Unity's frame.</summary>
        public Vector3 CentreOfMass
        {
            get { double* c = _d->subtree_com + 3 * _pelvis; return new Vector3((float)c[0], (float)c[2], (float)c[1]); }
        }

        /// <summary>The four corners of a foot's sole (0 left, 1 right), Unity's frame, into the array given.</summary>
        public void Sole(int f, Vector3[] corners)
        {
            double* gp = _d->geom_xpos + 3 * _foot[f], gm = _d->geom_xmat + 9 * _foot[f], size = _m->geom_size + 3 * _foot[f];
            int n = 0;
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                {
                    // The box's own x and y, at the bottom of its z: MuJoCo's frame, then Unity's.
                    double lx = sx * size[0], ly = sy * size[1], lz = -size[2];
                    double x = gp[0] + gm[0] * lx + gm[1] * ly + gm[2] * lz, y = gp[1] + gm[3] * lx + gm[4] * ly + gm[5] * lz, z = gp[2] + gm[6] * lx + gm[7] * ly + gm[8] * lz;
                    corners[n++] = new Vector3((float)x, (float)z, (float)y);
                }
        }

        /// <summary>Which way the boxer faces, Unity's frame, flat on the floor.</summary>
        public Vector3 Forward
        {
            get { double yaw = Yaw(_d->xquat + 4 * _pelvis); return new Vector3((float)Math.Cos(yaw), 0f, (float)Math.Sin(yaw)); }
        }

        public double DistanceFromOrigin => Math.Sqrt(_d->qpos[_rootQ] * _d->qpos[_rootQ] + _d->qpos[_rootQ + 1] * _d->qpos[_rootQ + 1]);
        public double JointPosition(int i) => _d->qpos[_jq[i]];
        public double JointVelocity(int i) => _d->qvel[_jv[i]];
        public double Torque(int i) => _d->actuator_force[_act[i]];
        public double Target(int i) => _d->ctrl[_act[i]];
        public void RootState(double[] pos3, double[] quat4, double[] lin3, double[] ang3)
        {
            for (int k = 0; k < 3; k++) { pos3[k] = _d->qpos[_rootQ + k]; lin3[k] = _d->qvel[_rootV + k]; ang3[k] = _d->qvel[_rootV + 3 + k]; }
            for (int k = 0; k < 4; k++) quat4[k] = _d->qpos[_rootQ + 3 + k];
        }
        /// <summary>The chest, in MuJoCo's frame: what a cube is thrown at.</summary>
        public void Chest(double[] o)
        {
            for (int k = 0; k < 3; k++) o[k] = _d->xpos[3 * _pelvis + k];
            o[2] += 0.25;
        }
        /// <summary>How far, in radians, it is from facing its stand-in.</summary>
        public double FacingError
        {
            get
            {
                StandIn();
                double* pos = _d->xpos + 3 * _pelvis;
                double off = Math.Atan2(_standIn[1][1] - pos[1], _standIn[1][0] - pos[0]) - Yaw(_d->xquat + 4 * _pelvis);
                return Math.Abs(Math.Atan2(Math.Sin(off), Math.Cos(off)));
            }
        }
    }
}
