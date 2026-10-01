using System;
using System.Collections.Generic;
using UnityEngine;
using PoBox.Fx;
using PoBox.Sim;

namespace PoBox.Broadcast
{
    /// <summary>
    /// Instant replay and the highlight reel.
    ///
    /// It records poses, not pictures: sixty times a simulated second, where every link of both fighters is
    /// and how hard its joint is working, into a ring a few seconds long. A hit worth seeing again copies
    /// the stretch around it out of the ring into a clip, and the best few of a bout are kept. Playing one
    /// back freezes the physics, hides the fighters and poses two puppets that look exactly like them,
    /// which means a replay can be watched from any camera at any speed and scrubbed by hand, and costs
    /// about 30 kB a second instead of a video encoder.
    /// </summary>
    public class ReplaySystem : MonoBehaviour
    {
        public static ReplaySystem Instance { get; private set; }

        public Bout bout;
        public FighterSkin redLive, blueLive, redPuppet, bluePuppet;

        [Header("Recording")]
        public float bufferSeconds = 12f;
        public int rate = 60;
        [Tooltip("Seconds kept before and after the moment.")]
        public float before = 1.6f;
        public float after = 1.2f;
        [Tooltip("Impulse x zone weight a clean hit needs to be worth a clip.")]
        public float clipThreshold = 9f;
        public int keep = 3;

        [Header("Playback")]
        [Range(0.1f, 1f)] public float playbackSpeed = 0.3f;

        public class Clip
        {
            public int frames, bones;
            public float[] time;
            public Vector3[] pos;
            public Quaternion[] rot;
            public float[] stress;
            public float score;
            public string caption;
            public Vector3 focus;
            public float eventTime;     // simulation seconds, same clock as time[]
            public float boutClock;     // Bout.Clock at the moment, for the graph marker
            public int round;
            public float Duration => frames > 1 ? time[frames - 1] - time[0] : 0f;
            /// <summary>Where in the clip the moment itself is, 0..1.</summary>
            public float EventAt => Duration > 0f ? Mathf.Clamp01((eventTime - time[0]) / Duration) : 0f;
        }

        struct Pending
        {
            public float eventTime, score, boutClock;
            public string caption;
            public Vector3 focus;
            public int round;
        }

        public bool Playing { get; private set; }
        public bool Paused { get; set; }
        public Clip Current { get; private set; }
        public int ClipIndex { get; private set; }
        public int ClipCount => _playlist.Count;
        public IReadOnlyList<Clip> Highlights => _best;
        /// <summary>Playhead of the current clip, 0..1.</summary>
        public float Position => Current != null && Current.Duration > 0f ? Mathf.Clamp01(_t / Current.Duration) : 0f;

        int _bones, _capacity, _head, _count;
        float[] _time, _stress;
        Vector3[] _pos;
        Quaternion[] _rot;
        float _simTime, _accumulator, _t;
        bool _loop;
        readonly List<Pending> _pending = new List<Pending>();
        readonly List<Clip> _best = new List<Clip>();
        readonly List<Clip> _playlist = new List<Clip>();
        Clip _roundBest;

        void Awake()
        {
            Instance = this;
            _bones = (redLive != null ? redLive.parts.Length : 0) + (blueLive != null ? blueLive.parts.Length : 0);
            _capacity = Mathf.Max(60, Mathf.CeilToInt(bufferSeconds * rate));
            _time = new float[_capacity];
            _pos = new Vector3[_capacity * _bones];
            _rot = new Quaternion[_capacity * _bones];
            _stress = new float[_capacity * _bones];
        }

        void Start() => ShowPuppets(false);

        void OnEnable()
        {
            SimBus.Hit += OnHit;
            SimBus.Knockdown += OnKnockdown;
            SimBus.PhaseChanged += OnPhase;
        }

        void OnDisable()
        {
            SimBus.Hit -= OnHit;
            SimBus.Knockdown -= OnKnockdown;
            SimBus.PhaseChanged -= OnPhase;
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------ recording

        void FixedUpdate()
        {
            if (!Bout.SimRunning || _bones == 0) return;
            float dt = Time.fixedDeltaTime;
            _simTime += dt;
            _accumulator += dt;
            float step = 1f / rate;
            if (_accumulator < step) return;
            _accumulator -= step;

            int at = _head * _bones;
            _time[_head] = _simTime;
            at = Write(redLive, at);
            Write(blueLive, at);
            _head = (_head + 1) % _capacity;
            if (_count < _capacity) _count++;

            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (_simTime < _pending[i].eventTime + after) continue;
                Cut(_pending[i]);
                _pending.RemoveAt(i);
            }
        }

