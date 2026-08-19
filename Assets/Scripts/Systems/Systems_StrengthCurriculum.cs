using Unity.MLAgents;
using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Curriculum hook for drive strength: re-reads the environment parameter
    /// "strength_scale" at every episode start and scales all joint drives
    /// (spring and force cap) through the rig. Lets a config teach balance at
    /// reduced power before granting full strength — or grant a weak rig extra
    /// power early and anneal it back to 1. Absent parameter = 1 (authored
    /// strength, no effect). Training-scene tool only.
    /// </summary>
    [DefaultExecutionOrder(-98)] // after agent/reward, before the Academy stepper
    public sealed class Systems_StrengthCurriculum : MonoBehaviour
    {
        private const string STRENGTH_ENV_PARAM = "strength_scale";

        [SerializeField] private Systems_FighterRig _rig;
        [SerializeField] private Agent_FighterBoxing _agent;

        private int _lastStepCount;
        private float _appliedScale = 1f;
        private bool _initialized;

        // Called by the editor scene builder.
        public void EditorInitialize(Systems_FighterRig rig, Agent_FighterBoxing agent)
        {
            _rig = rig;
            _agent = agent;
        }

        private void FixedUpdate()
        {
            int stepCount = _agent.StepCount;
            bool episodeBegan = stepCount < _lastStepCount;
            _lastStepCount = stepCount;
            if (!episodeBegan && _initialized)
            {
                return;
            }
            _initialized = true;

            float scale = Academy.Instance.EnvironmentParameters.GetWithDefault(STRENGTH_ENV_PARAM, 1f);
            if (!Mathf.Approximately(scale, _appliedScale))
            {
                _rig.SetStrengthScale(scale);
                _appliedScale = scale;
            }
        }
    }
}
