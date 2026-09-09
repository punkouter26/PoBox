using UnityEngine;
using Unity.InferenceEngine;

namespace PoBox
{
    /// <summary>
    /// What a fighter's brain IS, in the form a spectator can read: which run
    /// produced it, how many steps it trained for, and the one measured number
    /// that says why it ships.
    ///
    /// WHY THIS IS AN ASSET AND NOT PARSED AT RUNTIME. The provenance a brain
    /// carries today is <c>Assets/Agents/&lt;name&gt;/SOURCE.txt</c> — prose,
    /// written for a human, and the only machine-readable things in it are the
    /// run id, the checkpoint step and the observation count. The headline
    /// statistic is not: "167.9 steps between falls under shove against gen20's
    /// 91.7" is a sentence, not a field. So the parseable half is filled in by
    /// <c>SceneTool_SpectatorKit</c> from SOURCE.txt and the ONNX itself, and the
    /// headline is authored once and left alone. A shipped build never opens a
    /// .txt file.
    ///
    /// Brain folder names have historically lied about which generation they
    /// contain (see CLAUDE.md), which is exactly why
    /// <see cref="observationCount"/> is read from the model's own obs_0 input
    /// rather than typed in: it is the one number that cannot be wrong about
    /// itself.
    /// </summary>
    [CreateAssetMenu(fileName = "DOSSIER", menuName = "PoBox/Brain Dossier")]
    public sealed class Systems_BrainDossier : ScriptableObject
    {
        /// <summary>
        /// The brain this describes. Matched by reference, never by name — a
        /// roster entry points at a ModelAsset and so does this, so the two
        /// cannot drift apart the way two strings can.
        /// </summary>
        public ModelAsset model;

        /// <summary>Folder name under Assets/Agents, e.g. "Locomotion_gen25".</summary>
        public string brainLabel;

        /// <summary>Training run that produced it, e.g. "boxer_locomotion20".</summary>
        public string runId;

        /// <summary>Checkpoint step, from the checkpoint's own filename. 0 = unknown.</summary>
        public long trainingSteps;

        /// <summary>Width of the model's obs_0 input; -1 when it could not be read.</summary>
        public int observationCount = -1;

        /// <summary>
        /// The measured claim, e.g. "167.9 steps between falls". Authored by
        /// hand — see the class comment. Empty is fine; the card just omits it.
        /// </summary>
        public string headlineStat;

        /// <summary>One line on why this brain ships. Optional.</summary>
        [TextArea(2, 4)] public string note;

        /// <summary>Steps rendered the way a tale of the tape reads them: "34.0M", "4.5M", "—".</summary>
        public string StepsDisplay
        {
            get
            {
                if (trainingSteps <= 0)
                {
                    return "—";
                }
                if (trainingSteps >= 1_000_000)
                {
                    return (trainingSteps / 1_000_000f).ToString("0.#") + "M";
                }
                if (trainingSteps >= 1_000)
                {
                    return (trainingSteps / 1_000f).ToString("0.#") + "k";
                }
                return trainingSteps.ToString();
            }
        }
    }
}
