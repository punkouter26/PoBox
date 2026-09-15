using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace PoBox
{
    /// <summary>
    /// Match layer over the round referee: first fighter to win
    /// ROUNDS_TO_WIN rounds is crowned champion. Shows a persistent star
    /// scoreboard between rounds, freezes the referee at match end, plays the
    /// champion celebration, then reloads the scene for a rematch with the same
    /// line-up (Menu -> Fight -> Champion -> Rematch).
    /// Renders its scoreboard into the referee's HUD document (one panel
    /// fewer). Lives under the contest systems root. Test-scene harness only.
    /// </summary>
    public sealed class Systems_MatchDirector : MonoBehaviour
    {
        private const int ROUNDS_TO_WIN = 3;
        private const float CELEBRATION_SECONDS = 7f;

        public event System.Action<string> ChampionCrowned;

        /// <summary>True once the match is decided and the celebration is running.</summary>
        public bool MatchDecided => _matchOver;

        /// <summary>Who won the match, or "" while it is undecided.</summary>
        public string Champion { get; private set; } = string.Empty;

        /// <summary>Rounds each fighter has won, for the smoke test's flow check.</summary>
        public System.Collections.Generic.IReadOnlyDictionary<string, int> Wins => _wins;

        private Systems_ContestReferee _contest;
        private readonly System.Collections.Generic.Dictionary<string, int> _wins = new();
        private readonly System.Collections.Generic.Dictionary<string, Color> _colors = new();
        private VisualElement _scoreboard;
        private float _celebrationRemaining;
        private bool _matchOver;

        private void Start()
        {
            _contest = FindFirstObjectByType<Systems_ContestReferee>();
            if (_contest == null)
            {
                return;
            }

            // A MATCH NEEDS MORE THAN ONE ROUND, SO THIS IS A CONTRADICTION.
            //
            // This component exists to crown the fighter that wins
            // ROUNDS_TO_WIN rounds, and it does that by counting them. If the
            // referee is configured not to start a second round, the count can
            // never pass one: no champion is ever crowned, this component's
            // celebration never runs and the scene is never reloaded — the match
            // ends in the frozen aftermath of round one and the only way out is
            // the menu button.
            //
            // That is not hypothetical, it is what both shipping scenes did from
            // 2026-09-08: the commit that introduced the two-referee base class
            // also serialized `_restartRoundsAutomatically: 0` into them, which
            // makes Systems_ContestReferee.HoldRestarts permanently true. Nothing
            // failed — the rounds simply stopped, in silence. Reported rather than
            // corrected, because which of the two is wrong is a design decision:
            // either the match wants several rounds, or the contest is one round
            // and does not want a match director.
            if (_contest.HoldRestarts)
            {
                Debug.LogError("Systems_MatchDirector: the referee is set not to start another round, " +
                    "so a " + ROUNDS_TO_WIN + "-round match can never be decided — no champion will be " +
                    "crowned and this scene will never reload. Either enable " +
                    "'Restart Rounds Automatically' on the referee, or remove this component if the " +
                    "contest is meant to be a single round.");
            }
            // Share the referee's HUD document instead of owning a panel.
            var root = _contest.GetComponent<UIDocument>().rootVisualElement;

            // The referee reports a winner by name, so the tally needs its own
            // way back to that fighter's colour. Built once here: the roster
            // cannot change mid-match.
            IContestFighter[] fighters = Systems_Contestants.FindAll();
            for (int fighterIndex = 0; fighterIndex < fighters.Length; fighterIndex++)
            {
                _colors[fighters[fighterIndex].DisplayName] = fighters[fighterIndex].PlateColor;
            }

            _scoreboard = new VisualElement();
            _scoreboard.AddToClassList("score-row");
            // Placed by the shared HUD stack rather than by an absolute rect of
            // its own: a full-width centred tally reached into the top-left
            // corner and drew over the FPS readout the moment it listed more
            // than a couple of names.
            _scoreboard.pickingMode = PickingMode.Ignore;
            Systems_UiTheme.HudScoreSlot(root).Add(_scoreboard);

            _contest.RoundEnded += OnRoundEnded;
        }

        private void OnDestroy()
        {
            if (_contest != null)
            {
                _contest.RoundEnded -= OnRoundEnded;
            }
        }

        private void OnRoundEnded(string winnerName)
        {
            if (_matchOver || string.IsNullOrEmpty(winnerName))
            {
                return;
            }
            _wins.TryGetValue(winnerName, out int wins);
            wins++;
            _wins[winnerName] = wins;
            RefreshScoreboard();

            if (wins >= ROUNDS_TO_WIN)
            {
                _matchOver = true;
                Champion = winnerName;
                _contest.HoldRestarts = true;
                _celebrationRemaining = CELEBRATION_SECONDS;
                ChampionCrowned?.Invoke(winnerName);
            }
        }

        /// <summary>
        /// Rebuilds the star tally, best first. Ordered rather than left in
        /// dictionary order because this is the only place the player is told
        /// who is winning the MATCH, and "whoever scored first" is not that.
        /// </summary>
        private void RefreshScoreboard()
        {
            var ranked = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, int>>(_wins);
            ranked.Sort((left, right) => right.Value.CompareTo(left.Value));

            _scoreboard.Clear();
            for (int entryIndex = 0; entryIndex < ranked.Count; entryIndex++)
            {
                System.Collections.Generic.KeyValuePair<string, int> entry = ranked[entryIndex];
                if (!_colors.TryGetValue(entry.Key, out Color color))
                {
                    color = Color.white;
                }
                _scoreboard.Add(Systems_UiTheme.BuildScoreChip($"{entry.Key} ★{entry.Value}", color));
            }
        }

        private void Update()
        {
            if (!_matchOver)
            {
                return;
            }
            _celebrationRemaining -= Time.unscaledDeltaTime;
            if (_celebrationRemaining <= 0f)
            {
                // The celebration may be running under the banner's slow-motion
                // beat, and this scene is about to be destroyed along with every
                // request in it. Clearing the whole clock here rather than
                // flattening it to 1 is what stops a freeze held by the outgoing
                // scene from following the reload in.
                Systems_GameClock.RestoreAll("match decided — rematch reload");
                SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
            }
        }
    }
}
