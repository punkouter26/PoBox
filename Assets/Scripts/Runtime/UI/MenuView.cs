using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using PoBox.League;
using PoBox.Sim;
using static PoBox.UI.Ui;

namespace PoBox.UI
{
    /// <summary>
    /// The first screen: who is in the red corner, who is in the blue, and FIGHT.
    ///
    /// One grid, two cards to a row, a card for every trained boxer. The first tap puts a boxer in the red
    /// corner, the next in the blue, and so on, and a card in a corner says which. The two corners always hold
    /// different boxers: tapping the one already in the other corner swaps them over. (A boxer against a copy
    /// of itself was tried on 2026-10-01: two Matts settle into a perfectly matched stand-off and neither
    /// throws a punch.) One portrait screen, nothing scrolls: four rows of two share the height, room for
    /// eight. The pairing is written on FIGHT itself.
    ///
    /// The HUD's five anchors are here too: the frame rate top-centre and DEBUG bottom-left open a small
    /// panel of readings (so does the hidden switch: three fingers, or a long press on the frame rate), and
    /// the settings icon top-right opens the settings as a sheet. The thumb row at the bottom holds CUP,
    /// FIGHT and ALL. FIGHT loads the arena in the background; while it does the button is dimmed with its
    /// word trailing dots and the cards are locked.
    ///
    /// CUP opens the cup (League/Career.cs) as a sheet in the same place: its bracket, one line a match,
    /// NEW CUP (seeded by the ladder's ratings) and FIGHT, which fights the cup's next bout.
    ///
    /// It runs in the editor too, so the cards are in the Game view without pressing Play; the buttons
    /// only do anything while the game is running.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(UIDocument))]
    public class MenuView : MonoBehaviour
    {
        [Serializable]
        public class Boxer
        {
            [Tooltip("The entrant's folder name, which is what the arena knows it by.")]
            public string name;
            public string displayName;
            [Tooltip("One line under the name: weight, training generation.")]
            public string detail;
            public Texture2D portrait;
        }

        public Boxer[] boxers = new Boxer[0];

        public int Red { get; private set; }
        public int Blue { get; private set; }
        public VisualElement Root => _root;
        public bool SettingsOpen => Showing(_sheet);
        public bool DebugOpen => Showing(_debugPanel);
        public bool CupOpen => Showing(_cupSheet);
        public SettingsPage Settings => _settings;
        /// <summary>The corner the next tap on a card fills: 0 red, 1 blue.</summary>
        public int Turn { get; private set; }

        static readonly string[] BusyDots = { " ·", " ··", " ···" };
        static readonly string[] DebugKeys = { "FRAME", "MEMORY", "SCREEN", "SAFE" };

        UIDocument _doc;
        VisualElement _root, _safe, _picker, _sheet, _debugPanel, _cupSheet, _cupBracket;
        Label _cupTitle;
        Button _cupFight;
        Career _cup;
        Label _versus, _fightWord;
        // The word on the button that was pressed (FIGHT or ALL's MELEE), which trails dots while its scene loads
        Label _busyWord;
        string _busyBase;
        Button _fight, _fps, _melee, _cupButton;
        Button[] _cards = new Button[0];
        Label[] _tags = new Label[0];
        Label[] _debugValues = new Label[0];
        readonly SafeArea _safeArea = new SafeArea();
        SettingsPage _settings;
        bool _leaving;
        float _smoothed = 1f / 60f, _nextText;

        void OnEnable()
        {
            if (Application.isPlaying)
            {
                Application.targetFrameRate = 60;
                // Every way back lands here, so the clock is put right here: the free-for-all's bullet time sets it
                // directly and nothing else puts it back when the scene is left in the middle of one.
                Time.timeScale = 1f;
                SettingsPage.Load();
            }
            Build();
        }

