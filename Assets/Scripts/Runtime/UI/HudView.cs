using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using PoBox.Audio;
using PoBox.Broadcast;
using PoBox.Diag;
using PoBox.Fx;
using PoBox.League;
using PoBox.Mj;
using PoBox.Net;
using PoBox.Sim;

namespace PoBox.UI
{
    /// <summary>
    /// The whole interface, in one document, on one portrait screen, with nothing that scrolls.
    ///
    /// Five things never move: the game's name top-left, the frame rate top-centre, the menu top-right, DEBUG
    /// bottom-left and the version bottom-right. Between them: a scoreboard of three rows (the names
    /// written on the health bars with the clock between them, a hit leaving a pale chunk on the bar that
    /// drains a moment later; a tug-of-war win-probability bar; three chips, each a pair of numbers either
    /// side of an icon, the third cycling through five readings on a tap anywhere on the scoreboard), the
    /// picture, and a dock at the bottom with one graph that pages sideways between excitement, win
    /// probability and frame time.
    ///
    /// Over the picture: a pod in each top corner showing where that fighter has been hit and where its
    /// balance is, what it has left in the tank (and, for a boxer trained to tire, in its muscles), what it
    /// is doing this moment, and ON THE ROPES or TIRED when it is; the commentary as a caption
    /// along the bottom that fades when it has nothing to say; during the walk-on each fighter's name across
    /// the lower third; between rounds the three cards and the round's numbers (who held the centre of the
    /// ring among them); during a count an inset of
    /// the boxer left standing; and at a hard clean punch the glove's speed, written where it landed. A
    /// sideways drag on the picture takes the orbit camera and turns it.
    ///
    /// Everything else opens over that rather than replacing it, docked at the bottom where a thumb is and
    /// one at a time: the menu is a sheet with three tabs (the last of them the settings, which have three of their
    /// own), the debug readings are a panel that grows out of the DEBUG chip (the frame-rate chip opens and closes it
    /// too; the hidden switch, three fingers or a long press on the frame rate, opens it with the X-ray), and a tap
    /// anywhere outside either puts it away; the end of a bout is a single sheet carrying the result, what the pick
    /// paid, the three judges' cards, both fighters' numbers with a hit map each, the win-probability graph of the
    /// whole bout, and one row of four chips: REPLAY, SHARE, NEXT (or AGAIN) and MENU. MENU, and CHANGE BOXERS in
    /// the menu, go back to the first screen, loading it in the background with the button dimmed meanwhile.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class HudView : MonoBehaviour
    {
        public Bout bout;
        public LeagueTable league;
        public BroadcastDirector director;

        const int StripPages = 3;
        const int CycleModes = 5;
        const int LeagueRows = 8;
        const int ResultRows = 5;

        VisualElement _root, _safe;
        readonly SafeArea _safeArea = new SafeArea();

        // frame
        Button _fps, _menu, _debug;
        Label _version, _debugText;
        VisualElement _debugDot, _debugPanel;
        Glyph _menuGlyph;
        bool _leaving;
        Label[] _debugValues = new Label[0];

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

        // pods: the tank, the reserve, the action badge and the two flags
        readonly VisualElement[] _tank = new VisualElement[2], _reserve = new VisualElement[2], _reserveBar = new VisualElement[2], _ghost = new VisualElement[2];
        readonly Label[] _act = new Label[2], _ropes = new Label[2], _tired = new Label[2];
        // what has just changed, swollen until the next tick (Theme.uss .pop)
        readonly List<VisualElement> _popped = new List<VisualElement>(16);
        readonly PunchType[] _punch = new PunchType[2];
        readonly float[] _punchAt = { -99f, -99f };
        static readonly string[] PunchNames = { "JAB", "CROSS", "HOOK", "UPPER", "BODY" };
        /// <summary>Seconds a punch's name stays on the badge.</summary>
        const float PunchShown = 0.45f;

        // between rounds, the inset, the speed of a punch
        VisualElement _break, _pip, _picture;
        Label _breakTitle, _flash;
        readonly Label[] _breakLanded = new Label[2], _breakBlocked = new Label[2], _breakCentre = new Label[2];
        readonly VisualElement[] _breakCards = new VisualElement[Judges.Count];
        readonly Label[] _breakScores = new Label[Judges.Count];
        readonly FighterStats[] _roundStart = new FighterStats[2];
        [Tooltip("A clean punch this many times harder than these two usually land has its glove speed written where it landed.")]
        public float flashOverUsual = 1.25f;
        const float FlashSeconds = 0.9f;
        Vector3 _flashPoint;
        float _flashAt = -99f, _dragX;
        bool _dragging, _stripPressed;

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
        Label _resNextWord;
        readonly Label[] _resRed = new Label[ResultRows], _resBlue = new Label[ResultRows];
        readonly VisualElement[] _judgeCards = new VisualElement[Judges.Count];
        readonly Label[] _judgeScores = new Label[Judges.Count];

        // menu
        VisualElement _menuSheet;
        readonly Button[] _tabs = new Button[3];
        readonly VisualElement[] _pages = new VisualElement[3];
        readonly Button[] _camButtons = new Button[4], _speedButtons = new Button[2], _focusButtons = new Button[2];
        SettingsPage _settings;
        readonly Label[][] _leagueCells = new Label[LeagueRows][];
        readonly VisualElement[] _leagueRows = new VisualElement[LeagueRows];
        // CHANGE BOXERS in the menu and MENU on the result card, and their words, which trail dots while the first screen loads
        readonly Button[] _boxers = new Button[2];
        readonly Label[] _boxersWords = new Label[2];
        readonly string[] _boxersBase = new string[2];
        static readonly string[] BusyDots = { " ·", " ··", " ···" };

        // the features: the instant replay, SHARE, the pick before the bell, chat plays, the brain cam
        Button _resReplay, _resShare, _predictRed, _predictBlue;
        Label _resExtra, _chat, _predictCoins, _brainTitle;
        VisualElement _predict, _brain;
        VisualElement[] _brainBars = new VisualElement[0];
        int[] _brainGroup = new int[0];
        int _brainCorner;
        bool _replaying;
        TwitchChat _twitch;
        /// <summary>The brain cam's groups of the 21 joints, in the order drawn.</summary>
        static readonly string[] BrainGroups = { "LEGS", "TORSO", "L ARM", "R ARM" };

        float _nextFast, _nextSlow;
        int _lastBout = -1;
        BoutPhase _lastPhase = (BoutPhase)(-1);

        public bool MenuOpen => _menuSheet != null && !_menuSheet.ClassListContains("hidden");
        public bool DebugOpen => _debugPanel != null && !_debugPanel.ClassListContains("hidden");
        public bool ResultsOpen => _results != null && !_results.ClassListContains("hidden");
        public SettingsPage Settings => _settings;
        public VisualElement Root => _root;

        /// <summary>A pretend notch for the editor and the tests, in screen pixels, for every document (<see cref="SafeArea"/>). Null is the real safe area.</summary>
        public static Rect? SafeAreaOverride;

        // ------------------------------------------------------------ build

        void OnEnable()
        {
            // Built and hooked once a document: enabled again on the same tree, the buttons keep the handlers they have.
            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            if (root == null) return;
            if (root != _root) Build(root);

            SimBus.Line += OnLine;
            SimBus.PunchThrown += OnPunch;
            SimBus.Hit += OnHit;
            SimBus.Knockdown += OnKnockdown;
            _twitch ??= TwitchChat.FromPrefs();
        }

        void Build(VisualElement root)
        {
            _root = root;
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
                _reserve[i] = _root.Q<VisualElement>("reserve-" + corners[i]);
                _reserveBar[i] = _root.Q<VisualElement>("reserve-bar-" + corners[i]);
                _ghost[i] = _root.Q<VisualElement>(corners[i] + "-ghost");
                _act[i] = _root.Q<Label>("act-" + corners[i]);
                _ropes[i] = _root.Q<Label>("ropes-" + corners[i]);
                _tired[i] = _root.Q<Label>("tired-" + corners[i]);
                _breakLanded[i] = _root.Q<Label>("break-landed-" + corners[i]);
                _breakBlocked[i] = _root.Q<Label>("break-blocked-" + corners[i]);
                _breakCentre[i] = _root.Q<Label>("break-centre-" + corners[i]);
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
            _resNextWord = _root.Q<Label>("res-next-word");
            _resReplay = _root.Q<Button>("res-replay");
            _resShare = _root.Q<Button>("res-share");
            _resExtra = _root.Q<Label>("res-extra");
            _chat = _root.Q<Label>("chat");
            _predict = _root.Q<VisualElement>("predict");
            _predictRed = _root.Q<Button>("predict-red");
            _predictBlue = _root.Q<Button>("predict-blue");
            _predictCoins = _root.Q<Label>("predict-coins");

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
            // The frame rate: a tap is the frame-time graph and the panel; a long press (or three fingers) the panel and the X-ray.
            DebugPanel.Hook(_root, _fps, () => { SetStripPage(2); ToggleDebug(); }, () => { ToggleDebug(); PhysicsXray.On = DebugOpen; });
            // The third chip is shorter than a thumb, so the whole scoreboard is its button.
            if (_board != null) _board.RegisterCallback<ClickEvent>(_ =>
            {
                if (CloseOverlays()) return;
                _cycle = (_cycle + 1) % CycleModes;
                RefreshCycleKey();
                if (bout != null) RefreshTape();
            });

            BuildResultsTape();
            BuildJudges("res-judges", _judgeCards, _judgeScores);
            BuildJudges("break-judges", _breakCards, _breakScores);
            _debugValues = DebugPanel.Build(_debugPanel, DebugKeys);
            BuildLeagueRows();
            HookMenu();
            HookStrip();
            HookResults();
            HookPicture();
            HookPredict();
            BuildBrain();

            RefreshCycleKey();
            SetStripPage(0);
            ShowTab(0);
            SettingsPage.Load();
            RefreshMenu();
            _safeArea.Fit(_root, _safe);
        }

        void OnDisable()
        {
            SimBus.Line -= OnLine;
            SimBus.PunchThrown -= OnPunch;
            SimBus.Hit -= OnHit;
            SimBus.Knockdown -= OnKnockdown;
            _twitch?.Dispose();
            _twitch = null;
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
            // Landed, blocked, hardest, energy, damage: the icon is the key, as on the scoreboard's chips.
            GlyphKind[] kinds = { GlyphKind.Glove, GlyphKind.Shield, GlyphKind.Burst, GlyphKind.Bolt, GlyphKind.Heat };
            for (int i = 0; i < ResultRows; i++)
            {
                var row = new VisualElement { pickingMode = PickingMode.Ignore };
                row.AddToClassList("tape-row");
                _resRed[i] = TapeValue(true);
                _resBlue[i] = TapeValue(false);
                row.Add(_resRed[i]);
                row.Add(TapeKey(kinds[i]));
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

        static VisualElement TapeKey(GlyphKind kind)
        {
            var key = new VisualElement { pickingMode = PickingMode.Ignore };
            key.AddToClassList("tape-key");
            var g = new Glyph { kind = kind };
            g.AddToClassList("glyph");
            key.Add(g);
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
            "DAZE", "CRITIC", "SOUND", "USUAL", "BRAINS",
        };

        bool BrainOn => _brain != null && !_brain.ClassListContains("hidden");

        /// <summary>
        /// The brain cam, off until the settings' BRAIN (DEBUG tab) switches it on: a title (whose brain, and the punch it has just
        /// thrown or the arm it is loading) over a bar a joint, grouped legs, torso, left arm, right arm. A bar's
        /// height is the size of that joint's action (-1 to 1), pale when it is negative. A tap swaps boxers.
        /// </summary>
        void BuildBrain()
        {
            if (_safe == null) return;
            _brain = new VisualElement { name = "brain" };
            _brain.AddToClassList("brain");
            _brain.AddToClassList("hidden");
            _brainTitle = new Label("BRAIN") { pickingMode = PickingMode.Ignore };
            _brainTitle.AddToClassList("brain-title");
            _brain.Add(_brainTitle);
            var bars = new VisualElement { name = "brain-bars", pickingMode = PickingMode.Ignore };
            bars.AddToClassList("brain-bars");
            _brain.Add(bars);
            _brain.RegisterCallback<ClickEvent>(_ => { _brainCorner = 1 - _brainCorner; _brainBars = new VisualElement[0]; });
            _safe.Add(_brain);
        }

        /// <summary>One bar a joint of this boxer's, in its group by the joint's name (the config's joint_order).</summary>
        void BuildBrainBars(MjBoxer body)
        {
            VisualElement host = _brain.Q<VisualElement>("brain-bars");
            host.Clear();
            string[] joints = body.Cfg.joint_order;
            _brainBars = new VisualElement[body.Action.Length];
            _brainGroup = new int[body.Action.Length];
            for (int g = 0; g < BrainGroups.Length; g++)
            {
                var group = new VisualElement { pickingMode = PickingMode.Ignore };
                group.AddToClassList("brain-group");
                for (int j = 0; j < _brainBars.Length; j++)
                {
                    if (GroupOf(j < joints.Length ? joints[j] : "") != g) continue;
                    var slot = new VisualElement { pickingMode = PickingMode.Ignore };
                    slot.AddToClassList("brain-slot");
                    var bar = new VisualElement { pickingMode = PickingMode.Ignore };
                    bar.AddToClassList("brain-bar");
                    slot.Add(bar);
                    group.Add(slot);
                    _brainBars[j] = bar;
                    _brainGroup[j] = g;
                }
                var key = new Label(BrainGroups[g]) { pickingMode = PickingMode.Ignore };
                key.AddToClassList("brain-key");
                var column = new VisualElement { pickingMode = PickingMode.Ignore };
                column.AddToClassList("brain-column");
                column.Add(group);
                column.Add(key);
                host.Add(column);
            }
        }

        /// <summary>A joint's brain cam group by its name: 0 legs, 1 torso, 2 left arm, 3 right arm (FightLog counts effort by these too).</summary>
        internal static int GroupOf(string joint)
        {
            if (joint.StartsWith("abdomen")) return 1;
            if (joint.StartsWith("shoulder") || joint.StartsWith("elbow")) return joint.EndsWith("_l") ? 2 : 3;
            return 0;
        }

        /// <summary>The bars every frame; the title only with the rest of the text.</summary>
        void RefreshBrain(bool text)
        {
            Fighter f = _brainCorner == 0 ? bout.red : bout.blue;
            MjBoxer body = f != null ? f.boxer : null;
            if (body == null || !body.Bound || body.Action == null) return;
            if (_brainBars.Length != body.Action.Length) BuildBrainBars(body);
            float left = 0f, right = 0f;
            for (int j = 0; j < _brainBars.Length; j++)
            {
                float v = Mathf.Clamp((float)body.Action[j], -1f, 1f);
                _brainBars[j].style.height = Length.Percent(Mathf.Abs(v) * 100f);
                _brainBars[j].EnableInClassList("brain-bar--neg", v < 0f);
                if (_brainGroup[j] == 2) left = Mathf.Max(left, Mathf.Abs(v));
                if (_brainGroup[j] == 3) right = Mathf.Max(right, Mathf.Abs(v));
            }
            if (!text) return;
            bool punching = Time.time - _punchAt[_brainCorner] < PunchShown;
            string what = punching ? PunchNames[(int)_punch[_brainCorner]] : Mathf.Max(left, right) > 0.6f ? (left > right ? "LOADING LEFT" : "LOADING RIGHT") : "GUARD";
            Set(_brainTitle, $"BRAIN · {f.displayName} · {what}");
            _brainTitle.EnableInClassList("brain-title--blue", _brainCorner == 1);
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
            Hook(_root.Q<Button>("m-restart"), () => { StopReplay(); if (bout != null) bout.Restart(); CloseMenu(); });
            Hook(_root.Q<Button>("m-next"), () => { StopReplay(); if (bout != null) bout.NewBout(); CloseMenu(); });
            HookBoxers(0, _root.Q<Button>("m-boxers"), _root.Q<Label>("m-boxers-word"));

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

            // The settings the first screen has too, with the ladder to reset and the brain cam.
            _settings = new SettingsPage(_root, league, ToggleDebug, brain: true);
        }

        void HookStrip()
        {
            if (_strip == null) return;
            // A sideways swipe pages the graph; a tap goes to the next one. A touch the system takes back (a call
            // coming in, the swipe turning into the system's gesture) pages nothing.
            _strip.RegisterCallback<PointerDownEvent>(e =>
            {
                if (CloseOverlays()) return;
                _stripDownX = e.position.x;
                _stripPressed = true;
                _strip.CapturePointer(e.pointerId);
            });
            _strip.RegisterCallback<PointerUpEvent>(e =>
            {
                if (!_stripPressed) return;
                _stripPressed = false;
                if (_strip.HasPointerCapture(e.pointerId)) _strip.ReleasePointer(e.pointerId);
                float dx = e.position.x - _stripDownX;
                int step = Mathf.Abs(dx) < 40f ? 1 : dx < 0f ? 1 : -1;
                SetStripPage((_stripPage + step + StripPages) % StripPages);
            });
            _strip.RegisterCallback<PointerCancelEvent>(e =>
            {
                _stripPressed = false;
                if (_strip.HasPointerCapture(e.pointerId)) _strip.ReleasePointer(e.pointerId);
            });
            _strip.RegisterCallback<PointerCaptureOutEvent>(_ => _stripPressed = false);
        }

        /// <summary>A sideways drag on the picture turns the orbit camera, taking it on air first. AUTO in the menu gives the cameras back.</summary>
        void HookPicture()
        {
            if (_picture == null) return;
            _picture.RegisterCallback<PointerDownEvent>(e =>
            {
                // The menu and the debug panel lie over the picture: a tap on what is left of it puts them away.
                if (CloseOverlays()) return;
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
            _picture.RegisterCallback<PointerUpEvent>(e => EndDrag(e.pointerId));
            // A touch the system cancels, or a capture lost to anything else, ends the drag: otherwise the next
            // touch anywhere would turn the camera.
            _picture.RegisterCallback<PointerCancelEvent>(e => EndDrag(e.pointerId));
            _picture.RegisterCallback<PointerCaptureOutEvent>(_ => _dragging = false);
        }

        void EndDrag(int pointerId)
        {
            if (!_dragging) return;
            _dragging = false;
            if (_picture.HasPointerCapture(pointerId)) _picture.ReleasePointer(pointerId);
            RefreshMenu();
        }

        void HookResults()
        {
            HookBoxers(1, _root.Q<Button>("res-boxers"), _root.Q<Label>("res-boxers-word"));
            Hook(_resNext, () => { StopReplay(); if (bout != null) bout.NewBout(); });
            Hook(_resReplay, PlayReplay);
            Hook(_resShare, () => StartCoroutine(Share.Shot(ShareLine())));
        }

        /// <summary>
        /// The instant replay (Mj/MjReplay.cs): the last knockdown, or the last seconds of the bout, at half speed,
        /// the card put away while it plays and back when it is over; the cameras take the wide shot.
        /// </summary>
        void PlayReplay()
        {
            MjReplay replay = MjReplay.Instance;
            if (replay == null || !replay.Play()) return;
            _replaying = true;
            ShowResults();
            SimBus.Say("REPLAY · " + (bout != null ? bout.Method : ""), 3);
        }

        static void StopReplay()
        {
            if (MjReplay.Playing) MjReplay.Instance.Stop();
        }

        string ShareLine()
        {
            if (bout == null || bout.red == null || bout.blue == null) return "PoBox";
            Fighter w = bout.Winner;
            string what = w == null ? $"{bout.red.displayName} and {bout.blue.displayName} drew" : $"{w.displayName} beat {bout.Other(w).displayName}";
            return $"{what}: {bout.Method} · PoBox";
        }

        /// <summary>The pick before the first bell: either corner, at the ladder's odds (League/Prediction.cs).</summary>
        void HookPredict()
        {
            Hook(_predictRed, () => Pick(0));
            Hook(_predictBlue, () => Pick(1));
        }

        void Pick(int corner)
        {
            if (bout == null || !PickOpen) return;
            Prediction.Pick(corner, Prediction.Odds(Chance(corner)));
            RefreshPredict();
        }

        bool PickOpen => bout.Round == 1 && (bout.Phase == BoutPhase.WalkOn || bout.Phase == BoutPhase.Intro) && bout.red != null && bout.blue != null;

        /// <summary>The ladder's chance of this corner winning (an even chance off the ladder).</summary>
        float Chance(int corner)
        {
            if (league == null || bout.red == null || bout.blue == null) return 0.5f;
            float red = league.Expected(bout.red.displayName, bout.blue.displayName);
            return corner == 0 ? red : 1f - red;
        }

        void RefreshPredict()
        {
            if (_predict == null) return;
            bool open = PickOpen;
            _predict.EnableInClassList("hidden", !open);
            if (!open) return;
            bool picked = Prediction.Picked >= 0;
            _predictRed?.EnableInClassList("hidden", picked);
            _predictBlue?.EnableInClassList("hidden", picked);
            if (picked)
                Set(_predictCoins, $"YOUR PICK {(Prediction.Picked == 0 ? bout.red : bout.blue).displayName} · {Prediction.Coins} COINS");
            else
            {
                Set2(_predictRed, $"{bout.red.displayName} x{Prediction.Odds(Chance(0)):0.0}");
                Set2(_predictBlue, $"{bout.blue.displayName} x{Prediction.Odds(Chance(1)):0.0}");
                Set(_predictCoins, $"PICK · {Prediction.Stake} OF {Prediction.Coins}");
            }
        }

        /// <summary>Chat plays (Net/TwitchChat.cs): the corners' votes on a line under the scoreboard, and a cube thrown when chat asks.</summary>
        void RefreshChat()
        {
            if (_chat == null) return;
            _twitch.Poll();
            _chat.RemoveFromClassList("hidden");
            Fighter red = bout.red, blue = bout.blue;
            if (red == null || blue == null) return;
            int r = _twitch.Votes("red") + _twitch.Votes(red.displayName.ToLowerInvariant());
            int b = _twitch.Votes("blue") + _twitch.Votes(blue.displayName.ToLowerInvariant());
            Set(_chat, _twitch.Connected ? $"CHAT · {red.displayName} {r} · {blue.displayName} {b}" : $"CHAT · #{_twitch.Channel} ···");
            if (Bout.Fighting && _twitch.TakeCube())
            {
                Fighter at = Random.value < 0.5f ? red : blue;
                if (TwitchChat.ThrowCube(at)) SimBus.Say($"Chat throws a cube at {at.displayName}!", 1);
            }
        }

        /// <summary>A knockdown: the replay keeps the seconds around it.</summary>
        void OnKnockdown(Fighter f, HitEvent cause)
        {
            if (cause.attacker != null && MjReplay.Instance != null) MjReplay.Instance.Mark();
        }

        /// <summary>A new bout: no replay of the last one, its pick given back if it was never settled, chat's votes cleared.</summary>
        void OnNewBout()
        {
            if (MjReplay.Instance != null) MjReplay.Instance.Clear();
            Prediction.Reset();
            _twitch?.ClearVotes();
        }

        /// <summary>At the result, once: the pick paid out, and a cup bout (League/Career.cs) counted, in a line on the card.</summary>
        void SettleExtras()
        {
            Fighter red = bout.red, blue = bout.blue;
            if (red == null || blue == null) return;
            int winner = bout.Winner == null ? -1 : bout.Winner == red ? 0 : 1;
            string pick = Prediction.Picked >= 0 ? Prediction.Settle(winner, (Prediction.Picked == 0 ? red : blue).displayName) : null;
            string cup = null;
            if (Career.Live && MatchSelection.Chosen)
            {
                Career.Live = false;
                Career c = Career.Load();
                if (c != null && c.Report(MatchSelection.Red, MatchSelection.Blue, winner))
                    cup = c.Champion != null ? $"CUP WON BY {c.Shown(c.Champion)}" : "CUP · THROUGH TO THE NEXT ROUND";
                else if (c != null && winner < 0) cup = "CUP · A DRAW: FIGHT IT AGAIN";
            }
            string line = pick != null && cup != null ? pick + "  ·  " + cup : pick ?? cup ?? "";
            if (_resExtra == null) return;
            Set(_resExtra, line);
            _resExtra.EnableInClassList("hidden", line.Length == 0);
        }

        /// <summary>Back to the first screen to choose another pair. Not offered when the build has no such screen.</summary>
        void HookBoxers(int slot, Button b, Label word)
        {
            _boxers[slot] = b;
            _boxersWords[slot] = word;
            _boxersBase[slot] = word != null ? word.text : "";
            if (b == null) return;
            b.EnableInClassList("hidden", !MatchSelection.HasMenu);
            b.clicked += ToBoxers;
        }

        /// <summary>
        /// Leaves for the first screen, once: a second tap while the scene is loading would load it twice. It
        /// loads in the background; meanwhile both BOXERS buttons are dimmed and their word trails dots.
        /// </summary>
        public void ToBoxers()
        {
            if (_leaving || !MatchSelection.HasMenu) return;
            _leaving = true;
            foreach (Button b in _boxers) if (b != null) b.AddToClassList("btn--busy");
            SceneManager.LoadSceneAsync(MatchSelection.MenuScene);
        }

        // ------------------------------------------------------------ sheets

        /// <summary>The one overlay rule: a tap outside the menu or the debug panel puts it away. True when there was one to put away.</summary>
        bool CloseOverlays()
        {
            if (MenuOpen) { CloseMenu(); return true; }
            if (DebugOpen) { ToggleDebug(); return true; }
            return false;
        }

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
            if (_results != null) _results.EnableInClassList("hidden", bout == null || bout.Phase != BoutPhase.Results || MenuOpen || DebugOpen || MjReplay.Playing);
        }

        /// <summary>The menu's SETTINGS tab, open on one of the settings' own tabs (0 SOUND, 1 DISPLAY, 2 DEBUG).</summary>
        public void OpenSettings(int tab)
        {
            OpenMenu(2);
            _settings?.ShowTab(tab);
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

            if (_settings != null) _settings.Refresh();
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
                // What the rating started from: the boxer's record in the CPU's bouts (tools/elo.py).
                string cpu = LeagueTable.CpuRecord(row.name);
                _leagueCells[r][2].text = cpu.Length > 0 ? "CPU " + cpu : p != null ? p.Badge : "";
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
            _safeArea.Fit(_root, _safe);
            float now = Time.unscaledTime;
            if (_leaving)
            {
                string dots = BusyDots[(int)(now * 3f) % BusyDots.Length];
                for (int i = 0; i < _boxersWords.Length; i++) Set(_boxersWords[i], _boxersBase[i] + dots);
            }
            if (bout == null) return;
            // The brain cam is the settings' (DEBUG, BRAIN); it gives way to the pick before the bell and to the result.
            _brain?.EnableInClassList("hidden", !SettingsPage.BrainCam || bout.Phase == BoutPhase.Results || (_predict != null && !_predict.ClassListContains("hidden")));

            if (bout.BoutNumber != _lastBout || bout.Phase != _lastPhase)
            {
                if (bout.BoutNumber != _lastBout) OnNewBout();
                _lastBout = bout.BoutNumber;
                OnPhaseChanged(bout.Phase);
                _lastPhase = bout.Phase;
            }

            RefreshBars();
            RefreshOverlays(now);
            // The replay over: the card back. One still playing as the result runs out is stopped first, so the
            // next bout is placed on the live state and not under the replay's.
            if (_replaying && !MjReplay.Playing) { _replaying = false; ShowResults(); }
            if (MjReplay.Playing && (bout.Phase != BoutPhase.Results || (bout.autoAdvance && bout.ResultsTimeLeft < 0.5f))) StopReplay();
            bool brain = BrainOn;
            if (brain) RefreshBrain(false);

            if (now >= _nextFast)
            {
                _nextFast = now + 0.1f;
                Unpop();
                RefreshClock();
                RefreshTape();
                RefreshPods();
                if (_stripPage == 2) RefreshStrip();
                if (ResultsOpen) RefreshNext();
                RefreshPredict();
                if (brain) RefreshBrain(true);
                if (_twitch != null) RefreshChat();
            }

            if (now >= _nextSlow)
            {
                _nextSlow = now + 0.25f;
                RefreshFrame();
                if (_stripPage != 2) RefreshStrip();
                if (DebugOpen) RefreshDebug();
                ReportPictureBand();
                if (MenuOpen && _settings != null && _settings.Armed) _settings.Refresh();
            }

            // The caption is there while it is news, and gone after: five seconds, then a second of fading. Never
            // under anything open over the picture.
            if (_ticker != null)
            {
                float opacity = ResultsOpen || MenuOpen || DebugOpen || string.IsNullOrEmpty(_ticker.text) ? 0f : Mathf.Clamp01(6f - (now - _tickerAt));
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

            if (results) { FillResults(); SettleExtras(); }
            if (_resReplay != null) _resReplay.EnableInClassList("hidden", MjReplay.Instance == null);
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
            // The pale chunk behind each bar follows the health too, but its stylesheet holds it back 0.6 s first.
            if (_ghost[0] != null) _ghost[0].style.width = Length.Percent(red.Health);
            if (_ghost[1] != null) _ghost[1].style.width = Length.Percent(blue.Health);
            if (_momFill != null) _momFill.style.width = Length.Percent(Excitement.RedShare * 100f);
        }

        void RefreshClock()
        {
            if (_round != null) _round.text = "R" + bout.Round;
            if (_clock != null) _clock.text = Bout.ClockText(bout.RoundTimeLeft);
            Fighter red = bout.red, blue = bout.blue;
            if (red == null || blue == null) return;
            SetPop(_redHpText, Mathf.CeilToInt(red.Health).ToString());
            SetPop(_blueHpText, Mathf.CeilToInt(blue.Health).ToString());
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
            SetPop(_landedRed, $"{red.stats.landed}/{red.stats.thrown}");
            SetPop(_landedBlue, $"{blue.stats.landed}/{blue.stats.thrown}");
            SetPop(_peakRed, $"{red.stats.peakImpulse:0} N·s");
            SetPop(_peakBlue, $"{blue.stats.peakImpulse:0} N·s");
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

        /// <summary>
        /// The tank; under it the muscles' reserve, for a boxer whose config has it tire (for any other it would
        /// always be full); the badge under that: down, getting up, dazed, the punch just thrown, or in its
        /// guard; and the two flags, ON THE ROPES and TIRED (the reserve under half, where the drives weaken).
        /// </summary>
        void RefreshStatus(int corner, Fighter f)
        {
            if (_tank[corner] != null)
            {
                _tank[corner].style.width = Length.Percent(f.Stamina * 100f);
                _tank[corner].EnableInClassList("tank-fill--low", f.Stamina < 0.35f);
            }
            Mj.MjBoxer body = f.boxer;
            bool tires = body != null && body.Bound && body.Cfg.fatigue_j > 0.0;
            float reserve = tires ? (float)body.Reserve : 1f;
            if (_reserveBar[corner] != null) _reserveBar[corner].EnableInClassList("hidden", !tires);
            if (_reserve[corner] != null)
            {
                _reserve[corner].style.width = Length.Percent(reserve * 100f);
                _reserve[corner].EnableInClassList("tank-fill--weak", reserve < 0.5f);
            }
            Flag(_tired[corner], tires && reserve < 0.5f);
            Flag(_ropes[corner], f.OnTheRopes);

            if (_act[corner] == null) return;
            bool punching = Time.time - _punchAt[corner] < PunchShown;
            bool alarm = f.IsDown || f.Staggered;
            Set(_act[corner], f.IsRising ? "RISING" : f.IsDown ? "DOWN" : f.Staggered ? "DAZED" : punching ? PunchNames[(int)_punch[corner]] : "GUARD");
            _act[corner].EnableInClassList("act--alarm", alarm);
            _act[corner].EnableInClassList("act--punch", punching && !alarm);
        }

        /// <summary>A flag shown or put away; one going up swells as it does.</summary>
        void Flag(Label flag, bool on)
        {
            if (flag == null || flag.ClassListContains("hidden") != on) return;
            flag.EnableInClassList("hidden", !on);
            if (on) Pop(flag);
        }

        /// <summary>Between rounds: the cards as they stand, and landed of thrown, punches stopped and the share of the centre in the round just ended.</summary>
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
            float centre = bout.judges.LastCentreShare;
            Set(_breakCentre[0], $"{centre * 100f:0}%");
            Set(_breakCentre[1], $"{(1f - centre) * 100f:0}%");
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

        /// <summary>Set, and if the number changed, swell it until the next tick.</summary>
        void SetPop(Label l, string text)
        {
            if (l == null || l.text == text) return;
            bool first = string.IsNullOrEmpty(l.text);
            l.text = text;
            if (!first) Pop(l);
        }

        void Pop(VisualElement e)
        {
            if (_popped.Contains(e)) return;
            e.AddToClassList("pop");
            _popped.Add(e);
        }

        void Unpop()
        {
            for (int i = 0; i < _popped.Count; i++) _popped[i].RemoveFromClassList("pop");
            _popped.Clear();
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
                bool show = age < FlashSeconds && cam != null && _safe != null && !ResultsOpen && !MenuOpen && !DebugOpen;
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
            // Each page's colours are a class in the stylesheet.
            _stripGraph.EnableInClassList("spark--excite", _stripPage == 0);
            _stripGraph.EnableInClassList("spark--share", _stripPage == 1);
            _stripGraph.EnableInClassList("spark--frame", _stripPage == 2);
            switch (_stripPage)
            {
                case 0:
                    Set(_stripTitle, "EXCITEMENT");
                    Set(_stripValue, $"{Excitement.Value * 100f:0}%");
                    _stripGraph.baseline = float.NaN;
                    _stripGraph.fillFromBaseline = false;
                    if (ex != null) _stripGraph.Set(_sampleExcitement ??= i => Excitement.Instance.Sample(Excitement.Instance.excitementHistory, i), ex.HistoryCount, 0f, 1f);
                    break;
                case 1:
                    // The cards, what each has left, and, with trained fighters, what their critics expect.
                    Set(_stripTitle, ex != null && ex.HasCritic ? "WIN CHANCE · AI" : "WIN CHANCE");
                    Set(_stripValue, $"{Excitement.RedShare * 100f:0}% {(bout != null && bout.red != null ? bout.red.displayName : "RED")}");
                    _stripGraph.baseline = 0.5f;
                    _stripGraph.fillFromBaseline = true;
                    if (ex != null) _stripGraph.Set(_sampleShare ??= i => Excitement.Instance.Sample(Excitement.Instance.shareHistory, i), ex.HistoryCount, 0f, 1f);
                    break;
                default:
                    Set(_stripTitle, "FRAME TIME");
                    Set(_stripValue, perf != null ? $"{perf.FrameMs:0.0} ms · worst {perf.WorstMs:0}" : "-");
                    _stripGraph.baseline = perf != null ? 1000f / Mathf.Max(15, perf.targetFps) : 16.7f;
                    _stripGraph.fillFromBaseline = false;
                    if (perf != null) _stripGraph.Set(perf.Sample, PerfTelemetry.HistoryLength, 0f, 50f);
                    break;
            }
        }

        void RefreshDebug()
        {
            PerfTelemetry perf = PerfTelemetry.Instance;
            if (perf == null || _debugValues.Length < DebugKeys.Length) return;
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
            _debugValues[16].text = $"{perf.InferMs:0.00} ms";
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
                _resWinner.EnableInClassList("res-winner--red", w != null && w == red);
                _resWinner.EnableInClassList("res-winner--blue", w != null && w == blue);
            }
            Set(_resMethod, bout.Method);
            if (_resElo != null)
            {
                // The ladder points the winner took (or what red moved by, on a draw).
                float delta = w == blue ? -bout.EloDelta : bout.EloDelta;
                _resElo.text = (delta >= 0f ? "+" : "") + delta.ToString("0") + " ELO";
            }

            // The punch count: landed of thrown and the share; the hardest punch and the fastest glove that landed.
            Set(_resRed[0], PunchStats.Landed(red));
            Set(_resBlue[0], PunchStats.Landed(blue));
            Set(_resRed[1], red.stats.blocked.ToString());
            Set(_resBlue[1], blue.stats.blocked.ToString());
            Set(_resRed[2], PunchStats.Power(red));
            Set(_resBlue[2], PunchStats.Power(blue));
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
                _resGraph.Set(i => ex.Sample(ex.shareHistory, i), ex.HistoryCount, 0f, 1f);
            }
            RefreshNext();
        }

        /// <summary>The button that starts the next bout, counting down to doing it by itself.</summary>
        void RefreshNext()
        {
            if (_resNext == null) return;
            // The same two again when the boxers were chosen; the ladder's next pair when they were not.
            string word = bout.fixedEntrants ? "AGAIN" : "NEXT";
            Set(_resNextWord, bout.autoAdvance ? $"{word} {Mathf.CeilToInt(bout.ResultsTimeLeft)}" : word);
        }

        static void Set2(Button b, string text)
        {
            if (b != null && b.text != text) b.text = text;
        }
    }
}
