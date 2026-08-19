using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace PoBox
{
    /// <summary>
    /// Knockout presentation: a vignette pulse whenever a fighter goes down,
    /// and a short slow-motion beat with a deeper vignette when the round
    /// ends (winner freeze-frame feel). Works on a runtime clone of the post
    /// volume profile, so the shared asset is never modified. Time scale is
    /// always restored on disable/destroy; fixed Δt is never touched.
    /// Test-scene harness only.
    /// </summary>
    public sealed class Systems_KnockoutFx : MonoBehaviour
    {
        private const float PULSE_SECONDS = 0.5f;
        private const float PULSE_INTENSITY = 0.45f;
        private const float SLOWMO_SCALE = 0.35f;
        private const float SLOWMO_REAL_SECONDS = 1.4f;

        [SerializeField] private Volume _volume;

        private Systems_BalanceContest _contest;
        private Systems_FallImpactFx _fallFx;
        private Vignette _vignette;
        private float _baseVignette;
        private float _pulseRemaining;
        private float _slowmoRemaining;

        // Called by the editor scene tool.
        public void EditorInitialize(Volume volume)
        {
            _volume = volume;
        }

        private void Start()
        {
            if (_volume != null && _volume.profile.TryGet(out _vignette))
            {
                _baseVignette = _vignette.intensity.value;
            }
            _contest = FindFirstObjectByType<Systems_BalanceContest>();
            if (_contest != null)
            {
                _contest.RoundEnded += OnRoundEnded;
                _contest.RoundStarted += OnRoundStarted;
            }
            _fallFx = FindFirstObjectByType<Systems_FallImpactFx>();
            if (_fallFx != null)
            {
                _fallFx.FighterFell += OnFighterFell;
            }
        }

        private void OnDestroy()
        {
            Time.timeScale = 1f;
            if (_contest != null)
            {
                _contest.RoundEnded -= OnRoundEnded;
                _contest.RoundStarted -= OnRoundStarted;
            }
            if (_fallFx != null)
            {
                _fallFx.FighterFell -= OnFighterFell;
            }
        }

        private void OnDisable()
        {
            Time.timeScale = 1f;
        }

        private void OnFighterFell(Vector3 position)
        {
            _pulseRemaining = PULSE_SECONDS;
        }

        private void OnRoundEnded(string winnerName)
        {
            _slowmoRemaining = SLOWMO_REAL_SECONDS;
            Time.timeScale = SLOWMO_SCALE;
            _pulseRemaining = PULSE_SECONDS * 2f;
        }

        private void OnRoundStarted(int round)
        {
            // Cancel a pending slow-mo but do NOT touch timeScale here — the
            // round countdown freezes time at round start and owns the clock.
            _slowmoRemaining = 0f;
        }

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;

            if (_slowmoRemaining > 0f)
            {
                _slowmoRemaining -= dt;
                // Restore only from our own slow-mo — never overwrite the
                // countdown's freeze (timeScale 0).
                if (_slowmoRemaining <= 0f && Mathf.Approximately(Time.timeScale, SLOWMO_SCALE))
                {
                    Time.timeScale = 1f;
                }
            }

            if (_vignette == null)
            {
                return;
            }
            if (_pulseRemaining > 0f)
            {
                _pulseRemaining -= dt;
                float envelope = Mathf.Sin(Mathf.Clamp01(1f - _pulseRemaining / PULSE_SECONDS) * Mathf.PI);
                _vignette.intensity.value = _baseVignette + PULSE_INTENSITY * envelope;
            }
            else if (_vignette.intensity.value != _baseVignette)
            {
                _vignette.intensity.value = _baseVignette;
            }
        }
    }
}
