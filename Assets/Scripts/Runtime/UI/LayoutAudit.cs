using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoBox.UI
{
    /// <summary>
    /// The one-screen rule, as something that can fail. Walks a live panel and reports anything that breaks
    /// it: an element drawn outside the screen, a scroll view of any kind, text under the legibility floor,
    /// a label whose words do not fit its box, a button smaller than a thumb, two absolutely positioned
    /// neighbours drawn over each other, and each of the five frame anchors not being in its corner.
    ///
    /// Overlap is judged between the screen's layers: the visible, absolutely positioned children of the safe
    /// area (inside a widget, a bar's fill lying over its track is the point of it). A layer that covers its
    /// whole parent is a backdrop (the picture) and is left out; one marked with the class "over" (a sheet,
    /// the debug panel, the punch-speed flash) is meant to lie over the furniture and is only checked against
    /// other such overlays, so two of them open at once is a failure.
    /// The play-mode test runs it at four phone shapes; the editor capture tool prints it beside each picture.
    /// </summary>
    public static class LayoutAudit
    {
        /// <summary>Smallest text allowed, in panel units at the 1080-wide reference (about 10 sp on a phone).</summary>
        public const float MinFontSize = 26f;
        /// <summary>Smallest thing a thumb presses: a 44 pt target at 2x.</summary>
        public const float MinTouch = 88f;
        /// <summary>The class an overlay carries: it may lie over the furniture, never over another overlay.</summary>
        public const string OverClass = "over";

        public class Report
        {
            public float width, height;
            public int elements;
            public readonly List<string> offScreen = new List<string>();
            public readonly List<string> scroll = new List<string>();
            public readonly List<string> smallText = new List<string>();
            public readonly List<string> clipped = new List<string>();
            public readonly List<string> anchors = new List<string>();
            public readonly List<string> smallTouch = new List<string>();
            public readonly List<string> overlaps = new List<string>();

            public bool Passed => offScreen.Count == 0 && scroll.Count == 0 && smallText.Count == 0 && clipped.Count == 0 && anchors.Count == 0
                                  && smallTouch.Count == 0 && overlaps.Count == 0;

            public string Summary()
            {
                var s = new StringBuilder();
                s.Append($"{width:0}x{height:0} elements={elements} offscreen={offScreen.Count} scroll={scroll.Count} small={smallText.Count} clipped={clipped.Count} touch={smallTouch.Count} overlap={overlaps.Count} anchors={(anchors.Count == 0 ? "ok" : "BAD")}");
                void Dump(string label, List<string> items)
                {
                    foreach (string i in items) s.Append($"\n  {label}: {i}");
                }
                Dump("offscreen", offScreen); Dump("scroll", scroll); Dump("small", smallText); Dump("clipped", clipped); Dump("touch", smallTouch); Dump("overlap", overlaps); Dump("anchor", anchors);
                return s.ToString();
            }
        }

        /// <summary>The anchors the first screen has: all five, as the HUD and the testbed do.</summary>
        public static readonly string[] MenuAnchors = { "title", "fps", "menu", "debug", "version" };
        /// <summary>The free-for-all's: the frame rate and the settings (its round chip sits where the title does, and it has no debug or version).</summary>
        public static readonly string[] MeleeAnchors = { "fps", "menu" };

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
            Overlaps(safe ?? root, report);
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

                if (e is Button && e.enabledInHierarchy && e.pickingMode == PickingMode.Position && (b.width < MinTouch - 1.5f || b.height < MinTouch - 1.5f))   // one device pixel of snapping at 720 wide
                    report.smallTouch.Add($"{Path(e)} {b.width:0}x{b.height:0}");
            }

            for (int i = 0; i < e.hierarchy.childCount; i++) Walk(e.hierarchy[i], screen, report);
        }

        static void Overlaps(VisualElement parent, Report report)
        {
            int n = parent.hierarchy.childCount;
            for (int i = 0; i < n; i++)
            {
                VisualElement a = parent.hierarchy[i];
                if (!Layered(a, parent)) continue;
                for (int j = i + 1; j < n; j++)
                {
                    VisualElement b = parent.hierarchy[j];
                    if (!Layered(b, parent) || a.ClassListContains(OverClass) != b.ClassListContains(OverClass)) continue;
                    Rect ra = a.worldBound, rb = b.worldBound;
                    float w = Mathf.Min(ra.xMax, rb.xMax) - Mathf.Max(ra.xMin, rb.xMin), h = Mathf.Min(ra.yMax, rb.yMax) - Mathf.Max(ra.yMin, rb.yMin);
                    if (w > 1f && h > 1f) report.overlaps.Add($"{Path(a)} and {Path(b)} share {w:0}x{h:0}");
                }
            }
        }

        /// <summary>Drawn, absolutely positioned, and not a backdrop the size of its parent.</summary>
        static bool Layered(VisualElement e, VisualElement parent)
        {
            IResolvedStyle s = e.resolvedStyle;
            if (s.position != Position.Absolute || s.display == DisplayStyle.None || s.visibility != Visibility.Visible || s.opacity <= 0.01f) return false;
            Rect b = e.worldBound, p = parent.worldBound;
            if (b.width <= 0.5f || b.height <= 0.5f) return false;
            return b.width < p.width - 1f || b.height < p.height - 1f;
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
