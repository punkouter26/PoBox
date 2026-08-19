using UnityEngine;
using UnityEngine.UIElements;

namespace PoBox
{
    /// <summary>
    /// Ring announcer for the contest: big center callouts driven by referee
    /// events (round start/end, falls), near-fall saves detected from head
    /// height dips, hazard announcements from Systems_HazardDirector, and a
    /// boxing bell at round boundaries. Lives under the contest systems root,
    /// so Start runs after fighters spawn. Test-scene harness only.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    [RequireComponent(typeof(AudioSource))]
    public sealed class Systems_Announcer : MonoBehaviour
    {
        private const float CALLOUT_SECONDS = 2.6f;
        private const float NEAR_FALL_DIP_FRACTION = 0.72f;
        private const float NEAR_FALL_RECOVER_FRACTION = 0.86f;
        private const float SAVE_COOLDOWN_SECONDS = 5f;

        [SerializeField] private AudioClip _bellClip;
        [SerializeField] private float _bellVolume = 0.7f;

        private Systems_BalanceContest _contest;
        private Systems_HazardDirector _hazards;
        private Systems_MatchDirector _match;
        private AudioSource _audioSource;
        private Systems_FighterRig[] _rigs;
        private float[] _startHeadHeights;
        private bool[] _inDip;
        private float[] _saveCooldowns;
        private Label _callout;
        private Label _hazardChip;
        private float _calloutRemaining;

        private void Awake()
        {
            _audioSource = GetComponent<AudioSource>();
        }

        private void Start()
        {
            var root = GetComponent<UIDocument>().rootVisualElement;
            Systems_UiTheme.ApplyDefaultFont(root);

            _callout = new Label();
            _callout.style.position = Position.Absolute;
            _callout.style.top = Length.Percent(15f); // below title+scoreboard, above winner banner (32%) and countdown (42%)
            _callout.style.left = 0f;
            _callout.style.right = 0f;
            _callout.style.unityTextAlign = TextAnchor.MiddleCenter;
            _callout.style.fontSize = 128f;
            _callout.style.unityFontStyleAndWeight = FontStyle.Bold;
            _callout.style.color = Systems_UiTheme.GoldBright;
            _callout.style.textShadow = new TextShadow
            {
                offset = new Vector2(0f, 3f),
                blurRadius = 6f,
                color = new Color(0f, 0f, 0f, 0.9f)
            };
            _callout.pickingMode = PickingMode.Ignore;
            _callout.style.display = DisplayStyle.None;
            root.Add(_callout);

            _hazardChip = new Label();
            _hazardChip.style.position = Position.Absolute;
            _hazardChip.style.top = 16f;
            _hazardChip.style.right = 16f;
            _hazardChip.style.fontSize = 52f;
            _hazardChip.style.unityFontStyleAndWeight = FontStyle.Bold;
            _hazardChip.style.color = Systems_UiTheme.HazardOrange;
            _hazardChip.pickingMode = PickingMode.Ignore;
            root.Add(_hazardChip);

            _rigs = FindObjectsByType<Systems_FighterRig>(FindObjectsSortMode.InstanceID);
            _startHeadHeights = new float[_rigs.Length];
            _inDip = new bool[_rigs.Length];
            _saveCooldowns = new float[_rigs.Length];
            for (int rigIndex = 0; rigIndex < _rigs.Length; rigIndex++)
            {
                _startHeadHeights[rigIndex] = _rigs[rigIndex].Head.position.y;
            }

            _contest = FindFirstObjectByType<Systems_BalanceContest>();
            if (_contest != null)
            {
                _contest.RoundStarted += OnRoundStarted;
                _contest.RoundEnded += OnRoundEnded;
                _contest.FighterFell += OnFighterFell;
            }
            _hazards = FindFirstObjectByType<Systems_HazardDirector>();
            if (_hazards != null)
            {
                _hazards.HazardChosen += OnHazardChosen;
            }
            _match = FindFirstObjectByType<Systems_MatchDirector>();
            if (_match != null)
            {
                _match.ChampionCrowned += OnChampionCrowned;
            }

            RingBell();
            Announce("ROUND 1 — FIGHT!");
        }

        private void OnDestroy()
        {
            if (_contest != null)
            {
                _contest.RoundStarted -= OnRoundStarted;
                _contest.RoundEnded -= OnRoundEnded;
                _contest.FighterFell -= OnFighterFell;
            }
            if (_hazards != null)
            {
                _hazards.HazardChosen -= OnHazardChosen;
            }
            if (_match != null)
            {
                _match.ChampionCrowned -= OnChampionCrowned;
            }
        }

        private void OnChampionCrowned(string championName)
        {
            RingBell();
            Announce($"{championName} IS THE CHAMPION!");
        }

        private void Update()
        {
            float dt = Time.deltaTime;

            if (_calloutRemaining > 0f)
            {
                _calloutRemaining -= dt;
                _callout.style.opacity = Mathf.Clamp01(_calloutRemaining / 0.5f);
                if (_calloutRemaining <= 0f)
                {
                    _callout.style.display = DisplayStyle.None;
                }
            }

            for (int rigIndex = 0; rigIndex < _rigs.Length; rigIndex++)
            {
                if (_saveCooldowns[rigIndex] > 0f)
                {
                    _saveCooldowns[rigIndex] -= dt;
                }
                float headFraction = _rigs[rigIndex].Head.position.y / _startHeadHeights[rigIndex];
                if (!_inDip[rigIndex] && headFraction < NEAR_FALL_DIP_FRACTION && headFraction > 0.5f)
                {
                    _inDip[rigIndex] = true;
                }
                else if (_inDip[rigIndex] && headFraction > NEAR_FALL_RECOVER_FRACTION)
                {
                    _inDip[rigIndex] = false;
                    if (_saveCooldowns[rigIndex] <= 0f)
                    {
                        _saveCooldowns[rigIndex] = SAVE_COOLDOWN_SECONDS;
                        Announce($"{DisplayName(rigIndex)} SURVIVES!");
                    }
                }
            }
        }

        private string DisplayName(int rigIndex)
        {
            return _rigs[rigIndex].gameObject.name.Replace("Contest_", "");
        }

        private void OnRoundStarted(int round)
        {
            RingBell();
            Announce($"ROUND {round} — FIGHT!");
        }

        private void OnRoundEnded(string winnerName)
        {
            RingBell();
            Announce(string.IsNullOrEmpty(winnerName) ? "ROUND OVER!" : $"{winnerName} WINS THE ROUND!");
        }

        private void OnFighterFell(string fighterName)
        {
            Announce($"{fighterName} IS DOWN!");
        }

        private void OnHazardChosen(string hazardName)
        {
            _hazardChip.text = $"HAZARD: {hazardName}";
            Announce($"HAZARD — {hazardName}!");
        }

        private void Announce(string message)
        {
            _callout.text = message;
            _callout.style.display = DisplayStyle.Flex;
            _callout.style.opacity = 1f;
            _calloutRemaining = CALLOUT_SECONDS;
        }

        private void RingBell()
        {
            if (_bellClip != null)
            {
                _audioSource.PlayOneShot(_bellClip, _bellVolume);
            }
        }
    }
}
