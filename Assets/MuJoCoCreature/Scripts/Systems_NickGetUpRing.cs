using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace PoBox.MuJoCoCreature
{
    /// <summary>
    /// Nick's get-up practice ring: he stands, a big shove puts him on the
    /// floor on a timer, and the ring times how long he takes to stand back
    /// up -- the behaviour the get-up task in Tools/MuJoCo/nick_env.py
    /// (getup_fraction) trains. Hands-free by design: knock-down, success
    /// detection and the next attempt all run on their own, so a training
    /// session can be watched without touching anything.
    ///
    /// THE SUCCESS RULE is the one the training rewards: UP means head above
    /// 75% of his standing height AND STAYING there for _stableSeconds --
    /// then, and only then, the attempt counts. Head above 55% is the DOWN
    /// test, the same collapse rule Systems_NickBalanceRing and
    /// Systems_BalanceContest use.
    ///
    /// THE KNOCK-DOWN is an external impulse, not a joint limit: 1800 N for
    /// 0.35 s at the pelvis, several times the ring shoves he survives, so a
    /// healthy brain genuinely topples. Joint speed and force stay inside the
    /// human budget; the hazard does not.
    ///
    /// THE CONTROLLER'S AUTO-RESET IS OFF in this scene (the rig tool sets
    /// _fallHeight = -1): a fallen body stays fallen, exactly as in the
    /// contest scenes. If DebugResetCount moves anyway something is wrong --
    /// the brain teleporting itself upright would fake every success -- so
    /// that is logged loudly and fails the attempt.
    ///
    /// Logs GETUP_ROUND (knocked down) and GETUP_RESULT (success/fail) so a
    /// session is judged by grepping the log, same as CONTEST_ROUND.
    /// Keys: R stand him back up (fresh attempt), D knock down now.
    /// </summary>
    [DefaultExecutionOrder(-40)] // after the controller has read the sim
    public sealed class Systems_NickGetUpRing : MonoBehaviour
    {
        private enum Phase { Standing, GettingUp }

        [SerializeField] private CreatureSentisController _controller;

        [Header("Cycle")]
        [Tooltip("Seconds he stands between attempts before the automatic knock-down.")]
        [SerializeField] private float _standSeconds = 30f;
        [Tooltip("Seconds an attempt may take before it is judged a failure.")]
        [SerializeField] private float _getUpSeconds = 45f;
        [Tooltip("Head above this fraction of standing height AND held for _stableSeconds counts as up.")]
        [SerializeField] private float _upHeadFraction = 0.75f;
        [Tooltip("Head under this fraction of standing height means down (the ring collapse rule).")]
        [SerializeField] private float _downHeadFraction = 0.55f;
        [Tooltip("Continuously up this long = a successful get-up, matching the training reward.")]
        [SerializeField] private float _stableSeconds = 4f;

        [Header("Knock-down")]
        [Tooltip("Pelvis impulse force. Several times the ring shoves he survives, so it always topples.")]
        [SerializeField] private float _knockDownNewtons = 1800f;
        [SerializeField] private float _knockDownSeconds = 0.35f;
        [Tooltip("Give him this long standing before the first automatic knock-down.")]
        [SerializeField] private float _firstDelaySeconds = 3f;

        [Header("Failure handling")]
        [Tooltip("On a failed attempt, stand him back up so the cycle continues. OFF leaves him on the floor.")]
        [SerializeField] private bool _autoStandOnFail = true;

        [Header("Presentation")]
        [SerializeField] private UIDocument _hud;

        private Label _readout;
        private Phase _phase = Phase.Standing;
        private int _attempt;               // knock-downs so far
        private int _successes;
        private float _bestSeconds = float.MaxValue;
        private float _phaseTimer;
        private float _upStreak;            // consecutive seconds above the up line
        private float _startHeadZ;          // standing head height, captured each stand
        private int _resetsAtStand;

        private void Start()
        {
            if (_controller == null) { _controller = GetComponentInChildren<CreatureSentisController>(); }
            if (_hud != null)
            {
                _readout = new Label { name = "NickGetUpReadout" };
                _readout.style.color = Color.white;
                _readout.style.fontSize = 26;
                _readout.style.whiteSpace = WhiteSpace.Normal;
                _readout.style.marginLeft = 16;
                _readout.style.marginTop = 60;
                _hud.rootVisualElement.Add(_readout);
            }
        }

        private void FixedUpdate()
        {
            if (_controller == null || !_controller.IsBound) { return; }
            if (_startHeadZ <= 0f) { BeginStanding(_firstDelaySeconds); return; }

            _phaseTimer -= Time.fixedDeltaTime;
            float headZ = _controller.DebugHeadZ;

            // A controller self-reset would teleport him upright and fake a
            // success: it is disabled in this scene (see _fallHeight), so any
            // movement of the counter is a fault, not a recovery.
            if (_controller.DebugResetCount > _resetsAtStand)
            {
                Debug.LogError("GETUP_RESULT | attempt " + _attempt +
                               " | fault: the controller self-reset (auto-stand) mid-attempt.");
                BeginStanding(_standSeconds);
                UpdateReadout();
                return;
            }

            switch (_phase)
            {
                case Phase.Standing:
                    if (headZ < _startHeadZ * _downHeadFraction)
                    {
                        // He went over without the scripted shove (or was shoved
                        // by key early). Either way, this is an attempt.
                        StartAttempt();
                        break;
                    }
                    if (_phaseTimer <= 0f) { KnockDown(); }
                    break;

                case Phase.GettingUp:
                    if (headZ >= _startHeadZ * _upHeadFraction)
                    {
                        _upStreak += Time.fixedDeltaTime;
                        if (_upStreak >= _stableSeconds) { Succeed(); }
                    }
                    else
                    {
                        _upStreak = 0f;
                    }
                    if (_phase == Phase.GettingUp && _phaseTimer <= 0f) { Fail(); }
                    break;
            }

            UpdateReadout();
        }

        /// <summary>He is upright; run the standing clock until the shove.</summary>
        private void BeginStanding(float standSeconds)
        {
            // Canonical pose, velocities zeroed -- the same state every
            // training episode starts from.
            _controller.ResetCreature();
            _controller.SetCommand(0f);
            _phase = Phase.Standing;
            _phaseTimer = standSeconds;
            _upStreak = 0f;
            // Measured AFTER the reset: this is the height "up" is measured against.
            _startHeadZ = _controller.DebugHeadZ;
            _resetsAtStand = _controller.DebugResetCount;
        }

        private void StartAttempt()
        {
            _attempt++;
            _phase = Phase.GettingUp;
            _phaseTimer = _getUpSeconds;
            _upStreak = 0f;
            Debug.Log($"GETUP_ROUND {_attempt} | he is down | timer {_getUpSeconds:F0} s starts");
        }

        private void KnockDown()
        {
            float angle = Random.Range(0f, Mathf.PI * 2f);
            // MuJoCo world frame is Z-up, so a horizontal shove lives in XY.
            // Shove applies the force itself for _knockDownSeconds.
            var force = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f) * _knockDownNewtons;
            _controller.Shove(force, _knockDownSeconds);
            StartAttempt();
        }

        private void Succeed()
        {
            _successes++;
            float taken = _getUpSeconds - _phaseTimer;
            if (taken < _bestSeconds) { _bestSeconds = taken; }
            Debug.Log($"GETUP_RESULT | attempt {_attempt} | SUCCESS in {taken:F1} s " +
                      $"(up {_upStreak:F1} s of {_stableSeconds:F0} required) | " +
                      $"score {_successes}/{_attempt} | best {(_bestSeconds < float.MaxValue ? _bestSeconds.ToString("F1") : "-")} s");
            BeginStanding(_standSeconds);
        }

        private void Fail()
        {
            Debug.Log($"GETUP_RESULT | attempt {_attempt} | FAIL (still down after {_getUpSeconds:F0} s)" +
                      (_autoStandOnFail ? " -> standing him back up for the next attempt" : " -> leaving him down"));
            if (_autoStandOnFail) { BeginStanding(_standSeconds); }
            else { _phaseTimer = float.MaxValue; }
        }

        private void UpdateReadout()
        {
            if (_readout == null) { return; }
            var text = new StringBuilder();
            text.Append("NICK get-up practice  (MuJoCo Warp -> MuJoCo plugin)\n");
            text.Append("attempt ").Append(_attempt)
                .Append("   success ").Append(_successes);
            if (_bestSeconds < float.MaxValue) { text.Append("   best ").Append(_bestSeconds.ToString("F1")).Append(" s"); }
            text.Append('\n');
            if (_phase == Phase.Standing)
            {
                text.Append("standing — knock-down in ").Append(Mathf.Max(0f, _phaseTimer).ToString("F0")).Append(" s\n");
            }
            else
            {
                text.Append("DOWN — ").Append(Mathf.Max(0f, _phaseTimer).ToString("F0")).Append(" s to rise");
                if (_upStreak > 0f)
                {
                    text.Append("   up-streak ").Append(_upStreak.ToString("F1")).Append('/').Append(_stableSeconds.ToString("F0")).Append(" s");
                }
                text.Append('\n');
            }
            text.Append("success = head above ").Append((_upHeadFraction * 100f).ToString("F0"))
                .Append("% standing for ").Append(_stableSeconds.ToString("F0")).Append(" s\n");
            text.Append("R: stand him up   D: knock down now");
            _readout.text = text.ToString();
        }
    }
}
