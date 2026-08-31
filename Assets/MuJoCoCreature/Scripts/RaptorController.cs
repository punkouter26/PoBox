// Runtime inference for the Raptor -- the second MuJoCo creature.
//
// Same inference stack as CreatureSentisController (com.unity.ai.inference
// 2.6.1, namespace Unity.InferenceEngine -- NOT Sentis, whatever that file's
// name says), with two deliberate differences:
//
// 1. SCOPED TO ITS OWN CREATURE. MjScene is a singleton and merges every
//    creature's MJCF into one model, so qpos[2], actuator [0..nu) and a
//    whole-model reset all silently belong to whichever creature loaded
//    first. This controller binds bodies, joints and actuators found under
//    _creatureRoot only, addresses state through their QposAddress /
//    DofAddress / MujocoId, and resets nothing that is not its own.
//
// 2. DECIMATED CONTROL. Physics steps at 0.005 s; the policy was trained at
//    substeps=4 (50 Hz control, matching PoBox's FixedUpdate convention).
//    Inference runs every 4th FixedUpdate and ctrl holds in between --
//    position servos chase the last target, which is exactly what training
//    simulated.
//
// OBSERVATIONS mirror RaptorBalanceEnv.compute_obs term for term:
//   root   (13)  pelvis height; pelvis-local lin vel; pelvis-local ang
//                vel / 20; pelvis up (world); pelvis forward (world, -Y)
//   joints (7*13) local rotation quaternion + ang vel / 20, canonical order
//   feet    (8)  grounded flag + contact normal, x2
//   feet    (2)  normalised ground distance
//   total  114
// Feeding a different vector of the same width loads, runs, and produces
// confident nonsense -- shape is the only thing anything checks.

using System;
using System.Collections.Generic;
using Mujoco;
using Unity.InferenceEngine;
using UnityEngine;

namespace PoBox.MuJoCoCreature
{
    [DefaultExecutionOrder(-50)]
    public sealed class RaptorController : MonoBehaviour
    {
        private const int ROOT_OBS = 13;
        private const int PER_JOINT_OBS = 7;
        private const int FOOT_OBS = 8;
        private const int FOOT_HEIGHT_OBS = 2;
        private const float ANGULAR_VELOCITY_SCALE = 20f;
        private const float FOOT_RAY_MAX = 1f;

        // Canonical raptor joint order (raptor_mjcf.ORDER minus the pelvis
        // root). The action vector is consumed in MJCF actuator order, which
        // walks this list pitch -> roll -> yaw; neither may be reordered once
        // a brain is trained. Matched on the stem: MjScene appends numeric
        // suffixes when it regenerates the model.
        private static readonly string[] JointBodies =
        {
            "Rap_Torso", "Rap_Neck", "Rap_Head", "Rap_Tail01", "Rap_Tail02",
            "Rap_ThighL", "Rap_ShinL", "Rap_MetaL", "Rap_FootL",
            "Rap_ThighR", "Rap_ShinR", "Rap_MetaR", "Rap_FootR"
        };

        [Header("Assets")]
        [Tooltip("May be left empty for the parity harness: binding and " +
                 "observation gathering still work, no actions are applied.")]
        [SerializeField] private ModelAsset _onnxModelAsset;

        [Header("Creature")]
        [Tooltip("Root GameObject of the imported raptor. Everything this " +
                 "controller touches lives under it.")]
        [SerializeField] private GameObject _creatureRoot;

        [Header("Inference")]
        [SerializeField] private BackendType _backend = BackendType.GPUCompute;
        [SerializeField] private string _actionOutputName = "continuous_actions";
        [Tooltip("FixedUpdates per policy decision. 4 = the substeps the " +
                 "policy was trained with; ctrl holds between decisions.")]
        [SerializeField] private int _decimation = 4;

        [Header("Auto-reset")]
        [Tooltip("Reset when the pelvis body's world z drops below this.")]
        [SerializeField] private float _fallHeight = 0.25f;

