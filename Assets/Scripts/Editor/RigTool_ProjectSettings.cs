using UnityEditor;
using UnityEngine;

namespace PoBox.Editor
{
    /// <summary>
    /// Applies the locked, training-invalidating project settings from the
    /// feature brief. Idempotent — run any time; PhysicsGuard asserts the
    /// same values at runtime.
    /// </summary>
    internal static class RigTool_ProjectSettings
    {
        [MenuItem("Tools/ML Boxing/0. Apply Project Settings")]
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
