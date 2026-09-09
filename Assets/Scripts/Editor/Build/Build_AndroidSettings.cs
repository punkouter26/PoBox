using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;

namespace PoBox.Editor
{
    /// <summary>
    /// One-shot configuration of everything Google Play needs from PlayerSettings:
    /// application id, version, SDK levels, architecture, orientation, and the
    /// launcher icons.
    ///
    /// It is a MENU ITEM rather than an [InitializeOnLoad] because it writes
    /// ProjectSettings.asset, and settings that rewrite themselves on every domain
    /// reload cannot be overridden by hand. Run it once, or again after changing the
    /// icon art; the build tools re-assert identity and signing on every build but
    /// deliberately never touch the icons.
    ///
    /// Icon layout mirrors what Android consumes:
    ///   Adaptive (API 26+) — two layers, background first then foreground, at the
    ///     six densities Unity asks for. The foreground art must stay inside the
    ///     middle 66% of its canvas; launchers mask the rest away, and every OEM
    ///     masks it to a different shape.
    ///   Round / Legacy — the single pre-adaptive bitmap, for older launchers.
    /// </summary>
    public static class Build_AndroidSettings
    {
        private const string ICON_DIR = "Assets/Icons/";
        private const string ADAPTIVE_BACKGROUND = ICON_DIR + "AppIcon_Adaptive_Background.png";
        private const string ADAPTIVE_FOREGROUND = ICON_DIR + "AppIcon_Adaptive_Foreground.png";
        private const string LEGACY = ICON_DIR + "AppIcon_Legacy.png";

        private const string VERSION = "1.0.0";
        // FLOOR, not the value. Play rejects a reused version code, so every
        // build takes max(this, current + 1) and the number only ever goes up.
        // It was a const 1, which meant a second upload was rejected and the
        // fix was to remember to edit this file.
        private const int VERSION_CODE_FLOOR = 1;

        /// <summary>Next version code: one past whatever the project is on.</summary>
        private static int NextVersionCode()
        {
            int current = PlayerSettings.Android.bundleVersionCode;
            return Mathf.Max(VERSION_CODE_FLOOR, current + 1);
        }

        [MenuItem("PoBox/Build/Configure Android Release")]
        public static void Apply()
        {
            PlayerSettings.companyName = "Punkouter Software";
            PlayerSettings.productName = "PoBox";
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, Build_Android.APP_ID);
            PlayerSettings.bundleVersion = VERSION;
            int versionCode = NextVersionCode();
            PlayerSettings.Android.bundleVersionCode = versionCode;

            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;
            PlayerSettings.Android.targetSdkVersion = (AndroidSdkVersions)36;
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetIl2CppCompilerConfiguration(NamedBuildTarget.Android, Il2CppCompilerConfiguration.Release);


            ApplyDeviceSettings();

            // Signing paths only — Unity never serializes the passwords.
            PlayerSettings.Android.useCustomKeystore = true;
            PlayerSettings.Android.keystoreName = Build_Android.KEYSTORE_PATH;
            PlayerSettings.Android.keyaliasName = Build_Android.KEYALIAS;

            string iconReport = ApplyIcons();
            AssetDatabase.SaveAssets();
            // ProjectSettings.asset lives outside Assets/, so SaveAssets does not
            // cover it and the values would live only in the open Editor while
            // the file on disk still said the old thing. This is what writes it.
            EditorApplication.ExecuteMenuItem("File/Save Project");

            Debug.Log($"ANDROID CONFIG RESULT: id={Build_Android.APP_ID} v{VERSION} " +
                      $"(code {versionCode}) min=26 target=36 arch=ARM64 IL2CPP " +
                      $"gfx=Vulkan,GLES3 framePacing=on cutout=on | {iconReport}");
        }

        /// <summary>
        /// The device-facing settings, shared with <see cref="Build_Android"/> so a
        /// build that never ran the menu item still gets them. Keeping them only
        /// in Apply() is the same trap this project already hit once, where the
        /// APK and the AAB were configured by different code paths.
        /// </summary>
        internal static void ApplyDeviceSettings()
        {
            // Portrait-only: this project's UI is laid out against a 9:16 reference,
            // so a landscape rotation is not a degraded experience, it is a broken one.
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;
            PlayerSettings.allowedAutorotateToPortrait = true;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = false;
            PlayerSettings.allowedAutorotateToLandscapeRight = false;

            // GRAPHICS. Vulkan first, GLES3 as the fallback: URP on Vulkan is
            // the faster path on every device this ships to, and leaving the
            // list on "auto" lets Unity pick GLES3 first on some vendors.
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[]
            {
                GraphicsDeviceType.Vulkan,
                GraphicsDeviceType.OpenGLES3,
            });

            // Frame pacing: this is a physics game at a fixed 0.02 s step, so a
            // jittery present is visible as the fighters stuttering even when
            // the average frame rate is fine.
            PlayerSettings.Android.optimizedFramePacing = true;

            // Draw into the cutout/notch area. The HUD pads itself back out of
            // it with Screen.safeArea (Systems_DeviceHud), so nothing lands
            // under the camera hole -- rendering short of the cutout instead
            // leaves a black band on exactly the phones this is tested on.
            PlayerSettings.Android.renderOutsideSafeArea = true;
        }

        private static string ApplyIcons()
        {
            var background = AssetDatabase.LoadAssetAtPath<Texture2D>(ADAPTIVE_BACKGROUND);
            var foreground = AssetDatabase.LoadAssetAtPath<Texture2D>(ADAPTIVE_FOREGROUND);
            var legacy = AssetDatabase.LoadAssetAtPath<Texture2D>(LEGACY);

            if (background == null || foreground == null || legacy == null)
            {
                return "ICONS SKIPPED — missing art under " + ICON_DIR;
            }

            var report = new System.Text.StringBuilder("icons:");
            foreach (PlatformIconKind kind in PlayerSettings.GetSupportedIconKinds(NamedBuildTarget.Android))
            {
                PlatformIcon[] slots = PlayerSettings.GetPlatformIcons(NamedBuildTarget.Android, kind);
                foreach (PlatformIcon slot in slots)
                {
                    // Two layers means adaptive; Unity orders them background-first,
                    // which is the order the generated XML references them in.
                    if (slot.maxLayerCount >= 2)
                    {
                        slot.SetTextures(background, foreground);
                    }
                    else
                    {
                        slot.SetTextures(legacy);
                    }
                }
                PlayerSettings.SetPlatformIcons(NamedBuildTarget.Android, kind, slots);
                report.Append($" {kind}x{slots.Length}");
            }
            return report.ToString();
        }
    }
}
