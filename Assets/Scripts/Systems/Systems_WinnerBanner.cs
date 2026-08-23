using UnityEngine;
using UnityEngine.UIElements;

namespace PoBox
{
    /// <summary>
    /// Winner presentation for the contest test scene: on RoundEnded shows a
    /// popping "NAME WINS!" banner, fires confetti, plays a jingle, and dips
    /// time briefly for a slow-motion beat; on RoundStarted hides the banner
    /// and rings the bell. Shares the referee's UIDocument.
    /// Test-scene harness only — never use time dips in training scenes.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class Systems_WinnerBanner : MonoBehaviour
    {
        private const float POP_SECONDS = 0.45f;
        private const float SLOWMO_SCALE = 0.5f;
        private const float SLOWMO_SECONDS = 1.0f;

        [SerializeField] private ParticleSystem _confetti;
        [SerializeField] private AudioSource _audioSource;
        [SerializeField] private AudioClip _jingle;
        [SerializeField] private AudioClip _bell;

        private Systems_ContestReferee _contest;
        private VisualElement _banner;
        private Label _bannerText;
        private float _popTimer = -1f;
        private float _slowmoTimer = -1f;

        private void Start()
        {
            // Guarded rather than [RequireComponent]: the referee type is now
            // abstract, and Unity's RequireComponent would try to AddComponent it.
            _contest = GetComponent<Systems_ContestReferee>();
            if (_contest == null)
            {
                Debug.LogError($"{name}: no contest referee on this object - the winner banner will never show.");
                return;
            }
            _contest.RoundEnded += OnRoundEnded;
            _contest.RoundStarted += OnRoundStarted;

            var root = GetComponent<UIDocument>().rootVisualElement;
            _banner = new VisualElement();
            _banner.AddToClassList("winner-banner");
            _banner.pickingMode = PickingMode.Ignore;
            _bannerText = new Label();
            _bannerText.AddToClassList("winner-text");
            _banner.Add(_bannerText);
            _banner.style.display = DisplayStyle.None;
            root.Add(_banner);
        }

        private void OnDestroy()
        {
            if (_contest != null)
            {
                _contest.RoundEnded -= OnRoundEnded;
                _contest.RoundStarted -= OnRoundStarted;
            }
            Time.timeScale = 1f;
        }

        private void OnRoundEnded(string winnerName)
        {
            // "NO CONTEST", not "DRAW": an empty winner does not mean the field
            // tied, it means the walk race's leader never cleared the minimum
            // distance and nobody earned the round (Systems_WalkContest.
            // MIN_WIN_DISTANCE). Calling that a draw would tell the player the
            // race was close when what it actually was is that nothing happened.
            _bannerText.text = string.IsNullOrEmpty(winnerName)
                ? "NO CONTEST"
                : $"{winnerName.ToUpperInvariant()} WINS!";
            _banner.style.display = DisplayStyle.Flex;
            _popTimer = 0f;
            _slowmoTimer = 0f;
            Time.timeScale = SLOWMO_SCALE;
            if (_confetti != null)
            {
                _confetti.Play();
            }
            if (_audioSource != null && _jingle != null)
            {
                _audioSource.PlayOneShot(_jingle);
            }
        }

        private void OnRoundStarted(int round)
        {
            _banner.style.display = DisplayStyle.None;
            if (_audioSource != null && _bell != null)
            {
                _audioSource.PlayOneShot(_bell);
            }
        }

        private void Update()
        {
            if (_popTimer >= 0f)
            {
                _popTimer += Time.unscaledDeltaTime;
                // Ease-out-back pop: overshoots slightly, then settles.
                float t = Mathf.Clamp01(_popTimer / POP_SECONDS);
                float back = 1f + 1.7f * Mathf.Pow(t - 1f, 3f) + 1.7f * Mathf.Pow(t - 1f, 2f);
                _bannerText.style.scale = new Scale(new Vector3(back, back, 1f));
                if (t >= 1f)
                {
                    _popTimer = -1f;
                }
            }

            if (_slowmoTimer >= 0f)
            {
                _slowmoTimer += Time.unscaledDeltaTime;
                if (_slowmoTimer >= SLOWMO_SECONDS)
                {
                    _slowmoTimer = -1f;
                    Time.timeScale = 1f;
                }
            }
        }
    }
}
