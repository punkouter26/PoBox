using UnityEngine;
using UnityEngine.Audio;
using PoBox.Fx;
using PoBox.Sim;

namespace PoBox.Audio
{
    /// <summary>
    /// The sound of the fight, placed where it happens, on three buses: hits, crowd and ring.
    ///
    /// A punch is built from layers, each a 3D one-shot at the contact point and each set from what was
    /// measured. The slap of leather is always there, louder with the impulse and pitched up with the speed
    /// the glove arrived at. Under it, from about 6 N s, the thud of the body that took it, which grows
    /// faster than the slap and is pitched down as it gets heavier, so a hard shot is lower and longer and
    /// not just louder. A punch stopped on the gloves has a duller slap and no thud. And a punch harder than
    /// these two usually land draws a gasp from the crowd, a fraction of a second after it.
    ///
    /// The crowd is a looping bed whose level rides the excitement reading, with a swell laid over it on a
    /// big moment and a groan on a knockdown. So that a heavy punch is heard through it, the crowd bus is
    /// ducked for the quarter second the punch lasts and comes back over the next half.
    ///
    /// The buses are groups of an AudioMixer when there is one (Assets/Audio/PoBox.mixer, where their levels
    /// can be set by hand), and otherwise plain gains here. <see cref="AudioMeter"/> on the listener
    /// measures and limits the sum.
    /// </summary>
    public class AudioDirector : MonoBehaviour
    {
        public Bout bout;
        public AudioListener listener;

        [Header("Hits")]
        [Tooltip("The slap of a glove: a clean punch.")]
        public AudioClip[] punchLight = new AudioClip[0];
        [Tooltip("The slap of a hard one.")]
        public AudioClip[] punchHeavy = new AudioClip[0];
        [Tooltip("A punch stopped on the gloves or arms.")]
        public AudioClip[] punchBlocked = new AudioClip[0];
        [Tooltip("The thud of the body that took it, laid under the slap.")]
        public AudioClip[] bodyThud = new AudioClip[0];
        public AudioClip whoosh;
        public AudioClip bodyFall;
        public AudioClip[] footsteps = new AudioClip[0];
        [Tooltip("Impulse at which the heavy samples take over, N s.")]
        public float heavyImpulse = 14f;
        [Tooltip("Impulse at which the thud comes in under the slap, N s.")]
        public float thudImpulse = 6f;

        [Header("Ring")]
        public AudioClip bell;
        public AudioClip countBeep;
        public AudioClip sting;

        [Header("Crowd")]
        public AudioClip crowdBed;
        public AudioClip crowdSwell;
        public AudioClip crowdGroan;
        public AudioClip crowdApplause;
        [Tooltip("The short intake of breath when a punch lands harder than usual.")]
        public AudioClip crowdGasp;
        [Range(0f, 1f)] public float crowdQuiet = 0.22f;
        [Range(0f, 1f)] public float crowdLoud = 0.85f;

        [Header("Mix")]
        [Tooltip("Groups Hits, Crowd and Ring, each with its volume exposed (HitsVolume, CrowdVolume, RingVolume). Empty: the gains below are used.")]
        public AudioMixer mixer;
        [Range(0f, 1f)] public float master = 0.9f;
        [Range(0f, 1.5f)] public float hitsGain = 0.45f, crowdGain = 0.55f, ringGain = 0.6f;
        [Tooltip("How far the crowd is pulled down under a heavy punch, dB.")]
        public float duckDb = 7f;
        public int voices = 14;

        public static bool Muted;
        public int ActiveVoices { get; private set; }
        /// <summary>The finished mix's peak, dB below full scale. -100 with no meter on the listener.</summary>
        public float PeakDb => AudioMeter.Instance != null ? AudioMeter.Instance.PeakDb : -100f;
        /// <summary>How far the crowd is ducked at this moment, dB.</summary>
        public float DuckNow { get; private set; }

        AudioSource[] _pool;
        AudioSource _bed, _crowdSource, _ringSource;
        int _next, _lastCount;
        float _lastSwell = -99f, _duck, _gaspAt = -1f, _gaspVolume, _lastStep = -99f, _bedLevel;
        bool _mixed;

