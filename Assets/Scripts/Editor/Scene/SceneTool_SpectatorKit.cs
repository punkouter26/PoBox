using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;

namespace PoBox.Editor
{
    /// <summary>
    /// Builds the <see cref="Systems_SpectatorKit"/> in Resources: the mixer
    /// and its three busses, matched by name.
    ///
    /// It creates ASSETS ONLY and never opens, edits or saves a scene. That is
    /// deliberate: regenerating a contest scene is the documented way to
    /// destroy one (CLAUDE.md on <c>BuildAll</c>).
    ///
    /// Idempotent. Re-running it also drops the serialized fields of the
    /// materials, clip sets and brain dossiers the kit carried before the
    /// PhysX cast and the ML-Agents line were removed on 2026-09-14.
    /// </summary>
    public static class SceneTool_SpectatorKit
    {
        private const string RESOURCES_DIR = "Assets/Resources";
        private const string KIT_PATH = RESOURCES_DIR + "/SpectatorKit.asset";
        private const string MIXER_PATH = "Assets/Audio/AM_Contest.mixer";

        [MenuItem("PoBox/Scene/Build Spectator Kit")]
        public static void Build()
        {
            System.IO.Directory.CreateDirectory(RESOURCES_DIR);

            var kit = AssetDatabase.LoadAssetAtPath<Systems_SpectatorKit>(KIT_PATH);
            if (kit == null)
            {
                kit = ScriptableObject.CreateInstance<Systems_SpectatorKit>();
                AssetDatabase.CreateAsset(kit, KIT_PATH);
            }
            AttachMixer(kit);
            EditorUtility.SetDirty(kit);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("RigTool: spectator kit built — " +
                      $"mixer {(kit.mixer != null ? "ok" : "absent (per-source fallback)")}. " +
                      $"Kit at {KIT_PATH}.");
        }

        /// <summary>
        /// Links the mixer and its three busses, if one has been built.
        ///
        /// Matched BY NAME, which is safe here: the groups are created by
        /// <see cref="Editor_AudioMixer"/> from the same list
        /// <see cref="Systems_AudioMix"/> reads, in this same repository, so a
        /// name is the whole identity rather than a label somebody wrote on a
        /// folder.
        /// </summary>
        private static void AttachMixer(Systems_SpectatorKit kit)
        {
            var mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(MIXER_PATH);
            kit.mixer = mixer;
            if (mixer == null)
            {
                // Not a warning. The mixer is an upgrade, not a requirement —
                // Systems_AudioMix delivers the same three busses and the same
                // ducking without it. Run PoBox/Audio/Build Mixer to get one.
                kit.crowdGroup = null;
                kit.foleyGroup = null;
                kit.announcerGroup = null;
                return;
            }
            kit.crowdGroup = FindGroup(mixer, "Crowd");
            kit.foleyGroup = FindGroup(mixer, "Foley");
            kit.announcerGroup = FindGroup(mixer, "Announcer");
        }

        private static AudioMixerGroup FindGroup(AudioMixer mixer, string groupName)
        {
            AudioMixerGroup[] matches = mixer.FindMatchingGroups(groupName);
            if (matches == null || matches.Length == 0)
            {
                Debug.LogWarning($"RigTool: the mixer has no '{groupName}' group — that bus " +
                                 "falls back to a per-source gain. Run PoBox/Audio/Build Mixer.");
                return null;
            }
            return matches[0];
        }
    }
}
