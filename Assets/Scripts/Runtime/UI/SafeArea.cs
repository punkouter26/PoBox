using UnityEngine;
using UnityEngine.UIElements;

namespace PoBox.UI
{
    /// <summary>
    /// The one safe-area rule, for every document (the HUD, the first screen, the testbed): the element named
    /// "safe" has its edges moved in by the notch and the home bar, converted from screen pixels to the panel's
    /// own units. Edges, not padding: everything inside is absolutely positioned and would ignore padding.
    /// Re-applied until the panel has a width, because on the first frame it has none and the conversion
    /// would be wrong on any phone that is not exactly the reference width. Honours
    /// <see cref="HudView.SafeAreaOverride"/>, the pretend notch the editor and the tests use.
    /// </summary>
    public sealed class SafeArea
    {
        Rect _last;
        Vector2Int _screen;
        VisualElement _applied;
        bool _scaleKnown;

        public static Rect Current => HudView.SafeAreaOverride ?? Screen.safeArea;

        /// <summary>Call every frame; it does nothing unless the screen, the safe area or the document has changed.</summary>
        public void Fit(VisualElement root, VisualElement safe)
        {
            if (root == null || safe == null) return;
            Rect sa = Current;
            if (_scaleKnown && safe == _applied && sa == _last && _screen.x == Screen.width && _screen.y == Screen.height) return;
            _last = sa;
            _applied = safe;
            _screen = new Vector2Int(Screen.width, Screen.height);
            if (Screen.width <= 0 || Screen.height <= 0) return;

            if (sa.width <= 0f || sa.height <= 0f || sa.xMax > Screen.width + 1f || sa.yMax > Screen.height + 1f)
                sa = new Rect(0f, 0f, Screen.width, Screen.height);

            float panelWidth = root.panel != null ? root.panel.visualTree.layout.width : float.NaN;
            _scaleKnown = panelWidth > 1f;
            float scale = _scaleKnown ? panelWidth / Screen.width : 1f;
            safe.style.left = sa.xMin * scale;
            safe.style.right = (Screen.width - sa.xMax) * scale;
            safe.style.top = (Screen.height - sa.yMax) * scale;
            safe.style.bottom = sa.yMin * scale;
        }
    }
}
