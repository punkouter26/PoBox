// filepath: Assets/Scripts/Editor/Build_EvalEnv.cs
//
// Player that benchmarks a baked .onnx instead of training one. See
// Systems_EvalHarness for why a separate measurement path is needed at all.
//
//   Unity.exe -batchmode -quit -nographics -projectPath . -buildTarget Win64 \
//             -executeMethod PoBox.Editor.Build_EvalEnv.Build -buildOutput EvalBuild
//
//   EvalBuild\PoBoxEval.exe -batchmode -nographics \
//             -evalBrain Locomotion_gen20 -evalEpisodes 4 -evalSpeed 0 \
//             -evalOutput eval\gen20.json
//
// DELIBERATELY A SECOND OUTPUT DIRECTORY, not a rebuild of EnvBuild. Training
// runs for hours off EnvBuild\PoBoxTrain.exe and Windows holds a running exe
// open, so rebuilding in place would fail — and the whole point of an
// evaluation is to run it against the brains a training run is producing while
// that run is still going.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PoBox.Editor
{
    /// <summary>
    /// Standalone player containing the locomotion training scene plus every
    /// trained brain on disk, staged into Resources so one build can evaluate
    /// any of them by name.
    /// </summary>
    internal static class Build_EvalEnv
    {
        private const string DefaultOutput = "EvalBuild";
        private const string ExeName = "PoBoxEval.exe";
        private const string EvalScene = "Assets/Scenes/SCN_TRAIN_LOCOMOTION.unity";
        private const string AgentsFolder = "Assets/Agents";
        // Unity can only load a ModelAsset at runtime from a Resources folder —
        // the InferenceEngine's ONNX importer is editor-only, so reading a
        // .onnx off disk in a player is not an option.
        private const string StagingFolder = "Assets/Resources/EvalBrains";

        [MenuItem("Tools/ML Boxing/Build Brain Evaluation Env")]
        public static void BuildFromMenu()
        {
            if (Build(DefaultOutput) != BuildResult.Succeeded)
            {
                throw new Exception("Evaluation env build failed.");
            }
            EditorUtility.RevealInFinder(Path.GetFullPath(DefaultOutput));
        }

        /// <summary>Entry point for batch / CLI invocation.</summary>
        public static void Build()
        {
            if (Build(ResolveOutputDir()) != BuildResult.Succeeded)
            {
                EditorApplication.Exit(1);
            }
        }

        public static BuildResult Build(string outputDir)
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(EvalScene) == null)
            {
                Debug.LogError($"Evaluation scene not found at {EvalScene}.");
                return BuildResult.Failed;
            }

            List<string> staged = StageBrains();
            try
            {
                if (staged.Count == 0)
                {
                    Debug.LogWarning($"Build_EvalEnv: no .onnx found under {AgentsFolder}. The player will " +
                        "only be able to evaluate '-evalBrain heuristic'.");
                }
                else
                {
                    Debug.Log($"Build_EvalEnv: staged {staged.Count} brains: {string.Join(", ", staged)}");
                }

                PlayerSettings.runInBackground = true;
                string absoluteOut = Path.GetFullPath(outputDir);
                if (Directory.Exists(absoluteOut))
                {
                    Directory.Delete(absoluteOut, recursive: true);
                }
                Directory.CreateDirectory(absoluteOut);

                var options = new BuildPlayerOptions
                {
                    scenes = new[] { EvalScene },
                    locationPathName = Path.Combine(absoluteOut, ExeName),
                    target = BuildTarget.StandaloneWindows64,
                    targetGroup = BuildTargetGroup.Standalone,
                    options = BuildOptions.None,
                };

                BuildReport report = BuildPipeline.BuildPlayer(options);
                BuildSummary summary = report.summary;
                Debug.Log($"Evaluation env build {summary.result} in {summary.totalTime}. " +
                          $"Errors: {summary.totalErrors}.");
                return summary.result;
            }
            finally
            {
                // The staging folder is a BUILD ARTIFACT, not source. Leaving it
                // behind would put a second copy of every brain in the repo and,
                // worse, drag all of them into the shipped Android and WebGL
                // players — anything under a Resources folder is included in
                // every build whether or not a scene references it.
                UnstageBrains();
            }
        }

        /// <summary>
        /// Copies every brain under Assets/Agents into the Resources staging
        /// folder. Returns the resource names the player can ask for.
        /// </summary>
        private static List<string> StageBrains()
        {
            UnstageBrains();
            Directory.CreateDirectory(StagingFolder);
            AssetDatabase.Refresh();

            var staged = new List<string>();
            string[] guids = AssetDatabase.FindAssets("t:Object", new[] { AgentsFolder });
            for (int guidIndex = 0; guidIndex < guids.Length; guidIndex++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[guidIndex]);
                if (!path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string brainName = Path.GetFileNameWithoutExtension(path);
                string destination = $"{StagingFolder}/{brainName}.onnx";
                if (AssetDatabase.CopyAsset(path, destination))
                {
                    staged.Add(brainName);
                }
                else
                {
                    Debug.LogWarning($"Build_EvalEnv: could not stage {path}.");
                }
            }
            AssetDatabase.Refresh();
            return staged;
        }

        private static void UnstageBrains()
        {
            if (AssetDatabase.IsValidFolder(StagingFolder))
            {
                AssetDatabase.DeleteAsset(StagingFolder);
            }
            // Only if this build created it: a project that legitimately uses
            // Resources for something else must keep it.
            if (AssetDatabase.IsValidFolder("Assets/Resources") &&
                Directory.GetFileSystemEntries("Assets/Resources").Length == 0)
            {
                AssetDatabase.DeleteAsset("Assets/Resources");
            }
            AssetDatabase.Refresh();
        }

        private static string ResolveOutputDir()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int argIndex = 0; argIndex < args.Length - 1; argIndex++)
            {
                if (string.Equals(args[argIndex], "-buildOutput", StringComparison.OrdinalIgnoreCase))
                {
                    return args[argIndex + 1];
                }
            }
            return DefaultOutput;
        }
    }
}
