using System.Collections.Generic;
using UnityEngine;
using PoBox.Sim;

namespace PoBox.Fx
{
    /// <summary>
    /// A boxer's balance, drawn on the canvas under it: the outline of the patch its planted feet cover, and
    /// a dot at the capture point, where the body will come to rest if it does nothing about the way it is
    /// moving. The same picture as the gauge in the HUD's pod (<see cref="UI.BalanceGauge"/>), and the same
    /// colours: green with the dot well inside the patch, amber near its edge, red outside it. Nothing is
    /// drawn for a boxer that is down or has no foot on the canvas.
    /// </summary>
    public class BalanceMarks : MonoBehaviour
    {
        public Bout bout;
        [Tooltip("0 red corner, 1 blue corner.")]
        public int corner;
        [Tooltip("The outline of the feet: a closed line, in world space, lying flat.")]
        public LineRenderer patch;
        [Tooltip("The capture point: a line too short to be anything but its round ends.")]
        public LineRenderer point;
        [Tooltip("Height the marks are drawn at: just over the canvas and what is painted on it.")]
        public float y = 0.02f;
        public Color good = new Color(0.18f, 0.75f, 0.55f);
        public Color watch = new Color(1f, 0.78f, 0.33f);
        public Color bad = new Color(1f, 0.42f, 0.36f);

        void LateUpdate()
        {
            Fighter f = bout == null ? null : corner == 1 ? bout.blue : bout.red;
            IReadOnlyList<Vector2> hull = f != null ? f.Support : null;
            bool show = hull != null && hull.Count >= 3 && Bout.SimRunning && !f.IsDown;
            if (patch.enabled != show) patch.enabled = point.enabled = show;
            if (!show) return;

            patch.positionCount = hull.Count;
            for (int i = 0; i < hull.Count; i++) patch.SetPosition(i, new Vector3(hull[i].x, y, hull[i].y));
            var at = new Vector3(f.CapturePoint.x, y + 0.005f, f.CapturePoint.y);
            point.SetPosition(0, at);
            point.SetPosition(1, at + Vector3.right * 0.001f);

            Color c = f.BalanceMargin < 0.15f ? bad : f.BalanceMargin < 0.5f ? watch : good;
            patch.startColor = patch.endColor = point.startColor = point.endColor = c;
        }
    }
}
