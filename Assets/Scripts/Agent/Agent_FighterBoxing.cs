using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Active-ragdoll boxing agent. Observations and action count are derived
    /// from the rig — never hand-typed (see ComputeObservationCount).
    /// Actions are buffered in OnActionReceived and applied in FixedUpdate
    /// only (project rule). Rewards are added externally by Reward_ components
    /// in the training phase.
    /// </summary>
    [DefaultExecutionOrder(-100)] // deterministically before the ML-Agents Academy stepper
    public sealed class Agent_FighterBoxing : Agent
    {
        private const float ANGULAR_VELOCITY_SCALE = 20f;
        private const int ROOT_OBSERVATIONS = 13;
        private const int PER_JOINT_OBSERVATIONS = 7;
        private const int FOOT_OBSERVATIONS = 8;
        private const int FOOT_HEIGHT_OBSERVATIONS = 2;
        private const int OPPONENT_OBSERVATIONS = 19;
        // Commanded speed (1) + goal direction in pelvis-local space (3)
        // + gait clock as sin/cos (2).
        private const int LOCOMOTION_OBSERVATIONS = 6;
        // Cycles per second of the gait clock handed to the policy. Matches the
        // scripted bot's stride rate so both move at a plausible human cadence.
        private const float GAIT_CLOCK_FREQUENCY = 1.4f;
        private const float FOOT_RAY_MAX_METERS = 1f;

        // Heuristic balance bot (project rule: every app has one code-driven
        // heuristic bot). Ankle strategy + counter hip strategy, PD on the
        // horizontal center-of-mass offset over the feet. DOF roles are
        // precomputed in Initialize — zero per-step allocations.
        private const float HEURISTIC_MAX_ACTION = 0.6f;
        private const byte DOF_ROLE_NONE = 0;
        private const byte DOF_ROLE_ANKLE_PITCH = 1;
        private const byte DOF_ROLE_ANKLE_ROLL = 2;
        private const byte DOF_ROLE_HIP_PITCH = 3;
        private const byte DOF_ROLE_HIP_ROLL = 4;
        private const byte DOF_ROLE_KNEE_PITCH = 5;
        // Which leg a DOF belongs to. The balance bot never needed this, but a
        // gait is defined by the two legs being in antiphase, so the bot has to
        // tell them apart. Derived from pelvis-local X, not from bone names, so
        // it also works on the imported rigs whose bones are not named L/R.
        private const byte DOF_SIDE_CENTER = 0;
        private const byte DOF_SIDE_LEFT = 1;
        private const byte DOF_SIDE_RIGHT = 2;

        // Scripted gait, tuned for the capsule rig at ~1 m/s.
        private const float GAIT_STEP_FREQUENCY = 1.4f;   // strides per second
        private const float GAIT_HIP_SWING = 0.55f;       // hip pitch amplitude, action units
        private const float GAIT_KNEE_FLEX = 0.45f;       // knee bend during swing only
        private const float GAIT_ANKLE_PUSH = 0.25f;      // ankle push-off at end of stance
        private const float GAIT_LEAN_BIAS = 0.12f;       // forward lean that converts stepping into travel

        [SerializeField] private Systems_FighterRig _rig;
        [SerializeField] private Systems_Stamina _stamina;
        [SerializeField] private Sensor_GroundContact _footLeft;
        [SerializeField] private Sensor_GroundContact _footRight;
        [SerializeField] private Systems_FighterRig _opponentRig;
        [SerializeField] private Systems_Stamina _opponentStamina;
        [SerializeField] private Transform _ringCenter;
        // False in balance-phase prefabs/scenes: drops the 19 opponent-relative
        // observations that are always zero without an opponent. Flip to true
        // (and re-run Prepare for Training) for the boxing phase.
        [SerializeField] private bool _observeOpponent = true;
        // Adds 2 foot-to-ground raycast distances (WalkerAgent standing-phase
        // trick). CHANGES THE OBSERVATION SIZE: enabling it invalidates every
        // trained .onnx and requires re-running Prepare for Training plus a
        // fresh run. Leave false unless starting a new model line.
        [SerializeField] private bool _observeFootHeight;
        // Adds the commanded walking speed and the goal direction, turning one
        // brain into both mini-games: command 0 m/s and it stands (balance
        // contest), command 1 m/s and it walks (walk race). Randomized during
        // training so the brain learns to obey the command rather than
        // memorizing one behaviour. CHANGES THE OBSERVATION SIZE: enabling it
        // invalidates every trained .onnx and needs a fresh model line.
        [SerializeField] private bool _observeLocomotionCommand;
        // Negative by calibration (2026-08-17 contest-scene A/B): with
        // _invertTargetRotation fixed, negative gains stabilize the capsule
        // and Grandma rigs; positive gains actively topple them. Grandpa's
        // bone axes differ — tune per-rig if his bot underperforms.
        [SerializeField] private float _heuristicKp = -4f;
        [SerializeField] private float _heuristicKd = -1.2f;
        // Whether a positive hip-pitch action swings the leg forward. The rigs
        // disagree on joint axis sign (see _heuristicKp above), and the gait is
        // useless backwards, so this is exposed to be flipped per rig without a
        // recompile rather than guessed in code.
        [SerializeField] private float _gaitPitchSign = 1f;

        private float[] _pendingActions;
        private bool _hasPendingActions;
        private Sensor_GroundContact[] _contactSensors;
        private byte[] _dofRoles;
        private byte[] _dofSides;
        private float _gaitPhase;
        private float _lastActionDelta01;

        public Systems_FighterRig Rig => _rig;

        /// <summary>Mean |Δaction| of the latest decision, 0..1. Read by Reward_Balance's smoothness penalty.</summary>
        public float LastActionDelta01 => _lastActionDelta01;

        /// <summary>Deploy-time switch used by the contest spawner when brains of the new observation layout land.</summary>
        public void SetObserveFootHeight(bool observeFootHeight)
        {
            _observeFootHeight = observeFootHeight;
        }

        /// <summary>Speed the brain is being told to travel at, m/s. 0 stands still.</summary>
        public float CommandedSpeed { get; private set; }

        /// <summary>Unit world direction the brain is being told to travel in.</summary>
        public Vector3 CommandedDirection { get; private set; } = Vector3.forward;

        /// <summary>
        /// Sets this step's locomotion command. Written by Reward_Locomotion,
        /// which owns the curriculum and re-rolls the command each episode.
        /// </summary>
        public void SetLocomotionCommand(float speed, Vector3 direction)
        {
            CommandedSpeed = speed;
            CommandedDirection = direction.sqrMagnitude > 0f ? direction.normalized : Vector3.forward;
        }

        // Called by the editor scene builder when starting the locomotion model line.
        public void SetObserveLocomotionCommand(bool observeLocomotionCommand)
        {
            _observeLocomotionCommand = observeLocomotionCommand;
        }

        public static int ComputeObservationCount(int jointCount, bool observeOpponent, bool observeFootHeight,
            bool observeLocomotionCommand = false)
        {
            int count = ROOT_OBSERVATIONS + PER_JOINT_OBSERVATIONS * jointCount + FOOT_OBSERVATIONS;
            if (observeFootHeight)
            {
                count += FOOT_HEIGHT_OBSERVATIONS;
            }
            if (observeLocomotionCommand)
            {
                count += LOCOMOTION_OBSERVATIONS;
            }
            return observeOpponent ? count + OPPONENT_OBSERVATIONS : count;
        }

        // Called by the editor rig tool while building the prefab.
        public void EditorInitialize(Systems_FighterRig rig, Systems_Stamina stamina, Sensor_GroundContact footLeft, Sensor_GroundContact footRight)
        {
            _rig = rig;
            _stamina = stamina;
            _footLeft = footLeft;
            _footRight = footRight;
        }

        public void SetOpponent(Systems_FighterRig opponentRig, Systems_Stamina opponentStamina)
        {
            _opponentRig = opponentRig;
            _opponentStamina = opponentStamina;
        }

        public override void Initialize()
        {
            _pendingActions = new float[_rig.DofCount];
            _contactSensors = GetComponentsInChildren<Sensor_GroundContact>(true);
            BuildDofRoles();
        }

        private void BuildDofRoles()
        {
            _dofRoles = new byte[_rig.DofCount];
            _dofSides = new byte[_rig.DofCount];
            var joints = _rig.Joints;
            Transform pelvisTransform = _rig.Pelvis.transform;
            int dofIndex = 0;
            for (int jointIndex = 0; jointIndex < joints.Count; jointIndex++)
            {
                RigJointEntry entry = joints[jointIndex];
                string bodyName = entry.body.name;
                bool isAnkle = bodyName.IndexOf("foot", System.StringComparison.OrdinalIgnoreCase) >= 0;
                bool isHip = bodyName.IndexOf("thigh", System.StringComparison.OrdinalIgnoreCase) >= 0;
                bool isKnee = bodyName.IndexOf("shin", System.StringComparison.OrdinalIgnoreCase) >= 0;

                // Rest pose is symmetric, so local X sign separates the legs.
                // The dead zone keeps spine and arm chains out of the gait.
                float localX = pelvisTransform.InverseTransformPoint(entry.body.transform.position).x;
                byte side = Mathf.Abs(localX) < 0.02f
                    ? DOF_SIDE_CENTER
                    : localX < 0f ? DOF_SIDE_LEFT : DOF_SIDE_RIGHT;

                if (entry.hasPitch)
                {
                    _dofRoles[dofIndex] = isAnkle ? DOF_ROLE_ANKLE_PITCH
                        : isHip ? DOF_ROLE_HIP_PITCH
                        : isKnee ? DOF_ROLE_KNEE_PITCH
                        : DOF_ROLE_NONE;
                    _dofSides[dofIndex] = side;
                    dofIndex++;
                }
                if (entry.hasRoll)
                {
                    _dofRoles[dofIndex] = isAnkle ? DOF_ROLE_ANKLE_ROLL : isHip ? DOF_ROLE_HIP_ROLL : DOF_ROLE_NONE;
                    _dofSides[dofIndex] = side;
                    dofIndex++;
                }
                if (entry.hasYaw)
                {
                    _dofRoles[dofIndex] = DOF_ROLE_NONE;
                    _dofSides[dofIndex] = side;
                    dofIndex++;
                }
            }
        }

        public override void OnEpisodeBegin()
        {
            // Rule: clear fatigue BEFORE restoring motors.
            if (_stamina != null)
            {
                _stamina.ResetFatigue();
            }
            _rig.ResetToStartPose();
            for (int sensorIndex = 0; sensorIndex < _contactSensors.Length; sensorIndex++)
            {
                _contactSensors[sensorIndex].ResetContacts();
            }
            _hasPendingActions = false;
        }

        public override void CollectObservations(VectorSensor sensor)
        {
            Rigidbody pelvis = _rig.Pelvis;
            Transform pelvisTransform = pelvis.transform;

            // Root state (13)
            // Height ABOVE THE FLOOR, not absolute world Y. Absolute Y was an
            // altitude lock: every brain trained on a y = 0 ground read "hips at
            // 0.9 = standing", so raising the floor to a 1 m ring canvas made
            // every fighter believe it was airborne. Ground-relative height is
            // the same number at any altitude.
            sensor.AddObservation(pelvisTransform.position.y - _rig.GroundY);
            sensor.AddObservation(pelvisTransform.InverseTransformDirection(pelvis.linearVelocity));
            sensor.AddObservation(pelvisTransform.InverseTransformDirection(pelvis.angularVelocity) / ANGULAR_VELOCITY_SCALE);
            sensor.AddObservation(pelvisTransform.up);
            sensor.AddObservation(pelvisTransform.forward);

            // Proprioception (7 per joint)
            var joints = _rig.Joints;
            for (int jointIndex = 0; jointIndex < joints.Count; jointIndex++)
            {
                RigJointEntry entry = joints[jointIndex];
                sensor.AddObservation(entry.body.transform.localRotation);
                sensor.AddObservation(entry.body.angularVelocity / ANGULAR_VELOCITY_SCALE);
            }

            // Foot contact (8). The foot sensors are wired at rig-build time
            // and are never absent — no per-frame null checks in the hot path.
            sensor.AddObservation(_footLeft.IsGrounded);
            sensor.AddObservation(_footLeft.ContactNormal);
            sensor.AddObservation(_footRight.IsGrounded);
            sensor.AddObservation(_footRight.ContactNormal);

            if (_observeFootHeight)
            {
                sensor.AddObservation(FootGroundDistance01(_footLeft));
                sensor.AddObservation(FootGroundDistance01(_footRight));
            }

            if (_observeLocomotionCommand)
            {
                // Direction is pelvis-local so the command means the same thing
                // whichever way the fighter happens to be facing — that is what
                // makes the brain steerable instead of locked to one world axis.
                sensor.AddObservation(CommandedSpeed);
                sensor.AddObservation(pelvisTransform.InverseTransformDirection(CommandedDirection));

                // Gait clock. The policy is feed-forward with no memory, so it
                // has no way to invent a rhythm, and walking is periodic by
                // definition. Handing it a phase gives left/right alternation
                // something to key off. Measured 2026-08-19 without it: single
                // support 0.005 and foot lift 0.09 mm at every reward weighting
                // tried across three generations — an exploration problem, not
                // an incentive one. Derived from StepCount so it is
                // deterministic and restarts with each episode.
                float phase = GAIT_CLOCK_FREQUENCY * 2f * Mathf.PI * StepCount * Time.fixedDeltaTime;
                sensor.AddObservation(Mathf.Sin(phase));
                sensor.AddObservation(Mathf.Cos(phase));
            }

            // Opponent-relative (19); omitted entirely in balance-phase rigs.
            // Boxing scenes must wire an opponent before enabling the flag —
            // the old zero-padding fallback hid mis-wired scenes.
            if (!_observeOpponent)
            {
                return;
            }
            Rigidbody opponentPelvis = _opponentRig.Pelvis;
            sensor.AddObservation(pelvisTransform.InverseTransformPoint(opponentPelvis.transform.position));
            sensor.AddObservation(pelvisTransform.InverseTransformDirection(opponentPelvis.linearVelocity - pelvis.linearVelocity));
            sensor.AddObservation(pelvisTransform.InverseTransformPoint(_opponentRig.Head.position));
            sensor.AddObservation(pelvisTransform.InverseTransformPoint(_opponentRig.GloveLeft.position));
            sensor.AddObservation(pelvisTransform.InverseTransformPoint(_opponentRig.GloveRight.position));
            sensor.AddObservation(_opponentStamina != null ? _opponentStamina.Anaerobic01 : 1f);
            sensor.AddObservation(pelvisTransform.InverseTransformPoint(_ringCenter.position));
        }

        private static float FootGroundDistance01(Sensor_GroundContact foot)
        {
            if (foot == null)
            {
                return 1f;
            }
            return Physics.Raycast(foot.transform.position, Vector3.down, out RaycastHit hit, FOOT_RAY_MAX_METERS)
                ? hit.distance / FOOT_RAY_MAX_METERS
                : 1f;
        }

        public override void Heuristic(in ActionBuffers actionsOut)
        {
            var continuous = actionsOut.ContinuousActions;
            for (int actionIndex = 0; actionIndex < continuous.Length; actionIndex++)
            {
                continuous[actionIndex] = 0f;
            }
            if (_dofRoles == null)
            {
                return;
            }

            Rigidbody pelvis = _rig.Pelvis;
            Vector3 support = (_footLeft.transform.position + _footRight.transform.position) * 0.5f;
            Vector3 lean = pelvis.worldCenterOfMass - support;
            Vector3 velocity = pelvis.linearVelocity;
            Transform pelvisTransform = pelvis.transform;
            Vector3 localLean = pelvisTransform.InverseTransformDirection(new Vector3(lean.x, 0f, lean.z));
            Vector3 localVelocity = pelvisTransform.InverseTransformDirection(new Vector3(velocity.x, 0f, velocity.z));

            float pitch = Mathf.Clamp(-(_heuristicKp * localLean.z + _heuristicKd * localVelocity.z),
                -HEURISTIC_MAX_ACTION, HEURISTIC_MAX_ACTION);
            float roll = Mathf.Clamp(-(_heuristicKp * localLean.x + _heuristicKd * localVelocity.x),
                -HEURISTIC_MAX_ACTION, HEURISTIC_MAX_ACTION);

            // Standing (CommandedSpeed 0) leaves the balance controller exactly
            // as it was; the gait only fades in once a speed is commanded.
            float gaitBlend = Mathf.Clamp01(CommandedSpeed);
            if (gaitBlend <= 0f)
            {
                ApplyBalanceActions(continuous, pitch, roll);
                return;
            }

            // Leaning into the direction of travel is what turns stepping in
            // place into actual travel: the balance controller then keeps
            // catching a body that is already falling forwards.
            pitch += _gaitPitchSign * GAIT_LEAN_BIAS * gaitBlend;

            _gaitPhase += GAIT_STEP_FREQUENCY * 2f * Mathf.PI * Time.fixedDeltaTime;
            if (_gaitPhase > 2f * Mathf.PI)
            {
                _gaitPhase -= 2f * Mathf.PI;
            }
            float leftPhase = Mathf.Sin(_gaitPhase);
            float rightPhase = -leftPhase; // antiphase: one leg swings while the other bears load

            for (int dofIndex = 0; dofIndex < _dofRoles.Length; dofIndex++)
            {
                byte side = _dofSides[dofIndex];
                float legPhase = side == DOF_SIDE_LEFT ? leftPhase
                    : side == DOF_SIDE_RIGHT ? rightPhase
                    : 0f;
                float swing = _gaitPitchSign * gaitBlend * legPhase;

                switch (_dofRoles[dofIndex])
                {
                    case DOF_ROLE_HIP_PITCH:
                        continuous[dofIndex] = Mathf.Clamp(-0.5f * pitch + GAIT_HIP_SWING * swing,
                            -HEURISTIC_MAX_ACTION, HEURISTIC_MAX_ACTION);
                        break;
                    case DOF_ROLE_KNEE_PITCH:
                        // Bend only while the leg swings forward, so the foot
                        // clears the ground instead of scuffing it, and stay
                        // straight through stance to carry the body's weight.
                        continuous[dofIndex] = Mathf.Clamp(GAIT_KNEE_FLEX * Mathf.Max(0f, swing),
                            -HEURISTIC_MAX_ACTION, HEURISTIC_MAX_ACTION);
                        break;
                    case DOF_ROLE_ANKLE_PITCH:
                        continuous[dofIndex] = Mathf.Clamp(pitch - GAIT_ANKLE_PUSH * swing,
                            -HEURISTIC_MAX_ACTION, HEURISTIC_MAX_ACTION);
                        break;
                    case DOF_ROLE_ANKLE_ROLL: continuous[dofIndex] = roll; break;
                    case DOF_ROLE_HIP_ROLL: continuous[dofIndex] = -0.5f * roll; break;
                }
            }
        }

        private void ApplyBalanceActions(in ActionSegment<float> continuous, float pitch, float roll)
        {
            for (int dofIndex = 0; dofIndex < _dofRoles.Length; dofIndex++)
            {
                switch (_dofRoles[dofIndex])
                {
                    case DOF_ROLE_ANKLE_PITCH: continuous[dofIndex] = pitch; break;
                    case DOF_ROLE_ANKLE_ROLL: continuous[dofIndex] = roll; break;
                    case DOF_ROLE_HIP_PITCH: continuous[dofIndex] = -0.5f * pitch; break;
                    case DOF_ROLE_HIP_ROLL: continuous[dofIndex] = -0.5f * roll; break;
                }
            }
        }

        public override void OnActionReceived(ActionBuffers actions)
        {
            var continuous = actions.ContinuousActions;
            float deltaSum = 0f;
            for (int actionIndex = 0; actionIndex < _pendingActions.Length; actionIndex++)
            {
                float incoming = continuous[actionIndex];
                deltaSum += Mathf.Abs(incoming - _pendingActions[actionIndex]);
                _pendingActions[actionIndex] = incoming;
            }
            // Actions live in [-1, 1], so the largest per-DOF jump is 2.
            _lastActionDelta01 = _pendingActions.Length > 0
                ? Mathf.Clamp01(deltaSum / (_pendingActions.Length * 2f))
                : 0f;
            _hasPendingActions = true;
        }

        private void FixedUpdate()
        {
            if (_hasPendingActions)
            {
                _rig.ApplyActions(_pendingActions, 0);
            }
        }
    }
}
