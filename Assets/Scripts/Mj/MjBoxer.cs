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
        public double[] default_joint_pos, lower, upper, velocity_limit;
        public double stand_height, action_scale = 0.5, action_clip = 3.0, ring_half = 3.05, strength = 1.0, speed = 1.0;
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
        public const int Stand = 0, Walk = 1, Turn = 2;
        public const int ObservationSize = 103;

        [Tooltip("In front of every name of this boxer in the model: a_ for the red corner, b_ for the blue.")]
        public string prefix = "a_";
        [Tooltip("models/v2/NAME_policy_config.json")]
        public TextAsset config;
        [Tooltip("The policy. None is the zero action: the guard, held by the joint drives.")]
        public ModelAsset policy;

        public BoxerConfig Cfg { get; private set; }
        public bool Bound { get; private set; }
        public int Joints { get; private set; }

        /// <summary>What kind of episode it is in (Stand, Walk, Turn), the command, and where the stand-in opponent stands.</summary>
        [NonSerialized] public int Kind = Stand;
        public readonly double[] Cmd = new double[3];
        public readonly double[] Opp = new double[2];

        public readonly double[] Obs = new double[ObservationSize];
        public double[] Action { get; private set; }
        public float Value { get; private set; }

        MujocoLib.mjModel_* _m;
        MujocoLib.mjData_* _d;
        int[] _jq, _jv, _act;
        int _pelvis, _torsoBody, _head, _torso, _rootQ, _rootV;
        readonly int[] _glove = new int[2], _foot = new int[2];
        readonly double[][] _guard = { new double[3], new double[3], new double[3], new double[3] };   // head, body, gloves
        readonly double[][] _standIn = { new double[3], new double[3], new double[3], new double[3] };
        double _theirs;
        readonly double[] _headVel = new double[3], _prevHead = new double[3];
        Worker _worker;
        Tensor<float> _input;
        float[] _obsF;

        void Awake()
        {
            Cfg = JsonUtility.FromJson<BoxerConfig>(config.text);
            Joints = Cfg.joint_order.Length;
            Action = new double[Joints];
            _obsF = new float[ObservationSize];
            SetPolicy(policy);
        }

        /// <summary>Another policy from here on. None is the zero action.</summary>
        public void SetPolicy(ModelAsset asset)
        {
            _input?.Dispose();
            _worker?.Dispose();
            _input = null; _worker = null;
            policy = asset;
            if (asset == null) return;
            _worker = new Worker(ModelLoader.Load(asset), BackendType.CPU);
            _input = new Tensor<float>(new TensorShape(1, ObservationSize), false);
        }

        void OnDestroy()
        {
            _input?.Dispose();
            _worker?.Dispose();
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
                int j = Id(MujocoLib.mjtObj.mjOBJ_JOINT, Cfg.joint_order[i]);
                _jq[i] = _m->jnt_qposadr[j];
                _jv[i] = _m->jnt_dofadr[j];
                _act[i] = Id(MujocoLib.mjtObj.mjOBJ_ACTUATOR, Cfg.joint_order[i]);
            }
            int root = Id(MujocoLib.mjtObj.mjOBJ_JOINT, "root");
            _rootQ = _m->jnt_qposadr[root]; _rootV = _m->jnt_dofadr[root];
            _pelvis = Id(MujocoLib.mjtObj.mjOBJ_BODY, "pelvis"); _torsoBody = Id(MujocoLib.mjtObj.mjOBJ_BODY, "torso");
            _head = Id(MujocoLib.mjtObj.mjOBJ_GEOM, "head_geom"); _torso = Id(MujocoLib.mjtObj.mjOBJ_GEOM, "torso_geom");
            _glove[0] = Id(MujocoLib.mjtObj.mjOBJ_GEOM, "glove_l"); _glove[1] = Id(MujocoLib.mjtObj.mjOBJ_GEOM, "glove_r");
            _foot[0] = Id(MujocoLib.mjtObj.mjOBJ_GEOM, "foot_l_geom"); _foot[1] = Id(MujocoLib.mjtObj.mjOBJ_GEOM, "foot_r_geom");
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
            q[0] = x; q[1] = y; q[2] = Cfg.stand_height + 0.002;
            q[3] = Math.Cos(yaw / 2); q[4] = 0; q[5] = 0; q[6] = Math.Sin(yaw / 2);
            for (int k = 0; k < 6; k++) _d->qvel[_rootV + k] = 0;
            for (int i = 0; i < Joints; i++)
            {
                _d->qpos[_jq[i]] = Cfg.default_joint_pos[i];
                _d->qvel[_jv[i]] = 0;
                _d->ctrl[_act[i]] = Cfg.default_joint_pos[i];
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
                _d->ctrl[_act[i]] = jointPos[i];
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

        static double Yaw(double* q) => Math.Atan2(2.0 * (q[0] * q[3] + q[1] * q[2]), 1.0 - 2.0 * (q[2] * q[2] + q[3] * q[3]));

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
            double ox = Kind == Walk ? pos[0] + Math.Cos(yaw) * 1.2 : Opp[0], oy = Kind == Walk ? pos[1] + Math.Sin(yaw) * 1.2 : Opp[1];
            _theirs = Math.Atan2(pos[1] - oy, pos[0] - ox);
            double c = Math.Cos(_theirs), s = Math.Sin(_theirs);
            for (int p = 0; p < 4; p++)
            {
                double[] g = _guard[p], o = _standIn[p];
                o[0] = ox + c * g[0] - s * g[1]; o[1] = oy + s * g[0] + c * g[1]; o[2] = Cfg.stand_height + g[2];
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
                o[9 + i] = _d->qpos[_jq[i]] - Cfg.default_joint_pos[i];
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
            o[at++] = Clamp((cy * -pos[0] + sy * -pos[1]) / Cfg.ring_half, -1.0, 1.0);
            o[at++] = Clamp((-sy * -pos[0] + cy * -pos[1]) / Cfg.ring_half, -1.0, 1.0);
            o[at++] = Cmd[0]; o[at++] = Cmd[1]; o[at++] = Cmd[2];
            for (int i = 0; i < ObservationSize; i++) o[i] = Clamp(o[i], -100.0, 100.0);
        }

        static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

        /// <summary>The policy's answer to <see cref="Obs"/>, into <see cref="Action"/>. With no policy, zeros.</summary>
        public void Infer()
        {
            if (_worker == null) { Array.Clear(Action, 0, Joints); return; }
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
                Action[i] = Clamp(Action[i], -Cfg.action_clip, Cfg.action_clip);
                _d->ctrl[_act[i]] = Clamp(Cfg.default_joint_pos[i] + Action[i] * Cfg.action_scale, Cfg.lower[i], Cfg.upper[i]);
            }
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
        public bool Fallen => PelvisHeight < Cfg.stand_height * 0.6 || Upright < 0.4;
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
