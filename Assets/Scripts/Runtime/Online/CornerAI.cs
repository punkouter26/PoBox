using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace PoBox.Online
{
    /// <summary>
    /// The cornerman's line from Claude, through the proxy that holds the key (server/corner_proxy.py), never from
    /// the phone straight to the API: a key shipped inside an app is anyone's key. The game posts the fight's own
    /// numbers as JSON and gets one line back; no proxy set, the proxy down, or no answer in time, and the caller
    /// says its rule-based line instead (Commentary.CornerTip).
    ///
    /// The address is the settings' CORNER field (DEBUG tab), kept in PlayerPrefs; empty (off) until one is typed, in
    /// the editor too: a default that pointed at a proxy nobody had started cost 4 failed posts a bout.
    /// http://127.0.0.1:8787/corner is the proxy on this PC. A proxy started with --token takes it as ?token=... on the address.
    /// ponytail: plain HTTP to a LAN proxy; a phone build needs Player Settings' "Allow downloads over HTTP" (or an
    /// https proxy) before it can reach one.
    /// </summary>
    public static class CornerAI
    {
        public const string PrefKey = "pobox.corner";

        public static string Url => PlayerPrefs.GetString(PrefKey, "").Trim();

        [System.Serializable]
        class Reply { public string text; }

        /// <summary>Sends the numbers; null when there is no proxy to ask. Poll <see cref="Answer"/> until it is not pending.</summary>
        public static UnityWebRequest Ask(string json)
        {
            string url = Url;
            if (string.IsNullOrEmpty(url)) return null;
            var r = new UnityWebRequest(url, "POST")
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 10,
            };
            r.SetRequestHeader("content-type", "application/json");
            r.SendWebRequest();
            return r;
        }

        /// <summary>Whether the request is still out.</summary>
        public static bool Pending(UnityWebRequest r) => r != null && !r.isDone;

        /// <summary>The line, or null if there is none (no request, still out, failed, empty). Disposes a finished request.</summary>
        public static string Answer(ref UnityWebRequest r)
        {
            if (r == null || !r.isDone) return null;
            string text = null;
            if (r.result == UnityWebRequest.Result.Success)
            {
                try { text = JsonUtility.FromJson<Reply>(r.downloadHandler.text)?.text; }
                catch (System.Exception) { text = null; }
            }
            r.Dispose();
            r = null;
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        public static void Drop(ref UnityWebRequest r)
        {
            r?.Dispose();
            r = null;
        }
    }
}
