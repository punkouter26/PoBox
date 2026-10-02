using System;
using UnityEngine;
using PoBox.Sim;

namespace PoBox.Fx
{
    /// <summary>
    /// What a fighter looks like, as opposed to what it weighs: which renderers are which part of it, where
    /// its head, chest and pelvis are for the cameras, and the trails behind its gloves.
    ///
    /// It also feeds the overlay shader, per renderer through a property block. On the stand-in, whose body
    /// is one renderer a link, that is a dull bruise on a part that has been hit. On a trained fighter the
    /// overlay is drawn over the owner's own skinned mesh, and the bruises are marks at the very places the
    /// punches landed: each is remembered on the link it landed on, and its place in the world is handed to
    /// the shader every frame, so it moves with the body. Both get a wet highlight as the fighter tires and
    /// the corner colour as a rim.
    /// </summary>
    public class FighterSkin : MonoBehaviour
    {
        [Serializable]
        public class Part
        {
            public Transform bone;
            public Renderer[] renderers = new Renderer[0];
        }

        /// <summary>Order of the first three parts on every skin. The cameras rely on it.</summary>
        public const int Pelvis = 0, Torso = 1, Head = 2;

        [Tooltip("Same order as Fighter.parts: pelvis, torso, head, then the limbs.")]
        public Part[] parts = new Part[0];
        [Tooltip("Left glove, right glove.")]
        public TrailRenderer[] trails = new TrailRenderer[0];
        public Fighter fighter;
        public Color rimColor = Color.white;
        [Tooltip("From the head part's origin to the middle of the head. Zero when the part is already there.")]
        public Vector3 headOffset = new Vector3(0f, 0.12f, 0f);
        [Tooltip("Anything else that is this fighter to look at and is not a part: the skinned mesh.")]
        public Renderer[] extraRenderers = new Renderer[0];
        [Tooltip("The overlay drawn over the skinned mesh: bruises where punches landed, sweat, the corner's rim.")]
        public Renderer[] overlayRenderers = new Renderer[0];
        [Tooltip("Glove speed at which the trail starts, m/s.")]
        public float trailSpeed = 4.5f;
        [Tooltip("How far a bruise spreads from where the punch landed, metres.")]
        public float bruiseRadius = 0.09f;

        static readonly int BruiseId = Shader.PropertyToID("_Bruise");
        static readonly int SweatId = Shader.PropertyToID("_Sweat");
        static readonly int RimId = Shader.PropertyToID("_RimColor");
        static readonly int MarksId = Shader.PropertyToID("_BruiseMarks");
        static readonly int MarkCountId = Shader.PropertyToID("_BruiseCount");

        MaterialPropertyBlock _block;
        BodyPart[] _source;
        bool _visible = true;
        readonly Vector4[] _marks = new Vector4[Fighter.MaxBruises];

        public bool Visible => _visible;
        public Vector3 HeadPoint => parts.Length > Head && parts[Head].bone != null ? parts[Head].bone.TransformPoint(headOffset) : transform.position;
        public Vector3 ChestPoint => parts.Length > Torso && parts[Torso].bone != null ? parts[Torso].bone.TransformPoint(0f, 0.25f, 0f) : transform.position;
        public Vector3 PelvisPoint => parts.Length > Pelvis && parts[Pelvis].bone != null ? parts[Pelvis].bone.position : transform.position;

        public void SetVisible(bool visible)
        {
            _visible = visible;
            foreach (Part p in parts)
                foreach (Renderer r in p.renderers)
                    if (r != null) r.enabled = visible;
            foreach (Renderer r in extraRenderers)
                if (r != null) r.enabled = visible;
            foreach (Renderer r in overlayRenderers)
                if (r != null) r.enabled = visible;
            foreach (TrailRenderer t in trails)
            {
                if (t == null) continue;
                t.Clear();
                t.emitting = false;
                t.enabled = visible;
            }
        }

        void LateUpdate()
        {
            if (fighter == null || !_visible) return;
            if (_block == null) _block = new MaterialPropertyBlock();

            // Matched by bone, not by position in the list: the skin may carry a part the fighter has
            // no link for (a trained body's head is on its torso link).
            if (_source == null || _source.Length != parts.Length)
            {
                _source = new BodyPart[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                    foreach (BodyPart p in fighter.parts)
                        if (p != null && p.transform == parts[i].bone) { _source[i] = p; break; }
            }

            if (trails.Length >= 2)
            {
                if (trails[0] != null) trails[0].emitting = fighter.GloveSpeedL > trailSpeed;
                if (trails[1] != null) trails[1].emitting = fighter.GloveSpeedR > trailSpeed;
            }

            float sweat = 1f - fighter.Stamina;
            for (int i = 0; i < parts.Length; i++)
            {
                Renderer[] rs = parts[i].renderers;
                if (rs.Length == 0) continue;
                _block.Clear();
                _block.SetFloat(BruiseId, _source[i] != null ? _source[i].bruise : 0f);
                _block.SetFloat(SweatId, sweat);
                _block.SetColor(RimId, rimColor);
                for (int r = 0; r < rs.Length; r++)
                    if (rs[r] != null) rs[r].SetPropertyBlock(_block);
            }

            if (overlayRenderers.Length == 0) return;
            // Where each mark is now: xyz in the world, w how bad it is. Unused slots are zeroed.
            int count = Mathf.Min(fighter.bruises.Count, _marks.Length);
            for (int i = 0; i < _marks.Length; i++)
            {
                if (i >= count || fighter.bruises[i].bone == null) { _marks[i] = Vector4.zero; continue; }
                Fighter.Bruise b = fighter.bruises[i];
                Vector3 w = b.bone.TransformPoint(b.local);
                _marks[i] = new Vector4(w.x, w.y, w.z, b.amount);
            }
            _block.Clear();
            _block.SetFloat(BruiseId, 0f);
            _block.SetFloat(SweatId, sweat);
            _block.SetColor(RimId, rimColor);
            _block.SetVectorArray(MarksId, _marks);
            _block.SetFloat(MarkCountId, count);
            for (int r = 0; r < overlayRenderers.Length; r++)
                if (overlayRenderers[r] != null) overlayRenderers[r].SetPropertyBlock(_block);
        }
    }
}
