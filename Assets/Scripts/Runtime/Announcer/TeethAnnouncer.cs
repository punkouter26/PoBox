using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using PoBox.Sim;

namespace PoBox.Announcer
{
    /// <summary>
    /// The ring announcer: the owner's teeth (two dental scans, upper and lower jaw), on a cable the way
    /// the announcer's microphone is.
    ///
    /// It is seen twice in a bout and not otherwise (the owner's ruling of 2026-10-02): it is lowered to the
    /// middle of the ring, at about the height of a head, to introduce the two boxers standing in their
    /// corners, and again to name the winner; when it has spoken it is hauled back up into the lights and
    /// is not drawn at all. Its voice is still heard for the rest of what a ring announcer says, the number
    /// of the round and "down", with nothing in the picture saying it. Its jaw follows the loudness of its
    /// own voice; a line it has no recording of it mouths, a syllable at a time.
    ///
    /// It listens to <see cref="SimBus"/> and reads <see cref="Bout"/>; nothing else knows it is there.
    /// </summary>
    public class TeethAnnouncer : MonoBehaviour
    {
        [Header("Parts")]
        [Tooltip("What rides up and down the cable: the mouth hangs from it.")]
        public Transform mount;
        public Transform upperJaw;
        public Transform lowerJaw;
        [Tooltip("A cylinder two units tall, stretched each frame from the mount to the ceiling.")]
        public Transform cable;
        public AudioSource voice;

        [Header("Where it hangs, metres above the canvas")]
        [Tooltip("No longer used: it does not hang over the ring between announcements.")]
        public float restHeight = 3.3f;
        [Tooltip("Where it speaks from when it names the winner: over the boxers' heads, who may be standing in the middle.")]
        public float speakHeight = 2.45f;
        [Tooltip("Where it speaks from when it introduces the boxers, who are in their corners: about the height of a head.")]
        public float introHeight = 1.7f;
        [Tooltip("Up in the lights, out of every camera's picture: where it waits, not drawn.")]
        public float awayHeight = 8f;
        public float ceilingHeight = 9f;
        [Tooltip("No longer used: see arriveSeconds.")]
        public float travelSeconds = 0.7f;
        [Tooltip("Seconds to come down from the lights, and to go back up.")]
        public float arriveSeconds = 1.1f;
        [Tooltip("Seconds it stays down after its last word.")]
        public float lingerSeconds = 0.9f;
        [Tooltip("The same, after naming the winner: long enough to be seen with them.")]
        public float winnerLingerSeconds = 2.5f;
        [Tooltip("Turns to face whichever camera is live, so the picture always has its front.")]
        public bool faceCamera = true;

        [Header("Jaw")]
        [Tooltip("How far the lower jaw drops at its widest, degrees.")]
        public float openDegrees = 24f;
        [Tooltip("Share of that the upper jaw tips back by: a jaw on a cable has nothing to hold its top still.")]
        [Range(0f, 1f)] public float upperShare = 0.3f;
        public float syllablesPerSecond = 5.5f;
        [Tooltip("Seconds of mouthing per character of a line it does not say aloud.")]
        public float secondsPerCharacter = 0.055f;
        [Tooltip("Loudness of its own voice that opens the jaw fully.")]
        public float loudnessForOpen = 0.16f;

        [Header("Voice")]
        public bool speakAloud = true;
        [Tooltip("Recording names, lower-case letters and digits only: red_corner, round_1, winner, and one per boxer.")]
        public List<string> keys = new List<string>();
        public List<AudioClip> clips = new List<AudioClip>();

        /// <summary>How open the mouth is, 0 to 1.</summary>
        public float Open { get; private set; }
        public bool Speaking => _speech != null;
        /// <summary>In the picture: on its way down, speaking, or on its way back up.</summary>
        public bool Shown { get; private set; }

        readonly Dictionary<string, AudioClip> _byKey = new Dictionary<string, AudioClip>();
        readonly Dictionary<AudioClip, Vector2> _sound = new Dictionary<AudioClip, Vector2>();   // where the words start and end, seconds
        readonly float[] _window = new float[256];
        readonly List<string> _queue = new List<string>();
        Coroutine _speech;
        float _mouthUntil, _height, _lowUntil, _phase, _downTo, _linger = 0.9f;
        bool _onStage;                 // what it is saying now is said in the picture
        Renderer[] _renderers;
        Quaternion _upperRest, _lowerRest;

        void Awake()
        {
            for (int i = 0; i < keys.Count && i < clips.Count; i++)
                if (clips[i] != null) _byKey[keys[i]] = clips[i];
            if (upperJaw != null) _upperRest = upperJaw.localRotation;
            if (lowerJaw != null) _lowerRest = lowerJaw.localRotation;
            _height = awayHeight;
            _downTo = introHeight;
            _renderers = GetComponentsInChildren<Renderer>(true);
            Show(false);
        }

        void Show(bool on)
        {
            Shown = on;
            if (_renderers == null) return;
            foreach (Renderer r in _renderers)
                if (r != null) r.enabled = on;
        }