        void Awake()
        {
            AudioMixerGroup hits = Group("Hits"), crowd = Group("Crowd"), ring = Group("Ring");
            _mixed = mixer != null && hits != null && crowd != null && ring != null;

            _pool = new AudioSource[Mathf.Max(6, voices)];
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
                s.outputAudioMixerGroup = hits;
                _pool[i] = s;
            }

            _bed = Source2D(crowd);
            _bed.clip = crowdBed;
            _bed.loop = true;
            _bed.volume = 0f;
            _crowdSource = Source2D(crowd);
            _ringSource = Source2D(ring);
        }

        AudioMixerGroup Group(string name)
        {
            if (mixer == null) return null;
            AudioMixerGroup[] found = mixer.FindMatchingGroups(name);
            return found != null && found.Length > 0 ? found[0] : null;
        }

        AudioSource Source2D(AudioMixerGroup group)
        {
            AudioSource s = gameObject.AddComponent<AudioSource>();
            s.spatialBlend = 0f;
            s.playOnAwake = false;
            s.outputAudioMixerGroup = group;
            return s;
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
            SimBus.FootStep += OnFootStep;
            SimBus.PhaseChanged += OnPhase;
        }

        void OnDisable()
        {
            SimBus.Hit -= OnHit;
            SimBus.PunchThrown -= OnPunch;
            SimBus.Knockdown -= OnKnockdown;
            SimBus.FloorImpact -= OnFloor;
            SimBus.FootStep -= OnFootStep;
            SimBus.PhaseChanged -= OnPhase;
        }

        // ------------------------------------------------------------ events

        void OnPunch(Fighter f, PunchType type, int hand)
        {
            BodyPart arm = hand < 0 ? f.forearmL : f.forearmR;
            if (arm != null) PlayAt(whoosh, arm.transform.position, 0.16f, Random.Range(1.05f, 1.35f));
        }

        void OnHit(HitEvent e)
        {
            float k = Mathf.Clamp01(e.impulse / 22f);
            // Pitch from how fast the glove arrived: a quick one cracks, a slow heavy one thumps.
            float snap = Mathf.Lerp(0.92f, 1.14f, Mathf.Clamp01(e.gloveSpeed / 9f)) * Random.Range(0.96f, 1.04f);
            // The head rings a little higher than the body.
            float where = e.zone == PartKind.Head ? 1.04f : 0.94f;

            if (!e.clean)
            {
                PlayAt(Pick(punchBlocked), e.point, Mathf.Lerp(0.3f, 0.8f, k), snap * 0.95f);
                return;
            }

            bool heavy = e.impulse >= heavyImpulse;
            PlayAt(Pick(heavy ? punchHeavy : punchLight), e.point, Mathf.Lerp(0.35f, 0.95f, k), snap * where);
            if (e.impulse >= thudImpulse)
            {
                // The thud grows faster than the slap, and sinks as it grows.
                float t = Mathf.InverseLerp(thudImpulse, 22f, e.impulse);
                PlayAt(Pick(bodyThud), e.point, Mathf.Lerp(0.2f, 1f, t * t), Mathf.Lerp(1.05f, 0.78f, t) * where);
            }

            // How far above what these two usually land: that is what the room reacts to.
            Excitement ex = Excitement.Instance;
            float usual = ex != null ? ex.UsualImpulse : 12f;
            float surprise = Mathf.Clamp01((e.impulse - usual * 1.1f) / Mathf.Max(1f, usual * 0.5f));
            if (heavy || surprise > 0.2f) _duck = Mathf.Max(_duck, Mathf.Lerp(0.35f, 1f, Mathf.Max(surprise, k)));
            if (surprise > 0.15f)
            {
                // The gasp comes after the punch, not with it.
                _gaspAt = Time.unscaledTime + 0.12f;
                _gaspVolume = Mathf.Max(_gaspVolume, Mathf.Lerp(0.25f, 0.8f, surprise));
            }
            if (surprise > 0.6f && Time.unscaledTime - _lastSwell > 3f)
            {
                _lastSwell = Time.unscaledTime;
                PlayCrowd(crowdSwell, Mathf.Lerp(0.35f, 0.8f, surprise));
            }
        }

