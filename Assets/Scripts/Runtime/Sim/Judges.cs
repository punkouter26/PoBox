using System;
using UnityEngine;

namespace PoBox.Sim
{
    /// <summary>
    /// Three judges at ringside, each scoring every round on the ten-point must system and each looking for
    /// something different, so that a close bout can be given one way by two of them and the other way by
    /// the third. They see only what the physics measured: every landed punch with its impulse and where it
    /// landed, every punch stopped on the gloves, every knockdown.
    ///
    ///     POWER    how hard the clean punches were: impulse above the floor, raised to the power 1.5, so
    ///              one heavy shot outweighs several light ones; the head counts 1.6 and the body 1.
    ///     VOLUME   how many clean punches landed, wherever they landed, with a little for being the busier.
    ///     CRAFT    clean impulse to head and body alike, plus credit for every punch stopped on the guard.
    ///
    /// A round goes 10-9 to whoever a judge had ahead, 10-10 when that judge's two totals are within 4% of
    /// each other, and a fighter put down in the round loses a further point for each time (10-8, 10-7).
    /// </summary>
    public class Judges
    {
        public const int Count = 3;
        public static readonly string[] Names = { "POWER", "VOLUME", "CRAFT" };

        /// <summary>What each judge has given each corner in the rounds scored so far: [judge, corner].</summary>
        public readonly int[,] total = new int[Count, 2];
        /// <summary>The running tally of the round being fought, in each judge's own units: [judge, corner].</summary>
        public readonly float[,] round = new float[Count, 2];
        readonly int[] _downs = new int[2];
        public int RoundsScored { get; private set; }
        public float impulseFloor = 2.2f;

        public void Reset()
        {
            Array.Clear(total, 0, total.Length);
            Array.Clear(round, 0, round.Length);
            Array.Clear(_downs, 0, _downs.Length);
            RoundsScored = 0;
        }

        /// <param name="corner">Who landed it: 0 red, 1 blue.</param>
        public void OnHit(int corner, in HitEvent e)
        {
            float over = Mathf.Max(0f, e.impulse - impulseFloor);
            if (e.clean)
            {
                round[0, corner] += Mathf.Pow(over, 1.5f) * (e.zone == PartKind.Head ? 1.6f : 1f);
                round[1, corner] += 1f;
                round[2, corner] += over;
            }
            else
            {
                // Stopped on the gloves: the judge of craft gives that to the fighter who stopped it.
                round[2, 1 - corner] += 0.6f * over;
            }
        }

        public void OnThrown(int corner) => round[1, corner] += 0.12f;

        /// <param name="corner">Who went down.</param>
        public void OnKnockdown(int corner) => _downs[corner]++;

        /// <summary>Who a judge has ahead in the round being fought: +1 red, -1 blue, 0 level.</summary>
        public int Lean(int judge)
        {
            float r = round[judge, 0], b = round[judge, 1];
            if (_downs[0] != _downs[1]) return _downs[0] < _downs[1] ? 1 : -1;
            if (Mathf.Abs(r - b) <= 0.04f * (r + b) + 1e-4f) return 0;
            return r > b ? 1 : -1;
        }

        /// <summary>Writes the round on each card and clears the tallies for the next.</summary>
        public void CloseRound()
        {
            for (int j = 0; j < Count; j++)
            {
                int lean = Lean(j);
                int red = lean >= 0 ? 10 : 9, blue = lean <= 0 ? 10 : 9;
                // A knockdown costs a point however the rest of the round went.
                red = Mathf.Max(7, red - _downs[0]);
                blue = Mathf.Max(7, blue - _downs[1]);
                if (_downs[0] > 0 && _downs[1] == 0 && blue < 10) blue = 10;
                if (_downs[1] > 0 && _downs[0] == 0 && red < 10) red = 10;
                total[j, 0] += red;
                total[j, 1] += blue;
            }
            Array.Clear(round, 0, round.Length);
            Array.Clear(_downs, 0, _downs.Length);
            RoundsScored++;
        }

        /// <summary>How many of the three have red ahead, blue ahead, and level, on the cards as they stand.</summary>
        public void Tally(out int forRed, out int forBlue, out int level)
        {
            forRed = forBlue = level = 0;
            for (int j = 0; j < Count; j++)
            {
                if (total[j, 0] > total[j, 1]) forRed++;
                else if (total[j, 0] < total[j, 1]) forBlue++;
                else level++;
            }
        }

        /// <summary>"UNANIMOUS", "SPLIT", "MAJORITY" or a kind of draw; winner is 0 red, 1 blue, -1 nobody.</summary>
        public string Verdict(out int winner)
        {
            Tally(out int r, out int b, out int d);
            winner = r > b && r >= 2 ? 0 : b > r && b >= 2 ? 1 : -1;
            if (winner < 0) return r == 1 && b == 1 ? "SPLIT DRAW" : d >= 2 ? "MAJORITY DRAW" : "DRAW";
            int against = winner == 0 ? b : r;
            return against > 0 ? "SPLIT DECISION" : d > 0 ? "MAJORITY DECISION" : "UNANIMOUS DECISION";
        }

        public string Card(int judge) => $"{total[judge, 0]}-{total[judge, 1]}";

        /// <summary>
        /// Red's lead on the cards, counting the round in progress as each judge has it now: from -1 (blue
        /// ahead with all three in every round) to +1. What the win-probability bar starts from.
        /// </summary>
        public float Lead(int roundsInBout)
        {
            float sum = 0f;
            for (int j = 0; j < Count; j++) sum += total[j, 0] - total[j, 1] + Lean(j);
            return Mathf.Clamp(sum / (Count * Mathf.Max(1, roundsInBout)), -1f, 1f);
        }
    }
}
