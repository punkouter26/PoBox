// Runtime inference for the MuJoCo-simulated creature.
//
// NAME: kept as CreatureSentisController because that is what the pipeline
// asks for, but there is no Sentis here. This project ships
// com.unity.ai.inference 2.6.1, whose namespace is Unity.InferenceEngine --
// Sentis' successor. TensorFloat and WorkerFactory.CreateWorker do not exist
// in it; the equivalents are Tensor<float> and `new Worker(model, backend)`.
//
// OBSERVATIONS: the layout below is NOT "root velocity + projected gravity +
// qpos/qvel". It is a term-for-term mirror of the vector the policy was
// actually trained on, which is Agent_FighterBoxing.CollectObservations:
//
//   root   (13)  pelvis height above ground            1
//                pelvis-local linear velocity          3
//                pelvis-local angular velocity / 20    3
//                pelvis up axis (world)                3
//                pelvis forward axis (world)           3
//   joints (98)  body-local rotation quaternion        4  x 14, canonical order
//                body angular velocity / 20            3
//   feet    (8)  grounded flag + contact normal        4  x 2
//   feet    (2)  normalised ground distance            2
//   ------------------------------------------------------
//   total  121
//
// Feeding a different vector of the same width would load, run, and produce
// confident nonsense -- ML-Agents and InferenceEngine both only check SHAPE.
// If you change this, change the training env in lockstep.

using System;
using Mujoco;
using Unity.InferenceEngine;
using UnityEngine;

namespace PoBox.MuJoCoCreature
{
    [DefaultExecutionOrder(-50)] // after MjScene's own stepping order
    public sealed class CreatureSentisController : MonoBehaviour
    {
        private const int ROOT_OBS = 13;
        private const int PER_JOINT_OBS = 7;
        private const int FOOT_OBS = 8;
        private const int FOOT_HEIGHT_OBS = 2;
        private const float ANGULAR_VELOCITY_SCALE = 20f;
        private const float FOOT_RAY_MAX = 1f;

        // Canonical PoBox joint order (RigSegment minus the pelvis root). The
        // action vector is consumed in this order, pitch -> roll -> yaw within
        // each joint, so it must not be reordered once a brain is trained.
        private static readonly string[] JointBodies =
        {
            "Torso", "Head",
            "ThighL", "ShinL", "FootL",
            "ThighR", "ShinR", "FootR",
            "UpperArmL", "ForearmL", "GloveL",
            "UpperArmR", "ForearmR", "GloveR"
        };

        [Header("Assets")]
        [SerializeField] private ModelAsset _onnxModelAsset;
        [SerializeField] private MjScene _mjScene;

        [Header("Bodies")]
        [SerializeField] private MjBody _pelvis;
        [SerializeField] private MjBody _footLeft;
        [SerializeField] private MjBody _footRight;

        [Header("Inference")]
        [SerializeField] private BackendType _backend = BackendType.GPUCompute;
        [Tooltip("Output tensor holding the 30 continuous actions.")]
        [SerializeField] private string _actionOutputName = "continuous_actions";

        [Header("Auto-reset")]
        [Tooltip("Reset when the root free joint's z (qpos[2]) drops below this.")]
        [SerializeField] private float _fallHeight = 0.3f;

        [Header("Timestep")]
        [Tooltip("Physics timestep this scene runs at, in seconds. Applied to " +
                 "Time.fixedDeltaTime for the lifetime of this component and " +
                 "restored afterwards. 0 disables the override.")]
        [SerializeField] private float _fixedTimestepOverride = 0.005f;

        private float _previousFixedDeltaTime = -1f;

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

        private double[] _initialQpos;
        private int _numActuators;
        private MjActuator[] _ownActuators;
        private MjBaseJoint[] _ownJoints;
        private double[] _ownQpos0;
        private float _footContactHeight = 0.0525f;
        private bool _ready;

        public int ObservationCount =>
            ROOT_OBS + PER_JOINT_OBS * JointBodies.Length + FOOT_OBS + FOOT_HEIGHT_OBS;

        // Safe read-only telemetry. MjData is behind an unsafe pointer, so
        // nothing outside this class (an inspector, a test, the MCP eval
        // sandbox which forbids unsafe) can observe the sim without these.
        public bool IsBound => _ready;

        /// <summary>
        /// Forces a bind + observation gather and returns the raw 121-vector.
        /// Exists for the parity harness: the ONLY way to know the C# and Python
        /// observation builders agree is to feed both the same state and diff
        /// them element-wise. A shape match proves nothing -- a wrong term is
        /// the same width as a right one.
        /// </summary>
        public unsafe float[] DebugGatherObservations()
        {
            if (!_ready && !TryBind())
            {
                return null;
            }
            GatherObservations(_mjScene.Data);
            return (float[])_observations.Clone();
        }

