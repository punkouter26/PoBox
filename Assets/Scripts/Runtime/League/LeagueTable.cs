using System;
using System.Collections.Generic;
using UnityEngine;

namespace PoBox.League
{
    /// <summary>
    /// The ladder. Every finished bout moves two Elo ratings, the table is kept on the device, and the next
    /// pairing is whoever has fought least against whoever is rated closest to them.
    /// </summary>
    public class LeagueTable : MonoBehaviour
    {
        public PolicyProfile[] roster = new PolicyProfile[0];
        public float startRating = 1000f;
        public float kFactor = 32f;

        [Serializable]
        public class Row
        {
            public string name;
            public float elo;
            public int wins, losses, draws, knockouts;
            public string lastOpponent;
            public int Bouts => wins + losses + draws;
        }

        [Serializable]
        class Save { public List<Row> rows = new List<Row>(); }

        const string Key = "pobox.league.v1";
        Save _save = new Save();

        public IReadOnlyList<Row> Rows => _save.rows;

        void Awake() => Load();

        void Load()
        {
            string json = PlayerPrefs.GetString(Key, "");
            if (!string.IsNullOrEmpty(json))
            {
                try { _save = JsonUtility.FromJson<Save>(json) ?? new Save(); }
                catch (Exception) { _save = new Save(); }
            }
            foreach (PolicyProfile p in roster)
                if (p != null && Find(p.displayName) == null)
                    _save.rows.Add(new Row { name = p.displayName, elo = startRating });
            // A profile that has left the roster leaves the ladder with it.
            _save.rows.RemoveAll(r => ProfileFor(r.name) == null);
        }

        void Store()
        {
            PlayerPrefs.SetString(Key, JsonUtility.ToJson(_save));
            PlayerPrefs.Save();
        }

        public void ResetAll()
        {
            _save = new Save();
            PlayerPrefs.DeleteKey(Key);
            Load();
        }

        public Row Find(string name)
        {
            foreach (Row r in _save.rows) if (r.name == name) return r;
            return null;
        }

        public PolicyProfile ProfileFor(string name)
        {
            foreach (PolicyProfile p in roster) if (p != null && p.displayName == name) return p;
            return null;
        }

        /// <summary>The ladder, best first.</summary>
        public List<Row> Sorted()
        {
            var rows = new List<Row>(_save.rows);
            rows.Sort((a, b) => b.elo.CompareTo(a.elo));
            return rows;
        }

        public int RankOf(string name)
        {
            List<Row> rows = Sorted();
            for (int i = 0; i < rows.Count; i++) if (rows[i].name == name) return i + 1;
            return 0;
        }

        public static float Expected(float a, float b) => 1f / (1f + Mathf.Pow(10f, (b - a) / 400f));

        /// <summary>Chance the first name beats the second on rating alone, 0..1.</summary>
        public float Expected(string a, string b)
        {
            Row ra = Find(a), rb = Find(b);
            return ra != null && rb != null ? Expected(ra.elo, rb.elo) : 0.5f;
        }

        /// <summary>
        /// Whoever has fought least, against the closest rating that is not the fighter they met last time.
        /// </summary>
        public bool PickNext(out PolicyProfile a, out PolicyProfile b)
        {
            a = null; b = null;
            if (_save.rows.Count < 2) return false;

            Row first = null;
            foreach (Row r in _save.rows)
                if (first == null || r.Bouts < first.Bouts || (r.Bouts == first.Bouts && UnityEngine.Random.value < 0.35f))
                    first = r;

            Row second = null;
            float best = float.MaxValue;
            foreach (Row r in _save.rows)
            {
                if (r == first) continue;
                float gap = Mathf.Abs(r.elo - first.elo) + (r.name == first.lastOpponent ? 500f : 0f) + UnityEngine.Random.value * 40f;
                if (gap < best) { best = gap; second = r; }
            }
            if (second == null) return false;

            a = ProfileFor(first.name);
            b = ProfileFor(second.name);
            return a != null && b != null;
        }

        /// <summary>Records a result. <paramref name="scoreA"/> is 1 for a win by A, 0 for a loss, 0.5 for a draw.</summary>
        public float Report(string a, string b, float scoreA, bool knockout)
        {
            Row ra = Find(a), rb = Find(b);
            if (ra == null || rb == null) return 0f;

            float ea = Expected(ra.elo, rb.elo);
            float delta = kFactor * (scoreA - ea);
            ra.elo += delta;
            rb.elo -= delta;

            if (scoreA > 0.75f) { ra.wins++; rb.losses++; if (knockout) ra.knockouts++; }
            else if (scoreA < 0.25f) { rb.wins++; ra.losses++; if (knockout) rb.knockouts++; }
            else { ra.draws++; rb.draws++; }

            ra.lastOpponent = b;
            rb.lastOpponent = a;
            Store();
            return delta;
        }
    }
}
