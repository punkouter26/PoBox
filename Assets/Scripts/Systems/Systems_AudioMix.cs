using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.SceneManagement;

namespace PoBox
{
    /// <summary>Which voice a sound belongs to. One bus, one reason to be turned down.</summary>
    public enum AudioBus
    {
        /// <summary>Ambience and crowd swells. Loud, constant, and the first thing to duck.</summary>
        Crowd = 0,
        /// <summary>Bodies: thuds, scuffs, footsteps. Positioned in the ring.</summary>
        Foley = 1,
        /// <summary>The bell and the play-by-play. Never ducked — it is what the others duck FOR.</summary>
        Announcer = 2
    }

    /// <summary>
    /// The mix. Before this there was none: eight clips played through five
    /// systems that each chose their own volume, every one of them straight
    /// into the listener at full level, with no bus, no ducking and no way for
    /// a player to turn any of it down. The bell landed on top of the crowd
    /// swell it was meant to cut through, and the phone's volume keys were the
    /// only control that existed.
    ///
    /// WHAT IT DOES. Three busses (<see cref="AudioBus"/>) under a master gain,
    /// and ducking: while the announcer is speaking, crowd and foley drop and
    /// come back. Gains persist in PlayerPrefs, so the setting survives the app
    /// rather than the scene.
    ///
    /// TWO BACKENDS, ONE API. When <see cref="Systems_SpectatorKit"/> carries an
    /// AudioMixer, sources are routed to its groups and the gains drive its
    /// exposed parameters — real DSP busses. When it does not, the same gains
    /// are applied to AudioSource.volume directly. The fallback is not a stub:
    /// it delivers busses, ducking and a master volume on its own, so a project
    /// whose mixer asset has not been built is quieter and tidier rather than
    /// broken. Every consumer calls <see cref="Register"/> and neither knows nor
    /// cares which backend answered.
    ///
    /// A REGISTERED SOURCE KEEPS ITS AUTHORED LEVEL. The base volume is captured
    /// at registration and every gain multiplies it, so the crowd bed stays the
    /// quarter-volume bed <see cref="Systems_CrowdAudio"/> asked for and a
    /// PlayOneShot that passes its own volume still scales underneath. Taking a
    /// source over without capturing its level would silently reset five
    /// systems' hand-tuned balance to 1.0.
    ///
    /// NOT A SINGLETON (project rule). One is installed per scene load and dies
    /// with its scene, exactly like <see cref="Systems_DeviceHud"/>. Consumers
    /// find it with <see cref="Find"/> rather than through a static instance,
    /// and cope with null: no contest depends on the mix to be refereed
    /// correctly.
    /// </summary>
    [DefaultExecutionOrder(-50)]   // before the spectator systems register their voices
    public sealed class Systems_AudioMix : MonoBehaviour
    {
        /// <summary>PlayerPrefs key for the master gain. Bus keys append the bus name.</summary>
        private const string PREF_MASTER = "PoBox.Audio.Master";
        private const string PREF_BUS = "PoBox.Audio.Bus.";

        /// <summary>
        /// Exposed parameter names on the mixer, when there is one. They are the
        /// contract between <see cref="PoBox.Editor.Editor_AudioMixer"/> and
        /// this: the tool exposes exactly these and nothing else reads them.
        /// </summary>
        public const string PARAM_MASTER = "MasterVolume";
        public const string PARAM_CROWD = "CrowdVolume";
        public const string PARAM_FOLEY = "FoleyVolume";
        public const string PARAM_ANNOUNCER = "AnnouncerVolume";

        /// <summary>How far crowd and foley drop while the announcer talks.</summary>
        private const float DUCK_DEPTH = 0.35f;

        /// <summary>
        /// Duck attack and release, in seconds. Attack is fast enough to be
        /// under the first syllable and slow enough not to click; release is
        /// slower, because a crowd that snaps back the instant a callout ends
        /// reads as a mistake rather than as a mix.
        /// </summary>
        private const float DUCK_ATTACK = 0.08f;
        private const float DUCK_RELEASE = 0.45f;

        /// <summary>A source under the mix, with the level it was authored at.</summary>
        private struct Voice
        {
            public AudioSource source;
            public AudioBus bus;
            public float baseVolume;
        }

