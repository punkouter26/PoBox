using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace PoBox.MuJoCoCreature
{
    /// <summary>
    /// Nick's own balance ring: hold station under shoves that get harder every
    /// round, until he goes down.
    ///
    /// WHY THIS EXISTS RATHER THAN A ROSTER ENTRY IN SCN_TEST_BALANCE_CONTEST.
    /// Nick cannot share that scene, and both halves of that were measured on
    /// 2026-09-08 rather than assumed:
    ///
    ///   * Run Nick at the ring's 0.02 s and his brain scores a 1.06 s median
    ///     against a 1.08 s PASSIVE baseline -- no authority at all. Neither
    ///     integrator, armature, solref nor solver iterations recovered it.
    ///   * Run the ring at Nick's 0.005 s -- with Systems_ContestSpawner
    ///     compensating DecisionPeriod so the PhysX brains still decide at
    ///     50 Hz -- and Standard falls from 30.0 s to 2.9 s. The heuristic PD
    ///     bot got BETTER (2.8 -> 4.0 s), which is the tell: the physics is
    ///     fine, the learned policies are simply out of distribution.
    ///
    /// So one scene cannot hold both, and this is the half that needs no
    /// retraining. It logs CONTEST_ROUND in the same shape
    /// Systems_BalanceContest does, so the same grep verifies both rings.
    ///
    /// Keys: R resets the round, P shoves now.
    /// </summary>
    [DefaultExecutionOrder(-40)] // after the controller has read the sim
    public sealed class Systems_NickBalanceRing : MonoBehaviour
    {
        [SerializeField] private CreatureSentisController _controller;

        [Header("Round")]
        [Tooltip("Round ends at this many seconds even if he never goes down. " +
                 "30 s matches Systems_BalanceContest.ROUND_SECONDS so the two " +
                 "rings' CONTEST_ROUND lines mean the same thing.")]
        [SerializeField] private float _roundSeconds = 30f;
        [SerializeField] private float _restartSeconds = 2f;

        [Header("Shoves — harder every round")]
        [Tooltip("Round 1 uses this force; every later round adds _shoveStepNewtons. " +
                 "Measured on nick06: 99% of worlds survive a full 30 s at 150 and " +
                 "250 N, 80% at 350, 75% at 450.")]
        [SerializeField] private float _shoveBaseNewtons = 150f;
        [SerializeField] private float _shoveStepNewtons = 100f;
        [SerializeField] private float _shoveIntervalSeconds = 4f;
        [SerializeField] private float _shoveSeconds = 0.2f;

        [Header("Presentation")]
        [SerializeField] private UIDocument _hud;

        private Label _readout;
        private int _round;
        private float _roundTimer;
        private float _shoveTimer;
        private float _restartTimer = -1f;
        private bool _down;
        private float _startHeadZ;
        private int _fallsAtRoundStart;
        private float _lastShoveNewtons;

        private void Start()
        {
            if (_controller == null) { _controller = GetComponentInChildren<CreatureSentisController>(); }
            if (_hud != null)
            {
                _readout = new Label { name = "NickRingReadout" };
                _readout.style.color = Color.white;
                _readout.style.fontSize = 26;
                _readout.style.whiteSpace = WhiteSpace.Normal;
                _readout.style.marginLeft = 16;
                _readout.style.marginTop = 60;
                _hud.rootVisualElement.Add(_readout);
            }
        }

        private float ShoveNewtons => _shoveBaseNewtons + Mathf.Max(0, _round - 1) * _shoveStepNewtons;

        private void BeginRound()
        {
            _round++;
            // Canonical pose, velocities zeroed -- the same state every training
            // episode starts from, so round one is not a harder round than the rest.
            _controller.ResetCreature();
            _controller.SetCommand(0f);          // balance ring: hold station
            _roundTimer = 0f;
            _shoveTimer = 0f;
            _restartTimer = -1f;
            _down = false;
            _fallsAtRoundStart = _controller.DebugResetCount;
            _startHeadZ = _controller.DebugHeadZ;
            _lastShoveNewtons = 0f;
        }

        private void FixedUpdate()
        {
            if (_controller == null || !_controller.IsBound) { return; }
            if (_round == 0) { BeginRound(); return; }

            if (_restartTimer >= 0f)
            {
                _restartTimer -= Time.fixedDeltaTime;
                if (_restartTimer <= 0f) { BeginRound(); }
                UpdateReadout();
                return;
            }

            _roundTimer += Time.fixedDeltaTime;

            // Same collapse test as Systems_NickDemo and Systems_BalanceContest:
            // head below 55% of the height it started the round at. MuJoCo is
            // Z-up, hence DebugHeadZ rather than a transform's y.
            bool collapsed = _startHeadZ > 0f && _controller.DebugHeadZ < _startHeadZ * 0.55f;
            bool selfReset = _controller.DebugResetCount > _fallsAtRoundStart;
            if (!_down && (collapsed || selfReset))
            {
                _down = true;
                EndRound("down");
                return;
            }

            if (_roundTimer >= _roundSeconds)
            {
                EndRound("time up");
                return;
            }

            _shoveTimer += Time.fixedDeltaTime;
            if (_shoveIntervalSeconds > 0f && _shoveTimer >= _shoveIntervalSeconds)
            {
                _shoveTimer = 0f;
                ShoveNow();
            }

            if (Keyboard.current != null)
            {
                if (Keyboard.current.rKey.wasPressedThisFrame) { BeginRound(); }
                if (Keyboard.current.pKey.wasPressedThisFrame) { ShoveNow(); }
            }

            UpdateReadout();
        }

        private void ShoveNow()
        {
            float angle = Random.Range(0f, Mathf.PI * 2f);
            // MuJoCo world frame is Z-up, so a horizontal shove lives in XY.
            var force = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f) * ShoveNewtons;
            _lastShoveNewtons = ShoveNewtons;
            _controller.Shove(force, _shoveSeconds);
        }

        /// <summary>
        /// One greppable line per round, deliberately the same shape
        /// Systems_BalanceContest emits, so a headless run of EITHER ring is
        /// verified by grepping CONTEST_ROUND. The shove force is included
        /// because unlike the PhysX ring this one escalates, so the alive time
        /// means nothing without it.
        /// </summary>
        private void EndRound(string reason)
        {
            Debug.Log($"CONTEST_ROUND {_round} | {reason} | shove={ShoveNewtons:F0}N | " +
                      $"Nick={_roundTimer:F1}s{(_down ? "(down)" : "(up)")}");
            _restartTimer = _restartSeconds;
            UpdateReadout();
        }

        private void UpdateReadout()
        {
            if (_readout == null) { return; }
            var text = new StringBuilder();
            text.Append("NICK balance ring  (MuJoCo Warp -> MuJoCo plugin)\n");
            text.Append("round ").Append(_round).Append('\n');
            text.Append("shove ").Append(ShoveNewtons.ToString("F0")).Append(" N every ")
                .Append(_shoveIntervalSeconds.ToString("F0")).Append(" s\n");
            text.Append("alive ").Append(_roundTimer.ToString("F1")).Append(" s of ")
                .Append(_roundSeconds.ToString("F0")).Append('\n');
            text.Append(_down ? "DOWN\n" : "up\n");
            if (_lastShoveNewtons > 0f)
            {
                text.Append("last shove ").Append(_lastShoveNewtons.ToString("F0")).Append(" N\n");
            }
            text.Append("R: reset round   P: shove");
            _readout.text = text.ToString();
        }
    }
}
