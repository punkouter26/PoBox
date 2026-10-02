using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Playables;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Timeline;
using Unity.Cinemachine;
using PoBox.Fx;

namespace PoBox.EditorTools
{
    /// <summary>
    /// The staging: what makes the hall a venue rather than a lit box. Light cookies for the lamps, the
    /// pipeline switches they need, decals for the canvas, the hall's ambient light from a photograph of a
    /// real boxing gym, the audio mixer, the walk-on Timeline, and the lightmap bake. Like
    /// <see cref="AssetBakery"/> it writes real assets that can be opened and changed in the editor, and is
    /// called by <see cref="PoBoxBuilder"/>.
    /// </summary>
    public static class StageBakery
    {
        public const string TimelineDir = "Assets/Timeline";
        public const string MixerPath = "Assets/Audio/PoBox.mixer";
        public const string HdriPath = "Assets/Textures/PolyHaven/basement_boxing_ring_1k.hdr";
        const string DecalTemplate = "Packages/com.unity.render-pipelines.universal/Runtime/Materials/Decal.mat";

        // ---------------------------------------------------------------- pipeline

        /// <summary>
        /// The two things the staging needs from the render pipeline that are not on by default in the
        /// assets copied from PoDecath: light cookies on both tiers, and the decal renderer feature on both
        /// renderers.
        /// </summary>
        public static void PreparePipeline()
        {
            foreach (string tier in new[] { "PC", "Mobile" })
            {
                var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>($"{AssetBakery.SettingsDir}/{tier}_RPAsset.asset");
                if (asset != null)
                {
                    var so = new SerializedObject(asset);
                    SerializedProperty cookies = so.FindProperty("m_SupportsLightCookies");
                    if (cookies != null && !cookies.boolValue)
                    {
                        cookies.boolValue = true;
                        so.ApplyModifiedPropertiesWithoutUndo();
                        EditorUtility.SetDirty(asset);
                    }
                }
                EnsureDecalFeature($"{AssetBakery.SettingsDir}/{tier}_Renderer.asset");
            }
        }

        static void EnsureDecalFeature(string path)
        {
            var data = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(path);
            if (data == null) { Debug.LogWarning($"[PoBox] no renderer at {path}; decals will not draw on that tier."); return; }
            // No depth priming. With it the opaque pass draws only what matches the depth pre-pass exactly,
            // and a skinned mesh does not always: seen once the decals had brought a pre-pass in, as both
            // fighters drawn as nothing but their rim light.
            var renderer = new SerializedObject(data);
            SerializedProperty priming = renderer.FindProperty("m_DepthPrimingMode");
            if (priming != null && priming.intValue != 0)
            {
                priming.intValue = 0;
                renderer.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(data);
            }

            foreach (ScriptableRendererFeature f in data.rendererFeatures)
                if (f is DecalRendererFeature) { ScreenSpaceDecals(f); return; }

            var feature = ScriptableObject.CreateInstance<DecalRendererFeature>();
            feature.name = "Decals";
            AssetDatabase.AddObjectToAsset(feature, data);
            data.rendererFeatures.Add(feature);
            ScreenSpaceDecals(feature);
            // The renderer keeps the features' file ids in a list beside the features themselves.
            var so = new SerializedObject(data);
            SerializedProperty map = so.FindProperty("m_RendererFeatureMap");
            if (map != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out string _, out long id))
            {
                map.arraySize++;
                map.GetArrayElementAtIndex(map.arraySize - 1).longValue = id;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            data.SetDirty();
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
            Debug.Log($"[PoBox] added the decal renderer feature to {path}.");
        }

        /// <summary>
        /// Decals drawn in screen space, after the opaque pass, from the depth texture. The other way (the
        /// DBuffer) needs every opaque thing drawn a second time beforehand for its depth and normals: both
        /// fighters' meshes again, every frame, for the sake of a few marks on the floor.
        /// </summary>
        static void ScreenSpaceDecals(ScriptableRendererFeature feature)
        {
            var so = new SerializedObject(feature);
            SerializedProperty technique = so.FindProperty("m_Settings.technique");
            SerializedProperty blend = so.FindProperty("m_Settings.screenSpaceSettings.normalBlend");
            if (technique == null) { Debug.LogWarning("[PoBox] the decal feature's settings have moved; it is left on its default technique."); return; }
            if (technique.intValue == 2 && (blend == null || blend.intValue == 0)) return;
            technique.intValue = 2;                 // DecalTechniqueOption.ScreenSpace
            if (blend != null) blend.intValue = 0;  // DecalNormalBlend.Low: no normals texture needed
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(feature);
        }