        [Header("Timestep")]
        [Tooltip("Physics timestep, applied to Time.fixedDeltaTime for the " +
                 "lifetime of this component and restored afterwards. MjScene " +
                 "stamps the MJCF timestep with Unity's rate, so this IS the " +
                 "MuJoCo timestep. 0 disables the override.")]
        [SerializeField] private float _fixedTimestepOverride = 0.005f;

        private float _previousFixedDeltaTime = -1f;

        private MjScene _mjScene;
        private Worker _worker;
        private Model _runtimeModel;
        private Tensor<float> _input;
        private float[] _observations;
        private float[] _actions;

        private int[] _jointBodyIds;
        private int[] _jointParentIds;
        private int _pelvisId = -1;
        private int _footLeftId = -1;
        private int _footRightId = -1;
        private int[] _actuatorIds;                    // model ids, training order
        private MjActuator[] _actuators;               // same order as _actuatorIds
        private MjBaseJoint[] _ownJoints;              // for the scoped reset
        private double[] _ownQpos0;                    // qpos0 slices, same order
        private float _footContactHeight = 0.045f;     // FOOT_RADIUS 0.03 * 1.5
        private int _stepCounter;
        private bool _ready;

        public int ObservationCount =>
            ROOT_OBS + PER_JOINT_OBS * JointBodies.Length + FOOT_OBS + FOOT_HEIGHT_OBS;

        public bool IsBound => _ready;
        public float DebugPelvisHeight { get; private set; }
        public float DebugMaxAbsAction { get; private set; }
        public double DebugSimTime { get; private set; }
        public int DebugResetCount { get; private set; }
        public int DebugStepCount { get; private set; }

        /// <summary>Force a bind + gather; returns the raw 114-vector. For the
        /// parity harness -- the only way to know the C# and Python builders
        /// agree is to feed both the same state and diff element-wise.</summary>
        public unsafe float[] DebugGatherObservations()
        {
            if (!_ready && !TryBind())
            {
                return null;
            }
            GatherObservations(_mjScene.Data);
            return (float[])_observations.Clone();
        }

        /// <summary>Overwrite full-model qpos/qvel, then recompute derived
        /// state. Parity harness only -- deliberately NOT scoped, the harness
        /// owns the whole test scene.</summary>
        public unsafe void DebugSetState(double[] qpos, double[] qvel)
        {
            if (!_ready && !TryBind())
            {
                return;
            }
            for (int i = 0; i < qpos.Length && i < (int)_mjScene.Model->nq; i++)
            {
                _mjScene.Data->qpos[i] = qpos[i];
            }
            for (int i = 0; i < qvel.Length && i < (int)_mjScene.Model->nv; i++)
            {
                _mjScene.Data->qvel[i] = qvel[i];
            }
            MujocoLib.mj_forward(_mjScene.Model, _mjScene.Data);
        }

        /// <summary>Save the model MuJoCo is ACTUALLY running to an MJCF file.
        /// Phase-6 debugging: when Unity behaviour diverges from training, the
        /// first question is whether the runtime model is the training model.</summary>
        public unsafe string DebugSaveRuntimeModel(string path)
        {
            if (!_ready && !TryBind())
            {
                return "not bound";
            }
            var err = new System.Text.StringBuilder(1024);
            int ok = MujocoLib.mj_saveLastXML(path, _mjScene.Model, err, err.Capacity);
            return ok == 1 ? $"saved {path}" : $"FAILED: {err}";
        }

