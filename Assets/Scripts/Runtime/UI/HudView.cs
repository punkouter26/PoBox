using System.Collections.Generic;
using UnityEngine;
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
    /// Five things never move: the game's name top-left, the frame rate top-centre, MENU top-right, DEBUG
    /// bottom-left and the version bottom-right. Between them: a scoreboard (names, clock, health, a
    /// tug-of-war momentum bar and a mirrored tale of the tape whose last row cycles through four readings
    /// on a tap), the picture, and a dock at the bottom with the commentary line and one graph that pages
    /// sideways between excitement, momentum and frame time.
    ///
    /// Everything else opens over that rather than replacing it: the menu is a sheet with three tabs, the
    /// debug readings are a panel that grows out of the DEBUG chip, and the end of a bout is a single sheet
    /// carrying the result, both fighters' numbers, the momentum graph of the whole bout and the replay
    /// with its scrubber, while the highlight reel plays in the picture above it.
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
        const int CycleModes = 4;
        const int LeagueRows = 6;

        VisualElement _root, _safe;

        // frame
        Button _fps, _menu, _debug;
        Label _version, _debugText;
        VisualElement _debugDot, _debugPanel;
        readonly List<Label> _debugValues = new List<Label>();

        // board
        Label _redName, _blueName, _redBadge, _blueBadge, _round, _clock, _redHpText, _blueHpText, _momRed, _momBlue;
        VisualElement _redHp, _blueHp, _momFill, _board;
        Label _landedRed, _landedBlue, _peakRed, _peakBlue, _cycleRed, _cycleBlue, _cycleCode;
        Glyph _cycleGlyph;
        int _cycle;

        // picture overlays
        Label _banner, _replayTag;

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
        Label _resWinner, _resMethod, _resElo, _resCaption;
        Sparkline _resGraph;
        Scrubber _resScrub;
        Button _resPlay, _resNext;
        Label _resPlayText;
        readonly Label[] _resRed = new Label[4], _resBlue = new Label[4];

        // menu
        VisualElement _menuSheet;
        readonly Button[] _tabs = new Button[3];
        readonly VisualElement[] _pages = new VisualElement[3];
        readonly Button[] _camButtons = new Button[4], _speedButtons = new Button[4];
        Button _sSound, _sHeat, _sQuality, _sFps, _sSlow, _sReset;
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
            _cycleCode = _root.Q<Label>("cycle-code");
            _cycleGlyph = _root.Q<Glyph>("cycle-glyph");

            _banner = _root.Q<Label>("banner");
            _replayTag = _root.Q<Label>("replay-tag");

            _dock = _root.Q<VisualElement>("dock");
            _strip = _root.Q<VisualElement>("strip");
            _ticker = _root.Q<Label>("ticker");
            _stripTitle = _root.Q<Label>("strip-title");
            _stripValue = _root.Q<Label>("strip-value");
            _stripGraph = _root.Q<Sparkline>("strip-graph");
            for (int i = 0; i < StripPages; i++) _dots[i] = _root.Q<VisualElement>("dot" + i);

            _results = _root.Q<VisualElement>("results");
            _resWinner = _root.Q<Label>("res-winner"); _resMethod = _root.Q<Label>("res-method");
            _resElo = _root.Q<Label>("res-elo"); _resCaption = _root.Q<Label>("res-caption");
            _resGraph = _root.Q<Sparkline>("res-graph");
            _resScrub = _root.Q<Scrubber>("res-scrub");
            _resPlay = _root.Q<Button>("res-play"); _resNext = _root.Q<Button>("res-next");
            _resPlayText = _root.Q<Label>("res-play-text");

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
            BuildDebugPanel();
            BuildLeagueRows();
            HookMenu();
            HookStrip();
            HookResults();

            RefreshCycleKey();
            SetStripPage(0);
            ShowTab(0);
            LoadSettings();
            ApplySafeArea();

            SimBus.Line += OnLine;
        }

        void OnDisable()
        {
            SimBus.Line -= OnLine;
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
            GlyphKind[] kinds = { GlyphKind.Glove, GlyphKind.Burst, GlyphKind.Bolt, GlyphKind.Heat };
            string[] codes = { "LANDED", "PEAK", "ENERGY", "DAMAGE" };
            for (int i = 0; i < 4; i++)
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

        static readonly string[] DebugKeys =
        {
            "FRAME", "WORST", "PHYSICS", "SETPASS", "TRIS", "GC", "MEMORY", "SCALE", "SHOT", "VOICES", "CLOSING", "STRESS",
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

            string[] cams = { "cam-auto", "cam-wide", "cam-orbit", "cam-top" };
            Shot?[] shots = { null, Shot.Wide, Shot.Orbit, Shot.Overhead };
            for (int i = 0; i < cams.Length; i++)
            {
                int index = i;
                _camButtons[i] = _root.Q<Button>(cams[i]);
                Hook(_camButtons[i], () => { if (director != null) director.Pin(shots[index]); RefreshMenu(); });
            }

            string[] speeds = { "spd-quarter", "spd-half", "spd-one", "spd-two" };
            float[] scales = { 0.25f, 0.5f, 1f, 2f };
            for (int i = 0; i < speeds.Length; i++)
            {
                int index = i;
                _speedButtons[i] = _root.Q<Button>(speeds[i]);
                Hook(_speedButtons[i], () => { if (bout != null) bout.userTimeScale = scales[index]; RefreshMenu(); });
            }

            _sSound = _root.Q<Button>("s-sound");
            _sHeat = _root.Q<Button>("s-heat");
            _sQuality = _root.Q<Button>("s-quality");
            _sFps = _root.Q<Button>("s-fps");
            _sSlow = _root.Q<Button>("s-slow");
            _sReset = _root.Q<Button>("s-reset");

            Hook(_sSound, () => { AudioDirector.Muted = !AudioDirector.Muted; SaveSettings(); RefreshMenu(); });
            Hook(_sHeat, () => { FighterSkin.HeatmapOn = !FighterSkin.HeatmapOn; SaveSettings(); RefreshMenu(); });
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
            Hook(_sSlow, () => { if (director != null) director.slowMotion = !director.slowMotion; SaveSettings(); RefreshMenu(); });
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

        void HookResults()
        {
            if (_resScrub != null) _resScrub.scrubbed += v => { if (ReplaySystem.Instance != null) ReplaySystem.Instance.Scrub(v); };
            Hook(_resPlay, () =>
            {
                ReplaySystem rs = ReplaySystem.Instance;
                if (rs == null) return;
                if (!rs.Playing) rs.Replay(); else rs.Paused = !rs.Paused;
            });
            Hook(_root.Q<Button>("res-prev"), () => { if (ReplaySystem.Instance != null) ReplaySystem.Instance.Step(-1); });
            Hook(_root.Q<Button>("res-next-clip"), () => { if (ReplaySystem.Instance != null) ReplaySystem.Instance.Step(1); });
            Hook(_resNext, () => { if (bout != null) bout.NewBout(); });
        }

        // ------------------------------------------------------------ settings

        void LoadSettings()
        {
            AudioDirector.Muted = PlayerPrefs.GetInt("pobox.muted", 0) == 1;
            FighterSkin.HeatmapOn = PlayerPrefs.GetInt("pobox.heat", 1) == 1;
            if (director != null) director.slowMotion = PlayerPrefs.GetInt("pobox.slowmo", 1) == 1;
            PerfTelemetry perf = PerfTelemetry.Instance;
            if (perf != null) perf.SetTargetFps(PlayerPrefs.GetInt("pobox.fps", perf.targetFps));
            RefreshMenu();
        }

        void SaveSettings()
        {
            PlayerPrefs.SetInt("pobox.muted", AudioDirector.Muted ? 1 : 0);
            PlayerPrefs.SetInt("pobox.heat", FighterSkin.HeatmapOn ? 1 : 0);
            if (director != null) PlayerPrefs.SetInt("pobox.slowmo", director.slowMotion ? 1 : 0);
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
            _menuSheet.RemoveFromClassList("hidden");
            if (_menu != null) _menu.text = "CLOSE";
            ShowTab(tab);
        }

        public void CloseMenu()
        {
            if (_menuSheet == null) return;
            _menuSheet.AddToClassList("hidden");
            if (_menu != null) _menu.text = "MENU";
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
            _debugPanel.ToggleInClassList("hidden");
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

            float scale = bout != null ? bout.userTimeScale : 1f;
            float[] scales = { 0.25f, 0.5f, 1f, 2f };
            for (int i = 0; i < _speedButtons.Length; i++)
                if (_speedButtons[i] != null) _speedButtons[i].EnableInClassList("btn--on", Mathf.Approximately(scale, scales[i]));

            Toggle(_sSound, "SOUND", !AudioDirector.Muted);
            Toggle(_sHeat, "HEAT MAP", FighterSkin.HeatmapOn);
            Toggle(_sSlow, "SLOW-MO", director == null || director.slowMotion);
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
                if (_stripPage == 2) RefreshStrip();
                if (ResultsOpen) RefreshReplay();
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

            if (_ticker != null) _ticker.style.opacity = Mathf.Clamp01(1.4f - (now - _tickerAt) / 6f);
        }

        /// <summary>
        /// Tells the cameras which part of the screen is not under interface: from the bottom of the
        /// scoreboard to the top of the dock, or to the top of the results card when that is up.
        /// </summary>
        void ReportPictureBand()
        {
            if (director == null || _board == null || _root.panel == null) return;
            float height = _root.panel.visualTree.layout.height;
            if (!(height > 1f)) return;
            VisualElement below = ResultsOpen ? _results : _dock;
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
            if (_results != null) _results.EnableInClassList("hidden", !results);
            if (_dock != null) _dock.EnableInClassList("hidden", results);

            if (results) FillResults();
            if (MenuOpen && _pages[1] != null && !_pages[1].ClassListContains("hidden")) RefreshLeague();
        }

        string Badge(Fighter f)
        {
            string badge = f.profile != null ? f.profile.Badge : "SCRIPTED";
            if (league != null)
            {
                LeagueTable.Row row = league.Find(f.displayName);
                if (row != null) badge = $"#{league.RankOf(f.displayName)} · {row.elo:0}";
            }
            return badge;
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
            GlyphKind[] kinds = { GlyphKind.Bolt, GlyphKind.Heat, GlyphKind.Balance, GlyphKind.Power };
            string[] codes = { "ENERGY", "STRESS", "BALANCE", "POWER" };
            if (_cycleGlyph != null) _cycleGlyph.kind = kinds[_cycle];
            if (_cycleCode != null) _cycleCode.text = codes[_cycle];
        }

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
                case 1: return $"{f.PeakStress * 100f:0}%";
                case 2: return $"{f.BalanceMargin * 100f:0}%";
                default: return $"{f.PowerW:0} W";
            }
        }

        static void Set(Label l, string text)
        {
            if (l != null && l.text != text) l.text = text;
        }

        void RefreshOverlays(float now)
        {
            ReplaySystem rs = ReplaySystem.Instance;
            string banner = null;
            switch (bout.Phase)
            {
                case BoutPhase.Intro: banner = "ROUND " + bout.Round; break;
                case BoutPhase.Count: if (bout.Count > 0) banner = bout.Count.ToString(); break;
            }
            if (_banner != null)
            {
                _banner.EnableInClassList("hidden", banner == null);
                _banner.EnableInClassList("banner--count", bout.Phase == BoutPhase.Count);
                if (banner != null) Set(_banner, banner);
            }

            bool breakReplay = bout.Phase == BoutPhase.RoundBreak && rs != null && rs.Playing && rs.Current != null;
            if (_replayTag != null)
            {
                _replayTag.EnableInClassList("hidden", !breakReplay);
                if (breakReplay) Set(_replayTag, "REPLAY · " + rs.Current.caption);
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
                    if (ex != null) _stripGraph.Set(i => ex.Sample(ex.excitementHistory, i), ex.HistoryCount, 0f, 1f);
                    break;
                case 1:
                    Set(_stripTitle, "MOMENTUM");
                    Set(_stripValue, $"{Excitement.RedShare * 100f:0}% {(bout.red != null ? bout.red.displayName : "RED")}");
                    _stripGraph.baseline = 0.5f;
                    _stripGraph.fillFromBaseline = true;
                    _stripGraph.lineColor = Color.white;
                    _stripGraph.fillColor = new Color(1f, 1f, 1f, 0.12f);
                    if (ex != null) _stripGraph.Set(i => ex.Sample(ex.shareHistory, i), ex.HistoryCount, 0f, 1f);
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
            AudioDirector audio = FindAnyObjectByType<AudioDirector>();

            _debugValues[0].text = $"{perf.FrameMs:0.0} ms";
            _debugValues[1].text = $"{perf.WorstMs:0.0} ms";
            _debugValues[2].text = $"{perf.PhysicsMs:0.00} ms";
            _debugValues[3].text = perf.SetPass.ToString();
            _debugValues[4].text = perf.Triangles >= 1000 ? $"{perf.Triangles / 1000f:0.0}k" : perf.Triangles.ToString();
            _debugValues[5].text = $"{perf.GcKb:0.0} kB";
            _debugValues[6].text = $"{perf.MemoryMb:0} MB";
            _debugValues[7].text = $"{perf.RenderScale:0.00}";
            _debugValues[8].text = director != null ? director.Current.ToString() : "-";
            _debugValues[9].text = audio != null ? audio.ActiveVoices.ToString() : "-";
            _debugValues[10].text = Excitement.Instance != null ? $"{Excitement.Instance.ClosingSpeed:0.0} m/s" : "-";
            _debugValues[11].text = red != null && blue != null ? $"{red.PeakStress * 100f:0}% / {blue.PeakStress * 100f:0}%" : "-";
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
            Set(_resRed[1], $"{red.stats.peakImpulse:0} N·s");
            Set(_resBlue[1], $"{blue.stats.peakImpulse:0} N·s");
            Set(_resRed[2], $"{red.stats.energyJ / 1000f:0.0} kJ");
            Set(_resBlue[2], $"{blue.stats.energyJ / 1000f:0.0} kJ");
            Set(_resRed[3], $"{red.stats.damageDealt:0}");
            Set(_resBlue[3], $"{blue.stats.damageDealt:0}");

            Excitement ex = Excitement.Instance;
            if (_resGraph != null && ex != null)
            {
                _resGraph.baseline = 0.5f;
                _resGraph.fillFromBaseline = true;
                _resGraph.lineColor = Color.white;
                _resGraph.fillColor = new Color(1f, 1f, 1f, 0.12f);
                _resGraph.Set(i => ex.Sample(ex.shareHistory, i), ex.HistoryCount, 0f, 1f);

                var marks = new List<float>();
                ReplaySystem rs = ReplaySystem.Instance;
                float span = ex.HistoryCount * Excitement.SampleInterval;
                if (rs != null && span > 0.1f)
                {
                    // The graph only holds the last minute; a moment older than that has scrolled off it.
                    float first = Bout.Clock - span;
                    foreach (ReplaySystem.Clip c in rs.Highlights)
                        if (c.boutClock >= first) marks.Add((c.boutClock - first) / span);
                }
                _resGraph.SetMarkers(marks);
            }
            RefreshReplay();
        }

        void RefreshReplay()
        {
            ReplaySystem rs = ReplaySystem.Instance;
            bool has = rs != null && rs.Playing && rs.Current != null;
            if (_resCaption != null)
                Set(_resCaption, has ? $"{rs.ClipIndex + 1}/{rs.ClipCount} · {rs.Current.caption}" : rs != null && rs.Highlights.Count == 0 ? "No clean hit worth a replay" : "Replay stopped");
            if (_resScrub != null)
            {
                if (has && !_resScrub.Dragging) _resScrub.value = rs.Position;
                _resScrub.SetMark(has ? rs.Current.EventAt : -1f);
            }
            if (_resPlayText != null) Set(_resPlayText, !has ? "REPLAY" : rs.Paused ? "PLAY" : "PAUSE");
            if (_resNext != null)
                Set2(_resNext, bout.autoAdvance ? $"NEXT BOUT {Mathf.CeilToInt(bout.ResultsTimeLeft)}" : "NEXT BOUT");
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