        // ---------------------------------------------------------------- textures

        /// <summary>
        /// What a ring lamp throws: a soft-edged pool with the faint bars of its barn doors across it, so
        /// the canvas is lit unevenly, as by a real rig, rather than by four perfect cones.
        /// </summary>
        public static Texture2D LampCookie()
        {
            Texture2D t = AssetBakery.Tex("Cookie_Lamp", 256, (u, v) =>
            {
                float dx = u - 0.5f, dy = v - 0.5f;
                float r = Mathf.Sqrt(dx * dx + dy * dy) * 2f;
                float pool = Mathf.SmoothStep(1f, 0.55f, r);
                // Barn doors: the beam is cut a little harder top and bottom than left and right.
                float doors = Mathf.SmoothStep(0.98f, 0.72f, Mathf.Abs(dy) * 2f) * Mathf.SmoothStep(1.0f, 0.86f, Mathf.Abs(dx) * 2f);
                // The lens: concentric rings, faint.
                float lens = 0.93f + 0.07f * Mathf.Cos(r * 46f);
                float a = Mathf.Clamp01(pool * doors * lens);
                return new Color(a, a, a, a);
            }, false, true);
            SetCookieImport(t);
            return t;
        }

        /// <summary>A follow-spot: a hard-edged round beam with a bright core.</summary>
        public static Texture2D SweepCookie()
        {
            Texture2D t = AssetBakery.Tex("Cookie_Sweep", 256, (u, v) =>
            {
                float dx = u - 0.5f, dy = v - 0.5f;
                float r = Mathf.Sqrt(dx * dx + dy * dy) * 2f;
                float a = Mathf.SmoothStep(0.9f, 0.78f, r) * (0.7f + 0.3f * Mathf.SmoothStep(0.6f, 0f, r));
                return new Color(a, a, a, a);
            }, false, true);
            SetCookieImport(t);
            return t;
        }

        static void SetCookieImport(Texture2D t)
        {
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(t)) as TextureImporter;
            if (importer == null) return;
            if (importer.textureType == TextureImporterType.Cookie && importer.wrapMode == TextureWrapMode.Clamp) return;
            importer.textureType = TextureImporterType.Cookie;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.SaveAndReimport();
        }

        // Five by seven, one string a letter: what the canvas has printed on it.
        static readonly Dictionary<char, string[]> Letters = new Dictionary<char, string[]>
        {
            { 'P', new[] { "####.", "#...#", "#...#", "####.", "#....", "#....", "#...." } },
            { 'O', new[] { ".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###." } },
            { 'B', new[] { "####.", "#...#", "#...#", "####.", "#...#", "#...#", "####." } },
            { 'X', new[] { "#...#", "#...#", ".#.#.", "..#..", ".#.#.", "#...#", "#...#" } },
        };

