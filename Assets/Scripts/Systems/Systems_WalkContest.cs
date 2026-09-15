using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoBox
{
    /// <summary>
    /// Referee for the walk contest test scene: every contestant starts on one
    /// edge of the ring and races straight to the far edge. First across wins;
    /// a fall parks that racer where it dropped and its distance stands as its
    /// score, so a round always resolves even when nobody finishes. Mirrors
    /// <see cref="Systems_BalanceContest"/> — self-discovers contestants at
    /// Start through <see cref="IContestFighter"/>, drives the same USS_Contest
    /// scoreboard, and raises the same RoundEnded/RoundStarted events through
    /// the shared <see cref="Systems_ContestReferee"/> base, so the banner,
    /// crowd, match director and FX systems bind to it exactly as they do to
    /// the balance referee.
    /// Test-scene harness only — not used in training or the game loop.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class Systems_WalkContest : Systems_ContestReferee
    {
        private const float HEAD_COLLAPSE_FRACTION = 0.4f;
        // Was 4 s, set when a round was expected to last most of its 60 s limit.
        // It does not: measured 2026-08-22, rounds ended at roundTime 2.3 s with
        // the whole field down. The banner and the crowd cheer still need room
        // to land, which is what the remaining 2.5 s is for.
        private const float ROUND_RESTART_DELAY = 2.5f;
        private const float ROUND_TIME_LIMIT = 60f;
        // Commanded pace for the race. 1 m/s is a normal human walk.
        private const float RACE_SPEED = 1.0f;

        /// <summary>
        /// How far the leader must have actually walked for the round to be
        /// awarded to anybody. Without it the race declared a winner no matter
        /// what happened: measured 2026-08-22, rounds went to a racer for
        /// falling forward 20 cm while the rest of the field fell backward.
        /// Under this bar the round is a no contest, no star is awarded, and
        /// the match keeps going until somebody earns one. 0.75 m is a bit
        /// over one step -- deliberately low: the point is to reject topple
        /// noise, not to set a competitive standard.
        /// </summary>
        private const float MIN_WIN_DISTANCE = 0.75f;

        /// <summary>
        /// A round also ends when the field stops making progress for this
        /// long. The fall rule alone cannot end a round in which somebody
        /// simply stands still, so a stalled race would hold the scene for the
        /// full 60 s limit showing nothing at all.
        /// </summary>
        private const float STALL_SECONDS = 12f;

        /// <summary>Progress under this in <see cref="STALL_SECONDS"/> counts as no progress at all.</summary>
        private const float STALL_EPSILON = 0.25f;

        [SerializeField] private StyleSheet _styleSheet;
        // World direction from the start edge to the finish edge, and how far
        // that is. Written by the walk contest scene builder.
        [SerializeField] private Vector3 _goalDirection = Vector3.forward;
        [SerializeField] private float _goalDistance = 5.6f;

        private sealed class Racer
        {
            public string displayName;
            public IContestFighter fighter;
            /// <summary>Standing head height ABOVE THE FLOOR, not world Y.</summary>
            public float startHeadHeight;
            public float startProjection;
            public float travelled;
            public float finishTime;
            public bool fallen;
            public bool finished;
            public VisualElement plate;
            public Label label;
        }

        private readonly List<Racer> _racers = new();
        private Label _title;
        private int _round = 1;
        private float _restartTimer = -1f;
        private float _roundTime;
        // High-water mark of the whole field, and when it last moved: the stall
        // rule is about the RACE making progress, not any one racer.
        private float _bestTravelled;
        private float _lastProgressTime;

        private bool _initialized;

        // Called by the walk contest scene builder.
        public void EditorInitialize(Vector3 goalDirection, float goalDistance, StyleSheet styleSheet)
        {
            _goalDirection = goalDirection.normalized;
            _goalDistance = goalDistance;
            _styleSheet = styleSheet;
        }

        private System.Collections.IEnumerator Start()
        {
            yield return WaitForContestants();
            if (!enabled) { yield break; }
            _goalDirection = _goalDirection.normalized;

            var root = GetComponent<UIDocument>().rootVisualElement;
            if (_styleSheet != null)
            {
                root.styleSheets.Add(_styleSheet);
            }
            else
            {
                // Every class this HUD uses lives in USS_Contest. Without it the
                // scoreboard still builds and still updates, but at the 14 px black
                // default — invisible against the arena, which is how it shipped in
                // SCN_TEST_WALK_CONTEST until 2026-08-20. Fail loudly rather than
                // render a HUD nobody can read.
                Debug.LogError($"{name}: no StyleSheet assigned, so the walk scoreboard will " +
                    "render unstyled and effectively invisible. Assign USS_Contest.");
            }
            Systems_UiTheme.ApplyDefaultFont(root);

            var hudRoot = new VisualElement();
            hudRoot.AddToClassList("hud-root");
            hudRoot.pickingMode = PickingMode.Ignore;
            root.Add(hudRoot);

            AddMenuButton(root);

            var topBar = new VisualElement();
            topBar.AddToClassList("top-bar");
            topBar.pickingMode = PickingMode.Ignore;
            hudRoot.Add(topBar);

            _title = new Label();
            _title.AddToClassList("title-chip");
            topBar.Add(_title);
            _title.text = "Walk Contest — Round 1";

            var platesRow = new VisualElement();
            platesRow.AddToClassList("plates-row");
            platesRow.pickingMode = PickingMode.Ignore;
            hudRoot.Add(platesRow);

            IContestFighter[] fighters = Systems_Contestants.FindAll();
            for (int index = 0; index < fighters.Length; index++)
            {
                IContestFighter fighter = fighters[index];
                VisualElement plate = Systems_UiTheme.BuildPlate(fighter.PlateColor, out Label plateLabel);
                platesRow.Add(plate);
                fighter.ResetForRound();
                var racer = new Racer
                {
                    displayName = fighter.DisplayName,
                    fighter = fighter,
                    startHeadHeight = fighter.HeadHeightAboveGround,
                    startProjection = Vector3.Dot(fighter.WorldPosition, _goalDirection),
                    plate = plate,
                    label = plateLabel
                };
                _racers.Add(racer);
                CommandRace(racer);
            }
            _initialized = true;
            RaiseRoundStarted(_round);
        }

        private void FixedUpdate()
        {
            if (!_initialized) { return; }
            if (_restartTimer >= 0f)
            {
                if (HoldRestarts)
                {
                    return; // match decided — freeze on the final tableau
                }
                _restartTimer -= Time.fixedDeltaTime;
                if (_restartTimer < 0f)
                {
                    StartNextRound();
                }
                return;
            }

            _roundTime += Time.fixedDeltaTime;

            int racingCount = 0;
            for (int racerIndex = 0; racerIndex < _racers.Count; racerIndex++)
            {
                Racer racer = _racers[racerIndex];
                if (racer.finished || racer.fallen)
                {
                    continue;
                }
                // A fall freezes the score at the distance reached, so the
                // plate still reports how far that racer actually got.
                if (HasFallen(racer))
                {
                    racer.fallen = true;
                    RaiseFighterFell(racer.displayName);
                    continue;
                }

                // High-water mark, floored at zero, and only ever sampled while
                // the racer is still upright. Both halves matter. Reading the
                // live projection instead let a racer BANK ITS OWN TOPPLE: the
                // pelvis flies forward as the body goes down, so the reward for
                // falling over was the same 20-30 cm that was winning rounds.
                // The floor is what stops a backward faceplant scoring -0.6 m
                // and still placing.
                float projected = Vector3.Dot(racer.fighter.WorldPosition, _goalDirection) - racer.startProjection;
                racer.travelled = Mathf.Max(racer.travelled, Mathf.Max(0f, projected));
                if (racer.travelled > _bestTravelled + STALL_EPSILON)
                {
                    _bestTravelled = racer.travelled;
                    _lastProgressTime = _roundTime;
                }
                if (racer.travelled >= _goalDistance)
                {
                    racer.travelled = _goalDistance;
                    racer.finishTime = _roundTime;
                    racer.finished = true;
                    continue;
                }
                racingCount++;
            }

            bool timeUp = _roundTime >= ROUND_TIME_LIMIT;
            bool stalled = racingCount > 0 && _roundTime - _lastProgressTime >= STALL_SECONDS;
            if ((racingCount == 0 || timeUp || stalled) && _racers.Count > 0)
            {
                _restartTimer = ROUND_RESTART_DELAY;
                LogRaceResult(FindLeader(),
                    racingCount == 0 ? "all done" : timeUp ? "time up" : "stalled");
                RaiseRoundEnded(WinnerName());
            }
        }

        /// <summary>
        /// Who won the round, or "" for a no contest. A leader who never cleared
        /// <see cref="MIN_WIN_DISTANCE"/> did not beat anyone — the field just
        /// fell over in slightly different directions — and an empty name is
        /// what tells the match director to award no star and the banner to say
        /// so.
        /// </summary>
        private string WinnerName()
        {
            Racer leader = FindLeader();
            if (leader == null)
            {
                return "";
            }
            return leader.finished || leader.travelled >= MIN_WIN_DISTANCE ? leader.displayName : "";
        }

        private void Update()
        {
            if (!_initialized) { return; }
            bool roundOver = _restartTimer >= 0f;
            // Only a leader who actually earned the round wears the winner
            // plate; in a no contest nobody does.
            bool decided = roundOver && !string.IsNullOrEmpty(WinnerName());
            Racer leader = FindLeader();
            for (int racerIndex = 0; racerIndex < _racers.Count; racerIndex++)
            {
                Racer racer = _racers[racerIndex];
                racer.label.text = racer.finished
                    ? $"{racer.displayName}  {racer.finishTime:F1}s"
                    : $"{racer.displayName}  {racer.travelled:F1}m";
                racer.plate.EnableInClassList("plate--down", racer.fallen && !(decided && racer == leader));
                racer.plate.EnableInClassList("plate--winner", decided && racer == leader);
            }
            if (roundOver)
            {
                _title.text = decided
                    ? $"Round {_round} over — next in {Mathf.Max(0f, _restartTimer):F0}s"
                    : $"No contest — next in {Mathf.Max(0f, _restartTimer):F0}s";
                return;
            }
            _title.text = $"Walk Contest — Round {_round}  {Mathf.Max(0f, ROUND_TIME_LIMIT - _roundTime):F0}s";
        }

        /// <summary>
        /// One greppable line per race, the counterpart of
        /// Systems_BalanceContest's CONTEST_ROUND: "did anyone actually walk"
        /// is the question a shipping decision on a locomotion brain turns on,
        /// and it must be answerable without a human watching a screen.
        /// </summary>
        private void LogRaceResult(Racer leader, string reason)
        {
            var line = new System.Text.StringBuilder();
            line.Append($"WALK_RESULT {_round} | {reason} | goal={_goalDistance:F1}m | winner=");
            line.Append(string.IsNullOrEmpty(leader?.displayName) ? "NO CONTEST" : leader.displayName);
            line.Append(" |");
            for (int racerIndex = 0; racerIndex < _racers.Count; racerIndex++)
            {
                Racer racer = _racers[racerIndex];
                line.Append($" {racer.displayName}={racer.travelled:F2}m");
                line.Append(racer.finished ? "(finished)" : racer.fallen ? "(down)" : "(up)");
            }
            Debug.Log(line.ToString());
        }

        private Racer FindLeader()
        {
            Racer leader = null;
            for (int racerIndex = 0; racerIndex < _racers.Count; racerIndex++)
            {
                Racer racer = _racers[racerIndex];
                if (leader == null || Beats(racer, leader))
                {
                    leader = racer;
                }
            }
            return leader;
        }

        // Ranking: anyone who finished beats anyone who did not, earliest
        // finish first; among the unfinished, furthest travelled wins.
        private static bool Beats(Racer candidate, Racer incumbent)
        {
            if (candidate.finished != incumbent.finished)
            {
                return candidate.finished;
            }
            if (candidate.finished)
            {
                return candidate.finishTime < incumbent.finishTime;
            }
            return candidate.travelled > incumbent.travelled;
        }

        /// <summary>
        /// Tells the racer to walk. A trained brain reads this as an
        /// observation; without it the racer would just stand on the start
        /// line while the plates reported its distance.
        /// </summary>
        private void CommandRace(Racer racer)
        {
            racer.fighter.CommandWalk(RACE_SPEED, _goalDirection);
        }

        /// <summary>
        /// Down by the racer's own reckoning, or head under 40% of its standing
        /// height -- GROUND-RELATIVE, never raw world Y, for the reason
        /// Systems_BalanceContest.HasFallen records.
        /// </summary>
        private static bool HasFallen(Racer racer)
        {
            return racer.fighter.ReportsDown
                || racer.fighter.HeadHeightAboveGround < racer.startHeadHeight * HEAD_COLLAPSE_FRACTION;
        }

        private void StartNextRound()
        {
            _round++;
            _restartTimer = -1f;
            _roundTime = 0f;
            _bestTravelled = 0f;
            _lastProgressTime = 0f;
            for (int racerIndex = 0; racerIndex < _racers.Count; racerIndex++)
            {
                Racer racer = _racers[racerIndex];
                racer.fighter.ResetForRound();
                racer.startProjection = Vector3.Dot(racer.fighter.WorldPosition, _goalDirection);
                CommandRace(racer);
                racer.travelled = 0f;
                racer.finishTime = 0f;
                racer.fallen = false;
                racer.finished = false;
            }
            RaiseRoundStarted(_round);
        }
    }
}
