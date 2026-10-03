using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using PoBox.Audio;
using PoBox.Broadcast;
using PoBox.Diag;
using PoBox.Fx;
using PoBox.League;
using PoBox.Sim;

namespace PoBox.UI
{
    /// <summary>
    /// The whole interface, in one document, on one portrait screen, with nothing that scrolls.
    ///
    /// Five things never move: the game's name top-left, the frame rate top-centre, the menu top-right, DEBUG
    /// bottom-left and the version bottom-right. Between them: a scoreboard of three rows (the names
    /// written on the health bars with the clock between them; a tug-of-war win-probability bar; three
    /// chips, each a pair of numbers either side of an icon, the third cycling through five readings on a
    /// tap), the picture, and a dock at the bottom with one graph that pages sideways between excitement,
    /// win probability and frame time.
    ///
    /// Over the picture: a pod in each top corner showing where that fighter has been hit and where its
    /// balance is, what it has left in the tank and what it is doing this moment; the commentary as a caption
    /// along the bottom that fades when it has nothing to say; during the walk-on each fighter's name across
    /// the lower third; between rounds the three cards and the round's numbers; during a count an inset of
    /// the boxer left standing; and at a hard clean punch the glove's speed, written where it landed. A
    /// sideways drag on the picture takes the orbit camera and turns it.
    ///
    /// Everything else opens over that rather than replacing it, docked at the bottom where a thumb is and
    /// one at a time: the menu is a sheet with three tabs (a tap on the picture closes it), the debug
    /// readings are a panel that grows out of the DEBUG chip, and the end of a bout is a single sheet
    /// carrying the result, the three judges' cards, both fighters' numbers with a hit map each, and the
    /// win-probability graph of the whole bout. BOXERS, in the menu and on that sheet, goes back to the
    /// first screen to choose another pair.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class HudView : MonoBehaviour
    {
        public Bout bout;
        public LeagueTable league;
        public BroadcastDirector director;
        public Color redColor = new Color(0.91f, 0.29f, 0.29f);
        public Color blueColor = new Color(0.25f, 0.55f, 1f);

        const int StripPages = 3;
        const int CycleModes = 5;
        const int LeagueRows = 6;
        const int ResultRows = 5;

        VisualElement _root, _safe;

        // frame
        Button _fps, _menu, _debug;
        Label _version, _debugText;
        VisualElement _debugDot, _debugPanel;
        Glyph _menuGlyph;
        bool _leaving;
        readonly List<Label> _debugValues = new List<Label>();

        // board
        Label _redName, _blueName, _redBadge, _blueBadge, _round, _clock, _redHpText, _blueHpText, _momRed, _momBlue;
        VisualElement _redHp, _blueHp, _momFill, _board;
        Label _landedRed, _landedBlue, _peakRed, _peakBlue, _cycleRed, _cycleBlue;
        Glyph _cycleGlyph;
        int _cycle;

        // picture overlays
        Label _banner, _walkCorner, _walkName, _walkDetail;
        VisualElement _walkOn;
        HitMap _hitRed, _hitBlue, _resHitRed, _resHitBlue;
        BalanceGauge _balRed, _balBlue;
        AudioDirector _audio;
        System.Func<int, float> _sampleExcitement, _sampleShare;
        int _walkShown = -1;

        // pods: the tank and the action badge
        readonly VisualElement[] _tank = new VisualElement[2];
        readonly Label[] _act = new Label[2];
        readonly PunchType[] _punch = new PunchType[2];
        readonly float[] _punchAt = { -99f, -99f };
        static readonly string[] PunchNames = { "JAB", "CROSS", "HOOK", "UPPER", "BODY" };
        /// <summary>Seconds a punch's name stays on the badge.</summary>
        const float PunchShown = 0.45f;

        // between rounds, the inset, the speed of a punch
        VisualElement _break, _pip, _picture;
        Label _breakTitle, _flash;
        readonly Label[] _breakLanded = new Label[2], _breakBlocked = new Label[2];
        readonly VisualElement[] _breakCards = new VisualElement[Judges.Count];
        readonly Label[] _breakScores = new Label[Judges.Count];
        readonly FighterStats[] _roundStart = new FighterStats[2];
        [Tooltip("A clean punch this many times harder than these two usually land has its glove speed written where it landed.")]
        public float flashOverUsual = 1.25f;
        const float FlashSeconds = 0.9f;
        Vector3 _flashPoint;
        float _flashAt = -99f, _dragX;
        bool _dragging;

        // dock
        VisualElement _dock, _strip;
        Label _ticker, _stripTitle, _stripValue;
        Sparkline _stripGraph;
        readonly VisualElement[] _dots = new VisualElement[StripPages];
        int _stripPage;
        float _tickerAt = -99f, _stripDownX;
        int _tickerPriority;

        // results
        VisualElement _results;
        Label _resWinner, _resMethod, _resElo;
        Sparkline _resGraph;
        Button _resNext;
        readonly Label[] _resRed = new Label[ResultRows], _resBlue = new Label[ResultRows];
        readonly VisualElement[] _judgeCards = new VisualElement[Judges.Count];
        readonly Label[] _judgeScores = new Label[Judges.Count];

        // menu
        VisualElement _menuSheet;
        readonly Button[] _tabs = new Button[3];
        readonly VisualElement[] _pages = new VisualElement[3];
        readonly Button[] _camButtons = new Button[4], _speedButtons = new Button[2], _focusButtons = new Button[2];
        Button _sSound, _sQuality, _sFps, _sReset;
        readonly Label[][] _leagueCells = new Label[LeagueRows][];
        readonly VisualElement[] _leagueRows = new VisualElement[LeagueRows];
        float _resetArmedUntil;

        float _nextFast, _nextSlow;
        int _lastBout = -1;
        BoutPhase _lastPhase = (BoutPhase)(-1);
        Rect _lastSafe;
        Vector2Int _lastScreen;
        bool _scaleKnown;

        public bool MenuOpen => _menuSheet != null && !_menuSheet.ClassListContains("hidden");
        public bool DebugOpen => _debugPanel != null && !_debugPanel.ClassListContains("hidden");
        public bool ResultsOpen => _results != null && !_results.ClassListContains("hidden");
        public VisualElement Root => _root;

#if UNITY_EDITOR
        /// <summary>A pretend notch for the editor, in screen pixels. Null is the real safe area.</summary>
        public static Rect? SafeAreaOverride;
        static Rect SafeArea => SafeAreaOverride ?? Screen.safeArea;
#else
        static Rect SafeArea => Screen.safeArea;
#endif

        // ------------------------------------------------------------ build

        void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            if (_root == null) return;
            _safe = _root.Q<VisualElement>("safe");

            _fps = _root.Q<Button>("fps");
            _menu = _root.Q<Button>("menu");
            _menuGlyph = _root.Q<Glyph>("menu-glyph");
            _debug = _root.Q<Button>("debug");
            _version = _root.Q<Label>("version");
            _debugText = _root.Q<Label>("debug-text");
            _debugDot = _root.Q<VisualElement>("debug-dot");
            _debugPanel = _root.Q<VisualElement>("debug-panel");

            _board = _root.Q<VisualElement>("board");
            _redName = _root.Q<Label>("red-name"); _blueName = _root.Q<Label>("blue-name");
            _redBadge = _root.Q<Label>("red-badge"); _blueBadge = _root.Q<Label>("blue-badge");
            _round = _root.Q<Label>("round"); _clock = _root.Q<Label>("clock");
            _redHp = _root.Q<VisualElement>("red-hp"); _blueHp = _root.Q<VisualElement>("blue-hp");
            _redHpText = _root.Q<Label>("red-hp-text"); _blueHpText = _root.Q<Label>("blue-hp-text");
            _momFill = _root.Q<VisualElement>("momentum-fill");
            _momRed = _root.Q<Label>("momentum-red"); _momBlue = _root.Q<Label>("momentum-blue");
            _landedRed = _root.Q<Label>("landed-red"); _landedBlue = _root.Q<Label>("landed-blue");
            _peakRed = _root.Q<Label>("peak-red"); _peakBlue = _root.Q<Label>("peak-blue");
            _cycleRed = _root.Q<Label>("cycle-red"); _cycleBlue = _root.Q<Label>("cycle-blue");
            _cycleGlyph = _root.Q<Glyph>("cycle-glyph");

            _banner = _root.Q<Label>("banner");
            _walkOn = _root.Q<VisualElement>("walkon");
            _walkCorner = _root.Q<Label>("walkon-corner");
            _walkName = _root.Q<Label>("walkon-name");
            _walkDetail = _root.Q<Label>("walkon-detail");
            _hitRed = _root.Q<HitMap>("hit-red"); _hitBlue = _root.Q<HitMap>("hit-blue");
            _balRed = _root.Q<BalanceGauge>("bal-red"); _balBlue = _root.Q<BalanceGauge>("bal-blue");
            _resHitRed = _root.Q<HitMap>("res-hit-red"); _resHitBlue = _root.Q<HitMap>("res-hit-blue");

            string[] corners = { "red", "blue" };
            for (int i = 0; i < 2; i++)
            {
                _tank[i] = _root.Q<VisualElement>("tank-" + corners[i]);
                _act[i] = _root.Q<Label>("act-" + corners[i]);
                _breakLanded[i] = _root.Q<Label>("break-landed-" + corners[i]);
                _breakBlocked[i] = _root.Q<Label>("break-blocked-" + corners[i]);
            }
            _break = _root.Q<VisualElement>("break");
            _breakTitle = _root.Q<Label>("break-title");
            _pip = _root.Q<VisualElement>("pip");
            _flash = _root.Q<Label>("flash");
            _picture = _root.Q<VisualElement>("picture");
            if (_pip != null && director != null && director.pipCam != null && director.pipCam.targetTexture != null)
                _pip.style.backgroundImage = Background.FromRenderTexture(director.pipCam.targetTexture);

            _dock = _root.Q<VisualElement>("dock");
            _strip = _root.Q<VisualElement>("strip");
            _ticker = _root.Q<Label>("ticker");
            _stripTitle = _root.Q<Label>("strip-title");
            _stripValue = _root.Q<Label>("strip-value");
            _stripGraph = _root.Q<Sparkline>("strip-graph");
            for (int i = 0; i < StripPages; i++) _dots[i] = _root.Q<VisualElement>("dot" + i);

            _results = _root.Q<VisualElement>("results");
            _resWinner = _root.Q<Label>("res-winner"); _resMethod = _root.Q<Label>("res-method");
            _resElo = _root.Q<Label>("res-elo");
            _resGraph = _root.Q<Sparkline>("res-graph");
            _resNext = _root.Q<Button>("res-next");

            _menuSheet = _root.Q<VisualElement>("menu-sheet");
            string[] tabNames = { "match", "league", "settings" };
            for (int i = 0; i < 3; i++)
            {
                int tab = i;
                _tabs[i] = _root.Q<Button>("tab-" + tabNames[i]);
                _pages[i] = _root.Q<VisualElement>("page-" + tabNames[i]);
                if (_tabs[i] != null) _tabs[i].clicked += () => ShowTab(tab);
            }

            if (_version != null) _version.text = "v" + Application.version;

            Hook(_menu, ToggleMenu);
            Hook(_debug, ToggleDebug);
            Hook(_fps, () => { SetStripPage(2); if (!DebugOpen) ToggleDebug(); });
            Hook(_root.Q<Button>("cycle"), () => { _cycle = (_cycle + 1) % CycleModes; RefreshCycleKey(); RefreshTape(); });

            BuildResultsTape();
            BuildJudges("res-judges", _judgeCards, _judgeScores);
            BuildJudges("break-judges", _breakCards, _breakScores);
            BuildDebugPanel();
            BuildLeagueRows();
            HookMenu();
            HookStrip();
            HookResults();
            HookPicture();

            RefreshCycleKey();
            SetStripPage(0);
            ShowTab(0);
            LoadSettings();
            ApplySafeArea();

            SimBus.Line += OnLine;
            SimBus.PunchThrown += OnPunch;
            SimBus.Hit += OnHit;
        }

