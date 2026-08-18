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
