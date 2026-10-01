using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoBox.UI
{
    /// <summary>
    /// A line graph drawn with the UI Toolkit vector painter: no texture, no package, one draw.
    /// The caller hands it a sampler and a range; it keeps its own copy so a repaint never reads a buffer
    /// that is being written.
    /// </summary>
    [UxmlElement]
    public partial class Sparkline : VisualElement
    {
        float[] _values = new float[0];
        int _count;
        float _min, _max = 1f;
        readonly List<float> _markers = new List<float>();

        public Color lineColor = new Color(0.18f, 0.75f, 0.55f);
        public Color fillColor = new Color(0.18f, 0.75f, 0.55f, 0.18f);
        public Color markerColor = new Color(1f, 0.78f, 0.33f);
        /// <summary>A horizontal reference line at this value. NaN for none.</summary>
        public float baseline = float.NaN;
        /// <summary>Fill between the line and the baseline rather than the floor.</summary>
        public bool fillFromBaseline;

        public Sparkline()
        {
            generateVisualContent += Draw;
            pickingMode = PickingMode.Ignore;
        }

        public void Set(Func<int, float> sampler, int count, float min, float max)
        {
            if (_values.Length < count) _values = new float[count];
            for (int i = 0; i < count; i++) _values[i] = sampler(i);
            _count = count;
            _min = min;
            _max = Mathf.Max(min + 1e-4f, max);
            MarkDirtyRepaint();
        }

        /// <summary>Vertical ticks at positions along the graph, 0..1. The highlight moments.</summary>
        public void SetMarkers(IEnumerable<float> positions)
        {
            _markers.Clear();
            if (positions != null) _markers.AddRange(positions);
            MarkDirtyRepaint();
        }

        void Draw(MeshGenerationContext ctx)
        {
            Rect r = contentRect;
            if (r.width < 4f || r.height < 4f) return;
            Painter2D p = ctx.painter2D;

            float Y(float v) => r.yMax - Mathf.Clamp01((v - _min) / (_max - _min)) * r.height;

            if (!float.IsNaN(baseline))
            {
                p.strokeColor = new Color(1f, 1f, 1f, 0.22f);
                p.lineWidth = 2f;
                p.BeginPath();
                p.MoveTo(new Vector2(r.xMin, Y(baseline)));
                p.LineTo(new Vector2(r.xMax, Y(baseline)));
                p.Stroke();
            }

            if (_count >= 2)
            {
                float step = r.width / (_count - 1);
                float floor = fillFromBaseline && !float.IsNaN(baseline) ? Y(baseline) : r.yMax;

                p.fillColor = fillColor;
                p.BeginPath();
                p.MoveTo(new Vector2(r.xMin, floor));
                for (int i = 0; i < _count; i++) p.LineTo(new Vector2(r.xMin + i * step, Y(_values[i])));
                p.LineTo(new Vector2(r.xMin + (_count - 1) * step, floor));
                p.ClosePath();
                p.Fill();

                p.strokeColor = lineColor;
                p.lineWidth = 4f;
                p.lineJoin = LineJoin.Round;
                p.lineCap = LineCap.Round;
                p.BeginPath();
                p.MoveTo(new Vector2(r.xMin, Y(_values[0])));
                for (int i = 1; i < _count; i++) p.LineTo(new Vector2(r.xMin + i * step, Y(_values[i])));
                p.Stroke();
            }

            if (_markers.Count > 0)
            {
                p.strokeColor = markerColor;
                p.lineWidth = 4f;
                foreach (float m in _markers)
                {
                    float x = r.xMin + Mathf.Clamp01(m) * r.width;
                    p.BeginPath();
                    p.MoveTo(new Vector2(x, r.yMin));
                    p.LineTo(new Vector2(x, r.yMax));
                    p.Stroke();
                }
            }
        }
    }
}