        private readonly List<Voice> _voices = new();
        private readonly float[] _busGain = { 1f, 1f, 1f };
        private AudioMixer _mixer;
        private AudioMixerGroup[] _groups;
        private float _master = 1f;
        private float _duck = 1f;
        private float _duckHold;

        /// <summary>
        /// Installs one per scene, after the scene's own objects exist.
        ///
        /// Same mechanism and the same reason as <see cref="Systems_DeviceHud"/>:
        /// the three shipping scenes are hand-authored and committed, so a mix
        /// object dragged into each by hand is three chances for them to drift
        /// apart — and the training scenes, which are regenerated wholesale,
        /// would lose theirs on the next regeneration without anyone noticing.
        ///
        /// AND, LIKE THE HUD, IT HAS TO RE-INSTALL ITSELF. This is the one thing
        /// the first version of this class got wrong, in exactly the way
        /// Systems_DeviceHud's own comment records having got it wrong before:
        /// RuntimeInitializeOnLoadMethod fires ONCE PER APPLICATION START, not
        /// once per scene load. The object it made was not DontDestroyOnLoad (no
        /// singletons, project rule), so it belonged to SCN_MENU and was
        /// destroyed the moment the player pressed START — and nothing ever made
        /// another one. The result was a mix that existed only on the menu, where
        /// there is no crowd to duck and no bell to duck it for: every contest
        /// ran with no busses, no master volume and no ducking, and the persisted
        /// volume settings drove nothing at all. Nothing threw, because
        /// <see cref="Route"/> is null-safe by design — a missing mix is a
        /// supported configuration, which is precisely why its absence was
        /// invisible.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            Spawn();
            // Static subscription, per-scene objects: the handler outlives every
            // mix it makes, and each scene load needs its own.
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => Spawn();

        private static void Spawn()
        {
            // An additive load, or a re-entered scene, must not get a second mix:
            // two would both write Time-scaled gains to the same sources and
            // fight over the mixer's parameters.
            if (FindFirstObjectByType<Systems_AudioMix>() != null) { return; }
            var host = new GameObject("AudioMix");
            host.AddComponent<Systems_AudioMix>();
        }

        /// <summary>
        /// The mix for this scene, or null. Callers must handle null — see the
        /// class remarks.
        /// </summary>
        public static Systems_AudioMix Find()
        {
            return FindFirstObjectByType<Systems_AudioMix>();
        }

        /// <summary>
        /// Convenience for the systems that build a source and immediately want
        /// it on a bus. Null-safe in both directions: no mix in the scene
        /// returns the source untouched, which is what makes every call site a
        /// one-liner rather than a null check.
        /// </summary>
        public static AudioSource Route(AudioSource source, AudioBus bus)
        {
            Systems_AudioMix mix = Find();
            return mix != null ? mix.Register(source, bus) : source;
        }

        /// <summary>
        /// Puts <paramref name="source"/> on <paramref name="bus"/> and returns
        /// it, so a caller can register inline where it builds the source.
        /// Registering the same source twice replaces the first entry rather
        /// than stacking two gains on it.
        /// </summary>
        public AudioSource Register(AudioSource source, AudioBus bus)
        {
            if (source == null)
            {
                return null;
            }
            var voice = new Voice { source = source, bus = bus, baseVolume = source.volume };
            for (int index = 0; index < _voices.Count; index++)
            {
                if (_voices[index].source == source)
                {
                    // Keep the ORIGINAL base level. A re-registration after the
                    // gains have been applied would otherwise capture the ducked
                    // volume as the authored one and ratchet the source quieter
                    // every time it happened.
                    voice.baseVolume = _voices[index].baseVolume;
                    _voices[index] = voice;
                    ApplyTo(voice);
                    return source;
                }
            }
            _voices.Add(voice);
            ApplyTo(voice);
            return source;
        }

        /// <summary>
        /// Ducks crowd and foley for <paramref name="seconds"/>. Called by the
        /// announcer when a callout starts. Repeated calls extend the hold
        /// rather than restarting it, so a run of callouts holds one duck
        /// instead of pumping once per line.
        /// </summary>
        public void DuckFor(float seconds)
        {
            _duckHold = Mathf.Max(_duckHold, Mathf.Max(0f, seconds));
        }