        void OnDisable()
        {
            SimBus.Line -= OnLine;
            SimBus.PunchThrown -= OnPunch;
            SimBus.Hit -= OnHit;
        }

        static void Hook(Button b, System.Action action)
        {
            if (b != null) b.clicked += action;
        }

        void BuildResultsTape()
        {
            VisualElement host = _root.Q<VisualElement>("res-tape");
            if (host == null) return;
            host.Clear();
            GlyphKind[] kinds = { GlyphKind.Glove, GlyphKind.Shield, GlyphKind.Burst, GlyphKind.Bolt, GlyphKind.Heat };
            string[] codes = { "LANDED", "BLOCKED", "PEAK", "ENERGY", "DAMAGE" };
            for (int i = 0; i < ResultRows; i++)
            {
                var row = new VisualElement { pickingMode = PickingMode.Ignore };
                row.AddToClassList("tape-row");
                _resRed[i] = TapeValue(true);
                _resBlue[i] = TapeValue(false);
                row.Add(_resRed[i]);
                row.Add(TapeKey(kinds[i], codes[i]));
                row.Add(_resBlue[i]);
                host.Add(row);
            }
        }

        static Label TapeValue(bool left)
        {
            var l = new Label("0") { pickingMode = PickingMode.Ignore };
            l.AddToClassList("tape-val");
            l.AddToClassList(left ? "tape-val--l" : "tape-val--r");
            return l;
        }

