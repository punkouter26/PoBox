using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Presentation feedback for falls in the contest test scene: when a
    /// contestant's head drops below the fall threshold, plays a dust burst
    /// and a soft body thud at the impact point, kicks the drama camera, and
    /// raises FighterFell for other presentation systems (crowd, knockout FX).
    /// Re-arms when the contestant is reset upright. Self-discovers
    /// contestants through <see cref="IContestFighter"/>.
    /// Test-scene harness only — not used in training or the game loop.
    /// </summary>
    public sealed class Systems_FallImpactFx : MonoBehaviour
    {
        private const float FALL_HEAD_FRACTION = 0.45f;
        private const float REARM_HEAD_FRACTION = 0.8f;

        [SerializeField] private ParticleSystem _dust;
        [SerializeField] private AudioSource _audioSource;
        [SerializeField] private AudioClip[] _thudClips;

        public event System.Action<Vector3> FighterFell;

        private IContestFighter[] _fighters;
        private float[] _startHeadHeights;
        private bool[] _armed;
        private Systems_DramaCamera _dramaCamera;

        private void Start()
        {
            // The thud is a body sound, so it rides the foley bus and ducks
            // under a callout with the rest of them.
            Systems_AudioMix.Route(_audioSource, AudioBus.Foley);
            _dramaCamera = FindFirstObjectByType<Systems_DramaCamera>();
        }

        /// <summary>
        /// Bound late rather than in Start: a MuJoCo contestant reports a head
        /// height of 0 until its simulator has bound on its first physics tick,
        /// and a standing height captured then would arm this on a body that
        /// is perfectly upright.
        /// </summary>
        private bool Bind()
        {
            if (_fighters != null) { return true; }
            IContestFighter[] fighters = Systems_Contestants.FindAll();
            if (fighters.Length == 0) { return false; }
            for (int index = 0; index < fighters.Length; index++)
            {
                if (!fighters[index].IsReady) { return false; }
            }
            _fighters = fighters;
            _startHeadHeights = new float[fighters.Length];
            _armed = new bool[fighters.Length];
            for (int index = 0; index < fighters.Length; index++)
            {
                _startHeadHeights[index] = fighters[index].HeadHeightAboveGround;
                _armed[index] = true;
            }
            return true;
        }

        private void FixedUpdate()
        {
            if (!Bind())
            {
                return;
            }
            for (int index = 0; index < _fighters.Length; index++)
            {
                IContestFighter fighter = _fighters[index];
                float headFraction = Systems_Contestants.HeadFraction(fighter, _startHeadHeights[index]);
                if (_armed[index] && (headFraction < FALL_HEAD_FRACTION || fighter.ReportsDown))
                {
                    _armed[index] = false;
                    PlayImpact(fighter.WorldPosition);
                }
                else if (!_armed[index] && headFraction > REARM_HEAD_FRACTION && !fighter.ReportsDown)
                {
                    _armed[index] = true;
                }
            }
        }

        private void PlayImpact(Vector3 position)
        {
            if (_dust != null)
            {
                _dust.transform.position = position;
                _dust.Play();
            }
            if (_audioSource != null && _thudClips != null && _thudClips.Length > 0)
            {
                _audioSource.transform.position = position;
                _audioSource.PlayOneShot(_thudClips[Random.Range(0, _thudClips.Length)]);
            }
            if (_dramaCamera != null)
            {
                _dramaCamera.ShakeAt(position, 0.6f);
            }
            FighterFell?.Invoke(position);
        }
    }
}
