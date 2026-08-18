using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace PoBox
{
    /// <summary>
    /// Mandated joint-range verification (project rule): with gravity off and
    /// the pelvis frozen, sweeps every action DOF one at a time and logs the
    /// commanded vs measured parent-local angle at the positive peak. A
    /// FLIPPED result means Systems_FighterRig needs _invertTargetRotation.
    /// Use only in SCN_RIGSTAGE via Tools > ML Boxing > 6.
    /// </summary>
    public sealed class Systems_JointRangeTester : MonoBehaviour
    {
        private const float SIGN_CHECK_MIN_DEGREES = 5f;

        [SerializeField] private Systems_FighterRig _rig;
        [SerializeField] private float _secondsPerDof = 3f;

        private float[] _actions;
        private int _currentDof = -1;
        private float _dofTimer;
        private bool _peakLogged;

        // Called by the editor tool.
        public void EditorInitialize(Systems_FighterRig rig)
        {
            _rig = rig;
        }

        private void Start()
        {
            _actions = new float[_rig.DofCount];
            _rig.Pelvis.useGravity = false;
            _rig.Pelvis.isKinematic = true;
            var joints = _rig.Joints;
            for (int jointIndex = 0; jointIndex < joints.Count; jointIndex++)
            {
                joints[jointIndex].body.useGravity = false;
            }
            AdvanceDof();
            LogInfo($"JointRangeTester: sweeping {_rig.DofCount} DOF, {_secondsPerDof}s each. Watch for FLIPPED lines.");
        }

        private void FixedUpdate()
        {
            if (_currentDof >= _rig.DofCount)
            {
                return;
            }

            _dofTimer += Time.fixedDeltaTime;
            float phase = Mathf.Sin(2f * Mathf.PI * _dofTimer / _secondsPerDof);

            for (int actionIndex = 0; actionIndex < _actions.Length; actionIndex++)
            {
                _actions[actionIndex] = 0f;
            }
            _actions[_currentDof] = phase;
            _rig.ApplyActions(_actions, 0);

            if (!_peakLogged && phase > 0.98f)
            {
                LogPeak();
                _peakLogged = true;
            }

            if (_dofTimer >= _secondsPerDof)
            {
                AdvanceDof();
            }
        }

        private void AdvanceDof()
        {
            _currentDof++;
            _dofTimer = 0f;
            _peakLogged = false;
            if (_currentDof >= _rig.DofCount)
            {
                LogInfo("JointRangeTester: sweep complete. If any DOF was FLIPPED, enable Invert Target Rotation on Systems_FighterRig, rebuild the prefab, and re-run.");
                enabled = false;
            }
        }

        private void LogPeak()
        {
            if (!TryResolveDof(_currentDof, out RigJointEntry entry, out int axis, out float commanded))
            {
                return;
            }
            Vector3 localEuler = entry.body.transform.localEulerAngles;
            float measured = NormalizeAngle(axis == 0 ? localEuler.x : axis == 1 ? localEuler.y : localEuler.z);
            string axisName = axis == 0 ? "pitch/X" : axis == 1 ? "roll/Y" : "yaw/Z";
            string verdict = Mathf.Abs(commanded) < SIGN_CHECK_MIN_DEGREES || Mathf.Abs(measured) < SIGN_CHECK_MIN_DEGREES
                ? "inconclusive (small range)"
                : Mathf.Sign(commanded) == Mathf.Sign(measured) ? "OK" : "FLIPPED";
            LogInfo($"JointRangeTester: {entry.body.name} {axisName} commanded {commanded:F1} measured {measured:F1} -> {verdict}");
        }

        [Conditional("UNITY_EDITOR")]
        private static void LogInfo(string message)
        {
            Debug.Log(message);
        }

        private bool TryResolveDof(int dofIndex, out RigJointEntry entry, out int axis, out float peakAngle)
        {
            var joints = _rig.Joints;
            int cursor = 0;
            for (int jointIndex = 0; jointIndex < joints.Count; jointIndex++)
            {
                RigJointEntry candidate = joints[jointIndex];
                if (candidate.hasPitch && cursor++ == dofIndex)
                {
                    entry = candidate; axis = 0; peakAngle = candidate.pitchHigh; return true;
                }
                if (candidate.hasRoll && cursor++ == dofIndex)
                {
                    entry = candidate; axis = 1; peakAngle = candidate.rollHigh; return true;
                }
                if (candidate.hasYaw && cursor++ == dofIndex)
                {
                    entry = candidate; axis = 2; peakAngle = candidate.yawHigh; return true;
                }
            }
            entry = null; axis = 0; peakAngle = 0f;
            return false;
        }

        private static float NormalizeAngle(float degrees)
        {
            return degrees > 180f ? degrees - 360f : degrees;
        }
    }
}
