using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Asserts the training-invalidating physics constants at startup.
    /// Brains are fitted against these exact values; drift silently changes
    /// dynamics, so any mismatch HALTS the app — an error line scrolling past
    /// during an unattended overnight run helps nobody.
    /// </summary>
    internal static class Systems_PhysicsGuard
    {
        private const float REQUIRED_FIXED_DELTA = 0.02f;
        private const int REQUIRED_SOLVER_ITERATIONS = 16;
        private const int REQUIRED_SOLVER_VELOCITY_ITERATIONS = 16;
        private const float REQUIRED_GRAVITY_Y = -9.81f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Verify()
        {
            bool valid = true;
            if (!Mathf.Approximately(Time.fixedDeltaTime, REQUIRED_FIXED_DELTA))
            {
                Debug.LogError($"PhysicsGuard: fixedDeltaTime is {Time.fixedDeltaTime}, must be {REQUIRED_FIXED_DELTA}. Trained brains are invalid at this rate.");
                valid = false;
            }
            if (Physics.defaultSolverIterations != REQUIRED_SOLVER_ITERATIONS)
            {
                Debug.LogError($"PhysicsGuard: solver iterations {Physics.defaultSolverIterations}, must be {REQUIRED_SOLVER_ITERATIONS}. Run Tools > ML Boxing > 0. Apply Project Settings.");
                valid = false;
            }
            if (Physics.defaultSolverVelocityIterations != REQUIRED_SOLVER_VELOCITY_ITERATIONS)
            {
                Debug.LogError($"PhysicsGuard: solver velocity iterations {Physics.defaultSolverVelocityIterations}, must be {REQUIRED_SOLVER_VELOCITY_ITERATIONS}. Run Tools > ML Boxing > 0. Apply Project Settings.");
                valid = false;
            }
            if (!Mathf.Approximately(Physics.gravity.y, REQUIRED_GRAVITY_Y))
            {
                Debug.LogError($"PhysicsGuard: gravity.y is {Physics.gravity.y}, must be {REQUIRED_GRAVITY_Y}.");
                valid = false;
            }
            if (valid)
            {
                return;
            }
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit(1);
#endif
        }
    }
}