        /// <summary>
        /// Fills the document in and hooks its buttons: once a document, which in the editor is every time the
        /// layout or the stylesheet is saved.
        /// </summary>
        void Build()
        {
            if (_doc == null) _doc = GetComponent<UIDocument>();
            VisualElement root = _doc.rootVisualElement;
            if (root == null || root == _root) return;
            _root = root;
            _safe = _root.Q<VisualElement>("safe");
            _versus = _root.Q<Label>("versus");
            _fightWord = _root.Q<Label>("fight-word");
            _sheet = _root.Q<VisualElement>("menu-sheet");
            _debugPanel = _root.Q<VisualElement>("debug-panel");
            _fps = _root.Q<Button>("fps");

            _picker = _root.Q<VisualElement>("picker");
            BuildCards();
            _fight = _root.Q<Button>("fight");
            if (_fight != null)
            {
                _fight.clicked += Fight;
                _fight.SetEnabled(boxers.Length > 0);
            }
            _melee = _root.Q<Button>("melee");
            if (_melee != null)
            {
                _melee.clicked += FreeForAll;
                _melee.SetEnabled(Application.CanStreamedLevelBeLoaded(MatchSelection.MeleeScene));
            }
            Button settings = _root.Q<Button>("menu");
            if (settings != null) settings.clicked += ToggleSettings;
            Button debug = _root.Q<Button>("debug");
            if (debug != null) debug.clicked += ToggleDebug;
            DebugPanel.Hook(_root, _fps, ToggleDebug, ToggleDebug);
            _cupSheet = _root.Q<VisualElement>("cup-sheet");
            _cupBracket = _root.Q<VisualElement>("cup-bracket");
            _cupTitle = _root.Q<Label>("cup-title");
            _cupFight = _root.Q<Button>("cup-fight");
            _cupButton = _root.Q<Button>("career");
            if (_cupButton != null) _cupButton.clicked += ToggleCup;
            Button cupNew = _root.Q<Button>("cup-new");
            if (cupNew != null) cupNew.clicked += NewCup;
            if (_cupFight != null) _cupFight.clicked += FightCup;
            // A tap on what is left of the screen puts away whatever is open, as on the HUD.
            VisualElement picture = _root.Q<VisualElement>("picture");
            if (picture != null) picture.RegisterCallback<PointerDownEvent>(_ => CloseAll());
            _debugValues = DebugPanel.Build(_debugPanel, DebugKeys);
            // The first screen has no ladder to reset (that is the arena's), no brain cam and no boxers to X-ray.
            _settings = new SettingsPage(_root, null, ToggleDebug, xray: false);
            Frame(_root);

            // The pair fought last time; failing that the first boxer against the second.
            Red = Mathf.Max(0, IndexOf(MatchSelection.LastRed));
            int blue = IndexOf(MatchSelection.LastBlue);
            Blue = blue >= 0 ? blue : Mathf.Min(1, boxers.Length - 1);
            if (Blue == Red && boxers.Length > 1) Blue = (Red + 1) % boxers.Length;
            Refresh();
            _safeArea.Fit(_root, _safe);
        }

        void Update()
        {
            if (_doc == null) _doc = GetComponent<UIDocument>();
            VisualElement root = _doc.rootVisualElement;
            if (root == null) return;
            if (root != _root) Build();
            else if (_picker != null && _picker.childCount == 0 && boxers.Length > 0) { BuildCards(); Refresh(); }
            if (_root == null) return;
            _safeArea.Fit(_root, _safe);
            if (!Application.isPlaying) return;

            if (Input.GetKeyDown(KeyCode.Escape)) Back();
            if (_leaving) Set(_busyWord, _busyBase + BusyDots[(int)(Time.unscaledTime * 3f) % BusyDots.Length]);
            _smoothed = Mathf.Lerp(_smoothed, Time.unscaledDeltaTime, 0.05f);
            if (Time.unscaledTime < _nextText) return;
            _nextText = Time.unscaledTime + 0.25f;      // four times a second: text made every frame is garbage every frame
            Frame(_root);
            Set(_fps, $"{1f / Mathf.Max(_smoothed, 1e-4f):0} FPS");
            if (DebugOpen) RefreshDebug();
            if (_settings != null && SettingsOpen && _settings.Armed) _settings.Refresh();
        }

