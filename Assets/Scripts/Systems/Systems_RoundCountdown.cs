using UnityEngine;
using UnityEngine.UIElements;

namespace PoBox
{
    /// <summary>
    /// 3-2-1-GO countdown before every contest round: freezes time while the
    /// numbers tick (unscaled), then releases physics on GO so fighters start
    /// upright instead of mid-topple. Triggers itself for round 1 and on each
    /// RoundStarted after. Shares the referee's UIDocument.
    /// Test-scene harness only — never freeze time in training scenes.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    [RequireComponent(typeof(Systems_BalanceContest))]
    public sealed class Systems_RoundCountdown : MonoBehaviour
    {
        private const float SECONDS_PER_TICK = 0.8f;
        private const float GO_LINGER_SECONDS = 0.5f;

        private Systems_BalanceContest _contest;
        private Label _label;
        private float _timer = -1f;
        private int _lastShown;

        private void Start()
        {
            _contest = GetComponent<Systems_BalanceContest>();
            _contest.RoundStarted += OnRoundStarted;

            var root = GetComponent<UIDocument>().rootVisualElement;
            _label = new Label();
            _label.AddToClassList("countdown-label");
            _label.pickingMode = PickingMode.Ignore;
            _label.style.display = DisplayStyle.None;
            root.Add(_label);

            Begin();
        }

        private void OnDestroy()
        {
            if (_contest != null)
            {
                _contest.RoundStarted -= OnRoundStarted;
            }
            Time.timeScale = 1f;
        }

        private void OnRoundStarted(int round)
        {
            Begin();
        }

        private void Begin()
        {
            _timer = 0f;
            _lastShown = -1;
            Time.timeScale = 0f;
            _label.style.display = DisplayStyle.Flex;
        }

        private void Update()
        {
            if (_timer < 0f)
            {
                return;
            }
            _timer += Time.unscaledDeltaTime;

            float total = 3f * SECONDS_PER_TICK;
            if (_timer < total)
            {
                int shown = 3 - (int)(_timer / SECONDS_PER_TICK);
                if (shown != _lastShown)
                {
                    _lastShown = shown;
                    _label.text = shown.ToString();
                }
                return;
            }
            if (_lastShown != 0)
            {
                _lastShown = 0;
                _label.text = "GO!";
                Time.timeScale = 1f;
            }
            if (_timer >= total + GO_LINGER_SECONDS)
            {
                _timer = -1f;
                _label.style.display = DisplayStyle.None;
            }
        }
    }
}
