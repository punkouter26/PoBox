using System.IO;
using PoBox.Audio;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PoBox.EditorTools
{
    /// <summary>
    /// The boxers' voices, made in code and written as WAV files (Assets/Audio/Synth/voice_KIND_REGISTER_N.wav):
    /// there are no recordings of grunts in the project. Each is a glottal pulse train with breath noise in it,
    /// through three vowel formants in parallel, under an envelope. Four kinds:
    ///   * effort: the short forced "hah" of a punch thrown;
    ///   * pain: the "oof" of one landing, the vowel closing as the air goes out;
    ///   * groan: long, low and wavering: hurt, on the canvas, spent;
    ///   * pant: a heavy breath out, mostly air;
    ///   * exhale: the short sharp "tss" through the teeth that goes with nearly every punch thrown.
    /// Each in a low (a man's) and a high (a woman's) register, four takes of each. The same seeds give the same
    /// files. Menu PoBox/Audio/Write Voice WAVs writes them and hands them to the AudioDirector of the arena and the
    /// free-for-all.
    /// </summary>
    public static class VoiceWavs
    {
        public enum Kind { Effort, Pain, Groan, Pant, Exhale }
        const int Rate = 22050, Takes = 4;
        const string Dir = "Assets/Audio/Synth";

        struct Recipe
        {
            public float seconds, f0, f0End, attack, breath, voice;
            public float width;          // the formants' bandwidths, times the voice's (0 = 1): air wants them wide
            public Vector3 from, to;     // F1, F2, F3 at the start and at the end, Hz
        }

        static Recipe Of(Kind kind)
        {
            switch (kind)
            {
                case Kind.Effort: return new Recipe { seconds = 0.17f, f0 = 150f, f0End = 118f, attack = 0.012f, breath = 0.35f, voice = 1f,
                    from = new Vector3(700f, 1200f, 2500f), to = new Vector3(640f, 1150f, 2450f) };
                case Kind.Pain: return new Recipe { seconds = 0.26f, f0 = 138f, f0End = 92f, attack = 0.006f, breath = 0.45f, voice = 1f,
                    from = new Vector3(620f, 1050f, 2400f), to = new Vector3(360f, 800f, 2300f) };
                case Kind.Groan: return new Recipe { seconds = 0.85f, f0 = 112f, f0End = 84f, attack = 0.09f, breath = 0.4f, voice = 0.9f,
                    from = new Vector3(520f, 950f, 2400f), to = new Vector3(450f, 820f, 2350f) };
                case Kind.Exhale: return new Recipe { seconds = 0.11f, f0 = 160f, f0End = 150f, attack = 0.004f, breath = 1f, voice = 0.04f, width = 6f,
                    from = new Vector3(1100f, 2600f, 4200f), to = new Vector3(900f, 2300f, 3900f) };
                default: return new Recipe { seconds = 0.42f, f0 = 120f, f0End = 105f, attack = 0.03f, breath = 1f, voice = 0.12f, width = 3f,
                    from = new Vector3(760f, 1250f, 2500f), to = new Vector3(650f, 1150f, 2450f) };
            }
        }

        static float Rand(System.Random rng, float lo, float hi) => lo + (float)rng.NextDouble() * (hi - lo);

        /// <summary>One take: samples at 22050 Hz, peak 0.8.</summary>
        public static float[] Samples(Kind kind, bool high, System.Random rng)
        {
            Recipe r = Of(kind);
            float pitch = Rand(rng, 0.92f, 1.08f) * (high ? 1.75f : 1f);
            float formants = Rand(rng, 0.95f, 1.05f) * (high ? 1.16f : 1f);
            float seconds = r.seconds * Rand(rng, 0.85f, 1.15f);
            int n = Mathf.CeilToInt(seconds * Rate);
            var data = new float[n];
            // Three resonators (two-pole) in parallel: the vowel.
            var y1 = new float[3];
            var y2 = new float[3];
            float[] gain = { 1f, 0.55f, 0.22f };
            float[] width = { 90f, 120f, 180f };
            float phase = 0f, wobble = Rand(rng, 4f, 7f), wobblePhase = Rand(rng, 0f, 6f), peak = 1e-6f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)Rate, u = t / seconds;
                float f0 = Mathf.Lerp(r.f0, r.f0End, u) * pitch * (1f + (kind == Kind.Groan ? 0.03f : 0.01f) * Mathf.Sin(2f * Mathf.PI * wobble * t + wobblePhase));
                phase += f0 / Rate;
                if (phase >= 1f) phase -= 1f;
                // A glottal pulse: opens slowly, closes sharply.
                float pulse = phase < 0.6f ? Mathf.Sin(Mathf.PI * phase / 0.6f) : -0.4f * Mathf.Sin(Mathf.PI * (phase - 0.6f) / 0.4f);
                float source = r.voice * pulse + r.breath * 0.6f * Rand(rng, -1f, 1f);
                Vector3 f = Vector3.Lerp(r.from, r.to, u) * formants;
                float s = 0f;
                for (int k = 0; k < 3; k++)
                {
                    float bw = width[k] * (high ? 1.2f : 1f) * (r.width > 0f ? r.width : 1f);
                    float rr = Mathf.Exp(-Mathf.PI * bw / Rate);
                    float b1 = 2f * rr * Mathf.Cos(2f * Mathf.PI * f[k] / Rate), b2 = -rr * rr;
                    float y = (1f - rr) * source + b1 * y1[k] + b2 * y2[k];
                    y2[k] = y1[k];
                    y1[k] = y;
                    s += gain[k] * y;
                }
                // Up over the attack, held, then let go over the last 60%.
                float env = Mathf.Min(1f, t / r.attack) * (u < 0.4f ? 1f : Mathf.Pow(1f - (u - 0.4f) / 0.6f, 1.6f));
                data[i] = s * env;
                peak = Mathf.Max(peak, Mathf.Abs(data[i]));
            }
            for (int i = 0; i < n; i++) data[i] *= 0.8f / peak;
            return data;
        }

        [MenuItem("PoBox/Audio/Write Voice WAVs")]
        static void Menu() => Debug.Log("[VoiceWavs] " + Write());

        public static string Write()
        {
            // It opens the arena and the free-for-all to hand the voices over: never over unsaved work.
            if (!FreeForAllAuthoring.Leave()) return "the open scene has unsaved changes: save or discard them first";
            Directory.CreateDirectory(Dir);
            var sets = new AudioDirector.VoiceSet[2];
            for (int reg = 0; reg < 2; reg++)
            {
                var set = new AudioDirector.VoiceSet();
                foreach (Kind kind in System.Enum.GetValues(typeof(Kind)))
                {
                    int k = (int)kind;
                    var rng = new System.Random(11 + k + 20 * reg);
                    var clips = new AudioClip[Takes];
                    for (int i = 0; i < Takes; i++)
                    {
                        string path = $"{Dir}/voice_{kind.ToString().ToLowerInvariant()}_{(reg == 0 ? "low" : "high")}_{i + 1}.wav";
                        AssetBakery.WriteWav(Path.GetFullPath(path), Samples(kind, reg == 1, rng), Rate);
                        AudioDatabase(path);
                        clips[i] = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                    }
                    switch (kind)
                    {
                        case Kind.Effort: set.effort = clips; break;
                        case Kind.Pain: set.pain = clips; break;
                        case Kind.Groan: set.groan = clips; break;
                        case Kind.Pant: set.pant = clips; break;
                        default: set.exhale = clips; break;
                    }
                }
                sets[reg] = set;
            }
            // Handed to the AudioDirector in each scene that has one.
            string open = EditorSceneManager.GetActiveScene().path, done = "";
            foreach (string scene in new[] { "Assets/Scenes/Arena.unity", "Assets/Scenes/Melee.unity" })
            {
                if (!File.Exists(scene)) continue;
                var s = EditorSceneManager.OpenScene(scene);
                foreach (var d in Object.FindObjectsByType<AudioDirector>(FindObjectsInactive.Include))
                {
                    d.lowVoice = sets[0];
                    d.highVoice = sets[1];
                    EditorUtility.SetDirty(d);
                    done += $" {Path.GetFileNameWithoutExtension(scene)}";
                }
                EditorSceneManager.SaveScene(s);
            }
            if (!string.IsNullOrEmpty(open)) EditorSceneManager.OpenScene(open);
            return $"{2 * System.Enum.GetValues(typeof(Kind)).Length * Takes} voice WAVs in {Dir}; given to the AudioDirector in{done}";
        }

        static void AudioDatabase(string path)
        {
            AssetDatabase.ImportAsset(path);
            // Short, played often, many at once: decompressed in memory, mono.
            if (AssetImporter.GetAtPath(path) is AudioImporter imp)
            {
                imp.forceToMono = true;
                var settings = imp.defaultSampleSettings;
                settings.loadType = AudioClipLoadType.DecompressOnLoad;
                imp.defaultSampleSettings = settings;
                imp.SaveAndReimport();
            }
        }
    }
}
