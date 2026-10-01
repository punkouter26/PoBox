using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using PoBox.Sim;

namespace PoBox.Rl
{
    /// <summary>
    /// The match, simulated by MuJoCo itself: the engine the two policies were trained in, loaded as a
    /// library, stepping the very model they were trained on with the ring's ropes added.
    ///
    /// Why not Unity's physics: measured on 2026-10-01, with Unity's bodies matching MuJoCo's at rest to the
    /// millimetre and the observation matching to three decimal places, the same two policies fell in 70 to
    /// 90% of episodes in Unity and in none in MuJoCo. The two engines disagree about a swinging leg by a few
    /// centimetres within a third of a second, and a policy that was never shown any such disagreement has
    /// no margin for it. Rather than chase that, the fight runs in MuJoCo and Unity shows it.
    ///
    /// So each fighter here has two bodies. The real one is in MuJoCo. The one in the scene is its shadow:
    /// an articulation with gravity and drives switched off, put where the real one is after every step.
    /// Everything else in the game goes on reading the shadow as before: cameras, skin, replay, and the
    /// glove contacts that score hits. The policy reads the real one.
    ///
    /// Each physics step: joint targets in, one MuJoCo step, the state out, the shadows moved.
    /// </summary>
    [DefaultExecutionOrder(500)]   // after the brains and the fighters, before Unity's own physics step
    public class MujocoRing : MonoBehaviour
    {
        [Serializable]
        public class FighterLayout
        {
            public string name;
            public int root_q, root_v, ctrl, pelvis, head, torso, glove_l, glove_r, foot_l, foot_r;
            public int[] jq, jv, geoms;
            public float[] foot_half_l, foot_half_r, default_pos, lower, upper, kp, kv, limit;
            public float stand;
        }

        [Serializable]
        public class Layout
        {
            public string mujoco;
            public int version_number, nq, nv, nu, nbody, ngeom;
            public int off_xpos, off_xquat, off_geom_xpos, off_geom_xmat, off_actuator_force, off_geom_contype, off_geom_conaffinity;
            public float timestep, ring_half;
            public FighterLayout[] fighters;
            public float[] key_qpos;
            public int check_geom;
            public float[] check_xpos;
        }

        [Tooltip("The match model with the ropes in it (training/tools/export_mujoco_layout.py).")]
        [TextArea(2, 6)] public string xml;
        [Tooltip("Which numbers are which, and where in MuJoCo's data the body positions are.")]
        [TextArea(2, 6)] public string layoutJson;
        [Tooltip("The shadow rigs in the scene, in the model's order: fighter A, fighter B.")]
        public MjcfRig[] rigs = new MjcfRig[2];
        [Tooltip("MuJoCo's origin, in the scene: the centre of the ring, at canvas height.")]
        public Vector3 centre;
        public int controlDecimation = 4;

        public bool Ready { get; private set; }
        public Layout L { get; private set; }
        public float StepMs { get; private set; }

        IntPtr _model, _data;
        double[] _state, _qpos, _qvel, _ctrl, _xpos, _xquat, _gpos, _gmat, _force;
        readonly float[][] _target = new float[2][];
        readonly float[] _driveScale = { 1f, 1f };
        readonly Fighter[] _fighters = new Fighter[2];
        readonly bool[] _ghost = new bool[2];
        readonly Vector3[] _prevHead = new Vector3[2];
        readonly bool[] _hasPrev = new bool[2];
        // Where each fighter's head, body and gloves are from its pelvis when it stands in its guard, in
        // its own heading: what a stand-in for it looks like to the other fighter.
        readonly Vector3[] _guardHead = new Vector3[2], _guardBody = new Vector3[2], _guardGloveL = new Vector3[2], _guardGloveR = new Vector3[2];

        void Awake()
        {
            try
            {
                Load();
            }
            catch (Exception e)
            {
                // No library on this platform, or not the build the layout was made for. The fighters then
                // run on Unity's own physics, which works and falls over a great deal.
                Debug.LogError($"[MujocoRing] not running the match on MuJoCo: {e.Message}", this);
                Free();
            }
        }

