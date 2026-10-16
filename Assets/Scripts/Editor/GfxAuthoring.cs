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
    ///     (Assets/Settings/Look_*.asset);
    ///   * the venue round the ring (Environment/Venue): steps at the red and blue corners, a press row, two TV
    ///     cameras, a ring of barriers and a screen hung over the ring. Modelled in Blender (BLENDER/2026-10-16_arena-props,
    ///     GLBs in Assets/Models/Props, prefabs in Assets/Prefabs/Props). Seen, never touched: no MuJoCo body.
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

                var env = GameObject.Find("Environment");
                if (env != null && env.transform.Find("Venue") == null) sb.Append(" +venue ").Append(Venue(env.transform));

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

        /// <summary>
        /// The venue props, placed for the arena's ring (canvas at y 0, platform out to 3.7 m, hall floor at -1.3 m). Each
        /// prop's front, the way it faces, is +z as imported. Returns how many were placed.
        /// </summary>
        static int Venue(Transform env)
        {
            var venue = new GameObject("Venue").transform;
            venue.SetParent(env, false);
            const float floor = -1.3f;
            int n = 0;
            void Put(string prop, Vector3 at, float yaw)
            {
                var prefab = PropPrefab(prop);
                if (prefab == null) return;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, venue);
                go.transform.SetPositionAndRotation(at, Quaternion.Euler(0f, yaw, 0f));
                n++;
            }
            // Facing the ring's middle from where it stands.
            float Facing(Vector3 at) => Mathf.Atan2(-at.x, -at.z) * Mathf.Rad2Deg;

            // Steps climb towards -z as imported: the red corner's turned round to climb north onto the south side.
            Put("RingSteps", new Vector3(-2.4f, floor, -4.6f), 180f);
            Put("RingSteps", new Vector3(2.4f, floor, 4.6f), 0f);
            Put("PressTable", new Vector3(5.4f, floor, 0f), -90f);
            var camA = new Vector3(-5.6f, floor, 2.4f);
            var camB = new Vector3(1.8f, floor, -5.8f);
            Put("TVCamera", camA, Facing(camA));
            Put("TVCamera", camB, Facing(camB));
            // Barriers at 7 m round the ring, seven 2 m sections a side.
            for (int i = -3; i <= 3; i++)
            {
                Put("Barrier", new Vector3(i * 2f, floor, -7f), 0f);
                Put("Barrier", new Vector3(i * 2f, floor, 7f), 180f);
                Put("Barrier", new Vector3(-7f, floor, i * 2f), 90f);
                Put("Barrier", new Vector3(7f, floor, i * 2f), -90f);
            }
            Put("CenterScreen", new Vector3(0f, 7.3f, 0f), 45f);
            return n;
        }

        /// <summary>Assets/Prefabs/Props/NAME.prefab from Assets/Models/Props/NAME.glb, made if missing.</summary>
        static GameObject PropPrefab(string name)
        {
            string path = $"Assets/Prefabs/Props/{name}.prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab != null) return prefab;
            var model = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Models/Props/{name}.glb");
            if (model == null) return null;
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(model);
            inst.name = name;
            foreach (var r in inst.GetComponentsInChildren<MeshRenderer>(true))
            {
                r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                r.shadowCastingMode = name == "CenterScreen" ? UnityEngine.Rendering.ShadowCastingMode.Off : UnityEngine.Rendering.ShadowCastingMode.On;
            }
            prefab = PrefabUtility.SaveAsPrefabAsset(inst, path);
            Object.DestroyImmediate(inst);
            return prefab;
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