        int IndexOf(string name)
        {
            for (int i = 0; i < boxers.Length; i++) if (boxers[i].name == name) return i;
            return -1;
        }

        void BuildCards()
        {
            _cards = new Button[boxers.Length];
            _tags = new Label[boxers.Length];
            if (_picker == null) return;
            _picker.Clear();
            for (int i = 0; i < boxers.Length; i++)
            {
                int index = i;
                Boxer b = boxers[i];
                var card = new Button(() => Tap(index)) { name = "pick-" + b.name };
                card.AddToClassList("pick");

                var portrait = new VisualElement { pickingMode = PickingMode.Ignore };
                portrait.AddToClassList("pick-portrait");
                if (b.portrait != null) portrait.style.backgroundImage = new StyleBackground(b.portrait);
                card.Add(portrait);

                var foot = new VisualElement { pickingMode = PickingMode.Ignore };
                foot.AddToClassList("pick-foot");
                var title = new Label(b.displayName) { pickingMode = PickingMode.Ignore };
                title.AddToClassList("pick-name");
                var detail = new Label(b.detail) { pickingMode = PickingMode.Ignore };
                detail.AddToClassList("pick-detail");
                foot.Add(title);
                foot.Add(detail);
                card.Add(foot);

                // RED or BLUE over the portrait while the boxer is in a corner (the stylesheet shows it).
                var tag = new Label("") { pickingMode = PickingMode.Ignore };
                tag.AddToClassList("pick-tag");
                card.Add(tag);

                _picker.Add(card);
                _cards[i] = card;
                _tags[i] = tag;
            }
        }

        void RefreshDebug()
        {
            if (_debugValues.Length < DebugKeys.Length) return;
            Rect sa = SafeArea.Current;
            Set(_debugValues[0], $"{_smoothed * 1000f:0.0} ms");
            Set(_debugValues[1], $"{UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / (1024f * 1024f):0} MB");
            Set(_debugValues[2], $"{Screen.width}x{Screen.height}");
            Set(_debugValues[3], $"{sa.width:0}x{sa.height:0}");
        }

        static bool Showing(VisualElement e) => e != null && !e.ClassListContains("hidden");

        public void ToggleSettings() => Open(_sheet, !SettingsOpen);
        /// <summary>The settings, open on one of their tabs (0 SOUND, 1 DISPLAY, 2 DEBUG).</summary>
        public void OpenSettings(int tab) { Open(_sheet, true); _settings?.ShowTab(tab); }
        public void ToggleDebug() => Open(_debugPanel, !DebugOpen);
        void CloseAll() { Open(_sheet, false); Open(_debugPanel, false); Open(_cupSheet, false); }

        /// <summary>The phone's back button (Escape): puts away what is open, and on the bare first screen leaves the app.</summary>
        public void Back()
        {
            if (SettingsOpen || DebugOpen || CupOpen) CloseAll();
            else if (!_leaving) Application.Quit();
        }

        /// <summary>One at a time: they are docked in the same place.</summary>
        void Open(VisualElement which, bool open)
        {
            if (which == null) return;
            if (open)
            {
                foreach (VisualElement other in new[] { _sheet, _debugPanel, _cupSheet })
                    if (other != null && other != which) other.AddToClassList("hidden");
                if (which == _sheet && _settings != null) _settings.Refresh();
            }
            which.EnableInClassList("hidden", !open);
            if (open && which == _debugPanel) RefreshDebug();
        }

        /// <summary>A tap on a card: it goes in the corner whose turn it is, and the turn passes to the other.</summary>
        public void Tap(int index)
        {
            if (_leaving || index < 0 || index >= boxers.Length) return;
            Pick(Turn, index);
            Turn = 1 - Turn;
        }

        /// <summary>Corner 0 is red, 1 is blue.</summary>
        public void Pick(int corner, int index)
        {
            if (_leaving || index < 0 || index >= boxers.Length) return;
            int was = corner == 0 ? Red : Blue, other = corner == 0 ? Blue : Red;
            // Already in the other corner: the two change places.
            if (index == other && boxers.Length > 1) other = was;
            Red = corner == 0 ? index : other;
            Blue = corner == 0 ? other : index;
            Refresh();
        }

