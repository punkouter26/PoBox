using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace PoBox.League
{
    /// <summary>
    /// The cup: eight boxers, single elimination, seeded by the ladder's ratings (1 v 8, 4 v 5, 2 v 7, 3 v 6, so
    /// the top two can only meet in the final). With fewer than eight the top seeds get byes. The bracket is kept
    /// in Application.persistentDataPath/career.json and carried on from bout to bout, across sessions.
    ///
    /// The first screen starts or continues it and fights its next bout (setting <see cref="Live"/>); the arena,
    /// at the result of that bout, reports it (<see cref="Report"/>). A draw decides nothing: the bout is fought
    /// again. Damage and fatigue are not carried from one bout to the next.
    /// </summary>
    [Serializable]
    public class Career
    {
        public const int Size = 8, Matches = Size - 1;
        static readonly int[] SeedSlot = { 0, 7, 3, 4, 1, 6, 2, 5 };       // slot k holds seed SeedSlot[k] + 1
        static readonly string[] RoundNames = { "QF", "QF", "QF", "QF", "SF", "SF", "FINAL" };

        /// <summary>The bracket's first round, by slot: a boxer's folder name, or empty for a bye.</summary>
        public string[] slots = new string[Size];
        /// <summary>What each name is shown as.</summary>
        public string[] shown = new string[Size];
        /// <summary>The winner of each match (0 to 3 the first round, 4 and 5 the semi-finals, 6 the final), empty until fought.</summary>
        public string[] won = new string[Matches];

        /// <summary>A bout of the cup's is being fought: the arena reports its result.</summary>
        public static bool Live;

        static string FilePath => Path.Combine(Application.persistentDataPath, "career.json");

        public static Career Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                var c = JsonUtility.FromJson<Career>(File.ReadAllText(FilePath));
                return c != null && c.slots != null && c.slots.Length == Size && c.won != null && c.won.Length == Matches ? c : null;
            }
            catch (Exception) { return null; }
        }

        public void Save()
        {
            try { File.WriteAllText(FilePath, JsonUtility.ToJson(this)); }
            catch (Exception e) { Debug.LogWarning("[Career] not saved: " + e.Message); }
        }

        public static void Delete()
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); }
            catch (Exception) { }
        }

        /// <summary>A new cup from these boxers (folder name, shown name), best rated first; the first eight are in it.</summary>
        public static Career New(IList<(string name, string shown)> boxers)
        {
            var seeded = new List<(string name, string shown)>(boxers);
            seeded.Sort((a, b) => LeagueTable.SavedElo(b.shown).CompareTo(LeagueTable.SavedElo(a.shown)));
            var c = new Career();
            for (int k = 0; k < Size; k++)
            {
                int seed = SeedSlot[k];
                c.slots[k] = seed < seeded.Count ? seeded[seed].name : "";
                c.shown[k] = seed < seeded.Count ? seeded[seed].shown : "";
            }
            for (int m = 0; m < Matches; m++) c.won[m] = "";
            c.Resolve();
            c.Save();
            return c;
        }

        /// <summary>The two sides of a match: names, or empty for a bye, or null while not yet known.</summary>
        public void Sides(int match, out string a, out string b)
        {
            if (match < 4) { a = slots[2 * match]; b = slots[2 * match + 1]; return; }
            int first = match == 6 ? 4 : (match - 4) * 2;
            a = Known(won[first]); b = Known(won[first + 1]);
        }

        static string Known(string w) => string.IsNullOrEmpty(w) ? null : w == "-" ? "" : w;

        /// <summary>A side with nobody against it goes through; two byes put "-" (nobody) through.</summary>
        void Resolve()
        {
            for (int m = 0; m < Matches; m++)
            {
                if (!string.IsNullOrEmpty(won[m])) continue;
                Sides(m, out string a, out string b);
                if (a == null || b == null) continue;
                if (a.Length == 0) won[m] = b.Length == 0 ? "-" : b;
                else if (b.Length == 0) won[m] = a;
            }
        }

        /// <summary>The next bout to fight, red and blue (folder names). False when the cup is won.</summary>
        public bool Next(out int match, out string red, out string blue)
        {
            for (match = 0; match < Matches; match++)
            {
                if (!string.IsNullOrEmpty(won[match])) continue;
                Sides(match, out red, out blue);
                if (!string.IsNullOrEmpty(red) && !string.IsNullOrEmpty(blue)) return true;
            }
            red = blue = null;
            return false;
        }

        /// <summary>The cup's winner (folder name), or null while it runs.</summary>
        public string Champion => Known(won[Matches - 1]);

        /// <summary>A bout's result: counted if it is the cup's next bout. Winner 0 red, 1 blue, anything else a draw (fought again).</summary>
        public bool Report(string red, string blue, int winner)
        {
            if (!Next(out int m, out string r, out string b) || r != red || b != blue || (winner != 0 && winner != 1)) return false;
            won[m] = winner == 0 ? red : blue;
            Resolve();
            Save();
            return true;
        }

        public string Shown(string name)
        {
            if (string.IsNullOrEmpty(name)) return "BYE";
            for (int k = 0; k < Size; k++) if (slots[k] == name) return shown[k];
            return name.ToUpperInvariant();
        }

        /// <summary>The bracket, one line a match: "QF  MATT v ZOMBIE  > MATT".</summary>
        public string Line(int match)
        {
            Sides(match, out string a, out string b);
            var s = new StringBuilder(RoundNames[match]).Append("  ");
            s.Append(a == null ? "?" : Shown(a)).Append(" v ").Append(b == null ? "?" : Shown(b));
            string w = Known(won[match]);
            if (!string.IsNullOrEmpty(w)) s.Append("  > ").Append(Shown(w));
            return s.ToString();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ClearLive() => Live = false;
    }
}