        int Write(FighterSkin skin, int at)
        {
            for (int i = 0; i < skin.parts.Length; i++, at++)
            {
                Transform b = skin.parts[i].bone;
                _pos[at] = b.position;
                _rot[at] = b.rotation;
                _stress[at] = skin.StressOf(i);
            }
            return at;
        }

        void OnHit(HitEvent e)
        {
            if (!e.clean) return;
            float score = e.impulse * Fighter.ZoneWeight(e.zone);
            if (score < clipThreshold) return;
            string hand = e.hand < 0 ? "LEFT" : "RIGHT";
            Queue(score, $"{e.attacker.displayName} · {hand} {e.punch.ToString().ToUpperInvariant()} · {e.impulse:0} N·s", e.point);
        }

        void OnKnockdown(Fighter f, HitEvent cause)
        {
            float score = 60f + cause.impulse;
            Vector3 focus = cause.attacker != null ? cause.point : f.pelvis.transform.position + Vector3.up * 0.6f;
            Queue(score, $"KNOCKDOWN · {f.displayName} DOWN" + (cause.attacker != null ? $" · {cause.impulse:0} N·s" : ""), focus);
        }

        void Queue(float score, string caption, Vector3 focus)
        {
            // Two moments inside one clip's length are one moment: keep the better of them.
            for (int i = 0; i < _pending.Count; i++)
            {
                if (_simTime - _pending[i].eventTime > before) continue;
                if (score > _pending[i].score)
                {
                    Pending p = _pending[i];
                    p.score = score; p.caption = caption; p.focus = focus; p.eventTime = _simTime; p.boutClock = Bout.Clock;
                    _pending[i] = p;
                }
                return;
            }
            _pending.Add(new Pending
            {
                eventTime = _simTime, score = score, caption = caption, focus = focus,
                boutClock = Bout.Clock, round = bout != null ? bout.Round : 1,
            });
        }

        void Cut(Pending p)
        {
            if (_count < 2) return;
            float from = p.eventTime - before, to = p.eventTime + after;
            int oldest = (_head - _count + _capacity) % _capacity;

            int first = -1, last = -1;
            for (int i = 0; i < _count; i++)
            {
                float t = _time[(oldest + i) % _capacity];
                if (t < from) continue;
                if (t > to) break;
                if (first < 0) first = i;
                last = i;
            }
            if (first < 0 || last - first < 4) return;

            int n = last - first + 1;
            var clip = new Clip
            {
                frames = n, bones = _bones,
                time = new float[n], pos = new Vector3[n * _bones], rot = new Quaternion[n * _bones], stress = new float[n * _bones],
                score = p.score, caption = p.caption, focus = p.focus, eventTime = p.eventTime, boutClock = p.boutClock, round = p.round,
            };
            for (int i = 0; i < n; i++)
            {
                int src = (oldest + first + i) % _capacity;
                clip.time[i] = _time[src];
                Array.Copy(_pos, src * _bones, clip.pos, i * _bones, _bones);
                Array.Copy(_rot, src * _bones, clip.rot, i * _bones, _bones);
                Array.Copy(_stress, src * _bones, clip.stress, i * _bones, _bones);
            }

            if (_roundBest == null || clip.score > _roundBest.score) _roundBest = clip;
            _best.Add(clip);
            _best.Sort((a, b) => b.score.CompareTo(a.score));
            if (_best.Count > keep) _best.RemoveRange(keep, _best.Count - keep);
        }

        void FlushPending()
        {
            foreach (Pending p in _pending) Cut(p);
            _pending.Clear();
        }

        // ------------------------------------------------------------ phases