        /// <summary>Overwrite qpos/qvel, then recompute derived state.</summary>
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
        public float DebugPelvisHeight { get; private set; }
        public float DebugMaxAbsAction { get; private set; }
        public float DebugSumAbsQvel { get; private set; }
        public double DebugSimTime { get; private set; }
        public int DebugResetCount { get; private set; }
        public int DebugStepCount { get; private set; }

        private void Awake()
        {
            // MjScene does NOT honour the MJCF's own timestep. When it
            // regenerates the model at play time, MjcfGenerationContext writes
            //     optionMjcf.SetAttribute("timestep", $"{Time.fixedDeltaTime}")
            // so MuJoCo runs at Unity's rate, whatever the XML said. This
            // policy was trained at 0.005 s; left at PoBox's project-wide 0.02
            // the position servos integrate 4x too coarsely and the creature
            // settles into a crouch at ~0.44 m instead of standing at 0.93 m.
            //
            // Set here rather than in ProjectSettings/TimeManager.asset on
            // purpose: 0.02 is a locked project invariant that the balance and
            // walk contest brains were trained against, and changing it
            // globally would silently alter those shipping scenes. This
            // override is scoped to this component's lifetime and restored on
            // disable. Awake runs before MjScene's own setup (execution order
            // -50 vs its default 0), which is what makes it take effect.
            if (_fixedTimestepOverride > 0f)
            {
                _previousFixedDeltaTime = Time.fixedDeltaTime;
                Time.fixedDeltaTime = _fixedTimestepOverride;
            }
        }

