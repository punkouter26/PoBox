using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoBox.UI
{
    /// <summary>
    /// The debug panel's parts every screen shares: its cells (a key and a reading each), and the frame-rate chip's
    /// two presses. A tap on the chip opens or closes the panel; a long press on it, or three fingers down at once
    /// anywhere on the screen, is the hidden switch, which the screen uses to open the panel and the X-ray together.
    /// The long press is there for the editor, where there is only one finger.
    /// </summary>
    public static class DebugPanel
    {
        /// <summary>A press held this long on the frame rate is a long press, seconds.</summary>
        public const float LongPress = 0.6f;
        /// <summary>Three fingers come down within this of each other, seconds.</summary>
        const float ThreeFingers = 0.35f;

        /// <summary>Fills the panel with a cell a key; the readings are returned in the order of the keys.</summary>
        public static Label[] Build(VisualElement panel, IList<string> keys)
        {
            var values = new Label[keys.Count];
            if (panel == null) return values;
            panel.Clear();
            for (int i = 0; i < keys.Count; i++)
            {
                var cell = new VisualElement { pickingMode = PickingMode.Ignore };
                cell.AddToClassList("dbg-cell");
                var k = new Label(keys[i]) { pickingMode = PickingMode.Ignore };
                k.AddToClassList("dbg-key");
                values[i] = new Label("-") { pickingMode = PickingMode.Ignore };
                values[i].AddToClassList("dbg-val");
                cell.Add(k);
                cell.Add(values[i]);
                panel.Add(cell);
            }
            return values;
        }

        /// <summary>
        /// Hooks the frame-rate chip (a tap: <paramref name="tap"/>; a long press: <paramref name="hidden"/>) and three
        /// fingers anywhere under <paramref name="root"/> (<paramref name="hidden"/>). The chip's own click is not used,
        /// so a long press does not also count as a tap.
        /// </summary>
        public static void Hook(VisualElement root, VisualElement fps, System.Action tap, System.Action hidden)
        {
            if (root == null) return;
            var downs = new Dictionary<int, float>();
            root.RegisterCallback<PointerDownEvent>(e =>
            {
                float now = Time.unscaledTime;
                downs[e.pointerId] = now;
                int fingers = 0;
                foreach (float t in downs.Values) if (now - t < ThreeFingers) fingers++;
                if (fingers < 3) return;
                downs.Clear();
                hidden?.Invoke();
            }, TrickleDown.TrickleDown);

            if (fps == null) return;
            float pressed = -1f;
            fps.RegisterCallback<PointerDownEvent>(_ => pressed = Time.unscaledTime, TrickleDown.TrickleDown);
            fps.RegisterCallback<PointerUpEvent>(_ =>
            {
                if (pressed < 0f) return;
                bool held = Time.unscaledTime - pressed >= LongPress;
                pressed = -1f;
                if (held) hidden?.Invoke(); else tap?.Invoke();
            }, TrickleDown.TrickleDown);
            fps.RegisterCallback<PointerCancelEvent>(_ => pressed = -1f, TrickleDown.TrickleDown);
        }
    }
}
