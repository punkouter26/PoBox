using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PoBox.Editor
{
    /// <summary>
    /// Builds the Android artifacts: the signed release App Bundle (.aab) that
    /// goes to Google Play, and the sideloadable APK for on-device testing.
    ///
    /// ONE CLASS FOR BOTH, DELIBERATELY. These were two files, and the APK one
    /// set only the application id and the signing key. Everything else --
    /// min and target SDK, ARM64, IL2CPP, release compilation -- was applied
    /// solely on the bundle path, so the APK inherited whatever those settings
    /// happened to be left at. Its own comment claimed "what you test is what
    /// you ship" and nothing enforced it. Now both formats run the same
    /// Configure step and differ only in the two lines that must differ.
    ///
    /// The upload keystore and its password live OUTSIDE the repo so neither
    /// can be committed:
    ///     C:/Users/punko/Downloads/PoBox-Release/pobox-upload.jks
    ///     C:/Users/punko/Downloads/PoBox-Release/pobox-upload.pass
    ///
    /// Unity deliberately does not serialize keystore passwords into
    /// ProjectSettings.asset, so they must be supplied at build time -- that is
    /// what the .pass file, or POBOX_KEYSTORE_PASS which wins if set, is for.
    /// Without either the build ABORTS rather than producing an unsigned
    /// artifact that Play would reject minutes later.
    ///
    /// Both formats sign with the SAME key, so an APK installs over a Play
    /// build without a signature mismatch.
    ///
    /// Headless:
    ///     -executeMethod PoBox.Editor.Build_Android.Aab
    ///     -executeMethod PoBox.Editor.Build_Android.Apk
    ///
    /// The outcome is the "AAB BUILD RESULT:" or "BUILD RESULT:" line in the
    /// editor log. Those strings are load-bearing -- Scripts/publish.ps1 and the
    /// headless recipes in CLAUDE.md grep for them -- so do not reword them.
    /// </summary>
    public static class Build_Android
    {
        private const string AAB_OUTPUT_PATH = "Builds/Android/PoBox.aab";
        private const string APK_OUTPUT_PATH = "Builds/Android/PoBox.apk";

        /// PERMANENT once the first bundle is uploaded -- Play keys the app on it.
        internal const string APP_ID = "com.punkoutersoftware.pobox";

        internal const string KEYSTORE_PATH = "C:/Users/punko/Downloads/PoBox-Release/pobox-upload.jks";
        internal const string KEYALIAS = "pobox-upload";
        internal const string PASS_ENV_VAR = "POBOX_KEYSTORE_PASS";

        /// Google Play requires new apps and updates to target API 36 from 2026-08-31.
        private const int TARGET_SDK = 36;
        private const int MIN_SDK = 26;

        /// The scenes that belong in a PLAYER, in boot order -- index 0 is what
        /// the app opens into. An explicit list rather than whatever is ticked in
        /// Build Settings, because Build Settings also carries the SCN_TRAIN_*
        /// scenes, and shipping those would both bloat the bundle and, depending
        /// on order, boot a tester into a training rig.
        internal static readonly string[] SHIP_SCENES =
        {
            "Assets/Scenes/SCN_MENU.unity",
            "Assets/Scenes/SCN_TEST_BALANCE_CONTEST.unity",
            "Assets/Scenes/SCN_TEST_WALK_CONTEST.unity",
        };

        // ------------------------------------------------------------------
        // Entry points
        // ------------------------------------------------------------------

        [MenuItem("PoBox/Build/Android AAB (Play release)")]
        public static void Aab() => Run(appBundle: true);

        [MenuItem("PoBox/Build/Android APK")]
        public static void Apk() => Run(appBundle: false);

        /// <summary>
        /// Sideload APK signed with the ANDROID DEBUG KEY, for putting a build
        /// on a device that is plugged in right now.
        ///
        /// This is NOT the Play artifact and cannot become one: it does not
        /// carry the upload key, so it will not install over a Play build and
        /// Play will reject it. Use <see cref="Aab"/> for anything shippable.
        /// It exists because the release keystore lives outside the repo
        /// (C:/Users/punko/Downloads/PoBox-Release/) and is not on every
        /// machine -- without this the only way to get a build onto a handset
        /// is to first create an upload key, which is a permanent identity
        /// decision that a test install should not be making.
        /// </summary>
        [MenuItem("PoBox/Build/Android APK (debug-signed, sideload)")]
        public static void DevApk() => Run(appBundle: false, debugSigned: true);

        // ------------------------------------------------------------------

        private static void Run(bool appBundle) => Run(appBundle, debugSigned: false);

        private static void Run(bool appBundle, bool debugSigned)
        {
            // "AAB BUILD RESULT:" contains "BUILD RESULT:", so a log grep for the
            // shorter string still finds the bundle line. Both are kept verbatim.
            string tag = appBundle ? "AAB BUILD RESULT:" : "BUILD RESULT:";
            string output = appBundle ? AAB_OUTPUT_PATH : APK_OUTPUT_PATH;

            if (EditorApplication.isPlaying)
            {
                Debug.LogError($"{tag} Aborted -- exit Play mode first.");
                return;
            }

            string password = debugSigned ? null : ResolveKeystorePassword();
            if (!debugSigned && string.IsNullOrEmpty(password))
            {
                Debug.LogError($"{tag} Aborted -- no keystore password. Set {PASS_ENV_VAR} " +
                               $"or put it on the first line of {Path.ChangeExtension(KEYSTORE_PATH, null)}.pass");
                return;
            }

            // A present .pass with a missing .jks -- a half-restored release
            // folder -- satisfies the check above, so without this guard the
            // build runs for minutes and then dies inside Gradle on a signing
            // error.
            if (!debugSigned && !File.Exists(KEYSTORE_PATH))
            {
                Debug.LogError($"{tag} Aborted -- keystore not found at {KEYSTORE_PATH}");
                return;
            }

            List<string> scenes = ResolveShipScenes();
            if (scenes == null) { return; }

            Configure(password);
            if (debugSigned)
            {
                // Say it loudly and in the build log: an unlabelled debug-signed
                // APK that looks like a release one is how the wrong file gets
                // handed to a tester.
                PlayerSettings.Android.useCustomKeystore = false;
                Debug.LogWarning($"{tag} DEBUG-SIGNED sideload build -- android debug key, " +
                                 "NOT the Play upload key. Will not install over a Play build.");
            }

            // The only two settings that may differ between the formats. The AAB
            // builder leaves buildAppBundle = true persisted in
            // EditorUserBuildSettings, so without the false branch an "APK" build
            // silently emits an app bundle to PoBox.apk, which adb cannot install.
            EditorUserBuildSettings.buildAppBundle = appBundle;
            EditorUserBuildSettings.androidBuildType = AndroidBuildType.Release;
            EditorUserBuildSettings.development = false;

            Directory.CreateDirectory(Path.GetDirectoryName(output));

            var options = new BuildPlayerOptions
            {
                scenes = scenes.ToArray(),
                locationPathName = output,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None,
            };

            Debug.Log($"{(appBundle ? "AAB " : "")}BUILD START: {APP_ID} v{PlayerSettings.bundleVersion} " +
                      $"(code {PlayerSettings.Android.bundleVersionCode}) " +
                      $"target={TARGET_SDK} min={MIN_SDK} scenes={scenes.Count} " +
                      $"boot={Path.GetFileNameWithoutExtension(scenes[0])}");

            BuildReport report;
            try
            {
                report = BuildPipeline.BuildPlayer(options);
            }
            finally
            {
                // Do not leave the password sitting in the in-memory PlayerSettings.
                PlayerSettings.Android.keystorePass = string.Empty;
                PlayerSettings.Android.keyaliasPass = string.Empty;
            }

            BuildSummary summary = report.summary;
            Debug.Log($"{tag} {summary.result} | errors={summary.totalErrors} | " +
                      $"size={summary.totalSize / (1024 * 1024)}MB | " +
                      $"time={summary.totalTime.TotalMinutes:F1}min | {summary.outputPath}");
        }

        /// Identity, signing and the Play requirements. Applied identically to
        /// both formats, which is the whole point of merging the two builders.
        private static void Configure(string password)
        {
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, APP_ID);
            PlayerSettings.companyName = "Punkouter Software";
            PlayerSettings.productName = "PoBox";

            PlayerSettings.Android.useCustomKeystore = true;
            PlayerSettings.Android.keystoreName = KEYSTORE_PATH;
            PlayerSettings.Android.keystorePass = password;
            PlayerSettings.Android.keyaliasName = KEYALIAS;
            PlayerSettings.Android.keyaliasPass = password;

            PlayerSettings.Android.minSdkVersion = (AndroidSdkVersions)MIN_SDK;
            PlayerSettings.Android.targetSdkVersion = (AndroidSdkVersions)TARGET_SDK;
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetIl2CppCompilerConfiguration(NamedBuildTarget.Android,
                Il2CppCompilerConfiguration.Release);

            // Graphics API order, frame pacing, cutout rendering and the portrait
            // lock. Shared with the menu item rather than restated, so a build
            // that never ran "Configure Android Release" is still the same player.
            Build_AndroidSettings.ApplyDeviceSettings();
        }

        /// Verifies every shipping scene is actually on disk and returns them in
        /// boot order. A missing scene is an abort rather than a warning: a
        /// bundle silently short one scene is a crash on a tester's phone,
        /// discovered a day later.
        internal static List<string> ResolveShipScenes()
        {
            var scenes = new List<string>();
            foreach (string path in SHIP_SCENES)
            {
                if (!File.Exists(path))
                {
                    Debug.LogError("BUILD RESULT: Aborted -- shipping scene not found: " + path);
                    return null;
                }
                scenes.Add(path);
            }
            return scenes;
        }

        /// Environment variable wins; otherwise read the .pass beside the keystore.
        internal static string ResolveKeystorePassword()
        {
            string fromEnv = Environment.GetEnvironmentVariable(PASS_ENV_VAR);
            if (!string.IsNullOrEmpty(fromEnv)) { return fromEnv.Trim(); }

            string passFile = Path.ChangeExtension(KEYSTORE_PATH, null) + ".pass";
            if (File.Exists(passFile)) { return File.ReadAllText(passFile).Trim(); }

            string sibling = Path.Combine(Path.GetDirectoryName(KEYSTORE_PATH) ?? ".", "keystore.pass");
            return File.Exists(sibling) ? File.ReadAllText(sibling).Trim() : null;
        }
    }
}
