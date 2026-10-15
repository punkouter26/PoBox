using System;
using UnityEngine;
using PoBox.Sim;

namespace PoBox.Audio
{
    /// <summary>
    /// The commentary read aloud by the phone's own voice: Android's TextToSpeech, on the device, no network.
    ///
    /// Only the lines worth saying (the hits called, the knockdowns, the counts) and only during the fight:
    /// the round numbers, the introductions and the winner are the teeth announcer's, who has them recorded.
    /// One line waits while another is said, the newest and most important; a line that has waited longer
    /// than <see cref="maxLag"/> is dropped, so what is said is never more than that behind the fight, and a
    /// knockdown cuts in over whatever is being said. The crowd is ducked while it talks
    /// (<see cref="AudioDirector.Speaking"/>).
    ///
    /// Nothing happens in the editor, when <see cref="AudioDirector.Muted"/>, or when <see cref="Enabled"/> is
    /// off. The Settings page sets <see cref="Enabled"/> (saved by HudView).
    /// </summary>
    public class SpeechCommentary : MonoBehaviour
    {
        public static bool Enabled = true;

        [Tooltip("Lines of this priority and up are read out: 1 the hits called, 2 the counts, 3 a knockdown.")]
        public int minPriority = 1;
        [Tooltip("A line still waiting after this long, seconds, is dropped.")]
        public float maxLag = 1f;
        public float speechRate = 1.15f;

        const int QueueFlush = 0;
        string _pending;
        int _pendingPriority;
        float _pendingAt, _spokeAt = -9f;
        bool _talking;

        AndroidJavaObject _activity;
        volatile AndroidJavaObject _tts;
        volatile bool _ready;
        IntPtr _speak, _isSpeaking, _id;
        readonly jvalue[] _args = new jvalue[4], _none = new jvalue[0];

        class Init : AndroidJavaProxy
        {
            readonly SpeechCommentary _owner;
            public Init(SpeechCommentary owner) : base("android.speech.tts.TextToSpeech$OnInitListener") => _owner = owner;
            void onInit(int status) => _owner._ready = status == 0;
        }

        void Start()
        {
            if (Application.platform != RuntimePlatform.Android) return;
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                    _activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                // Made on Android's own thread, which its answer comes back on.
                _activity.Call("runOnUiThread", new AndroidJavaRunnable(() =>
                    _tts = new AndroidJavaObject("android.speech.tts.TextToSpeech", _activity, new Init(this))));
            }
            catch (Exception e) { Debug.LogWarning("SpeechCommentary: no TextToSpeech: " + e.Message); }
        }

        void OnDestroy()
        {
            AudioDirector.Speaking = false;
            if (_tts == null) return;
            _tts.Call("shutdown");
            _tts.Dispose();
            if (_id != IntPtr.Zero) AndroidJNI.DeleteGlobalRef(_id);
        }

        void OnEnable() => SimBus.Line += OnLine;

        void OnDisable()
        {
            SimBus.Line -= OnLine;
            AudioDirector.Speaking = false;
        }

        void OnLine(string text, int priority)
        {
            if (!_ready || priority < minPriority || string.IsNullOrEmpty(text)) return;
            // The announcer's: "ROUND 2" is raised just after the fight phase begins.
            Bout bout = Bout.Instance;
            if (bout == null || (bout.Phase != BoutPhase.Fight && bout.Phase != BoutPhase.Count) || text.StartsWith("ROUND ")) return;
            if (_pending != null && priority < _pendingPriority) return;
            _pending = text;
            _pendingPriority = priority;
            _pendingAt = Time.unscaledTime;
        }

        void Update()
        {
            if (!_ready) return;
            if (_speak == IntPtr.Zero) Bind();
            bool quiet = !Enabled || AudioDirector.Muted;
            if (_talking)
            {
                // The engine takes a moment to start: until then it says it is not speaking.
                _talking = Time.unscaledTime - _spokeAt < 0.4f || AndroidJNI.CallBooleanMethod(_tts.GetRawObject(), _isSpeaking, _none);
                if (_talking && quiet) { _tts.Call<int>("stop"); _talking = false; }
            }
            if (_pending != null && (quiet || Time.unscaledTime - _pendingAt > maxLag)) _pending = null;
            if (_pending != null && (!_talking || _pendingPriority >= 3))
            {
                Speak(_pending);
                _pending = null;
            }
            AudioDirector.Speaking = _talking;
        }

        void Bind()
        {
            IntPtr cls = AndroidJNI.GetObjectClass(_tts.GetRawObject());
            _speak = AndroidJNI.GetMethodID(cls, "speak", "(Ljava/lang/CharSequence;ILandroid/os/Bundle;Ljava/lang/String;)I");
            _isSpeaking = AndroidJNI.GetMethodID(cls, "isSpeaking", "()Z");
            AndroidJNI.DeleteLocalRef(cls);
            IntPtr id = AndroidJNI.NewStringUTF("pobox");
            _id = AndroidJNI.NewGlobalRef(id);
            AndroidJNI.DeleteLocalRef(id);
            _tts.Call<int>("setSpeechRate", speechRate);
        }

        void Speak(string line)
        {
            // Said, not read: no numbers after the dot, no shouting.
            int dot = line.IndexOf('·');
            if (dot > 0) line = line.Substring(0, dot);
            if (line.StartsWith("DOWN! ")) line = line.Substring(6);
            line = line.Trim().ToLowerInvariant();
            if (line.Length == 0) return;
            IntPtr text = AndroidJNI.NewStringUTF(line);
            _args[0].l = text;
            _args[1].i = QueueFlush;        // what is being said is old by now: this replaces it
            _args[2].l = IntPtr.Zero;
            _args[3].l = _id;
            AndroidJNI.CallIntMethod(_tts.GetRawObject(), _speak, _args);
            AndroidJNI.DeleteLocalRef(text);
            IntPtr e = AndroidJNI.ExceptionOccurred();
            if (e != IntPtr.Zero) { AndroidJNI.ExceptionClear(); AndroidJNI.DeleteLocalRef(e); }
            _spokeAt = Time.unscaledTime;
            _talking = true;
        }
    }
}