        void Refresh()
        {
            for (int i = 0; i < _cards.Length; i++)
            {
                if (_cards[i] == null) continue;
                _cards[i].EnableInClassList("pick--red", i == Red);
                _cards[i].EnableInClassList("pick--blue", i == Blue);
                Set(_tags[i], i == Red ? "RED" : i == Blue ? "BLUE" : "");
            }
            if (_versus != null && boxers.Length > 0) _versus.text = $"{boxers[Red].displayName}  v  {boxers[Blue].displayName}";
        }

        public void Fight()
        {
            // Once: the arena takes seconds to load, and a second tap in that time would load it twice.
            if (boxers.Length == 0 || !Application.isPlaying || _leaving) return;
            Career.Live = false;
            _leaving = true;
            CloseAll();
            if (_fight != null) _fight.AddToClassList("btn--busy");
            _busyWord = _fightWord;
            _busyBase = "FIGHT";
            foreach (Button card in _cards) card?.SetEnabled(false);
            _melee?.SetEnabled(false);
            _cupButton?.SetEnabled(false);
            MatchSelection.Choose(boxers[Red].name, boxers[Blue].name);
            SceneManager.LoadSceneAsync(MatchSelection.ArenaScene);
        }

        /// <summary>Every boxer in the ring at once (the Melee scene): the picks above do not come into it.</summary>
        public void FreeForAll()
        {
            if (!Application.isPlaying || _leaving) return;
            _leaving = true;
            CloseAll();
            if (_melee != null) _melee.AddToClassList("btn--busy");
            _busyWord = _melee?.Q<Label>(className: "thumb-sub");
            _busyBase = "MELEE";
            foreach (Button card in _cards) card?.SetEnabled(false);
            _fight?.SetEnabled(false);
            _cupButton?.SetEnabled(false);
            SceneManager.LoadSceneAsync(MatchSelection.MeleeScene);
        }

        // ------------------------------------------------------------ the cup

        public void ToggleCup()
        {
            bool open = !CupOpen;
            if (open)
            {
                if (_cup == null) _cup = Career.Load();
                RefreshCup();
            }
            Open(_cupSheet, open);
        }

        /// <summary>A new cup from every boxer on this screen, best rated first (the first eight are in it).</summary>
        public void NewCup()
        {
            if (!Application.isPlaying || _leaving) return;
            var entrants = new System.Collections.Generic.List<(string, string)>();
            foreach (Boxer b in boxers) entrants.Add((b.name, b.displayName));
            _cup = Career.New(entrants);
            RefreshCup();
        }

        /// <summary>The cup's next bout, in the corners the bracket gives, fought as FIGHT fights; its result goes back to the cup.</summary>
        public void FightCup()
        {
            if (_cup == null || !_cup.Next(out _, out string red, out string blue)) return;
            int r = IndexOf(red), b = IndexOf(blue);
            if (r < 0 || b < 0) return;
            Red = r;
            Blue = b;
            Refresh();
            Fight();
            if (_leaving) Career.Live = true;
        }

        void RefreshCup()
        {
            if (_cupBracket == null) return;
            _cupBracket.Clear();
            int next = -1;
            bool more = _cup != null && _cup.Next(out next, out _, out _);
            if (_cup == null) Set(_cupTitle, "NO CUP YET · NEW CUP SEEDS THEM BY RATING");
            else if (_cup.Champion != null) Set(_cupTitle, "CUP WON BY " + _cup.Shown(_cup.Champion));
            else Set(_cupTitle, "THE CUP");
            if (_cupFight != null) _cupFight.SetEnabled(more);
            if (_cup == null) return;
            for (int m = 0; m < Career.Matches; m++)
            {
                var line = new Label(_cup.Line(m)) { pickingMode = PickingMode.Ignore };
                line.AddToClassList("cup-line");
                line.EnableInClassList("cup-line--next", m == next);
                _cupBracket.Add(line);
            }
        }
    }
}
