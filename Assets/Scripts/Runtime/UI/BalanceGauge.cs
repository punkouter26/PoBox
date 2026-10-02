using UnityEngine;
using UnityEngine.UIElements;

namespace PoBox.UI
{
    /// <summary>
    /// A fighter's balance, seen from above. Two feet, and a dot for the capture point: where the body will
    /// come to rest if it does nothing about the way it is moving. While the dot sits between the feet the
    /// fighter is planted; as it slides towards the edge the dot goes amber, and outside the feet it is red
    /// and only a step, or the canvas, is left. It moves before the fall does, which is the point of it.
    /// The fighter faces up the screen.
    /// </summary>
    [UxmlElement]
    public partial class BalanceGauge : VisualElement
    {
        readonly Vector2[] _foot = { new Vector2(-0.15f, 0f), new Vector2(0.15f, 0f) };
        readonly bool[] _down = { true, true };
        Vector2 _capture;
        float _margin = 1f;
        bool _fallen;

        public Color footColor = new Color(1f, 1f, 1f, 0.55f);
        public Color liftedColor = new Color(1f, 1f, 1f, 0.18f);
        public Color good = new Color(0.18f, 0.75f, 0.55f);
        public Color watch = new Color(1f, 0.78f, 0.33f);
        public Color bad = new Color(1f, 0.42f, 0.36f);
        /// <summary>Metres from the middle of the gauge to its side edge.</summary>
        public float halfSpan = 0.42f;

        public BalanceGauge()
        {
            generateVisualContent += Draw;
            pickingMode = PickingMode.Ignore;
        }

        /// <param name="feet">Each foot from the middle of the two, metres: x to the fighter's right, y ahead.</param>
        /// <param name="capture">The capture point in the same frame.</param>
        /// <param name="margin">1 planted, 0 on the edge.</param>
        public void Set(Vector2[] feet, bool[] down, Vector2 capture, float margin, bool fallen)
        {
            bool changed = fallen != _fallen || Mathf.Abs(margin - _margin) > 0.02f || (capture - _capture).sqrMagnitude > 0.0001f;
            for (int i = 0; i < 2; i++)
                if (down[i] != _down[i] || (feet[i] - _foot[i]).sqrMagnitude > 0.0001f) changed = true;
            if (!changed) return;
            for (int i = 0; i < 2; i++) { _foot[i] = feet[i]; _down[i] = down[i]; }
            _capture = capture;
            _margin = margin;
            _fallen = fallen;
            MarkDirtyRepaint();
        }

        void Draw(MeshGenerationContext ctx)
        {
            Rect r = contentRect;
            if (r.width < 8f || r.height < 8f) return;
            Painter2D p = ctx.painter2D;
            float scale = r.width * 0.5f / halfSpan;                    // pixels a metre
            Vector2 c = r.center;
            // Ahead of the fighter is up the screen.
            Vector2 P(Vector2 m) => new Vector2(
                Mathf.Clamp(c.x + m.x * scale, r.xMin + 6f, r.xMax - 6f),
                Mathf.Clamp(c.y - m.y * scale, r.yMin + 6f, r.yMax - 6f));

            // The feet: a sole is about 26 cm long and 9 wide.
            for (int i = 0; i < 2; i++)
            {
                p.strokeColor = _fallen ? liftedColor : _down[i] ? footColor : liftedColor;
                p.lineWidth = 0.09f * scale;
                p.lineCap = LineCap.Round;
                p.BeginPath();
                p.MoveTo(P(_foot[i] + new Vector2(0f, -0.085f)));
                p.LineTo(P(_foot[i] + new Vector2(0f, 0.085f)));
                p.Stroke();
            }

            Color dot = _fallen || _margin < 0.15f ? bad : _margin < 0.5f ? watch : good;
            Vector2 at = P(_capture);
            p.fillColor = new Color(dot.r, dot.g, dot.b, 0.28f);
            p.BeginPath();
            p.Arc(at, 15f, 0f, 360f);
            p.Fill();
            p.fillColor = dot;
            p.BeginPath();
            p.Arc(at, 8f, 0f, 360f);
            p.Fill();
        }
    }
}