        void Load()
        {
            L = JsonUtility.FromJson<Layout>(layoutJson);
            if (L == null || L.fighters == null || L.fighters.Length != 2) throw new InvalidOperationException("no layout");
            if (rigs.Length != 2 || rigs[0] == null || rigs[1] == null) throw new InvalidOperationException("two rigs are needed");
            int version = MuJoCoNative.mj_version();
            if (version != L.version_number) throw new InvalidOperationException($"the library is MuJoCo {version}, the layout was made for {L.version_number}");

            var error = new StringBuilder(1024);
            IntPtr spec = MuJoCoNative.mj_parseXMLString(xml, IntPtr.Zero, error, error.Capacity);
            if (spec == IntPtr.Zero) throw new InvalidOperationException("the model did not parse: " + error);
            _model = MuJoCoNative.mj_compile(spec, IntPtr.Zero);
            MuJoCoNative.mj_deleteSpec(spec);
            if (_model == IntPtr.Zero) throw new InvalidOperationException("the model did not compile");
            _data = MuJoCoNative.mj_makeData(_model);

            int nq = MuJoCoNative.mj_stateSize(_model, MuJoCoNative.StateQPos), nv = MuJoCoNative.mj_stateSize(_model, MuJoCoNative.StateQVel);
            int nu = MuJoCoNative.mj_stateSize(_model, MuJoCoNative.StateCtrl);
            if (nq != L.nq || nv != L.nv || nu != L.nu) throw new InvalidOperationException($"the model has {nq}/{nv}/{nu} numbers, the layout expects {L.nq}/{L.nv}/{L.nu}");
            _state = new double[nq + nv];
            _qpos = new double[nq]; _qvel = new double[nv]; _ctrl = new double[nu];
            _xpos = new double[L.nbody * 3]; _xquat = new double[L.nbody * 4];
            _gpos = new double[L.ngeom * 3]; _gmat = new double[L.ngeom * 9];
            _force = new double[nu];

            MuJoCoNative.mj_resetDataKeyframe(_model, _data, 0);
            MuJoCoNative.mj_forward(_model, _data);
            Read();

            // The pointer offsets are the one thing here MuJoCo's API does not vouch for. A glove's position
            // in the model's own starting pose is known; if what is read there is not it, nothing is trusted.
            for (int i = 0; i < 3; i++)
                if (Math.Abs(_gpos[L.check_geom * 3 + i] - L.check_xpos[i]) > 1e-4)
                    throw new InvalidOperationException("the body positions are not where the layout says they are in this build of the library");

            for (int k = 0; k < 2; k++)
            {
                FighterLayout f = L.fighters[k];
                _target[k] = (float[])f.default_pos.Clone();
                double yaw = YawOf(f.pelvis);
                _guardHead[k] = Heading(Geom(f.head) - Body(f.pelvis), yaw);
                _guardBody[k] = Heading(Geom(f.torso) - Body(f.pelvis), yaw);
                _guardGloveL[k] = Heading(Geom(f.glove_l) - Body(f.pelvis), yaw);
                _guardGloveR[k] = Heading(Geom(f.glove_r) - Body(f.pelvis), yaw);

                MjcfRig rig = rigs[k];
                rig.ring = this;
                rig.slot = k;
                _fighters[k] = rig.GetComponentInParent<Fighter>();
                // The shadow: nothing of its own moves it.
                foreach (ArticulationBody ab in rig.GetComponentsInChildren<ArticulationBody>())
                {
                    ab.useGravity = false;
                    if (ab == rig.root || ab.jointType != ArticulationJointType.RevoluteJoint) continue;
                    ArticulationDrive d = ab.xDrive;
                    d.stiffness = 0f;
                    d.damping = 0f;
                    ab.xDrive = d;
                    ab.twistLock = ArticulationDofLock.FreeMotion;
                }
            }
            Ready = true;
            Debug.Log($"[MujocoRing] the match runs on MuJoCo {L.mujoco}: {L.fighters[0].name} v {L.fighters[1].name}, {L.nbody} bodies, {L.ngeom} shapes, step {L.timestep * 1000f:0.#} ms.");
        }

