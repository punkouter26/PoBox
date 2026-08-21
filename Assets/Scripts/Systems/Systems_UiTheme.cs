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

        // Down-pointing small triangle. The contest scoreboard already renders
        // stars and em dashes through the same font, so symbol coverage is fine.
        private const string CARET_GLYPH = "▾";

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

        /// <summary>
        /// Version stamp: top-left, inset, non-pickable, outside any ScrollView
        /// (project rule). Call it before any early return in the screen's Start
        /// — Systems_ContestSetupMenu used to build its stamp after the
        /// "picks already made in SCN_MENU" bail-out, so the stamp appeared only
        /// when the contest scene was played directly and never on the real
        /// menu -> contest path.
        /// </summary>
        public static void AddVersionStamp(VisualElement root)
        {
            var stamp = new Label(Application.version);
            stamp.pickingMode = PickingMode.Ignore;
            stamp.style.position = Position.Absolute;
            stamp.style.left = 18f;
            stamp.style.top = 18f;
            stamp.style.fontSize = 52f;
            stamp.style.color = new Color(1f, 1f, 1f, 0.55f);
            root.Add(stamp);
        }

        /// <summary>
        /// Makes a DropdownField read as something you can open.
        ///
        /// The runtime theme resolves no background image for the stock arrow, so
        /// the built-in affordance draws nothing at all while still taking its
        /// layout space — measured 2026-08-20 in the balance setup menu: an
        /// invisible arrow element 420.75 px wide, which left the fields looking
        /// like flat, non-interactive labels and pushed the value text off centre.
        /// Hide it and add a glyph the font actually has.
        ///
        /// <paramref name="centreValue"/> centres the value text (the contest
        /// setup grid) rather than left-aligning it (the mini-game menu rows).
        /// </summary>
        public static void StyleDropdown(DropdownField dropdown, float rowHeight, float fontSize,
            bool centreValue)
        {
            VisualElement input = dropdown.Q(className: "unity-base-popup-field__input");
            if (input != null)
            {
                // The input laid its children out as a column, which stacked the arrow
                // under the value text and left the text top-aligned in a box twice its
                // height. Pin the direction and centre the row explicitly.
                input.style.flexDirection = FlexDirection.Row;
                input.style.alignItems = Align.Center;
                input.style.height = rowHeight;
                input.style.backgroundColor = new Color(0f, 0f, 0f, 0.35f);
                input.style.paddingLeft = 18f;
                input.style.paddingRight = 12f;
                SetRadius(input, 12f);
                SetBorderColor(input, new Color(1f, 1f, 1f, 0.12f));
                SetBorderWidth(input, 1f);
            }

            var text = dropdown.Q<TextElement>(className: "unity-base-popup-field__text");
            if (text != null)
            {
                text.style.fontSize = fontSize;
                text.style.color = GoldBright;
                text.style.unityTextAlign = centreValue ? TextAnchor.MiddleCenter : TextAnchor.MiddleLeft;
                // Middle-align centres glyphs inside the element, not inside the input,
                // so the element has to fill the input height for it to mean anything.
                text.style.flexGrow = 1f;
                text.style.height = Length.Percent(100f);
            }

            VisualElement arrow = dropdown.Q(className: "unity-base-popup-field__arrow");
            if (arrow != null)
            {
                arrow.style.display = DisplayStyle.None;
            }
            if (input != null)
            {
                var caret = new Label(CARET_GLYPH);
                caret.style.fontSize = fontSize * 0.83f;
                caret.style.color = Gold;
                caret.style.marginLeft = 8f;
                caret.style.unityTextAlign = TextAnchor.MiddleCenter;
                caret.pickingMode = PickingMode.Ignore;
                input.Add(caret);
            }
        }

        public static void SetRadius(VisualElement element, float radius)
        {
            element.style.borderTopLeftRadius = radius;
            element.style.borderTopRightRadius = radius;
            element.style.borderBottomLeftRadius = radius;
            element.style.borderBottomRightRadius = radius;
        }

        public static void SetBorderWidth(VisualElement element, float width)
        {
            element.style.borderTopWidth = width;
            element.style.borderBottomWidth = width;
            element.style.borderLeftWidth = width;
            element.style.borderRightWidth = width;
        }

        public static void SetBorderColor(VisualElement element, Color color)
        {
            element.style.borderTopColor = color;
            element.style.borderBottomColor = color;
            element.style.borderLeftColor = color;
            element.style.borderRightColor = color;
        }
    }
}
