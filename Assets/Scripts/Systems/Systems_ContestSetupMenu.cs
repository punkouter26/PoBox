using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoBox
{
    /// <summary>
    /// Opening menu of the game, self-contained in the contest scene: title,
    /// mandated version stamp top-left (project rule — this is the opening
    /// scene), one dropdown per ring slot pre-filled with a random available
    /// fighter, and a FIGHT button that spawns the picks via
    /// Systems_ContestSpawner and hides the menu. UI Toolkit runtime UI.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class Systems_ContestSetupMenu : MonoBehaviour
    {
        private const string EMPTY_CHOICE = "Empty";

        [SerializeField] private Systems_ContestSpawner _spawner;
        // Set when SCN_MENU launched this scene. Null or unset means the scene
        // was opened directly, so this menu still runs as the opening menu.
        [SerializeField] private Systems_MiniGameSelection _selection;

        private readonly List<DropdownField> _slotDropdowns = new();
        private VisualElement _menuRoot;
        private bool _started;

        // Called by the editor scene tool.
        public void EditorInitialize(Systems_ContestSpawner spawner)
        {
            _spawner = spawner;
        }

        // Called by the editor scene tool when wiring the SCN_MENU hand-off.
        public void EditorSetSelection(Systems_MiniGameSelection selection)
        {
            _selection = selection;
        }

        private void Start()
        {
            // Picks already made in SCN_MENU: skip straight to the ring rather
            // than asking the same question twice. Consumed so a later direct
            // run of this scene shows the menu again.
            if (_selection != null && _selection.HasSelection)
            {
                int[] picks = _selection.Picks;
                _selection.Clear();
                _started = true;
                _spawner.SpawnAndBegin(picks);
                return;
            }

            var root = GetComponent<UIDocument>().rootVisualElement;
            Systems_UiTheme.ApplyDefaultFont(root);

            // Sizes are in reference units: PS_Contest scales 1080×1920 by width.
            Color gold = Systems_UiTheme.Gold;

            // Version stamp: opening-scene rule — top-left, inset, non-pickable.
            var stamp = new Label(Application.version);
            stamp.style.position = Position.Absolute;
            stamp.style.top = 18f;
            stamp.style.left = 18f;
            stamp.style.fontSize = 52f;
            stamp.style.color = new Color(1f, 1f, 1f, 0.55f);
            stamp.pickingMode = PickingMode.Ignore;
            root.Add(stamp);

            _menuRoot = new VisualElement();
            _menuRoot.style.flexGrow = 1f;
            _menuRoot.style.justifyContent = Justify.Center;
            _menuRoot.style.alignItems = Align.Center;
            root.Add(_menuRoot);

            var panel = new VisualElement();
            panel.style.width = Length.Percent(88f);
            panel.style.maxWidth = 960f;
            panel.style.backgroundColor = Systems_UiTheme.PanelDark;
            panel.style.paddingTop = 48f;
            panel.style.paddingBottom = 40f;
            panel.style.paddingLeft = 40f;
            panel.style.paddingRight = 40f;
            panel.style.borderTopLeftRadius = 28f;
            panel.style.borderTopRightRadius = 28f;
            panel.style.borderBottomLeftRadius = 28f;
            panel.style.borderBottomRightRadius = 28f;
            panel.style.borderTopWidth = 2f;
            panel.style.borderBottomWidth = 2f;
            panel.style.borderLeftWidth = 2f;
            panel.style.borderRightWidth = 2f;
            Color borderGold = new Color(gold.r, gold.g, gold.b, 0.4f);
            panel.style.borderTopColor = borderGold;
            panel.style.borderBottomColor = borderGold;
            panel.style.borderLeftColor = borderGold;
            panel.style.borderRightColor = borderGold;
            _menuRoot.Add(panel);

            var title = new Label("PoBox");
            title.style.fontSize = 192f;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = Color.white;
            title.style.alignSelf = Align.Center;
            panel.Add(title);

            var subtitle = new Label("BALANCE CONTEST");
            subtitle.style.fontSize = 64f;
            subtitle.style.letterSpacing = 12f;
            subtitle.style.color = gold;
            subtitle.style.alignSelf = Align.Center;
            subtitle.style.marginBottom = 36f;
            panel.Add(subtitle);

            ContestRosterEntry[] roster = _spawner.Roster;
            var choices = new List<string>(roster.Length + 1);
            for (int rosterIndex = 0; rosterIndex < roster.Length; rosterIndex++)
            {
                choices.Add(roster[rosterIndex].displayName);
            }
            choices.Add(EMPTY_CHOICE);

            // Slots: two-column grid, numbered chips instead of long labels.
            var grid = new VisualElement();
            grid.style.flexDirection = FlexDirection.Row;
            grid.style.flexWrap = Wrap.Wrap;
            grid.style.justifyContent = Justify.SpaceBetween;
            panel.Add(grid);

            var random = new System.Random();
            for (int slotIndex = 0; slotIndex < _spawner.SlotCount; slotIndex++)
            {
                string defaultChoice = roster.Length > 0
                    ? roster[random.Next(roster.Length)].displayName
                    : EMPTY_CHOICE;
                var dropdown = new DropdownField(choices, defaultChoice);
                dropdown.style.width = Length.Percent(48.5f);
                dropdown.style.height = 130f;
                dropdown.style.fontSize = 68f;
                dropdown.style.marginBottom = 18f;
                dropdown.style.backgroundColor = new Color(0.13f, 0.13f, 0.18f, 1f);
                dropdown.style.borderTopLeftRadius = 14f;
                dropdown.style.borderTopRightRadius = 14f;
                dropdown.style.borderBottomLeftRadius = 14f;
                dropdown.style.borderBottomRightRadius = 14f;
                var valueText = dropdown.Q<TextElement>(className: "unity-base-popup-field__text");
                if (valueText != null)
                {
                    valueText.style.color = Color.white;
                    valueText.style.unityTextAlign = TextAnchor.MiddleCenter;
                }
                grid.Add(dropdown);
                _slotDropdowns.Add(dropdown);
            }

            var startButton = new Button(StartContest) { text = "FIGHT" };
            // UITK defaults buttons to upper-left, which parked 321 px of text in the
            // corner of an 867 x 180 button. The mini-game menu sets this on its own
            // buttons; this one was missed.
            startButton.style.unityTextAlign = TextAnchor.MiddleCenter;
            startButton.style.marginTop = 28f;
            startButton.style.height = 180f;
            startButton.style.width = Length.Percent(100f);
            startButton.style.fontSize = 96f;
            startButton.style.unityFontStyleAndWeight = FontStyle.Bold;
            startButton.style.backgroundColor = gold;
            startButton.style.color = new Color(0.1f, 0.08f, 0.02f);
            startButton.style.borderTopLeftRadius = 18f;
            startButton.style.borderTopRightRadius = 18f;
            startButton.style.borderBottomLeftRadius = 18f;
            startButton.style.borderBottomRightRadius = 18f;
            startButton.style.borderTopWidth = 0f;
            startButton.style.borderBottomWidth = 0f;
            startButton.style.borderLeftWidth = 0f;
            startButton.style.borderRightWidth = 0f;
            panel.Add(startButton);
        }

        /// <summary>Starts with the current (default random) picks — also used by automated tests.</summary>
        public void StartContest()
        {
            if (_started)
            {
                return;
            }
            _started = true;

            ContestRosterEntry[] roster = _spawner.Roster;
            var picks = new int[_slotDropdowns.Count];
            for (int slotIndex = 0; slotIndex < _slotDropdowns.Count; slotIndex++)
            {
                picks[slotIndex] = -1;
                string value = _slotDropdowns[slotIndex].value;
                for (int rosterIndex = 0; rosterIndex < roster.Length; rosterIndex++)
                {
                    if (roster[rosterIndex].displayName == value)
                    {
                        picks[slotIndex] = rosterIndex;
                        break;
                    }
                }
            }
            _menuRoot.style.display = DisplayStyle.None;
            _spawner.SpawnAndBegin(picks);
        }
    }
}
