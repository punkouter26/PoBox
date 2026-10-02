using System;
using UnityEngine;
using Unity.InferenceEngine;
using PoBox.Sim;

namespace PoBox.Rl
{
    /// <summary>
    /// A trained fighter's brain: builds the observation the policy was trained on, runs the ONNX policy
    /// through Unity's Inference Engine, and turns its output into joint targets. Fifty times a second,
    /// every fourth physics step, as in training. No outside help: there is no balance assist here.
    ///
    /// The observation is the trainer's, number for number (training/envs/boxing.py, at the top):
    ///     base_lin_vel(3) base_ang_vel(3) projected_gravity(3)
    ///     joint_pos - default(21) joint_vel(21) last_action(21)
    ///     foot_contact(2) base_height(1)
    ///     target_head(3) target_body(3) target_head_velocity(3)
    ///     own_gloves(2x3) opponent_gloves(2x3) opponent_facing(2) position_in_ring(2)
    /// in the trainer's frame (x forward, y left, z up), positions relative to the pelvis and turned into
    /// the fighter's own heading. Anything that does not match is an input the policy has never seen, and
    /// nothing will say so except the fighter falling over.
    ///
    /// One thing the policy was not trained for is handled around it rather than by it: while the referee
    /// counts over the other fighter it is shown a stand-in to square up to, so it does not stand over a
    /// body. Being hurt it was trained for: its joint drives weaken with the fighter's daze, exactly as
    /// they did in training, and the policy has learned what that costs.
    ///
    /// A fighter that has gone down gets up with a second policy, trained for that and nothing else
    /// (training/envs/getup.py). It sees the same hundred numbers, with a stand-in for the opponent
    /// standing where the real one is. When the fighter is on its feet the match policy has it back.
    ///
    /// Beside its actions a policy exported since 2026-10-01 gives its critic's value of the state: what
    /// it expects the next couple of seconds to be worth to it. <see cref="Value"/> is that number; the
    /// win-probability bar is built from the two fighters' values.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class PolicyBrain : MonoBehaviour
    {
        public Fighter fighter;
        public MjcfRig rig;
        public MjcfRig opponent;
        public ModelAsset model;
        [Tooltip("The policy that gets the fighter up off the canvas. Empty: the fighter is stood up by the referee.")]
        public ModelAsset getUpModel;
        public BackendType backend = BackendType.CPU;
        public Vector3 ringCentre;
        public float ringHalf = 3.05f;
        [Tooltip("Where this fighter goes while the referee counts over the other one.")]
        public Vector3 neutralSpot;
        [Tooltip("Glove speed towards the opponent that counts as a punch being thrown, m/s.")]
        public float punchSpeed = 3.5f;
        [Tooltip("The policy has only ever hit the bag. A bag has no gloves and faces nowhere, so those eight inputs were always " +
                 "zero in training and the policy's input scaling for them is meaningless: anything but zero there is noise at full volume.")]
        public bool bagStage;

        /// <summary>For tests: fills the action for a step itself and returns true, and the policy is not asked.
        /// Arguments are the brain, the number of steps since it was last reset, and the action to fill.</summary>
        public static Func<PolicyBrain, int, float[], bool> ActionOverride;
        /// <summary>For tests: called after every control step, with the observation and action of that step still in place.</summary>
        public static Action<PolicyBrain, int> Stepped;

        public const int ObservationSize = 100;

        public bool HasModel => _worker != null;
        public bool HasGetUp => _getUpWorker != null;
        /// <summary>The fighter is down and this is the get-up policy at work.</summary>
        public bool GettingUp { get; private set; }
        /// <summary>The critic's value of the present state, in the trainer's reward units. Only with <see cref="HasValue"/>.</summary>
        public float Value { get; private set; }
        public bool HasValue { get; private set; }
        public int PolicySteps { get; private set; }
        public float InferenceMs { get; private set; }
        /// <summary>Share of joint targets that had to be clamped to the joint ranges, smoothed.</summary>
        public float TargetClamping { get; private set; }
        public float[] LastObservation => _obs;
        public float[] LastAction => _action;
        int _sinceReset;

        Model _model, _getUpRuntime;
        Worker _worker, _getUpWorker;
        Tensor<float> _input;
        float[] _obs = new float[ObservationSize];
        float[] _action = new float[0], _lastAction = new float[0], _targets = new float[0], _jointPos = new float[0], _jointVel = new float[0];
        ArticulationDrive[] _baseDrives = new ArticulationDrive[0];
        float _driveScale = 1f;
        int _stepCounter;
        Vector3 _prevTargetHead;
        bool _hasPrev, _wasAway;
        readonly float[] _punchNotedAt = { -9f, -9f };

        void Awake()
        {
            if (rig == null) rig = GetComponentInChildren<MjcfRig>();
            if (fighter == null) fighter = GetComponent<Fighter>();
            int n = rig.joints.Length;
            _action = new float[n]; _lastAction = new float[n]; _targets = new float[n];
            _jointPos = new float[n]; _jointVel = new float[n];
            _baseDrives = new ArticulationDrive[n];
            for (int i = 0; i < n; i++) _baseDrives[i] = rig.joints[i].xDrive;

            if (model == null)
            {
                Debug.LogWarning($"[PolicyBrain] {name} has no policy; it will hold its guard and fall over.", this);
                return;
            }
            try
            {
                _model = ModelLoader.Load(model);
                _worker = new Worker(_model, backend);
                _input = new Tensor<float>(new TensorShape(1, ObservationSize), false);
                foreach (Model.Output o in _model.outputs) if (o.name == "value") HasValue = true;
                if (getUpModel != null)
                {
                    _getUpRuntime = ModelLoader.Load(getUpModel);
                    _getUpWorker = new Worker(_getUpRuntime, backend);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[PolicyBrain] could not load '{model.name}': {e.Message}", this);
                Dispose();
            }
        }

        void OnDestroy() => Dispose();

        void Dispose()
        {
            _input?.Dispose();
            _worker?.Dispose();
            _getUpWorker?.Dispose();
            _input = null;
            _worker = null;
            _getUpWorker = null;
        }

        /// <summary>Sent by the fighter when its time on the canvas is up. Answers only if there is a policy for getting up.</summary>
        public void BeginGetUp()
        {
            if (_getUpWorker == null || fighter == null) return;
            fighter.AcceptGetUp();
            GettingUp = true;
            Array.Clear(_lastAction, 0, _lastAction.Length);
            Array.Clear(_action, 0, _action.Length);
            _hasPrev = false;
        }

        /// <summary>Sent by the fighter when it is back on its feet: the match policy takes over where the get-up left the body.</summary>
        public void EndGetUp()
        {
            GettingUp = false;
            _hasPrev = false;
            _sight = Sight.Real;
        }

        /// <summary>Called when the fighter is stood back up: the policy starts from a clean slate, as an episode does.</summary>
        public void ResetBrain()
        {
            Array.Clear(_lastAction, 0, _lastAction.Length);
            Array.Clear(_action, 0, _action.Length);
            // One physics step, then the first control step. Not none: a body that has just been stood up
            // has been told where it is, but its transforms still say where it lay until the physics has
            // stepped once, and the policy's first look would be at a fighter on the floor.
            // (In MuJoCo there is nothing stale to wait for: the first look is at the reset itself, as in training.)
            _stepCounter = rig != null ? Mathf.Max(0, rig.controlDecimation - (rig.Shadowed ? 1 : 2)) : 0;
            _atCorner = false;
            _sight = Sight.Real;
            _sinceReset = 0;
            _hasPrev = false;
            GettingUp = false;
            SetDriveScale(1f);
        }

        /// <summary>The policy's answer to an observation given to it, without touching the fighter. For tests.</summary>
        public float[] Infer(float[] observation)
        {
            var result = new float[_action.Length];
            if (_worker == null) return result;
            using var input = new Tensor<float>(new TensorShape(1, ObservationSize), observation);
            _worker.Schedule(input);
            if (_worker.PeekOutput() is Tensor<float> output)
            {
                output.CompleteAllPendingOperations();
                ReadOnlySpan<float> span = output.AsReadOnlySpan();
                for (int i = 0; i < result.Length && i < span.Length; i++) result[i] = span[i];
            }
            return result;
        }

        void FixedUpdate()
        {
            if (!Bout.SimRunning || rig == null || rig.root == null) return;

            // Hurt: the drives weaken. Down: they go slack and the body is a ragdoll. The fighter keeps the number.
            SetDriveScale(fighter != null ? fighter.DriveScale : 1f);

            // While the referee counts over the other fighter this one stands in its guard, turned towards
            // them, and waits. When they are up it is let go from that stance, with a clear head.
            if (rig.Shadowed && fighter != null && fighter.opponent != null && opponent != null && !fighter.IsDown)
            {
                Vector3 towards = opponent.root.transform.position - rig.root.transform.position;
                if (fighter.opponent.IsDown)
                {
                    rig.ring.Hold(rig.slot, towards);
                    _holding = true;
                    return;
                }
                if (_holding)
                {
                    // One last look at where they have been stood, then go.
                    rig.ring.Hold(rig.slot, towards);
                    _holding = false;
                    _releaseNext = true;
                    return;
                }
                if (_releaseNext)
                {
                    _releaseNext = false;
                    rig.ring.Release(rig.slot);
                    ResetBrain();
                }
            }

            _stepCounter++;
            if (_stepCounter % rig.controlDecimation != 0) return;
            // Lying on the canvas with nothing answering, there is nothing to decide.
            if (fighter != null && fighter.IsDown && !GettingUp) return;
            Step();
        }

        void Step()
        {
            float controlDt = rig.controlDecimation * Time.fixedDeltaTime;
            BuildObservation(controlDt);

            int n = _action.Length;
            if (ActionOverride != null && ActionOverride(this, _sinceReset, _action))
            {
                for (int i = 0; i < n; i++) _action[i] = Mathf.Clamp(_action[i], -rig.actionClip, rig.actionClip);
            }
            else if (_worker != null)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                Worker worker = GettingUp && _getUpWorker != null ? _getUpWorker : _worker;
                _input.Upload(_obs);
                worker.Schedule(_input);
                if (HasValue && worker == _worker && worker.PeekOutput("value") is Tensor<float> value)
                {
                    value.CompleteAllPendingOperations();
                    float v = value.AsReadOnlySpan()[0];
                    if (!float.IsNaN(v) && !float.IsInfinity(v)) Value = v;
                }
                if (worker.PeekOutput("actions") is Tensor<float> output)
                {
                    output.CompleteAllPendingOperations();
                    ReadOnlySpan<float> span = output.AsReadOnlySpan();
                    int count = Mathf.Min(span.Length, n);
                    for (int i = 0; i < count; i++)
                    {
                        float a = span[i];
                        if (float.IsNaN(a) || float.IsInfinity(a)) a = 0f;
                        _action[i] = Mathf.Clamp(a, -rig.actionClip, rig.actionClip);
                    }
                }
                float ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                InferenceMs += (ms - InferenceMs) * 0.02f;
            }

            for (int i = 0; i < n; i++)
            {
                _targets[i] = rig.defaultPos[i] + _action[i] * rig.actionScale;
                _lastAction[i] = _action[i];
            }
            int clamped = rig.ApplyTargets(_targets);
            TargetClamping += (clamped / (float)n - TargetClamping) * 0.02f;
            PolicySteps++;
            NotePunches();
            Stepped?.Invoke(this, _sinceReset);
            _sinceReset++;
        }

        // ---------------------------------------------------------------- observation

        bool _atCorner, _holding, _releaseNext;
        enum Sight { Real, StandIn, Return }
        Sight _sight;
        Vector3 _phantom;

        /// <summary>
        /// Who the policy is shown while the referee counts over the real opponent: a stand-in for it,
        /// standing in its guard on the spot where the real one stood. A policy knows one thing to do, which
        /// is to box whoever is in front of it, so that is what it goes on doing: it holds its ground and
        /// shadow-boxes until the count is over. Then the stand-in walks to wherever the real opponent has
        /// been stood up, which is never far, and the two become one again.
        ///
        /// What this replaced, and why. First the fighter was shown its neutral corner outright: across the
        /// ring and often behind it, it spun round, hurried, and fell. Then a stand-in that walked to the
        /// corner at a stroll, which the fighter followed well enough, only to be left with its back to the
        /// real opponent when the count ended; a policy that in all its training has never had its opponent
        /// behind it fell over turning round. Forty-two such falls in one headless bout. A fighter that
        /// stays facing the one on the floor has no turning to do.
        /// Returns null when the fighter should simply be shown the real opponent.
        /// </summary>
        Vector3? Phantom(Vector3 here, float dt)
        {
            bool counting = opponent == null || (fighter != null && fighter.opponent != null && fighter.opponent.IsDown);
            Vector3 theirs = opponent != null ? opponent.root.transform.position : here + rig.Forward;
            theirs.y = here.y;
            if (counting)
            {
                if (_sight != Sight.StandIn) _phantom = _sight == Sight.Return ? _phantom : theirs;
                _sight = Sight.StandIn;
                GiveGround(here);
                return _phantom;
            }
            if (_sight == Sight.StandIn) _sight = Sight.Return;
            if (_sight == Sight.Return)
            {
                Vector3 to = theirs - _phantom;
                to.y = 0f;
                float d = to.magnitude;
                if (d < 0.15f) _sight = Sight.Real;
                else
                {
                    _phantom += to / d * Mathf.Min(0.6f * dt, d);
                    GiveGround(here);
                    return _phantom;
                }
            }
            return null;
        }

        /// <summary>
        /// The stand-in backs away from a fighter that comes inside punching range. It has to: these
        /// policies press forward until the other body stops them, and a stand-in has no body. Left where it
        /// was, the fighter walked straight through it, found its opponent suddenly behind it, and fell.
        /// </summary>
        void GiveGround(Vector3 here)
        {
            const float Range = 0.8f;
            Vector3 off = _phantom - here;
            off.y = 0f;
            if (off.magnitude < Range) _phantom = here + (off.sqrMagnitude > 1e-6f ? off.normalized : rig.Forward) * Range;
            float inside = ringHalf - 0.45f;
            _phantom.x = Mathf.Clamp(_phantom.x, ringCentre.x - inside, ringCentre.x + inside);
            _phantom.z = Mathf.Clamp(_phantom.z, ringCentre.z - inside, ringCentre.z + inside);
            _phantom.y = here.y;
        }

        /// <summary>Where this fighter currently believes its opponent to be standing, while the real one is on the floor.</summary>
        public Vector3? StandInSpot => _sight == Sight.StandIn ? _phantom : (Vector3?)null;

        /// <summary>For tests: who the policy is being shown, and where the stand-in is from the fighter (metres, and degrees left of straight ahead).</summary>
        public string SightDebug
        {
            get
            {
                if (_sight == Sight.Real) return "real";
                Vector3 to = _phantom - rig.root.transform.position;
                to.y = 0f;
                Vector3 fwd = rig.Forward;
                fwd.y = 0f;
                return $"{_sight} {to.magnitude:0.00} m {Vector3.SignedAngle(fwd, to, Vector3.down):0}deg";
            }
        }

        void BuildObservation(float controlDt)
        {
            if (rig.Shadowed)
            {
                // Getting up, the fighter is shown what it was shown when it learned to: a stand-in for the
                // other fighter, in its guard, on the spot where the real one is standing.
                Vector3? shown = GettingUp && opponent != null ? opponent.root.transform.position
                                 : Phantom(rig.root.transform.position, controlDt);
                rig.ring.Observe(rig.slot, _obs, _lastAction, shown, bagStage);
                return;
            }
            Transform pelvis = rig.root.transform;
            Vector3 pos = pelvis.position;
            Quaternion inv = Quaternion.Inverse(pelvis.rotation);
            // MuJoCo's free joint reports the velocity of the body's own origin, which is not quite its
            // centre of mass.
            Vector3 linWorld = rig.root.GetPointVelocity(pos);
            float floorY = ringCentre.y;
            int o = 0;

            CoordinateTransform.Write(_obs, o, inv * linWorld); o += 3;
            CoordinateTransform.WriteAngular(_obs, o, inv * rig.root.angularVelocity); o += 3;
            CoordinateTransform.Write(_obs, o, inv * Vector3.down); o += 3;

            rig.ReadJointState(_jointPos, _jointVel);
            int n = _jointPos.Length;
            for (int i = 0; i < n; i++) _obs[o + i] = _jointPos[i] - rig.defaultPos[i];
            o += n;
            for (int i = 0; i < n; i++) _obs[o + i] = _jointVel[i];
            o += n;
            for (int i = 0; i < n; i++) _obs[o + i] = _lastAction[i];
            o += n;

            // The trainer calls a foot down when its lowest corner is within 5 mm of the floor. PhysX rests
            // a box a hair higher than MuJoCo does, so the margin here is a little wider.
            _obs[o++] = MjcfRig.SoleClearance(rig.footL, floorY) < 0.012f ? 1f : 0f;
            _obs[o++] = MjcfRig.SoleClearance(rig.footR, floorY) < 0.012f ? 1f : 0f;
            _obs[o++] = pos.y - floorY;

            // Whoever is being fought: the opponent, or, while the referee is counting over them, a spot in
            // the neutral corner at head and chest height.
            Vector3? standIn = Phantom(pos, controlDt);
            bool away = standIn.HasValue;
            Vector3 targetHead, targetBody;
            if (away)
            {
                Vector3 at = standIn.Value;
                at.y = floorY;
                targetHead = at + Vector3.up * 1.6f;
                targetBody = at + Vector3.up * 1.2f;
            }
            else
            {
                targetHead = opponent.headGeom.position;
                targetBody = opponent.torsoGeom.position;
            }
            // The target has just changed from the fighter to the corner or back: its "velocity" across
            // that step would be the length of the ring in a fiftieth of a second.
            if (away != _wasAway) _hasPrev = false;
            _wasAway = away;
            float yaw = rig.Yaw;
            WriteHeading(o, targetHead - pos, yaw); o += 3;
            WriteHeading(o, targetBody - pos, yaw); o += 3;
            Vector3 headVelocity = _hasPrev ? (targetHead - _prevTargetHead) / controlDt : Vector3.zero;
            // Nobody's head does 20 m/s. That is the other fighter being stood back up by the referee.
            if (headVelocity.sqrMagnitude > 400f) headVelocity = Vector3.zero;
            _prevTargetHead = targetHead;
            _hasPrev = true;
            WriteHeading(o, headVelocity - linWorld, yaw); o += 3;

            WriteHeading(o, rig.gloveL.position - pos, yaw); o += 3;
            WriteHeading(o, rig.gloveR.position - pos, yaw); o += 3;
            if (away || bagStage)
            {
                for (int i = 0; i < 8; i++) _obs[o + i] = 0f;
                o += 8;
            }
            else
            {
                WriteHeading(o, opponent.gloveL.position - pos, yaw); o += 3;
                WriteHeading(o, opponent.gloveR.position - pos, yaw); o += 3;
                float turn = opponent.Yaw - yaw;
                _obs[o++] = Mathf.Cos(turn);
                _obs[o++] = Mathf.Sin(turn);
            }

            Vector3 ring = CoordinateTransform.UnityToExternal(ringCentre - pos);
            float c = Mathf.Cos(yaw), s = Mathf.Sin(yaw);
            _obs[o++] = (c * ring.x + s * ring.y) / ringHalf;
            _obs[o++] = (-s * ring.x + c * ring.y) / ringHalf;

            for (int i = 0; i < _obs.Length; i++)
            {
                float v = _obs[i];
                _obs[i] = float.IsNaN(v) ? 0f : Mathf.Clamp(v, -100f, 100f);
            }
        }

        /// <summary>A world vector, into the trainer's axes and then turned into the fighter's own heading.</summary>
        void WriteHeading(int o, Vector3 worldVector, float yaw)
        {
            Vector3 e = CoordinateTransform.UnityToExternal(worldVector);
            float c = Mathf.Cos(yaw), s = Mathf.Sin(yaw);
            _obs[o + 0] = c * e.x + s * e.y;
            _obs[o + 1] = -s * e.x + c * e.y;
            _obs[o + 2] = e.z;
        }

        // ---------------------------------------------------------------- punches, for the scorekeeper

        /// <summary>
        /// The scripted fighters announced each punch as they threw it; a policy announces nothing. A punch
        /// is recognised here by what it is: a glove moving at the opponent faster than a guard moves.
        /// </summary>
        void NotePunches()
        {
            if (fighter == null || opponent == null || fighter.IsDown) return;
            Vector3 head = opponent.headGeom.position;
            for (int hand = 0; hand < 2; hand++)
            {
                if (Time.time - _punchNotedAt[hand] < 0.35f) continue;
                BodyPart arm = hand == 0 ? fighter.forearmL : fighter.forearmR;
                Transform glove = hand == 0 ? rig.gloveL : rig.gloveR;
                if (arm == null || glove == null) continue;
                Vector3 to = head - glove.position;
                float distance = to.magnitude;
                if (distance < 0.05f || distance > 1.3f) continue;
                Vector3 v = arm.body.GetPointVelocity(glove.position);
                float closing = Vector3.Dot(v, to / distance);
                if (closing < punchSpeed) continue;

                _punchNotedAt[hand] = Time.time;
                // Named by how the glove is travelling: straight at the head, up into it, round at it, or low.
                float speed = Mathf.Max(0.01f, v.magnitude);
                PunchType type;
                if (glove.position.y < opponent.torsoGeom.position.y + 0.12f) type = PunchType.Body;
                else if (v.y / speed > 0.6f) type = PunchType.Uppercut;
                else if (closing / speed < 0.75f) type = PunchType.Hook;
                else type = hand == 0 ? PunchType.Jab : PunchType.Cross;
                fighter.NotePunch(hand == 0 ? -1 : 1, type);
            }
        }

        // ---------------------------------------------------------------- drives

        void SetDriveScale(float s)
        {
            if (rig.Shadowed) { rig.ring.SetDriveScale(rig.slot, s); _driveScale = s; return; }
            if (Mathf.Abs(s - _driveScale) < 0.03f) return;
            _driveScale = s;
            for (int i = 0; i < rig.joints.Length; i++)
            {
                ArticulationBody ab = rig.joints[i];
                ArticulationDrive d = _baseDrives[i];
                d.target = ab.xDrive.target;
                d.stiffness *= s;
                d.damping *= Mathf.Lerp(0.3f, 1f, s);
                ab.xDrive = d;
            }
        }
    }
}
