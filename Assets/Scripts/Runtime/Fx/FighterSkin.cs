using System;
using UnityEngine;
using PoBox.Sim;

namespace PoBox.Fx
{
    /// <summary>
    /// What a fighter looks like, as opposed to what it weighs. Both the live fighter and its replay puppet
    /// carry one, with their parts in the same order, so the replay can pose a puppet and the joint-stress
    /// heat map can paint either without knowing which it has.
    ///
    /// The heat map is per renderer through a property block: each link glows from amber to red as the
    /// drive torque on the joint above it nears that joint's limit, takes a dull bruise where it has been
    /// hit, and picks up a wet highlight as the fighter tires. The overlay shader reads the three numbers.
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
        [Tooltip("The live fighter. Empty on a replay puppet, which is fed by the replay instead.")]
        public Fighter fighter;
        public Color rimColor = Color.white;
        [Tooltip("From the head part's origin to the middle of the head. Zero when the part is already there.")]
        public Vector3 headOffset = new Vector3(0f, 0.12f, 0f);
        [Tooltip("Anything else that is this fighter to look at and is not a part: the skinned mesh.")]
        public Renderer[] extraRenderers = new Renderer[0];
        [Tooltip("Glove speed at which the trail starts, m/s.")]
        public float trailSpeed = 4.5f;

        /// <summary>Settings toggle: the joint-stress glow on or off for everybody.</summary>
        public static bool HeatmapOn = true;

        static readonly int StressId = Shader.PropertyToID("_Stress");
        static readonly int BruiseId = Shader.PropertyToID("_Bruise");
        static readonly int SweatId = Shader.PropertyToID("_Sweat");
        static readonly int RimId = Shader.PropertyToID("_RimColor");

        MaterialPropertyBlock _block;
        float[] _stress, _bruise;
        BodyPart[] _source;
        float _sweat;
        bool _visible = true;

        public bool Visible => _visible;
        public Vector3 HeadPoint => parts.Length > Head && parts[Head].bone != null ? parts[Head].bone.TransformPoint(headOffset) : transform.position;
        public Vector3 ChestPoint => parts.Length > Torso && parts[Torso].bone != null ? parts[Torso].bone.TransformPoint(0f, 0.25f, 0f) : transform.position;
        public Vector3 PelvisPoint => parts.Length > Pelvis && parts[Pelvis].bone != null ? parts[Pelvis].bone.position : transform.position;

        void Awake() => Ensure();

        void Ensure()
        {
            if (_block == null) _block = new MaterialPropertyBlock();
            if (_stress == null || _stress.Length != parts.Length)
            {
                _stress = new float[parts.Length];
                _bruise = new float[parts.Length];
            }
        }

        public void SetPart(int i, float stress, float bruise)
        {
            Ensure();
            if (i < 0 || i >= _stress.Length) return;
            _stress[i] = stress;
            _bruise[i] = bruise;
        }

        public float StressOf(int i) => _stress != null && i >= 0 && i < _stress.Length ? _stress[i] : 0f;
        public float BruiseOf(int i) => _bruise != null && i >= 0 && i < _bruise.Length ? _bruise[i] : 0f;
        public void SetSweat(float sweat) => _sweat = sweat;
        public float Sweat => _sweat;

        public void SetVisible(bool visible)
        {
            _visible = visible;
            foreach (Part p in parts)
                foreach (Renderer r in p.renderers)
                    if (r != null) r.enabled = visible;
            foreach (Renderer r in extraRenderers)
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
            Ensure();
            if (fighter != null)
            {
                // Matched by bone, not by position in the list: the skin may carry a part the fighter has
                // no link for (a trained body's head is on its torso link).
                if (_source == null || _source.Length != parts.Length)
                {
                    _source = new BodyPart[parts.Length];
                    for (int i = 0; i < parts.Length; i++)
                        foreach (BodyPart p in fighter.parts)
                            if (p != null && p.transform == parts[i].bone) { _source[i] = p; break; }
                }
                for (int i = 0; i < parts.Length; i++)
                {
                    if (_source[i] == null) continue;
                    _stress[i] = _source[i].stress;
                    _bruise[i] = _source[i].bruise;
                }
                _sweat = 1f - fighter.Stamina;

                if (trails.Length >= 2 && _visible)
                {
                    if (trails[0] != null) trails[0].emitting = fighter.GloveSpeedL > trailSpeed;
                    if (trails[1] != null) trails[1].emitting = fighter.GloveSpeedR > trailSpeed;
                }
            }
            if (!_visible) return;

            float heat = HeatmapOn ? 1f : 0f;
            for (int i = 0; i < parts.Length; i++)
            {
                _block.Clear();
                _block.SetFloat(StressId, _stress[i] * heat);
                _block.SetFloat(BruiseId, _bruise[i]);
                _block.SetFloat(SweatId, _sweat);
                _block.SetColor(RimId, rimColor);
                Renderer[] rs = parts[i].renderers;
                for (int r = 0; r < rs.Length; r++)
                    if (rs[r] != null) rs[r].SetPropertyBlock(_block);
            }
        }
    }
}
