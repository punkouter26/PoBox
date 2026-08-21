using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Crowd bed for the contest test scene: a quiet looping ambience, a
    /// cheer swell whenever a fighter goes down (via Systems_FallImpactFx),
    /// and a big cheer for the round winner (via Systems_BalanceContest).
    /// Test-scene harness only — not used in training or the game loop.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class Systems_CrowdAudio : MonoBehaviour
    {
        [SerializeField] private Systems_ContestReferee _contest;
        [SerializeField] private Systems_FallImpactFx _fallFx;
        [SerializeField] private AudioClip _ambienceLoop;
        [SerializeField] private AudioClip _cheer;
        [SerializeField] private float _ambienceVolume = 0.25f;
        [SerializeField] private float _cheerVolume = 0.8f;

        private AudioSource _ambienceSource;

        private void Start()
        {
            _ambienceSource = GetComponent<AudioSource>();
            if (_ambienceLoop != null)
            {
                _ambienceSource.clip = _ambienceLoop;
                _ambienceSource.loop = true;
                _ambienceSource.volume = _ambienceVolume;
                _ambienceSource.spatialBlend = 0f;
                _ambienceSource.Play();
            }
            if (_contest != null)
            {
                _contest.RoundEnded += OnRoundEnded;
            }
            if (_fallFx != null)
            {
                _fallFx.FighterFell += OnFighterFell;
            }
        }

        private void OnDestroy()
        {
            if (_contest != null)
            {
                _contest.RoundEnded -= OnRoundEnded;
            }
            if (_fallFx != null)
            {
                _fallFx.FighterFell -= OnFighterFell;
            }
        }

        private void OnRoundEnded(string winnerName)
        {
            if (_cheer != null)
            {
                _ambienceSource.PlayOneShot(_cheer, _cheerVolume);
            }
        }

        private void OnFighterFell(Vector3 position)
        {
            if (_cheer != null)
            {
                _ambienceSource.PlayOneShot(_cheer, _cheerVolume * 0.4f);
            }
        }
    }
}
