// filepath: Assets/Scripts/Editor/Build_WebGL.cs
//
// Invoked from `build-web.ps1` via:
//   Unity.exe -batchmode -quit -projectPath . -buildTarget WebGL \
//             -executeMethod PoBox.Editor.Build_WebGL.Build \
//             -buildOutput WEB
//
// The `-buildOutput` value (relative to project root) is read from
// `Environment.GetCommandLineArgs()`. Anything else is ignored so the Editor
// stays usable from menu items / MCP without changing the project's default
// platform.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PoBox.Editor
{
    /// <summary>
    /// WebGL build pipeline. The Azure static site at <c>WEB/</c> is the
    /// deployment artifact, so the default output is <c>WEB</c> next to the
    /// project root.
    /// </summary>
    internal static class Build_WebGL
    {
        private const string DefaultOutput = "WEB";
        private const string BuildSubdir = "Build";

        [MenuItem("Tools/Web/Build WebGL to WEB/")]
        public static void BuildFromMenu()
        {
            var report = Build(DefaultOutput);
            EditorUtility.RevealInFinder(Path.Combine(DefaultOutput, BuildSubdir));
            if (report != BuildResult.Succeeded)
            {
                throw new Exception($"WebGL build failed: {report}");
            }
        }

        /// <summary>
        /// Re-applies the WebGL audio overrides to every AudioImporter in the
        /// project. The preprocessor runs this on every WebGL build anyway,
        /// but invoking it manually is useful after pulling new audio assets.
        /// </summary>
        [MenuItem("Tools/Web/Force WebGL Audio to PCM")]
        public static void ForceWebGLAudioToPcmMenu()
        {
            int touched = ForceWebGLAudioToPcm();
            AssetDatabase.SaveAssets();
            EditorUtility.DisplayDialog(
                "WebGL Audio",
                $"Reimport settings updated on {touched} AudioImporter(s).\n" +
                "WebGL now uses PCM (compressionFormat=0) so clips play without\n" +
                "going through AudioContext.decodeAudioData().",
                "OK");
        }

        /// <summary>
        /// Apply the WebGL PCM audio override to every AudioImporter in the
        /// project. Returns the number of importers modified.
        ///
        /// Why: Unity's WebGL player routes compressed audio (Vorbis) through
        /// AudioContext.decodeAudioData(). When that path fails — and it
        /// routinely does on Chromium-based browsers with autoplay restrictions
        /// and certain Vorbis bitstreams — Unity surfaces an
        /// "EncodingError: Unable to decode audio data" banner and the clip is
        /// silent. Forcing PCM at import time sidesteps the browser decoder
        /// entirely; the only downside is a slightly larger data file, which
        /// is irrelevant for this project.
        /// </summary>
        public static int ForceWebGLAudioToPcm()
        {
            string[] guids = AssetDatabase.FindAssets("t:AudioImporter");
            int modified = 0;
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                AudioImporter importer = AssetImporter.GetAtPath(path) as AudioImporter;
                if (importer == null) continue;

                AudioImporterSampleSettings settings =
                    importer.GetOverrideSampleSettings("WebGL");

                // Only write if the override is missing or wrong, to avoid
                // dirtying every .meta on every build.
                bool needsWrite = !importer.ContainsSampleSettingsOverride("WebGL")
                                 || settings.compressionFormat == AudioCompressionFormat.Vorbis;
                if (!needsWrite) continue;

                settings.compressionFormat = AudioCompressionFormat.PCM;
                settings.loadType = AudioClipLoadType.DecompressOnLoad;
                settings.sampleRateSetting = AudioSampleRateSetting.OptimizeSampleRate;
                importer.SetOverrideSampleSettings("WebGL", settings);
                importer.SaveAndReimport();
                modified++;
            }
            return modified;
        }

        /// <summary>
        /// Entry point for batch / CLI invocation. Returns the build result
        /// enum so callers can decide what to log without needing reflection.
        /// </summary>
        public static void Build()
        {
            string output = ResolveOutputDir();
            var result = Build(output);
            if (result != BuildResult.Succeeded)
            {
                // Editor.Exit with non-zero so the wrapping PowerShell script
                // surfaces the failure instead of silently shipping a broken
                // build like the previous index.html-with-placeholders incident.
                EditorApplication.Exit(1);
            }
        }

        private static BuildResult Build(string outputDir)
        {
            // Resolve + clean the destination BEFORE switching target so we
            // don't carry stale WebAssembly from a previous run.
            string absoluteOut = Path.GetFullPath(outputDir);
            string buildSubdir = Path.Combine(absoluteOut, BuildSubdir);
            if (Directory.Exists(buildSubdir))
            {
                Directory.Delete(buildSubdir, recursive: true);
            }
            // StreamingAssets inside WEB/ must also be wiped: Unity will
            // regenerate it on build, but only if the parent is gone.
            string saDir = Path.Combine(absoluteOut, "StreamingAssets");
            if (Directory.Exists(saDir))
            {
                Directory.Delete(saDir, recursive: true);
            }

            // Switch active build target if we're not already there. First
            // switch on a project triggers a full re-import — that takes a
            // while but only happens once.
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.WebGL)
            {
                bool switched = EditorUserBuildSettings.SwitchActiveBuildTarget(
                    BuildTargetGroup.WebGL, BuildTarget.WebGL);
                if (!switched)
                {
                    Debug.LogError("Failed to switch active build target to WebGL.");
                    return BuildResult.Failed;
                }
            }

            // Scenes in build order. EditorBuildSettings.scenes is the source
            // of truth and matches what `File > Build Settings` would use.
            string[] scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();

            if (scenes.Length == 0)
            {
                Debug.LogError("No scenes enabled in Build Settings — nothing to build.");
                return BuildResult.Failed;
            }

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = absoluteOut,
                target = BuildTarget.WebGL,
                targetGroup = BuildTargetGroup.WebGL,
                options = BuildOptions.None,
            };

            Debug.Log($"Building WebGL to {absoluteOut} with {scenes.Length} scene(s).");
            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;

            Debug.Log($"WebGL build {summary.result} in {summary.totalTime}. " +
                      $"Size: {summary.totalSize} bytes, errors: {summary.totalErrors}, " +
                      $"warnings: {summary.totalWarnings}.");

            return summary.result;
        }

        private static string ResolveOutputDir()
        {
            // Look for -buildOutput <path> in the command line, the same
            // convention Unity itself uses for the build output location.
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

    /// <summary>
    /// Runs on every WebGL build. Forces all AudioImporter overrides to PCM
    /// for WebGL so the player doesn't need to call AudioContext.decodeAudioData
    /// — see <see cref="Build_WebGL.ForceWebGLAudioToPcm"/> for the rationale.
    /// </summary>
    internal sealed class WebGLAudioPcmPreprocessor : IPreprocessBuildWithReport
    {
        // Run after AssetBundles are built, before the player is packaged.
        public int callbackOrder => 100;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.WebGL) return;
            int touched = Build_WebGL.ForceWebGLAudioToPcm();
            if (touched > 0)
            {
                AssetDatabase.SaveAssets();
                Debug.Log($"WebGL preprocessor: forced {touched} AudioImporter(s) to PCM.");
            }
        }
    }
}