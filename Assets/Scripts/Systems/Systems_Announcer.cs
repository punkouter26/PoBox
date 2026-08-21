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
        // Callouts are sized to fill the width and then shrunk to fit, because the
        // longest of them overflow: "Grandma IS DOWN!" measures 1292 px at 128 px
        // against a 1080 px panel, so it wrapped to two lines and collided with the
        // winner banner below it. Floor keeps a very long name legible rather than tiny.
        private const float CALLOUT_MAX_FONT_SIZE = 128f;
        private const float CALLOUT_MIN_FONT_SIZE = 64f;
        private const float CALLOUT_SIDE_MARGIN = 32f;
        private const float NEAR_FALL_DIP_FRACTION = 0.72f;
        private const float NEAR_FALL_RECOVER_FRACTION = 0.86f;
        private const float SAVE_COOLDOWN_SECONDS = 5f;

        [SerializeField] private AudioClip _bellClip;
        [SerializeField] private float _bellVolume = 0.7f;

        private Systems_ContestReferee _contest;
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

            // Resolved before any UI is built: the hazard chip has to go into
            // the REFEREE's document, not this one. The announcer owns its own
            // UIDocument, so a shared HUD stack built against this root is a
            // second stack in a second panel — and the match tally, which lives
            // on the referee, then lands at exactly the same coordinates one
            // panel away. Measured 2026-08-21: "Standard ★1" and
            // "HAZARD: BALL RAIN" both at y 158-217.
            _contest = FindFirstObjectByType<Systems_ContestReferee>();
            VisualElement hudRoot = _contest != null
                ? _contest.GetComponent<UIDocument>().rootVisualElement
                : root;

            _callout = new Label();
            _callout.style.position = Position.Absolute;
            // Below the HUD top stack at its two-line worst case, and still
            // above the winner banner (32%) and countdown (42%).
            _callout.style.top = Length.Percent(20f);
            _callout.style.left = 0f;
            _callout.style.right = 0f;
            _callout.style.unityTextAlign = TextAnchor.MiddleCenter;
            _callout.style.fontSize = CALLOUT_MAX_FONT_SIZE;
            _callout.style.whiteSpace = WhiteSpace.NoWrap;
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
            // Sits under the winner tally in the shared HUD stack. Pinning it to
            // an absolute y only ever worked for the tally height of the day: at
            // y 16 a long name ("HAZARD: BALL RAIN" is 562 px wide) reached back
            // under the round title, and at y 200 it collided with the tally as
            // soon as that wrapped to a second line. Stacked, it simply follows
            // whatever the tally ends up occupying.
            // 44 px matches the tally above it and keeps the longest name,
            // "HAZARD: GRAVITY LEAN", on one line inside the stack's band.
            _hazardChip.style.fontSize = 44f;
            _hazardChip.style.unityFontStyleAndWeight = FontStyle.Bold;
            _hazardChip.style.color = Systems_UiTheme.HazardOrange;
            _hazardChip.pickingMode = PickingMode.Ignore;
            Systems_UiTheme.HudHazardSlot(hudRoot).Add(_hazardChip);

            _rigs = FindObjectsByType<Systems_FighterRig>(FindObjectsSortMode.InstanceID);
            _startHeadHeights = new float[_rigs.Length];
            _inDip = new bool[_rigs.Length];
            _saveCooldowns = new float[_rigs.Length];
            for (int rigIndex = 0; rigIndex < _rigs.Length; rigIndex++)
            {
                _startHeadHeights[rigIndex] = _rigs[rigIndex].Head.position.y;
            }

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
            _callout.style.fontSize = FittedCalloutFontSize(message);
            _callout.style.display = DisplayStyle.Flex;
            _callout.style.opacity = 1f;
            _calloutRemaining = CALLOUT_SECONDS;
        }

        /// <summary>
        /// Largest size at or below <see cref="CALLOUT_MAX_FONT_SIZE"/> that keeps
        /// <paramref name="message"/> on one line. Text width scales linearly with
        /// font size, so measuring once at whatever size the label currently carries
        /// gives the answer without a search loop — and without assuming the previous
        /// callout left the label at full size.
        /// </summary>
        private float FittedCalloutFontSize(string message)
        {
            // Deliberately the PARENT width, not the label's own. The callout is
            // display:none between announcements, so its layout is stale at zero
            // every time Announce runs and reading it silently disabled this whole
            // method. The parent spans the panel and is always laid out.
            float panelWidth = _callout.parent != null ? _callout.parent.resolvedStyle.width : 0f;
            float available = panelWidth - CALLOUT_SIDE_MARGIN * 2f;
            // resolvedStyle.fontSize may lag a frame behind the last size set, but
            // MeasureTextSize reads the same computed style, so the ratio of the two
            // is width-per-point regardless of which size that happens to be.
            float measuredAtSize = _callout.resolvedStyle.fontSize;
            if (available <= 0f || measuredAtSize <= 0f)
            {
                return CALLOUT_MAX_FONT_SIZE;
            }
            Vector2 measured = _callout.MeasureTextSize(message, 0f, VisualElement.MeasureMode.Undefined,
                0f, VisualElement.MeasureMode.Undefined);
            if (measured.x <= 0f)
            {
                return CALLOUT_MAX_FONT_SIZE;
            }
            float fitted = available / (measured.x / measuredAtSize);
            // Below the floor, shrinking further would be unreadable, so let it wrap
            // instead — a two-line callout beats one that runs off the screen.
            _callout.style.whiteSpace = fitted < CALLOUT_MIN_FONT_SIZE
                ? WhiteSpace.Normal
                : WhiteSpace.NoWrap;
            return Mathf.Clamp(fitted, CALLOUT_MIN_FONT_SIZE, CALLOUT_MAX_FONT_SIZE);
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
