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
    ///     for the length of a ripple) with its material, and one ScreenRipple in each scene pointing at both;
    ///   * a beam through the haze under each of ArenaMood's ring lights (a cone, Assets/Settings/LightBeam.asset,
    ///     on Shaders/LightBeam), handed to ArenaMood;
    ///   * the looks of the night (<see cref="PhaseLook"/>): walk-on, replay and result volumes with their profiles
    ///     (Assets/Settings/Look_*.asset).
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

                var mood = Object.FindAnyObjectByType<ArenaMood>(FindObjectsInactive.Include);
                if (mood != null)
                {
                    sb.Append(" beams ").Append(Beams(mood));
                    EditorUtility.SetDirty(mood);
                }

                var look = Object.FindAnyObjectByType<PhaseLook>(FindObjectsInactive.Include);
                if (look == null)
                {
                    look = new GameObject("Phase Look").AddComponent<PhaseLook>();
                    look.walkOn = LookVolume(look.transform, "Walk-on", "Look_WalkOn", WalkOnLook);
                    look.replay = LookVolume(look.transform, "Replay", "Look_Replay", ReplayLook);
                    look.result = LookVolume(look.transform, "Result", "Look_Result", ResultLook);
                    sb.Append(" +looks");
                }
                look.bout = Object.FindAnyObjectByType<PoBox.Sim.Bout>(FindObjectsInactive.Include);
                var ringForLook = Object.FindAnyObjectByType<MjRing>(FindObjectsInactive.Include);
                if (ringForLook != null) look.ringCentre = new Vector3(ringForLook.centre.x, 1f, ringForLook.centre.y);
                EditorUtility.SetDirty(look);

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

        /// <summary>A beam under each ring light: a cone from the lamp to the canvas, as wide as the spot. Made if missing; returns the count.</summary>
        static int Beams(ArenaMood mood)
        {
            Mesh cone = BeamMesh();
            const string matPath = "Assets/Settings/LightBeam.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("PoBox/LightBeam"));
                AssetDatabase.CreateAsset(mat, matPath);
            }
            var beams = new Renderer[mood.ringLights.Length];
            for (int i = 0; i < mood.ringLights.Length; i++)
            {
                Light l = mood.ringLights[i];
                if (l == null) continue;
                Transform t = l.transform.Find("Beam");
                if (t == null)
                {
                    t = new GameObject("Beam").transform;
                    t.SetParent(l.transform, false);
                }
                float down = Mathf.Max(0.2f, -l.transform.forward.y);
                float length = Mathf.Min(l.range, l.transform.position.y / down);
                // A shaft, not the whole spot: a wide spot's cone would fill the hall with haze.
                float r = Mathf.Tan(0.5f * l.spotAngle * Mathf.Deg2Rad) * length * 0.35f;
                t.localPosition = Vector3.zero;
                t.localRotation = Quaternion.identity;
                t.localScale = new Vector3(r, r, length);
                // Not ??: a missing component is Unity's fake null, which ?? does not see.
                var mf = t.GetComponent<MeshFilter>();
                if (mf == null) mf = t.gameObject.AddComponent<MeshFilter>();
                mf.sharedMesh = cone;
                var mr = t.GetComponent<MeshRenderer>();
                if (mr == null) mr = t.gameObject.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                beams[i] = mr;
            }
            mood.beams = beams;
            mood.beamIntensity = 0.25f;
            return beams.Count(b => b != null);
        }

        /// <summary>A cone, apex at the origin, opening along +z to a radius of 1 at z = 1; v runs 0 at the apex to 1 at the rim.</summary>
        static Mesh BeamMesh()
        {
            const string path = "Assets/Settings/LightBeam.asset";
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (mesh != null) return mesh;
            const int n = 32;
            var v = new System.Collections.Generic.List<Vector3>();
            var nm = new System.Collections.Generic.List<Vector3>();
            var uv = new System.Collections.Generic.List<Vector2>();
            var tri = new System.Collections.Generic.List<int>();
            for (int k = 0; k <= n; k++)
            {
                float a = k * Mathf.PI * 2f / n;
                var dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                var normal = (dir - new Vector3(0f, 0f, 1f)).normalized;      // a cone of slope 1 in its own frame
                v.Add(Vector3.zero); nm.Add(normal); uv.Add(new Vector2(k / (float)n, 0f));
                v.Add(dir + Vector3.forward); nm.Add(normal); uv.Add(new Vector2(k / (float)n, 1f));
            }
            for (int k = 0; k < n; k++)
            {
                int a = 2 * k;
                tri.AddRange(new[] { a, a + 1, a + 3, a, a + 3, a + 2 });
            }
            mesh = new Mesh { name = "LightBeam" };
            mesh.SetVertices(v);
            mesh.SetNormals(nm);
            mesh.SetUVs(0, uv);
            mesh.SetTriangles(tri, 0);
            mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        static UnityEngine.Rendering.Volume LookVolume(Transform parent, string name, string asset, System.Action<UnityEngine.Rendering.VolumeProfile> fill)
        {
            string path = $"Assets/Settings/{asset}.asset";
            var profile = AssetDatabase.LoadAssetAtPath<UnityEngine.Rendering.VolumeProfile>(path);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<UnityEngine.Rendering.VolumeProfile>();
                AssetDatabase.CreateAsset(profile, path);
                fill(profile);
                foreach (var c in profile.components) AssetDatabase.AddObjectToAsset(c, profile);
                EditorUtility.SetDirty(profile);
                AssetDatabase.SaveAssets();
            }
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var vol = go.AddComponent<UnityEngine.Rendering.Volume>();
            vol.isGlobal = true;
            vol.priority = 12f;
            vol.weight = 0f;
            vol.enabled = false;
            vol.sharedProfile = profile;
            return vol;
        }

        static void WalkOnLook(UnityEngine.Rendering.VolumeProfile p)
        {
            var vig = p.Add<Vignette>(); vig.intensity.Override(0.38f); vig.smoothness.Override(0.5f);
            var col = p.Add<ColorAdjustments>(); col.contrast.Override(14f);
        }

        static void ReplayLook(UnityEngine.Rendering.VolumeProfile p)
        {
            var dof = p.Add<DepthOfField>(); dof.mode.Override(DepthOfFieldMode.Gaussian); dof.gaussianStart.Override(6f); dof.gaussianEnd.Override(14f); dof.gaussianMaxRadius.Override(1.2f);
            var blur = p.Add<MotionBlur>(); blur.intensity.Override(0.35f); blur.quality.Override(MotionBlurQuality.Low);
            var col = p.Add<ColorAdjustments>(); col.saturation.Override(-22f); col.colorFilter.Override(new Color(0.9f, 0.96f, 1f));
            var vig = p.Add<Vignette>(); vig.intensity.Override(0.3f);
        }

        static void ResultLook(UnityEngine.Rendering.VolumeProfile p)
        {
            var bloom = p.Add<Bloom>(); bloom.intensity.Override(1.4f);
            var col = p.Add<ColorAdjustments>(); col.postExposure.Override(0.15f); col.colorFilter.Override(new Color(1f, 0.95f, 0.86f));
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