        void OnDestroy() => Free();

        void Free()
        {
            Ready = false;
            foreach (MjcfRig rig in rigs)
                if (rig != null && rig.ring == this) rig.ring = null;
            if (_data != IntPtr.Zero) MuJoCoNative.mj_deleteData(_data);
            if (_model != IntPtr.Zero) MuJoCoNative.mj_deleteModel(_model);
            _data = _model = IntPtr.Zero;
        }

        // ---------------------------------------------------------------- stepping

        void FixedUpdate()
        {
            if (!Ready || !Bout.SimRunning) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int k = 0; k < 2; k++)
                if (_fighters[k] != null) SetGhost(k, _fighters[k].IsDown);

            for (int k = 0; k < 2; k++)
            {
                FighterLayout f = L.fighters[k];
                float s = _driveScale[k];
                for (int i = 0; i < f.jq.Length; i++)
                {
                    double want = _target[k][i];
                    // Hurt: the spring to the target is weakened, the damping is not. Asking for a point
                    // part of the way from where the joint is to where the policy wants it is exactly that.
                    if (s < 0.999f) { double q = _qpos[f.jq[i]]; want = q + s * (want - q); }
                    _ctrl[f.ctrl + i] = want;
                }
            }
            MuJoCoNative.mj_setState(_model, _data, _ctrl, MuJoCoNative.StateCtrl);
            MuJoCoNative.mj_step(_model, _data);
            Read();
            for (int k = 0; k < 2; k++) Push(k);

            float ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            StepMs += (ms - StepMs) * 0.05f;
        }

        void Read()
        {
            MuJoCoNative.mj_getState(_model, _data, _state, MuJoCoNative.StateQPos | MuJoCoNative.StateQVel);
            Array.Copy(_state, 0, _qpos, 0, _qpos.Length);
            Array.Copy(_state, _qpos.Length, _qvel, 0, _qvel.Length);
            Marshal.Copy(Marshal.ReadIntPtr(_data, L.off_xpos), _xpos, 0, _xpos.Length);
            Marshal.Copy(Marshal.ReadIntPtr(_data, L.off_xquat), _xquat, 0, _xquat.Length);
            Marshal.Copy(Marshal.ReadIntPtr(_data, L.off_geom_xpos), _gpos, 0, _gpos.Length);
            Marshal.Copy(Marshal.ReadIntPtr(_data, L.off_geom_xmat), _gmat, 0, _gmat.Length);
            Marshal.Copy(Marshal.ReadIntPtr(_data, L.off_actuator_force), _force, 0, _force.Length);
        }

        /// <summary>Puts the shadow where the real body is, moving as it moves.</summary>
        void Push(int k)
        {
            MjcfRig rig = rigs[k];
            FighterLayout f = L.fighters[k];
            int b = f.pelvis, rq = f.root_q, rv = f.root_v;
            // MuJoCo's x, y, z are the scene's x, z, y. Swapping two axes turns a rotation of w about n into
            // one of -w about the swapped n.
            var position = new Vector3((float)_xpos[b * 3], (float)_xpos[b * 3 + 2], (float)_xpos[b * 3 + 1]) + centre;
            var rotation = new Quaternion(-(float)_qpos[rq + 4], -(float)_qpos[rq + 6], -(float)_qpos[rq + 5], (float)_qpos[rq + 3]);
            ArticulationBody root = rig.root;
            root.TeleportRoot(position, rotation);
            root.linearVelocity = new Vector3((float)_qvel[rv], (float)_qvel[rv + 2], (float)_qvel[rv + 1]);
            // A free joint's angular velocity is in the body's own frame.
            root.angularVelocity = rotation * new Vector3(-(float)_qvel[rv + 3], -(float)_qvel[rv + 5], -(float)_qvel[rv + 4]);
            for (int i = 0; i < f.jq.Length; i++)
            {
                ArticulationBody ab = rig.joints[i];
                ab.jointPosition = new ArticulationReducedSpace(rig.sign[i] * (float)_qpos[f.jq[i]]);
                ab.jointVelocity = new ArticulationReducedSpace(rig.sign[i] * (float)_qvel[f.jv[i]]);
            }
        }