        void OnKnockdown(Fighter f, HitEvent cause)
        {
            _lastSwell = Time.unscaledTime;
            _duck = 1f;
            PlayCrowd(crowdSwell, 0.95f);
            PlayCrowd(crowdGroan, 0.5f);
        }

        void OnFloor(Vector3 point, float impulse)
        {
            PlayAt(bodyFall, point, Mathf.Lerp(0.4f, 1f, Mathf.Clamp01(impulse / 80f)), Random.Range(0.9f, 1.05f));
        }

        void OnFootStep(Vector3 point, float speed)
        {
            // Boots on canvas: quiet, and only a step that was really taken, not a shuffle.
            if (speed < 0.5f || Time.unscaledTime - _lastStep < 0.08f) return;
            _lastStep = Time.unscaledTime;
            PlayAt(Pick(footsteps), point, Mathf.Lerp(0.08f, 0.3f, Mathf.Clamp01(speed / 3f)), Random.Range(0.85f, 1.1f));
        }

        void OnPhase(BoutPhase from, BoutPhase to)
        {
            if (to == BoutPhase.Fight && from == BoutPhase.Intro) PlayRing(bell, 0.8f);
            else if (to == BoutPhase.RoundBreak) PlayRing(bell, 0.8f);
            else if (to == BoutPhase.WalkOn) PlayRing(sting, 0.5f);
            else if (to == BoutPhase.Results)
            {
                PlayRing(bell, 0.7f);
                PlayRing(sting, 0.45f);
                PlayCrowd(crowdApplause, 0.6f);
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
            _bedLevel = Mathf.MoveTowards(_bedLevel, target, dt * 0.6f);

            // The duck: down at once, back over half a second.
            _duck = Mathf.MoveTowards(_duck, 0f, dt * 2f);
            DuckNow = duckDb * Mathf.Clamp01(_duck);
            float crowdLinear = Mathf.Pow(10f, -DuckNow / 20f);
            if (_mixed)
            {
                mixer.SetFloat("HitsVolume", Db(hitsGain));
                mixer.SetFloat("CrowdVolume", Db(crowdGain) - DuckNow);
                mixer.SetFloat("RingVolume", Db(ringGain));
                _bed.volume = _bedLevel;
            }
            else
            {
                // No mixer: the same thing with the sources' own volumes.
                _bed.volume = _bedLevel * crowdGain * crowdLinear;
                _crowdSource.volume = crowdGain * crowdLinear;
                _ringSource.volume = ringGain;
            }

            if (_gaspAt > 0f && Time.unscaledTime >= _gaspAt)
            {
                PlayCrowd(crowdGasp, _gaspVolume);
                _gaspAt = -1f;
                _gaspVolume = 0f;
            }

            if (bout != null && bout.Phase == BoutPhase.Count && bout.Count != _lastCount && bout.Count > 0)
                PlayRing(countBeep, 0.5f);
            _lastCount = bout != null ? bout.Count : 0;

            int active = 0;
            for (int i = 0; i < _pool.Length; i++) if (_pool[i].isPlaying) active++;
            ActiveVoices = active;
        }

        static float Db(float linear) => linear > 1e-4f ? 20f * Mathf.Log10(linear) : -80f;

        // ------------------------------------------------------------ helpers

        static AudioClip Pick(AudioClip[] set) => set != null && set.Length > 0 ? set[Random.Range(0, set.Length)] : null;

        void PlayAt(AudioClip clip, Vector3 position, float volume, float pitch)
        {
            if (clip == null || _pool == null) return;
            AudioSource s = _pool[_next];
            _next = (_next + 1) % _pool.Length;
            s.transform.position = position;
            s.clip = clip;
            s.volume = volume * (_mixed ? 1f : hitsGain);
            s.pitch = pitch;
            s.Play();
        }

        void PlayCrowd(AudioClip clip, float volume)
        {
            if (clip != null && _crowdSource != null) _crowdSource.PlayOneShot(clip, volume);
        }

        void PlayRing(AudioClip clip, float volume)
        {
            if (clip != null && _ringSource != null) _ringSource.PlayOneShot(clip, volume);
        }
    }
}
