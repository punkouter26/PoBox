using System;
using UnityEngine;
using PoBox.Sim;

namespace PoBox.Audio
{
    /// <summary>
    /// The phone buzzes with the fight: a tap for a punch landing, as long and as strong as the punch is
    /// against what these two usually land; a long full one for a knockdown; a thump for a body on the canvas.
    /// Never more than about eight a second (a knockdown always gets through).
    ///
    /// Android's VibrationEffect.createOneShot (API 26; the project's minimum is 31), called through raw JNI
    /// with its method ids looked up once, so a buzz allocates nothing. Handheld.Vibrate is the fallback, and
    /// its use here is also what has Unity put the VIBRATE permission in the built manifest. Nothing happens
    /// in the editor. The Settings page sets <see cref="Enabled"/> (saved by HudView).
    /// </summary>
    public class Haptics : MonoBehaviour
    {
        public static bool Enabled = true;

        [Tooltip("Fewest seconds between two buzzes.")]
        public float minInterval = 0.125f;

        float _last = -9f;
        bool _android;
        AndroidJavaObject _vibrator;
        IntPtr _effectClass, _createOneShot, _vibrate;
        readonly jvalue[] _oneShot = new jvalue[2], _arg = new jvalue[1];

        void Awake()
        {
            _android = Application.platform == RuntimePlatform.Android;
            if (!_android) return;
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                    _vibrator = activity.Call<AndroidJavaObject>("getSystemService", "vibrator");
                IntPtr local = AndroidJNI.FindClass("android/os/VibrationEffect");
                _effectClass = AndroidJNI.NewGlobalRef(local);
                AndroidJNI.DeleteLocalRef(local);
                _createOneShot = AndroidJNI.GetStaticMethodID(_effectClass, "createOneShot", "(JI)Landroid/os/VibrationEffect;");
                IntPtr cls = AndroidJNI.GetObjectClass(_vibrator.GetRawObject());
                _vibrate = AndroidJNI.GetMethodID(cls, "vibrate", "(Landroid/os/VibrationEffect;)V");
                AndroidJNI.DeleteLocalRef(cls);
                if (_createOneShot == IntPtr.Zero || _vibrate == IntPtr.Zero) _vibrator = null;
            }
            catch (Exception) { _vibrator = null; }
            if (Thrown()) _vibrator = null;
        }

        /// <summary>A Java exception left by the last call, cleared.</summary>
        static bool Thrown()
        {
            IntPtr e = AndroidJNI.ExceptionOccurred();
            if (e == IntPtr.Zero) return false;
            AndroidJNI.ExceptionClear();
            AndroidJNI.DeleteLocalRef(e);
            return true;
        }

        void OnDestroy()
        {
            if (_effectClass != IntPtr.Zero) AndroidJNI.DeleteGlobalRef(_effectClass);
            _vibrator?.Dispose();
        }

        void OnEnable()
        {
            SimBus.Hit += OnHit;
            SimBus.Knockdown += OnKnockdown;
            SimBus.FloorImpact += OnFloor;
        }

        void OnDisable()
        {
            SimBus.Hit -= OnHit;
            SimBus.Knockdown -= OnKnockdown;
            SimBus.FloorImpact -= OnFloor;
        }

        void OnHit(HitEvent e)
        {
            Excitement ex = Excitement.Instance;
            float k = Mathf.Clamp01(e.impulse / (1.5f * (ex != null ? ex.UsualImpulse : 10f)));
            if (k > 0.15f) Buzz(Mathf.Lerp(8f, 40f, k), Mathf.Lerp(50f, 255f, k), false);
        }

        void OnKnockdown(Fighter f, HitEvent cause) => Buzz(160f, 255f, true);

        void OnFloor(Vector3 point, float impulse)
        {
            float k = Mathf.Clamp01(impulse / 120f);
            if (k > 0.1f) Buzz(Mathf.Lerp(10f, 60f, k), Mathf.Lerp(60f, 220f, k), false);
        }

        void Buzz(float ms, float amplitude, bool always)
        {
            if (!_android || !Enabled || (!always && Time.unscaledTime - _last < minInterval)) return;
            _last = Time.unscaledTime;
            if (_vibrator == null)
            {
                // Half a second each, which is too long for a punch.
                if (always) Handheld.Vibrate();
                return;
            }
            _oneShot[0].j = (long)ms;
            _oneShot[1].i = Mathf.Clamp((int)amplitude, 1, 255);
            IntPtr effect = AndroidJNI.CallStaticObjectMethod(_effectClass, _createOneShot, _oneShot);
            _arg[0].l = effect;
            AndroidJNI.CallVoidMethod(_vibrator.GetRawObject(), _vibrate, _arg);
            AndroidJNI.DeleteLocalRef(effect);
            if (Thrown()) _vibrator = null;
        }
    }
}
