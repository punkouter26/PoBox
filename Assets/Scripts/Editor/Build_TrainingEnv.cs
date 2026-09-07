// filepath: Assets/Scripts/Editor/Build_TrainingEnv.cs
//
// Headless ML-Agents training environment. Built so `mlagents-learn` can drive
// N copies of the player instead of the single running Editor:
//
//   mlagents-learn Config\BoxerLocomotion12.yaml --run-id=boxer_locomotion12 `
//                  --env=EnvBuild\PoBoxTrain.exe --num-envs=3 --no-graphics
//
// Measured on the 6-core i7-10750H this project runs on: the Editor sustains
// ~1,400 steps/s with total CPU load around 30%, because one env's physics is
// main-thread bound and cannot use the other cores. Three player instances fill
// them. Past three, 3.6 GB of free RAM and laptop thermals take the win back.
//
// Invoked from the Editor menu, from `eval`, or in batch mode via:
//   Unity.exe -batchmode -quit -projectPath . -buildTarget Win64 \
//             -executeMethod PoBox.Editor.Build_TrainingEnv.Build \
//             -buildOutput EnvBuild

using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PoBox.Editor
{
    /// <summary>
    /// Standalone player containing only a training scene. Deliberately not the
    /// game build: no menu, no contest scene, nothing but the scene the trainer
    /// attaches to.
    /// </summary>
    internal static class Build_TrainingEnv
    {
        private const string DefaultOutput = "EnvBuild";
        private const string ExeName = "PoBoxTrain.exe";

        /// <summary>
        /// The scene the env contains by default. Training scenes are headless
        /// by project rule — no cameras, HUD or audio — which is what makes
        /// them safe to run under -nographics without changes.
        /// </summary>
        private const string TrainingScene = "Assets/Scenes/SCN_TRAIN_LOCOMOTION.unity";
        private const string RaptorTrainingScene = "Assets/Scenes/SCN_TRAIN_BALANCE_RAPTOR.unity";

        [MenuItem("Tools/ML Boxing/Build Headless Training Env")]
        public static void BuildFromMenu()
        {
            BuildResult result = Build(DefaultOutput, TrainingScene);
            if (result != BuildResult.Succeeded)
            {
                throw new Exception($"Training env build failed: {result}");
            }
            EditorUtility.RevealInFinder(Path.GetFullPath(DefaultOutput));
        }

        [MenuItem("Tools/ML Boxing/Build Headless Raptor Training Env")]
        public static void BuildRaptorFromMenu()
        {
            BuildResult result = Build(DefaultOutput, RaptorTrainingScene);
            if (result != BuildResult.Succeeded)
            {
                throw new Exception($"Raptor training env build failed: {result}");
            }
            EditorUtility.RevealInFinder(Path.GetFullPath(DefaultOutput));
        }

        /// <summary>Entry point for batch / CLI invocation.</summary>
        public static void Build()
        {
            if (Build(ResolveOutputDir(), TrainingScene) != BuildResult.Succeeded)
            {
                EditorApplication.Exit(1);
            }
        }

        public static BuildResult Build(string outputDir, string trainingScene)
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(trainingScene) == null)
            {
                Debug.LogError($"Training scene not found at {trainingScene}.");
                return BuildResult.Failed;
            }

            // The trainer starts many copies of this player and kills them at the
            // end of a run. Without runInBackground the unfocused instances stop
            // stepping and the whole fleet stalls behind whichever one has focus.
            PlayerSettings.runInBackground = true;

            string absoluteOut = Path.GetFullPath(outputDir);
            if (!TryClearOutput(absoluteOut, ExeName))
            {
                return BuildResult.Failed;
            }

            var options = new BuildPlayerOptions
            {
                scenes = new[] { trainingScene },
                locationPathName = Path.Combine(absoluteOut, ExeName),
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                // Development builds carry the profiler and a slower player
                // loop. This env exists to be fast, and nothing reads its logs.
                options = BuildOptions.None,
            };

            Debug.Log($"Building headless training env to {absoluteOut}.");
            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;
            Debug.Log($"Training env build {summary.result} in {summary.totalTime}. " +
                      $"Size: {summary.totalSize} bytes, errors: {summary.totalErrors}.");
            return summary.result;
        }


        /// <summary>
        /// Empties the output directory, or explains clearly why it could not.
        ///
        /// Windows keeps a running .exe and its DLLs open, so a player left over
        /// from an earlier run makes Directory.Delete throw
        /// UnauthorizedAccessException on something like 'dstorage.dll'. That
        /// surfaced as "executeMethod ... threw exception" with the real cause
        /// forty lines further up the log, which is a poor way to learn that a
        /// stray process is holding the folder.
        /// </summary>
        private static bool TryClearOutput(string absoluteOut, string exeName)
        {
            if (Directory.Exists(absoluteOut))
            {
                try
                {
                    Directory.Delete(absoluteOut, recursive: true);
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException || exception is IOException)
                {
                    Debug.LogError($"Cannot clear {absoluteOut}: {exception.Message}. Something is holding a " +
                        $"file open there — almost always a {exeName} still running from an earlier run. " +
                        "Stop it and build again.");
                    return false;
                }
            }
            Directory.CreateDirectory(absoluteOut);
            return true;
        }

        private static string ResolveOutputDir()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "-buildOutput", StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }
            return DefaultOutput;
        }
    }
}
