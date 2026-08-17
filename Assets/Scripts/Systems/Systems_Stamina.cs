using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Dual-pool biomechanical stamina. Anaerobic drains from estimated joint
    /// motor power (torque proxy x angular speed — never the action vector,
    /// per project rules), recovers toward the aerobic ceiling when motors go
    /// quiet. Depletion attenuates joint springs:
    /// k = base * (0.35 + 0.65 * anaerobic^1.2).
    /// v1 torque proxy: min(maxForce, spring * |w|) — refined in match phase.
    /// </summary>
    [DefaultExecutionOrder(-95)] // after the agent (-100), before the Academy stepper
    public sealed class Systems_Stamina : MonoBehaviour
    {
        private const float SPRING_FLOOR = 0.35f;
        private const float SPRING_RANGE = 0.65f;
        private const float ATTENUATION_EXPONENT = 1.2f;
        private const float REST_RECOVERY_FRACTION = 0.8f;

        [SerializeField] private Systems_FighterRig _rig;
        [SerializeField] private float _drainCoefficient = 0.000004f;
        [SerializeField] private float _regenPerSecond = 0.05f;
        [SerializeField] private float _quietPowerThreshold = 400f;
        [SerializeField] private float _aerobicCouplingFraction = 0.1f;

        private float _anaerobic = 1f;
        private float _aerobic = 1f;
        private float _lastPower;
        private float _lastAppliedScale = -1f;

        public float Anaerobic01 => _anaerobic;
        public float Aerobic01 => _aerobic;

        /// <summary>Motor power estimate from the last FixedUpdate. Used by energy-penalty rewards.</summary>
        public float LastPower => _lastPower;

        // Called by the editor rig tool while building the prefab.
        public void EditorInitialize(Systems_FighterRig rig)
        {
            _rig = rig;
        }

        private void FixedUpdate()
        {
            float power = EstimateMotorPower();
            _lastPower = power;
            float dt = Time.fixedDeltaTime;

            if (power > _quietPowerThreshold)
            {
                float drain = _drainCoefficient * power * dt;
                _anaerobic = Mathf.Max(0f, _anaerobic - drain);
                _aerobic = Mathf.Max(0f, _aerobic - drain * _aerobicCouplingFraction);
            }
            else
            {
                _anaerobic = Mathf.Min(_aerobic, _anaerobic + _regenPerSecond * dt);
            }

            float scale = SPRING_FLOOR + SPRING_RANGE * Mathf.Pow(_anaerobic, ATTENUATION_EXPONENT);
            // 14 slerpDrive struct writes per call — skip when nothing changed.
            if (Mathf.Abs(scale - _lastAppliedScale) > 0.005f)
            {
                _rig.SetSpringScale(scale);
                _lastAppliedScale = scale;
            }
        }

        /// <summary>Corner rest: both pools recover 80% of their current deficit.</summary>
        public void RestRecover()
        {
            _aerobic += REST_RECOVERY_FRACTION * (1f - _aerobic);
            _anaerobic += REST_RECOVERY_FRACTION * (1f - _anaerobic);
            _anaerobic = Mathf.Min(_anaerobic, 1f);
            _aerobic = Mathf.Min(_aerobic, 1f);
        }

        /// <summary>
        /// Clears fatigue. MUST be called before the rig restores motors on
        /// episode reset (project rule: clear fatigue on reset before motors).
        /// </summary>
        public void ResetFatigue()
        {
            _anaerobic = 1f;
            _aerobic = 1f;
            _rig.SetSpringScale(1f);
            _lastAppliedScale = 1f;
        }

        private float EstimateMotorPower()
        {
            float total = 0f;
            var joints = _rig.Joints;
            float springScale = _rig.CurrentSpringScale;
            for (int jointIndex = 0; jointIndex < joints.Count; jointIndex++)
            {
                RigJointEntry entry = joints[jointIndex];
                if (entry.body == null)
                {
                    continue;
                }
                float angularSpeed = entry.body.angularVelocity.magnitude;
                float torqueProxy = Mathf.Min(entry.baseMaxForce, entry.baseSpring * springScale * angularSpeed);
                total += torqueProxy * angularSpeed;
            }
            return total;
        }
    }
}