        void OnPhase(BoutPhase from, BoutPhase to)
        {
            switch (to)
            {
                case BoutPhase.RoundBreak:
                    FlushPending();
                    if (_roundBest != null) Play(new List<Clip> { _roundBest }, false);
                    break;
                case BoutPhase.Results:
                    FlushPending();
                    if (_best.Count > 0)
                    {
                        var reel = new List<Clip>(_best);
                        reel.Sort((a, b) => a.eventTime.CompareTo(b.eventTime));
                        Play(reel, true);
                    }
                    break;
                case BoutPhase.Intro:
                    Stop();
                    _roundBest = null;
                    _pending.Clear();
                    if (bout != null && bout.Round == 1) _best.Clear();
                    // The ring only has to be continuous within a round.
                    _count = 0; _head = 0; _accumulator = 0f;
                    break;
            }
        }

        // ------------------------------------------------------------ playback

        void Play(List<Clip> clips, bool loop)
        {
            _playlist.Clear();
            _playlist.AddRange(clips);
            if (_playlist.Count == 0) return;
            _loop = loop;
            ClipIndex = 0;
            Current = _playlist[0];
            _t = 0f;
            Paused = false;
            Playing = true;
            if (bout != null) bout.replayBusy = true;
            ShowPuppets(true);
            Apply();
        }

        public void Stop()
        {
            bool was = Playing;
            Playing = false;
            Current = null;
            _playlist.Clear();
            if (bout != null) bout.replayBusy = false;
            if (was || (redPuppet != null && redPuppet.Visible)) ShowPuppets(false);
        }

        /// <summary>Plays the bout's reel again from the top. The REPLAY button on the results card.</summary>
        public void Replay()
        {
            if (_best.Count == 0) return;
            var reel = new List<Clip>(_best);
            reel.Sort((a, b) => a.eventTime.CompareTo(b.eventTime));
            Play(reel, true);
        }

        /// <summary>Puts the playhead somewhere by hand and holds it there.</summary>
        public void Scrub(float position01)
        {
            if (!Playing || Current == null) return;
            Paused = true;
            _t = Mathf.Clamp01(position01) * Current.Duration;
            Apply();
        }

        public void Step(int direction)
        {
            if (!Playing || _playlist.Count == 0) return;
            ClipIndex = (ClipIndex + direction + _playlist.Count) % _playlist.Count;
            Current = _playlist[ClipIndex];
            _t = 0f;
            Paused = false;
            Apply();
        }

        void ShowPuppets(bool show)
        {
            if (redPuppet != null) redPuppet.SetVisible(show);
            if (bluePuppet != null) bluePuppet.SetVisible(show);
            if (redLive != null) redLive.SetVisible(!show);
            if (blueLive != null) blueLive.SetVisible(!show);
        }

        void Update()
        {
            if (!Playing || Current == null) return;
            if (!Paused)
            {
                _t += Time.unscaledDeltaTime * playbackSpeed;
                // Half a second of held last frame, in clip time, before moving on.
                if (_t > Current.Duration + 0.15f)
                {
                    if (ClipIndex + 1 < _playlist.Count) { ClipIndex++; Current = _playlist[ClipIndex]; _t = 0f; }
                    else if (_loop) { ClipIndex = 0; Current = _playlist[0]; _t = 0f; }
                    else { Stop(); return; }
                }
            }
            Apply();
        }

        void Apply()
        {
            Clip c = Current;
            if (c == null || c.frames < 2) return;
            float t = c.time[0] + Mathf.Clamp(_t, 0f, c.Duration);

            int i = 0;
            while (i < c.frames - 2 && c.time[i + 1] < t) i++;
            float span = c.time[i + 1] - c.time[i];
            float k = span > 1e-5f ? Mathf.Clamp01((t - c.time[i]) / span) : 0f;

            int a = i * c.bones, b = (i + 1) * c.bones;
            int at = Pose(redPuppet, c, a, b, k, 0);
            Pose(bluePuppet, c, a, b, k, at);
        }

        static int Pose(FighterSkin puppet, Clip c, int a, int b, float k, int offset)
        {
            if (puppet == null) return offset;
            for (int i = 0; i < puppet.parts.Length; i++, offset++)
            {
                Transform bone = puppet.parts[i].bone;
                bone.SetPositionAndRotation(
                    Vector3.LerpUnclamped(c.pos[a + offset], c.pos[b + offset], k),
                    Quaternion.SlerpUnclamped(c.rot[a + offset], c.rot[b + offset], k));
                puppet.SetPart(i, Mathf.Lerp(c.stress[a + offset], c.stress[b + offset], k), 0f);
            }
            return offset;
        }
    }
}
