using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoBox
{
    /// <summary>
    /// Referee for the balance contest test scene: every fighter stands until
    /// a fall sensor touches ground or its head collapses; longest time wins.
    /// Self-discovers contestants at Start, shows a UI Toolkit scoreboard
    /// (styled by USS_Contest.uss: title chip up top, name plates at the
    /// bottom so the ring stays unobstructed), announces the winner, then
    /// resets everyone for the next round. Raises RoundEnded/RoundStarted for
    /// presentation systems (banner, crowd, FX).
    /// Test-scene harness only — not used in training or the game loop.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class Systems_BalanceContest : MonoBehaviour
    {
        private const float HEAD_COLLAPSE_FRACTION = 0.4f;
        private const float ROUND_RESTART_DELAY = 4f;

        [SerializeField] private StyleSheet _styleSheet;

        public event System.Action<string> RoundEnded;
        public event System.Action<int> RoundStarted;
        public event System.Action<string> FighterFell;

        private sealed class Contestant
        {
            public string displayName;
            public Systems_FighterRig rig;
            public Sensor_GroundContact[] fallSensors;
            public float startHeadHeight;
            public float aliveTime;
            public bool fallen;
            public Label label;
        }

        private readonly List<Contestant> _contestants = new();
        private Label _title;
        private int _round = 1;
        private float _restartTimer = -1f;

        /// <summary>Set by the match director when the match is decided: the referee stops starting new rounds.</summary>
        public bool HoldRestarts { get; set; }

        private void Start()
        {
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
                var contestant = new Contestant
                {
                    displayName = rig.gameObject.name.Replace("Contest_", ""),
                    rig = rig,
                    fallSensors = fallSensors.ToArray(),
                    startHeadHeight = rig.Head.position.y,
                    label = plate
                };
                _contestants.Add(contestant);
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
                    FighterFell?.Invoke(contestant.displayName);
                    continue;
                }
                contestant.aliveTime += Time.fixedDeltaTime;
                aliveCount++;
            }

            if (aliveCount == 0 && _contestants.Count > 0)
            {
                _restartTimer = ROUND_RESTART_DELAY;
                RoundEnded?.Invoke(FindLeader()?.displayName ?? "");
            }
        }

        private void Update()
        {
            bool roundOver = _restartTimer >= 0f;
            Contestant leader = FindLeader();
            for (int contestantIndex = 0; contestantIndex < _contestants.Count; contestantIndex++)
            {
                Contestant contestant = _contestants[contestantIndex];
                contestant.label.text = $"{contestant.displayName}  {contestant.aliveTime:F1}s";
                contestant.label.EnableInClassList("plate--down", contestant.fallen && !(roundOver && contestant == leader));
                contestant.label.EnableInClassList("plate--winner", roundOver && contestant == leader);
            }
            _title.text = roundOver
                ? $"Round {_round} over — next in {Mathf.Max(0f, _restartTimer):F0}s"
                : $"Balance Contest — Round {_round}";
        }

        private Contestant FindLeader()
        {
            Contestant leader = null;
            for (int contestantIndex = 0; contestantIndex < _contestants.Count; contestantIndex++)
            {
                Contestant contestant = _contestants[contestantIndex];
                if (leader == null || contestant.aliveTime > leader.aliveTime)
                {
                    leader = contestant;
                }
            }
            return leader;
        }

        private bool HasFallen(Contestant contestant)
        {
            for (int sensorIndex = 0; sensorIndex < contestant.fallSensors.Length; sensorIndex++)
            {
                if (contestant.fallSensors[sensorIndex].IsGrounded)
                {
                    return true;
                }
            }
            return contestant.rig.Head.position.y < contestant.startHeadHeight * HEAD_COLLAPSE_FRACTION;
        }

        private void StartNextRound()
        {
            _round++;
            _restartTimer = -1f;
            for (int contestantIndex = 0; contestantIndex < _contestants.Count; contestantIndex++)
            {
                Contestant contestant = _contestants[contestantIndex];
                contestant.rig.ResetToStartPose();
                foreach (Sensor_GroundContact sensor in contestant.rig.GetComponentsInChildren<Sensor_GroundContact>(true))
                {
                    sensor.ResetContacts();
                }
                contestant.aliveTime = 0f;
                contestant.fallen = false;
            }
            RoundStarted?.Invoke(_round);
        }
    }
}
