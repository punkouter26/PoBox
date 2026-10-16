using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace PoBox.UI
{
    /// <summary>
    /// SHARE: the screen as it is, saved as a PNG in Application.persistentDataPath, then handed to Android's share
    /// sheet with a line of text (the result). No plugin and no FileProvider: the picture is also put in the
    /// phone's gallery (MediaStore, Pictures/PoBox, which an app may write to without a permission since Android
    /// 10), and the gallery's content:// address is what is shared. If that fails the text alone is shared.
    /// Anywhere but a phone it only saves the file and logs where.
    /// </summary>
    public static class Share
    {
        public static string LastPath { get; private set; }

        /// <summary>Run as a coroutine (the screen can only be read at the end of a frame).</summary>
        public static IEnumerator Shot(string text)
        {
            yield return new WaitForEndOfFrame();
            Texture2D shot = ScreenCapture.CaptureScreenshotAsTexture();
            byte[] png = shot.EncodeToPNG();
            UnityEngine.Object.Destroy(shot);
            string name = $"pobox-{DateTime.Now:yyyyMMdd-HHmmss}.png";
            try
            {
                LastPath = Path.Combine(Application.persistentDataPath, name);
                File.WriteAllBytes(LastPath, png);
            }
            catch (Exception e) { Debug.LogWarning("[Share] not saved: " + e.Message); }
#if UNITY_ANDROID && !UNITY_EDITOR
            Send(png, name, text);
#else
            Debug.Log($"[Share] {text} · {LastPath}");
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        static void Send(byte[] png, string name, string text)
        {
            AndroidJavaObject image = null;
            try { image = ToGallery(png, name); }
            catch (Exception e) { Debug.LogWarning("[Share] picture not shared, text only: " + e.Message); }
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (AndroidJavaObject activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var intent = new AndroidJavaObject("android.content.Intent", "android.intent.action.SEND"))
                {
                    intent.Call<AndroidJavaObject>("setType", image != null ? "image/png" : "text/plain");
                    intent.Call<AndroidJavaObject>("putExtra", "android.intent.extra.TEXT", text);
                    if (image != null)
                    {
                        intent.Call<AndroidJavaObject>("putExtra", "android.intent.extra.STREAM", image);
                        intent.Call<AndroidJavaObject>("addFlags", 1);      // FLAG_GRANT_READ_URI_PERMISSION
                    }
                    using (var chooser = new AndroidJavaClass("android.content.Intent"))
                    using (AndroidJavaObject sheet = chooser.CallStatic<AndroidJavaObject>("createChooser", intent, "Share the fight"))
                        activity.Call("startActivity", sheet);
                }
            }
            catch (Exception e) { Debug.LogWarning("[Share] " + e.Message); }
            image?.Dispose();
        }

        /// <summary>The PNG written into the gallery; its content:// address.</summary>
        static AndroidJavaObject ToGallery(byte[] png, string name)
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (AndroidJavaObject activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (AndroidJavaObject resolver = activity.Call<AndroidJavaObject>("getContentResolver"))
            using (var values = new AndroidJavaObject("android.content.ContentValues"))
            using (var media = new AndroidJavaClass("android.provider.MediaStore$Images$Media"))
            {
                values.Call("put", "_display_name", name);
                values.Call("put", "mime_type", "image/png");
                values.Call("put", "relative_path", "Pictures/PoBox");
                AndroidJavaObject uri = resolver.Call<AndroidJavaObject>("insert", media.GetStatic<AndroidJavaObject>("EXTERNAL_CONTENT_URI"), values);
                using (AndroidJavaObject stream = resolver.Call<AndroidJavaObject>("openOutputStream", uri))
                {
                    stream.Call("write", (sbyte[])(Array)png);
                    stream.Call("close");
                }
                return uri;
            }
        }
#endif
    }
}
