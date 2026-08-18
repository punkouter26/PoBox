using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Presentation feedback for falls in the contest test scene: when a
    /// fighter's head drops below the fall threshold, plays a dust burst and a
    /// soft body thud at the impact point, and raises FighterFell for other
    /// presentation systems (crowd, camera). Re-arms when the fighter is
    /// reset upright. Self-discovers contestants at Start.
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

        private Systems_FighterRig[] _rigs;
        private float[] _startHeadHeights;
        private bool[] _armed;

        private void Start()
        {
            _rigs = FindObjectsByType<Systems_FighterRig>(FindObjectsSortMode.InstanceID);
            _startHeadHeights = new float[_rigs.Length];
            _armed = new bool[_rigs.Length];
            for (int rigIndex = 0; rigIndex < _rigs.Length; rigIndex++)
            {
                _startHeadHeights[rigIndex] = _rigs[rigIndex].Head.position.y;
                _armed[rigIndex] = true;
            }
        }

        private void FixedUpdate()
        {
            if (_rigs == null)
            {
                return;
            }
            for (int rigIndex = 0; rigIndex < _rigs.Length; rigIndex++)
            {
                Systems_FighterRig rig = _rigs[rigIndex];
                float headFraction = rig.Head.position.y / _startHeadHeights[rigIndex];
                if (_armed[rigIndex] && headFraction < FALL_HEAD_FRACTION)
                {
                    _armed[rigIndex] = false;
                    PlayImpact(rig.Pelvis.position);
                }
                else if (!_armed[rigIndex] && headFraction > REARM_HEAD_FRACTION)
                {
                    _armed[rigIndex] = true;
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
            FighterFell?.Invoke(position);
        }
    }
}
