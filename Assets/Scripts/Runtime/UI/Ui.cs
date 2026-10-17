using UnityEngine;
using UnityEngine.UIElements;
using PoBox.Diag;

namespace PoBox.UI
{
    /// <summary>What the three screens (HudView, MeleeView, MenuView) all do to their documents.</summary>
    public static class Ui
    {
        /// <summary>Writes the text only when it changed: text made every frame is garbage every frame.</summary>
        public static void Set(TextElement e, string text)
        {
            if (e != null && e.text != text) e.text = text;
        }

        /// <summary>
        /// The frame's furniture as the settings have it: with STATS off the document's root is "plain" and every
        /// element marked "stats-only" (the frame rate, DEBUG, the version, the numbers) is out of the layout.
        /// </summary>
        public static void Frame(VisualElement root)
        {
            if (root == null) return;
            root.EnableInClassList("plain", !SettingsPage.Stats);
            Set(root.Q<Label>("version"), "v" + Application.version);
        }

        /// <summary>The frame rate on its chip and DEBUG's dot, both coloured by how far over budget the frame is.</summary>
        public static void FrameRate(Button fps, VisualElement dot)
        {
            PerfTelemetry perf = PerfTelemetry.Instance;
            if (perf == null) return;
            Set(fps, $"{perf.Fps:0} FPS");
            fps?.EnableInClassList("frame-fps--warn", perf.Grade == 1);
            fps?.EnableInClassList("frame-fps--bad", perf.Grade == 2);
            dot?.EnableInClassList("dot--warn", perf.Grade == 1);
            dot?.EnableInClassList("dot--bad", perf.Grade == 2);
        }
    }
}
