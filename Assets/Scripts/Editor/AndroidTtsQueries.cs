using System.IO;
using UnityEditor.Android;

namespace PoBox.EditorTools
{
    /// <summary>
    /// Since Android 11 an app sees only the services it declares it looks for, so without this the spoken
    /// commentary (SpeechCommentary) finds no speech engine on the phone. Adds the TTS_SERVICE query to the
    /// generated manifest; the project has no manifest of its own to put it in.
    /// </summary>
    class AndroidTtsQueries : IPostGenerateGradleAndroidProject
    {
        const string Query = "<queries><intent><action android:name=\"android.intent.action.TTS_SERVICE\" /></intent></queries>";

        public int callbackOrder => 0;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            string manifest = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            string xml = File.ReadAllText(manifest);
            if (xml.Contains("android.intent.action.TTS_SERVICE")) return;
            File.WriteAllText(manifest, xml.Replace("</manifest>", "  " + Query + "\n</manifest>"));
        }
    }
}
