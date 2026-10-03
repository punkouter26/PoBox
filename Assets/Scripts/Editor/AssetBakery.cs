using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;
using PoBox.League;

namespace PoBox.EditorTools
{
    /// <summary>
    /// Everything the scene needs that is not in the scene: materials, the generated textures, the crowd
    /// mesh, the synthesised punch sounds, the two post-processing profiles, the UI panel and the ladder's
    /// profiles. All of it is written as real assets so it can be opened and changed in the editor, and all
    /// of it is deterministic, so re-baking does not churn the project.
    /// </summary>
    public static class AssetBakery
    {
        public const string MaterialDir = "Assets/Materials";
        public const string TextureDir = "Assets/Textures/Generated";
        public const string MeshDir = "Assets/Meshes";
        public const string SynthDir = "Assets/Audio/Synth";
        public const string RealDir = "Assets/Audio/Real";
        public const string SettingsDir = "Assets/Settings";
        public const string LeagueDir = "Assets/League";
        public const string UiDir = "Assets/UI";
        public const string PanelPath = UiDir + "/PoBoxPanel.asset";
        public const string HudUxml = UiDir + "/Hud.uxml";
        public const string MenuUxml = UiDir + "/Menu.uxml";
        public const string PortraitDir = UiDir + "/Portraits";
        public const string LookPath = SettingsDir + "/Arena_Volume.asset";
        public const string PulsePath = SettingsDir + "/Arena_Pulse.asset";

        public static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        // ---------------------------------------------------------------- materials

