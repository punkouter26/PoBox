using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoBox.UI
{
    /// <summary>
    /// The one-screen rule, as something that can fail. Walks a live panel and reports anything that breaks
    /// it: an element drawn outside the screen, a scroll view of any kind, text under the legibility floor,
    /// a label whose words do not fit its box, and each of the five frame anchors not being in its corner.
    /// The play-mode test runs it at three phone shapes; the editor capture tool prints it beside each picture.
    /// </summary>
    public static class LayoutAudit
    {
        /// <summary>Smallest text allowed, in panel units at the 1080-wide reference (about 10 sp on a phone).</summary>
        public const float MinFontSize = 26f;

        public class Report
        {
            public float width, height;
            public int elements;
            public readonly List<string> offScreen = new List<string>();
            public readonly List<string> scroll = new List<string>();
            public readonly List<string> smallText = new List<string>();
            public readonly List<string> clipped = new List<string>();
            public readonly List<string> anchors = new List<string>();

            public bool Passed => offScreen.Count == 0 && scroll.Count == 0 && smallText.Count == 0 && clipped.Count == 0 && anchors.Count == 0;

            public string Summary()
            {
                var s = new StringBuilder();
                s.Append($"{width:0}x{height:0} elements={elements} offscreen={offScreen.Count} scroll={scroll.Count} small={smallText.Count} clipped={clipped.Count} anchors={(anchors.Count == 0 ? "ok" : "BAD")}");
                void Dump(string label, List<string> items)
                {
                    foreach (string i in items) s.Append($"\n  {label}: {i}");
                }
                Dump("offscreen", offScreen); Dump("scroll", scroll); Dump("small", smallText); Dump("clipped", clipped); Dump("anchor", anchors);
                return s.ToString();
            }
        }

        /// <summary>The anchors the first screen has: it carries no frame rate, menu or debug chip.</summary>
        public static readonly string[] MenuAnchors = { "title", "version" };

        /// <param name="anchors">Which of the five frame anchors this screen is expected to have. Null is all of them.</param>
        public static Report Check(VisualElement root, string[] anchors = null)
        {
            var report = new Report();
            if (root == null || root.panel == null) { report.anchors.Add("no panel"); return report; }

            Rect screen = root.panel.visualTree.worldBound;
            report.width = screen.width;
            report.height = screen.height;
            Walk(root, screen, report);

            // The corners are the corners of the safe area: on a phone with a notch the frame sits inside it.
            VisualElement safe = root.Q<VisualElement>("safe");
            Rect frame = safe != null && safe.worldBound.width > 1f ? safe.worldBound : screen;
            bool Has(string name) => anchors == null || System.Array.IndexOf(anchors, name) >= 0;
            if (Has("title")) Anchor(root, "title", frame, report, left: true, top: true);
            if (Has("fps")) Anchor(root, "fps", frame, report, centre: true, top: true);
            if (Has("menu")) Anchor(root, "menu", frame, report, right: true, top: true);
            if (Has("debug")) Anchor(root, "debug", frame, report, left: true, bottom: true);
            if (Has("version")) Anchor(root, "version", frame, report, right: true, bottom: true);
            return report;
        }

        static void Walk(VisualElement e, Rect screen, Report report)
        {
            if (e.resolvedStyle.display == DisplayStyle.None) return;
            report.elements++;

            if (e is ScrollView) report.scroll.Add(Path(e));

            bool drawn = e.resolvedStyle.visibility == Visibility.Visible && e.resolvedStyle.opacity > 0.01f;
            Rect b = e.worldBound;
            if (drawn && b.width > 0.5f && b.height > 0.5f)
            {
                const float slack = 1f;
                if (b.xMin < screen.xMin - slack || b.yMin < screen.yMin - slack || b.xMax > screen.xMax + slack || b.yMax > screen.yMax + slack)
                    report.offScreen.Add($"{Path(e)} [{b.xMin:0},{b.yMin:0} {b.width:0}x{b.height:0}]");

                if (e is TextElement t && !string.IsNullOrEmpty(t.text))
                {
                    float size = t.resolvedStyle.fontSize;
                    if (size < MinFontSize - 0.01f) report.smallText.Add($"{Path(e)} {size:0.#}px '{t.text}'");

                    // A single-line label wider than its box is cut off; a wrapping one is judged on height.
                    Rect content = t.contentRect;
                    bool wraps = t.resolvedStyle.whiteSpace == WhiteSpace.Normal;
                    Vector2 need = t.MeasureTextSize(t.text, wraps ? content.width : 0f, wraps ? VisualElement.MeasureMode.Exactly : VisualElement.MeasureMode.Undefined,
                                                     0f, VisualElement.MeasureMode.Undefined);
                    if (need.x > content.width + 1.5f || need.y > content.height + 1.5f)
                        report.clipped.Add($"{Path(e)} needs {need.x:0}x{need.y:0} has {content.width:0}x{content.height:0} '{t.text}'");
                }
            }

            for (int i = 0; i < e.hierarchy.childCount; i++) Walk(e.hierarchy[i], screen, report);
        }

        static void Anchor(VisualElement root, string name, Rect screen, Report report,
                           bool left = false, bool right = false, bool centre = false, bool top = false, bool bottom = false)
        {
            VisualElement e = root.Q<VisualElement>(name);
            if (e == null) { report.anchors.Add($"{name}: missing"); return; }
            Rect b = e.worldBound;
            float edge = screen.width * 0.08f, band = screen.height * 0.08f;

            if (left && b.xMin - screen.xMin > edge) report.anchors.Add($"{name}: not at the left edge (x={b.xMin:0})");
            if (right && screen.xMax - b.xMax > edge) report.anchors.Add($"{name}: not at the right edge (xMax={b.xMax:0})");
            if (centre && Mathf.Abs(b.center.x - screen.center.x) > screen.width * 0.03f) report.anchors.Add($"{name}: not centred (cx={b.center.x:0})");
            if (top && b.yMax - screen.yMin > band) report.anchors.Add($"{name}: not in the top row (yMax={b.yMax:0})");
            if (bottom && screen.yMax - b.yMin > band) report.anchors.Add($"{name}: not in the bottom row (y={b.yMin:0})");
        }

        static string Path(VisualElement e)
        {
            string name = string.IsNullOrEmpty(e.name) ? e.GetType().Name : e.name;
            VisualElement p = e.hierarchy.parent;
            while (p != null && string.IsNullOrEmpty(p.name)) p = p.hierarchy.parent;
            return p != null ? p.name + "/" + name : name;
        }
    }
}
