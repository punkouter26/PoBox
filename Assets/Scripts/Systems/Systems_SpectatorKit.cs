using UnityEngine;
using UnityEngine.Audio;

namespace PoBox
{
    /// <summary>
    /// The audio assets the spectator layer needs and cannot obtain any other
    /// way: the mix and its three busses.
    ///
    /// WHY IT IS LOADED FROM Resources. The systems that read this are created
    /// or wired at RUNTIME rather than placed in a scene, so there is no
    /// serialized field on a scene object for anyone to drag an asset into.
    ///
    /// This is not a singleton and holds no state: it is a read-only bundle of
    /// asset references, which is the same role
    /// <see cref="Systems_MiniGameSelection"/> plays for cross-scene data. Every
    /// consumer keeps its own reference and copes with null, so a project that
    /// has never run <c>PoBox/Scene/Build Spectator Kit</c> simply has no mix
    /// rather than a scene full of exceptions.
    ///
    /// The overlay and blob-shadow materials and the footstep and impact clip
    /// sets that used to live here served the PhysX rig's heatmap, shadows and
    /// foley, which went with the PhysX cast on 2026-09-14.
    /// </summary>
    public sealed class Systems_SpectatorKit : ScriptableObject
    {
        /// <summary>Resources path, without extension. See <see cref="Load"/>.</summary>
        public const string RESOURCE_PATH = "SpectatorKit";

        [Tooltip("The mix. Optional: Systems_AudioMix falls back to per-source gains without it.")]
        public AudioMixer mixer;

        [Tooltip("Crowd bus. Ducked while the announcer speaks.")]
        public AudioMixerGroup crowdGroup;

        [Tooltip("Foley bus — bodies and impacts. Ducked while the announcer speaks.")]
        public AudioMixerGroup foleyGroup;

        [Tooltip("Announcer bus — the bell and the play-by-play. Never ducked.")]
        public AudioMixerGroup announcerGroup;

        /// <summary>
        /// The kit, or null when it has not been generated. Callers must handle
        /// null: the kit is a presentation nicety and no contest depends on it
        /// to be refereed correctly.
        /// </summary>
        public static Systems_SpectatorKit Load()
        {
            return Resources.Load<Systems_SpectatorKit>(RESOURCE_PATH);
        }
    }
}
