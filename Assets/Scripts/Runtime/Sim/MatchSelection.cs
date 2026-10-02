using UnityEngine;

namespace PoBox.Sim
{
    /// <summary>
    /// Which two boxers the menu picked, carried from the menu scene into the arena. Nothing chosen means
    /// the arena was opened on its own (the editor's Play button, a test), and it uses its own default pair.
    /// The last pick is also kept on the device, so the menu opens on it next time.
    /// </summary>
    public static class MatchSelection
    {
        public const string MenuScene = "Menu";
        public const string ArenaScene = "Arena";
        const string RedKey = "pobox.pick.red", BlueKey = "pobox.pick.blue";

        public static string Red { get; private set; }
        public static string Blue { get; private set; }
        public static bool Chosen => !string.IsNullOrEmpty(Red) && !string.IsNullOrEmpty(Blue);

        public static string LastRed => PlayerPrefs.GetString(RedKey, "");
        public static string LastBlue => PlayerPrefs.GetString(BlueKey, "");

        /// <summary>The menu scene is in the build: there is somewhere to go back to.</summary>
        public static bool HasMenu => Application.CanStreamedLevelBeLoaded(MenuScene);

        public static void Choose(string red, string blue)
        {
            Red = red;
            Blue = blue;
            PlayerPrefs.SetString(RedKey, red);
            PlayerPrefs.SetString(BlueKey, blue);
            PlayerPrefs.Save();
        }

        /// <summary>Back to nothing chosen. Also at every start: statics outlive a play session when the editor is set not to reload the domain.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void Clear()
        {
            Red = null;
            Blue = null;
        }
    }
}
