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
    /// the shader every frame, so it moves with the body. Both get a wet highlight as the fighter tires (its
    /// stamina, or its muscles' reserve where it has one, whichever is lower), a flush round the face as
    /// its health goes, and the corner colour as a rim. The sheen is the overlay's, not the body material's
    /// smoothness: a property block on the body itself would take it out of the SRP batcher.
    ///
    /// The head of the owner's mesh moves too, though MuJoCo's head is a ball fixed on the trunk: a punch to it
    /// snaps it away from the glove on a spring and lets it come back, and a tired fighter's chin sinks. That is
    /// drawn only, on the neck bone the skin binder leaves to follow the chest; no number of the fight changes.
    /// It runs after the binder (order 200), so the chest it turns the neck from is this frame's.
    /// </summary>
    [DefaultExecutionOrder(210)]
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
        [Header("Head (drawn only)")]
        [Tooltip("How fast a head shot turns the head, degrees a second per m/s of the punch. Body shots a third of it.")]
        public float snapPerSpeed = 70f;
        [Tooltip("The neck's spring: how often it swings back, a second, and how much each swing loses.")]
        public float snapHz = 2.6f, snapDamping = 0.35f;
        [Tooltip("The furthest the head turns, degrees.")]
        public float snapMax = 38f;
        [Tooltip("How far a spent or dazed fighter's chin sinks, degrees.")]
        public float tiredDroop = 14f;

        static readonly int BruiseId = Shader.PropertyToID("_Bruise");
        static readonly int SweatId = Shader.PropertyToID("_Sweat");
        static readonly int RimId = Shader.PropertyToID("_RimColor");
        static readonly int MarksId = Shader.PropertyToID("_BruiseMarks");
        static readonly int MarkCountId = Shader.PropertyToID("_BruiseCount");
        static readonly int FlushId = Shader.PropertyToID("_Flush");
        static readonly int RippleId = Shader.PropertyToID("_Ripple");
        static readonly int RippleStrengthId = Shader.PropertyToID("_RippleStrength");
        static readonly int LoadId = Shader.PropertyToID("_Load");
        static readonly int LoadCountId = Shader.PropertyToID("_LoadCount");
        [Tooltip("A clean punch of this impulse, N s, sends a full-strength ripple over the skin.")]
        public float rippleImpulse = 12f;
        readonly Vector4[] _load = new Vector4[12];
        Vector3 _ripplePoint;
        float _rippleAt = -10f, _rippleStrength;

        MaterialPropertyBlock _block;
        BodyPart[] _source;
        bool _visible = true;
        readonly Vector4[] _marks = new Vector4[Fighter.MaxBruises];

        Transform _neck;
        Quaternion _neckRest;
        bool _neckSought;
        Vector3 _snap, _snapRate;           // the head's turn and its rate, as world rotation vectors (degrees)

        public bool Visible => _visible;

        void OnEnable() => SimBus.Hit += OnHit;
        void OnDisable() => SimBus.Hit -= OnHit;

        void OnHit(HitEvent e)
        {
            if (e.victim != fighter || fighter == null) return;
            if (e.clean)
            {
                _ripplePoint = e.point;
                _rippleAt = Time.time;
                _rippleStrength = Mathf.Clamp(e.impulse / rippleImpulse, 0.3f, 1.5f);
            }
            // Away from the glove; a punch from below (an uppercut) tips the head back, away from the puncher.
            Vector3 away = Vector3.ProjectOnPlane(-e.normal, Vector3.up);
            if (away.sqrMagnitude < 0.09f && e.attacker != null) away = Vector3.ProjectOnPlane(HeadPoint - e.attacker.HeadPosition, Vector3.up);
            if (away.sqrMagnitude < 1e-6f) return;
            float share = e.zone == PartKind.Head ? 1f : e.clean ? 0.33f : 0.1f;
            _snapRate += Vector3.Cross(Vector3.up, away.normalized) * (snapPerSpeed * share * e.gloveSpeed);
        }

        /// <summary>The first bone of the mesh called neck (or, failing that, head): what the head is turned by.</summary>
        void FindNeck()
        {
            _neckSought = true;
            foreach (string word in new[] { "neck", "head" })
                foreach (Renderer r in extraRenderers)
                    if (r is SkinnedMeshRenderer smr && smr.rootBone != null)
                        foreach (Transform t in smr.rootBone.GetComponentsInChildren<Transform>(true))
                            if (t.name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0 && t.parent != null)
                            {
                                _neck = t;
                                _neckRest = t.localRotation;
                                return;
                            }
        }

        void TurnHead(float dt)
        {
            if (!_neckSought) FindNeck();
            if (_neck == null) return;
            // A damped spring back to straight, in steps short enough to stay steady at a low frame rate.
            float w = 2f * Mathf.PI * snapHz;
            for (float left = dt; left > 0f; left -= 0.01f)
            {
                float h = Mathf.Min(0.01f, left);
                _snapRate += (-w * w * _snap - 2f * snapDamping * w * _snapRate) * h;
                _snap += _snapRate * h;
            }
            _snap = Vector3.ClampMagnitude(_snap, snapMax);

            float spent = 1f - fighter.Stamina;
            Mj.MjBoxer boxer = fighter.boxer;
            if (boxer != null && boxer.Bound && boxer.Cfg.fatigue_j > 0.0) spent = Mathf.Max(spent, 1f - (float)boxer.Reserve);
            float droop = tiredDroop * Mathf.Max(Mathf.InverseLerp(0.5f, 1f, spent), fighter.Daze01);
            Vector3 forward = boxer != null && boxer.Bound ? boxer.Forward : transform.forward;
            Vector3 turn = _snap + Vector3.Cross(Vector3.up, forward) * droop;

            _neck.localRotation = _neckRest;
            if (turn.sqrMagnitude > 1e-4f) _neck.rotation = Quaternion.AngleAxis(turn.magnitude, turn.normalized) * _neck.rotation;
        }
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
            if (Application.isPlaying) TurnHead(Time.deltaTime);

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
            Mj.MjBoxer boxer = fighter.boxer;
            if (boxer != null && boxer.Cfg != null && boxer.Cfg.fatigue_j > 0.0) sweat = Mathf.Max(sweat, 1f - (float)boxer.Reserve);
            // The face: where it is, and how flushed (the health lost, the first half of it showing most).
            Vector3 head = HeadPoint;
            var flush = new Vector4(head.x, head.y, head.z, Mathf.Sqrt(Mathf.Clamp01(1f - fighter.Health / 100f)));
            for (int i = 0; i < parts.Length; i++)
            {
                Renderer[] rs = parts[i].renderers;
                if (rs.Length == 0) continue;
                _block.Clear();
                _block.SetFloat(BruiseId, _source[i] != null ? _source[i].bruise : 0f);
                _block.SetFloat(SweatId, sweat);
                _block.SetColor(RimId, rimColor);
                _block.SetVector(FlushId, flush);
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
            _block.SetVector(FlushId, flush);
            // The ripple of the last clean punch, and how hard each joint is working (its link's origin is the joint).
            _block.SetVector(RippleId, new Vector4(_ripplePoint.x, _ripplePoint.y, _ripplePoint.z, Time.time - _rippleAt));
            _block.SetFloat(RippleStrengthId, _rippleStrength);
            int joints = 0;
            foreach (BodyPart p in fighter.parts)
            {
                if (p == null || joints == _load.Length) continue;
                Vector3 at = p.transform.position;
                _load[joints++] = new Vector4(at.x, at.y, at.z, p.stress);
            }
            for (int i = joints; i < _load.Length; i++) _load[i] = Vector4.zero;
            _block.SetVectorArray(LoadId, _load);
            _block.SetFloat(LoadCountId, joints);
            for (int r = 0; r < overlayRenderers.Length; r++)
                if (overlayRenderers[r] != null) overlayRenderers[r].SetPropertyBlock(_block);
        }
    }
}
