using System.Collections.Generic;
using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// The one place in the game that writes <c>Time.timeScale</c>.
    ///
    /// WHY THIS EXISTS. Four systems used to write the clock directly and none
    /// of them owned it: the round countdown froze it to 0, the winner banner
    /// dipped it to 0.5, the knockout FX dipped it to 0.35, and the match
    /// director flattened it to 1 before reloading the scene. Each also
    /// restored "1" on the way out, in <c>OnDestroy</c>, as a band-aid for the
    /// scene it was leaving. So two of them firing near each other meant the
    /// last writer won and the other's restore-to-1 could cancel a freeze a
    /// third system was relying on — and the countdown is exactly that: it
    /// holds physics still so a round does not begin mid-topple.
    ///
    /// Only the knockout FX guarded its own restore, by checking the clock was
    /// still at its own value first. That is the right instinct applied in one
    /// place out of four, which is what a missing owner looks like.
    ///
    /// THE MODEL IS REQUESTS, NOT VALUES. A caller asks for a freeze or a
    /// slow-motion rate under its own name and keeps asking until it releases
    /// or dies. The clock then resolves the lot: any live freeze means 0,
    /// otherwise the slowest live slow-motion request wins. Nothing has to know
    /// about anything else, and no caller can stomp another's request by
    /// restoring a value it assumed was its own.
    ///
    /// A RELEASED REQUEST IS DROPPED EVEN IF IT HAD ALREADY EXPIRED, and
    /// <see cref="RestoreAll"/> exists because leaving a scene — by the menu
    /// button, by the referee's escape hatch, by the match director's rematch
    /// reload — must not carry a freeze into the next scene. The menu freezing
    /// solid because a countdown was holding time at 0 when the player left is
    /// the failure this prevents, and it was previously prevented by accident:
    /// whichever <c>OnDestroy</c> happened to run last restored the clock.
    /// </summary>
    internal static class Systems_GameClock
    {
        private static readonly HashSet<string> Freezes = new HashSet<string>();
        private static readonly Dictionary<string, float> Slowmos = new Dictionary<string, float>();

        /// <summary>
        /// Play mode in the Editor reuses statics across sessions unless the
        /// domain is reloaded, and this state is nonsense outside a live scene:
        /// a freeze left over from the last session would freeze the next one
        /// before any countdown had even run.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForNewPlaySession()
        {
            Freezes.Clear();
            Slowmos.Clear();
            Time.timeScale = 1f;
        }

        /// <summary>The resolved scale, for anything that wants to report it.</summary>
        public static float TimeScale => Time.timeScale;

        /// <summary>
        /// Stop the simulation dead. Held until <see cref="ReleaseFreeze"/> or
        /// until the scene is left. The countdown is the only caller: a round
        /// must not start with the fighters still settling from their spawn.
        /// </summary>
        public static void HoldFreeze(string owner)
        {
            if (string.IsNullOrEmpty(owner) || !Freezes.Add(owner))
            {
                return;
            }
            Apply();
        }

        /// <summary>Give up a freeze. Harmless if the freeze was never held.</summary>
        public static void ReleaseFreeze(string owner)
        {
            if (string.IsNullOrEmpty(owner) || !Freezes.Remove(owner))
            {
                return;
            }
            Apply();
        }

        /// <summary>
        /// Run the simulation slower than real time. A rate, not a duration —
        /// the caller keeps it alive for as long as it wants the beat and calls
        /// <see cref="ReleaseSlowmo"/> when it is done, so a second request
        /// arriving mid-beat cannot be cancelled by the first one ending.
        /// Requesting twice under the same name replaces the rate.
        /// </summary>
        public static void RequestSlowmo(string owner, float scale)
        {
            if (string.IsNullOrEmpty(owner))
            {
                return;
            }
            Slowmos[owner] = Mathf.Clamp(scale, 0.01f, 1f);
            Apply();
        }

        /// <summary>Give up a slow-motion request. Harmless if there was none.</summary>
        public static void ReleaseSlowmo(string owner)
        {
            if (string.IsNullOrEmpty(owner) || !Slowmos.Remove(owner))
            {
                return;
            }
            Apply();
        }

        /// <summary>
        /// Drop every request there is, whoever made it, and put the clock back
        /// to real time. For scene transitions: the requests belong to the
        /// scene being left, and their owners are about to be destroyed.
        ///
        /// <paramref name="reason"/> is only for the log line, so a freeze that
        /// outlived its owner can be traced to whatever ended the scene.
        /// </summary>
        public static void RestoreAll(string reason)
        {
            if (Freezes.Count == 0 && Slowmos.Count == 0)
            {
                if (!Mathf.Approximately(Time.timeScale, 1f))
                {
                    // Time was not 1 and nobody is holding a request that says
                    // why. That is a leak someone should see, not a state to
                    // silently paper over.
                    Debug.LogWarning($"Systems_GameClock: time scale was {Time.timeScale:0.00} with no " +
                        $"outstanding request ({reason}) — restoring 1.");
                }
                Time.timeScale = 1f;
                return;
            }
            Freezes.Clear();
            Slowmos.Clear();
            Time.timeScale = 1f;
        }

        /// <summary>
        /// Resolve every live request. A freeze outranks any slow motion: the
        /// countdown's "physics has not started yet" is not a speed, and a
        /// 0.5 rate must not quietly re-enable simulation underneath it.
        /// </summary>
        private static void Apply()
        {
            if (Freezes.Count > 0)
            {
                Time.timeScale = 0f;
                return;
            }
            float scale = 1f;
            foreach (KeyValuePair<string, float> request in Slowmos)
            {
                if (request.Value < scale)
                {
                    scale = request.Value;
                }
            }
            Time.timeScale = scale;
        }
    }
}