        private void OnEnable()
        {
            // Deliberately does NOT touch MjScene.Instance. That property
            // throws if its static _instance is still null while an MjScene
            // exists in the scene, and _instance is only assigned in
            // MjScene.Awake() -- so any OnEnable that races ahead of that Awake
            // brings the whole scene down. Resolution is deferred to the first
            // FixedUpdate, by which point every Awake has run. Binding is
            // deferred anyway: MjScene builds the model in its own startup and
            // Mujoco ids are meaningless before that.
            _ready = false;
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

            if (_onnxModelAsset == null)
            {
                Debug.LogError($"{nameof(CreatureSentisController)}: no ONNX model assigned.", this);
                enabled = false;
                return false;
            }

            _runtimeModel = ModelLoader.Load(_onnxModelAsset);
            _worker = new Worker(_runtimeModel, _backend);

            _observations = new float[ObservationCount];
            _input = new Tensor<float>(new TensorShape(1, ObservationCount));

            // OWN actuators only, sorted by model id (= MJCF/training order).
            // MjScene is a singleton: in a scene shared with another creature,
            // Model->nu counts BOTH rigs, and writing ctrl[0..nu) would zero
            // the other creature's targets every tick. Scoped since the
            // raptor integration; identical behaviour when alone.
            _ownActuators = GetComponentsInChildren<MjActuator>();
            Array.Sort(_ownActuators, (a, b) => a.MujocoId.CompareTo(b.MujocoId));
            _numActuators = _ownActuators.Length;
            _actions = new float[_numActuators];

            // Own joints + their qpos0 slices, for a reset that touches
            // nothing belonging to another creature in the shared model.
            _ownJoints = GetComponentsInChildren<MjBaseJoint>();
            var qpos0List = new System.Collections.Generic.List<double>();
            foreach (var j in _ownJoints)
            {
                int n = QposSize(j);
                for (int k = 0; k < n; k++)
                {
                    qpos0List.Add(_mjScene.Model->qpos0[j.QposAddress + k]);
                }
            }
            _ownQpos0 = qpos0List.ToArray();

            _pelvisId = _pelvis != null ? _pelvis.MujocoId : -1;
            _footLeftId = _footLeft != null ? _footLeft.MujocoId : -1;
            _footRightId = _footRight != null ? _footRight.MujocoId : -1;
            if (_pelvisId < 0 || _footLeftId < 0 || _footRightId < 0)
            {
                Debug.LogError($"{nameof(CreatureSentisController)}: Pelvis/FootL/FootR bodies are not assigned.", this);
                enabled = false;
                return false;
            }

            _jointBodyIds = new int[JointBodies.Length];
            _jointParentIds = new int[JointBodies.Length];
            foreach (var body in FindObjectsByType<MjBody>(FindObjectsSortMode.None))
            {
                int index = Array.IndexOf(JointBodies, body.name);
                if (index >= 0)
                {
                    _jointBodyIds[index] = body.MujocoId;
                }
            }
            for (int i = 0; i < _jointBodyIds.Length; i++)
            {
                if (_jointBodyIds[i] == 0)
                {
                    Debug.LogError($"{nameof(CreatureSentisController)}: no MjBody named '{JointBodies[i]}'. " +
                                   "The observation layout depends on all 14 being present.", this);
                    enabled = false;
                    return false;
                }
                _jointParentIds[i] = _mjScene.Model->body_parentid[_jointBodyIds[i]];
            }

            // Reference pose for auto-reset, taken from the MODEL's qpos0, not
            // from live Data->qpos. Binding happens on the first FixedUpdate,
            // by which point the model has already settled under gravity --
            // snapshotting there recorded a pelvis 10 cm below the true rest
            // height, so every reset started the creature in a half-crouch it
            // was never trained from.
            int nq = (int)_mjScene.Model->nq;
            _initialQpos = new double[nq];
            for (int i = 0; i < nq; i++)
            {
                _initialQpos[i] = _mjScene.Model->qpos0[i];
            }

            int expectedActions = _numActuators;
            Debug.Log($"{nameof(CreatureSentisController)}: bound. obs={ObservationCount} " +
                      $"actuators={expectedActions} backend={_backend}");
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

            // Height read from the pelvis BODY, not qpos[2]: in a shared
            // MjScene qpos[2] belongs to whichever creature compiled first.
            if (d->xpos[3 * _pelvisId + 2] < _fallHeight)
            {
                ResetCreature();
                return;
            }

            GatherObservations(d);

            _input.Upload(_observations);
            _worker.Schedule(_input);

            // PeekOutput returns a tensor the worker owns -- never dispose it.
            // ReadbackAndClone gives a CPU copy we DO own, hence the using.
            Tensor peeked = string.IsNullOrEmpty(_actionOutputName)
                ? _worker.PeekOutput()
                : _worker.PeekOutput(_actionOutputName);
            using (var readable = (peeked as Tensor<float>)?.ReadbackAndClone())
            {
                if (readable == null)
                {
                    Debug.LogError($"{nameof(CreatureSentisController)}: output '{_actionOutputName}' is not a float tensor.", this);
                    enabled = false;
                    return;
                }
                readable.DownloadToArray().CopyTo(_actions, 0);
            }

            DebugStepCount++;
            DebugSimTime = d->time;
            DebugPelvisHeight = (float)d->xpos[3 * _pelvisId + 2];
            float sumQvel = 0f;
            for (int i = 0; i < (int)_mjScene.Model->nv; i++)
            {
                sumQvel += Mathf.Abs((float)d->qvel[i]);
            }
            DebugSumAbsQvel = sumQvel;
            float maxAction = 0f;
            for (int i = 0; i < _actions.Length; i++)
            {
                maxAction = Mathf.Max(maxAction, Mathf.Abs(_actions[i]));
            }
            DebugMaxAbsAction = maxAction;

            int count = Mathf.Min(_actions.Length, _numActuators);
            for (int i = 0; i < count; i++)
            {
                // The policy emits [-1,1]; ctrl is a TARGET ANGLE in radians,
                // mapped zero-centred exactly as Systems_FighterRig does:
                //   action >= 0 ? action * high : -action * low
                // Written through the component's Control field, indexed by
                // the actuator's OWN model id -- MjScene.SyncUnityToMjState
                // copies Control into d->ctrl after every step, and in a
                // shared scene actuator i of the model is not necessarily
                // actuator i of this creature.
                int id = _ownActuators[i].MujocoId;
                double low = _mjScene.Model->actuator_ctrlrange[2 * id];
                double high = _mjScene.Model->actuator_ctrlrange[2 * id + 1];
                float a = Mathf.Clamp(_actions[i], -1f, 1f);
                _ownActuators[i].Control = (float)(a >= 0f ? a * high : -a * low);
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
            Vector3 fwd = pelvisRot * new Vector3(0f, -1f, 0f);
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

            // --- foot contact (8) ---
            // The training floor is a single plane, so the contact normal is
            // +Z whenever a foot is down. Wrong the moment terrain is added.
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

        public unsafe void ResetCreature()
        {
            if (_mjScene == null || _mjScene.Data == null || _initialQpos == null)
            {
                return;
            }
            // Scoped to THIS creature's joints and actuators: a shared
            // MjScene holds every creature's state, and the old whole-model
            // restore teleported the other creature to qpos0 on every fall.
            MujocoLib.mjData_* d = _mjScene.Data;
            int cursor = 0;
            foreach (var j in _ownJoints)
            {
                int nqj = QposSize(j);
                for (int k = 0; k < nqj; k++)
                {
                    d->qpos[j.QposAddress + k] = _ownQpos0[cursor++];
                }
                int nvj = DofSize(j);
                for (int k = 0; k < nvj; k++)
                {
                    d->qvel[j.DofAddress + k] = 0.0;
                }
            }
            for (int i = 0; i < _numActuators; i++)
            {
                d->ctrl[_ownActuators[i].MujocoId] = 0.0;
                _ownActuators[i].Control = 0f;
            }
            MujocoLib.mj_forward(_mjScene.Model, _mjScene.Data);
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
