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
    /// RoundEnded/RoundStarted events so the existing banner, crowd and FX
    /// systems work unchanged.
    /// Test-scene harness only — not used in training or the game loop.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class Systems_WalkContest : MonoBehaviour
    {
        private const float HEAD_COLLAPSE_FRACTION = 0.4f;
        private const float ROUND_RESTART_DELAY = 4f;
        private const float ROUND_TIME_LIMIT = 60f;
        // Commanded pace for the race. 1 m/s is a normal human walk.
        private const float RACE_SPEED = 1.0f;

        [SerializeField] private StyleSheet _styleSheet;
        // World direction from the start edge to the finish edge, and how far
        // that is. Written by the walk contest scene builder.
        [SerializeField] private Vector3 _goalDirection = Vector3.forward;
        [SerializeField] private float _goalDistance = 5.6f;

        public event System.Action<string> RoundEnded;
        public event System.Action<int> RoundStarted;
        public event System.Action<string> FighterFell;

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
            public Label label;
        }

        private readonly List<Racer> _racers = new();
        private Label _title;
        private int _round = 1;
        private float _restartTimer = -1f;
        private float _roundTime;

        /// <summary>Set by the match director when the match is decided: the referee stops starting new rounds.</summary>
        public bool HoldRestarts { get; set; }

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
                var plate = new Label();
                plate.AddToClassList("plate");
                platesRow.Add(plate);
                var racer = new Racer
                {
                    displayName = rig.gameObject.name.Replace("Contest_", ""),
                    rig = rig,
                    agent = rig.GetComponent<Agent_FighterBoxing>(),
                    fallSensors = fallSensors.ToArray(),
                    startHeadHeight = rig.Head.position.y,
                    startProjection = Vector3.Dot(rig.Pelvis.position, _goalDirection),
                    label = plate
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
                    FighterFell?.Invoke(racer.displayName);
                    continue;
                }

                racer.travelled = Vector3.Dot(racer.rig.Pelvis.position, _goalDirection) - racer.startProjection;
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
            if ((racingCount == 0 || timeUp) && _racers.Count > 0)
            {
                _restartTimer = ROUND_RESTART_DELAY;
                RoundEnded?.Invoke(FindLeader()?.displayName ?? "");
            }
        }

        private void Update()
        {
            bool roundOver = _restartTimer >= 0f;
            Racer leader = FindLeader();
            for (int racerIndex = 0; racerIndex < _racers.Count; racerIndex++)
            {
                Racer racer = _racers[racerIndex];
                racer.label.text = racer.finished
                    ? $"{racer.displayName}  {racer.finishTime:F1}s"
                    : $"{racer.displayName}  {racer.travelled:F1}m";
                racer.label.EnableInClassList("plate--down", racer.fallen && !(roundOver && racer == leader));
                racer.label.EnableInClassList("plate--winner", roundOver && racer == leader);
            }
            _title.text = roundOver
                ? $"Round {_round} over — next in {Mathf.Max(0f, _restartTimer):F0}s"
                : $"Walk Contest — Round {_round}  {Mathf.Max(0f, ROUND_TIME_LIMIT - _roundTime):F0}s";
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
            RoundStarted?.Invoke(_round);
        }
    }
}