        /// <summary>Reset, gather obs, run one inference, and report the raw
        /// action vector plus the ctrl each action maps to -- comparable
        /// number-for-number with the python side at qpos0.</summary>
        public unsafe string DebugProbeDecision()
        {
            if (!_ready && !TryBind())
            {
                return "not bound";
            }
            if (_worker == null)
            {
                return "no worker";
            }
            ResetCreature();
            MujocoLib.mjData_* d = _mjScene.Data;
            GatherObservations(d);
            _input.Upload(_observations);
            _worker.Schedule(_input);
            Tensor peeked = string.IsNullOrEmpty(_actionOutputName)
                ? _worker.PeekOutput()
                : _worker.PeekOutput(_actionOutputName);
            using (var readable = (peeked as Tensor<float>)?.ReadbackAndClone())
            {
                if (readable == null)
                {
                    return "readback failed";
                }
                readable.DownloadToArray().CopyTo(_actions, 0);
            }
            var sb = new System.Text.StringBuilder();
            sb.Append("obs[0..5]: ");
            for (int i = 0; i < 6; i++) sb.Append(_observations[i].ToString("F5")).Append(' ');
            sb.Append("\nactions: ");
            for (int i = 0; i < _actions.Length; i++) sb.Append(_actions[i].ToString("F4")).Append(' ');
            sb.Append("\nctrlrange[0..3]: ");
            for (int i = 0; i < 4; i++)
            {
                int id = _actuatorIds[i];
                sb.Append($"[{_mjScene.Model->actuator_ctrlrange[2 * id]:F4},{_mjScene.Model->actuator_ctrlrange[2 * id + 1]:F4}] ");
            }
            sb.Append($"\ntimestep: {_mjScene.Model->opt.timestep:F6}  nu: {(int)_mjScene.Model->nu}");
            return sb.ToString();
        }

        // -- tick-level trace, for hunting Unity-vs-python divergence -------
        private System.Text.StringBuilder _trace;
        private int _traceTicksLeft;
        private int _traceTick;
        private bool _tracePolicy;

        /// <summary>Reset, then record (tick, pelvisZ, action[0], ctrl[0]) for
        /// `ticks` FixedUpdates. policy=false holds ctrl at 0 -- a passive
        /// fall curve directly comparable with the python one.</summary>
        public string DebugBeginTrace(int ticks, bool policy)
        {
            if (!_ready && !TryBind())
            {
                return "not bound";
            }
            ResetCreature();
            _trace = new System.Text.StringBuilder("tick,pelvisZ,act0,ctrl0\n");
            _traceTicksLeft = ticks;
            _traceTick = 0;
            _tracePolicy = policy;
            return $"tracing {ticks} ticks policy={policy}";
        }

        public string DebugDumpTrace() =>
            _traceTicksLeft > 0 ? $"still {_traceTicksLeft} ticks left" : _trace?.ToString() ?? "no trace";

        private unsafe void TraceTick(MujocoLib.mjData_* d)
        {
            if (_trace == null || _traceTicksLeft <= 0)
            {
                return;
            }
            _traceTicksLeft--;
            _trace.AppendLine(FormattableString.Invariant(
                $"{_traceTick++},{d->xpos[3 * _pelvisId + 2]:F6},{_actions[0]:F6},{d->ctrl[_actuatorIds[0]]:F6}"));
        }

        private void Awake()
        {
            if (_fixedTimestepOverride > 0f)
            {
                _previousFixedDeltaTime = Time.fixedDeltaTime;
                Time.fixedDeltaTime = _fixedTimestepOverride;
            }
        }

        private void OnEnable()
        {
            // MjScene.Instance is resolved lazily in the first FixedUpdate --
            // touching it here races the singleton's own Awake.
            _ready = false;
        }

        private static string Stem(string name)
        {
            // Strip a trailing "_<digits>" suffix ONLY -- the exporter's
            // numeric suffix. "Rap_Tail01" must survive intact; "Rap_Tail01_7"
            // becomes "Rap_Tail01". Mirrors Python's re.sub(r"_\d+$", "", s).
            int i = name.Length;
            while (i > 0 && char.IsDigit(name[i - 1])) i--;
            if (i > 0 && i < name.Length && name[i - 1] == '_')
            {
                return name.Substring(0, i - 1);
            }
            return name;
        }

