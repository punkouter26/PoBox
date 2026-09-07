using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace PoBox.MuJoCoCreature
{
    /// <summary>
    /// Runs Nick through his two behaviours on a timer so the scene
    /// demonstrates itself: hold still under shoves (the balance ring's
    /// command), then walk at pace (the walk race's command), repeating.
    ///
    /// It LOGS one line per phase, NICK_DEMO, with the same columns
    /// Systems_MattDemo logs as MATT_DEMO and Tools/MuJoCo/watch_nick.py
    /// prints in Python. Those three tables side by side are the comparison
    /// this line exists to make: a policy trained in MuJoCo Warp, run in the
    /// MuJoCo Unity plugin, against one trained in Isaac Lab and run in
    /// Unity's PhysX ragdoll. Same reward design, same columns, two simulators.
    ///
    /// Keys (Input System): Space switches phase now, R resets the creature,
    /// P shoves it.
    /// </summary>
    [DefaultExecutionOrder(-40)] // after the controller has read the sim
    public sealed class Systems_NickDemo : MonoBehaviour
    {
        // Matches Systems_MattDemo and watch_matt.py: a stance change every
        // ~35 control steps at 50 Hz is full credit.
        private const float TargetSwitchesPerStep = 1f / 35f;

        [SerializeField] private CreatureSentisController _controller;
        [SerializeField] private float _phaseSeconds = 10f;
        [SerializeField] private float _walkSpeed = 1f;

        [Header("Shoves (balance phase)")]
        [SerializeField] private bool _autoShove = true;
        [SerializeField] private float _shoveIntervalSeconds = 4f;
        [Tooltip("Horizontal force on the pelvis, newtons, held for shoveSeconds. " +
                 "150 N for 0.2 s is a 30 N.s impulse: about 0.4 m/s on a 75 kg body.")]
        [SerializeField] private float _shoveNewtons = 150f;
        [SerializeField] private float _shoveSeconds = 0.2f;

        [Header("Presentation")]
        [SerializeField] private UIDocument _hud;
        [SerializeField] private Transform _camera;
        [SerializeField] private Vector3 _cameraOffset = new Vector3(3.0f, 1.5f, -3.0f);

        private Label _readout;
        private bool _walking;
        private bool _started;
        private float _phaseTimer;
        private float _shoveTimer;
        private int _phaseIndex;

        private Vector3 _phaseStart;        // MuJoCo world frame
        private float _speedSum;
        private int _physicsSteps;
        private int _switches;
        private int _stance;                // -1 left carries, +1 right carries, 0 neither
        private float _clearanceSum;
        private int _downSteps;
        private int _fallsAtPhaseStart;
        private float _footRestZ;
        private float _startHeadZ;

        private void Start()
        {
            if (_controller == null) { _controller = GetComponentInChildren<CreatureSentisController>(); }
            if (_hud != null)
            {
                _readout = new Label { name = "NickReadout" };
                _readout.style.color = Color.white;
                _readout.style.fontSize = 26;
                _readout.style.whiteSpace = WhiteSpace.Normal;
                _readout.style.marginLeft = 16;
                _readout.style.marginTop = 60;
                _hud.rootVisualElement.Add(_readout);
            }
        }

        private void BeginPhase(bool walking)
        {
            LogPhaseIfAny();
            // Canonical pose, velocities zeroed, command re-aimed along the
            // rest heading -- what every training episode starts from.
            _controller.ResetCreature();
            _walking = walking;
            _phaseTimer = 0f;
            _shoveTimer = 0f;
            _speedSum = 0f;
            _physicsSteps = 0;
            _switches = 0;
            _stance = 0;
            _clearanceSum = 0f;
            _downSteps = 0;
            _fallsAtPhaseStart = _controller.DebugResetCount;
            _phaseStart = _controller.DebugPelvisPosition;
            _footRestZ = Mathf.Min(_controller.DebugFootLeftZ, _controller.DebugFootRightZ);
            _startHeadZ = _controller.DebugHeadZ;
            _controller.SetCommand(walking ? _walkSpeed : 0f);
        }

        private void FixedUpdate()
        {
            if (_controller == null || !_controller.IsBound) { return; }
            if (!_started)
            {
                _started = true;
                BeginPhase(false);
                return;
            }

            _phaseTimer += Time.fixedDeltaTime;
            _physicsSteps++;

            Vector3 velocity = _controller.DebugPelvisLinVel;
            _speedSum += Vector3.Dot(new Vector3(velocity.x, velocity.y, 0f), _controller.CommandedDirection);

            bool leftDown = _controller.DebugFootLeftDown;
            bool rightDown = _controller.DebugFootRightDown;
            float clearance = Mathf.Max(_controller.DebugFootLeftZ, _controller.DebugFootRightZ) - _footRestZ;
            _clearanceSum += Mathf.Max(0f, clearance);

            // Alternation counts stance CHANGES: swapping which foot carries
            // the body. Two feet down scores nothing; one leg held scores nothing.
            int stance = 0;
            if (leftDown && !rightDown) { stance = -1; }
            else if (rightDown && !leftDown) { stance = 1; }
            if (stance != 0 && _stance != 0 && stance != _stance) { _switches++; }
            if (stance != 0) { _stance = stance; }

            if (_startHeadZ > 0f && _controller.DebugHeadZ < _startHeadZ * 0.55f)
            {
                _downSteps++;
            }

            if (!_walking && _autoShove && _shoveIntervalSeconds > 0f)
            {
                _shoveTimer += Time.fixedDeltaTime;
                if (_shoveTimer >= _shoveIntervalSeconds)
                {
                    _shoveTimer = 0f;
                    ShoveRandomly();
                }
            }

            UpdateReadout();
            if (_phaseTimer >= _phaseSeconds) { BeginPhase(!_walking); }
        }

        private void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && _started)
            {
                if (keyboard.spaceKey.wasPressedThisFrame) { BeginPhase(!_walking); }
                if (keyboard.rKey.wasPressedThisFrame) { BeginPhase(_walking); }
                if (keyboard.pKey.wasPressedThisFrame) { ShoveRandomly(); }
            }
        }

        private void LateUpdate()
        {
            if (_camera == null || _controller == null || !_controller.IsBound) { return; }
            // MuJoCo (x, y, z-up) -> Unity (x, y-up, z): the plugin places the
            // pelvis MjBody's transform for us, so follow that in Unity space.
            Transform pelvis = _controller.PelvisTransform;
            if (pelvis == null) { return; }
            Vector3 target = pelvis.position + _cameraOffset;
            _camera.position = Vector3.Lerp(_camera.position, target, 1f - Mathf.Exp(-4f * Time.deltaTime));
            _camera.LookAt(pelvis.position + Vector3.up * 0.2f);
        }

        private void ShoveRandomly()
        {
            float angle = Random.Range(0f, 2f * Mathf.PI);
            var force = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f) * _shoveNewtons;
            _controller.Shove(force, _shoveSeconds);
        }

        private int ControlSteps => Mathf.Max(1, _physicsSteps / _controller.Decimation);
        private float MeanSpeed() => _physicsSteps > 0 ? _speedSum / _physicsSteps : 0f;
        private float MeanClearance() => _physicsSteps > 0 ? _clearanceSum / _physicsSteps : 0f;
        private float Distance()
        {
            Vector3 delta = _controller.DebugPelvisPosition - _phaseStart;
            return new Vector2(delta.x, delta.y).magnitude;
        }
        private float Alternation() =>
            _physicsSteps > 0 ? Mathf.Min(1f, (_switches / (float)ControlSteps) / TargetSwitchesPerStep) : 0f;
        private int Falls => _controller.DebugResetCount - _fallsAtPhaseStart;

        private void UpdateReadout()
        {
            if (_readout == null) { return; }
            var text = new StringBuilder();
            text.Append("NICK  MuJoCo Warp -> MuJoCo plugin\n");
            text.Append(_walking ? "WALK" : "BALANCE").Append("   commanded ")
                .Append((_walking ? _walkSpeed : 0f).ToString("0.00")).Append(" m/s\n");
            text.Append("measured  ").Append(MeanSpeed().ToString("0.00")).Append(" m/s\n");
            text.Append("distance  ").Append(Distance().ToString("0.00")).Append(" m\n");
            text.Append("alternation ").Append(Alternation().ToString("0.00"))
                .Append("  (").Append(_switches).Append(" steps)\n");
            text.Append("clearance ").Append(MeanClearance().ToString("0.000")).Append(" m\n");
            text.Append("falls ").Append(Falls).Append("\n");
            text.Append("Space: switch   R: reset   P: shove");
            _readout.text = text.ToString();
        }

        /// <summary>
        /// One line per phase, so a run answers "does the MuJoCo Warp policy
        /// hold up in the Unity plugin" without anyone watching. Compare
        /// against watch_nick.py's table for the same checkpoint in Python,
        /// and against MATT_DEMO for the Isaac line.
        /// </summary>
        private void LogPhaseIfAny()
        {
            if (_physicsSteps == 0) { return; }
            Debug.Log($"NICK_DEMO {_phaseIndex++} | {(_walking ? "WALK" : "BALANCE")} | " +
                      $"cmd={(_walking ? _walkSpeed : 0f):0.00} " +
                      $"measured={MeanSpeed():0.000} m/s " +
                      $"distance={Distance():0.000} m " +
                      $"alternation={Alternation():0.000} " +
                      $"switches={_switches} " +
                      $"clearance={MeanClearance():0.000} m " +
                      $"down={_downSteps / _controller.Decimation}/{ControlSteps} steps " +
                      $"falls={Falls}");
        }
    }
}
