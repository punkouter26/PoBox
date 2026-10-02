using UnityEngine;
using UnityEngine.UIElements;

namespace PoBox.UI
{
    /// <summary>
    /// A fighter seen from the front, each part of it coloured by how much it has been hit: the impulse that
    /// has landed there, in newton-seconds, as a share of the most that has landed anywhere on either
    /// fighter. Cold is grey; then amber; then red. The arms are the guard: heat there is punches stopped.
    /// Round the head runs a ring that fills as the fighter's daze rises, and closes when the legs go.
    /// Drawn with the vector painter: no texture.
    /// </summary>
    [UxmlElement]
    public partial class HitMap : VisualElement
    {
        readonly float[] _heat = new float[5];
        float _daze;
        bool _mirror;

        /// <summary>The fighter's left is drawn on the viewer's right, as when facing it. Mirrored puts it the other way, for the corner on the right.</summary>
        [UxmlAttribute]
        public bool mirror
        {
            get => _mirror;
            set { _mirror = value; MarkDirtyRepaint(); }
        }

        public Color cold = new Color(1f, 1f, 1f, 0.16f);
        public Color warm = new Color(1f, 0.78f, 0.33f, 0.95f);
        public Color hot = new Color(1f, 0.30f, 0.24f, 1f);
        public Color ring = new Color(1f, 0.78f, 0.33f, 1f);

        public HitMap()
        {
            generateVisualContent += Draw;
            pickingMode = PickingMode.Ignore;
        }

        /// <param name="taken">Impulse taken by zone: head, chest, belly, left arm, right arm (Fighter.taken).</param>
        /// <param name="scale">The impulse that is fully hot.</param>
        public void Set(float[] taken, float scale, float daze01)
        {
            bool changed = Mathf.Abs(daze01 - _daze) > 0.01f;
            float full = Mathf.Max(1f, scale);
            for (int i = 0; i < _heat.Length; i++)
                if (Mathf.Abs(Mathf.Clamp01(taken[i] / full) - _heat[i]) > 0.01f) changed = true;
            if (!changed) return;
            for (int i = 0; i < _heat.Length; i++) _heat[i] = Mathf.Clamp01(taken[i] / full);
            _daze = daze01;
            MarkDirtyRepaint();
        }

        Color Heat(float h)
        {
            if (h <= 0.001f) return cold;
            return h < 0.5f ? Color.Lerp(cold, warm, h * 2f) : Color.Lerp(warm, hot, (h - 0.5f) * 2f);
        }

        void Draw(MeshGenerationContext ctx)
        {
            Rect r = contentRect;
            if (r.width < 8f || r.height < 8f) return;
            Painter2D p = ctx.painter2D;
            // The figure is drawn in a box 100 wide and 150 tall, fitted to the element.
            float s = Mathf.Min(r.width / 100f, r.height / 150f);
            var origin = new Vector2(r.center.x - 50f * s, r.center.y - 75f * s);
            Vector2 P(float x, float y) => origin + new Vector2(_mirror ? 100f - x : x, y) * s;

            void Capsule(Vector2 a, Vector2 b, float width, Color colour)
            {
                p.strokeColor = colour;
                p.lineWidth = width * s;
                p.lineCap = LineCap.Round;
                p.BeginPath();
                p.MoveTo(a);
                p.LineTo(b);
                p.Stroke();
            }

            // Legs are never a target: always cold.
            Capsule(P(40f, 100f), P(37f, 142f), 13f, cold);
            Capsule(P(60f, 100f), P(63f, 142f), 13f, cold);

            // Belly, then chest over it.
            Capsule(P(42f, 86f), P(58f, 86f), 22f, Heat(_heat[2]));
            Capsule(P(38f, 60f), P(62f, 60f), 28f, Heat(_heat[1]));

            // Arms in the guard: upper arm down from the shoulder, forearm back up to the chin. The fighter's
            // left arm is on the viewer's right.
            Color left = Heat(_heat[3]), right = Heat(_heat[4]);
            Capsule(P(80f, 52f), P(84f, 78f), 11f, left);
            Capsule(P(84f, 78f), P(70f, 44f), 11f, left);
            Capsule(P(20f, 52f), P(16f, 78f), 11f, right);
            Capsule(P(16f, 78f), P(30f, 44f), 11f, right);

            // Head.
            p.fillColor = Heat(_heat[0]);
            p.BeginPath();
            p.Arc(P(50f, 22f), 15f * s, 0f, 360f);
            p.Fill();

            // The daze ring: an arc from the top, clockwise.
            if (_daze > 0.02f)
            {
                p.strokeColor = _daze > 0.75f ? hot : ring;
                p.lineWidth = 4f * s;
                p.lineCap = LineCap.Butt;
                p.BeginPath();
                p.Arc(P(50f, 22f), 20.5f * s, -90f, -90f + 360f * Mathf.Clamp01(_daze));
                p.Stroke();
            }
        }
    }
}
