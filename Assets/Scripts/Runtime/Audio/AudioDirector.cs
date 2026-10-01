using UnityEngine;
using PoBox.Fx;
using PoBox.Sim;

namespace PoBox.Audio
{
    /// <summary>
    /// The sound of the fight, placed where it happens.
    ///
    /// Every hit is a 3D one-shot at the contact point, picked by weight (light, heavy, on the gloves) with
    /// its level and pitch set from the measured impulse, so a hard shot is lower and louder without a
    /// separate recording for each strength. The crowd is a looping bed whose level rides the excitement
    /// reading, with a swell laid over it on a big moment and a groan on a knockdown. In slow motion a
    /// low-pass on the listener closes down and every voice drops in pitch with the time scale, which is
    /// what makes slow motion sound like slow motion.
    /// </summary>
    public class AudioDirector : MonoBehaviour
    {
        public Bout bout;
        public AudioListener listener;

        [Header("Hits")]
        public AudioClip[] punchLight = new AudioClip[0];
        public AudioClip[] punchHeavy = new AudioClip[0];
        public AudioClip[] punchBlocked = new AudioClip[0];
        public AudioClip whoosh;
        public AudioClip bodyFall;
        [Tooltip("Impulse at which the heavy samples take over, N s.")]
        public float heavyImpulse = 12f;

        [Header("Ring")]
        public AudioClip bell;
        public AudioClip countBeep;
        public AudioClip sting;

        [Header("Crowd")]
        public AudioClip crowdBed;
        public AudioClip crowdSwell;
        public AudioClip crowdGroan;
        public AudioClip crowdApplause;
        [Range(0f, 1f)] public float crowdQuiet = 0.22f;
        [Range(0f, 1f)] public float crowdLoud = 0.85f;

        [Header("Mix")]
        [Range(0f, 1f)] public float master = 0.9f;
        public int voices = 10;

        public static bool Muted;
        public int ActiveVoices { get; private set; }

        AudioSource[] _pool;
        AudioSource _bed, _stingSource;
        AudioLowPassFilter _lowPass;
        int _next, _lastCount;
        float _lastSwell = -99f;

        void Awake()
        {
            _pool = new AudioSource[Mathf.Max(4, voices)];
            for (int i = 0; i < _pool.Length; i++)
            {
                var go = new GameObject("Voice " + i);
                go.transform.SetParent(transform, false);
                AudioSource s = go.AddComponent<AudioSource>();
                s.playOnAwake = false;
                s.spatialBlend = 1f;
                s.rolloffMode = AudioRolloffMode.Linear;
                s.minDistance = 3f;
                s.maxDistance = 30f;
                s.dopplerLevel = 0f;
                _pool[i] = s;
            }

            _bed = gameObject.AddComponent<AudioSource>();
            _bed.clip = crowdBed;
            _bed.loop = true;
            _bed.spatialBlend = 0f;
            _bed.volume = 0f;
            _bed.playOnAwake = false;

            _stingSource = gameObject.AddComponent<AudioSource>();
            _stingSource.spatialBlend = 0f;
            _stingSource.playOnAwake = false;

            if (listener != null)
            {
                _lowPass = listener.GetComponent<AudioLowPassFilter>();
                if (_lowPass == null) _lowPass = listener.gameObject.AddComponent<AudioLowPassFilter>();
                _lowPass.cutoffFrequency = 22000f;
            }
        }

        void Start()
        {
            if (_bed.clip != null) _bed.Play();
        }

        void OnEnable()
        {
            SimBus.Hit += OnHit;
            SimBus.PunchThrown += OnPunch;
            SimBus.Knockdown += OnKnockdown;
            SimBus.FloorImpact += OnFloor;
            SimBus.PhaseChanged += OnPhase;
        }

        void OnDisable()
        {
            SimBus.Hit -= OnHit;
            SimBus.PunchThrown -= OnPunch;
            SimBus.Knockdown -= OnKnockdown;
            SimBus.FloorImpact -= OnFloor;
            SimBus.PhaseChanged -= OnPhase;
        }

        // ------------------------------------------------------------ events

