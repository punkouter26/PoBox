using UnityEngine;
using UnityEngine.UIElements;

namespace PoBox.UI
{
    public enum GlyphKind { Glove, Burst, Bolt, Heat, Balance, Power, Shield, Daze, Menu, Close }

    /// <summary>
    /// The small icons that stand in for words on the tale of the tape: a glove for punches landed, a burst
    /// for the hardest hit, a bolt for energy, and so on. Drawn as vectors so they are sharp at any
    /// resolution and take the text colour, and so the game ships no icon it had to licence.
    /// </summary>
    [UxmlElement]
    public partial class Glyph : VisualElement
    {
        GlyphKind _kind;

        [UxmlAttribute]
        public GlyphKind kind
        {
            get => _kind;
            set { _kind = value; MarkDirtyRepaint(); }
        }

        public Glyph()
        {
            generateVisualContent += Draw;
            pickingMode = PickingMode.Ignore;
        }

        void Draw(MeshGenerationContext ctx)
        {
            Rect r = contentRect;
            float s = Mathf.Min(r.width, r.height);
            if (s < 4f) return;
            Vector2 c = r.center;
            float u = s / 2f;   // half extent
            Painter2D p = ctx.painter2D;
            Color ink = resolvedStyle.color;
            p.fillColor = ink;
            p.strokeColor = ink;
            p.lineWidth = Mathf.Max(3f, s * 0.11f);
            p.lineCap = LineCap.Round;
            p.lineJoin = LineJoin.Round;

            Vector2 P(float x, float y) => c + new Vector2(x, y) * u;

            switch (_kind)
            {
                case GlyphKind.Glove:
                    // A fist: a round mitt, a thumb, a cuff.
                    p.BeginPath(); p.Arc(P(0.05f, -0.2f), u * 0.62f, 0f, 360f); p.Fill();
                    p.BeginPath(); p.Arc(P(-0.55f, 0.05f), u * 0.28f, 0f, 360f); p.Fill();
                    p.BeginPath();
                    p.MoveTo(P(-0.35f, 0.45f)); p.LineTo(P(0.45f, 0.45f)); p.LineTo(P(0.45f, 0.9f)); p.LineTo(P(-0.35f, 0.9f));
                    p.ClosePath(); p.Fill();
                    break;

                case GlyphKind.Burst:
                    // An eight-point impact star.
                    p.BeginPath();
                    for (int i = 0; i < 16; i++)
                    {
                        float a = i * Mathf.PI / 8f;
                        float rad = (i % 2 == 0 ? 0.95f : 0.42f);
                        Vector2 v = P(Mathf.Sin(a) * rad, -Mathf.Cos(a) * rad);
                        if (i == 0) p.MoveTo(v); else p.LineTo(v);
                    }
                    p.ClosePath(); p.Fill();
                    break;

                case GlyphKind.Bolt:
                    p.BeginPath();
                    p.MoveTo(P(0.25f, -0.95f)); p.LineTo(P(-0.55f, 0.1f)); p.LineTo(P(-0.05f, 0.1f));
                    p.LineTo(P(-0.25f, 0.95f)); p.LineTo(P(0.55f, -0.2f)); p.LineTo(P(0.05f, -0.2f));
                    p.ClosePath(); p.Fill();
                    break;

                case GlyphKind.Heat:
                    // A joint under load: a hinge of two bars with a hot dot on the pivot.
                    p.BeginPath(); p.MoveTo(P(-0.8f, 0.75f)); p.LineTo(P(0f, 0f)); p.LineTo(P(0.8f, 0.75f)); p.Stroke();
                    p.BeginPath(); p.Arc(P(0f, -0.1f), u * 0.36f, 0f, 360f); p.Fill();
                    p.BeginPath(); p.MoveTo(P(0f, -0.95f)); p.LineTo(P(0f, -0.65f)); p.Stroke();
                    p.BeginPath(); p.MoveTo(P(-0.6f, -0.7f)); p.LineTo(P(-0.42f, -0.5f)); p.Stroke();
                    p.BeginPath(); p.MoveTo(P(0.6f, -0.7f)); p.LineTo(P(0.42f, -0.5f)); p.Stroke();
                    break;

                case GlyphKind.Balance:
                    // A beam on a pivot.
                    p.BeginPath(); p.MoveTo(P(-0.9f, -0.25f)); p.LineTo(P(0.9f, -0.55f)); p.Stroke();
                    p.BeginPath(); p.MoveTo(P(0f, -0.35f)); p.LineTo(P(-0.5f, 0.85f)); p.LineTo(P(0.5f, 0.85f)); p.ClosePath(); p.Fill();
                    break;

                case GlyphKind.Shield:
                    // A shield: punches stopped on the guard.
                    p.BeginPath();
                    p.MoveTo(P(-0.75f, -0.8f)); p.LineTo(P(0.75f, -0.8f)); p.LineTo(P(0.75f, 0.05f));
                    p.LineTo(P(0f, 0.95f)); p.LineTo(P(-0.75f, 0.05f));
                    p.ClosePath(); p.Fill();
                    break;

                case GlyphKind.Daze:
                    // Seeing stars: a ring with three dots going round it.
                    p.BeginPath(); p.Arc(P(0f, 0f), u * 0.5f, 0f, 360f); p.Stroke();
                    for (int i = 0; i < 3; i++)
                    {
                        float a = i * Mathf.PI * 2f / 3f - 0.5f;
                        p.BeginPath(); p.Arc(P(Mathf.Sin(a) * 0.82f, -Mathf.Cos(a) * 0.82f), u * 0.17f, 0f, 360f); p.Fill();
                    }
                    break;

                case GlyphKind.Menu:
                    // Three bars.
                    for (int i = -1; i <= 1; i++)
                    {
                        p.BeginPath(); p.MoveTo(P(-0.8f, i * 0.55f)); p.LineTo(P(0.8f, i * 0.55f)); p.Stroke();
                    }
                    break;

                case GlyphKind.Close:
                    p.BeginPath(); p.MoveTo(P(-0.65f, -0.65f)); p.LineTo(P(0.65f, 0.65f)); p.Stroke();
                    p.BeginPath(); p.MoveTo(P(0.65f, -0.65f)); p.LineTo(P(-0.65f, 0.65f)); p.Stroke();
                    break;

                default: // Power: a gauge
                    p.BeginPath(); p.Arc(P(0f, 0.35f), u * 0.85f, 180f, 360f); p.Stroke();
                    p.BeginPath(); p.MoveTo(P(0f, 0.35f)); p.LineTo(P(0.45f, -0.3f)); p.Stroke();
                    p.BeginPath(); p.Arc(P(0f, 0.35f), u * 0.16f, 0f, 360f); p.Fill();
                    break;
            }
        }
    }
}
