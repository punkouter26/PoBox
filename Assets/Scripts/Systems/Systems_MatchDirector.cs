using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace PoBox
{
    /// <summary>
    /// Match layer over the round referee: first fighter to win
    /// ROUNDS_TO_WIN rounds is crowned champion. Shows a persistent star
    /// scoreboard between rounds, freezes the referee at match end, plays the
    /// champion celebration, then reloads the scene — which lands back on the
    /// setup menu with fresh random slots (Menu -> Fight -> Champion -> Menu).
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
        private Label _scoreboard;
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

            _scoreboard = new Label();
            // Placed by the shared HUD stack rather than by an absolute rect of
            // its own: a full-width centred tally reached into the top-left
            // corner and drew over the FPS readout the moment it listed more
            // than a couple of names.
            _scoreboard.style.unityTextAlign = TextAnchor.MiddleCenter;
            _scoreboard.style.fontSize = 44f;
            _scoreboard.style.color = Systems_UiTheme.Gold;
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

        private void RefreshScoreboard()
        {
            var builder = new System.Text.StringBuilder();
            foreach (System.Collections.Generic.KeyValuePair<string, int> entry in _wins)
            {
                if (builder.Length > 0)
                {
                    builder.Append("    ");
                }
                builder.Append(entry.Key).Append(" ★").Append(entry.Value);
            }
            _scoreboard.text = builder.ToString();
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
