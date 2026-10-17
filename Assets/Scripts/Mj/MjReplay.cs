using System;
using Mujoco;
using UnityEngine;

namespace PoBox.Mj
{
    /// <summary>
    /// The instant replay. Every control step (50 a second) the whole world's joint positions (mjData.qpos: every
    /// boxer and every cube) go into a ring buffer holding the last <see cref="seconds"/>. A knockdown is marked
    /// (<see cref="Mark"/>), and <see cref="after"/> seconds later the buffer is kept as the clip, so by the time the
    /// count is over and the result is up, the punch that did it has not been written over.
    ///
    /// <see cref="Play"/> keeps a copy of the live mjData (mj_copyData: positions, velocities, actuator states,
    /// controls, time, the solver's warm start, everything), stops the ring, and then each frame writes a pose of
    /// the clip into qpos, runs mj_kinematics and moves the scene's objects to it, at half speed; the skinned meshes
    /// follow their bodies as they always do. At the end (or <see cref="Stop"/>) the copy goes back over mjData, so
    /// the live state is exactly what it was. Nothing is stepped while it plays: no contact, no punch, no fall.
    ///
    /// Memory: 8 s x 50 Hz x nq doubles, twice (the buffer and the clip). Eight boxers (28 each) and eight cubes
    /// (7 each) are an nq of 280: about 1.8 MB.
    /// </summary>
    public unsafe class MjReplay : MonoBehaviour
    {
        /// <summary>Control steps a second (the trainer's 0.005 s step, four to a decision): the rate it records at.</summary>
        public const float Rate = 50f;

        [Tooltip("Seconds kept.")]
        public float seconds = 8f;
        [Tooltip("Seconds after a knockdown that the clip ends.")]
        public float after = 2f;
        [Tooltip("Speed it plays at.")]
        public float speed = 0.5f;

        public static MjReplay Instance { get; private set; }
        public static bool Playing => Instance != null && Instance._playing;
        /// <summary>There is something to show: a marked knockdown, or the last seconds recorded.</summary>
        public bool HasClip => _clipCount > 1 || _count > 1;

        MjRing _ring;
        MujocoLib.mjModel_* _m;
        MujocoLib.mjData_* _d, _saved;
        int _nq, _frames, _head, _count, _clipCount, _pending = -1;
        double[] _live = new double[0], _clip = new double[0];
        float _at;
        bool _playing, _wasRunning;

        void Awake()
        {
            Instance = this;
            _ring = GetComponent<MjRing>();
            if (_ring == null) _ring = FindAnyObjectByType<MjRing>();
            MjScene.Instance.postInitEvent += OnModel;
        }

        void OnEnable()
        {
            if (_ring != null) _ring.ControlStep += Record;
        }

        void OnDisable()
        {
            if (_ring != null) _ring.ControlStep -= Record;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (MjScene.InstanceExists) MjScene.Instance.postInitEvent -= OnModel;
            if (_saved != null) MujocoLib.mj_deleteData(_saved);
            _saved = null;
        }

        void OnModel(object sender, MjStepArgs a)
        {
            _playing = false;
            if (_saved != null) MujocoLib.mj_deleteData(_saved);
            _saved = null;
            _m = a.model; _d = a.data;
            if (_m == null) return;
            _nq = (int)_m->nq;
            _frames = Mathf.Max(2, Mathf.RoundToInt(seconds * Rate));
            _live = new double[_frames * _nq];
            _clip = new double[_frames * _nq];
            _saved = MujocoLib.mj_makeData(_m);
            Clear();
        }

        /// <summary>A new bout: nothing recorded is of it.</summary>
        public void Clear()
        {
            _head = 0; _count = 0; _clipCount = 0; _pending = -1;
        }

        /// <summary>A knockdown: the clip is what has been recorded up to <see cref="after"/> seconds from now.</summary>
        public void Mark()
        {
            if (_d == null) return;
            _pending = Mathf.Max(1, Mathf.RoundToInt(after * Rate));
        }

        void Record()
        {
            if (_playing || _d == null || _frames == 0) return;
            double* q = _d->qpos;
            int at = _head * _nq;
            for (int i = 0; i < _nq; i++) _live[at + i] = q[i];
            _head = (_head + 1) % _frames;
            _count = Math.Min(_count + 1, _frames);
            if (_pending > 0 && --_pending == 0) Keep();
        }

        /// <summary>The buffer, oldest first, becomes the clip.</summary>
        void Keep()
        {
            for (int i = 0; i < _count; i++)
            {
                int from = (_head - _count + i + _frames) % _frames;
                Array.Copy(_live, from * _nq, _clip, i * _nq, _nq);
            }
            _clipCount = _count;
            _pending = -1;
        }

        /// <summary>Plays the clip (the last knockdown's, or the last seconds recorded). False when there is nothing to play.</summary>
        public bool Play()
        {
            if (_playing || _d == null || _saved == null || _ring == null) return false;
            if (_pending > 0 || _clipCount < 2) Keep();
            if (_clipCount < 2) return false;
            MujocoLib.mj_copyData(_saved, _m, _d);
            _wasRunning = _ring.running;
            _ring.running = false;
            MjScene.Instance.enabled = false;       // the ring does this in its own Update; not a step is to be taken before then
            _at = 0f;
            _playing = true;
            Pose();
            return true;
        }

        /// <summary>Ends the replay with the live state put back exactly as it was.</summary>
        public void Stop()
        {
            if (!_playing) return;
            _playing = false;
            MujocoLib.mj_copyData(_d, _m, _saved);
            MjScene.Instance.SyncUnityToMjState();
            _ring.running = _wasRunning;
        }

        void Update()
        {
            if (!_playing) return;
            _at += Time.unscaledDeltaTime * speed * Rate;
            if (_at >= _clipCount - 1) { Stop(); return; }
            Pose();
        }

        /// <summary>
        /// The pose between two recorded ones. A free joint's quaternion is blended as four numbers, which
        /// mj_kinematics normalises: 20 ms apart, the two are never far enough apart for that to show.
        /// </summary>
        void Pose()
        {
            int i = Mathf.Min((int)_at, _clipCount - 2);
            double t = _at - i;
            int a = i * _nq, b = a + _nq;
            double* q = _d->qpos;
            for (int k = 0; k < _nq; k++) q[k] = _clip[a + k] + (_clip[b + k] - _clip[a + k]) * t;
            MujocoLib.mj_kinematics(_m, _d);
            MjScene.Instance.SyncUnityToMjState();
        }
    }
}