        private unsafe bool TryBind()
        {
            if (_mjScene == null)
            {
                _mjScene = MjScene.InstanceExists ? MjScene.Instance : null;
            }
            if (_mjScene == null || _mjScene.Model == null || _mjScene.Data == null)
            {
                return false;
            }
            if (_creatureRoot == null)
            {
                Debug.LogError($"{nameof(RaptorController)}: no creature root assigned.", this);
                enabled = false;
                return false;
            }

            // -- bodies, by stem name, scoped to the creature root ----------
            _jointBodyIds = new int[JointBodies.Length];
            for (int i = 0; i < _jointBodyIds.Length; i++) _jointBodyIds[i] = -1;
            foreach (var body in _creatureRoot.GetComponentsInChildren<MjBody>())
            {
                var stem = Stem(body.name);
                if (stem == "Rap_Pelvis") _pelvisId = body.MujocoId;
                else if (stem == "Rap_FootL") _footLeftId = body.MujocoId;
                else if (stem == "Rap_FootR") _footRightId = body.MujocoId;
                int index = Array.IndexOf(JointBodies, stem);
                if (index >= 0) _jointBodyIds[index] = body.MujocoId;
            }
            if (_pelvisId < 0 || _footLeftId < 0 || _footRightId < 0)
            {
                Debug.LogError($"{nameof(RaptorController)}: pelvis/feet not found under root.", this);
                enabled = false;
                return false;
            }
            _jointParentIds = new int[_jointBodyIds.Length];
            for (int i = 0; i < _jointBodyIds.Length; i++)
            {
                if (_jointBodyIds[i] < 0)
                {
                    Debug.LogError($"{nameof(RaptorController)}: no MjBody stem '{JointBodies[i]}'. " +
                                   "The observation layout depends on all 13 being present.", this);
                    enabled = false;
                    return false;
                }
                _jointParentIds[i] = _mjScene.Model->body_parentid[_jointBodyIds[i]];
            }

            // -- actuators, model order == training order -------------------
            var acts = _creatureRoot.GetComponentsInChildren<MjActuator>();
            Array.Sort(acts, (a, b) => a.MujocoId.CompareTo(b.MujocoId));
            _actuators = acts;
            _actuatorIds = new int[acts.Length];
            for (int i = 0; i < acts.Length; i++) _actuatorIds[i] = acts[i].MujocoId;
            _actions = new float[_actuatorIds.Length];

            // -- own joints + their qpos0 slices, for the scoped reset ------
            _ownJoints = _creatureRoot.GetComponentsInChildren<MjBaseJoint>();
            var qpos0 = new List<double>();
            foreach (var j in _ownJoints)
            {
                int n = QposSize(j);
                for (int k = 0; k < n; k++)
                {
                    qpos0.Add(_mjScene.Model->qpos0[j.QposAddress + k]);
                }
            }
            _ownQpos0 = qpos0.ToArray();

            _observations = new float[ObservationCount];

            if (_onnxModelAsset != null)
            {
                _runtimeModel = ModelLoader.Load(_onnxModelAsset);
                _worker = new Worker(_runtimeModel, _backend);
                _input = new Tensor<float>(new TensorShape(1, ObservationCount));
            }
            else
            {
                Debug.LogWarning($"{nameof(RaptorController)}: no ONNX assigned -- " +
                                 "parity mode, observations only.", this);
            }

            Debug.Log($"{nameof(RaptorController)}: bound. obs={ObservationCount} " +
                      $"actuators={_actuatorIds.Length} decimation={_decimation} backend={_backend}");
            _ready = true;
            return true;
        }

        private static int QposSize(MjBaseJoint j)
        {
            switch (j)
            {
                case MjFreeJoint _: return 7;
                case MjBallJoint _: return 4;
                default: return 1;
            }
        }

        private static int DofSize(MjBaseJoint j)
        {
            switch (j)
            {
                case MjFreeJoint _: return 6;
                case MjBallJoint _: return 3;
                default: return 1;
            }
        }

