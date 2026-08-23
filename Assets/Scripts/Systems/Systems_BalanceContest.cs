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
    /// presentation systems (banner, crowd, FX) through
    /// <see cref="Systems_ContestReferee"/>, which is what they bind to.
    /// Test-scene harness only — not used in training or the game loop.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class Systems_BalanceContest : Systems_ContestReferee
    {
        private const float HEAD_COLLAPSE_FRACTION = 0.4f;
        private const float ROUND_RESTART_DELAY = 4f;
        /// <summary>
        /// Hard cap on a round, mirroring <see cref="Systems_WalkContest"/>.
        /// Without one the round ended only when the last fighter fell, and the
        /// code-driven PD bot does not fall: measured 2026-08-21, every trained
        /// fighter in an eight-slot ring was down inside 3.0 s while the bot was
        /// still standing at 109 s, so a ring containing a bot never advanced.
        /// 30 s is long enough to settle a balance round on merit and short
        /// enough that a lone unfallable survivor cannot stall the match.
        /// </summary>
        private const float ROUND_TIME_LIMIT = 30f;

        [SerializeField] private StyleSheet _styleSheet;

        private sealed class Contestant
        {
            public string displayName;
            public Systems_FighterRig rig;
            public Agent_FighterBoxing agent;
            public Sensor_GroundContact[] fallSensors;
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
        private float _roundTime;

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
                var contestant = new Contestant
                {
                    displayName = displayName,
                    rig = rig,
                    agent = rig.GetComponent<Agent_FighterBoxing>(),
                    fallSensors = fallSensors.ToArray(),
                    startHeadHeight = rig.Head.position.y,
                    plate = plate,
                    label = plateLabel
                };
                _contestants.Add(contestant);
                CommandStand(contestant);
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
                contestant.uprightnessSum +=
                    contestant.rig.Head.position.y / contestant.startHeadHeight * Time.fixedDeltaTime;
                aliveCount++;
            }

            bool timeUp = _roundTime >= ROUND_TIME_LIMIT;
            // Last one standing ends it there and then. Without this the round
            // ran to its 30 s limit no matter what: measured 2026-08-22, round 2
            // had seven of eight fighters down inside 5 s and then held on a mat
            // of bodies for another 25, which is the single longest stretch of
            // dead air in the game. Guarded on a field of more than one, because
            // a single-fighter ring — the spawner's fallback when nothing is
            // picked — would otherwise end every round on its first frame.
            bool lastStanding = aliveCount == 1 && _contestants.Count > 1;
            if ((aliveCount == 0 || lastStanding || timeUp) && _contestants.Count > 0)
            {
                _restartTimer = ROUND_RESTART_DELAY;
                RaiseRoundEnded(FindLeader()?.displayName ?? "");
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
        /// The tie-break is not a nicety. Comparing aliveTime alone with a
        /// strict &gt; hands every tie to whichever fighter FindObjectsByType
        /// happened to return first, and at the 30 s round limit ties are the
        /// NORMAL case, not the edge one: every fighter still standing when time
        /// runs out has been alive for exactly the round length. Measured
        /// 2026-08-22, four of eight were sitting on an identical 4.0 s four
        /// seconds into round 4. So the round — and, three rounds later, the
        /// match — was being decided by scene hierarchy order.
        ///
        /// Mean uprightness is the natural decider because it is the thing the
        /// contest is nominally about: of two fighters who both lasted the
        /// distance, the one that spent it nearer its full standing height
        /// balanced better. It is a float sum over fixed ticks, so an exact tie
        /// on it is vanishingly unlikely; if one happens, order decides, and by
        /// then the two really are indistinguishable.
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
        /// Tells the fighter to hold station. The balance contest is the
        /// 0 m/s end of the locomotion command the shared brain was trained
        /// against (Reward_Locomotion: 0 = stand, 1 = walk), so saying so
        /// explicitly is what makes one brain serve both mini-games — and it
        /// re-asserts the command after every round reset. Harmless for a
        /// fighter whose brain does not observe the command.
        /// </summary>
        private static void CommandStand(Contestant contestant)
        {
            if (contestant.agent != null)
            {
                contestant.agent.SetLocomotionCommand(0f, Vector3.forward);
            }
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
            _roundTime = 0f;
            for (int contestantIndex = 0; contestantIndex < _contestants.Count; contestantIndex++)
            {
                Contestant contestant = _contestants[contestantIndex];
                contestant.rig.ResetToStartPose();
                foreach (Sensor_GroundContact sensor in contestant.rig.GetComponentsInChildren<Sensor_GroundContact>(true))
                {
                    sensor.ResetContacts();
                }
                CommandStand(contestant);
                contestant.aliveTime = 0f;
                contestant.uprightnessSum = 0f;
                contestant.fallen = false;
            }
            RaiseRoundStarted(_round);
        }
    }
}
