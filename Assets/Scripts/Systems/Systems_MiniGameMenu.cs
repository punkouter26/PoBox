using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace PoBox
{
    /// <summary>
    /// Opening menu of the game (SCN_MENU, build index 0): mandated version
    /// stamp top-left, a mini-game picker, one fighter dropdown per slot in
    /// that mini-game — 8 for the balance contest, 4 for the walk race — and a
    /// START button that records the picks in
    /// <see cref="Systems_MiniGameSelection"/> and loads the chosen scene.
    /// UI Toolkit runtime UI, portrait 9:16 (project rules).
    ///
    /// Sizes are in reference units: the panel scales 1080x1920 by width, and
    /// the WebGL template letterboxes the canvas to 9:16 so those units hold.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class Systems_MiniGameMenu : MonoBehaviour
    {
        private const string EMPTY_CHOICE = "Empty";
        // Down-pointing small triangle. The contest scoreboard already renders
        // stars and em dashes through the same font, so symbol coverage is fine.
        private const string CARET_GLYPH = "▾";
        private const int BALANCE_SLOTS = 8;
        private const int WALK_SLOTS = 4;

        [SerializeField] private Systems_MiniGameSelection _selection;
        // Names offered per slot, in the same order as the receiving scene's
        // spawner roster — the pick is sent across as that roster index.
        [SerializeField] private string[] _fighterNames = { "Standard", "Grandma", "Grandpa", "Bot" };
        [SerializeField] private string _balanceScene = "SCN_TEST_BALANCE_CONTEST";
        [SerializeField] private string _walkScene = "SCN_TEST_WALK_CONTEST";

        private readonly List<DropdownField> _slotDropdowns = new();
        private VisualElement _slotsPanel;
        private Label _slotsHeading;
        private Button _balanceButton;
        private Button _walkButton;
        private MiniGameKind _game = MiniGameKind.Balance;
        private bool _started;

        private static readonly Color Ink = new(0.06f, 0.05f, 0.03f);
        private static readonly Color Dim = new(1f, 1f, 1f, 0.38f);
        private static readonly Color CardFill = new(1f, 1f, 1f, 0.045f);
        private static readonly Color RowFill = new(1f, 1f, 1f, 0.035f);

        // Called by the editor scene tool.
        public void EditorInitialize(Systems_MiniGameSelection selection)
        {
            _selection = selection;
        }

        private void Start()
        {
            var root = GetComponent<UIDocument>().rootVisualElement;
            Systems_UiTheme.ApplyDefaultFont(root);
            Color gold = Systems_UiTheme.Gold;

            // Version stamp: opening-scene rule — top-left, inset, non-pickable,
            // outside any ScrollView.
            var stamp = new Label(Application.version);
            stamp.pickingMode = PickingMode.Ignore;
            stamp.style.position = Position.Absolute;
            stamp.style.left = 34f;
            stamp.style.top = 28f;
            stamp.style.fontSize = 26f;
            stamp.style.color = Dim;
            root.Add(stamp);

            var page = new VisualElement();
            page.style.flexGrow = 1f;
            page.style.paddingLeft = 56f;
            page.style.paddingRight = 56f;
            page.style.paddingTop = 120f;
            page.style.paddingBottom = 56f;
            root.Add(page);

            var title = new Label("PO BOX");
            title.style.fontSize = 132f;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = gold;
            title.style.unityTextAlign = TextAnchor.MiddleCenter;
            title.style.letterSpacing = 8f;
            page.Add(title);

            var tagline = new Label("CHOOSE YOUR CONTEST");
            tagline.style.fontSize = 30f;
            tagline.style.color = Dim;
            tagline.style.unityTextAlign = TextAnchor.MiddleCenter;
            tagline.style.letterSpacing = 6f;
            tagline.style.marginTop = 6f;
            tagline.style.marginBottom = 40f;
            page.Add(tagline);

            var pickRow = new VisualElement();
            pickRow.style.flexDirection = FlexDirection.Row;
            page.Add(pickRow);

            _balanceButton = MakeGameButton("BALANCE", MiniGameKind.Balance);
            _walkButton = MakeGameButton("WALK", MiniGameKind.Walk);
            _balanceButton.style.marginRight = 10f;
            _walkButton.style.marginLeft = 10f;
            pickRow.Add(_balanceButton);
            pickRow.Add(_walkButton);

            _slotsHeading = new Label();
            _slotsHeading.style.fontSize = 28f;
            _slotsHeading.style.color = Dim;
            _slotsHeading.style.letterSpacing = 5f;
            _slotsHeading.style.marginTop = 44f;
            _slotsHeading.style.marginBottom = 14f;
            page.Add(_slotsHeading);

            _slotsPanel = new VisualElement();
            _slotsPanel.style.backgroundColor = CardFill;
            SetRadius(_slotsPanel, 22f);
            _slotsPanel.style.paddingTop = 16f;
            _slotsPanel.style.paddingBottom = 8f;
            _slotsPanel.style.paddingLeft = 16f;
            _slotsPanel.style.paddingRight = 16f;
            page.Add(_slotsPanel);

            // Absorbs leftover height so the 4-slot walk layout does not leave
            // a dead gap above START, and the 8-slot layout stays compact.
            var spacer = new VisualElement();
            spacer.style.flexGrow = 1f;
            spacer.style.minHeight = 24f;
            page.Add(spacer);

            var startButton = new Button(StartGame) { text = "START" };
            startButton.style.height = 168f;
            startButton.style.fontSize = 82f;
            startButton.style.unityFontStyleAndWeight = FontStyle.Bold;
            startButton.style.unityTextAlign = TextAnchor.MiddleCenter;
            startButton.style.letterSpacing = 6f;
            startButton.style.backgroundColor = gold;
            startButton.style.color = Ink;
            startButton.style.marginLeft = 0f;
            startButton.style.marginRight = 0f;
            SetRadius(startButton, 22f);
            page.Add(startButton);

            SelectGame(MiniGameKind.Balance);
        }

        private Button MakeGameButton(string text, MiniGameKind game)
        {
            var button = new Button(() => SelectGame(game)) { text = text };
            button.style.flexGrow = 1f;
            button.style.flexBasis = 0f;
            button.style.height = 132f;
            button.style.fontSize = 50f;
            button.style.unityFontStyleAndWeight = FontStyle.Bold;
            button.style.unityTextAlign = TextAnchor.MiddleCenter;
            button.style.letterSpacing = 4f;
            button.style.marginTop = 0f;
            button.style.marginBottom = 0f;
            SetRadius(button, 20f);
            return button;
        }

        private void SelectGame(MiniGameKind game)
        {
            _game = game;
            StyleGameButton(_balanceButton, game == MiniGameKind.Balance);
            StyleGameButton(_walkButton, game == MiniGameKind.Walk);

            int slotCount = game == MiniGameKind.Balance ? BALANCE_SLOTS : WALK_SLOTS;
            _slotsHeading.text = game == MiniGameKind.Balance
                ? $"RING  ·  {slotCount} FIGHTERS"
                : $"START LINE  ·  {slotCount} RACERS";
            BuildSlots(slotCount);
        }

        // Selected reads as a filled chip, unselected as a quiet outline —
        // colour alone would be too subtle at a glance on a phone.
        private static void StyleGameButton(Button button, bool selected)
        {
            Color gold = Systems_UiTheme.Gold;
            button.style.backgroundColor = selected ? gold : new Color(1f, 1f, 1f, 0.05f);
            button.style.color = selected ? Ink : gold;
            SetBorderColor(button, selected ? gold : new Color(gold.r, gold.g, gold.b, 0.35f));
            SetBorderWidth(button, selected ? 0f : 2f);
        }

        // Rebuilt on every game change: the two mini-games field different
        // numbers of fighters, and a stale extra dropdown would send a pick
        // for a slot the receiving scene does not have.
        private void BuildSlots(int slotCount)
        {
            _slotsPanel.Clear();
            _slotDropdowns.Clear();

            var choices = new List<string>(_fighterNames.Length + 1);
            for (int nameIndex = 0; nameIndex < _fighterNames.Length; nameIndex++)
            {
                choices.Add(_fighterNames[nameIndex]);
            }
            choices.Add(EMPTY_CHOICE);

            for (int slotIndex = 0; slotIndex < slotCount; slotIndex++)
            {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.height = 92f;
                row.style.paddingLeft = 14f;
                row.style.paddingRight = 14f;
                row.style.marginBottom = 8f;
                row.style.backgroundColor = RowFill;
                SetRadius(row, 14f);

                var badge = new Label((slotIndex + 1).ToString());
                badge.style.width = 54f;
                badge.style.fontSize = 32f;
                badge.style.unityFontStyleAndWeight = FontStyle.Bold;
                badge.style.color = new Color(1f, 1f, 1f, 0.30f);
                badge.style.unityTextAlign = TextAnchor.MiddleLeft;
                row.Add(badge);

                // Empty label: the badge already names the slot, and the
                // built-in label would push the value box off-centre.
                var dropdown = new DropdownField(string.Empty, choices,
                    _fighterNames.Length > 0 ? slotIndex % _fighterNames.Length : choices.Count - 1);
                dropdown.style.flexGrow = 1f;
                dropdown.style.marginLeft = 0f;
                dropdown.style.marginRight = 0f;
                StyleDropdown(dropdown);
                row.Add(dropdown);

                _slotsPanel.Add(row);
                _slotDropdowns.Add(dropdown);
            }
        }

        // The stock DropdownField renders as low-contrast grey-on-grey at this
        // scale, which is what made the first pass unreadable. Style the inner
        // parts, not just the field.
        private static void StyleDropdown(DropdownField dropdown)
        {
            VisualElement input = dropdown.Q(className: "unity-base-popup-field__input");
            if (input != null)
            {
                // The input laid its children out as a column, which stacked the arrow
                // under the value text and left the text top-aligned in a box twice its
                // height. Pin the direction and centre the row explicitly.
                input.style.flexDirection = FlexDirection.Row;
                input.style.alignItems = Align.Center;
                input.style.height = 62f;
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
                text.style.fontSize = 36f;
                text.style.color = Systems_UiTheme.GoldBright;
                text.style.unityTextAlign = TextAnchor.MiddleLeft;
                // Middle-align centres glyphs inside the element, not inside the input,
                // so the element has to fill the input height for it to mean anything.
                text.style.flexGrow = 1f;
                text.style.height = Length.Percent(100f);
            }

            // The runtime theme resolves no background image for the stock arrow, so
            // tinting it drew nothing and the fields read as plain boxes with no hint
            // that they open. Replace it with a glyph the font actually has.
            VisualElement arrow = dropdown.Q(className: "unity-base-popup-field__arrow");
            if (arrow != null)
            {
                arrow.style.display = DisplayStyle.None;
            }
            if (input != null)
            {
                var caret = new Label(CARET_GLYPH);
                caret.style.fontSize = 30f;
                caret.style.color = Systems_UiTheme.Gold;
                caret.style.marginLeft = 8f;
                caret.style.unityTextAlign = TextAnchor.MiddleCenter;
                caret.pickingMode = PickingMode.Ignore;
                input.Add(caret);
            }
        }

        private static void SetRadius(VisualElement element, float radius)
        {
            element.style.borderTopLeftRadius = radius;
            element.style.borderTopRightRadius = radius;
            element.style.borderBottomLeftRadius = radius;
            element.style.borderBottomRightRadius = radius;
        }

        private static void SetBorderWidth(VisualElement element, float width)
        {
            element.style.borderTopWidth = width;
            element.style.borderBottomWidth = width;
            element.style.borderLeftWidth = width;
            element.style.borderRightWidth = width;
        }

        private static void SetBorderColor(VisualElement element, Color color)
        {
            element.style.borderTopColor = color;
            element.style.borderBottomColor = color;
            element.style.borderLeftColor = color;
            element.style.borderRightColor = color;
        }

        private void StartGame()
        {
            if (_started)
            {
                return;
            }
            _started = true;

            var picks = new int[_slotDropdowns.Count];
            for (int slotIndex = 0; slotIndex < _slotDropdowns.Count; slotIndex++)
            {
                picks[slotIndex] = -1; // EMPTY_CHOICE and anything unmatched
                string value = _slotDropdowns[slotIndex].value;
                for (int nameIndex = 0; nameIndex < _fighterNames.Length; nameIndex++)
                {
                    if (_fighterNames[nameIndex] == value)
                    {
                        picks[slotIndex] = nameIndex;
                        break;
                    }
                }
            }

            _selection.Set(_game, picks);
            SceneManager.LoadScene(_game == MiniGameKind.Balance ? _balanceScene : _walkScene);
        }
    }
}
