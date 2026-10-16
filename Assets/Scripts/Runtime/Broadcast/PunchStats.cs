using System.Collections.Generic;
using UnityEngine;
using PoBox.Sim;

namespace PoBox.Broadcast
{
    /// <summary>
    /// The punch count, as the broadcast puts it: thrown, landed and the share landed come from the fighter's own
    /// counters (Fighter.stats); the fastest glove that landed (its speed at first touch, from SimBus.Hit) is
    /// kept here, since nothing else keeps it. Cleared at the start of every bout (a walk-on, or a first round
    /// starting on a clock of nothing).
    /// </summary>
    public static class PunchStats
    {
        static readonly Dictionary<Fighter, float> Fastest = new Dictionary<Fighter, float>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Hook()
        {
            // SimBus has just been emptied (it resets at SubsystemRegistration, which runs first).
            Fastest.Clear();
            SimBus.Hit += OnHit;
            SimBus.PhaseChanged += OnPhase;
        }

        static void OnHit(HitEvent e)
        {
            if (e.attacker == null) return;
            Fastest.TryGetValue(e.attacker, out float v);
            if (e.gloveSpeed > v) Fastest[e.attacker] = e.gloveSpeed;
        }

        static void OnPhase(BoutPhase from, BoutPhase to)
        {
            if (to == BoutPhase.WalkOn || (to == BoutPhase.Intro && Bout.Clock <= 0f)) Fastest.Clear();
        }

        /// <summary>The fastest glove of this fighter's that landed this bout, km/h.</summary>
        public static float FastestKmh(Fighter f) => f != null && Fastest.TryGetValue(f, out float v) ? v * 3.6f : 0f;

        public static int LandedPercent(Fighter f) => f == null || f.stats.thrown == 0 ? 0 : Mathf.RoundToInt(100f * f.stats.landed / f.stats.thrown);

        /// <summary>"12/40 30%".</summary>
        public static string Landed(Fighter f) => f == null ? "-" : $"{f.stats.landed}/{f.stats.thrown} {LandedPercent(f)}%";

        /// <summary>"45 N·s 32 km/h": the hardest punch and the fastest glove.</summary>
        public static string Power(Fighter f) => f == null ? "-" : $"{f.stats.peakImpulse:0} N·s {FastestKmh(f):0} km/h";
    }
}