        static VisualElement TapeKey(GlyphKind kind, string code)
        {
            var key = new VisualElement { pickingMode = PickingMode.Ignore };
            key.AddToClassList("tape-key");
            var g = new Glyph { kind = kind };
            g.AddToClassList("glyph");
            var c = new Label(code) { pickingMode = PickingMode.Ignore };
            c.AddToClassList("tape-code");
            key.Add(g);
            key.Add(c);
            return key;
        }

        /// <summary>One card a judge: the judge's name over the score, red's first.</summary>
        void BuildJudges(string hostName, VisualElement[] cards, Label[] scores)
        {
            VisualElement host = _root.Q<VisualElement>(hostName);
            if (host == null) return;
            host.Clear();
            for (int j = 0; j < Judges.Count; j++)
            {
                var card = new VisualElement { pickingMode = PickingMode.Ignore };
                card.AddToClassList("judge");
                var name = new Label(Judges.Names[j]) { pickingMode = PickingMode.Ignore };
                name.AddToClassList("judge-name");
                var score = new Label("-") { pickingMode = PickingMode.Ignore };
                score.AddToClassList("judge-card");
                card.Add(name);
                card.Add(score);
                host.Add(card);
                cards[j] = card;
                scores[j] = score;
            }
        }

        static readonly string[] DebugKeys =
        {
            "FRAME", "WORST", "PHYSICS", "SETPASS", "TRIS", "GC", "MEMORY", "SCALE", "SHOT", "VOICES", "CLOSING", "STRESS",
            "DAZE", "CRITIC", "SOUND", "USUAL",
        };

        void BuildDebugPanel()
        {
            if (_debugPanel == null) return;
            _debugPanel.Clear();
            _debugValues.Clear();
            foreach (string key in DebugKeys)
            {
                var cell = new VisualElement { pickingMode = PickingMode.Ignore };
                cell.AddToClassList("dbg-cell");
                var k = new Label(key) { pickingMode = PickingMode.Ignore };
                k.AddToClassList("dbg-key");
                var v = new Label("-") { pickingMode = PickingMode.Ignore };
                v.AddToClassList("dbg-val");
                cell.Add(k);
                cell.Add(v);
                _debugPanel.Add(cell);
                _debugValues.Add(v);
            }
        }

        void BuildLeagueRows()
        {
            VisualElement host = _pages[1];
            if (host == null) return;
            host.Clear();
            string[] classes = { "lg-rank", "lg-name", "lg-badge", "lg-elo", "lg-record" };
            for (int r = 0; r < LeagueRows; r++)
            {
                var row = new VisualElement { pickingMode = PickingMode.Ignore };
                row.AddToClassList("lg-row");
                _leagueCells[r] = new Label[classes.Length];
                for (int c = 0; c < classes.Length; c++)
                {
                    var l = new Label("") { pickingMode = PickingMode.Ignore };
                    l.AddToClassList("lg-cell");
                    l.AddToClassList(classes[c]);
                    row.Add(l);
                    _leagueCells[r][c] = l;
                }
                host.Add(row);
                _leagueRows[r] = row;
            }
        }

        void HookMenu()
        {
            Hook(_root.Q<Button>("m-restart"), () => { if (bout != null) bout.Restart(); CloseMenu(); });
            Hook(_root.Q<Button>("m-next"), () => { if (bout != null) bout.NewBout(); CloseMenu(); });
            HookBoxers(_root.Q<Button>("m-boxers"));

            string[] cams = { "cam-auto", "cam-wide", "cam-orbit", "cam-top" };
            Shot?[] shots = { null, Shot.Wide, Shot.Orbit, Shot.Overhead };
            for (int i = 0; i < cams.Length; i++)
            {
                int index = i;
                _camButtons[i] = _root.Q<Button>(cams[i]);
                Hook(_camButtons[i], () => { if (director != null) director.Pin(shots[index]); RefreshMenu(); });
            }

            for (int i = 0; i < 2; i++)
            {
                int corner = i;
                _focusButtons[i] = _root.Q<Button>(i == 0 ? "cam-red" : "cam-blue");
                Hook(_focusButtons[i], () => { if (director != null) director.Focus(corner); RefreshMenu(); });
            }

            string[] speeds = { "spd-one", "spd-two" };
            float[] scales = { 1f, 2f };
            for (int i = 0; i < speeds.Length; i++)
            {
                int index = i;
                _speedButtons[i] = _root.Q<Button>(speeds[i]);
                Hook(_speedButtons[i], () => { if (bout != null) bout.userTimeScale = scales[index]; RefreshMenu(); });
            }

            _sSound = _root.Q<Button>("s-sound");
            _sQuality = _root.Q<Button>("s-quality");
            _sFps = _root.Q<Button>("s-fps");
            _sReset = _root.Q<Button>("s-reset");

            Hook(_sSound, () => { AudioDirector.Muted = !AudioDirector.Muted; SaveSettings(); RefreshMenu(); });
            Hook(_sQuality, () =>
            {
                int n = QualitySettings.names.Length;
                if (n > 0) QualitySettings.SetQualityLevel((QualitySettings.GetQualityLevel() + 1) % n, true);
                SaveSettings(); RefreshMenu();
            });
            Hook(_sFps, () =>
            {
                PerfTelemetry perf = PerfTelemetry.Instance;
                if (perf != null) perf.SetTargetFps(perf.targetFps >= 60 ? 30 : 60);
                SaveSettings(); RefreshMenu();
            });
            Hook(_sReset, () =>
            {
                // Twice, because it throws away every result the ladder has.
                if (Time.unscaledTime < _resetArmedUntil)
                {
                    if (league != null) league.ResetAll();
                    _resetArmedUntil = 0f;
                }
                else _resetArmedUntil = Time.unscaledTime + 3f;
                RefreshMenu();
            });
        }

