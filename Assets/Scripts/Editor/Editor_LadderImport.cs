using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PoBox.Editor
{
    /// <summary>
    /// Copies the generation ladder's ratings out of <c>Tools/ladder/ladder.json</c>
    /// and into the brain dossiers, so the standing a brain has earned shows up
    /// on the tale of the tape beside its provenance.
    ///
    /// WHY IT IS AN IMPORT AND NOT A RUNTIME READ. Same reason the rest of
    /// <see cref="Systems_BrainDossier"/> is authored rather than parsed: a
    /// shipped build never opens a file under Tools/, and a phone has no Python
    /// to recompute a rating with. The ladder is computed offline from the eval
    /// reports and the ANSWER is baked into an asset.
    ///
    /// IT REFUSES A RATING WHOSE OBSERVATION WIDTH DOES NOT MATCH THE BRAIN IN
    /// THE FOLDER, and that check is the reason this is a tool rather than a
    /// one-line copy. The ladder records the width every duel was fought at —
    /// which the eval harness read from the model's own obs_0 input through the
    /// Inference Engine — and the dossier records the width of the .onnx
    /// currently sitting in <c>Assets/Agents/&lt;name&gt;/</c>. CLAUDE.md
    /// records that brain folder names in this project have historically lied
    /// about which generation they contain. If those two widths disagree, the
    /// folder no longer holds the thing the ladder rated, and writing the
    /// rating in anyway would put a measured number on a card beside a brain
    /// that never earned it.
    ///
    /// Brains on the ladder with no folder — the heuristic PD bot, and the
    /// staged <c>_Candidates</c> checkpoints — are skipped and counted, not
    /// warned about one by one: the bot has no dossier by design, and a
    /// candidate is meant to be transient.
    /// </summary>
    public static class Editor_LadderImport
    {
        private const string LADDER_PATH = "Tools/ladder/ladder.json";
        private const string AGENTS_DIR = "Assets/Agents";
        private const string DOSSIER_FILE = "DOSSIER.asset";

        [Serializable]
        private sealed class LadderEntry
        {
            public string brain;
            public float elo;
            public int rank;
            public int entrants;
            public int duels;
            public int observationWidth = -1;
        }

        [Serializable]
        private sealed class Ladder
        {
            public string generated;
            public int duels;
            public LadderEntry[] brains;
        }

        [MenuItem("PoBox/Brains/Import Ladder Ratings")]
        public static void Import()
        {
            string path = Path.Combine(ProjectRoot(), LADDER_PATH);
            if (!File.Exists(path))
            {
                Debug.LogWarning($"Editor_LadderImport: no {LADDER_PATH}. Run " +
                                 "\"python Tools/ladder.py eval/*.json\" first — the ladder is " +
                                 "built from eval-harness reports, which eval_candidates.ps1 produces.");
                return;
            }

            Ladder ladder;
            try
            {
                ladder = JsonUtility.FromJson<Ladder>(File.ReadAllText(path));
            }
            catch (ArgumentException exception)
            {
                Debug.LogError($"Editor_LadderImport: {LADDER_PATH} is not readable: {exception.Message}");
                return;
            }
            if (ladder?.brains == null || ladder.brains.Length == 0)
            {
                Debug.LogWarning($"Editor_LadderImport: {LADDER_PATH} lists no brains.");
                return;
            }

            int written = 0;
            int skipped = 0;
            var refused = new List<string>();
            for (int entryIndex = 0; entryIndex < ladder.brains.Length; entryIndex++)
            {
                LadderEntry entry = ladder.brains[entryIndex];
                if (entry == null || string.IsNullOrEmpty(entry.brain))
                {
                    continue;
                }
                string assetPath = $"{AGENTS_DIR}/{entry.brain}/{DOSSIER_FILE}";
                var dossier = AssetDatabase.LoadAssetAtPath<Systems_BrainDossier>(assetPath);
                if (dossier == null)
                {
                    skipped++;
                    continue;
                }
                if (entry.observationWidth >= 0 && dossier.observationCount >= 0 &&
                    entry.observationWidth != dossier.observationCount)
                {
                    refused.Add($"{entry.brain} (ladder {entry.observationWidth} obs, " +
                                $"folder holds {dossier.observationCount})");
                    continue;
                }
                dossier.eloRating = entry.elo;
                dossier.ladderRank = entry.rank;
                dossier.ladderEntrants = entry.entrants;
                EditorUtility.SetDirty(dossier);
                written++;
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"Editor_LadderImport: {written} dossier(s) rated from {ladder.duels} duel(s) " +
                      $"generated {ladder.generated}; {skipped} ladder entrant(s) have no dossier.");
            if (refused.Count > 0)
            {
                // An ERROR, not a warning. This is the silent-failure family
                // Systems_BrainCompatibility exists to make loud, arriving by a
                // different door: the folder contents changed under a name the
                // ladder still knows.
                Debug.LogError("Editor_LadderImport: REFUSED " + refused.Count +
                               " rating(s) whose observation width does not match the brain now in " +
                               "the folder — " + string.Join("; ", refused) +
                               ". Re-measure that folder and rebuild the ladder, or check that the " +
                               "folder still holds the generation its name claims.");
            }
        }

        /// <summary>
        /// The project root, which is Assets/'s parent. Resolved rather than
        /// assumed to be the working directory: a headless -executeMethod run
        /// starts wherever the caller was.
        /// </summary>
        private static string ProjectRoot()
        {
            DirectoryInfo parent = Directory.GetParent(Application.dataPath);
            return parent != null ? parent.FullName : Application.dataPath;
        }
    }
}
