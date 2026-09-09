using UnityEditor;
using UnityEngine;

namespace PoBox.Editor
{
    /// <summary>
    /// Applies the locked, training-invalidating project settings from the
    /// feature brief. Idempotent — run any time; PhysicsGuard asserts the
    /// same values at runtime.
    /// </summary>
    internal static class Editor_ProjectSettings
    {
/// <summary>
        /// EXPERIMENT: drop the whole scene to MuJoCo's 0.005 s step, which is
        /// what a shared PhysX + MuJoCo contest scene would have to run at.
        /// Systems_ContestSpawner compensates DecisionPeriod so the PhysX
        /// brains still decide at 50 Hz. Undo with Apply().
        /// </summary>
        public static void ApplyFineTimestep()
        {
            Time.fixedDeltaTime = 0.005f;
            Physics.gravity = new Vector3(0f, -9.81f, 0f);
            Physics.defaultSolverIterations = 16;
            Physics.defaultSolverVelocityIterations = 16;
            Debug.Log("RigTool: fixedDeltaTime = 0.005 (shared-scene experiment).");
        }

                [MenuItem("PoBox/Apply Project Settings")]
        public static void Apply()
        {
            Time.fixedDeltaTime = 0.02f;
            Physics.gravity = new Vector3(0f, -9.81f, 0f);
            Physics.defaultSolverIterations = 16;
            Physics.defaultSolverVelocityIterations = 16;

            PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;

            int originalQualityLevel = QualitySettings.GetQualityLevel();
            for (int qualityIndex = 0; qualityIndex < QualitySettings.names.Length; qualityIndex++)
            {
                QualitySettings.SetQualityLevel(qualityIndex, false);
                QualitySettings.vSyncCount = 0;
            }
            QualitySettings.SetQualityLevel(originalQualityLevel, false);

            AssetDatabase.SaveAssets();
            Debug.Log("RigTool: project settings applied — dt 0.02, solver 16/16, gravity -9.81, portrait, vsync off.");
        }
    }
}
