using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace PoBox
{
    /// <summary>
    /// What every contest referee owes the presentation layer: a round
    /// lifecycle to hang banners, crowd swells, camera cuts and the match
    /// score off, plus a brake the match director pulls once the match is
    /// decided.
    ///
    /// This exists because the presentation systems used to name
    /// <see cref="Systems_BalanceContest"/> directly — a dozen
    /// FindFirstObjectByType&lt;Systems_BalanceContest&gt;() calls. That made
    /// <see cref="Systems_WalkContest"/>'s matching RoundStarted/RoundEnded
    /// events unreachable no matter which objects the walk scene contained, so
    /// the walk race shipped with no announcer, no winner banner, no crowd and
    /// — with no match director to set <see cref="HoldRestarts"/> — no end:
    /// measured 2026-08-20 still looping at round 14 with no way out.
    /// Discover a referee with FindFirstObjectByType&lt;Systems_ContestReferee&gt;()
    /// and both mini-games light up the same stack.
    ///
    /// Events are declared here and raised through the protected Raise*
    /// helpers, because C# only lets the declaring class invoke an event.
    /// Test-scene harness only — not used in training or the game loop.
    /// </summary>
    public abstract class Systems_ContestReferee : MonoBehaviour
    {
        /// <summary>Round is over; carries the round winner's display name ("" for a draw).</summary>
        public event System.Action<string> RoundEnded;

        /// <summary>A new round has begun; carries the 1-based round number.</summary>
        public event System.Action<int> RoundStarted;

        /// <summary>One fighter has gone down; carries its display name.</summary>
        public event System.Action<string> FighterFell;

        /// <summary>Set by the match director when the match is decided: the referee stops starting new rounds.</summary>
        public bool HoldRestarts { get; set; }

        protected void RaiseRoundEnded(string winnerName) => RoundEnded?.Invoke(winnerName);

        protected void RaiseRoundStarted(int round) => RoundStarted?.Invoke(round);

        protected void RaiseFighterFell(string fighterName) => FighterFell?.Invoke(fighterName);

        /// <summary>
        /// Adds the "‹ MENU" escape hatch to a referee HUD. Until this existed
        /// the only two LoadScene calls in the project were menu -> contest and
        /// contest -> itself, so pressing START locked the player into that
        /// mini-game permanently — on WebGL the only way back was reloading the
        /// page.
        ///
        /// Top-RIGHT, not top-left: the version stamp and the FPS readout both
        /// live in the top-left corner, and the first pass at this button sat
        /// straight on top of them. The referee's title chip is centred and the
        /// announcer's hazard chip starts 200 px down, so this corner is the one
        /// piece of HUD nothing else claims.
        /// </summary>
        protected static void AddMenuButton(VisualElement root)
        {
            var button = new Button(LoadMenuScene) { text = "‹ MENU" };
            button.style.position = Position.Absolute;
            button.style.top = 16f;
            button.style.right = 16f;
            button.style.height = 84f;
            button.style.paddingLeft = 26f;
            button.style.paddingRight = 26f;
            button.style.fontSize = 40f;
            button.style.unityFontStyleAndWeight = FontStyle.Bold;
            button.style.unityTextAlign = TextAnchor.MiddleCenter;
            button.style.color = Systems_UiTheme.Gold;
            button.style.backgroundColor = new Color(0f, 0f, 0f, 0.55f);
            button.style.borderTopWidth = 2f;
            button.style.borderBottomWidth = 2f;
            button.style.borderLeftWidth = 2f;
            button.style.borderRightWidth = 2f;
            Color border = Systems_UiTheme.Gold;
            border.a = 0.45f;
            button.style.borderTopColor = border;
            button.style.borderBottomColor = border;
            button.style.borderLeftColor = border;
            button.style.borderRightColor = border;
            button.style.borderTopLeftRadius = 14f;
            button.style.borderTopRightRadius = 14f;
            button.style.borderBottomLeftRadius = 14f;
            button.style.borderBottomRightRadius = 14f;
            root.Add(button);
        }

        /// <summary>
        /// Back to SCN_MENU (build index 0). Restores the time scale first: the
        /// round countdown parks it at 0 and the knockout FX at 0.35, and either
        /// would carry into the menu and freeze it.
        /// </summary>
        private static void LoadMenuScene()
        {
            Time.timeScale = 1f;
            if (SceneManager.sceneCountInBuildSettings > 0)
            {
                SceneManager.LoadScene(0);
            }
        }
    }
}
