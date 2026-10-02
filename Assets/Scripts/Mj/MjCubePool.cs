using System;
using Mujoco;
using UnityEngine;

namespace PoBox.Mj
{
    /// <summary>
    /// The cubes thrown at a boxer. MuJoCo cannot add a body while it runs, so all of them are in the model
    /// from the start (MjBody + MjFreeJoint + a box MjGeom each, children of this object), and nothing is
    /// ever instantiated or destroyed. A waiting cube is put back at its parking place every control step
    /// (it is not resting on anything); one in play is left to the physics for two seconds. A cube is
    /// thrown by writing its qpos and qvel, exactly as training/envs/footwork.py throws its own.
    /// </summary>
    public unsafe class MjCubePool : MonoBehaviour
    {
        public const double SecondsInPlay = 2.0;

        MujocoLib.mjModel_* _m;
        MujocoLib.mjData_* _d;
        int[] _q, _v;
        double[] _left;
        int _next;

        public int Count => _q?.Length ?? 0;
        public int InPlay { get { int n = 0; for (int i = 0; i < Count; i++) if (_left[i] > 0.0) n++; return n; } }

        public void Bind(MujocoLib.mjModel_* model, MujocoLib.mjData_* data)
        {
            _m = model; _d = data;
            var joints = GetComponentsInChildren<MjFreeJoint>();
            Array.Sort(joints, (a, b) => string.CompareOrdinal(a.name, b.name));
            _q = new int[joints.Length]; _v = new int[joints.Length]; _left = new double[joints.Length];
            for (int i = 0; i < joints.Length; i++) { _q[i] = joints[i].QposAddress; _v[i] = joints[i].DofAddress; }
            _next = 0;
        }

        /// <summary>Once a control step, before the physics: the clocks run down and every waiting cube is held where it waits.</summary>
        public void Tick(double dt)
        {
            for (int i = 0; i < _q.Length; i++)
            {
                _left[i] = Math.Max(0.0, _left[i] - dt);
                if (_left[i] > 0.0) continue;
                for (int k = 0; k < 7; k++) _d->qpos[_q[i] + k] = _m->qpos0[_q[i] + k];
                for (int k = 0; k < 6; k++) _d->qvel[_v[i] + k] = 0.0;
            }
        }

        /// <summary>The next waiting cube, put in play with this position (and no turn) and this velocity, in MuJoCo's frame. False if it is still in play.</summary>
        public bool Fire(double x, double y, double z, double vx, double vy, double vz)
        {
            int i = _next;
            if (Count == 0 || _left[i] > 0.0) return false;
            double* q = _d->qpos + _q[i], v = _d->qvel + _v[i];
            q[0] = x; q[1] = y; q[2] = z; q[3] = 1; q[4] = 0; q[5] = 0; q[6] = 0;
            v[0] = vx; v[1] = vy; v[2] = vz; v[3] = 0; v[4] = 0; v[5] = 0;
            _left[i] = SecondsInPlay;
            _next = (i + 1) % Count;
            return true;
        }

        /// <summary>At a point: from `distance` away on `bearing`, starting `rise` above it, on the arc that arrives there at `speed`; or, with dropFrom, let go that far above it.</summary>
        public bool Throw(double[] at, double speed, double bearing, double distance, double rise, double dropFrom = 0.0)
        {
            if (dropFrom > 0.0) return Fire(at[0], at[1], at[2] + dropFrom, 0, 0, 0);
            double t = distance / speed, ax = Math.Cos(bearing), ay = Math.Sin(bearing);
            return Fire(at[0] + ax * distance, at[1] + ay * distance, at[2] + rise, -ax * speed, -ay * speed, -rise / t + 0.5 * 9.81 * t);
        }

        public void ParkAll()
        {
            for (int i = 0; i < Count; i++) _left[i] = 0.0;
            _next = 0;
        }
    }
}
