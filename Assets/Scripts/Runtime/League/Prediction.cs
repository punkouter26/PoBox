using UnityEngine;

namespace PoBox.League
{
    /// <summary>
    /// The prediction game: before the first bell the viewer picks a corner, staking <see cref="Stake"/> coins at
    /// the odds the ladder gives (LeagueTable.Expected: a boxer the ratings make a 2-to-1 favourite pays 1.5 times
    /// the stake, the other 3 times). A draw gives the stake back. Coins are kept on the device; nobody starts
    /// with nothing, and anyone who has lost everything is given a fresh <see cref="StartCoins"/>.
    /// </summary>
    public static class Prediction
    {
        public const int Stake = 10, StartCoins = 100;
        const string CoinsKey = "pobox.coins";

        public static int Coins
        {
            get { int c = PlayerPrefs.GetInt(CoinsKey, StartCoins); return c < Stake ? StartCoins : c; }
            private set { PlayerPrefs.SetInt(CoinsKey, value); PlayerPrefs.Save(); }
        }

        /// <summary>What a winning pick pays back, stake included, as a multiple: 1 over the chance the ratings give it.</summary>
        public static float Odds(float chance) => Mathf.Clamp(1f / Mathf.Max(0.01f, chance), 1.05f, 20f);

        /// <summary>The corner picked this bout (-1 none), and its odds when it was picked.</summary>
        public static int Picked { get; private set; } = -1;
        static float _odds;

        public static void Pick(int corner, float odds)
        {
            if (Picked >= 0) return;
            Picked = corner;
            _odds = odds;
            Coins -= Stake;
        }

        /// <summary>The bout is over (winner -1 for a draw): the coins paid, and a line saying so. Null with no pick.</summary>
        public static string Settle(int winner, string pickedName)
        {
            if (Picked < 0) return null;
            int back = winner < 0 ? Stake : winner == Picked ? Mathf.RoundToInt(Stake * _odds) : 0;
            Coins += back;
            string line = winner < 0 ? $"DRAW · STAKE BACK · {Coins} COINS"
                : back > 0 ? $"YOU CALLED {pickedName} · +{back - Stake} · {Coins} COINS"
                : $"{pickedName} LOST · -{Stake} · {Coins} COINS";
            Picked = -1;
            return line;
        }

        /// <summary>A new bout: an unsettled pick (the bout thrown away) gives its stake back.</summary>
        public static void Reset()
        {
            if (Picked >= 0) Coins += Stake;
            Picked = -1;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Clear() => Picked = -1;
    }
}
