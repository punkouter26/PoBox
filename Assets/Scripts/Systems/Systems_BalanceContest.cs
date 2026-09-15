using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoBox
{
    /// <summary>
    /// Referee for the balance contest test scene: every contestant stands
    /// until it reports itself down or its head collapses; longest time wins.
    /// Self-discovers contestants at Start through <see cref="IContestFighter"/>,
    /// shows a UI Toolkit scoreboard (styled by USS_Contest.uss: title chip up
    /// top, name plates at the bottom so the ring stays unobstructed),
    /// announces the winner, then resets everyone for the next round. Raises
    /// RoundEnded/RoundStarted for presentation systems (banner, crowd, FX)
    /// through <see cref="Systems_ContestReferee"/>, which is what they bind to.
    /// Test-scene harness only — not used in training or the game loop.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class Systems_BalanceContest : Systems_ContestReferee
    {
        private const float HEAD_COLLAPSE_FRACTION = 0.4f;
        private const float ROUND_RESTART_DELAY = 4f;
        /// <summary>
        /// Hard cap on a round, mirroring <see cref="Systems_WalkContest"/>.
        /// Without one the round ended only when the last fighter fell, and a
        /// contestant that never falls stalls the match for ever: measured
        /// 2026-08-21, a coded bot was still standing at 109 s. 30 s is long
        /// enough to settle a balance round on merit and short enough that a
        /// lone unfallable survivor cannot stall the match.
        /// </summary>
        private const float ROUND_TIME_LIMIT = 30f;

        [SerializeField] private StyleSheet _styleSheet;

        private sealed class Contestant
        {
            public string displayName;
            public IContestFighter fighter;
            /// <summary>Standing head height ABOVE THE FLOOR, not world Y.</summary>
            public float startHeadHeight;
            public float aliveTime;
            /// <summary>
            /// Head height as a fraction of the start pose, summed over every
            /// upright tick. Divided by aliveTime it is mean uprightness, and it
            /// exists to break ties on aliveTime — see <see cref="Beats"/>.
            /// </summary>
            public float uprightnessSum;
            public bool fallen;
            public VisualElement plate;
            public Label label;
        }

        private readonly List<Contestant> _contestants = new();
        private Label _title;
        private int _round = 1;
        private float _restartTimer = -1f;
        /// <summary>Active hazard, so a round line says what the fighters were up against.</summary>
        private string _hazard = "none";
        private float _roundTime;

        private bool _initialized;

        private System.Collections.IEnumerator Start()
        {
            yield return WaitForContestants();
            if (!enabled) { yield break; }
            var root = GetComponent<UIDocument>().rootVisualElement;
            if (_styleSheet != null)
            {
                root.styleSheets.Add(_styleSheet);
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
            _title.text = "Balance Contest — Round 1";

            var platesRow = new VisualElement();
            platesRow.AddToClassList("plates-row");
            platesRow.pickingMode = PickingMode.Ignore;
            hudRoot.Add(platesRow);

            var hazards = FindFirstObjectByType<Systems_HazardDirector>(FindObjectsInactive.Include);
            if (hazards != null) { hazards.HazardChosen += name => _hazard = name; }

            // ROUND ONE STARTS FROM THE SAME POSE EVERY OTHER ROUND DOES. It used
            // to start from whatever the scene load left behind, so round one
            // asked the brain to catch a falling body it had never seen, and a
            // player watching the FIRST round of a fresh contest saw the worst
            // the game has to offer. Every round begins from ResetForRound --
            // the canonical pose, velocities zeroed -- which is also the pose
            // the policy trained on.
            IContestFighter[] fighters = Systems_Contestants.FindAll();
            for (int index = 0; index < fighters.Length; index++)
            {
                IContestFighter fighter = fighters[index];
                VisualElement plate = Systems_UiTheme.BuildPlate(fighter.PlateColor, out Label plateLabel);
                platesRow.Add(plate);
                fighter.ResetForRound();
                var contestant = new Contestant
                {
                    displayName = fighter.DisplayName,
                    fighter = fighter,
                    // Measured AFTER the reset, so it describes the pose the
                    // fighter actually stands in rather than a mid-drop one.
                    startHeadHeight = fighter.HeadHeightAboveGround,
                    plate = plate,
                    label = plateLabel
                };
                _contestants.Add(contestant);
                fighter.CommandStand();
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

            int aliveCount = 0;
            for (int contestantIndex = 0; contestantIndex < _contestants.Count; contestantIndex++)
            {
                Contestant contestant = _contestants[contestantIndex];
                if (contestant.fallen)
                {
                    continue;
                }
                if (HasFallen(contestant))
                {
                    contestant.fallen = true;
                    RaiseFighterFell(contestant.displayName);
                    continue;
                }
                contestant.aliveTime += Time.fixedDeltaTime;
                // Ground-relative, and the divisor is floored: a contestant
                // whose head measured at its own ground level would otherwise
                // divide this by zero, and an infinite uprightnessSum makes
                // Beats() compare infinities and then NaNs, which decides the
                // round by accident rather than by who balanced better.
                contestant.uprightnessSum +=
                    Systems_Contestants.HeadFraction(contestant.fighter, contestant.startHeadHeight)
                    * Time.fixedDeltaTime;
                aliveCount++;
            }

            bool timeUp = _roundTime >= ROUND_TIME_LIMIT;
            // Last one standing ends it there and then. Without this the round
            // ran to its 30 s limit no matter what: measured 2026-08-22, round 2
            // had seven of eight fighters down inside 5 s and then held on a mat
            // of bodies for another 25, which is the single longest stretch of
            // dead air in the game. Guarded on a field of more than one, because
            // a single-fighter ring would otherwise end every round on its first
            // frame.
            bool lastStanding = aliveCount == 1 && _contestants.Count > 1;
            if ((aliveCount == 0 || lastStanding || timeUp) && _contestants.Count > 0)
            {
                _restartTimer = ROUND_RESTART_DELAY;
                Contestant leaderAtEnd = FindLeader();
                LogRoundResult(leaderAtEnd, timeUp, lastStanding);
                RaiseRoundEnded(leaderAtEnd?.displayName ?? "");
            }
        }

        /// <summary>
        /// One greppable line per round: who won, how, and how long every
        /// fighter lasted. Round outcomes used to exist ONLY in the UI Toolkit
        /// HUD, so "does the contest actually resolve rounds correctly" was
        /// unanswerable without a human watching a screen.
        /// </summary>
        private void LogRoundResult(Contestant leader, bool timeUp, bool lastStanding)
        {
            string reason = lastStanding ? "last standing" : (timeUp ? "time up" : "all down");
            var line = new System.Text.StringBuilder();
            line.Append($"CONTEST_ROUND {_round} | {reason} | hazard={_hazard} | winner=");
            line.Append(string.IsNullOrEmpty(leader?.displayName) ? "NO CONTEST" : leader.displayName);
            line.Append(" |");
            for (int contestantIndex = 0; contestantIndex < _contestants.Count; contestantIndex++)
            {
                Contestant contestant = _contestants[contestantIndex];
                line.Append($" {contestant.displayName}={contestant.aliveTime:F1}s");
                line.Append(contestant.fallen ? "(down)" : "(up)");
            }
            Debug.Log(line.ToString());
        }

        private void Update()
        {
            if (!_initialized) { return; }
            bool roundOver = _restartTimer >= 0f;
            Contestant leader = FindLeader();
            for (int contestantIndex = 0; contestantIndex < _contestants.Count; contestantIndex++)
            {
                Contestant contestant = _contestants[contestantIndex];
                contestant.label.text = $"{contestant.displayName}  {contestant.aliveTime:F1}s";
                contestant.plate.EnableInClassList("plate--down", contestant.fallen && !(roundOver && contestant == leader));
                contestant.plate.EnableInClassList("plate--winner", roundOver && contestant == leader);
            }
            _title.text = roundOver
                ? $"Round {_round} over — next in {Mathf.Max(0f, _restartTimer):F0}s"
                : $"Balance Contest — Round {_round}  {Mathf.Max(0f, ROUND_TIME_LIMIT - _roundTime):F0}s";
        }

        private Contestant FindLeader()
        {
            Contestant leader = null;
            for (int contestantIndex = 0; contestantIndex < _contestants.Count; contestantIndex++)
            {
                Contestant contestant = _contestants[contestantIndex];
                if (leader == null || Beats(contestant, leader))
                {
                    leader = contestant;
                }
            }
            return leader;
        }

        /// <summary>
        /// Longest upright wins; a tie on that goes to whoever stood straighter
        /// while doing it.
        ///
        /// The tie-break is not a nicety. At the 30 s round limit ties are the
        /// NORMAL case: every fighter still standing when time runs out has been
        /// alive for exactly the round length, so comparing aliveTime alone
        /// handed the round to whichever fighter FindObjectsByType happened to
        /// return first. Mean uprightness is the natural decider because it is
        /// the thing the contest is nominally about.
        /// </summary>
        private static bool Beats(Contestant candidate, Contestant incumbent)
        {
            const float ALIVE_TIME_EPSILON = 0.001f;
            if (Mathf.Abs(candidate.aliveTime - incumbent.aliveTime) > ALIVE_TIME_EPSILON)
            {
                return candidate.aliveTime > incumbent.aliveTime;
            }
            return MeanUprightness(candidate) > MeanUprightness(incumbent);
        }

        private static float MeanUprightness(Contestant contestant)
        {
            return contestant.aliveTime > 0f ? contestant.uprightnessSum / contestant.aliveTime : 0f;
        }

        /// <summary>
        /// Down by the contestant's own reckoning, or head under 40% of its
        /// standing height. Both GROUND-RELATIVE, never raw world Y: the ring
        /// canvas sits at Systems_ContestSpawner.RING_FLOOR_Y = 1 m, and an
        /// absolute height compared against a fraction of an absolute height
        /// silently rescales with altitude -- a fighter had to sink to four
        /// centimetres above the floor to count as collapsed instead of the
        /// intended ~64 cm.
        /// </summary>
        private static bool HasFallen(Contestant contestant)
        {
            return contestant.fighter.ReportsDown
                || contestant.fighter.HeadHeightAboveGround
                   < contestant.startHeadHeight * HEAD_COLLAPSE_FRACTION;
        }

        private void StartNextRound()
        {
            _round++;
            _restartTimer = -1f;
            _roundTime = 0f;
            for (int contestantIndex = 0; contestantIndex < _contestants.Count; contestantIndex++)
            {
                Contestant contestant = _contestants[contestantIndex];
                contestant.fighter.ResetForRound();
                // Re-asserted after every reset: the 0 m/s end of the locomotion
                // command a single brain serves both mini-games with.
                contestant.fighter.CommandStand();
                contestant.aliveTime = 0f;
                contestant.uprightnessSum = 0f;
                contestant.fallen = false;
            }
            RaiseRoundStarted(_round);
        }
    }
}
