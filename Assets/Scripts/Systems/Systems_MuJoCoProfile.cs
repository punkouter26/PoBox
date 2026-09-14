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
        private bool _gaveUp;
        private int _resolveAttempts;

        /// <summary>Attempts to find the creature before accepting there is none.</summary>
        private const int RESOLVE_ATTEMPTS = 40;

        /// <summary>Command-line switch that turns this on. See <see cref="Enabled"/>.</summary>
        private const string SWITCH = "-mjprofile";

        /// <summary>
        /// Whether the profile runs at all. FALSE IN A NORMAL SESSION: only a
        /// player launched with <c>-mjprofile</c> turns it on.
        ///
        /// IT SWITCHES OFF LIVE GAMEPLAY, which is acceptable for a measurement
        /// and not for anything else. The three phases disable the creature's
        /// controller for two thirds of every cycle and its physics for a third,
        /// so a contest holding him plays with a body that is un-controlled for
        /// twelve seconds out of eighteen and not simulated at all for six of
        /// them — on a loop, for the whole session. That is what this did in
        /// every build and every contest, because nothing ever set Enabled to
        /// false. It also destroys the very thing this project judges a policy
        /// by, which is watching how the creature moves.
        ///
        /// Opting IN rather than out is the choice
        /// <see cref="Systems_EvalHarness"/> already makes for its own
        /// command-line install, and for the same reason: a measurement and the
        /// thing being measured cannot honestly run at the same time.
        /// </summary>
        public static bool Enabled { get; set; } = RequestedOnCommandLine();

        private static bool RequestedOnCommandLine()
        {
            string[] arguments = System.Environment.GetCommandLineArgs();
            for (int index = 0; index < arguments.Length; index++)
            {
                if (string.Equals(arguments[index], SWITCH, System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Latest attribution, for the debug panel. Empty until it has data.</summary>
        public static string Summary { get; private set; } = string.Empty;

        private void Start()
        {
            if (!Enabled)
            {
                // Disabled at the component rather than short-circuited in
                // Update: a disabled MonoBehaviour costs nothing at all, and this
                // one must not be able to touch the creature by any path.
                enabled = false;
                return;
            }
            _phaseStart = Time.unscaledTime;
            Resolve();
        }

        /// <summary>
        /// Finds the creature by type name, retried briefly because a scene can
        /// create it a frame or two after this component starts — and then GIVES
        /// UP RATHER THAN RETRYING FOREVER.
        ///
        /// Both ships' scenes are handed this component unconditionally, and a
        /// contest with no creature in it has nothing to attribute. The unbounded
        /// version of this ran a full FindObjectsByType over every MonoBehaviour
        /// in the scene on EVERY FRAME, allocating an array each time, for the
        /// whole session. Forty attempts is under a second at 50 Hz — longer than
        /// any scene here takes to build its creature, and short enough not to be
        /// noticed.
        /// </summary>
        private void Resolve()
        {
            if (_resolved) { return; }
            if (_resolveAttempts++ >= RESOLVE_ATTEMPTS)
            {
                _resolved = true;
                _gaveUp = true;
                return;
            }
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
            if (_gaveUp || !_resolved) { return; }

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
