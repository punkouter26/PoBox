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
            // Share the referee's HUD document instead of owning a panel.
            var root = _contest.GetComponent<UIDocument>().rootVisualElement;

            // The referee reports a winner by name, so the tally needs its own
            // way back to that fighter's colour. Built once here: the roster
            // cannot change mid-match.
            Systems_FighterRig[] rigs = FindObjectsByType<Systems_FighterRig>(FindObjectsSortMode.InstanceID);
            for (int rigIndex = 0; rigIndex < rigs.Length; rigIndex++)
            {
                Systems_FighterIdentity.Resolve(rigs[rigIndex], out string name, out Color color);
                _colors[name] = color;
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
                Time.timeScale = 1f;
                SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
            }
        }
    }
}
