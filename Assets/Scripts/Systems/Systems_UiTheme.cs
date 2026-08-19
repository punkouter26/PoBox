using UnityEngine;
using UnityEngine.UIElements;

namespace PoBox
{
    /// <summary>
    /// Single source for runtime UI theming shared by every UIDocument screen.
    /// The contest panel uses an empty theme, which supplies no font — without
    /// an explicit one, labels render as nothing at all.
    /// </summary>
    internal static class Systems_UiTheme
    {
        // Shared palette — every runtime screen pulls from here so the app
        // stays visually consistent (menu, announcer, plates, banner).
        public static readonly Color Gold = new(0.85f, 0.68f, 0.25f);
        public static readonly Color GoldBright = new(1f, 0.9f, 0.4f);
        public static readonly Color HazardOrange = new(1f, 0.55f, 0.25f);
        public static readonly Color PanelDark = new(0.05f, 0.05f, 0.08f, 0.94f);

        private static FontDefinition _font;
        private static bool _loaded;

        public static void ApplyDefaultFont(VisualElement root)
        {
            if (!_loaded)
            {
                _font = FontDefinition.FromFont(
                    Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
                _loaded = true;
            }
            root.style.unityFontDefinition = _font;
            // The panel has no theme stylesheet, so the document root never
            // receives a size — every child layout collapses to 0 height
            // without this explicit stretch.
            root.style.position = Position.Absolute;
            root.style.top = 0;
            root.style.bottom = 0;
            root.style.left = 0;
            root.style.right = 0;
        }
    }
}
