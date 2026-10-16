using System.Linq;
using System.Text;
using PoBox.Broadcast;
using PoBox.Fx;
using PoBox.Mj;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace PoBox.EditorTools
{
    /// <summary>
    /// What the graphics and sound need in the scenes, put there once and then ordinary scene objects. Safe to
    /// run again: each thing is looked for first and only made if it is missing.
    ///   * the hall's reverb: one AudioReverbZone over the ring, big enough that the camera is always in it;
    ///   * the free-for-all's camera shake (<see cref="CameraJolt"/>) on its one camera;
    ///   * a check that both URP renderers carry the Decal feature the canvas marks are drawn with;
    ///   * the screen ripple: a full-screen pass on both renderers (off; <see cref="ScreenRipple"/> switches it on
    ///     for the length of a ripple) with its material, and one ScreenRipple in each scene pointing at both.
    /// Applied to the arena and the free-for-all. Rebuilding the free-for-all (it is copied from the arena)
    /// keeps the reverb and loses the shake: run this again after it.
    /// </summary>
    public static class GfxAuthoring
    {
        static readonly string[] Scenes = { "Assets/Scenes/Arena.unity", "Assets/Scenes/Melee.unity" };
        static readonly string[] Renderers = { "Assets/Settings/Mobile_Renderer.asset", "Assets/Settings/PC_Renderer.asset" };

        [MenuItem("PoBox/Do All/Graphics/Apply To Arena And Melee")]
        static void ApplyMenu() => Debug.Log("[Gfx] " + Apply());

        public static string Apply()
        {
            if (EditorApplication.isPlaying) return "stop play first";
            if (!FreeForAllAuthoring.Leave()) return "the open scene has unsaved changes: save or discard them first";
            string back = EditorSceneManager.GetActiveScene().path;
            var sb = new StringBuilder();

            var ripplePasses = new System.Collections.Generic.List<ScriptableRendererFeature>();
            Material rippleMat = RippleMaterial();
            foreach (string path in Renderers)
            {
                var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(path);
                bool has = data != null && data.rendererFeatures.Any(f => f is DecalRendererFeature);
                sb.Append($"{System.IO.Path.GetFileNameWithoutExtension(path)} decals {(has ? "ok" : "MISSING: add the Decal feature by hand")}; ");
                if (data == null) continue;
                var pass = data.rendererFeatures.OfType<FullScreenPassRendererFeature>().FirstOrDefault(f => f.name == "Screen Ripple");
                if (pass == null)
                {
                    pass = ScriptableObject.CreateInstance<FullScreenPassRendererFeature>();
                    pass.name = "Screen Ripple";
                    pass.injectionPoint = FullScreenPassRendererFeature.InjectionPoint.AfterRenderingPostProcessing;
                    pass.fetchColorBuffer = true;
                    pass.passMaterial = rippleMat;
                    pass.SetActive(false);
                    AssetDatabase.AddObjectToAsset(pass, data);
                    data.rendererFeatures.Add(pass);
                    data.SetDirty();
                    EditorUtility.SetDirty(data);
                    sb.Append("+ripple pass; ");
                }
                ripplePasses.Add(pass);
            }
            AssetDatabase.SaveAssets();

            foreach (string path in Scenes)
            {
                if (!System.IO.File.Exists(path)) { sb.Append($"{path} not found; "); continue; }
                var scene = EditorSceneManager.OpenScene(path);
                sb.Append(System.IO.Path.GetFileNameWithoutExtension(path)).Append(':');

                var reverb = Object.FindAnyObjectByType<AudioReverbZone>(FindObjectsInactive.Include);
                if (reverb == null)
                {
                    var ring = Object.FindAnyObjectByType<MjRing>(FindObjectsInactive.Include);
                    Vector2 c = ring != null ? ring.centre : Vector2.zero;
                    var go = new GameObject("Hall Reverb");
                    go.transform.position = new Vector3(c.x, 2f, c.y);
                    reverb = go.AddComponent<AudioReverbZone>();
                    // A hall, kept back: the room level well under the preset's, so it is space and not echo.
                    reverb.reverbPreset = AudioReverbPreset.Auditorium;
                    reverb.room = -1400;
                    reverb.minDistance = 12f;
                    reverb.maxDistance = 40f;
                    sb.Append(" +reverb");
                }

                var melee = Object.FindAnyObjectByType<MeleeCamera>(FindObjectsInactive.Include);
                if (melee != null && melee.GetComponent<CameraJolt>() == null)
                {
                    melee.gameObject.AddComponent<CameraJolt>();
                    sb.Append(" +shake");
                }

                var ripple = Object.FindAnyObjectByType<ScreenRipple>(FindObjectsInactive.Include);
                if (ripple == null)
                {
                    ripple = new GameObject("Screen Ripple").AddComponent<ScreenRipple>();
                    sb.Append(" +ripple");
                }
                ripple.passes = ripplePasses.ToArray();
                EditorUtility.SetDirty(ripple);

                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                sb.Append(" saved; ");
            }

            if (!string.IsNullOrEmpty(back) && System.IO.File.Exists(back)) EditorSceneManager.OpenScene(back);
            return sb.ToString();
        }

        /// <summary>Assets/Settings/ScreenRipple.mat on Shaders/ScreenRipple.shader, made if missing.</summary>
        static Material RippleMaterial()
        {
            const string path = "Assets/Settings/ScreenRipple.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat != null) return mat;
            mat = new Material(Shader.Find("PoBox/ScreenRipple"));
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }
    }
}