        static Material Mat(string name, string shaderName)
        {
            EnsureFolder(MaterialDir);
            string path = $"{MaterialDir}/{name}.mat";
            Shader shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Debug.LogError($"[PoBox] shader '{shaderName}' not found for {name}.");
                shader = Shader.Find("Universal Render Pipeline/Unlit");
            }
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(m, path);
            }
            else if (m.shader != shader) m.shader = shader;
            return m;
        }

        public static Material Lit(string name, Color color, float smoothness, float metallic = 0f, Texture2D texture = null, Color? emission = null)
        {
            Material m = Mat(name, "Universal Render Pipeline/Lit");
            m.SetColor("_BaseColor", color);
            m.SetFloat("_Smoothness", smoothness);
            m.SetFloat("_Metallic", metallic);
            if (texture != null) m.SetTexture("_BaseMap", texture);
            if (emission.HasValue)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", emission.Value);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>
        /// Lays a fine weave over a material as a detail normal map, tiled many times across it: the cloth
        /// of the canvas, which the printed design on it is far too coarse to carry.
        /// </summary>
        public static void Weave(Material m, string normalMapPath, float tiling, float strength)
        {
            var importer = AssetImporter.GetAtPath(normalMapPath) as TextureImporter;
            if (importer == null) { Debug.LogWarning($"[PoBox] {normalMapPath} is missing; the canvas stays smooth."); return; }
            if (importer.textureType != TextureImporterType.NormalMap || importer.wrapMode != TextureWrapMode.Repeat)
            {
                importer.textureType = TextureImporterType.NormalMap;
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.SaveAndReimport();
            }
            var normal = AssetDatabase.LoadAssetAtPath<Texture2D>(normalMapPath);
            m.SetTexture("_DetailNormalMap", normal);
            m.SetFloat("_DetailNormalMapScale", strength);
            m.SetTextureScale("_DetailAlbedoMap", new Vector2(tiling, tiling));
            m.EnableKeyword("_DETAIL_MULX2");
            EditorUtility.SetDirty(m);
        }

        /// <summary>
        /// A transparent particle material. Surface 1 is transparent; blend 0 is alpha, 2 is additive. The
        /// keywords and the blend factors have to be set by hand: the inspector normally does it.
        /// </summary>
        public static Material Particle(string name, Texture2D texture, bool additive, Color tint)
        {
            Material m = Mat(name, "Universal Render Pipeline/Particles/Unlit");
            if (texture != null) m.SetTexture("_BaseMap", texture);
            m.SetColor("_BaseColor", tint);
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", additive ? 2f : 0f);
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_Cull", 0f);
            m.SetFloat("_AlphaClip", 0f);
            m.renderQueue = (int)RenderQueue.Transparent;
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            if (additive) m.EnableKeyword("_ALPHAMODULATE_ON"); else m.DisableKeyword("_ALPHAMODULATE_ON");
            m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)(additive ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
            EditorUtility.SetDirty(m);
            return m;
        }

        public static Material Overlay()
        {
            Material m = Mat("Fighter_Overlay", "PoBox/FighterOverlay");
            m.SetFloat("_RimPower", 3f);
            m.SetFloat("_RimIntensity", 0.55f);
            EditorUtility.SetDirty(m);
            return m;
        }

        public static Material Crowd()
        {
            Material m = Mat("Crowd", "PoBox/Crowd");
            m.SetFloat("_Width", 0.62f);
            m.SetFloat("_Height", 0.95f);
            m.SetFloat("_Brightness", 0.38f);
            EditorUtility.SetDirty(m);
            return m;
        }

        // ---------------------------------------------------------------- textures

        public static Texture2D Tex(string name, int size, Func<float, float, Color> pixel, bool transparent, bool clamp)
        {
            EnsureFolder(TextureDir);
            string path = $"{TextureDir}/{name}.png";
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    px[y * size + x] = pixel((x + 0.5f) / size, (y + 0.5f) / size);
            t.SetPixels(px);
            t.Apply();
            byte[] png = t.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(t);

            string full = Path.GetFullPath(path);
            // Unchanged bytes are not rewritten, so a re-bake does not reimport anything.
            if (!File.Exists(full) || !Same(File.ReadAllBytes(full), png)) File.WriteAllBytes(full, png);
            AssetDatabase.ImportAsset(path);

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                bool dirty = importer.alphaIsTransparency != transparent
                          || importer.wrapMode != (clamp ? TextureWrapMode.Clamp : TextureWrapMode.Repeat)
                          || !importer.mipmapEnabled;
                if (dirty)
                {
                    importer.alphaIsTransparency = transparent;
                    importer.wrapMode = clamp ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
                    importer.mipmapEnabled = true;
                    importer.SaveAndReimport();
                }
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static float Hash(float x, float y)
        {
            float h = Mathf.Sin(x * 127.1f + y * 311.7f) * 43758.5453f;
            return h - Mathf.Floor(h);
        }

        /// <summary>The ring canvas: a worn blue cloth, a centre roundel, a border, and the two corners in their colours.</summary>
        public static Texture2D CanvasTexture()
        {
            Color cloth = new Color(0.20f, 0.32f, 0.52f), line = new Color(0.90f, 0.92f, 0.95f);
            Color red = new Color(0.80f, 0.16f, 0.16f), blue = new Color(0.14f, 0.36f, 0.84f);
            return Tex("Canvas", 1024, (u, v) =>
            {
                float weave = 0.94f + 0.06f * Hash(Mathf.Floor(u * 512f), Mathf.Floor(v * 512f));
                float wear = 0.9f + 0.1f * Hash(Mathf.Floor(u * 24f), Mathf.Floor(v * 24f));
                Color c = cloth * weave * wear;

                float dx = u - 0.5f, dy = v - 0.5f;
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                // A darker disc in the middle, for the promoter's mark to be laid on as a decal.
                if (r < 0.2f) c = Color.Lerp(c, new Color(0.12f, 0.20f, 0.36f), 0.7f * Mathf.SmoothStep(0.2f, 0.185f, r));

                float edge = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy));
                if (edge > 0.455f && edge < 0.47f) c = line;               // border
                if (edge > 0.47f) c = new Color(0.10f, 0.12f, 0.18f);

                // Corner pads: red at (0,0), blue at (1,1).
                if (u < 0.16f && v < 0.16f && u + v < 0.22f) c = red;
                if (u > 0.84f && v > 0.84f && (1f - u) + (1f - v) < 0.22f) c = blue;
                c.a = 1f;
                return c;
            }, false, true);
        }

        /// <summary>A soft round dot for droplets and trails.</summary>
        public static Texture2D DotTexture() => Tex("Fx_Dot", 64, (u, v) =>
        {
            float r = Mathf.Sqrt((u - 0.5f) * (u - 0.5f) + (v - 0.5f) * (v - 0.5f)) * 2f;
            float a = Mathf.Clamp01(1f - r);
            return new Color(1f, 1f, 1f, a * a);
        }, true, true);

        /// <summary>A thin ring for the impact flash.</summary>
        public static Texture2D RingTexture() => Tex("Fx_Ring", 128, (u, v) =>
        {
            float r = Mathf.Sqrt((u - 0.5f) * (u - 0.5f) + (v - 0.5f) * (v - 0.5f)) * 2f;
            float a = Mathf.Clamp01(1f - Mathf.Abs(r - 0.82f) / 0.12f);
            return new Color(1f, 1f, 1f, a * a);
        }, true, true);

        // ---------------------------------------------------------------- crowd mesh

        /// <summary>
        /// Rows of spectators in a ring round the ring, each row a step higher than the one in front. One
        /// quad a figure; the shader turns them to the camera and makes them move.
        /// </summary>
        public static Mesh CrowdMesh(float innerRadius, int rows, float rowDepth, float rowRise, float floorY, float spacing)
        {
            EnsureFolder(MeshDir);
            string path = $"{MeshDir}/Crowd.asset";

            var pos = new List<Vector3>();
            var uv = new List<Vector2>();
            var corner = new List<Vector2>();
            var seed = new List<Vector2>();
            var col = new List<Color>();
            var tri = new List<int>();

            Color[] shirts =
            {
                new Color(0.62f, 0.16f, 0.16f), new Color(0.16f, 0.30f, 0.62f), new Color(0.78f, 0.74f, 0.66f),
                new Color(0.22f, 0.24f, 0.28f), new Color(0.70f, 0.52f, 0.18f), new Color(0.28f, 0.48f, 0.34f),
                new Color(0.50f, 0.30f, 0.52f), new Color(0.86f, 0.86f, 0.88f),
            };

            var rng = new System.Random(20260930);
            float R() => (float)rng.NextDouble();

            for (int row = 0; row < rows; row++)
            {
                float radius = innerRadius + row * rowDepth;
                int count = Mathf.FloorToInt(2f * Mathf.PI * radius / spacing);
                for (int i = 0; i < count; i++)
                {
                    if (R() < 0.08f) continue;   // an empty seat here and there
                    float a = (i + R() * 0.4f) / count * Mathf.PI * 2f;
                    var pivot = new Vector3(Mathf.Cos(a) * radius, floorY + row * rowRise + 0.45f, Mathf.Sin(a) * radius);
                    Vector2 s = new Vector2(R(), R());
                    Color shirt = shirts[rng.Next(shirts.Length)] * (0.75f + 0.25f * R());
                    shirt.a = 1f;

                    int b = pos.Count;
                    for (int k = 0; k < 4; k++)
                    {
                        float cx = k == 0 || k == 3 ? -0.5f : 0.5f;
                        float cy = k < 2 ? 0f : 1f;
                        pos.Add(pivot);
                        uv.Add(new Vector2(cx + 0.5f, cy));
                        corner.Add(new Vector2(cx, cy));
                        seed.Add(s);
                        col.Add(shirt);
                    }
                    tri.Add(b); tri.Add(b + 2); tri.Add(b + 1);
                    tri.Add(b); tri.Add(b + 3); tri.Add(b + 2);
                }
            }

            var mesh = new Mesh { name = "Crowd" };
            mesh.SetVertices(pos);
            mesh.SetUVs(0, uv);
            mesh.SetUVs(1, corner);
            mesh.SetUVs(2, seed);
            mesh.SetColors(col);
            mesh.SetTriangles(tri, 0);
            // The vertices are all pivots; the real extent is a figure's height and a jump above them.
            float outer = innerRadius + rows * rowDepth + 1f;
            mesh.bounds = new Bounds(new Vector3(0f, floorY + rows * rowRise * 0.5f + 1f, 0f), new Vector3(outer * 2f, rows * rowRise + 4f, outer * 2f));

            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(mesh, path);
                return mesh;
            }
            existing.Clear();
            EditorUtility.CopySerialized(mesh, existing);
            UnityEngine.Object.DestroyImmediate(mesh);
            EditorUtility.SetDirty(existing);
            return existing;
        }

        // ---------------------------------------------------------------- audio

        /// <summary>
        /// A punch, synthesised: a sine thump whose pitch falls as it rings, under a burst of low-passed
        /// noise for the slap of leather. The recordings in Assets/Audio/Real cover the crowd and the bell;
        /// nothing in that set is a glove on a body, so these are made here.
        /// </summary>
        public static AudioClip Punch(string name, float seconds, float thumpHz, float noiseCutHz, float noiseGain, int seedValue)
        {
            EnsureFolder(SynthDir);
            string path = $"{SynthDir}/{name}.wav";
            const int rate = 44100;
            int n = Mathf.CeilToInt(seconds * rate);
            var samples = new float[n];
            var rng = new System.Random(seedValue);

            float low = 0f;
            float alpha = 1f - Mathf.Exp(-2f * Mathf.PI * noiseCutHz / rate);
            float phase = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)rate;
                float k = t / seconds;
                float hz = thumpHz * (1f - 0.45f * k);
                phase += 2f * Mathf.PI * hz / rate;
                float thump = Mathf.Sin(phase) * Mathf.Exp(-t * 16f);

                float white = (float)rng.NextDouble() * 2f - 1f;
                low += (white - low) * alpha;
                float slap = low * Mathf.Exp(-t * 55f) * noiseGain;

                float attack = Mathf.Clamp01(t / 0.002f);
                float tail = Mathf.Clamp01((seconds - t) / 0.01f);
                samples[i] = (float)Math.Tanh((thump * 0.9f + slap) * 1.6f) * 0.9f * attack * tail;
            }

            WriteWav(Path.GetFullPath(path), samples, rate);
            AssetDatabase.ImportAsset(path);
            return AssetDatabase.LoadAssetAtPath<AudioClip>(path);
        }

        public static void WriteWav(string fullPath, float[] samples, int rate)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                int bytes = samples.Length * 2;
                w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + bytes);
                w.Write(new[] { 'W', 'A', 'V', 'E' });
                w.Write(new[] { 'f', 'm', 't', ' ' }); w.Write(16); w.Write((short)1); w.Write((short)1);
                w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(bytes);
                foreach (float s in samples) w.Write((short)Mathf.RoundToInt(Mathf.Clamp(s, -1f, 1f) * 32767f));
                w.Flush();
                byte[] data = ms.ToArray();
                if (!File.Exists(fullPath) || !Same(File.ReadAllBytes(fullPath), data)) File.WriteAllBytes(fullPath, data);
            }
        }

        public static AudioClip Real(string name) => AssetDatabase.LoadAssetAtPath<AudioClip>($"{RealDir}/{name}.wav");

        // ---------------------------------------------------------------- look

        static VolumeProfile Profile(string path)
        {
            EnsureFolder(SettingsDir);
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, path);
            }
            // Rebuilt from scratch every bake, and each old override taken out of the asset file as well as
            // the list, or the file fills up with orphans.
            foreach (VolumeComponent c in new List<VolumeComponent>(profile.components))
            {
                profile.components.Remove(c);
                if (c == null) continue;
                if (AssetDatabase.IsSubAsset(c)) AssetDatabase.RemoveObjectFromAsset(c);
                UnityEngine.Object.DestroyImmediate(c, true);
            }
            profile.components.Clear();
            return profile;
        }

        /// <summary>
        /// Writes the overrides into the profile as sub-assets. VolumeProfile.Add only puts them in a list;
        /// without this the file holds a row of null references and the volume loads with nothing in it.
        /// </summary>
        static void Persist(VolumeProfile profile)
        {
            foreach (VolumeComponent c in profile.components)
            {
                if (c == null || AssetDatabase.IsSubAsset(c)) continue;
                c.hideFlags = HideFlags.HideInHierarchy | HideFlags.HideInInspector;
                AssetDatabase.AddObjectToAsset(c, profile);
            }
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
        }

        /// <summary>The house look: a dark hall, hot lamps, a filmic roll-off, glow on anything brighter than white.</summary>
        public static VolumeProfile Look()
        {
            VolumeProfile p = Profile(LookPath);

            var tone = p.Add<Tonemapping>(true);
            tone.mode.Override(TonemappingMode.ACES);

            var colour = p.Add<ColorAdjustments>(true);
            colour.postExposure.Override(0.25f);
            colour.contrast.Override(12f);
            colour.saturation.Override(8f);

            var bloom = p.Add<Bloom>(true);
            bloom.threshold.Override(1.0f);
            bloom.intensity.Override(0.7f);
            bloom.scatter.Override(0.66f);
            bloom.tint.Override(new Color(1f, 0.96f, 0.9f));

            var vignette = p.Add<Vignette>(true);
            vignette.intensity.Override(0.3f);
            vignette.smoothness.Override(0.5f);

            Persist(p);
            return p;
        }

        /// <summary>
        /// What a punch out of the ordinary does to the picture at full weight, for ImpactVfx to fade in and out
        /// over the house look: the lamps flare, the lens gives, the edges fringe, and what moves smears.
        /// </summary>
        [MenuItem("PoBox/Dev/Bake Hit Pulse Profile")]
        public static VolumeProfile Pulse()
        {
            VolumeProfile p = Profile(PulsePath);

            var bloom = p.Add<Bloom>(true);
            bloom.intensity.Override(2.2f);
            bloom.threshold.Override(0.8f);

            var lens = p.Add<LensDistortion>(true);
            lens.intensity.Override(-0.22f);
            lens.scale.Override(1.03f);

            var fringe = p.Add<ChromaticAberration>(true);
            fringe.intensity.Override(0.45f);

            var blur = p.Add<MotionBlur>(true);
            blur.intensity.Override(0.5f);
            blur.quality.Override(MotionBlurQuality.Low);

            Persist(p);
            return p;
        }

        // ---------------------------------------------------------------- UI

        /// <summary>
        /// The panel the HUD is drawn on: a 1080 x 1920 reference, scaled to the device with Expand, which
        /// takes the smaller of the two scale factors. A taller phone gets its full designed width and
        /// spare height; nothing is ever cropped on either axis, which is what keeps the layout to one screen.
        /// </summary>
        public static PanelSettings Panel()
        {
            EnsureFolder(UiDir);
            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelPath);
            if (panel == null)
            {
                panel = ScriptableObject.CreateInstance<PanelSettings>();
                AssetDatabase.CreateAsset(panel, PanelPath);
            }
            var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(UiDir + "/PoBox.tss");
            if (theme != null) panel.themeStyleSheet = theme;
            else Debug.LogWarning("[PoBox] PoBox.tss did not import as a theme; the panel will use Unity's default.");

            panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            panel.referenceResolution = new Vector2Int(1080, 1920);
            panel.screenMatchMode = PanelScreenMatchMode.Expand;
            panel.clearColor = false;
            panel.targetTexture = null;
            EditorUtility.SetDirty(panel);
            return panel;
        }

        // ---------------------------------------------------------------- ladder

        public static PolicyProfile[] Profiles()
        {
            EnsureFolder(LeagueDir);
            return new[]
            {
                Profile("JABBER",  0.55f, 1.02f, 1.00f, 0.25f, 0.10f, 0.10f, 0.05f, 0.50f, 0.70f),
                Profile("SLUGGER", 0.80f, 0.86f, 0.75f, 0.35f, 0.45f, 0.15f, 0.15f, 0.20f, 0.30f),
                Profile("SWARMER", 0.95f, 0.82f, 1.20f, 0.60f, 0.25f, 0.35f, 0.10f, 0.60f, 0.40f),
                Profile("COUNTER", 0.35f, 1.08f, 1.10f, 0.45f, 0.20f, 0.10f, 0.10f, 0.70f, 0.80f),
                Profile("ROOKIE",  0.40f, 0.95f, 0.70f, 0.10f, 0.30f, 0.10f, 0.05f, 0.10f, 0.20f),
                Profile("TANK",    0.60f, 0.90f, 0.55f, 0.20f, 0.30f, 0.30f, 0.20f, 0.15f, 0.90f),
            };
        }

        static PolicyProfile Profile(string name, float aggression, float range, float footSpeed, float combo,
                                     float hook, float body, float uppercut, float headMovement, float guard)
        {
            string path = $"{LeagueDir}/Profile_{name}.asset";
            var p = AssetDatabase.LoadAssetAtPath<PolicyProfile>(path);
            if (p == null)
            {
                p = ScriptableObject.CreateInstance<PolicyProfile>();
                AssetDatabase.CreateAsset(p, path);
            }
            p.displayName = name;
            p.aggression = aggression; p.range = range; p.footSpeed = footSpeed; p.combo = combo;
            p.hookBias = hook; p.bodyBias = body; p.uppercutBias = uppercut; p.headMovement = headMovement; p.guard = guard;
            EditorUtility.SetDirty(p);
            return p;
        }
    }
}
