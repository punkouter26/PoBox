using Unity.InferenceEngine;
using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// The assets the spectator layer needs and cannot obtain any other way:
    /// two materials, the impact foley set, and the brain dossiers.
    ///
    /// WHY IT IS LOADED FROM Resources. Every system that reads this is created
    /// at RUNTIME by <see cref="Systems_ContestSpawner"/> rather than placed in
    /// a scene (see its EnsureSpectatorSystems), so there is no serialized field
    /// on a scene object for anyone to drag a material into. The two remaining
    /// ways to get a material at runtime are Shader.Find, which strips out of
    /// the Android build — the copy-wash comment in the spawner records that
    /// lesson — and Resources, which does not.
    ///
    /// This is not a singleton and holds no state: it is a read-only bundle of
    /// asset references, which is the same role
    /// <see cref="Systems_MiniGameSelection"/> plays for cross-scene data. Every
    /// consumer keeps its own reference and copes with null, so a project that
    /// has never run <c>Tools/ML Boxing/17</c> simply has no overlays rather
    /// than a scene full of exceptions.
    /// </summary>
    public sealed class Systems_SpectatorKit : ScriptableObject
    {
        /// <summary>Resources path, without extension. See <see cref="Load"/>.</summary>
        public const string RESOURCE_PATH = "SpectatorKit";

        [Tooltip("Additive unlit material for the joint-stress overlay and impact sparks. " +
                 "Never applied to a fighter's own renderers — see Systems_JointStressView.")]
        public Material overlayMaterial;

        [Tooltip("Body-on-canvas impacts, picked at random and pitched by impulse.")]
        public AudioClip[] impactClips;

        [Tooltip("One per brain under Assets/Agents. Matched to a roster entry by ModelAsset reference.")]
        public Systems_BrainDossier[] dossiers;

        /// <summary>
        /// The kit, or null when it has not been generated. Callers must handle
        /// null: the kit is a presentation nicety and no contest depends on it
        /// to be refereed correctly.
        /// </summary>
        public static Systems_SpectatorKit Load()
        {
            return Resources.Load<Systems_SpectatorKit>(RESOURCE_PATH);
        }

        /// <summary>
        /// Dossier for <paramref name="modelAsset"/>, or null. Matched by
        /// asset reference rather than by name: two brains exported from the
        /// same run carry the same file name often enough that a string match
        /// would confidently return the wrong generation.
        /// </summary>
        public Systems_BrainDossier Find(ModelAsset modelAsset)
        {
            if (modelAsset == null || dossiers == null)
            {
                return null;
            }
            for (int dossierIndex = 0; dossierIndex < dossiers.Length; dossierIndex++)
            {
                Systems_BrainDossier dossier = dossiers[dossierIndex];
                if (dossier != null && dossier.model == modelAsset)
                {
                    return dossier;
                }
            }
            return null;
        }
    }
}