        // ---------------------------------------------------------------- what the rig and the brain ask

        public void CopyJointState(int k, float[] pos, float[] vel)
        {
            FighterLayout f = L.fighters[k];
            for (int i = 0; i < f.jq.Length; i++)
            {
                pos[i] = (float)_qpos[f.jq[i]];
                vel[i] = (float)_qvel[f.jv[i]];
            }
        }

        public float Torque(int k, int joint) => (float)_force[L.fighters[k].ctrl + joint];
        public float Speed(int k, int joint) => (float)_qvel[L.fighters[k].jv[joint]];
        public float TorqueLimit(int k, int joint) => L.fighters[k].limit[joint];

        /// <summary>Joint targets, radians, trainer's sign. Returns how many had to be clamped to the joint ranges.</summary>
        public int SetTargets(int k, float[] targets)
        {
            FighterLayout f = L.fighters[k];
            int clamped = 0;
            for (int i = 0; i < f.jq.Length; i++)
            {
                float t = targets[i];
                if (t < f.lower[i] || t > f.upper[i]) { clamped++; t = Mathf.Clamp(t, f.lower[i], f.upper[i]); }
                _target[k][i] = t;
            }
            return clamped;
        }

        public void SetDriveScale(int k, float scale) => _driveScale[k] = Mathf.Clamp01(scale);

        /// <summary>
        /// A fighter that is down for a count still lies on the canvas and against the ropes, but the other
        /// fighter passes through it: led away to a neutral corner, a policy that has never seen a body on
        /// the floor walks straight over this one and goes down with it. Done by moving the downed fighter's
        /// shapes to a collision layer only the ring is on.
        /// </summary>
        void SetGhost(int k, bool ghost)
        {
            if (_ghost[k] == ghost || L.off_geom_contype == 0 || L.fighters[k].geoms == null) return;
            _ghost[k] = ghost;
            IntPtr contype = Marshal.ReadIntPtr(_model, L.off_geom_contype), conaffinity = Marshal.ReadIntPtr(_model, L.off_geom_conaffinity);
            int layer = ghost ? 2 : 1;
            foreach (int g in L.fighters[k].geoms)
            {
                Marshal.WriteInt32(contype, g * 4, layer);
                Marshal.WriteInt32(conaffinity, g * 4, layer);
            }
        }

        /// <summary>Stands one fighter in its guard at a spot on the canvas, facing a way, with nothing moving. The other is left as it is.</summary>
        public void ResetFighter(int k, Vector3 floorPoint, Vector3 facing)
        {
            if (!Ready) return;
            FighterLayout f = L.fighters[k];
            double yaw = Math.Atan2(facing.z, facing.x);
            _qpos[f.root_q] = floorPoint.x - centre.x;
            _qpos[f.root_q + 1] = floorPoint.z - centre.z;
            _qpos[f.root_q + 2] = f.stand + 0.002;
            _qpos[f.root_q + 3] = Math.Cos(yaw * 0.5);
            _qpos[f.root_q + 4] = 0.0;
            _qpos[f.root_q + 5] = 0.0;
            _qpos[f.root_q + 6] = Math.Sin(yaw * 0.5);
            for (int i = 0; i < 6; i++) _qvel[f.root_v + i] = 0.0;
            for (int i = 0; i < f.jq.Length; i++)
            {
                _qpos[f.jq[i]] = f.default_pos[i];
                _qvel[f.jv[i]] = 0.0;
                _target[k][i] = f.default_pos[i];
                _ctrl[f.ctrl + i] = f.default_pos[i];
            }
            _driveScale[k] = 1f;
            SetGhost(k, false);
            _hasPrev[k] = false;
            _hasPrev[1 - k] = false;    // the other fighter's target has just jumped
            Array.Copy(_qpos, 0, _state, 0, _qpos.Length);
            Array.Copy(_qvel, 0, _state, _qpos.Length, _qvel.Length);
            MuJoCoNative.mj_setState(_model, _data, _state, MuJoCoNative.StateQPos | MuJoCoNative.StateQVel);
            MuJoCoNative.mj_setState(_model, _data, _ctrl, MuJoCoNative.StateCtrl);
            MuJoCoNative.mj_forward(_model, _data);
            Read();
            Push(k);
        }

