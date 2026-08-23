using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoBox
{
    /// <summary>
    /// Referee for the walk contest test scene: every fighter starts on one
    /// edge of the ring and races straight to the far edge. First across wins;
    /// a fall parks that racer where it dropped and its distance stands as its
    /// score, so a round always resolves even when nobody finishes. Mirrors
    /// <see cref="Systems_BalanceContest"/> — self-discovers contestants at
    /// Start, drives the same USS_Contest scoreboard, and raises the same
    /// RoundEnded/RoundStarted events through the shared
    /// <see cref="Systems_ContestReferee"/> base, so the banner, crowd, match
    /// director and FX systems bind to it exactly as they do to the balance
    /// referee. They could not before: each of them named
    /// Systems_BalanceContest outright, so this scene ran with no announcer,
    /// no winner banner and - lacking a match director to set HoldRestarts -
    /// no end at all.
    /// Test-scene harness only — not used in training or the game loop.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class Systems_WalkContest : Systems_ContestReferee
    {
        private const float HEAD_COLLAPSE_FRACTION = 0.4f;
        // Was 4 s, set when a round was expected to last most of its 60 s limit.
        // It does not: measured 2026-08-22, rounds ended at roundTime 2.3 s with
        // the whole field down, so the race spent nearly twice as long showing a
        // frozen scoreboard as it did racing. The banner and the crowd cheer
        // still need room to land, which is what the remaining 2.5 s is for.
        private const float ROUND_RESTART_DELAY = 2.5f;
        private const float ROUND_TIME_LIMIT = 60f;
        // Commanded pace for the race. 1 m/s is a normal human walk.
        private const float RACE_SPEED = 1.0f;

        /// <summary>
        /// How far the leader must have actually walked for the round to be
        /// awarded to anybody.
        ///
        /// Without it the race declares a winner no matter what happens, and
        /// what happens is nothing: measured 2026-08-22 over five rounds, the
        /// winning distances toward a 5.6 m goal were 0.5 m, 0.2 m and 0.2 m,
        /// and the rest of the field scored NEGATIVE — round 5 went to Grandpa
        /// for falling forward 20 cm while the other three fell backward. A
        /// scoreboard that crowns a champion out of that is lying to the player
        /// about what it just showed them. Under this bar the round is a no
        /// contest, no star is awarded, and the match keeps going until somebody
        /// earns one.
        ///
        /// 0.75 m is a bit over one step. It is deliberately low: the point is
        /// to reject topple noise, not to set a competitive standard.
        /// </summary>
        private const float MIN_WIN_DISTANCE = 0.75f;

        /// <summary>
        /// A round also ends when the field stops making progress for this long.
        ///
        /// The fall rule alone cannot end a round in which somebody simply
        /// stands still — the heuristic bot is very good at not falling over and
        /// commanding it to walk does not oblige it to — so a stalled race would
        /// hold the scene for the full 60 s limit showing nothing at all. Ending
        /// on a stall keeps the worst case at about a quarter of that.
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
            public Systems_FighterRig rig;
            public Agent_FighterBoxing agent;
            public Sensor_GroundContact[] fallSensors;
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

        // Called by the walk contest scene builder.
        public void EditorInitialize(Vector3 goalDirection, float goalDistance, StyleSheet styleSheet)
        {
            _goalDirection = goalDirection.normalized;
            _goalDistance = goalDistance;
            _styleSheet = styleSheet;
        }

        private void Start()
        {
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
                    "render unstyled and effectively invisible. Assign USS_Contest, or rebuild " +
                    "the scene with the walk contest scene tool.");
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

            Systems_FighterRig[] rigs = FindObjectsByType<Systems_FighterRig>(FindObjectsSortMode.InstanceID);
            for (int rigIndex = 0; rigIndex < rigs.Length; rigIndex++)
            {
                Systems_FighterRig rig = rigs[rigIndex];
                var fallSensors = new List<Sensor_GroundContact>();
                foreach (Sensor_GroundContact sensor in rig.GetComponentsInChildren<Sensor_GroundContact>(true))
                {
                    if (sensor != rig.FootLeftSensor && sensor != rig.FootRightSensor)
                    {
                        fallSensors.Add(sensor);
                    }
                }
                Systems_FighterIdentity.Resolve(rig, out string displayName, out Color plateColor);
                VisualElement plate = Systems_UiTheme.BuildPlate(plateColor, out Label plateLabel);
                platesRow.Add(plate);
                var racer = new Racer
                {
                    displayName = displayName,
                    rig = rig,
                    agent = rig.GetComponent<Agent_FighterBoxing>(),
                    fallSensors = fallSensors.ToArray(),
                    startHeadHeight = rig.Head.position.y,
                    startProjection = Vector3.Dot(rig.Pelvis.position, _goalDirection),
                    plate = plate,
                    label = plateLabel
                };
                _racers.Add(racer);
                CommandRace(racer);
            }
        }

        private void FixedUpdate()
        {
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
                // and still placing, which is a number no scoreboard should
                // ever have shown a player.
                float projected = Vector3.Dot(racer.rig.Pelvis.position, _goalDirection) - racer.startProjection;
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

        // Ranking: anyone who finished beats anyone who did not, earliest
        // finish first; among the unfinished, furthest travelled wins.
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

        // Tells the fighter to walk. A trained brain reads this as an
        // observation; the code-driven bot reads it to switch its scripted gait
        // on. Without it both would just stand on the start line.
        private void CommandRace(Racer racer)
        {
            if (racer.agent == null)
            {
                return;
            }
            racer.agent.SetLocomotionCommand(RACE_SPEED, _goalDirection);
        }

        private bool HasFallen(Racer racer)
        {
            for (int sensorIndex = 0; sensorIndex < racer.fallSensors.Length; sensorIndex++)
            {
                if (racer.fallSensors[sensorIndex].IsGrounded)
                {
                    return true;
                }
            }
            return racer.rig.Head.position.y < racer.startHeadHeight * HEAD_COLLAPSE_FRACTION;
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
                racer.rig.ResetToStartPose();
                foreach (Sensor_GroundContact sensor in racer.rig.GetComponentsInChildren<Sensor_GroundContact>(true))
                {
                    sensor.ResetContacts();
                }
                racer.startProjection = Vector3.Dot(racer.rig.Pelvis.position, _goalDirection);
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
