using System.Diagnostics;
using Unity.Profiling;
using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Where the frame goes. The open item on this project is that a contest
    /// with Nick in it runs at about 20 fps on a Pixel 9 Pro where the same
    /// scene without him runs at 60, and the only readout the game had was a
    /// frame counter — which says the frame is slow and nothing about why.
    ///
    /// FOUR NUMBERS, AND THE FIRST TWO ALWAYS WORK.
    ///
    ///   frame     wall-clock milliseconds per frame, smoothed
    ///   fixed     milliseconds per frame spent inside FixedUpdate
    ///   draws     draw calls
    ///   tris      triangles submitted
    ///
    /// The fixed-step figure is the one that matters here and it is measured
    /// rather than sampled from the profiler: a stopwatch is started by a probe
    /// ordered first in FixedUpdate and stopped by this component ordered last,
    /// so the span covers the whole fixed window — MuJoCo's mj_step, PhysX,
    /// every agent's action application and every reward — and the total per
    /// frame is what is reported. That is exactly the quantity
    /// <see cref="Systems_MuJoCoProfile"/> takes three six-second phases to
    /// bracket, available continuously and for free.
    ///
    /// WHY NOT JUST THE PROFILER. ProfilerRecorder's render counters are not
    /// available in a release player — which is the only build that reproduces
    /// the 20 fps. Every recorder here is checked for validity and the ones that
    /// answer are shown; the two measured numbers are always shown. A readout
    /// that works only in a development build would be absent from exactly the
    /// case it was written for.
    ///
    /// IT DOES NOT REPLACE <see cref="Systems_MuJoCoProfile"/>, which toggles
    /// the suspects off in turn and is the only thing that can ATTRIBUTE the
    /// cost between policy inference and physics. This says how much; that says
    /// whose. Both are wanted until the 20 fps is closed.
    ///
    /// Attached by <see cref="Systems_DeviceHud"/>, which draws it. Not a
    /// singleton and does not survive a scene change (project rule).
    /// </summary>
    [DefaultExecutionOrder(32000)]   // last in FixedUpdate: closes the span the probe opened
    public sealed class Systems_PerfTelemetry : MonoBehaviour
    {
        /// <summary>
        /// Exponential smoothing for the frame time. The same constant the HUD's
        /// frame counter uses, so the two numbers move together rather than
        /// disagreeing about the same frame.
        /// </summary>
        private const float SMOOTHING = 0.1f;

        private readonly Stopwatch _fixedWatch = new();
        private double _fixedMillisThisFrame;
        private float _smoothedFixedMillis;
        private float _smoothedFrameMillis;

        private ProfilerRecorder _drawCalls;
        private ProfilerRecorder _triangles;
        private ProfilerRecorder _setPass;

        /// <summary>Milliseconds of wall clock per frame, smoothed.</summary>
        public float FrameMillis => _smoothedFrameMillis;

        /// <summary>Milliseconds per frame spent inside FixedUpdate, smoothed.</summary>
        public float FixedMillis => _smoothedFixedMillis;

        /// <summary>
        /// The fixed step's share of the frame, 0..1. This is the number that
        /// answers the open question: a humanoid's physics should be a couple of
        /// percent of a 16 ms frame, not two thirds of it.
        /// </summary>
        public float FixedShare01 =>
            _smoothedFrameMillis > 0.01f ? Mathf.Clamp01(_smoothedFixedMillis / _smoothedFrameMillis) : 0f;

        /// <summary>Draw calls, or -1 where the counter is unavailable (release players).</summary>
        public long DrawCalls => _drawCalls.Valid ? _drawCalls.LastValue : -1;

        /// <summary>Triangles submitted, or -1 where the counter is unavailable.</summary>
        public long Triangles => _triangles.Valid ? _triangles.LastValue : -1;

        /// <summary>SetPass calls, or -1 where the counter is unavailable.</summary>
        public long SetPassCalls => _setPass.Valid ? _setPass.LastValue : -1;

        /// <summary>
        /// One line for the HUD. Draw counters are appended only when they
        /// answered, so a release build shows a shorter line rather than a line
        /// of "n/a" — the readout has to stay narrow enough for a portrait top
        /// bar, where the title already holds 40% of the width.
        /// </summary>
        public string Summarise()
        {
            string line = $"{_smoothedFrameMillis:0.0}ms · fix {_smoothedFixedMillis:0.0}ms " +
                          $"({FixedShare01 * 100f:0}%)";
            if (DrawCalls >= 0)
            {
                line += $" · {DrawCalls} draws";
            }
            if (Triangles >= 0)
            {
                line += $" · {Triangles / 1000f:0.0}k tris";
            }
            return line;
        }

        private void Awake()
        {
            // The probe has to exist before the first FixedUpdate, and it has to
            // be a separate component because execution order is per component:
            // nothing can be both first and last in the same callback.
            gameObject.AddComponent<FixedStepProbe>().Bind(this);

            _drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            _triangles = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
            _setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
        }

        private void OnDestroy()
        {
            // Recorders hold native memory and are not garbage collected. A
            // scene change makes a new telemetry object, so leaking these would
            // leak one set per scene load for the life of the app.
            _drawCalls.Dispose();
            _triangles.Dispose();
            _setPass.Dispose();
        }

        /// <summary>Called by the probe, first thing in the fixed step.</summary>
        internal void BeginFixedSpan()
        {
            _fixedWatch.Restart();
        }

        /// <summary>
        /// Closes the span. Several fixed steps can run in one frame — that is
        /// the whole shape of the problem when the step is expensive — so they
        /// ACCUMULATE here and the total is taken once per frame in Update.
        /// </summary>
        private void FixedUpdate()
        {
            if (_fixedWatch.IsRunning)
            {
                _fixedWatch.Stop();
                _fixedMillisThisFrame += _fixedWatch.Elapsed.TotalMilliseconds;
            }
        }

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            if (dt > 0f)
            {
                float frameMillis = dt * 1000f;
                _smoothedFrameMillis = _smoothedFrameMillis <= 0f
                    ? frameMillis
                    : Mathf.Lerp(_smoothedFrameMillis, frameMillis, SMOOTHING);
            }

            float fixedMillis = (float)_fixedMillisThisFrame;
            _smoothedFixedMillis = _smoothedFixedMillis <= 0f
                ? fixedMillis
                : Mathf.Lerp(_smoothedFixedMillis, fixedMillis, SMOOTHING);
            _fixedMillisThisFrame = 0d;
        }

        /// <summary>
        /// Opens the fixed-step span. Ordered before everything in the project —
        /// the sensors are at -101, the agent at -100, the rewards at -99 — so
        /// the span contains all of them as well as the physics step itself.
        /// </summary>
        [DefaultExecutionOrder(-32000)]
        private sealed class FixedStepProbe : MonoBehaviour
        {
            private Systems_PerfTelemetry _owner;

            internal void Bind(Systems_PerfTelemetry owner)
            {
                _owner = owner;
            }

            private void FixedUpdate()
            {
                if (_owner != null)
                {
                    _owner.BeginFixedSpan();
                }
            }
        }
    }
}