        // ---------------------------------------------------------------- the observation

        Vector3 Body(int id) => new Vector3((float)_xpos[id * 3], (float)_xpos[id * 3 + 1], (float)_xpos[id * 3 + 2]);
        Vector3 Geom(int id) => new Vector3((float)_gpos[id * 3], (float)_gpos[id * 3 + 1], (float)_gpos[id * 3 + 2]);

        double YawOf(int body)
        {
            double w = _xquat[body * 4], x = _xquat[body * 4 + 1], y = _xquat[body * 4 + 2], z = _xquat[body * 4 + 3];
            return Math.Atan2(2.0 * (w * z + x * y), 1.0 - 2.0 * (y * y + z * z));
        }

        /// <summary>A vector in MuJoCo's world, turned into the frame that has turned yaw about the vertical.</summary>
        static Vector3 Heading(Vector3 v, double yaw)
        {
            float c = (float)Math.Cos(yaw), s = (float)Math.Sin(yaw);
            return new Vector3(c * v.x + s * v.y, -s * v.x + c * v.y, v.z);
        }

        static void Put(float[] o, int at, Vector3 v) { o[at] = v.x; o[at + 1] = v.y; o[at + 2] = v.z; }

        float Sole(int geom, float[] half)
        {
            int m = geom * 9;
            return (float)(_gpos[geom * 3 + 2] - (Math.Abs(_gmat[m + 6]) * half[0] + Math.Abs(_gmat[m + 7]) * half[1] + Math.Abs(_gmat[m + 8]) * half[2]));
        }

