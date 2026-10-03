using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PoBox.EditorTools
{
    /// <summary>
    /// Getting the game onto an Android phone with its fight still running on MuJoCo.
    ///
    /// The fight is stepped by MuJoCo's own library, from the embedded package Packages/bin.mujoco
    /// (joanllobera/mujoco-bin#3.5.0, the house rule). That tag's Android library is MuJoCo 3.3.7, where the plugin
    /// and the Windows library are 3.5.0, and the two lay mjModel and mjData out differently; the one here is 3.5.0,
    /// built for arm64 with the NDK (Packages/bin.mujoco/README-PoBox.md). The report reads the version out of the
    /// library itself, so a 3.3.7 one put back by a package update is caught.
    ///
    /// <c>PoBox/Android/Prepare</c> sets the plug-ins' platforms and the player settings a phone build needs
    /// and says what is still missing. <c>PoBox/Android/Build APK</c> builds, and switches the editor to the
    /// Android target to do it (every asset is re-imported, both ways).
    /// </summary>
    public static class AndroidPrep
    {
        public const string Library = "Packages/bin.mujoco/Runtime/Plugins/Android/libmujoco.so";
        const string WindowsLibrary = "Packages/bin.mujoco/Runtime/Plugins/x86_64/mujoco.dll";

        [MenuItem("PoBox/Android/Prepare", priority = 60)]
        public static void Prepare() => Debug.Log("[PoBox] Android: " + Report(true));

        /// <summary>What is ready and what is missing, as one line. With <paramref name="apply"/>, the settings are made first.</summary>
        public static string Report(bool apply)
        {
            var missing = new System.Collections.Generic.List<string>();
            var ready = new System.Collections.Generic.List<string>();

            if (File.Exists(Library))
            {
                if (apply)
                {
                    var plugin = AssetImporter.GetAtPath(Library) as PluginImporter;
                    if (plugin != null)
                    {
                        plugin.SetCompatibleWithAnyPlatform(false);
                        plugin.SetCompatibleWithEditor(false);
                        plugin.SetCompatibleWithPlatform(BuildTarget.Android, true);
                        plugin.SetPlatformData(BuildTarget.Android, "CPU", "ARM64");
                        plugin.SaveAndReimport();
                    }
                    // The Windows library must not be offered to a phone build.
                    var windows = AssetImporter.GetAtPath(WindowsLibrary) as PluginImporter;
                    if (windows != null && windows.GetCompatibleWithPlatform(BuildTarget.Android))
                    {
                        windows.SetCompatibleWithPlatform(BuildTarget.Android, false);
                        windows.SaveAndReimport();
                    }
                }
                string version = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(Library)).Contains("3.5.0") ? "3.5.0" : null;
                if (version != null) ready.Add($"MuJoCo {version} library for arm64 ({new FileInfo(Library).Length / (1024 * 1024)} MB)");
                else missing.Add("a MuJoCo 3.5.0 library for arm64 (" + Library + " is another version; Packages/bin.mujoco/README-PoBox.md)");
            }
            else missing.Add("the MuJoCo library (" + Library + ", Packages/bin.mujoco/README-PoBox.md)");


            if (apply)
            {
                // The library is built for 64-bit ARM and Android 12; nothing older can load it.
                PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
                PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
                if ((int)PlayerSettings.Android.minSdkVersion < 31) PlayerSettings.Android.minSdkVersion = (AndroidSdkVersions)31;
                PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android, "com.po.pobox");
                PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;
                // A phone starts on the Mobile quality tier.
                string[] names = QualitySettings.names;
                int mobile = Array.IndexOf(names, "Mobile");
                if (mobile >= 0)
                {
                    var quality = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset")[0]);
                    SerializedProperty per = quality.FindProperty("m_PerPlatformDefaultQuality");
                    if (per != null)
                    {
                        bool found = false;
                        for (int i = 0; i < per.arraySize; i++)
                        {
                            SerializedProperty entry = per.GetArrayElementAtIndex(i);
                            if (entry.FindPropertyRelative("first").stringValue != "Android") continue;
                            entry.FindPropertyRelative("second").intValue = mobile;
                            found = true;
                        }
                        if (!found)
                        {
                            per.arraySize++;
                            SerializedProperty entry = per.GetArrayElementAtIndex(per.arraySize - 1);
                            entry.FindPropertyRelative("first").stringValue = "Android";
                            entry.FindPropertyRelative("second").intValue = mobile;
                        }
                        quality.ApplyModifiedPropertiesWithoutUndo();
                    }
                }
                AssetDatabase.SaveAssets();
            }
            ready.Add("player settings (IL2CPP, ARM64, Android 12+, portrait, Mobile tier)");

            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android))
                missing.Add("Unity's Android Build Support module");
            // The SDK, NDK and JDK: wherever the editor has been told they are (Preferences > External Tools).
            // Asked by reflection, so this file compiles on a machine without the Android module.
            Type tools = Type.GetType("UnityEditor.Android.AndroidExternalToolsSettings, UnityEditor.Android.Extensions");
            foreach ((string property, string what) in new[] { ("sdkRootPath", "Android SDK"), ("ndkRootPath", "Android NDK"), ("jdkRootPath", "JDK") })
            {
                string path = null;
                try { path = tools?.GetProperty(property)?.GetValue(null) as string; } catch (Exception) { }
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) ready.Add($"{what} ({path})");
                else missing.Add(what + " (Unity Hub > Installs > this editor > Add modules, or Preferences > External Tools)");
            }

            return (missing.Count == 0 ? "ready to build. " : "NOT ready: missing " + string.Join("; ", missing) + ". ") + "In place: " + string.Join("; ", ready) + ".";
        }

        [MenuItem("PoBox/Android/Build APK", priority = 61)]
        public static void Build()
        {
            string report = Report(true);
            if (report.StartsWith("NOT ready")) { Debug.LogError("[PoBox] Android build not started. " + report); return; }
            Directory.CreateDirectory("Build/Android");
            var options = new BuildPlayerOptions
            {
                scenes = Array.ConvertAll(EditorBuildSettings.scenes, s => s.path),
                locationPathName = "Build/Android/PoBox.apk",
                target = BuildTarget.Android,
                options = BuildOptions.None,
            };
            BuildSummary summary = BuildPipeline.BuildPlayer(options).summary;
            Debug.Log($"[PoBox] Android build {summary.result}: {summary.totalSize / (1024 * 1024)} MB in {summary.totalTime.TotalMinutes:0.0} min, {summary.totalErrors} error(s). {summary.outputPath}");
        }
    }
}