        /// <summary>Master gain, 0..1. Persisted.</summary>
        public float Master
        {
            get => _master;
            set
            {
                _master = Mathf.Clamp01(value);
                PlayerPrefs.SetFloat(PREF_MASTER, _master);
                ApplyAll();
            }
        }

        /// <summary>Gain for one bus, 0..1. Persisted.</summary>
        public float GetBus(AudioBus bus)
        {
            return _busGain[(int)bus];
        }

        public void SetBus(AudioBus bus, float value)
        {
            _busGain[(int)bus] = Mathf.Clamp01(value);
            PlayerPrefs.SetFloat(PREF_BUS + bus, _busGain[(int)bus]);
            ApplyAll();
        }

        private void Awake()
        {
            _master = PlayerPrefs.GetFloat(PREF_MASTER, 1f);
            for (int busIndex = 0; busIndex < _busGain.Length; busIndex++)
            {
                _busGain[busIndex] = PlayerPrefs.GetFloat(PREF_BUS + (AudioBus)busIndex, 1f);
            }

            Systems_SpectatorKit kit = Systems_SpectatorKit.Load();
            if (kit != null && kit.mixer != null)
            {
                _mixer = kit.mixer;
                _groups = new[] { kit.crowdGroup, kit.foleyGroup, kit.announcerGroup };
            }
            ApplyAll();
        }

        /// <summary>
        /// Unscaled time throughout. The duck has to keep releasing through the
        /// round countdown's timeScale 0 and the knockout slow-mo, like every
        /// other presentation system here — a mix stuck at its attack level
        /// because the game paused is the one failure a duck must not have.
        /// </summary>
        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            float target = 1f;
            if (_duckHold > 0f)
            {
                _duckHold -= dt;
                target = DUCK_DEPTH;
            }

            float rate = target < _duck ? DUCK_ATTACK : DUCK_RELEASE;
            float next = Mathf.MoveTowards(_duck, target, dt / Mathf.Max(0.001f, rate));
            if (!Mathf.Approximately(next, _duck))
            {
                _duck = next;
                ApplyAll();
            }
        }

        private void ApplyAll()
        {
            if (_mixer != null)
            {
                _mixer.SetFloat(PARAM_MASTER, ToDecibels(_master));
                _mixer.SetFloat(PARAM_CROWD, ToDecibels(_busGain[(int)AudioBus.Crowd] * _duck));
                _mixer.SetFloat(PARAM_FOLEY, ToDecibels(_busGain[(int)AudioBus.Foley] * _duck));
                _mixer.SetFloat(PARAM_ANNOUNCER, ToDecibels(_busGain[(int)AudioBus.Announcer]));
            }
            for (int index = _voices.Count - 1; index >= 0; index--)
            {
                if (_voices[index].source == null)
                {
                    // Voices belong to fighters and to spectator systems, both of
                    // which are destroyed between contests. Swap-remove rather
                    // than RemoveAt: order here means nothing, and this runs
                    // every time the duck moves.
                    _voices[index] = _voices[_voices.Count - 1];
                    _voices.RemoveAt(_voices.Count - 1);
                    continue;
                }
                ApplyTo(_voices[index]);
            }
        }

        private void ApplyTo(Voice voice)
        {
            if (voice.source == null)
            {
                return;
            }
            if (_mixer != null && _groups != null)
            {
                int groupIndex = (int)voice.bus;
                if (groupIndex < _groups.Length && _groups[groupIndex] != null)
                {
                    voice.source.outputAudioMixerGroup = _groups[groupIndex];
                    // The mixer owns the gain from here; the source keeps the
                    // level it was authored with.
                    voice.source.volume = voice.baseVolume;
                    return;
                }
            }
            float duck = voice.bus == AudioBus.Announcer ? 1f : _duck;
            voice.source.volume = voice.baseVolume * _busGain[(int)voice.bus] * duck * _master;
        }

        /// <summary>
        /// Linear 0..1 to the decibels a mixer wants. -80 dB is the mixer's own
        /// floor and reads as silence; the curve is squared because a linear
        /// slider on a decibel scale spends most of its travel in a range
        /// nobody can hear a difference across.
        /// </summary>
        private static float ToDecibels(float linear)
        {
            return linear <= 0.0001f ? -80f : Mathf.Log10(linear * linear) * 10f;
        }
    }
}
