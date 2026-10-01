using System.Diagnostics;
using UnityEngine;

namespace PoBox.Sim
{
    /// <summary>
    /// Steps the physics by hand, once per fixed update, after every other script has had its turn.
    ///
    /// Two reasons. Freezing the world for a replay becomes "do not step", with nothing to switch on and
    /// off. And the step can be timed with a stopwatch, which is the only honest physics cost there is to
    /// show: the engine's own profiler markers for the physics update read zero outside the profiler.
    ///
    /// The execution order matters. The brains add their forces and set their drive targets in FixedUpdate;
    /// this has to run after all of them, or a step would be simulated with last step's inputs.
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public class PhysicsStepper : MonoBehaviour
    {
        /// <summary>Milliseconds the physics took in the last rendered frame, all steps together, smoothed.</summary>
        public static float FrameMs { get; private set; }
        /// <summary>Physics steps taken in the last rendered frame.</summary>
        public static int StepsLastFrame { get; private set; }

        long _ticks;
        int _steps;
        SimulationMode _previous;

        void OnEnable()
        {
            _previous = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;
            FrameMs = 0f;
        }

        void OnDisable()
        {
            Physics.simulationMode = _previous;
        }

        void FixedUpdate()
        {
            if (!Bout.SimRunning) return;
            long start = Stopwatch.GetTimestamp();
            Physics.Simulate(Time.fixedDeltaTime);
            _ticks += Stopwatch.GetTimestamp() - start;
            _steps++;
        }

        void Update()
        {
            float ms = (float)(_ticks * 1000.0 / Stopwatch.Frequency);
            FrameMs += (ms - FrameMs) * 0.15f;
            StepsLastFrame = _steps;
            _ticks = 0;
            _steps = 0;
        }
    }
}