        /// <summary>
        /// The trainer's observation for one fighter, number for number (training/envs/boxing.py, and
        /// training/tools/eval_cmujoco.py, which is this routine in Python and was checked first), read from
        /// MuJoCo's own state. With <paramref name="phantom"/> set, the other fighter is replaced by a
        /// stand-in of it standing in its guard at that spot in the scene, facing this one: something for a
        /// fighter to square up to while the referee counts over the real one.
        /// </summary>
        public void Observe(int k, float[] obs, float[] lastAction, Vector3? phantom, bool zeroOpponent)
        {
            FighterLayout f = L.fighters[k], other = L.fighters[1 - k];
            int n = f.jq.Length, o = 0, b = f.pelvis, rv = f.root_v;
            Vector3 pos = Body(b);
            double w = _xquat[b * 4], x = _xquat[b * 4 + 1], y = _xquat[b * 4 + 2], z = _xquat[b * 4 + 3];
            double yaw = YawOf(b);
            // World to body: the transpose of the pelvis's rotation matrix, written out.
            double r00 = 1 - 2 * (y * y + z * z), r01 = 2 * (x * y - w * z), r02 = 2 * (x * z + w * y);
            double r10 = 2 * (x * y + w * z), r11 = 1 - 2 * (x * x + z * z), r12 = 2 * (y * z - w * x);
            double r20 = 2 * (x * z - w * y), r21 = 2 * (y * z + w * x), r22 = 1 - 2 * (x * x + y * y);
            double vx = _qvel[rv], vy = _qvel[rv + 1], vz = _qvel[rv + 2];
            var linWorld = new Vector3((float)vx, (float)vy, (float)vz);

            obs[o++] = (float)(r00 * vx + r10 * vy + r20 * vz);
            obs[o++] = (float)(r01 * vx + r11 * vy + r21 * vz);
            obs[o++] = (float)(r02 * vx + r12 * vy + r22 * vz);
            obs[o++] = (float)_qvel[rv + 3]; obs[o++] = (float)_qvel[rv + 4]; obs[o++] = (float)_qvel[rv + 5];
            obs[o++] = (float)-r20; obs[o++] = (float)-r21; obs[o++] = (float)-r22;

            for (int i = 0; i < n; i++) obs[o + i] = (float)_qpos[f.jq[i]] - f.default_pos[i];
            o += n;
            for (int i = 0; i < n; i++) obs[o + i] = (float)_qvel[f.jv[i]];
            o += n;
            for (int i = 0; i < n; i++) obs[o + i] = lastAction[i];
            o += n;

            obs[o++] = Sole(f.foot_l, f.foot_half_l) < 0.005f ? 1f : 0f;
            obs[o++] = Sole(f.foot_r, f.foot_half_r) < 0.005f ? 1f : 0f;
            obs[o++] = pos.z;

            Vector3 head, body, theirL, theirR;
            double theirYaw;
            if (phantom.HasValue)
            {
                var at = new Vector3(phantom.Value.x - centre.x, phantom.Value.z - centre.z, other.stand);
                theirYaw = Math.Atan2(pos.y - at.y, pos.x - at.x);
                Vector3 Place(Vector3 local)
                {
                    float c = (float)Math.Cos(theirYaw), s = (float)Math.Sin(theirYaw);
                    return new Vector3(at.x + c * local.x - s * local.y, at.y + s * local.x + c * local.y, at.z + local.z);
                }
                head = Place(_guardHead[1 - k]); body = Place(_guardBody[1 - k]);
                theirL = Place(_guardGloveL[1 - k]); theirR = Place(_guardGloveR[1 - k]);
            }
            else
            {
                head = Geom(other.head); body = Geom(other.torso);
                theirL = Geom(other.glove_l); theirR = Geom(other.glove_r);
                theirYaw = YawOf(other.pelvis);
            }

            float dt = L.timestep * controlDecimation;
            Vector3 headVelocity = _hasPrev[k] ? (head - _prevHead[k]) / dt : Vector3.zero;
            // Nobody's head does 20 m/s: that is the target changing, or being stood back up.
            if (headVelocity.sqrMagnitude > 400f) headVelocity = Vector3.zero;
            _prevHead[k] = head;
            _hasPrev[k] = true;

            Put(obs, o, Heading(head - pos, yaw)); o += 3;
            Put(obs, o, Heading(body - pos, yaw)); o += 3;
            Put(obs, o, Heading(headVelocity - linWorld, yaw)); o += 3;
            Put(obs, o, Heading(Geom(f.glove_l) - pos, yaw)); o += 3;
            Put(obs, o, Heading(Geom(f.glove_r) - pos, yaw)); o += 3;
            if (zeroOpponent)
            {
                for (int i = 0; i < 8; i++) obs[o + i] = 0f;
                o += 8;
            }
            else
            {
                Put(obs, o, Heading(theirL - pos, yaw)); o += 3;
                Put(obs, o, Heading(theirR - pos, yaw)); o += 3;
                obs[o++] = (float)Math.Cos(theirYaw - yaw);
                obs[o++] = (float)Math.Sin(theirYaw - yaw);
            }
            Vector3 ring = Heading(new Vector3(-pos.x, -pos.y, 0f), yaw) / L.ring_half;
            obs[o++] = ring.x;
            obs[o++] = ring.y;

            for (int i = 0; i < obs.Length; i++)
            {
                float v = obs[i];
                obs[i] = float.IsNaN(v) ? 0f : Mathf.Clamp(v, -100f, 100f);
            }
        }
    }
}