        void HookStrip()
        {
            if (_strip == null) return;
            // A sideways swipe pages the graph; a tap goes to the next one.
            _strip.RegisterCallback<PointerDownEvent>(e => _stripDownX = e.position.x);
            _strip.RegisterCallback<PointerUpEvent>(e =>
            {
                float dx = e.position.x - _stripDownX;
                int step = Mathf.Abs(dx) < 40f ? 1 : dx < 0f ? 1 : -1;
                SetStripPage((_stripPage + step + StripPages) % StripPages);
            });
        }

        /// <summary>A sideways drag on the picture turns the orbit camera, taking it on air first. AUTO in the menu gives the cameras back.</summary>
        void HookPicture()
        {
            if (_picture == null) return;
            _picture.RegisterCallback<PointerDownEvent>(e =>
            {
                // The menu is a sheet over the picture: a tap on what is left of the picture puts it away.
                if (MenuOpen) { CloseMenu(); return; }
                _dragX = e.position.x;
                _dragging = true;
                _picture.CapturePointer(e.pointerId);
            });
            _picture.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (!_dragging || director == null) return;
                float dx = e.position.x - _dragX;
                // A tap, or a finger resting, is not a drag: the cameras are only taken by a real one.
                if (director.Pinned != Shot.Orbit && Mathf.Abs(dx) < 24f) return;
                _dragX = e.position.x;
                director.TurnOrbit(-dx * 0.25f);
            });
            _picture.RegisterCallback<PointerUpEvent>(e =>
            {
                if (!_dragging) return;
                _dragging = false;
                _picture.ReleasePointer(e.pointerId);
                RefreshMenu();
            });
        }

        void HookResults()
        {
            HookBoxers(_root.Q<Button>("res-boxers"));
            Hook(_resNext, () => { if (bout != null) bout.NewBout(); });
        }

        /// <summary>Back to the first screen to choose another pair. Not offered when the build has no such screen.</summary>
        void HookBoxers(Button b)
        {
            if (b == null) return;
            b.EnableInClassList("hidden", !MatchSelection.HasMenu);
            b.clicked += ToBoxers;
        }

        /// <summary>Leaves for the first screen, once: a second tap while the scene is loading would load it twice.</summary>
        public void ToBoxers()
        {
            if (_leaving || !MatchSelection.HasMenu) return;
            _leaving = true;
            SceneManager.LoadScene(MatchSelection.MenuScene);
        }

        // ------------------------------------------------------------ settings

        void LoadSettings()
        {
            AudioDirector.Muted = PlayerPrefs.GetInt("pobox.muted", 0) == 1;
            PerfTelemetry perf = PerfTelemetry.Instance;
            if (perf != null) perf.SetTargetFps(PlayerPrefs.GetInt("pobox.fps", perf.targetFps));
            RefreshMenu();
        }

        void SaveSettings()
        {
            PlayerPrefs.SetInt("pobox.muted", AudioDirector.Muted ? 1 : 0);
            if (PerfTelemetry.Instance != null) PlayerPrefs.SetInt("pobox.fps", PerfTelemetry.Instance.targetFps);
            PlayerPrefs.Save();
        }

        // ------------------------------------------------------------ sheets

        public void ToggleMenu()
        {
            if (MenuOpen) CloseMenu(); else OpenMenu(0);
        }

        public void OpenMenu(int tab)
        {
            if (_menuSheet == null) return;
            // One thing open at a time: the three of them are docked in the same place.
            if (DebugOpen) ToggleDebug();
            _menuSheet.RemoveFromClassList("hidden");
            if (_menuGlyph != null) _menuGlyph.kind = GlyphKind.Close;
            ShowResults();
            ShowTab(tab);
        }

        /// <summary>The results card is up when the bout is over and neither the menu nor the debug panel is in its place.</summary>
        void ShowResults()
        {
            if (_results != null) _results.EnableInClassList("hidden", bout == null || bout.Phase != BoutPhase.Results || MenuOpen || DebugOpen);
        }

        public void CloseMenu()
        {
            if (_menuSheet == null) return;
            _menuSheet.AddToClassList("hidden");
            if (_menuGlyph != null) _menuGlyph.kind = GlyphKind.Menu;
            ShowResults();
        }

        public void ShowTab(int tab)
        {
            for (int i = 0; i < 3; i++)
            {
                if (_tabs[i] != null) _tabs[i].EnableInClassList("seg-btn--on", i == tab);
                if (_pages[i] != null) _pages[i].EnableInClassList("hidden", i != tab);
            }
            if (tab == 1) RefreshLeague();
            RefreshMenu();
        }

        public void ToggleDebug()
        {
            if (_debugPanel == null) return;
            if (!DebugOpen) CloseMenu();
            _debugPanel.ToggleInClassList("hidden");
            ShowResults();
            RefreshDebug();
        }

        public void SetStripPage(int page)
        {
            _stripPage = Mathf.Clamp(page, 0, StripPages - 1);
            for (int i = 0; i < StripPages; i++)
                if (_dots[i] != null) _dots[i].EnableInClassList("dot--on", i == _stripPage);
            RefreshStrip();
        }

        void RefreshMenu()
        {
            if (_root == null) return;
            Shot? pinned = director != null ? director.Pinned : null;
            Shot?[] shots = { null, Shot.Wide, Shot.Orbit, Shot.Overhead };
            for (int i = 0; i < _camButtons.Length; i++)
                if (_camButtons[i] != null) _camButtons[i].EnableInClassList("btn--on", pinned == shots[i]);
            for (int i = 0; i < 2; i++)
                if (_focusButtons[i] != null) _focusButtons[i].EnableInClassList("btn--on", director != null && director.Focused == i);

            float scale = bout != null ? bout.userTimeScale : 1f;
            float[] scales = { 1f, 2f };
            for (int i = 0; i < _speedButtons.Length; i++)
                if (_speedButtons[i] != null) _speedButtons[i].EnableInClassList("btn--on", Mathf.Approximately(scale, scales[i]));

            Toggle(_sSound, "SOUND", !AudioDirector.Muted);
            if (_sQuality != null)
            {
                string[] names = QualitySettings.names;
                int level = QualitySettings.GetQualityLevel();
                _sQuality.text = "QUALITY " + (level >= 0 && level < names.Length ? names[level].ToUpperInvariant() : "-");
            }
            if (_sFps != null) _sFps.text = (PerfTelemetry.Instance != null ? PerfTelemetry.Instance.targetFps : 60) + " FPS";
            if (_sReset != null)
            {
                bool armed = Time.unscaledTime < _resetArmedUntil;
                _sReset.text = armed ? "SURE?" : "RESET LADDER";
                _sReset.EnableInClassList("btn--danger", armed);
            }
        }

        static void Toggle(Button b, string label, bool on)
        {
            if (b == null) return;
            b.text = label + (on ? " ON" : " OFF");
            b.EnableInClassList("btn--on", on);
        }

        void RefreshLeague()
        {
            if (league == null) return;
            List<LeagueTable.Row> rows = league.Sorted();
            for (int r = 0; r < LeagueRows; r++)
            {
                bool has = r < rows.Count;
                if (_leagueRows[r] != null) _leagueRows[r].style.visibility = has ? Visibility.Visible : Visibility.Hidden;
                if (!has || _leagueCells[r] == null) continue;
                LeagueTable.Row row = rows[r];
                PolicyProfile p = league.ProfileFor(row.name);
                _leagueCells[r][0].text = (r + 1).ToString();
                _leagueCells[r][1].text = row.name;
                _leagueCells[r][2].text = p != null ? p.Badge : "";
                _leagueCells[r][3].text = row.elo.ToString("0");
                _leagueCells[r][4].text = $"{row.wins}-{row.losses}-{row.draws}";
                bool live = bout != null && ((bout.red != null && bout.red.displayName == row.name) || (bout.blue != null && bout.blue.displayName == row.name));
                _leagueRows[r].EnableInClassList("lg-row--live", live);
            }
        }

        // ------------------------------------------------------------ per frame

        void OnLine(string text, int priority)
        {
            // A statistic does not talk over a knockdown call that has only just gone up.
            if (priority < _tickerPriority && Time.unscaledTime - _tickerAt < 2.5f) return;
            _tickerPriority = priority;
            _tickerAt = Time.unscaledTime;
            if (_ticker != null) _ticker.text = text;
        }

        void OnPunch(Fighter f, PunchType type, int hand)
        {
            if (bout == null) return;
            int corner = f == bout.blue ? 1 : 0;
            _punch[corner] = type;
            _punchAt[corner] = Time.time;
        }

        void OnHit(HitEvent e)
        {
            Excitement ex = Excitement.Instance;
            if (!e.clean || ex == null || e.impulse < ex.UsualImpulse * flashOverUsual || _flash == null) return;
            _flashPoint = e.point;
            _flashAt = Time.unscaledTime;
            _flash.text = $"{e.gloveSpeed:0.0} m/s";
        }

        void Update()
        {
            if (_root == null) return;
            if (!_scaleKnown || _lastSafe != SafeArea || _lastScreen.x != Screen.width || _lastScreen.y != Screen.height) ApplySafeArea();
            if (bout == null) return;

            float now = Time.unscaledTime;
            if (bout.BoutNumber != _lastBout || bout.Phase != _lastPhase)
            {
                _lastBout = bout.BoutNumber;
                OnPhaseChanged(bout.Phase);
                _lastPhase = bout.Phase;
            }

            RefreshBars();
            RefreshOverlays(now);

            if (now >= _nextFast)
            {
                _nextFast = now + 0.1f;
                RefreshClock();
                RefreshTape();
                RefreshPods();
                if (_stripPage == 2) RefreshStrip();
                if (ResultsOpen) RefreshNext();
            }

            if (now >= _nextSlow)
            {
                _nextSlow = now + 0.25f;
                RefreshFrame();
                if (_stripPage != 2) RefreshStrip();
                if (DebugOpen) RefreshDebug();
                ReportPictureBand();
                if (MenuOpen && now < _resetArmedUntil + 0.3f) RefreshMenu();
            }

            // The caption is there while it is news, and gone after: five seconds, then a second of fading.
            if (_ticker != null)
            {
                float opacity = ResultsOpen || MenuOpen || string.IsNullOrEmpty(_ticker.text) ? 0f : Mathf.Clamp01(6f - (now - _tickerAt));
                if (!Mathf.Approximately(opacity, _tickerOpacity)) { _tickerOpacity = opacity; _ticker.style.opacity = opacity; }
            }
        }

        float _tickerOpacity = -1f;

        /// <summary>
        /// Tells the cameras which part of the screen is not under interface: from the bottom of the
        /// scoreboard to the top of the dock, or to the top of the results card or the menu when one is up.
        /// </summary>
        void ReportPictureBand()
        {
            if (director == null || _board == null || _root.panel == null) return;
            float height = _root.panel.visualTree.layout.height;
            if (!(height > 1f)) return;
            VisualElement below = ResultsOpen ? _results : MenuOpen ? _menuSheet : _dock;
            if (below == null) return;
            float top = _board.worldBound.yMax / height, bottom = below.worldBound.yMin / height;
            if (bottom > top + 0.1f) director.SetPictureBand(top, bottom);
        }

        void OnPhaseChanged(BoutPhase phase)
        {
            Fighter red = bout.red, blue = bout.blue;
            if (red != null && _redName != null)
            {
                _redName.text = red.displayName;
                if (_redBadge != null) _redBadge.text = Badge(red);
            }
            if (blue != null && _blueName != null)
            {
                _blueName.text = blue.displayName;
                if (_blueBadge != null) _blueBadge.text = Badge(blue);
            }

            bool results = phase == BoutPhase.Results;
            ShowResults();
            if (_dock != null) _dock.EnableInClassList("hidden", results);
            // The card carries both hit maps; the pods would say the same thing twice.
            if (_hitRed != null && _hitRed.parent != null) _hitRed.parent.EnableInClassList("hidden", results);
            if (_hitBlue != null && _hitBlue.parent != null) _hitBlue.parent.EnableInClassList("hidden", results);

            // A round starts: what each has done so far, so the break can show the round alone.
            if (phase == BoutPhase.Intro)
            {
                if (red != null) _roundStart[0] = red.stats;
                if (blue != null) _roundStart[1] = blue.stats;
            }
            if (_break != null) _break.EnableInClassList("hidden", phase != BoutPhase.RoundBreak);
            if (phase == BoutPhase.RoundBreak) FillBreak();
            if (_pip != null)
            {
                _pip.EnableInClassList("hidden", phase != BoutPhase.Count || director == null || director.pipCam == null);
                // The frame is the colour of the corner of the boxer in it: the one left standing.
                _pip.EnableInClassList("pip--blue", bout.Downed == red);
            }

            if (results) FillResults();
            if (MenuOpen && _pages[1] != null && !_pages[1].ClassListContains("hidden")) RefreshLeague();
        }

        string Badge(Fighter f)
        {
            // The plate has room for the name and a rank; the rating and the rest are on the walk-on card.
            if (league != null && league.Find(f.displayName) != null) return "#" + league.RankOf(f.displayName);
            return f.profile != null && f.profile.IsTrained ? "AI" : "BOT";
        }

        /// <summary>The line under a fighter's name on the walk-on card: rank, rating, record, weight, how long it trained.</summary>
        string WalkOnDetail(Fighter f)
        {
            var s = new System.Text.StringBuilder();
            LeagueTable.Row row = league != null ? league.Find(f.displayName) : null;
            if (row != null) s.Append($"#{league.RankOf(f.displayName)} · {row.elo:0} ELO · {row.wins}-{row.losses}-{row.draws}");
            if (f.totalMass > 0f) s.Append($"{(s.Length > 0 ? " · " : "")}{f.totalMass:0} kg");
            if (f.profile != null && f.profile.IsTrained) s.Append($" · GEN {f.profile.generation}");
            return s.ToString();
        }

        void RefreshBars()
        {
            Fighter red = bout.red, blue = bout.blue;
            if (red == null || blue == null) return;
            if (_redHp != null) _redHp.style.width = Length.Percent(red.Health);
            if (_blueHp != null) _blueHp.style.width = Length.Percent(blue.Health);
            if (_momFill != null) _momFill.style.width = Length.Percent(Excitement.RedShare * 100f);
        }

        void RefreshClock()
        {
            if (_round != null) _round.text = "R" + bout.Round;
            if (_clock != null) _clock.text = Bout.ClockText(bout.RoundTimeLeft);
            Fighter red = bout.red, blue = bout.blue;
            if (red == null || blue == null) return;
            if (_redHpText != null) _redHpText.text = Mathf.CeilToInt(red.Health).ToString();
            if (_blueHpText != null) _blueHpText.text = Mathf.CeilToInt(blue.Health).ToString();
            int share = Mathf.RoundToInt(Excitement.RedShare * 100f);
            if (_momRed != null) _momRed.text = share + "%";
            if (_momBlue != null) _momBlue.text = (100 - share) + "%";
        }

        void RefreshCycleKey()
        {
            if (_cycleGlyph != null) _cycleGlyph.kind = CycleKinds[_cycle];
        }

        // Energy spent, punches stopped on the guard, how dazed, the hardest-working joint, power now.
        static readonly GlyphKind[] CycleKinds = { GlyphKind.Bolt, GlyphKind.Shield, GlyphKind.Daze, GlyphKind.Heat, GlyphKind.Power };

        void RefreshTape()
        {
            Fighter red = bout.red, blue = bout.blue;
            if (red == null || blue == null) return;
            Set(_landedRed, $"{red.stats.landed}/{red.stats.thrown}");
            Set(_landedBlue, $"{blue.stats.landed}/{blue.stats.thrown}");
            Set(_peakRed, $"{red.stats.peakImpulse:0} N·s");
            Set(_peakBlue, $"{blue.stats.peakImpulse:0} N·s");
            Set(_cycleRed, CycleText(red));
            Set(_cycleBlue, CycleText(blue));
        }

        string CycleText(Fighter f)
        {
            switch (_cycle)
            {
                case 0: return $"{f.stats.energyJ / 1000f:0.0} kJ";
                case 1: return f.stats.blocked.ToString();
                case 2: return $"{f.Daze01 * 100f:0}%";
                case 3: return $"{f.PeakStress * 100f:0}%";
                default: return $"{f.PowerW:0} W";
            }
        }

        /// <summary>The two pods: where each fighter has been hit, how dazed it is, and its balance.</summary>
        void RefreshPods()
        {
            Fighter red = bout.red, blue = bout.blue;
            if (red == null || blue == null) return;
            float scale = HitScale(red, blue);
            if (_hitRed != null) _hitRed.Set(red.taken, scale, red.Daze01);
            if (_hitBlue != null) _hitBlue.Set(blue.taken, scale, blue.Daze01);
            if (_balRed != null) _balRed.Set(red.footOffset, red.footDown, red.CaptureOffset, red.BalanceMargin, red.IsDown && !red.IsRising);
            if (_balBlue != null) _balBlue.Set(blue.footOffset, blue.footDown, blue.CaptureOffset, blue.BalanceMargin, blue.IsDown && !blue.IsRising);
            RefreshStatus(0, red);
            RefreshStatus(1, blue);
        }

        /// <summary>The tank, and the badge under it: down, getting up, dazed, the punch just thrown, or in its guard.</summary>
        void RefreshStatus(int corner, Fighter f)
        {
            if (_tank[corner] != null)
            {
                _tank[corner].style.width = Length.Percent(f.Stamina * 100f);
                _tank[corner].EnableInClassList("tank-fill--low", f.Stamina < 0.35f);
            }
            if (_act[corner] == null) return;
            bool punching = Time.time - _punchAt[corner] < PunchShown;
            bool alarm = f.IsDown || f.Staggered;
            Set(_act[corner], f.IsRising ? "RISING" : f.IsDown ? "DOWN" : f.Staggered ? "DAZED" : punching ? PunchNames[(int)_punch[corner]] : "GUARD");
            _act[corner].EnableInClassList("act--alarm", alarm);
            _act[corner].EnableInClassList("act--punch", punching && !alarm);
        }

        /// <summary>Between rounds: the cards as they stand, and landed of thrown and punches stopped in the round just ended.</summary>
        void FillBreak()
        {
            Set(_breakTitle, "END OF ROUND " + bout.Round);
            for (int j = 0; j < Judges.Count; j++)
            {
                if (_breakCards[j] == null) continue;
                int r = bout.judges.total[j, 0], b = bout.judges.total[j, 1];
                Set(_breakScores[j], bout.judges.Card(j));
                _breakCards[j].EnableInClassList("judge--red", r > b);
                _breakCards[j].EnableInClassList("judge--blue", b > r);
            }
            for (int i = 0; i < 2; i++)
            {
                Fighter f = i == 0 ? bout.red : bout.blue;
                if (f == null) continue;
                Set(_breakLanded[i], $"{f.stats.landed - _roundStart[i].landed}/{f.stats.thrown - _roundStart[i].thrown}");
                Set(_breakBlocked[i], (f.stats.blocked - _roundStart[i].blocked).ToString());
            }
        }

        /// <summary>The impulse that is drawn fully hot: the most that has landed on any one part of either fighter, and never less than a few punches' worth.</summary>
        static float HitScale(Fighter red, Fighter blue)
        {
            float most = 40f;
            for (int i = 0; i < red.taken.Length; i++) most = Mathf.Max(most, Mathf.Max(red.taken[i], blue.taken[i]));
            return most;
        }

        static void Set(Label l, string text)
        {
            if (l != null && l.text != text) l.text = text;
        }

        void RefreshOverlays(float now)
        {
            string banner = null;
            switch (bout.Phase)
            {
                case BoutPhase.Intro: banner = "ROUND " + bout.Round; break;
                case BoutPhase.Count: if (bout.Count > 0) banner = bout.Count.ToString(); break;
            }

            // The walk-on: red's name for the first half of it, blue's for the second.
            int walk = bout.Phase != BoutPhase.WalkOn ? -1 : bout.PhaseAge < bout.walkOnSeconds * 0.5f ? 0 : 1;
            if (walk != _walkShown && _walkOn != null)
            {
                _walkShown = walk;
                _walkOn.EnableInClassList("hidden", walk < 0);
                _walkOn.EnableInClassList("walkon--blue", walk == 1);
                Fighter f = walk == 0 ? bout.red : walk == 1 ? bout.blue : null;
                if (f != null)
                {
                    Set(_walkCorner, walk == 0 ? "RED CORNER" : "BLUE CORNER");
                    Set(_walkName, f.displayName);
                    Set(_walkDetail, WalkOnDetail(f));
                }
            }
            // The speed of a hard punch, where it landed, rising a little as it fades.
            if (_flash != null)
            {
                float age = now - _flashAt;
                Camera cam = director != null ? director.mainCamera : null;
                bool show = age < FlashSeconds && cam != null && _safe != null && !ResultsOpen && !MenuOpen;
                _flash.EnableInClassList("hidden", !show);
                if (show)
                {
                    Vector3 v = cam.WorldToViewportPoint(_flashPoint);
                    Rect area = _safe.layout;
                    _flash.style.left = Mathf.Clamp(v.x * area.width - 100f, 0f, area.width - 200f);
                    _flash.style.top = Mathf.Clamp((1f - v.y) * area.height - 90f - age * 60f, 0f, area.height - 48f);
                    _flash.style.opacity = Mathf.Clamp01((FlashSeconds - age) * 4f);
                }
            }
            if (_banner != null)
            {
                _banner.EnableInClassList("hidden", banner == null);
                _banner.EnableInClassList("banner--count", bout.Phase == BoutPhase.Count);
                if (banner != null) Set(_banner, banner);
            }
        }

        void RefreshFrame()
        {
            PerfTelemetry perf = PerfTelemetry.Instance;
            if (perf == null) return;
            if (_fps != null)
            {
                _fps.text = $"{perf.Fps:0} FPS";
                _fps.EnableInClassList("frame-fps--warn", perf.Grade == 1);
                _fps.EnableInClassList("frame-fps--bad", perf.Grade == 2);
            }
            if (_debugDot != null)
            {
                _debugDot.EnableInClassList("dot--warn", perf.Grade == 1);
                _debugDot.EnableInClassList("dot--bad", perf.Grade == 2);
            }
        }

        void RefreshStrip()
        {
            if (_stripGraph == null) return;
            Excitement ex = Excitement.Instance;
            PerfTelemetry perf = PerfTelemetry.Instance;
            switch (_stripPage)
            {
                case 0:
                    Set(_stripTitle, "EXCITEMENT");
                    Set(_stripValue, $"{Excitement.Value * 100f:0}%");
                    _stripGraph.baseline = float.NaN;
                    _stripGraph.fillFromBaseline = false;
                    _stripGraph.lineColor = new Color(1f, 0.78f, 0.33f);
                    _stripGraph.fillColor = new Color(1f, 0.78f, 0.33f, 0.16f);
                    if (ex != null) _stripGraph.Set(_sampleExcitement ??= i => Excitement.Instance.Sample(Excitement.Instance.excitementHistory, i), ex.HistoryCount, 0f, 1f);
                    break;
                case 1:
                    // The cards, what each has left, and, with trained fighters, what their critics expect.
                    Set(_stripTitle, ex != null && ex.HasCritic ? "WIN CHANCE · AI" : "WIN CHANCE");
                    Set(_stripValue, $"{Excitement.RedShare * 100f:0}% {(bout.red != null ? bout.red.displayName : "RED")}");
                    _stripGraph.baseline = 0.5f;
                    _stripGraph.fillFromBaseline = true;
                    _stripGraph.lineColor = Color.white;
                    _stripGraph.fillColor = new Color(1f, 1f, 1f, 0.12f);
                    if (ex != null) _stripGraph.Set(_sampleShare ??= i => Excitement.Instance.Sample(Excitement.Instance.shareHistory, i), ex.HistoryCount, 0f, 1f);
                    break;
                default:
                    Set(_stripTitle, "FRAME TIME");
                    Set(_stripValue, perf != null ? $"{perf.FrameMs:0.0} ms · worst {perf.WorstMs:0}" : "-");
                    _stripGraph.baseline = perf != null ? 1000f / Mathf.Max(15, perf.targetFps) : 16.7f;
                    _stripGraph.fillFromBaseline = false;
                    _stripGraph.lineColor = new Color(0.18f, 0.75f, 0.55f);
                    _stripGraph.fillColor = new Color(0.18f, 0.75f, 0.55f, 0.16f);
                    if (perf != null) _stripGraph.Set(perf.Sample, PerfTelemetry.HistoryLength, 0f, 50f);
                    break;
            }
        }

        void RefreshDebug()
        {
            PerfTelemetry perf = PerfTelemetry.Instance;
            if (perf == null || _debugValues.Count < DebugKeys.Length) return;
            Fighter red = bout != null ? bout.red : null, blue = bout != null ? bout.blue : null;
            if (_audio == null) _audio = FindAnyObjectByType<AudioDirector>();
            AudioDirector audio = _audio;
            Excitement ex = Excitement.Instance;

            _debugValues[0].text = $"{perf.FrameMs:0.0} ms";
            _debugValues[1].text = $"{perf.WorstMs:0.0} ms";
            _debugValues[2].text = $"{perf.PhysicsMs:0.00} ms";
            _debugValues[3].text = perf.SetPass.ToString();
            _debugValues[4].text = perf.Triangles >= 1000 ? $"{perf.Triangles / 1000f:0.0}k" : perf.Triangles.ToString();
            // Averaged: the single-frame figure jumps whenever the editor itself is asked for anything.
            _debugValues[5].text = $"{perf.GcAverageKb:0.0} kB";
            _debugValues[6].text = $"{perf.MemoryMb:0} MB";
            _debugValues[7].text = $"{perf.RenderScale:0.00}";
            _debugValues[8].text = director != null ? director.Current.ToString() : "-";
            _debugValues[9].text = audio != null ? audio.ActiveVoices.ToString() : "-";
            _debugValues[10].text = Excitement.Instance != null ? $"{Excitement.Instance.ClosingSpeed:0.0} m/s" : "-";
            _debugValues[11].text = red != null && blue != null ? $"{red.PeakStress * 100f:0}% / {blue.PeakStress * 100f:0}%" : "-";
            _debugValues[12].text = red != null && blue != null ? $"{red.Daze:0} / {blue.Daze:0}" : "-";
            _debugValues[13].text = ex != null && ex.HasCritic ? $"{ex.CriticEdge:+0.00;-0.00}" : "none";
            _debugValues[14].text = audio != null ? $"{audio.PeakDb:0} dB" : "-";
            _debugValues[15].text = ex != null ? $"{ex.UsualImpulse:0.0} N·s" : "-";
        }

        // ------------------------------------------------------------ results

        void FillResults()
        {
            Fighter red = bout.red, blue = bout.blue;
            if (red == null || blue == null) return;

            Fighter w = bout.Winner;
            if (_resWinner != null)
            {
                _resWinner.text = w != null ? w.displayName + " WINS" : "DRAW";
                _resWinner.style.color = w == null ? Color.white : w == red ? redColor : blueColor;
            }
            Set(_resMethod, bout.Method);
            if (_resElo != null)
            {
                // The ladder points the winner took (or what red moved by, on a draw).
                float delta = w == blue ? -bout.EloDelta : bout.EloDelta;
                _resElo.text = (delta >= 0f ? "+" : "") + delta.ToString("0") + " ELO";
            }

            Set(_resRed[0], $"{red.stats.landed}/{red.stats.thrown}");
            Set(_resBlue[0], $"{blue.stats.landed}/{blue.stats.thrown}");
            Set(_resRed[1], red.stats.blocked.ToString());
            Set(_resBlue[1], blue.stats.blocked.ToString());
            Set(_resRed[2], $"{red.stats.peakImpulse:0} N·s");
            Set(_resBlue[2], $"{blue.stats.peakImpulse:0} N·s");
            Set(_resRed[3], $"{red.stats.energyJ / 1000f:0.0} kJ");
            Set(_resBlue[3], $"{blue.stats.energyJ / 1000f:0.0} kJ");
            Set(_resRed[4], $"{red.stats.damageDealt:0}");
            Set(_resBlue[4], $"{blue.stats.damageDealt:0}");

            // The cards. After a decision they are the result; after a stoppage they are how it stood.
            for (int j = 0; j < Judges.Count; j++)
            {
                if (_judgeCards[j] == null) continue;
                int r = bout.judges.total[j, 0], b = bout.judges.total[j, 1];
                Set(_judgeScores[j], bout.judges.RoundsScored > 0 ? bout.judges.Card(j) : "-");
                _judgeCards[j].EnableInClassList("judge--red", r > b);
                _judgeCards[j].EnableInClassList("judge--blue", b > r);
            }

            float scale = HitScale(red, blue);
            if (_resHitRed != null) _resHitRed.Set(red.taken, scale, 0f);
            if (_resHitBlue != null) _resHitBlue.Set(blue.taken, scale, 0f);

            Excitement ex = Excitement.Instance;
            if (_resGraph != null && ex != null)
            {
                _resGraph.baseline = 0.5f;
                _resGraph.fillFromBaseline = true;
                _resGraph.lineColor = Color.white;
                _resGraph.fillColor = new Color(1f, 1f, 1f, 0.12f);
                _resGraph.Set(i => ex.Sample(ex.shareHistory, i), ex.HistoryCount, 0f, 1f);
            }
            RefreshNext();
        }

        /// <summary>The button that starts the next bout, counting down to doing it by itself.</summary>
        void RefreshNext()
        {
            if (_resNext == null) return;
            // The same two again when the boxers were chosen; the ladder's next pair when they were not.
            string word = bout.fixedEntrants ? "REMATCH" : "NEXT BOUT";
            Set2(_resNext, bout.autoAdvance ? $"{word} {Mathf.CeilToInt(bout.ResultsTimeLeft)}" : word);
        }

        static void Set2(Button b, string text)
        {
            if (b != null && b.text != text) b.text = text;
        }

        // ------------------------------------------------------------ safe area

        /// <summary>
        /// Moves the safe element's edges in by the notch and the home bar, converted from screen pixels to
        /// the panel's own units. Edges, not padding: everything inside is absolutely positioned and would
        /// ignore padding. Re-applied until the panel has a width, because on the first frame it has none
        /// and the conversion would be wrong on any phone that is not exactly the reference width.
        /// </summary>
        void ApplySafeArea()
        {
            _lastSafe = SafeArea;
            _lastScreen = new Vector2Int(Screen.width, Screen.height);
            if (_safe == null || Screen.width <= 0 || Screen.height <= 0) return;

            Rect sa = SafeArea;
            if (sa.width <= 0f || sa.height <= 0f || sa.xMax > Screen.width + 1f || sa.yMax > Screen.height + 1f)
                sa = new Rect(0f, 0f, Screen.width, Screen.height);

            float panelWidth = _root.panel != null ? _root.panel.visualTree.layout.width : float.NaN;
            _scaleKnown = panelWidth > 1f;
            float scale = _scaleKnown ? panelWidth / Screen.width : 1f;
            _safe.style.left = sa.xMin * scale;
            _safe.style.right = (Screen.width - sa.xMax) * scale;
            _safe.style.top = (Screen.height - sa.yMax) * scale;
            _safe.style.bottom = sa.yMin * scale;
        }
    }
}