        /// <summary>
        /// The promoter's mark in the middle of the canvas: the game's name in stencilled capitals inside a
        /// ring, printed a little worn. White on transparent; the decal lays it on the cloth.
        /// </summary>
        public static Texture2D LogoTexture()
        {
            const string word = "POBOX";
            const int cell = 6;                                   // five columns and a gap
            int columns = word.Length * cell - 1;
            return AssetBakery.Tex("Decal_Logo", 512, (u, v) =>
            {
                float dx = u - 0.5f, dy = v - 0.5f;
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                float a = 0f;
                // The ring.
                a = Mathf.Max(a, Mathf.SmoothStep(0.014f, 0.006f, Mathf.Abs(r - 0.44f)));
                a = Mathf.Max(a, Mathf.SmoothStep(0.006f, 0.002f, Mathf.Abs(r - 0.40f)));
                // The word, across the middle: each stencil cell is a soft-cornered block.
                float x = (u - 0.14f) / 0.72f * columns, y = (0.5f + 0.105f - v) / 0.21f * 7f;
                if (x >= 0f && x < columns && y >= 0f && y < 7f)
                {
                    int col = Mathf.FloorToInt(x), row = Mathf.FloorToInt(y);
                    int letter = col / cell, inLetter = col % cell;
                    if (inLetter < 5 && Letters[word[letter]][row][inLetter] == '#')
                    {
                        float fx = Mathf.Abs(x - col - 0.5f), fy = Mathf.Abs(y - row - 0.5f);
                        a = Mathf.Max(a, Mathf.SmoothStep(0.62f, 0.46f, Mathf.Max(fx, fy)));
                    }
                }
                // Two bars under and over the word.
                if (Mathf.Abs(dx) < 0.30f)
                {
                    a = Mathf.Max(a, Mathf.SmoothStep(0.010f, 0.004f, Mathf.Abs(dy - 0.16f)));
                    a = Mathf.Max(a, Mathf.SmoothStep(0.010f, 0.004f, Mathf.Abs(dy + 0.16f)));
                }
                // Wear: the print has come off in patches.
                float wear = 0.72f + 0.28f * Noise(u * 14f, v * 14f) - 0.25f * Mathf.SmoothStep(0.55f, 0.8f, Noise(u * 5f + 3f, v * 5f));
                return new Color(0.95f, 0.96f, 0.98f, Mathf.Clamp01(a * wear));
            }, true, true);
        }

        /// <summary>A wet spot: a dark blot with an uneven edge and a few drops flung beyond it.</summary>
        public static Texture2D SpotTexture()
        {
            return AssetBakery.Tex("Decal_Spot", 128, (u, v) =>
            {
                float dx = u - 0.5f, dy = v - 0.5f;
                float angle = Mathf.Atan2(dy, dx);
                float r = Mathf.Sqrt(dx * dx + dy * dy) * 2f;
                float edge = 0.55f + 0.16f * Mathf.Sin(angle * 5f + 1.3f) + 0.08f * Mathf.Sin(angle * 11f);
                float a = Mathf.SmoothStep(edge, edge - 0.25f, r);
                // Satellite drops.
                for (int i = 0; i < 6; i++)
                {
                    float t = i * 1.0472f + 0.4f;
                    var c = new Vector2(0.5f + Mathf.Cos(t) * (0.34f + 0.05f * (i % 3)), 0.5f + Mathf.Sin(t) * (0.34f + 0.05f * (i % 3)));
                    float d = Vector2.Distance(new Vector2(u, v), c);
                    a = Mathf.Max(a, Mathf.SmoothStep(0.045f, 0.02f, d) * 0.8f);
                }
                return new Color(0.02f, 0.03f, 0.05f, Mathf.Clamp01(a) * 0.62f);
            }, true, true);
        }

        /// <summary>A scuff: the canvas rubbed pale and dirty where something heavy landed or a boot turned.</summary>
        public static Texture2D ScuffTexture()
        {
            return AssetBakery.Tex("Decal_Scuff", 128, (u, v) =>
            {
                float dx = u - 0.5f, dy = (v - 0.5f) * 1.6f;
                float r = Mathf.Sqrt(dx * dx + dy * dy) * 2f;
                float streak = 0.5f + 0.5f * Noise(u * 3f, v * 40f);
                float a = Mathf.SmoothStep(0.95f, 0.2f, r) * streak * (0.5f + 0.5f * Noise(u * 22f, v * 22f));
                return new Color(0.05f, 0.05f, 0.06f, Mathf.Clamp01(a) * 0.5f);
            }, true, true);
        }

        static float Noise(float x, float y)
        {
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
            float fx = x - xi, fy = y - yi;
            float H(int a, int b)
            {
                float h = Mathf.Sin(a * 127.1f + b * 311.7f) * 43758.5453f;
                return h - Mathf.Floor(h);
            }
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            return Mathf.Lerp(Mathf.Lerp(H(xi, yi), H(xi + 1, yi), fx), Mathf.Lerp(H(xi, yi + 1), H(xi + 1, yi + 1), fx), fy);
        }