        /// <summary>The announcer in the scene, if there is one: the cameras frame the introductions round it.</summary>
        public static TeethAnnouncer Current { get; private set; }

        /// <summary>Where it speaks from when it is in the picture, in the world.</summary>
        public Vector3 StagePoint => transform.TransformPoint(new Vector3(0f, _downTo, 0f));
        /// <summary>Down, or on its way down, to say something in the picture: the cameras frame it then.</summary>
        public bool OnStage => Time.unscaledTime < _lowUntil || (_onStage && _speech != null);

        void OnEnable()
        {
            Current = this;
            SimBus.Line += OnLine;
            SimBus.PhaseChanged += OnPhase;
        }

        void OnDisable()
        {
            if (Current == this) Current = null;
            SimBus.Line -= OnLine;
            SimBus.PhaseChanged -= OnPhase;
            _speech = null;
        }

        // ------------------------------------------------------------ what it says

        void OnPhase(BoutPhase from, BoutPhase to)
        {
            Bout bout = Bout.Instance;
            if (bout == null) return;
            switch (to)
            {
                case BoutPhase.WalkOn:
                    Present(introHeight, "red_corner", NameKey(bout.red), "blue_corner", NameKey(bout.blue));
                    break;
                case BoutPhase.Intro:
                    // A bout that starts with no walk-on still has its boxers introduced.
                    if (bout.Round == 1 && from != BoutPhase.WalkOn && from != BoutPhase.RoundBreak)
                        Present(introHeight, "red_corner", NameKey(bout.red), "blue_corner", NameKey(bout.blue));
                    break;
                case BoutPhase.Fight:
                    // After whatever it is still saying, not over it. Heard, not seen: it is on its way up.
                    if (from == BoutPhase.Intro) Then($"round_{bout.Round}");
                    break;
                case BoutPhase.Count:
                    Say("down");
                    break;
                case BoutPhase.Results:
                {
                    // Over the middle of the ring again: at the height of a head if the middle is empty, over
                    // their heads if a boxer is standing in it.
                    bool crowded = false;
                    foreach (Fighter f in new[] { bout.red, bout.blue })
                    {
                        if (f == null || f.pelvis == null) continue;
                        Vector3 off = f.pelvis.transform.position - transform.position;
                        off.y = 0f;
                        crowded |= off.magnitude < 1.3f;
                    }
                    float height = crowded ? speakHeight : introHeight;
                    if (bout.Winner != null) Present(height, "winner", NameKey(bout.Winner));
                    else Present(height, "draw");
                    _linger = winnerLingerSeconds;
                    break;
                }
            }
        }

        void OnLine(string text, int priority)
        {
            // The commentary's own lines were mouthed while it hung over the ring all bout. It is only in the
            // picture for its own announcements now, so there is nothing to mouth them with.
        }

        /// <summary>Comes down to this height and says these in the picture: the introductions and the winner.</summary>
        public void Present(float height, params string[] phrase)
        {
            _downTo = height;
            _linger = lingerSeconds;
            _lowUntil = Time.unscaledTime + lingerSeconds;
            Show(true);
            Begin(true, phrase);
        }

