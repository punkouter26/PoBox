using System.Text;
using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// On-device attribution for Nick's frame cost, because the number needed
    /// explaining rather than tuning: enabling MuJoCo took the balance contest
    /// from 60 fps to 20 on a Pixel 9 Pro, and `mj_step` on a 30-DoF humanoid
    /// should be a couple of percent of a 16 ms frame, not two thirds of it.
    ///
    /// A profiler connection over adb is the "right" tool and is also fiddly on
    /// a release build, so this measures the only way that cannot be argued
    /// with: it turns the suspects off in turn and reads the frame rate.
    ///
    ///   FULL      controller + MjScene            (what ships)
    ///   NO_POLICY controller off, MjScene stepping (physics only)
    ///   NO_MUJOCO both off                         (the 60 fps baseline)
    ///
    /// FULL - NO_POLICY is the policy inference cost; NO_POLICY - NO_MUJOCO is
    /// the physics-plus-transform-sync cost. Delete this file once the answer
    /// is in; it is a measurement, not a feature.
    /// </summary>
    [DefaultExecutionOrder(600)]
    public sealed class Systems_MuJoCoProfile : MonoBehaviour
    {
        private const float PHASE_SECONDS = 6f;
        private const float WARMUP_SECONDS = 1.5f;   // ignore the frames right after a toggle

        private enum Phase { Full = 0, NoPolicy = 1, NoMujoco = 2 }

        private readonly float[] _sum = new float[3];
        private readonly int[] _count = new int[3];
        private Phase _phase = Phase.Full;
        private float _phaseStart;
        private MonoBehaviour _controller;
        // Found by type NAME, not by type: PoBox.Runtime deliberately does not
        // reference the MuJoCo plugin assembly (CLAUDE.md -- the shipping game
        // must not have to link it), so `using Mujoco;` here would both break
        // that rule and fail to compile.
        private MonoBehaviour _scene;
        private bool _resolved;

        public static bool Enabled { get; set; } = true;

        /// <summary>Latest attribution, for the debug panel. Empty until it has data.</summary>
        public static string Summary { get; private set; } = string.Empty;

        private void Start()
        {
            _phaseStart = Time.unscaledTime;
            Resolve();
        }

        private void Resolve()
        {
            if (_resolved) { return; }
            foreach (MonoBehaviour behaviour in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (behaviour == null) { continue; }
                string type = behaviour.GetType().Name;
                if (type == "CreatureSentisController") { _controller = behaviour; }
                else if (type == "MjScene") { _scene = behaviour; }
            }
            _resolved = _controller != null || _scene != null;
        }

        private void Update()
        {
            if (!Enabled) { return; }
            Resolve();
            if (!_resolved) { return; }

            float elapsed = Time.unscaledTime - _phaseStart;
            float dt = Time.unscaledDeltaTime;
            if (elapsed > WARMUP_SECONDS && dt > 0f)
            {
                _sum[(int)_phase] += 1f / dt;
                _count[(int)_phase]++;
            }

            if (elapsed < PHASE_SECONDS) { return; }

            _phase = (Phase)(((int)_phase + 1) % 3);
            _phaseStart = Time.unscaledTime;
            Apply(_phase);
            Publish();
        }

        private void Apply(Phase phase)
        {
            bool policy = phase == Phase.Full;
            bool mujoco = phase != Phase.NoMujoco;
            if (_controller != null) { _controller.enabled = policy; }
            // MjScene owns the step; disabling the component stops mj_step and
            // the transform write-back together, which is the pair we want to
            // price against the baseline.
            if (_scene != null) { _scene.enabled = mujoco; }
        }

        private void Publish()
        {
            var text = new StringBuilder("MJ PROFILE ");
            for (int i = 0; i < 3; i++)
            {
                string name = i == 0 ? "full" : i == 1 ? "no-policy" : "no-mujoco";
                float fps = _count[i] > 0 ? _sum[i] / _count[i] : 0f;
                if (i > 0) { text.Append("  "); }
                text.Append($"{name} {fps:0.0}");
            }
            Summary = text.ToString();
            Debug.Log(Summary);
        }

        private void OnDisable()
        {
            // Never leave the creature switched off behind us.
            if (_controller != null) { _controller.enabled = true; }
            if (_scene != null) { _scene.enabled = true; }
        }
    }
}
