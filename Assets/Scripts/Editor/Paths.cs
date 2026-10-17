using System.IO;
using UnityEditor;

namespace PoBox.EditorTools
{
    /// <summary>Where the editor tools find the scenes and the folders they write into.</summary>
    public static class Paths
    {
        public const string ArenaScene = "Assets/Scenes/Arena.unity";
        public const string MenuScene = "Assets/Scenes/Menu.unity";
        public const string MeleeScene = "Assets/Scenes/Melee.unity";
        public const string TestbedScene = "Assets/Scenes/Testbed.unity";
        public const string SettingsDir = "Assets/Settings";
        public const string LeagueDir = "Assets/League";
        public const string PortraitDir = "Assets/UI/Portraits";

        public static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