        /// <summary>A boxer's recording is named after it in lower-case letters and digits: "LIL MATT" is lilmatt.</summary>
        public static string NameKey(Fighter f)
        {
            if (f == null || string.IsNullOrEmpty(f.displayName)) return "";
            var sb = new System.Text.StringBuilder();
            foreach (char c in f.displayName.ToLowerInvariant())
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>Says these recordings one after another, in place of whatever it was saying.</summary>
        public void Say(params string[] phrase) => Begin(false, phrase);

        void Begin(bool onStage, string[] phrase)
        {
            if (_speech != null) StopCoroutine(_speech);
            if (voice != null) voice.Stop();
            _onStage = onStage;
            _queue.Clear();
            _queue.AddRange(phrase);
            _speech = StartCoroutine(Speak());
        }

        /// <summary>Says these once it has finished what it is saying now.</summary>
        public void Then(params string[] phrase)
        {
            // What is added to an announcement made in the picture is not part of it: the round is called as
            // the announcer goes back up.
            foreach (string key in phrase) _queue.Add(OffStage + key);
            if (_speech == null) _speech = StartCoroutine(Speak());
        }

        const string OffStage = "~";

        IEnumerator Speak()
        {
            while (_queue.Count > 0)
            {
                string key = _queue[0];
                _queue.RemoveAt(0);
                if (key.StartsWith(OffStage)) { key = key.Substring(OffStage.Length); _onStage = false; }
                // It stays down for as long as it has something to say in the picture, and a moment more.
                if (_onStage) _lowUntil = Time.unscaledTime + _linger;
                if (!speakAloud || voice == null || !_byKey.TryGetValue(key, out AudioClip clip))
                {
                    // No recording of this one (a new boxer, say): it is mouthed, for about as long as a word takes.
                    _mouthUntil = Time.unscaledTime + 0.8f;
                    yield return new WaitForSecondsRealtime(0.8f);
                    continue;
                }
                Vector2 span = SoundSpan(clip);
                voice.clip = clip;
                voice.time = span.x;
                voice.Play();
                float end = Time.unscaledTime + (span.y - span.x);
                while (Time.unscaledTime < end)
                {
                    if (_onStage) _lowUntil = Time.unscaledTime + _linger;
                    yield return null;
                }
                voice.Stop();
            }
            _onStage = false;
            _speech = null;
        }

        /// <summary>Where the words are in a recording: a synthesised phrase has silence at both ends, and
        /// two of them in a row would have a pause no announcer leaves.</summary>
        Vector2 SoundSpan(AudioClip clip)
        {
            if (_sound.TryGetValue(clip, out Vector2 span)) return span;
            span = new Vector2(0f, clip.length);
            var data = new float[clip.samples * clip.channels];
            if (clip.GetData(data, 0))
            {
                int step = clip.channels, first = -1, last = -1;
                for (int i = 0; i < data.Length; i += step)
                {
                    if (Mathf.Abs(data[i]) < 0.02f) continue;
                    if (first < 0) first = i;
                    last = i;
                }
                if (first >= 0)
                {
                    float perSecond = clip.frequency * clip.channels;
                    span = new Vector2(Mathf.Max(0f, first / perSecond - 0.03f), Mathf.Min(clip.length, last / perSecond + 0.12f));
                }
            }
            _sound[clip] = span;
            return span;
        }

        // ------------------------------------------------------------ how it moves

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            float now = Time.unscaledTime;

            // The jaw: its own voice if it is speaking, a syllable at a time if it is only mouthing a line.
            float want = 0f;
            if (voice != null && voice.isPlaying)
            {
                voice.GetOutputData(_window, 0);
                float sum = 0f;
                for (int i = 0; i < _window.Length; i++) sum += _window[i] * _window[i];
                want = Mathf.Clamp01(Mathf.Sqrt(sum / _window.Length) / Mathf.Max(1e-4f, loudnessForOpen));
            }
            else if (now < _mouthUntil)
            {
                _phase += dt * syllablesPerSecond;
                // Each syllable opens and shuts; no two are the same size.
                float size = 0.45f + 0.55f * Mathf.PerlinNoise(_phase * 0.37f, 3.1f);
                want = size * Mathf.Abs(Mathf.Sin(_phase * Mathf.PI));
            }
            Open = Mathf.MoveTowards(Open, want, dt * (want > Open ? 14f : 9f));
            if (lowerJaw != null) lowerJaw.localRotation = _lowerRest * Quaternion.Euler(Open * openDegrees, 0f, 0f);
            if (upperJaw != null) upperJaw.localRotation = _upperRest * Quaternion.Euler(-Open * openDegrees * upperShare, 0f, 0f);

            if (mount == null) return;
            // Down for an announcement made in the picture, back up into the lights when it is over, and not
            // drawn once it is there.
            // (Asked of the speech itself as well as of the clock: the first frame after a scene loads takes
            // seconds, and a time set before it has passed by the frame after.)
            bool down = now < _lowUntil || (_onStage && _speech != null);
            float target = down ? _downTo : awayHeight;
            float speed = Mathf.Abs(awayHeight - introHeight) / Mathf.Max(0.05f, arriveSeconds);
            _height = Mathf.MoveTowards(_height, target, speed * Mathf.Min(dt, 0.05f));
            bool inPicture = down || _height < awayHeight - 0.01f;
            if (inPicture != Shown) Show(inPicture);
            if (!Shown) return;
            // It hangs, so it is never quite still.
            float bob = 0.025f * Mathf.Sin(now * 1.3f);
            mount.localPosition = new Vector3(0f, _height + bob, 0f);

            Quaternion facing = mount.rotation;
            Camera cam = faceCamera ? Camera.main : null;
            if (cam != null)
            {
                Vector3 to = cam.transform.position - mount.position;
                float flat = new Vector2(to.x, to.z).magnitude;
                if (flat > 0.5f)
                {
                    float yaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
                    float pitch = Mathf.Clamp(-Mathf.Atan2(to.y, flat) * Mathf.Rad2Deg, -25f, 25f);
                    facing = Quaternion.Euler(pitch, yaw, 0f);
                }
            }
            Quaternion sway = Quaternion.Euler(0f, 0f, 2.5f * Mathf.Sin(now * 0.9f));
            mount.rotation = Quaternion.Slerp(mount.rotation, facing * sway, 1f - Mathf.Exp(-dt * 4f));

            if (cable != null)
            {
                float length = Mathf.Max(0.01f, ceilingHeight - mount.localPosition.y);
                cable.localPosition = new Vector3(0f, mount.localPosition.y + 0.5f * length, 0f);
                Vector3 s = cable.localScale;
                cable.localScale = new Vector3(s.x, 0.5f * length, s.z);
            }
        }
    }
}
