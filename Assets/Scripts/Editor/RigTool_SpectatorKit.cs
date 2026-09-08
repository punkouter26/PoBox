using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace PoBox.Editor
{
    /// <summary>
    /// Builds the assets the spectator layer loads at runtime: the additive
    /// overlay material, one <see cref="Systems_BrainDossier"/> per brain under
    /// Assets/Agents, and the <see cref="Systems_SpectatorKit"/> in Resources
    /// that ties them together.
    ///
    /// It creates ASSETS ONLY and never opens, edits or saves a scene. That is
    /// deliberate: regenerating a contest scene is the documented way to
    /// destroy one (CLAUDE.md on <c>BuildAll</c>), and the three systems this
    /// kit feeds are attached at runtime by
    /// <c>Systems_ContestSpawner.EnsureSpectatorSystems</c> precisely so that
    /// no scene has to be touched to gain them.
    ///
    /// Idempotent, and deliberately conservative about what it overwrites: the
    /// machine-readable half of a dossier is refreshed from SOURCE.txt and from
    /// the ONNX every run, while a headline statistic that someone has already
    /// authored is left alone. See <see cref="HeadlineSeeds"/>.
    /// </summary>
    public static class RigTool_SpectatorKit
    {
        private const string RESOURCES_DIR = "Assets/Resources";
        private const string KIT_PATH = RESOURCES_DIR + "/SpectatorKit.asset";
        private const string MATERIAL_PATH = RESOURCES_DIR + "/FX_SpectatorOverlay.mat";
        private const string AGENTS_DIR = "Assets/Agents";
        private const string DOSSIER_FILE = "DOSSIER.asset";

        private static readonly string[] ImpactClipPaths =
        {
            "Assets/Audio/Contest/impactSoft_heavy_000.ogg",
            "Assets/Audio/Contest/impactSoft_heavy_001.ogg",
            "Assets/Audio/Contest/impactSoft_heavy_002.ogg"
        };

        /// <summary>
        /// Headline statistics for the brains that ship today, keyed by the
        /// folder name under Assets/Agents. Every number here is copied from the
        /// "What ships" table in CLAUDE.md and was measured by
        /// <c>Tools/eval_candidates.ps1</c>, not estimated.
        ///
        /// SEEDS, NOT TRUTH. They are written only into a dossier whose headline
        /// is still empty, so re-running this tool never clobbers a figure
        /// someone has since corrected — and a brain promoted after this table
        /// was written simply gets a blank headline rather than an inherited
        /// lie. A dossier's step count and observation width are refreshed from
        /// the artifacts themselves every run and need no table.
        /// </summary>
        private static readonly Dictionary<string, string> HeadlineSeeds = new()
        {
            { "Locomotion_gen25", "167.9 steps between falls" },
            { "Locomotion_gen20", "91.7 steps between falls" },
            { "Locomotion_gen18_34M", "alternation 0.601" }
        };

        private static readonly Dictionary<string, string> NoteSeeds = new()
        {
            { "Locomotion_gen25", "Balance brain: trained with commanded speed pinned at 0. Cannot walk." },
            { "Locomotion_gen20", "Superseded by gen25 in the balance ring." },
            { "Locomotion_gen18_34M", "The walk brain: still the only one that actually WALKS." },
            { "RaptorBalance01", "The raptor's own model line, 13-joint rig. The shared 127-observation brain cannot load on it." }
        };

        [MenuItem("Tools/ML Boxing/17. Build Spectator Kit")]
        public static void Build()
        {
            Directory.CreateDirectory(RESOURCES_DIR);

            Material overlay = BuildOverlayMaterial();
            Systems_BrainDossier[] dossiers = BuildDossiers();
            AudioClip[] clips = LoadImpactClips();

            var kit = AssetDatabase.LoadAssetAtPath<Systems_SpectatorKit>(KIT_PATH);
            if (kit == null)
            {
                kit = ScriptableObject.CreateInstance<Systems_SpectatorKit>();
                AssetDatabase.CreateAsset(kit, KIT_PATH);
            }
            kit.overlayMaterial = overlay;
            kit.dossiers = dossiers;
            kit.impactClips = clips;
            EditorUtility.SetDirty(kit);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"RigTool: spectator kit built — {dossiers.Length} dossier(s), " +
                      $"{clips.Length} impact clip(s), overlay material " +
                      $"{(overlay != null ? "ok" : "MISSING")}. Kit at {KIT_PATH}.");
        }

        /// <summary>
        /// The additive unlit material every overlay in the spectator layer
        /// draws with — joint-stress glows and impact scuffs alike.
        ///
        /// Additive rather than alpha-blended because these are LIGHT, not
        /// surfaces: a glow over a dark ring should brighten it and a glow over
        /// a bright fighter should wash out, and neither should ever occlude the
        /// body behind it. ZWrite is off for the same reason. The colour is
        /// supplied per draw through a MaterialPropertyBlock, so this asset
        /// carries white and is never edited at runtime.
        /// </summary>
        private static Material BuildOverlayMaterial()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(MATERIAL_PATH);
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
            {
                Debug.LogError("RigTool: 'Universal Render Pipeline/Unlit' not found — " +
                               "spectator overlays will be disabled.");
                return existing;
            }

            Material material = existing;
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, MATERIAL_PATH);
            }
            material.shader = shader;
            material.SetColor("_BaseColor", Color.white);
            // Transparent surface, additive blend. Set as raw render state as
            // well as through URP's _Surface/_Blend shorthand: the shorthand is
            // what the inspector reads, the render state is what actually draws,
            // and a material built from code gets neither for free.
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 2f);
            material.SetFloat("_SrcBlend", (float)BlendMode.One);
            material.SetFloat("_DstBlend", (float)BlendMode.One);
            material.SetFloat("_ZWrite", 0f);
            material.SetFloat("_AlphaClip", 0f);
            material.SetFloat("_Cull", (float)CullMode.Back);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            material.renderQueue = (int)RenderQueue.Transparent;
            EditorUtility.SetDirty(material);
            return material;
        }

        private static AudioClip[] LoadImpactClips()
        {
            var clips = new List<AudioClip>(ImpactClipPaths.Length);
            for (int pathIndex = 0; pathIndex < ImpactClipPaths.Length; pathIndex++)
            {
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(ImpactClipPaths[pathIndex]);
                if (clip != null)
                {
                    clips.Add(clip);
                }
                else
                {
                    Debug.LogWarning($"RigTool: impact clip not found at {ImpactClipPaths[pathIndex]} — " +
                                     "impacts will be quieter than intended.");
                }
            }
            return clips.ToArray();
        }

        /// <summary>
        /// One dossier per folder under Assets/Agents that contains an .onnx.
        /// The folder is the unit because that is what
        /// <c>Tools/promote_brain.ps1</c> writes and what SOURCE.txt describes.
        /// </summary>
        private static Systems_BrainDossier[] BuildDossiers()
        {
            var dossiers = new List<Systems_BrainDossier>();
            if (!Directory.Exists(AGENTS_DIR))
            {
                Debug.LogWarning($"RigTool: {AGENTS_DIR} not found — no dossiers built.");
                return dossiers.ToArray();
            }

            string[] folders = Directory.GetDirectories(AGENTS_DIR);
            for (int folderIndex = 0; folderIndex < folders.Length; folderIndex++)
            {
                string folder = folders[folderIndex].Replace('\\', '/');
                string brainName = Path.GetFileName(folder);
                // _Candidates holds staged checkpoints that do not ship; a
                // dossier for one would advertise a brain no roster points at.
                if (brainName.StartsWith("_"))
                {
                    continue;
                }
                ModelAsset model = FindModel(folder);
                if (model == null)
                {
                    continue;
                }
                dossiers.Add(BuildDossier(folder, brainName, model));
            }
            return dossiers.ToArray();
        }

        private static Systems_BrainDossier BuildDossier(string folder, string brainName, ModelAsset model)
        {
            string path = folder + "/" + DOSSIER_FILE;
            var dossier = AssetDatabase.LoadAssetAtPath<Systems_BrainDossier>(path);
            bool created = dossier == null;
            if (created)
            {
                dossier = ScriptableObject.CreateInstance<Systems_BrainDossier>();
                AssetDatabase.CreateAsset(dossier, path);
            }

            dossier.model = model;
            dossier.brainLabel = brainName;
            // Authoritative, and the only field here that cannot be wrong about
            // itself — brain folder names have historically lied about which
            // generation they hold, obs_0's width has not.
            dossier.observationCount = Systems_BrainCompatibility.ObservationWidth(model);

            string source = ReadSource(folder);
            dossier.runId = ParseRunId(source);
            dossier.trainingSteps = ParseSteps(source);

            // Seeded only when empty — see HeadlineSeeds.
            if (string.IsNullOrEmpty(dossier.headlineStat) &&
                HeadlineSeeds.TryGetValue(brainName, out string headline))
            {
                dossier.headlineStat = headline;
            }
            if (string.IsNullOrEmpty(dossier.note) &&
                NoteSeeds.TryGetValue(brainName, out string note))
            {
                dossier.note = note;
            }

            EditorUtility.SetDirty(dossier);
            return dossier;
        }

        private static ModelAsset FindModel(string folder)
        {
            string[] files = Directory.GetFiles(folder, "*.onnx", SearchOption.TopDirectoryOnly);
            for (int fileIndex = 0; fileIndex < files.Length; fileIndex++)
            {
                string assetPath = files[fileIndex].Replace('\\', '/');
                var model = AssetDatabase.LoadAssetAtPath<ModelAsset>(assetPath);
                if (model != null)
                {
                    return model;
                }
            }
            return null;
        }

        private static string ReadSource(string folder)
        {
            string path = folder + "/SOURCE.txt";
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }

        /// <summary>
        /// The training run.
        ///
        /// THREE PATTERNS BECAUSE THERE ARE THREE FILES AND NO FORMAT. SOURCE.txt
        /// is prose written by hand at promotion time, and the four that exist
        /// today open three different ways: "boxer_locomotion18, checkpoint ...",
        /// "From run 'boxer_locomotion25', checkpoint ...", and the raptor's
        /// aligned "TRAINED  ... run raptor_balance01, checkpoint ...". Each
        /// pattern below was written against one of them. Empty when none match,
        /// which is honest: a guessed run id is worse than none.
        /// </summary>
        private static string ParseRunId(string source)
        {
            if (string.IsNullOrEmpty(source))
            {
                return string.Empty;
            }
            // Quoted first: "From run 'boxer_locomotion25'" also satisfies the
            // bare-word pattern below but only the quotes delimit it exactly.
            Match quoted = Regex.Match(source, @"run\s+'([^']+)'");
            if (quoted.Success)
            {
                return quoted.Groups[1].Value;
            }
            // Then the opening clause, "boxer_locomotion20, checkpoint ...".
            // BEFORE the loose pattern, not after: gen 20's file goes on to say
            // "The run was stopped early, on request", and a bare
            // run-followed-by-a-word match reads that as a run named "was".
            Match leading = Regex.Match(source, @"^\s*([A-Za-z0-9_\-]+)\s*,");
            if (leading.Success)
            {
                return leading.Groups[1].Value;
            }
            // Last, the raptor's "run raptor_balance01,". Required to contain a
            // digit or an underscore, which every run id in this project has
            // (boxer_locomotion18, raptor_balance01) and no English word does —
            // the cheapest available guard against matching prose.
            Match named = Regex.Match(source, @"\brun\s+([A-Za-z0-9\-]*[_0-9][A-Za-z0-9_\-]*)");
            return named.Success ? named.Groups[1].Value : string.Empty;
        }

        /// <summary>
        /// The checkpoint step. Read from the CHECKPOINT FILENAME first
        /// ("Boxer-33999872.onnx"), because that is what the exporter itself
        /// wrote and what <c>Tools/promote_brain.ps1</c> records provenance
        /// from — as opposed to the prose around it, which is what somebody
        /// believed the run had reached. Falls back to the "step N" phrase.
        /// </summary>
        private static long ParseSteps(string source)
        {
            if (string.IsNullOrEmpty(source))
            {
                return 0;
            }
            Match fromFile = Regex.Match(source, @"-(\d{4,})\.onnx");
            if (fromFile.Success && long.TryParse(fromFile.Groups[1].Value, out long fileSteps))
            {
                return fileSteps;
            }
            // The raptor's file names its checkpoint without the extension
            // ("checkpoint Boxer-8999875"), and its only spelled-out step count
            // is the rounded "9.0M steps" — which the prose pattern below would
            // read as 9. Anchoring on the word "checkpoint" keeps this from
            // matching an unrelated hyphenated number elsewhere in the prose.
            Match fromCheckpoint = Regex.Match(source, @"checkpoint\s+\S*?-(\d{4,})");
            if (fromCheckpoint.Success &&
                long.TryParse(fromCheckpoint.Groups[1].Value, out long checkpointSteps))
            {
                return checkpointSteps;
            }
            Match fromProse = Regex.Match(source, @"step\s+([\d,]+)");
            if (fromProse.Success &&
                long.TryParse(fromProse.Groups[1].Value.Replace(",", string.Empty), out long proseSteps))
            {
                return proseSteps;
            }
            return 0;
        }
    }
}
