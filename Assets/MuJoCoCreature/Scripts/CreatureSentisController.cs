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
//   command (6)  commanded speed                       1   only when
//                pelvis-local commanded direction      3   _observeLocomotionCommand
//                gait clock sin, cos                   2   -> 127
//
// The optional 6-term block is Agent_FighterBoxing's _observeLocomotionCommand,
// term for term, and it is what turns a balance brain into a locomotion brain:
// the same network is told 0 m/s in the balance ring and ~1 m/s in the walk
// race. Tools/MuJoCo/nick_env.py builds the identical vector in Python.
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
        private const int LOCOMOTION_COMMAND_OBS = 6;
        private const float ANGULAR_VELOCITY_SCALE = 20f;
        private const float FOOT_RAY_MAX = 1f;
        // Agent_FighterBoxing.GAIT_CLOCK_FREQUENCY: the same clock the
        // ML-Agents locomotion line hands its policy, so the contract is shared.
        private const float GAIT_CLOCK_FREQUENCY = 1.4f;
        private const int HEAD_INDEX = 1;

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

        [Header("Locomotion command")]
        [Tooltip("Append the 6-term locomotion command (speed, pelvis-local " +
                 "direction, gait clock) to the observation vector, exactly as " +
                 "Agent_FighterBoxing does under _observeLocomotionCommand. The " +
                 "brain must have been trained with it: 127 observations, not 121.")]
        [SerializeField] private bool _observeLocomotionCommand = false;
        [Tooltip("Run the policy every N physics steps and hold its targets in " +
                 "between. The MuJoCo Warp locomotion brain was trained at 4 " +
                 "(50 Hz control on a 0.005 s step); the 2026-08-31 balance brain at 1.")]
        [SerializeField, Min(1)] private int _decimation = 1;
        [Tooltip("Commanded forward speed in m/s. 0 is the balance ring, ~1 the walk race.")]
        [SerializeField] private float _commandedSpeed = 0f;

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
        // Legacy contract (121-observation balance brain): a foot is "down"
        // when its ANKLE body is below 5.25 cm, which never fires while
        // standing -- the ankle rests 15 cm up. Kept verbatim for that brain.
        private const float LEGACY_FOOT_CONTACT_HEIGHT = 0.0525f;
        // Locomotion contract: down when the ankle is within this of its rest
        // height. Tools/MuJoCo/nick_env.py FOOT_CONTACT_ABOVE_REST.
        private const float FOOT_CONTACT_ABOVE_REST = 0.03f;
        private float _footLeftContactZ = LEGACY_FOOT_CONTACT_HEIGHT;
        private float _footRightContactZ = LEGACY_FOOT_CONTACT_HEIGHT;
        private float _footLeftRestZ;
        private float _footRightRestZ;
        private bool _ready;

        private int _physicsSteps;     // since reset; drives the decimation
        private int _policySteps;      // since reset; drives the gait clock
        private int _shoveStepsLeft;
        private Vector3 _shoveForce;

        public int ObservationCount =>
            ROOT_OBS + PER_JOINT_OBS * JointBodies.Length + FOOT_OBS + FOOT_HEIGHT_OBS +
            (_observeLocomotionCommand ? LOCOMOTION_COMMAND_OBS : 0);

        // Safe read-only telemetry. MjData is behind an unsafe pointer, so
        // nothing outside this class (an inspector, a test, the MCP eval
        // sandbox which forbids unsafe) can observe the sim without these.
        public bool IsBound => _ready;
        /// <summary>The pelvis MjBody's transform, in Unity space, for cameras and the like.</summary>
        public Transform PelvisTransform => _pelvis != null ? _pelvis.transform : null;
        public bool ObservesLocomotionCommand => _observeLocomotionCommand;
        public int Decimation => Mathf.Max(1, _decimation);
        /// <summary>Seconds between policy decisions: the physics step times the decimation.</summary>
        public float ControlDeltaTime => Time.fixedDeltaTime * Decimation;

        /// <summary>Commanded forward speed, m/s. Observed by the brain only when it was trained to.</summary>
        public float CommandedSpeed
        {
            get => _commandedSpeed;
            set => _commandedSpeed = value;
        }

        /// <summary>
        /// Commanded travel direction as a unit vector in MuJoCo world
        /// coordinates (Z up), horizontal. Defaults to wherever the pelvis
        /// faced at the last reset, so "walk" means "walk forward".
        /// </summary>
        public Vector3 CommandedDirection { get; private set; } = new Vector3(0f, -1f, 0f);

        public void SetCommand(float speed)
        {
            _commandedSpeed = speed;
        }

        public void SetCommand(float speed, Vector3 directionMjWorld)
        {
            _commandedSpeed = speed;
            var flat = new Vector3(directionMjWorld.x, directionMjWorld.y, 0f);
            if (flat.sqrMagnitude > 1e-6f)
            {
                CommandedDirection = flat.normalized;
            }
        }

        /// <summary>Points the command along the pelvis's current heading.</summary>
        public void FaceCurrentHeading()
        {
            var flat = new Vector3(DebugPelvisForward.x, DebugPelvisForward.y, 0f);
            if (flat.sqrMagnitude > 1e-6f)
            {
                CommandedDirection = flat.normalized;
            }
        }

        /// <summary>
        /// Applies a horizontal force to the pelvis for a short time -- the
        /// balance ring's shove, in MuJoCo terms (xfrc_applied). Force is in
        /// MuJoCo world coordinates, newtons.
        /// </summary>
        public void Shove(Vector3 forceMjWorld, float seconds)
        {
            _shoveForce = forceMjWorld;
            _shoveStepsLeft = Mathf.Max(1, Mathf.RoundToInt(seconds / Mathf.Max(1e-4f, Time.fixedDeltaTime)));
        }

        /// <summary>
        /// Forces a bind + observation gather and returns the raw observation
        /// vector. Exists for the parity harness: the ONLY way to know the C#
        /// and Python observation builders agree is to feed both the same
        /// state and diff them element-wise. A shape match proves nothing --
        /// a wrong term is the same width as a right one.
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
        // MuJoCo world frame (Z up), refreshed every physics step once bound.
        public Vector3 DebugPelvisPosition { get; private set; }
        public Vector3 DebugPelvisForward { get; private set; } = new Vector3(0f, -1f, 0f);
        public Vector3 DebugPelvisLinVel { get; private set; }
        public float DebugHeadZ { get; private set; }
        public float DebugFootLeftZ { get; private set; }
        public float DebugFootRightZ { get; private set; }
        public float DebugFootLeftRestZ => _footLeftRestZ;
        public float DebugFootRightRestZ => _footRightRestZ;
        public bool DebugFootLeftDown { get; private set; }
        public bool DebugFootRightDown { get; private set; }

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

            // Shape check against the graph. InferenceEngine will happily run
            // a 121-wide vector through a 127-input model and return garbage;
            // this is the only place the mismatch can be caught.
            foreach (var input in _runtimeModel.inputs)
            {
                var shape = input.shape;
                // Get(1) is -1 for a dynamic dimension, which is skipped.
                int width = shape.rank == 2 ? shape.Get(1) : -1;
                if (width > 0 && width != ObservationCount)
                {
                    Debug.LogError($"{nameof(CreatureSentisController)}: model input '{input.name}' is " +
                                   $"{width} wide but this controller builds {ObservationCount} " +
                                   $"observations (observeLocomotionCommand={_observeLocomotionCommand}). " +
                                   "Refusing to drive the creature with a brain it cannot read.", this);
                    enabled = false;
                    return false;
                }
            }

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
            foreach (var body in GetComponentsInChildren<MjBody>())
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

            // Rest heights of the ankle bodies from the model's own qpos0, for
            // the rest-relative contact test and the demo's clearance column.
            {
                var probe = new double[nq];
                for (int i = 0; i < nq; i++) { probe[i] = _mjScene.Data->qpos[i]; }
                for (int i = 0; i < nq; i++) { _mjScene.Data->qpos[i] = _initialQpos[i]; }
                MujocoLib.mj_kinematics(_mjScene.Model, _mjScene.Data);
                _footLeftRestZ = (float)_mjScene.Data->xpos[3 * _footLeftId + 2];
                _footRightRestZ = (float)_mjScene.Data->xpos[3 * _footRightId + 2];
                for (int i = 0; i < nq; i++) { _mjScene.Data->qpos[i] = probe[i]; }
                MujocoLib.mj_forward(_mjScene.Model, _mjScene.Data);
            }
            if (_observeLocomotionCommand)
            {
                _footLeftContactZ = _footLeftRestZ + FOOT_CONTACT_ABOVE_REST;
                _footRightContactZ = _footRightRestZ + FOOT_CONTACT_ABOVE_REST;
            }

            int expectedActions = _numActuators;
            Debug.Log($"{nameof(CreatureSentisController)}: bound. obs={ObservationCount} " +
                      $"actuators={expectedActions} decimation={Decimation} backend={_backend}");
            _ready = true;

            // The command's default heading is whichever way the creature
            // faces at rest; sample it now so "walk" has a direction before
            // anyone calls SetCommand.
            RefreshTelemetry(_mjScene.Data);
            FaceCurrentHeading();
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

            // Shove: an external force on the pelvis, held for a few steps.
            // xfrc_applied persists until overwritten, so it is cleared
            // explicitly the step after the shove ends.
            if (_shoveStepsLeft > 0)
            {
                d->xfrc_applied[6 * _pelvisId + 0] = _shoveForce.x;
                d->xfrc_applied[6 * _pelvisId + 1] = _shoveForce.y;
                d->xfrc_applied[6 * _pelvisId + 2] = _shoveForce.z;
                _shoveStepsLeft--;
                if (_shoveStepsLeft == 0) { _shoveStepsLeft = -1; }
            }
            else if (_shoveStepsLeft < 0)
            {
                for (int k = 0; k < 6; k++) { d->xfrc_applied[6 * _pelvisId + k] = 0.0; }
                _shoveStepsLeft = 0;
            }

            // Telemetry is refreshed every physics step; the policy only runs
            // on every Decimation-th one and its targets are held in between,
            // which is exactly the zero-order hold the training env applies.
            RefreshTelemetry(d);
            bool decide = (_physicsSteps % Decimation) == 0;
            _physicsSteps++;
            if (!decide)
            {
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
            _policySteps++;
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

        private unsafe void RefreshTelemetry(MujocoLib.mjData_* d)
        {
            DebugPelvisPosition = BodyPos(d, _pelvisId);
            Quaternion pelvisRot = BodyQuat(d, _pelvisId);
            DebugPelvisForward = pelvisRot * new Vector3(0f, -1f, 0f);
            DebugPelvisLinVel = BodyLinVel(d, _pelvisId);
            DebugHeadZ = BodyPos(d, _jointBodyIds[HEAD_INDEX]).z;
            DebugFootLeftZ = BodyPos(d, _footLeftId).z;
            DebugFootRightZ = BodyPos(d, _footRightId).z;
            // Telemetry is always rest-relative: the demo counts stance
            // changes with it, whichever brain is loaded.
            DebugFootLeftDown = DebugFootLeftZ < _footLeftRestZ + FOOT_CONTACT_ABOVE_REST;
            DebugFootRightDown = DebugFootRightZ < _footRightRestZ + FOOT_CONTACT_ABOVE_REST;
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
            float lGround = lz < _footLeftContactZ ? 1f : 0f;
            float rGround = rz < _footRightContactZ ? 1f : 0f;
            _observations[c++] = lGround;
            _observations[c++] = 0f; _observations[c++] = 0f; _observations[c++] = lGround;
            _observations[c++] = rGround;
            _observations[c++] = 0f; _observations[c++] = 0f; _observations[c++] = rGround;

            // --- foot height (2) ---
            _observations[c++] = Mathf.Clamp01(lz / FOOT_RAY_MAX);
            _observations[c++] = Mathf.Clamp01(rz / FOOT_RAY_MAX);

            // --- locomotion command (6), optional ---
            if (_observeLocomotionCommand)
            {
                _observations[c++] = _commandedSpeed;
                Vector3 localDir = inv * CommandedDirection;
                _observations[c++] = localDir.x; _observations[c++] = localDir.y; _observations[c++] = localDir.z;
                // Gait clock, counted in POLICY steps since reset so it is
                // deterministic and independent of the decimation.
                float phase = GAIT_CLOCK_FREQUENCY * 2f * Mathf.PI * _policySteps * ControlDeltaTime;
                _observations[c++] = Mathf.Sin(phase);
                _observations[c++] = Mathf.Cos(phase);
            }
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
            for (int k = 0; k < 6; k++) { d->xfrc_applied[6 * _pelvisId + k] = 0.0; }
            _shoveStepsLeft = 0;
            MujocoLib.mj_forward(_mjScene.Model, _mjScene.Data);
            _physicsSteps = 0;
            _policySteps = 0;
            RefreshTelemetry(d);
            FaceCurrentHeading();
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
