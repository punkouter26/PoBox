using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Impact feedback for one pooled thrown cube: a 3D-positioned thwack and
    /// a spark burst when it hits something hard enough. Clips and the shared
    /// spark system are handed over by Systems_CubeThrower at pool build.
    /// Test-scene harness only.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class Systems_CubeImpactFx : MonoBehaviour
    {
        private const float MIN_IMPACT_SPEED = 3f;
        private const float COOLDOWN_SECONDS = 0.2f;
        private const float MAX_VOLUME_SPEED = 20f;

        private AudioSource _audioSource;
        private AudioClip[] _clips;
        private ParticleSystem _sharedSpark;
        private System.Random _random;
        private float _cooldownRemaining;

        public void Initialize(AudioClip[] clips, ParticleSystem sharedSpark, int seed)
        {
            _audioSource = GetComponent<AudioSource>();
            _audioSource.playOnAwake = false;
            _audioSource.spatialBlend = 1f;
            _clips = clips;
            _sharedSpark = sharedSpark;
            _random = new System.Random(seed);
        }

        private void FixedUpdate()
        {
            if (_cooldownRemaining > 0f)
            {
                _cooldownRemaining -= Time.fixedDeltaTime;
            }
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (_clips == null || _cooldownRemaining > 0f)
            {
                return;
            }
            float speed = collision.relativeVelocity.magnitude;
            if (speed < MIN_IMPACT_SPEED)
            {
                return;
            }
            _cooldownRemaining = COOLDOWN_SECONDS;

            float volume = Mathf.Clamp01(speed / MAX_VOLUME_SPEED);
            if (_clips.Length > 0)
            {
                _audioSource.PlayOneShot(_clips[_random.Next(_clips.Length)], volume);
            }
            if (_sharedSpark != null && collision.contactCount > 0)
            {
                _sharedSpark.transform.position = collision.GetContact(0).point;
                _sharedSpark.Play();
            }
        }
    }
}
