using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoBox.UI
{
    /// <summary>
    /// The one-screen rule, as something that can fail. Walks a live panel and reports anything that breaks
    /// it: an element drawn outside the screen, a scroll view of any kind, text under the legibility floor,
    /// a label whose words do not fit its box (one cut short with an ellipsis fits by design), a button smaller
    /// than a thumb, two absolutely positioned neighbours drawn over each other, and any of the five frame
    /// anchors every screen has (title, fps, menu, debug, version) missing or not in its corner, or the version
    /// not saying which build this is. Two compaction rules: the way on (FIGHT, NEXT, AGAIN, MENU, REPLAY,
    /// SHARE) sits in the thumb's reach, the bottom 40% of the safe area; and the word beside a button's icon
    /// is one word of at most eight letters (a count or dots after it are fine), so a longer idea is an icon.
    /// And nothing in the flow spills out of the top or bottom of its parent, which is how a fixed-height page with
    /// too much in it shows: squeezed rows drawn over each other, with nothing off the screen to give it away.
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
        /// <summary>The top of the thumb's reach, as a share of the safe area's height from its top.</summary>
        public const float ReachTop = 0.6f;
        /// <summary>The most letters a button's word may have.</summary>
        public const int MaxWord = 8;
        /// <summary>The buttons that carry on or leave (on the first screen, the arena's card, the free-for-all's).</summary>
        static readonly string[] Primary = { "fight", "melee", "career", "res-next", "res-boxers", "res-replay", "res-share", "again", "leave", "replay", "share" };
        /// <summary>The classes of a button's own words.</summary>
        static readonly string[] WordClasses = { "btn-word", "thumb-word", "thumb-sub" };

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
            public readonly List<string> reach = new List<string>();
            public readonly List<string> wordy = new List<string>();
            public readonly List<string> spill = new List<string>();

            public bool Passed => offScreen.Count == 0 && scroll.Count == 0 && smallText.Count == 0 && clipped.Count == 0 && anchors.Count == 0
                                  && smallTouch.Count == 0 && overlaps.Count == 0 && reach.Count == 0 && wordy.Count == 0 && spill.Count == 0;

            public string Summary()
            {
                var s = new StringBuilder();
                s.Append($"{width:0}x{height:0} elements={elements} offscreen={offScreen.Count} scroll={scroll.Count} small={smallText.Count} clipped={clipped.Count} touch={smallTouch.Count} overlap={overlaps.Count} reach={reach.Count} wordy={wordy.Count} spill={spill.Count} anchors={(anchors.Count == 0 ? "ok" : "BAD")}");
                void Dump(string label, List<string> items)
                {
                    foreach (string i in items) s.Append($"\n  {label}: {i}");
                }
                Dump("offscreen", offScreen); Dump("scroll", scroll); Dump("small", smallText); Dump("clipped", clipped); Dump("touch", smallTouch); Dump("overlap", overlaps); Dump("reach", reach); Dump("wordy", wordy); Dump("spill", spill); Dump("anchor", anchors);
                return s.ToString();
            }
        }

        public static Report Check(VisualElement root)
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
            Anchor(root, "title", frame, report, left: true, top: true);
            Anchor(root, "fps", frame, report, centre: true, top: true);
            Anchor(root, "menu", frame, report, right: true, top: true);
            Anchor(root, "debug", frame, report, left: true, bottom: true);
            Anchor(root, "version", frame, report, right: true, bottom: true);
            foreach (string name in Primary)
            {
                VisualElement b = root.Q<VisualElement>(name);
                if (b == null || !Shown(b)) continue;
                float top = frame.yMin + frame.height * ReachTop;
                if (b.worldBound.center.y < top) report.reach.Add($"{name}: centre at y={b.worldBound.center.y:0}, above the thumb's reach (y={top:0})");
            }
            if (root.Q<VisualElement>("version") is TextElement version && !version.text.Contains(Application.version))
                report.anchors.Add($"version: says '{version.text}', not {Application.version}");
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
                    if (Wordy(t)) report.wordy.Add($"{Path(e)} '{t.text}': one word of at most {MaxWord} letters beside an icon");

                    // A single-line label wider than its box is cut off; a wrapping one is judged on height. One that
                    // ends in an ellipsis when it is too long (a name) is cut short on purpose and only its height counts.
                    Rect content = t.contentRect;
                    bool wraps = t.resolvedStyle.whiteSpace == WhiteSpace.Normal;
                    bool clamped = !wraps && t.resolvedStyle.textOverflow == TextOverflow.Ellipsis;
                    Vector2 need = t.MeasureTextSize(t.text, wraps ? content.width : 0f, wraps ? VisualElement.MeasureMode.Exactly : VisualElement.MeasureMode.Undefined,
                                                     0f, VisualElement.MeasureMode.Undefined);
                    if ((need.x > content.width + 1.5f && !clamped) || need.y > content.height + 1.5f)
                        report.clipped.Add($"{Path(e)} needs {need.x:0}x{need.y:0} has {content.width:0}x{content.height:0} '{t.text}'");
                }

                // In the flow and out of its parent's top or bottom: the parent was squeezed below what it holds, and
                // what follows it is drawn over it (a page with more rows than its height).
                VisualElement parent = e.hierarchy.parent;
                if (parent != null && e.resolvedStyle.position == Position.Relative && parent.worldBound.height > 0.5f
                    && (b.yMax > parent.worldBound.yMax + 1.5f || b.yMin < parent.worldBound.yMin - 1.5f))
                    report.spill.Add($"{Path(e)} [{b.yMin:0}-{b.yMax:0}] out of {Path(parent)} [{parent.worldBound.yMin:0}-{parent.worldBound.yMax:0}]");

                if (e is Button && e.enabledInHierarchy && e.pickingMode == PickingMode.Position && (b.width < MinTouch - 1.5f || b.height < MinTouch - 1.5f))   // one device pixel of snapping at 720 wide
                    report.smallTouch.Add($"{Path(e)} {b.width:0}x{b.height:0}");
            }

            for (int i = 0; i < e.hierarchy.childCount; i++) Walk(e.hierarchy[i], screen, report);
        }

        /// <summary>A button's word that is more than one word, or one longer than <see cref="MaxWord"/>; numbers and dots do not count.</summary>
        static bool Wordy(TextElement t)
        {
            bool isWord = false;
            foreach (string c in WordClasses) isWord |= t.ClassListContains(c);
            return isWord && Wordy(t.text);
        }

        public static bool Wordy(string text)
        {
            int words = 0;
            foreach (string token in text.Split(' '))
            {
                int letters = 0;
                foreach (char ch in token) if (char.IsLetter(ch)) letters++;
                if (letters == 0) continue;
                if (++words > 1 || letters > MaxWord) return true;
            }
            return false;
        }

        /// <summary>Neither it nor anything it is in is switched off.</summary>
        static bool Shown(VisualElement e)
        {
            for (; e != null; e = e.hierarchy.parent)
                if (e.resolvedStyle.display == DisplayStyle.None || e.resolvedStyle.visibility != Visibility.Visible) return false;
            return true;
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
            // The frame rate, DEBUG and the version are only there with the settings' STATS (Ui.Frame): out of the layout, nothing to place.
            if (e.ClassListContains("stats-only") && e.resolvedStyle.display == DisplayStyle.None) return;
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