        private unsafe void FixedUpdate()
        {
            if (!_ready && !TryBind())
            {
                return;
            }

            MujocoLib.mjData_* d = _mjScene.Data;

            float pelvisZ = (float)d->xpos[3 * _pelvisId + 2];
            DebugPelvisHeight = pelvisZ;
            DebugSimTime = d->time;

            if (_trace != null && _traceTicksLeft > 0)
            {
                // Trace mode: record the raw trajectory. No auto-reset (it
                // would truncate the curve); passive traces skip inference so
                // ctrl stays at the reset value of 0.
                TraceTick(d);
                if (!_tracePolicy)
                {
                    return;
                }
            }
            else if (pelvisZ < _fallHeight)
            {
                ResetCreature();
                return;
            }

            if (_worker == null)
            {
                return; // parity mode
            }

            // Decimated control: infer on every _decimation-th step, hold
            // ctrl in between -- the cadence the policy was trained at.
            if (_stepCounter++ % Mathf.Max(1, _decimation) != 0)
            {
                return;
            }

            GatherObservations(d);

            _input.Upload(_observations);
            _worker.Schedule(_input);

            // PeekOutput returns a tensor the worker owns -- never dispose
            // it. ReadbackAndClone gives a CPU copy we DO own.
            Tensor peeked = string.IsNullOrEmpty(_actionOutputName)
                ? _worker.PeekOutput()
                : _worker.PeekOutput(_actionOutputName);
            using (var readable = (peeked as Tensor<float>)?.ReadbackAndClone())
            {
                if (readable == null)
                {
                    Debug.LogError($"{nameof(RaptorController)}: output '{_actionOutputName}' is not a float tensor.", this);
                    enabled = false;
                    return;
                }
                readable.DownloadToArray().CopyTo(_actions, 0);
            }

            DebugStepCount++;
            float maxAction = 0f;
            for (int i = 0; i < _actions.Length; i++)
            {
                maxAction = Mathf.Max(maxAction, Mathf.Abs(_actions[i]));
            }
            DebugMaxAbsAction = maxAction;

            for (int i = 0; i < _actuatorIds.Length; i++)
            {
                // [-1,1] -> target angle in radians, zero-centred exactly as
                // Systems_FighterRig.MapZeroCentered:
                //   action >= 0 ? action * high : -action * low
                //
                // Written to the COMPONENT's Control field, never d->ctrl:
                // MjScene.SyncUnityToMjState() copies Control into d->ctrl
                // after every step, so a direct d->ctrl write survives exactly
                // one step and is then silently zeroed. At 4x decimation that
                // ran 3 of every 4 steps against rest-pose targets -- the bug
                // behind the Phase 6 fall-loop. (Nick's controller writes
                // d->ctrl and survives only because it rewrites every tick.)
                int id = _actuatorIds[i];
                double low = _mjScene.Model->actuator_ctrlrange[2 * id];
                double high = _mjScene.Model->actuator_ctrlrange[2 * id + 1];
                float a = Mathf.Clamp(_actions[i], -1f, 1f);
                _actuators[i].Control = (float)(a >= 0f ? a * high : -a * low);
            }
        }

        private unsafe void GatherObservations(MujocoLib.mjData_* d)
        {
            int c = 0;

            // --- root (13) ---
            Vector3 pelvisPos = BodyPos(d, _pelvisId);
            Quaternion pelvisRot = BodyQuat(d, _pelvisId);
            Quaternion inv = Quaternion.Inverse(pelvisRot);

            _observations[c++] = pelvisPos.z;                       // MuJoCo Z is up
            Vector3 lin = inv * BodyLinVel(d, _pelvisId);
            _observations[c++] = lin.x; _observations[c++] = lin.y; _observations[c++] = lin.z;
            Vector3 ang = (inv * BodyAngVel(d, _pelvisId)) / ANGULAR_VELOCITY_SCALE;
            _observations[c++] = ang.x; _observations[c++] = ang.y; _observations[c++] = ang.z;
            Vector3 up = pelvisRot * new Vector3(0f, 0f, 1f);
            _observations[c++] = up.x; _observations[c++] = up.y; _observations[c++] = up.z;
            Vector3 fwd = pelvisRot * new Vector3(0f, -1f, 0f);     // raptor faces -Y
            _observations[c++] = fwd.x; _observations[c++] = fwd.y; _observations[c++] = fwd.z;

            // --- proprioception (7 per joint) ---
            for (int j = 0; j < _jointBodyIds.Length; j++)
            {
                Quaternion childRot = BodyQuat(d, _jointBodyIds[j]);
                Quaternion parentRot = BodyQuat(d, _jointParentIds[j]);
                Quaternion local = Quaternion.Inverse(parentRot) * childRot;
                _observations[c++] = local.w;
                _observations[c++] = local.x;
                _observations[c++] = local.y;
                _observations[c++] = local.z;
                Vector3 w = BodyAngVel(d, _jointBodyIds[j]) / ANGULAR_VELOCITY_SCALE;
                _observations[c++] = w.x; _observations[c++] = w.y; _observations[c++] = w.z;
            }

            // --- foot contact (8): flat plane, normal is +Z when down ---
            float lz = BodyPos(d, _footLeftId).z;
            float rz = BodyPos(d, _footRightId).z;
            float lGround = lz < _footContactHeight ? 1f : 0f;
            float rGround = rz < _footContactHeight ? 1f : 0f;
            _observations[c++] = lGround;
            _observations[c++] = 0f; _observations[c++] = 0f; _observations[c++] = lGround;
            _observations[c++] = rGround;
            _observations[c++] = 0f; _observations[c++] = 0f; _observations[c++] = rGround;

            // --- foot height (2) ---
            _observations[c++] = Mathf.Clamp01(lz / FOOT_RAY_MAX);
            _observations[c++] = Mathf.Clamp01(rz / FOOT_RAY_MAX);
        }

