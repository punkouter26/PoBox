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
        //
        // GEN 8 (2026-08-21): 1.0 was too far the other way, and the value is
        // now bracketed by two measured failures.
        //
        //   0.3 (gen 6): blend 0 / .67 / 1 / 1 / 1 / 1 - saturated at rung 3.
        //                The fighter stepped and fell (clearance 0.258,
        //                survival 3.3 s).
        //   1.0 (gen 7): blend 0 / .2 / .4 / .6 / .8 / 1 - never bit. At rung 3
        //                the command averages 0.32 m/s, so the blend is 0.32 and
        //                BOTH FEET PLANTED still scores 0.68 on the support
        //                term. Standing costs support^.3 * clearance^.35, a 20%
        //                haircut; attempting a step and falling costs ~60% of
        //                the episode. The fighter correctly paid the 20%:
        //                SingleSupportMean measured 0.005-0.034 across all
        //                12.2M steps and every lesson. It never once stood on
        //                one foot. That is the same "pay the penalty and ignore
        //                it" failure the WEIGHTS were raised to fix in gen 2,
        //                arriving through the blend instead of the weight.
        //
        // 0.6 gives 0 / .33 / .67 / 1 / 1 / 1: it saturates one rung LATER than
        // the value that collapsed, and at rung 3 a planted fighter keeps only
        // 0.64 instead of 0.80 - a 36% haircut rather than 20%.
        //
        // GEN 9 (2026-08-21): 0.6 has now been measured too, and this constant
        // is NOT the lever. Gen 8 ran 9.55M steps: SingleSupportMean crept
        // 0.009 -> 0.022 and stopped, reward peaked at 0.115 and never reached
        // the 0.17 Shuffle gate. The cause is the SHAPE of the support term,
        // not this value -- see the support block in FixedUpdate. The
        // pre-registered fallback of 0.8 would have made things WORSE, not
        // better: it puts rung 3 at blend 0.40, where planting scores 0.60
        // against stepping's 0.40, so planting wins outright again. The old
        // term stopped discriminating at blend 0.5 and inverted below it, and
        // no choice of this constant fixes that.
        //
        // It stays at 0.6 so gen 9 moves one mechanism. The ladder still reads
        // 0 / .33 / .67 / 1 / 1 / 1.
        private const float GAIT_FULL_SPEED = 0.6f;
        // Separate from the blend on purpose: below this the LESSON is about
        // standing, so commands are still drawn from zero. Folding this into
        // the blend constant is what made one number do two unrelated jobs.
        private const float STAND_LESSON_SPEED = 0.3f;
        // True ground clearance of the swing foot that scores full credit.
        //
        // GEN 11 (2026-08-22): 0.09 measured a GAP BETWEEN FOOT CENTRES, which
        // gen 10 proved is not the same quantity at all. It now measures the
        // lowest point of the swing foot above the floor, so the number had to
        // be re-scaled with the metric: 9 cm of centre separation is reachable
        // by ankle rotation alone, while 9 cm of true clearance is a high
        // march. 5 cm is a real step for a fighter that has never taken one,
        // and keeps the DIFFICULTY comparable rather than the digits.
        private const float TARGET_CLEARANCE = 0.05f;
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
        // that forces alternation: single support always scores full marks,
        // while the credit for both feet planted fades to nothing as the
        // commanded speed rises, so standing still stops being viable.
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

        private Collider _footLeftCollider;
        private Collider _footRightCollider;
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
            // Cached: bounds is queried twice per fixed step per fighter.
            _footLeftCollider = _rig.FootLeftSensor.GetComponent<Collider>();
            _footRightCollider = _rig.FootRightSensor.GetComponent<Collider>();
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
            // GEN 10: clearance is computed BEFORE support, because support now
            // uses it as a continuous stand-in for "one foot is unloading".
            //
            // Gen 4 measured swing height against a snapshot of the reset pose,
            // captured before the body had settled under gravity; once the
            // agent learned to brace and sink, both feet sat below the snapshot
            // for the rest of every episode and ClearanceMean read exactly
            // 0.000 for five million steps. Gen 5 replaced it with a
            // foot-to-foot gap, which was self-calibrating against crouching.
            //
            // GEN 11: measured from the LOWEST POINT of each foot, not the foot
            // transform. Gen 10 farmed the old measure outright. The sensor
            // transform sits at the foot's centre, and the ankle has 65 degrees
            // of pitch range, so rolling onto the toe raises the centre without
            // breaking contact. Measured live at 10.8M steps, mid-run:
            //
            //   rig  centre  lowest  grounded
            //    5   0.156   0.003   true      <- foot vertical, toe still down
            //    2   0.108   0.002   true
            //
            // Rig 5 scored FULL clearance credit with both feet planted. Four of
            // sixteen fighters were doing this. Gens 5-9 never found it because
            // the binary support cliff made clearance alone nearly worthless;
            // gen 10's ramp made the climb worth it and the cheapest route up
            // was rotation, not elevation.
            //
            // The collider's world AABB minimum is the lowest corner whatever
            // the foot's orientation, so a pivoted foot reads ~0 and only real
            // elevation scores. It is also ground-relative rather than
            // foot-to-foot, so a fighter cannot manufacture a gap by sinking
            // the stance foot either.
            float footLeftLift = Mathf.Max(0f, _footLeftCollider.bounds.min.y - _rig.GroundY);
            float footRightLift = Mathf.Max(0f, _footRightCollider.bounds.min.y - _rig.GroundY);
            float swingHeight = Mathf.Max(footLeftLift, footRightLift);
            float clearance = Mathf.Clamp01(swingHeight / TARGET_CLEARANCE);
            // GEN 9 bug fix: with BOTH feet off the floor there is no stance
            // foot, so the gap between them is not clearance -- it is a topple.
            // singleSupport already scores 0 in that state (XOR), but clearance
            // did not, so falling over paid up to 31% on this term. Gen 8's
            // last 1.4M steps are that exploit running: ClearanceMean 0.156 ->
            // 0.248 while StepsSurvived fell 856 -> 250 and support stayed
            // flat. Clearance now has to be earned with a foot on the ground.
            if (!leftDown && !rightDown)
            {
                clearance = 0f;
            }
            // Exactly one foot down is the single-support phase every step
            // passes through. Rewarding it is what makes the legs take turns.
            float singleSupport = leftDown ^ rightDown ? 1f : 0f;
            float doubleSupport = leftDown && rightDown ? 1f : 0f;
            // GEN 9: was Lerp(doubleSupport, singleSupport, gaitBlend), which
            // scores planting (1 - blend) against stepping's blend. Those two
            // curves cross at blend 0.5 -- so the term stopped discriminating
            // exactly where the curriculum spends its time, and PAID PLANTING
            // MORE below it. At rung 3 (blend 0.53) stepping was worth 0.533
            // against 0.467: a 4.1% gain once the 0.3 exponent is applied. Gen
            // 8 measured what that buys -- support crept 0.009 -> 0.022 over
            // 9.55M steps and went no further, because 4% cannot pay for a
            // behaviour that risks the episode. Return is per-step score times
            // survived/MaxStep, and gen 8's own fall from 880 surviving steps
            // to 250 cost 72% of it.
            //
            // This form holds single support at FULL value whatever the blend
            // and fades only the credit for standing planted:
            //
            //   blend 0.00 (StandStill)  planted 1.00   stepping 1.00
            //   blend 0.53 (Shuffle)     planted 0.47   stepping 1.00
            //   blend 1.00 (Walk)        planted 0.00   stepping 1.00
            //
            // Standing is still paid in full at blend 0, so the StandStill
            // lesson is unchanged; at rung 3 stepping is now worth 25.7% over
            // planting rather than 4.1%; and planting can never outscore
            // stepping at any blend, which the Lerp did for every blend
            // below 0.5.
            //
            // GEN 10 (2026-08-21): the term above was still a CLIFF. singleSupport
            // is a binary XOR of two contact flags, so it pays exactly 0 until the
            // trailing foot fully breaks contact and 1 the instant it does. There
            // is no gradient across the last and hardest increment of a step.
            //
            // Gen 9's own brain, run in inference at 0.32 m/s, measured:
            //
            //   SingleSupportMean        0.0001   over 10,587 steps
            //   lateral weight shift     mean 0.333, max 0.798 (1 = over one foot)
            //   foot-to-foot gap         ~1.4 cm against a 9 cm target
            //
            // It is NOT a statue and it is NOT missing the precursor skill: it
            // rocks up to 80% of its weight onto one foot and raises the other
            // by over a centimetre. It simply never collects a single point for
            // any of that, because the flag never flips. Four generations of
            // tuning the SIZE of the single-support prize could not matter while
            // the prize remained unreachable in one discontinuous jump.
            //
            // So partial unloading now earns partial credit. `clearance` is the
            // continuous stand-in for "a foot is coming off": the credit for
            // standing planted decays as it rises, and the swing-foot lift
            // itself substitutes for the binary flag until the flag flips.
            //
            //   blend 0.53, planted, no lift      0.47   (unchanged from gen 9)
            //   blend 0.53, planted, half lift    0.74
            //   blend 0.53, true single support   1.00   (unchanged from gen 9)
            //
            // Both endpoints are exactly gen 9's, so this adds a ramp between
            // them rather than moving the target.
            float planted = doubleSupport * (1f - clearance);
            float supportReward = Mathf.Min(1f, Mathf.Max(singleSupport, clearance) + (1f - gaitBlend) * planted);

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
