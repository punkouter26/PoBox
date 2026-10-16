using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using PoBox.Sim;

namespace PoBox.UI
{
    /// <summary>
    /// The first screen: who is in the red corner, who is in the blue, and FIGHT.
    ///
    /// Two columns, one a corner, each with a card for every trained boxer; a tap picks one. The two corners
    /// always hold different boxers: tapping the one already in the other corner swaps them over. (A boxer
    /// against a copy of itself was tried on 2026-10-01: two Matts settle into a perfectly matched stand-off
    /// and neither throws a punch.) One portrait screen, nothing scrolls: the cards share the column's
    /// height, so more boxers means shorter cards, not a list. The pairing is written on FIGHT itself.
    ///
    /// The HUD's five anchors are here too: the frame rate top-centre and DEBUG bottom-left open a small
    /// panel of readings, and the settings icon top-right opens the HUD's settings page as a sheet. FIGHT
    /// loads the arena in the background; while it does the button is dimmed with its word trailing dots
    /// and the cards are locked.
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
        [Tooltip("Not drawn any more: the corners take their colours from Theme.uss. Kept because the scene builder sets them.")]
        public Color redColor = new Color(0.89f, 0.24f, 0.24f);
        public Color blueColor = new Color(0.20f, 0.49f, 0.94f);

        public int Red { get; private set; }
        public int Blue { get; private set; }
        public VisualElement Root => _root;
        public bool SettingsOpen => Showing(_sheet);
        public bool DebugOpen => Showing(_debugPanel);

        static readonly string[] BusyWords = { "FIGHT ·", "FIGHT ··", "FIGHT ···" };
        static readonly string[] DebugKeys = { "FRAME", "MEMORY", "SCREEN", "SAFE" };

        UIDocument _doc;
        VisualElement _root, _safe, _redHost, _sheet, _debugPanel;
        Label _versus, _fightWord;
        Button _fight, _fps, _melee;
        Button[][] _cards = new Button[2][];
        readonly Label[] _debugValues = new Label[4];
        readonly SafeArea _safeArea = new SafeArea();
        SettingsPage _settings;
        bool _leaving;
        float _smoothed = 1f / 60f, _nextText;

        void OnEnable()
        {
            if (Application.isPlaying)
            {
                Application.targetFrameRate = 60;
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
            Label version = _root.Q<Label>("version");
            if (version != null) version.text = "v" + Application.version;

            _redHost = _root.Q<VisualElement>("red-cards");
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
            if (_fps != null) _fps.clicked += ToggleDebug;
            // A tap on what is left of the screen puts away whatever is open, as on the HUD.
            VisualElement picture = _root.Q<VisualElement>("picture");
            if (picture != null) picture.RegisterCallback<PointerDownEvent>(_ => CloseAll());
            BuildDebugPanel();
            // The first screen has no ladder to reset: that is the arena's.
            _settings = new SettingsPage(_root, null);

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
            else if (_redHost != null && _redHost.childCount == 0 && boxers.Length > 0) { BuildCards(); Refresh(); }
            if (_root == null) return;
            _safeArea.Fit(_root, _safe);
            if (!Application.isPlaying) return;

            if (_leaving) Set(_fightWord, BusyWords[(int)(Time.unscaledTime * 3f) % BusyWords.Length]);
            _smoothed = Mathf.Lerp(_smoothed, Time.unscaledDeltaTime, 0.05f);
            if (Time.unscaledTime < _nextText) return;
            _nextText = Time.unscaledTime + 0.25f;      // four times a second: text made every frame is garbage every frame
            if (_fps != null) _fps.text = $"{1f / Mathf.Max(_smoothed, 1e-4f):0} FPS";
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
            BuildCards(0, _redHost);
            BuildCards(1, _root.Q<VisualElement>("blue-cards"));
        }

        void BuildCards(int corner, VisualElement host)
        {
            _cards[corner] = new Button[boxers.Length];
            if (host == null) return;
            host.Clear();
            for (int i = 0; i < boxers.Length; i++)
            {
                int index = i;
                Boxer b = boxers[i];
                var card = new Button(() => Pick(corner, index)) { name = (corner == 0 ? "red-" : "blue-") + b.name };
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

                host.Add(card);
                _cards[corner][i] = card;
            }
        }

        void BuildDebugPanel()
        {
            if (_debugPanel == null) return;
            _debugPanel.Clear();
            for (int i = 0; i < DebugKeys.Length; i++)
            {
                var cell = new VisualElement { pickingMode = PickingMode.Ignore };
                cell.AddToClassList("dbg-cell");
                var k = new Label(DebugKeys[i]) { pickingMode = PickingMode.Ignore };
                k.AddToClassList("dbg-key");
                _debugValues[i] = new Label("-") { pickingMode = PickingMode.Ignore };
                _debugValues[i].AddToClassList("dbg-val");
                cell.Add(k);
                cell.Add(_debugValues[i]);
                _debugPanel.Add(cell);
            }
        }

        void RefreshDebug()
        {
            Rect sa = SafeArea.Current;
            Set(_debugValues[0], $"{_smoothed * 1000f:0.0} ms");
            Set(_debugValues[1], $"{UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / (1024f * 1024f):0} MB");
            Set(_debugValues[2], $"{Screen.width}x{Screen.height}");
            Set(_debugValues[3], $"{sa.width:0}x{sa.height:0}");
        }

        static bool Showing(VisualElement e) => e != null && !e.ClassListContains("hidden");

        public void ToggleSettings() => Open(_sheet, !SettingsOpen);
        public void ToggleDebug() => Open(_debugPanel, !DebugOpen);
        void CloseAll() { Open(_sheet, false); Open(_debugPanel, false); }

        /// <summary>One at a time: they are docked in the same place.</summary>
        void Open(VisualElement which, bool open)
        {
            if (which == null) return;
            if (open)
            {
                VisualElement other = which == _sheet ? _debugPanel : _sheet;
                if (other != null) other.AddToClassList("hidden");
                if (which == _sheet && _settings != null) _settings.Refresh();
            }
            which.EnableInClassList("hidden", !open);
            if (open && which == _debugPanel) RefreshDebug();
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
            for (int corner = 0; corner < 2; corner++)
            {
                if (_cards[corner] == null) continue;
                int picked = corner == 0 ? Red : Blue;
                for (int i = 0; i < _cards[corner].Length; i++)
                    if (_cards[corner][i] != null) _cards[corner][i].EnableInClassList("pick--on", i == picked);
            }
            if (_versus != null && boxers.Length > 0) _versus.text = $"{boxers[Red].displayName}  v  {boxers[Blue].displayName}";
        }

        public void Fight()
        {
            // Once: the arena takes seconds to load, and a second tap in that time would load it twice.
            if (boxers.Length == 0 || !Application.isPlaying || _leaving) return;
            _leaving = true;
            CloseAll();
            if (_fight != null) _fight.AddToClassList("btn--busy");
            for (int corner = 0; corner < 2; corner++)
                if (_cards[corner] != null)
                    foreach (Button card in _cards[corner]) if (card != null) card.SetEnabled(false);
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
            if (_fight != null) _fight.SetEnabled(false);
            SceneManager.LoadSceneAsync(MatchSelection.MeleeScene);
        }

        static void Set(Label l, string text)
        {
            if (l != null && l.text != text) l.text = text;
        }
    }
}