        // ---------------------------------------------------------------- materials

        /// <summary>
        /// A decal material: a copy of the pipeline's own default decal material (which has the keywords a
        /// decal needs already set) with this texture on it.
        /// </summary>
        public static Material Decal(string name, Texture2D texture)
        {
            AssetBakery.EnsureFolder(AssetBakery.MaterialDir);
            string path = $"{AssetBakery.MaterialDir}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                if (!AssetDatabase.CopyAsset(DecalTemplate, path))
                {
                    Shader shader = Shader.Find("Shader Graphs/Decal");
                    if (shader == null) { Debug.LogError("[PoBox] the decal shader is missing; no decals."); return null; }
                    AssetDatabase.CreateAsset(new Material(shader) { name = name }, path);
                }
                m = AssetDatabase.LoadAssetAtPath<Material>(path);
                m.name = name;
            }
            m.SetTexture("Base_Map", texture);
            m.SetFloat("Normal_Blend", 0f);
            EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>The photograph the hall is lit by. Not drawn: the camera clears to black. It is the ambient light and the reflections.</summary>
        public static Material Skybox()
        {
            var hdri = AssetDatabase.LoadAssetAtPath<Texture>(HdriPath);
            if (hdri == null) { Debug.LogWarning($"[PoBox] {HdriPath} is missing; the hall keeps its flat ambient light."); return null; }
            var importer = AssetImporter.GetAtPath(HdriPath) as TextureImporter;
            if (importer != null && (importer.textureShape != TextureImporterShape.Texture2D || importer.wrapModeU != TextureWrapMode.Repeat || importer.mipmapEnabled == false))
            {
                importer.textureShape = TextureImporterShape.Texture2D;
                importer.wrapModeU = TextureWrapMode.Repeat;
                importer.wrapModeV = TextureWrapMode.Clamp;
                importer.mipmapEnabled = true;
                importer.SaveAndReimport();
                hdri = AssetDatabase.LoadAssetAtPath<Texture>(HdriPath);
            }
            AssetBakery.EnsureFolder(AssetBakery.MaterialDir);
            string path = $"{AssetBakery.MaterialDir}/Hall_Sky.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            Shader shader = Shader.Find("Skybox/Panoramic");
            if (shader == null) return null;
            if (m == null)
            {
                m = new Material(shader) { name = "Hall_Sky" };
                AssetDatabase.CreateAsset(m, path);
            }
            m.SetTexture("_MainTex", hdri);
            m.SetFloat("_Exposure", 1f);
            m.SetFloat("_Rotation", 0f);
            EditorUtility.SetDirty(m);
            return m;
        }

        // ---------------------------------------------------------------- sound

