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

        /// <summary>
        /// One scoreboard name plate: a colour swatch identifying the fighter,
        /// then its name and score.
        ///
        /// The swatch is the whole point. A plate used to be a bare Label, and
        /// an eight-slot ring filled from a four-entry roster holds two of each
        /// kind — so the scoreboard read "Grandma  1.4s" and "Grandma2  1.3s"
        /// over a ring containing two fighters wearing the same untinted
        /// texture, with nothing at all connecting either plate to either body.
        /// The spawner already knows which colour it gave each fighter (roster
        /// tint, then the copy wash); this is where that answer gets shown.
        /// </summary>
        public static VisualElement BuildPlate(Color swatchColor, out Label label)
        {
            var plate = new VisualElement();
            plate.AddToClassList("plate");
            plate.pickingMode = PickingMode.Ignore;

            var swatch = new VisualElement();
            swatch.AddToClassList("plate-swatch");
            swatch.style.backgroundColor = swatchColor;
            plate.Add(swatch);

            label = new Label();
            label.AddToClassList("plate-label");
            plate.Add(label);
            return plate;
        }

        /// <summary>
        /// One entry in the match star tally: "NAME ★n" on the fighter's own
        /// colour.
        ///
        /// The tally used to be a single Label holding every name joined by
        /// spaces, which wraps rather than truncates — so a full ring pushed it
        /// to two and three lines of near-identical names ("Standard ★2
        /// Standard2 ★1") straight down the top of the screen, shoving the
        /// hazard chip with it. Chips wrap into a row that stays one line for
        /// as long as the interesting case (two or three fighters on the board)
        /// lasts, and each carries the swatch that says which fighter it is.
        /// </summary>
        public static VisualElement BuildScoreChip(string text, Color swatchColor)
        {
            var chip = new VisualElement();
            chip.AddToClassList("score-chip");
            chip.pickingMode = PickingMode.Ignore;

            var swatch = new VisualElement();
            swatch.AddToClassList("plate-swatch");
            swatch.style.backgroundColor = swatchColor;
            chip.Add(swatch);

            var label = new Label(text);
            label.AddToClassList("score-chip-label");
            chip.Add(label);
            return chip;
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

        // --- Contest HUD top stack -------------------------------------------
        //
        // The corners of a contest HUD are spoken for: the version stamp
        // (y 18-83) and the FPS readout (y 84-149) own the top-left, the
        // "MENU" button (y 16-100) the top-right, and the referee's centred
        // title chip the strip above them. Everything else that wants to sit
        // near the top - the match star tally, the hazard chip - used to place
        // itself absolutely and guess at the free space, which is how the
        // full-width centred tally came to be drawn straight through the FPS
        // counter as soon as it listed more than two names, and how a long
        // hazard name reached back into the round title.
        //
        // These two live in one column instead, inset clear of both corners, so
        // they stack rather than collide and a wrapped line pushes what is
        // below it down instead of overlapping it.

        private const string HUD_STACK_NAME = "hud-top-stack";
        private const string HUD_SCORE_SLOT_NAME = "hud-score-slot";
        private const string HUD_HAZARD_SLOT_NAME = "hud-hazard-slot";

        // Inset from the panel edges. 250 px was sized to clear the FPS readout
        // and the MENU button, but both of those END at y 149 and this stack
        // STARTS at y 158 — so the gutters were dodging things that are not
        // beside them, and paying 500 px of the 1080 for it. Two star chips
        // ("Grandma *1", "Grandpa *1") measure about 570 px together and wrapped
        // to two lines inside the 580 px that left. 80 px is the ordinary screen
        // margin and fits three chips on one line.
        private const float HUD_CORNER_GUTTER = 80f;
        // Below the FPS readout, which ends at y 149.
        private const float HUD_STACK_TOP = 158f;

        /// <summary>Row the match scoreboard belongs in — first in the stack.</summary>
        public static VisualElement HudScoreSlot(VisualElement root)
        {
            return EnsureHudStack(root).Q<VisualElement>(HUD_SCORE_SLOT_NAME);
        }

        /// <summary>Row the hazard chip belongs in — below the scoreboard.</summary>
        public static VisualElement HudHazardSlot(VisualElement root)
        {
            return EnsureHudStack(root).Q<VisualElement>(HUD_HAZARD_SLOT_NAME);
        }

        // Both slots are created together, in display order, the first time
        // either is asked for: the scoreboard and the hazard chip are owned by
        // different components and neither Start is guaranteed to run first.
        private static VisualElement EnsureHudStack(VisualElement root)
        {
            VisualElement stack = root.Q<VisualElement>(HUD_STACK_NAME);
            if (stack != null)
            {
                return stack;
            }
            stack = new VisualElement { name = HUD_STACK_NAME };
            stack.style.position = Position.Absolute;
            stack.style.top = HUD_STACK_TOP;
            stack.style.left = HUD_CORNER_GUTTER;
            stack.style.right = HUD_CORNER_GUTTER;
            stack.style.alignItems = Align.Center;
            stack.pickingMode = PickingMode.Ignore;
            stack.Add(NewSlot(HUD_SCORE_SLOT_NAME));
            stack.Add(NewSlot(HUD_HAZARD_SLOT_NAME));
            root.Add(stack);
            return stack;
        }

        private static VisualElement NewSlot(string name)
        {
            var slot = new VisualElement { name = name };
            slot.style.alignItems = Align.Center;
            slot.pickingMode = PickingMode.Ignore;
            return slot;
        }
    }
}
