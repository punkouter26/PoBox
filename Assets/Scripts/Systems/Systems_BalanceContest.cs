using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoBox
{
    /// <summary>
    /// Referee for the balance contest test scene: every fighter stands until
    /// a fall sensor touches ground or its head collapses; longest time wins.
    /// Self-discovers contestants at Start, shows a UI Toolkit scoreboard,
    /// announces the winner, then resets everyone for the next round.
    /// Test-scene harness only — not used in training or the game loop.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class Systems_BalanceContest : MonoBehaviour
    {
        private const float HEAD_COLLAPSE_FRACTION = 0.4f;
        private const float ROUND_RESTART_DELAY = 4f;

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

        private void Start()
        {
            var root = GetComponent<UIDocument>().rootVisualElement;
            root.style.position = Position.Absolute;
            root.style.left = 12;
            root.style.top = 12;

            _title = MakeLabel(root, 22, FontStyle.Bold);
            _title.text = "Balance Contest — Round 1";

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
                var contestant = new Contestant
                {
                    displayName = rig.gameObject.name.Replace("Contest_", ""),
                    rig = rig,
                    fallSensors = fallSensors.ToArray(),
                    startHeadHeight = rig.Head.position.y,
                    label = MakeLabel(root, 17, FontStyle.Normal)
                };
                _contestants.Add(contestant);
            }
        }

        private Label MakeLabel(VisualElement root, int size, FontStyle style)
        {
            var label = new Label();
            // The contest panel uses an empty theme, which supplies no font —
            // without an explicit one the labels render as nothing at all.
            label.style.unityFontDefinition = FontDefinition.FromFont(
                Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
            label.style.fontSize = size;
            label.style.unityFontStyleAndWeight = style;
            label.style.color = Color.white;
            label.style.textShadow = new TextShadow { offset = new Vector2(1f, 1f), blurRadius = 2f, color = Color.black };
            label.style.marginBottom = 2;
            root.Add(label);
            return label;
        }

        private void FixedUpdate()
        {
            if (_restartTimer >= 0f)
            {
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
                    continue;
                }
                contestant.aliveTime += Time.fixedDeltaTime;
                aliveCount++;
            }

            if (aliveCount == 0 && _contestants.Count > 0)
            {
                _restartTimer = ROUND_RESTART_DELAY;
            }
        }

        private void Update()
        {
            bool roundOver = _restartTimer >= 0f;
            Contestant leader = null;
            for (int contestantIndex = 0; contestantIndex < _contestants.Count; contestantIndex++)
            {
                Contestant contestant = _contestants[contestantIndex];
                if (leader == null || contestant.aliveTime > leader.aliveTime)
                {
                    leader = contestant;
                }
            }
            for (int contestantIndex = 0; contestantIndex < _contestants.Count; contestantIndex++)
            {
                Contestant contestant = _contestants[contestantIndex];
                string status = contestant.fallen ? "DOWN" : "standing";
                string crown = roundOver && contestant == leader ? "  << WINNER" : "";
                contestant.label.text = $"{contestant.displayName}: {contestant.aliveTime:F1} s  ({status}){crown}";
            }
            _title.text = roundOver
                ? $"Balance Contest — Round {_round} over, next in {Mathf.Max(0f, _restartTimer):F0} s"
                : $"Balance Contest — Round {_round}";
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
        }
    }
}