        /// <summary>
        /// The intake of breath of a few hundred people: noise through a band a voice sits in, rising fast
        /// and falling slowly, with a second, slower band under it. Synthesised because the crowd recordings
        /// are all long: nothing in the set is a quarter-second gasp.
        /// </summary>
        public static AudioClip Gasp()
        {
            AssetBakery.EnsureFolder(AssetBakery.SynthDir);
            string path = $"{AssetBakery.SynthDir}/crowd_gasp.wav";
            const int rate = 44100;
            const float seconds = 0.9f;
            int n = Mathf.CeilToInt(seconds * rate);
            var samples = new float[n];
            var rng = new System.Random(4711);
            float lowA = 0f, lowB = 0f, highA = 0f;
            // One-pole filters: a band between about 350 and 1800 Hz, and a body under it at 180 to 500.
            float aLow = 1f - Mathf.Exp(-2f * Mathf.PI * 1800f / rate), aHigh = 1f - Mathf.Exp(-2f * Mathf.PI * 350f / rate);
            float bLow = 1f - Mathf.Exp(-2f * Mathf.PI * 500f / rate);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)rate;
                float white = (float)rng.NextDouble() * 2f - 1f;
                lowA += (white - lowA) * aLow;
                highA += (lowA - highA) * aHigh;
                float voice = lowA - highA;
                lowB += (white - lowB) * bLow;
                float envelope = Mathf.Clamp01(t / 0.07f) * Mathf.Exp(-Mathf.Max(0f, t - 0.07f) * 4.2f);
                float tail = Mathf.Clamp01((seconds - t) / 0.05f);
                samples[i] = (float)Math.Tanh((voice * 1.6f + lowB * 0.5f) * envelope * 2.2f) * 0.8f * tail;
            }
            AssetBakery.WriteWav(Path.GetFullPath(path), samples, rate);
            AssetDatabase.ImportAsset(path);
            return AssetDatabase.LoadAssetAtPath<AudioClip>(path);
        }

        public static AudioClip[] Clips(string folder, string stem, int count)
        {
            var clips = new List<AudioClip>();
            for (int i = 0; i < count; i++)
            {
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>($"{folder}/{stem}_{i:000}.ogg");
                if (clip != null) clips.Add(clip);
            }
            if (clips.Count == 0) Debug.LogWarning($"[PoBox] no sound files {folder}/{stem}_NNN.ogg.");
            return clips.ToArray();
        }

        /// <summary>
        /// The mixer: Master with three groups under it, Hits, Crowd and Ring, each with its volume exposed
        /// so the game can duck the crowd under a punch and the owner can set the balance in the Audio Mixer
        /// window. Unity has no public way to make a mixer from a script; this goes through the editor's own
        /// classes by reflection, and if they have changed it says so and the game mixes without one.
        /// </summary>
        public static AudioMixer Mixer()
        {
            var existing = AssetDatabase.LoadAssetAtPath<AudioMixer>(MixerPath);
            if (existing != null && existing.FindMatchingGroups("Hits").Length > 0 && existing.FindMatchingGroups("Crowd").Length > 0 && existing.FindMatchingGroups("Ring").Length > 0)
                return existing;
            try
            {
                AssetBakery.EnsureFolder("Assets/Audio");
                if (existing != null) AssetDatabase.DeleteAsset(MixerPath);
                Assembly editor = typeof(Editor).Assembly;
                Type controllerType = editor.GetType("UnityEditor.Audio.AudioMixerController", true);
                Type groupType = editor.GetType("UnityEditor.Audio.AudioMixerGroupController", true);
                Type pathType = editor.GetType("UnityEditor.Audio.AudioGroupParameterPath", true);
                const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

                object controller = controllerType.GetMethod("CreateMixerControllerAtPath", Any).Invoke(null, new object[] { MixerPath });
                object master = controllerType.GetProperty("masterGroup", Any).GetValue(controller);
                MethodInfo createGroup = controllerType.GetMethod("CreateNewGroup", Any);
                MethodInfo addChild = controllerType.GetMethod("AddChildToParent", Any);
                MethodInfo addToView = controllerType.GetMethod("AddGroupToCurrentView", Any);
                MethodInfo expose = controllerType.GetMethod("AddExposedParameter", Any);
                MethodInfo volumeGuid = groupType.GetMethod("GetGUIDForVolume", Any);

                var wanted = new Dictionary<string, string>();          // volume parameter's GUID -> the name to expose it under
                foreach (string name in new[] { "Hits", "Crowd", "Ring" })
                {
                    object group = createGroup.Invoke(controller, new object[] { name, false });
                    addChild.Invoke(controller, new[] { group, master });
                    // A mixer that has never been opened in the Audio Mixer window has no view to add the
                    // group to; the window makes one, with every group in it, when it is first opened.
                    try { addToView.Invoke(controller, new[] { group }); } catch (Exception) { }
                    object guid = volumeGuid.Invoke(group, null);
                    object parameter = pathType.GetConstructors(Any)[0].Invoke(new[] { group, guid });
                    expose.Invoke(controller, new[] { parameter });
                    wanted[guid.ToString()] = name + "Volume";
                }

                // Exposed parameters are given placeholder names, and are kept sorted, not in the order they were added.
                PropertyInfo exposed = controllerType.GetProperty("exposedParameters", Any);
                Array list = (Array)exposed.GetValue(controller);
                for (int i = 0; i < list.Length; i++)
                {
                    object entry = list.GetValue(i);
                    string guid = entry.GetType().GetField("guid", Any).GetValue(entry).ToString();
                    if (!wanted.TryGetValue(guid, out string name)) continue;
                    entry.GetType().GetField("name", Any).SetValue(entry, name);
                    list.SetValue(entry, i);
                }
                exposed.SetValue(controller, list);
                EditorUtility.SetDirty((UnityEngine.Object)controller);
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(MixerPath);
                var mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(MixerPath);
                Debug.Log($"[PoBox] made {MixerPath}: Master with Hits, Crowd and Ring, their volumes exposed.");
                return mixer;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PoBox] could not make an AudioMixer from a script ({e.GetBaseException().Message}); the game will mix with its own gains.");
                return null;
            }
        }

        // ---------------------------------------------------------------- the walk-on

        /// <summary>
        /// The walk-on, as a Timeline that can be opened and re-cut in the editor: a camera track that holds
        /// red's corner for the first half and blue's for the second, an animation track that brings the
        /// house lights down at the start and up again at the end, and one that swings each follow-spot
        /// onto its corner in turn.
        /// </summary>
        /// <param name="lighting">The object the two follow-spots hang under: the animation's paths start here.</param>
        public static PlayableDirector WalkOn(float seconds, GameObject host, CinemachineBrain brain, CinemachineCamera redShot, CinemachineCamera blueShot,
                                              GameObject lighting, Light sweepRed, Light sweepBlue, Vector3 redCorner, Vector3 blueCorner,
                                              ArenaMood mood, float sweepIntensity)
        {
            AssetBakery.EnsureFolder(TimelineDir);
            string path = $"{TimelineDir}/WalkOn.playable";
            // Rebuilt from nothing each time: a track left over from an older recipe would play too.
            if (AssetDatabase.LoadAssetAtPath<TimelineAsset>(path) != null) AssetDatabase.DeleteAsset(path);
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            AssetDatabase.CreateAsset(timeline, path);
            timeline.editorSettings.frameRate = 60;

            var director = host.AddComponent<PlayableDirector>();
            director.playableAsset = timeline;
            director.playOnAwake = false;
            director.extrapolationMode = DirectorWrapMode.None;
            // Driven by hand from the bout's own clock (BroadcastDirector), so the two cannot drift apart.
            director.timeUpdateMode = DirectorUpdateMode.Manual;

            float half = seconds * 0.5f;

            // ---- cameras
            var cameras = timeline.CreateTrack<CinemachineTrack>(null, "Cameras");
            director.SetGenericBinding(cameras, brain);
            Shot(cameras, director, redShot, "Red corner", 0f, half);
            Shot(cameras, director, blueShot, "Blue corner", half, seconds - half);

            // ---- house lights: down in the first half second, up in the last
            AnimationClip house = Clip("WalkOn_HouseLights");
            AnimationUtility.SetEditorCurve(house, EditorCurveBinding.FloatCurve("", typeof(ArenaMood), "houseLights"),
                new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(0.5f, 0.12f), new Keyframe(seconds - 0.6f, 0.12f), new Keyframe(seconds, 1f)));
            var houseTrack = timeline.CreateTrack<AnimationTrack>(null, "House lights");
            director.SetGenericBinding(houseTrack, Animated(mood.gameObject));
            Add(houseTrack, house, seconds);

            // ---- follow-spots: each starts pointing at the far side of the hall and swings onto its corner
            AnimationClip sweeps = Clip("WalkOn_Sweeps");
            Sweep(sweeps, sweepRed, lighting.transform, redCorner, 0.2f, half, sweepIntensity);
            Sweep(sweeps, sweepBlue, lighting.transform, blueCorner, half, seconds - 0.3f, sweepIntensity);
            var sweepTrack = timeline.CreateTrack<AnimationTrack>(null, "Follow-spots");
            director.SetGenericBinding(sweepTrack, Animated(lighting));
            Add(sweepTrack, sweeps, seconds);

            EditorUtility.SetDirty(timeline);
            AssetDatabase.SaveAssets();
            return director;
        }

        static void Shot(CinemachineTrack track, PlayableDirector director, CinemachineCamera camera, string name, float start, float duration)
        {
            TimelineClip clip = track.CreateClip<CinemachineShot>();
            clip.displayName = name;
            clip.start = start;
            clip.duration = duration;
            var shot = (CinemachineShot)clip.asset;
            shot.DisplayName = name;
            // A scene object referred to from an asset: by name, through the director's table.
            shot.VirtualCamera.exposedName = GUID.Generate().ToString();
            director.SetReferenceValue(shot.VirtualCamera.exposedName, camera);
        }

        static AnimationClip Clip(string name)
        {
            string path = $"{TimelineDir}/{name}.anim";
            if (AssetDatabase.LoadAssetAtPath<AnimationClip>(path) != null) AssetDatabase.DeleteAsset(path);
            var clip = new AnimationClip { name = name, frameRate = 60f };
            AssetDatabase.CreateAsset(clip, path);
            return clip;
        }

        static void Add(AnimationTrack track, AnimationClip clip, float seconds)
        {
            TimelineClip tc = track.CreateClip(clip);
            tc.start = 0.0;
            tc.duration = seconds;
            EditorUtility.SetDirty(clip);
        }

        static Animator Animated(GameObject go)
        {
            Animator a = go.GetComponent<Animator>();
            if (a == null) a = go.AddComponent<Animator>();
            a.updateMode = AnimatorUpdateMode.UnscaledTime;
            a.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            return a;
        }

        /// <summary>
        /// One follow-spot's part: off, then on and swinging from somewhere out in the stands onto the
        /// corner, holding there, then off. Written as curves on the light's own rotation and intensity.
        /// </summary>
        static void Sweep(AnimationClip clip, Light light, Transform root, Vector3 corner, float from, float until, float intensity)
        {
            string path = AnimationUtility.CalculateTransformPath(light.transform, root);
            Vector3 at = light.transform.position;
            Vector3 target = corner + Vector3.up * 1.2f;
            // Start pointing past the far ropes, on the other side of the ring from the corner.
            Vector3 away = new Vector3(-corner.x * 2.2f, 0.4f, -corner.z * 2.2f);
            Quaternion start = Quaternion.LookRotation(away - at), end = Quaternion.LookRotation(target - at);
            float arrive = Mathf.Lerp(from, until, 0.45f);

            var curves = new AnimationCurve[4];
            for (int i = 0; i < 4; i++) curves[i] = new AnimationCurve();
            void Key(float t, Quaternion q)
            {
                curves[0].AddKey(t, q.x); curves[1].AddKey(t, q.y); curves[2].AddKey(t, q.z); curves[3].AddKey(t, q.w);
            }
            // Local to the lighting group, which sits unrotated at the origin.
            Quaternion inv = Quaternion.Inverse(root.rotation);
            Key(0f, inv * start);
            Key(from, inv * start);
            Key(Mathf.Lerp(from, arrive, 0.5f), inv * Quaternion.Slerp(start, end, 0.6f));
            Key(arrive, inv * end);
            Key(until, inv * end);
            string[] names = { "m_LocalRotation.x", "m_LocalRotation.y", "m_LocalRotation.z", "m_LocalRotation.w" };
            for (int i = 0; i < 4; i++)
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(Transform), names[i]), curves[i]);

            var level = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(Mathf.Max(0f, from - 0.01f), 0f), new Keyframe(from + 0.25f, intensity),
                                           new Keyframe(until - 0.25f, intensity), new Keyframe(until, 0f));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(Light), "m_Intensity"), level);
        }

        // ---------------------------------------------------------------- baked light

        /// <summary>
        /// Bakes the hall. The lamps over the ring and the dim house lights are Mixed: what they do to the
        /// fighters stays real-time, what they do to the hall floor, the apron and the stands' steps is
        /// baked, bounce and all, and a grid of light probes over the ring carries that bounce onto the
        /// fighters. On the CPU, at a low resolution: the hall is large and plain, and the GPU may be busy
        /// training. Blocks until done.
        /// </summary>
        public static bool BakeLighting()
        {
            const string settingsPath = AssetBakery.SettingsDir + "/PoBox.lighting";
            if (AssetDatabase.LoadAssetAtPath<LightingSettings>(settingsPath) != null) AssetDatabase.DeleteAsset(settingsPath);
            var settings = new LightingSettings
            {
                name = "PoBox Lighting",
                lightmapper = LightingSettings.Lightmapper.ProgressiveCPU,
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
