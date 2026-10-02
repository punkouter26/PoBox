using System;
using UnityEngine;

namespace PoBox.Mj
{
    /// <summary>
    /// A run recorded in plain C MuJoCo by training/tools/footwork_c.py --reference: a row a control step,
    /// and what was written into the simulation from outside (a cube thrown, a shove) as events. A row's
    /// observation and action are those before its step; everything else in it is after. MuJoCo's frame.
    /// </summary>
    [Serializable]
    public class ReferenceTrajectory
    {
        [Serializable]
        public class Row
        {
            public double t;
            public double[] obs, action, ctrl, torque, root_pos, root_quat, root_linvel, root_angvel, joint_pos, joint_vel;
            public bool[] foot_contact;
            public bool fell;
        }

        [Serializable]
        public class Event
        {
            public double t, seconds;
            public int cube = -1;            // -1: not a cube
            public double[] qpos, qvel, shove;
        }

        public string mujoco, model, policy;
        public double control_dt, timestep;
        public string[] joint_order;
        public double[] stand_in_xy, command;
        public Event[] events;
        public Row[] rows;

        public static ReferenceTrajectory FromJson(string json) => JsonUtility.FromJson<ReferenceTrajectory>(json);
    }
}