        void OnPunch(Fighter f, PunchType type, int hand)
        {
            BodyPart arm = hand < 0 ? f.forearmL : f.forearmR;
            if (arm != null) PlayAt(whoosh, arm.transform.position, 0.22f, Random.Range(1.05f, 1.35f));
        }

        void OnHit(HitEvent e)
        {
            float k = Mathf.Clamp01(e.impulse / 25f);
            AudioClip[] set = !e.clean ? punchBlocked : e.impulse >= heavyImpulse ? punchHeavy : punchLight;
            PlayAt(Pick(set), e.point, Mathf.Lerp(0.35f, 1f, k), Mathf.Lerp(1.12f, 0.86f, k) * Random.Range(0.95f, 1.05f));

            if (e.clean && e.impulse >= heavyImpulse && Time.unscaledTime - _lastSwell > 3f)
            {
                _lastSwell = Time.unscaledTime;
                Play2D(crowdSwell, Mathf.Lerp(0.35f, 0.8f, k));
            }
        }

        void OnKnockdown(Fighter f, HitEvent cause)
        {
            _lastSwell = Time.unscaledTime;
            Play2D(crowdSwell, 0.95f);
            Play2D(crowdGroan, 0.5f);
        }

        void OnFloor(Vector3 point, float impulse)
        {
            PlayAt(bodyFall, point, Mathf.Lerp(0.4f, 1f, Mathf.Clamp01(impulse / 80f)), Random.Range(0.9f, 1.05f));
        }

        void OnPhase(BoutPhase from, BoutPhase to)
        {
            if (to == BoutPhase.Fight && from == BoutPhase.Intro) Play2D(bell, 0.8f);
            else if (to == BoutPhase.RoundBreak) Play2D(bell, 0.8f);
            else if (to == BoutPhase.Results)
            {
                Play2D(bell, 0.8f);
                Play2D(sting, 0.6f);
                Play2D(crowdApplause, 0.8f);
            }
        }

        // ------------------------------------------------------------ per frame

        void Update()
        {
            AudioListener.volume = Muted ? 0f : master;
            float dt = Time.unscaledDeltaTime;

            // The bed follows the excitement squared: a quiet hall most of the time, a roar when it earns one.
            float e = Excitement.Value;
            float target = Mathf.Lerp(crowdQuiet, crowdLoud, Mathf.Clamp01(e * e + ArenaMood.Burst * 0.5f));
            _bed.volume = Mathf.MoveTowards(_bed.volume, target, dt * 0.6f);

            float scale = Mathf.Clamp(Time.timeScale, 0.2f, 1f);
            if (_lowPass != null)
            {
                float cutoff = Mathf.Lerp(700f, 22000f, Mathf.InverseLerp(0.25f, 1f, scale));
                _lowPass.cutoffFrequency = Mathf.Lerp(_lowPass.cutoffFrequency, cutoff, Mathf.Clamp01(dt * 12f));
            }
            _bed.pitch = Mathf.Lerp(0.8f, 1f, Mathf.InverseLerp(0.25f, 1f, scale));

            if (bout != null && bout.Phase == BoutPhase.Count && bout.Count != _lastCount && bout.Count > 0)
                Play2D(countBeep, 0.5f);
            _lastCount = bout != null ? bout.Count : 0;

            int active = 0;
            foreach (AudioSource s in _pool) if (s.isPlaying) active++;
            ActiveVoices = active;
        }

        // ------------------------------------------------------------ helpers

        static AudioClip Pick(AudioClip[] set) => set != null && set.Length > 0 ? set[Random.Range(0, set.Length)] : null;

        void PlayAt(AudioClip clip, Vector3 position, float volume, float pitch)
        {
            if (clip == null || _pool == null) return;
            AudioSource s = _pool[_next];
            _next = (_next + 1) % _pool.Length;
            s.transform.position = position;
            s.clip = clip;
            s.volume = volume;
            // Slow motion drops the pitch with it; a punch at full pitch over a slowed picture sounds dubbed.
            s.pitch = pitch * Mathf.Lerp(0.6f, 1f, Mathf.InverseLerp(0.25f, 1f, Time.timeScale));
            s.Play();
        }

        void Play2D(AudioClip clip, float volume)
        {
            if (clip != null && _stingSource != null) _stingSource.PlayOneShot(clip, volume);
        }
    }
}
