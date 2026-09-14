using UnityEngine;

namespace PoBox
{
    /// <summary>
    /// Feeds <c>_Strain</c> to the bodies wearing
    /// <c>Assets/Art/Shaders/SH_FighterBody.shader</c>, so how hard a fighter is
    /// working shows on the fighter rather than only in the commentary band.
    ///
    /// THE NUMBER IS THE SAME ONE EVERYTHING ELSE USES.
    /// <see cref="Systems_Stamina.JointLoad01"/> — what the heatmap draws and
    /// what stamina drains from — never the action vector, which is a request
    /// rather than a force and reads as full effort from a policy pinned against
    /// a limit. Three systems now show one quantity three ways: the overlay per
    /// joint, the band in words, the body in colour.
    ///
    /// PER FIGHTER, NOT PER PART, and that is the design rather than a shortcut.
    /// <see cref="Systems_JointStressView"/> already answers "which joint" with
    /// a glow on that joint; what no view answered was "is this fighter in
    /// trouble", which is a whole-body question a spectator asks from across a
    /// portrait screen. Per-part tinting would also need a property block on
    /// every renderer of every fighter — 8 fighters of ~15 parts each — and each
    /// block opts its renderer out of the SRP batcher.
    ///
    /// PEAK, NOT MEAN. A mean over fourteen joints cannot see an ankle pinned at
    /// its ceiling, because the other thirteen dilute it; the same reasoning the
    /// project applies to per-body statistics against a single aggregate. The
    /// peak is smoothed instead, which keeps one noisy step from strobing the
    /// whole body.
    ///
    /// SAFE ON EVERY OTHER MATERIAL. Renderers are collected once, at Start, and
    /// only those whose material actually declares _Strain are kept. Grandma and
    /// Grandpa wear their own textured materials and simply are not in the list,
    /// so this does nothing to them — and costs nothing for them either, since a
    /// property block set on a renderer that ignores it would still drop that
    /// renderer out of the batcher.
    ///
    /// Created at runtime by <see cref="Systems_ContestSpawner"/>. Test-scene
    /// harness only — no training scene has a camera to see it with.
    /// </summary>
    public sealed class Systems_FighterShading : MonoBehaviour
    {
        /// <summary>
        /// Seconds for the tint to follow a change in load. Long enough that a
        /// single hard physics step does not flash the body, short enough that
        /// catching a shove still reads as it happens.
        /// </summary>
        private const float SMOOTHING_SECONDS = 0.28f;

        /// <summary>
        /// Load below which nothing is shown. Joint servos idle well above zero
        /// just holding a body up, and a fighter standing still glowing at a
        /// third strain would make the cue meaningless.
        /// </summary>
        private const float STRAIN_FLOOR = 0.45f;

        private static readonly int StrainId = Shader.PropertyToID("_Strain");

        /// <summary>One fighter, its rig and the renderers that can show strain.</summary>
        private struct Body
        {
            public Systems_FighterRig rig;
            public Renderer[] renderers;
            public float smoothed;
        }

        private Body[] _bodies;
        private MaterialPropertyBlock _block;

        private void Start()
        {
            Systems_FighterRig[] rigs = FindObjectsByType<Systems_FighterRig>(FindObjectsSortMode.InstanceID);
            if (rigs.Length == 0)
            {
                return;
            }
            _block = new MaterialPropertyBlock();

            var bodies = new System.Collections.Generic.List<Body>(rigs.Length);
            for (int rigIndex = 0; rigIndex < rigs.Length; rigIndex++)
            {
                Renderer[] renderers = CollectStrainRenderers(rigs[rigIndex]);
                if (renderers.Length > 0)
                {
                    bodies.Add(new Body { rig = rigs[rigIndex], renderers = renderers });
                }
            }
            _bodies = bodies.ToArray();
        }

        /// <summary>
        /// The renderers on <paramref name="rig"/> whose material declares
        /// _Strain. sharedMaterial rather than material: reading
        /// <c>Renderer.material</c> INSTANTIATES the material, which would leak
        /// one copy per body part per fighter per contest and quietly undo the
        /// copy wash the spawner set through a property block.
        /// </summary>
        private static Renderer[] CollectStrainRenderers(Systems_FighterRig rig)
        {
            var renderers = rig.GetComponentsInChildren<Renderer>(true);
            var kept = new System.Collections.Generic.List<Renderer>(renderers.Length);
            for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
            {
                Material material = renderers[rendererIndex].sharedMaterial;
                if (material != null && material.HasProperty(StrainId))
                {
                    kept.Add(renderers[rendererIndex]);
                }
            }
            return kept.ToArray();
        }

        /// <summary>
        /// LateUpdate and unscaled time, like every other presentation system
        /// here: the tint keeps living through the round countdown's timeScale 0
        /// and the knockout slow-mo, where a frozen body is exactly when a
        /// viewer has time to look at it.
        /// </summary>
        private void LateUpdate()
        {
            if (_bodies == null)
            {
                return;
            }
            float step = Time.unscaledDeltaTime / Mathf.Max(0.001f, SMOOTHING_SECONDS);
            for (int bodyIndex = 0; bodyIndex < _bodies.Length; bodyIndex++)
            {
                Body body = _bodies[bodyIndex];
                if (body.rig == null)
                {
                    continue;
                }
                float peak = PeakLoad(body.rig);
                // Rescaled from the floor rather than clamped at it, so the
                // visible range covers the whole span between "working" and
                // "at the ceiling" instead of the top half of it.
                float target = Mathf.InverseLerp(STRAIN_FLOOR, 1f, peak);
                body.smoothed = Mathf.MoveTowards(body.smoothed, target, step);
                _bodies[bodyIndex] = body;

                for (int rendererIndex = 0; rendererIndex < body.renderers.Length; rendererIndex++)
                {
                    Renderer renderer = body.renderers[rendererIndex];
                    if (renderer == null)
                    {
                        continue;
                    }
                    // Read-modify-write. The block already carries the copy
                    // wash for the second and later fighters of a kind, and a
                    // fresh block would erase it — turning both Grandmas the
                    // same colour again, which is the exact bug the wash was
                    // added to fix.
                    renderer.GetPropertyBlock(_block);
                    _block.SetFloat(StrainId, body.smoothed);
                    renderer.SetPropertyBlock(_block);
                }
            }
        }

        private static float PeakLoad(Systems_FighterRig rig)
        {
            var joints = rig.Joints;
            float springScale = rig.CurrentSpringScale;
            float peak = 0f;
            for (int jointIndex = 0; jointIndex < joints.Count; jointIndex++)
            {
                RigJointEntry entry = joints[jointIndex];
                if (entry == null || entry.joint == null)
                {
                    continue;
                }
                float load = Systems_Stamina.JointLoad01(entry, springScale);
                if (load > peak)
                {
                    peak = load;
                }
            }
            return peak;
        }
    }
}
