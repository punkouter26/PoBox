using Unity.MLAgents;
using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Unified stand-and-walk reward. The agent is handed a commanded speed
    /// each episode and paid for matching it while staying upright, so
    /// standing still is simply the 0 m/s case of walking and no separate
    /// balance phase or brain hand-off is needed. The curriculum raises
    /// <c>speed_command_max</c> from 0 to walking pace; the command is also an
    /// observation, so the finished brain obeys whatever speed the game asks
    /// for at runtime.
    ///
    /// Reward shape, deliberately different from <see cref="Reward_Balance"/>:
    /// there is NO terminal fall penalty. Per-step reward is positive-definite
    /// and ending early already forfeits every remaining step, which is
    /// punishment enough. The old -1 terminal was ~40x anything a 1.5 s
    /// episode could earn and drowned the signal (observed 2026-08-19:
    /// mean reward pinned at -0.98, episodes stuck at 76 steps).
    /// </summary>
    [DefaultExecutionOrder(-99)] // after the agent (-100), before the Academy stepper
    public sealed class Reward_Locomotion : MonoBehaviour
    {
        private const float HEAD_COLLAPSE_FRACTION = 0.4f;
        // Speed error at which the match term decays to ~e^-1. Was 0.6, which
        // was far too forgiving: standing dead still while commanded 0.2 m/s
        // still scored 0.98, so the agent learned to stand and eat the loss
        // (measured 2026-08-19: 3 cm travelled in 19 s at reward 0.94).
        private const float SPEED_KERNEL = 0.3f;
        // Commanded speed at which the gait terms reach full weight. Below it
        // they fade out, so the StandStill lesson still wants both feet down.
        //
        // Was 0.3, which quietly capped the curriculum at its third rung. The
        // blend is commandedSpeed / this, clamped to 1, so at 0.3 the gait
        // demand ran 0 -> 0.67 -> 1.0 -> 1.0 -> 1.0 -> 1.0 across gen 6's six
        // speed rungs: a fighter that had never lifted a foot was handed the
        // FULL step-and-alternate demand by rung 3, and rungs 4-6 then asked
        // for nothing new. Gen 6 collapsed at exactly rung 3 (survival 15 s ->
        // 3.3 s while clearance climbed 0.079 -> 0.258) and gen 5 collapsed the
        // same way one rung earlier. Matching this to the top curriculum speed
        // makes the blend track the ladder 1:1 - 0.2 speed means 0.2 gait
        // demand - so every rung is a real, small increase in what is asked.
        private const float GAIT_FULL_SPEED = 1f;
        // Separate from the blend on purpose: below this the LESSON is about
        // standing, so commands are still drawn from zero. Folding this into
        // the blend constant is what made one number do two unrelated jobs.
        private const float STAND_LESSON_SPEED = 0.3f;
        // Gap between the two feet that scores full clearance. Roughly one
        // foot depth — enough to clear the floor, not a high march.
        private const float TARGET_CLEARANCE = 0.09f;
        // Floor of the commanded-speed draw, as a fraction of the lesson cap,
        // once the cap is above walking-relevant speed. Gen 3 drew uniformly
        // from [0, cap]: with cap 1.0 that made ~30% of episodes ask for under
        // 0.3 m/s, where standing still is CORRECT and scores ~1. The agent
        // banked those easy episodes and ignored the fast ones, which is how
        // mean reward read 0.34 while it had never once lifted a foot.
        private const float SPEED_COMMAND_MIN_FRACTION = 0.6f;
        // Never let a [0,1] criterion reach exactly 0: one zero would wipe the
        // whole product and erase every other criterion's gradient.
        // Gen 4 used 0.05, which paid the statue far too well: failing speed,
        // support and clearance outright still returned 0.05^0.5 * 0.05^0.3 *
        // 0.05^0.35 = 0.031 per step, guaranteed and risk-free, for 3000
        // steps. At 0.01 the same total failure returns 0.005 -- a 6x cut to
        // the statue and no change at all to a real walker, widening the gap
        // from 32x to 200x. The floor stays POSITIVE on purpose: a negative
        // per-step reward plus a terminable episode makes falling over the
        // fastest way to stop losing points, which is how the old -1 terminal
        // failed.
        private const float PRODUCT_FACTOR_FLOOR = 0.01f;

        [SerializeField] private Agent_FighterBoxing _agent;
        [SerializeField] private Systems_FighterRig _rig;
        [SerializeField] private Sensor_GroundContact[] _fallContacts;
        [SerializeField] private float _uprightWeight = 0.15f;
        // Head height was 0.15 and is now 0: it is the one term that pays the
        // agent for NOT moving, and gen 2 leaned on it to justify standing
        // still. Uprightness plus the fall terminal already keep posture.
        [SerializeField] private float _heightWeight;
        [SerializeField] private float _speedMatchWeight = 0.5f;
        // Pays for standing on exactly ONE foot while moving. This is the term
        // that forces alternation: both feet planted scores nothing once a
        // speed is commanded, so standing still stops being viable.
        // Weight is an EXPONENT, so small values barely bite — at gen 2's 0.15
        // a total failure cost only ~10%, and the agent simply paid it.
        [SerializeField] private float _singleSupportWeight = 0.3f;
        // Pays the swinging foot for leaving the floor, which rules out
        // shuffling and sliding as ways to satisfy the speed term. Was 0.1,
        // where failing completely cost 6.5% — measured outcome was 0.09 mm of
        // foot lift. At 0.35 the same failure costs ~24%.
        [SerializeField] private float _clearanceWeight = 0.35f;
        // Penalizes jerky tick-to-tick command changes so calm motion wins.
        [SerializeField] private float _smoothnessWeight = 0.05f;
        // Upper end of the commanded-speed range. Driven by the curriculum
        // parameter "speed_command_max": 0 in the standing lesson, walking
        // pace in the last one.
        [SerializeField] private float _speedCommandMax;

        private float _stepScale;
        private float _startHeadHeight;
        private float _commandedSpeed;
        private Vector3 _commandedDirection = Vector3.forward;
        private bool _terminated;
        private int _lastStepCount;
        private float _speedMatchSum;
        private float _supportSum;
        private float _clearanceSum;
        private int _speedMatchSamples;

        // Called by the editor scene builder.
        public void EditorInitialize(Agent_FighterBoxing agent, Systems_FighterRig rig,
            Sensor_GroundContact[] fallContacts)
        {
            _agent = agent;
            _rig = rig;
            _fallContacts = fallContacts;
        }

        private void Start()
        {
            // Scaled so a full-length episode at perfect score returns ~1.
            _stepScale = 1f / Mathf.Max(1, _agent.MaxStep);
            _startHeadHeight = _rig.Head.position.y;
            RollCommand();
        }

        private void FixedUpdate()
        {
            int stepCount = _agent.StepCount;
            if (stepCount < _lastStepCount)
            {
                // New episode began (fall reset or MaxStep rollover).
                FlushEpisodeStats();
                _startHeadHeight = _rig.Head.position.y;
                RollCommand();
                _terminated = false;
            }
            _lastStepCount = stepCount;
            if (_terminated)
            {
                return;
            }

            if (IsFallen(out int fallCause))
            {
                // No penalty added: forfeiting the rest of the episode is the
                // punishment. See the class summary.
                Academy.Instance.StatsRecorder.Add("Locomotion/FallCause", fallCause, StatAggregationMethod.Histogram);
                Academy.Instance.StatsRecorder.Add("Locomotion/StepsSurvived", stepCount);
                _terminated = true;
                _agent.EndEpisode();
                return;
            }

            Vector3 torsoUp = _rig.Torso.transform.up;
            float uprightDot = Mathf.Max(0f, Vector3.Dot(torsoUp, Vector3.up));
            float uprightReward = uprightDot * uprightDot;

            float headDelta = _rig.Head.position.y - _startHeadHeight;
            float heightReward = Mathf.Exp(-20f * headDelta * headDelta);

            // Signed: travelling backwards scores worse than standing still,
            // which stops "fall away from the goal" from looking neutral.
            float speedAlongGoal = Vector3.Dot(_rig.Pelvis.linearVelocity, _commandedDirection);
            float speedError = speedAlongGoal - _commandedSpeed;
            float speedMatchReward = Mathf.Exp(-(speedError * speedError) / (SPEED_KERNEL * SPEED_KERNEL));
            _speedMatchSum += speedMatchReward;
            _speedMatchSamples++;

            // Gait terms fade in with the command: at 0 m/s the agent should
            // be planted on both feet, so demanding one-foot support there
            // would punish correct standing.
            float gaitBlend = Mathf.Clamp01(_commandedSpeed / GAIT_FULL_SPEED);

            bool leftDown = _rig.FootLeftSensor.IsGrounded;
            bool rightDown = _rig.FootRightSensor.IsGrounded;
            // Exactly one foot down is the single-support phase every step
            // passes through. Rewarding it is what makes the legs take turns:
            // both feet planted pays nothing once a speed is commanded.
            float singleSupport = leftDown ^ rightDown ? 1f : 0f;
            float doubleSupport = leftDown && rightDown ? 1f : 0f;
            float supportReward = Mathf.Lerp(doubleSupport, singleSupport, gaitBlend);

            // Swing-foot height is measured against the STANCE foot, not a
            // snapshot of the reset pose. Gen 4 captured that snapshot on the
            // reset tick, before the body had settled under gravity; once the
            // agent learned to brace and sink, both feet sat below the
            // snapshot for the rest of every episode and ClearanceMean read
            // exactly 0.000 for five million steps -- the reward was blind,
            // not merely stingy. Foot-to-foot is self-calibrating: crouching,
            // sinking and the reset pose all cancel out, and the gap between
            // the feet is literally what a step is.
            float footLeftY = _rig.FootLeftSensor.transform.position.y;
            float footRightY = _rig.FootRightSensor.transform.position.y;
            float swingHeight = Mathf.Abs(footLeftY - footRightY);
            float clearance = Mathf.Clamp01(swingHeight / TARGET_CLEARANCE);
            float clearanceReward = Mathf.Lerp(1f, clearance, gaitBlend);
            // Log the RAW single-support fraction, not the blended term: the
            // blended value mixes in the blend weight and cannot tell "both
            // feet planted" from "perfect alternation" at mid-blend.
            _supportSum += singleSupport;
            _clearanceSum += clearance;

            float locomotionReward =
                ProductFactor(uprightReward, _uprightWeight) *
                ProductFactor(heightReward, _heightWeight) *
                ProductFactor(speedMatchReward, _speedMatchWeight) *
                ProductFactor(supportReward, _singleSupportWeight) *
                ProductFactor(clearanceReward, _clearanceWeight);

            _agent.AddReward(_stepScale * (locomotionReward - _smoothnessWeight * _agent.LastActionDelta01));
        }

        // Clamps a [0,1] criterion away from zero, then applies its weight as
        // an exponent so the terms form a weighted geometric mean.
        private static float ProductFactor(float value, float weight)
        {
            return Mathf.Pow(Mathf.Max(PRODUCT_FACTOR_FLOOR, value), weight);
        }

        // One command per episode: a constant target is far easier to learn
        // than one that changes mid-episode, and the game only ever sets it
        // once per round anyway.
        private void RollCommand()
        {
            _speedCommandMax = Academy.Instance.EnvironmentParameters
                .GetWithDefault("speed_command_max", _speedCommandMax);
            // Below the gait-blend speed the lesson is genuinely about standing,
            // so keep drawing from zero. Above it, hold the floor up so most
            // episodes actually require travel.
            float commandMin = _speedCommandMax > STAND_LESSON_SPEED
                ? _speedCommandMax * SPEED_COMMAND_MIN_FRACTION
                : 0f;
            _commandedSpeed = Random.Range(commandMin, _speedCommandMax);
            _commandedDirection = Vector3.forward;
            _agent.SetLocomotionCommand(_commandedSpeed, _commandedDirection);
        }

        private void FlushEpisodeStats()
        {
            if (_speedMatchSamples > 0)
            {
                Academy.Instance.StatsRecorder.Add("Locomotion/SpeedMatchMean", _speedMatchSum / _speedMatchSamples);
                Academy.Instance.StatsRecorder.Add("Locomotion/CommandedSpeed", _commandedSpeed);
                // The two numbers that say whether it is walking or cheating:
                // SingleSupportMean near 0 means both feet stayed planted,
                // ClearanceMean near 0 means it shuffled without lifting.
                Academy.Instance.StatsRecorder.Add("Locomotion/SingleSupportMean", _supportSum / _speedMatchSamples);
                Academy.Instance.StatsRecorder.Add("Locomotion/ClearanceMean", _clearanceSum / _speedMatchSamples);
            }
            _speedMatchSum = 0f;
            _supportSum = 0f;
            _clearanceSum = 0f;
            _speedMatchSamples = 0;
        }

        private bool IsFallen(out int fallCause)
        {
            for (int contactIndex = 0; contactIndex < _fallContacts.Length; contactIndex++)
            {
                if (_fallContacts[contactIndex].IsGrounded)
                {
                    fallCause = contactIndex;
                    return true;
                }
            }
            // Measured above the floor on BOTH sides. Comparing raw world Y
            // against a fraction of raw world Y silently rescales with altitude:
            // on a 1 m ring canvas, 40% of a 2.6 m head height is 1.04 m, so a
            // fighter would have to sink to 4 cm above the canvas to count as
            // collapsed instead of the intended ~64 cm.
            float headAboveGround = _rig.Head.position.y - _rig.GroundY;
            float standingHeadAboveGround = _startHeadHeight - _rig.GroundY;
            if (headAboveGround < standingHeadAboveGround * HEAD_COLLAPSE_FRACTION)
            {
                fallCause = _fallContacts.Length;
                return true;
            }
            fallCause = -1;
            return false;
        }
    }
}