        private unsafe Vector3 BodyPos(MujocoLib.mjData_* d, int id) =>
            new Vector3((float)d->xpos[3 * id], (float)d->xpos[3 * id + 1], (float)d->xpos[3 * id + 2]);

        private unsafe Quaternion BodyQuat(MujocoLib.mjData_* d, int id) =>
            new Quaternion((float)d->xquat[4 * id + 1], (float)d->xquat[4 * id + 2],
                           (float)d->xquat[4 * id + 3], (float)d->xquat[4 * id]); // mjc is wxyz

        // cvel is [angular(3), linear(3)].
        private unsafe Vector3 BodyAngVel(MujocoLib.mjData_* d, int id) =>
            new Vector3((float)d->cvel[6 * id], (float)d->cvel[6 * id + 1], (float)d->cvel[6 * id + 2]);

        private unsafe Vector3 BodyLinVel(MujocoLib.mjData_* d, int id) =>
            new Vector3((float)d->cvel[6 * id + 3], (float)d->cvel[6 * id + 4], (float)d->cvel[6 * id + 5]);

        /// <summary>Reset THIS creature only: its joints back to qpos0, its
        /// velocities and actuator targets to zero. Other creatures sharing
        /// the MjScene are untouched.</summary>
        public unsafe void ResetCreature()
        {
            if (_mjScene == null || _mjScene.Data == null || _ownJoints == null)
            {
                return;
            }
            MujocoLib.mjData_* d = _mjScene.Data;
            int cursor = 0;
            foreach (var j in _ownJoints)
            {
                int nq = QposSize(j);
                for (int k = 0; k < nq; k++)
                {
                    d->qpos[j.QposAddress + k] = _ownQpos0[cursor++];
                }
                int nv = DofSize(j);
                for (int k = 0; k < nv; k++)
                {
                    d->qvel[j.DofAddress + k] = 0.0;
                }
            }
            for (int i = 0; i < _actuatorIds.Length; i++)
            {
                d->ctrl[_actuatorIds[i]] = 0.0;
                if (_actuators != null && _actuators[i] != null)
                {
                    _actuators[i].Control = 0f;
                }
            }
            MujocoLib.mj_forward(_mjScene.Model, _mjScene.Data);
            _stepCounter = 0;
            DebugResetCount++;
        }

        private void OnDisable()
        {
            // Both hold GPU allocations; leaking them leaks VRAM for the
            // lifetime of the play session.
            _worker?.Dispose();
            _worker = null;
            _input?.Dispose();
            _input = null;
            _ready = false;

            if (_previousFixedDeltaTime > 0f)
            {
                Time.fixedDeltaTime = _previousFixedDeltaTime;
                _previousFixedDeltaTime = -1f;
            }
        }
    }
}
