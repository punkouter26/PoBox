using System;
using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Command-line switches for a headless training player, applied after the
    /// scene loads.
    ///
    /// WHY THIS EXISTS. SCN_TRAIN_LOCOMOTION disables every fighter's
    /// Systems_Shover, deliberately: shoves are balance stressors and they fight
    /// the walking signal while the speed curriculum is still coming up. That is
    /// the right default for the walk line and the wrong one for the balance
    /// line, because SCN_TEST_BALANCE_CONTEST runs hazards and shoves. A brain
    /// trained only on undisturbed standing has never once had to recover from a
    /// push, and then the game pushes it.
    ///
    /// Turning that into a second scene would mean a second generated artifact
    /// to keep in step with the first, and this project has already been bitten
    /// by a training scene drifting from the tool that builds it. A switch on
    /// the player leaves ONE scene and lets the run decide:
    ///
    ///   mlagents-learn Config/BoxerLocomotion27.yaml --run-id=... \
    ///       --env EnvBuild5/PoBoxTrain.exe --env-args -trainShove 1
    ///
    /// The force itself is still the shover's own "shove_force_max" environment
    /// parameter, so a config can ramp it through a curriculum exactly like the
    /// commanded speed. This switch only decides whether the component runs at
    /// all.
    /// </summary>
    internal static class Systems_TrainingOptions
    {
        private const string SHOVE_FLAG = "-trainShove";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Apply()
        {
            if (!HasFlag(SHOVE_FLAG))
            {
                return;
            }
            Systems_Shover[] shovers =
                UnityEngine.Object.FindObjectsByType<Systems_Shover>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int shoverIndex = 0; shoverIndex < shovers.Length; shoverIndex++)
            {
                shovers[shoverIndex].enabled = true;
            }
            Debug.Log($"TrainingOptions: {SHOVE_FLAG} enabled {shovers.Length} shovers. " +
                "Force comes from the 'shove_force_max' environment parameter; 0 disables it.");
        }

        private static bool HasFlag(string flag)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int argIndex = 0; argIndex < args.Length; argIndex++)
            {
                if (string.Equals(args[argIndex], flag, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
