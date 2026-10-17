using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PoBox.EditorTools
{
    /// <summary>
    /// Bakes the hall. The lamps over the ring and the dim house lights are Mixed: what they do to the
    /// fighters stays real-time, what they do to the hall floor, the apron and the stands' steps is
    /// baked, bounce and all, and a grid of light probes over the ring carries that bounce onto the
    /// fighters. On the CPU, at a low resolution: the hall is large and plain, and the GPU may be busy
    /// training. Blocks until done.
    /// </summary>
    public static class Lighting
    {
        [MenuItem("PoBox/Bake Arena Lighting", priority = 5)]
        public static void BakeNow()
        {
            if (EditorSceneManager.GetActiveScene().path != Paths.ArenaScene) EditorSceneManager.OpenScene(Paths.ArenaScene, OpenSceneMode.Single);
            if (Bake()) EditorSceneManager.SaveOpenScenes();
        }

        public static bool Bake()
        {
            const string settingsPath = Paths.SettingsDir + "/PoBox.lighting";
            if (AssetDatabase.LoadAssetAtPath<LightingSettings>(settingsPath) != null) AssetDatabase.DeleteAsset(settingsPath);
            var settings = new LightingSettings
            {
                name = "PoBox Lighting",
                // Deprecated in favour of the GPU baker, which has not been tried here: the bake is on the CPU on purpose.
#pragma warning disable CS0618
                lightmapper = LightingSettings.Lightmapper.ProgressiveCPU,
#pragma warning restore CS0618
                bakedGI = true,
                realtimeGI = false,
                mixedBakeMode = MixedLightingMode.IndirectOnly,
                lightmapResolution = 6f,
                lightmapMaxSize = 512,
                lightmapPadding = 2,
                directSampleCount = 16,
                indirectSampleCount = 96,
                environmentSampleCount = 96,
                maxBounces = 2,
                ao = true,
                aoMaxDistance = 1.2f,
                directionalityMode = LightmapsMode.NonDirectional,
                lightmapCompression = LightmapCompression.NormalQuality,
            };
            AssetDatabase.CreateAsset(settings, settingsPath);
            Lightmapping.lightingSettings = settings;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            bool ok = Lightmapping.Bake();
            Debug.Log($"[PoBox] lighting bake {(ok ? "done" : "FAILED")} in {watch.Elapsed.TotalSeconds:0} s: {LightmapSettings.lightmaps.Length} lightmap(s), " +
                      $"{(LightmapSettings.lightProbes != null ? LightmapSettings.lightProbes.count : 0)} light probes.");
            return ok;
        }
    }
}
