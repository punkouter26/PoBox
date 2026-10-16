using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using PoBox.Broadcast;
using PoBox.Diag;
using PoBox.Fx;
using PoBox.Mj;
using PoBox.Net;
using PoBox.Sim;

namespace PoBox.UI
{
    /// <summary>
    /// The free-for-all's HUD (Assets/UI/Melee.uxml), on the five anchors every screen has: the round and its time,
    /// the frame rate, the way back and the settings along the top, DEBUG and the version along the bottom; every boxer
    /// in two rows of four (its colour, its name, and the count over it, SLIP, OUT or WINNER); the referee's calls
    /// (SimBus.Say) across the picture; the result, the arena's card, with REPLAY, SHARE, AGAIN and MENU when it is
    /// over. The debug panel has the frame, the physics and the brains' costs; the hidden switch (three fingers, or a
    /// long press on the frame rate) opens it with the X-ray. It only reads <see cref="FreeForAll"/>.
    ///
    /// While they fight, the boxer whose own critic (MjBoxer.Value) expects the most is marked AI FAV: each critic
    /// judges its own boxer's prospects, so this is "who feels best about it", not a calibrated chance. The result
    /// turns the board into its table (each boxer's punches landed of thrown) and carries the fastest glove and the
    /// hardest punch, an instant REPLAY of the last knockdown (Mj/MjReplay.cs) and SHARE; with a
    /// Twitch channel set, chat's favourite is on a line under the board and chat can throw cubes.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class MeleeHud : MonoBehaviour
    {
        public FreeForAll rules;
        [Tooltip("Seconds a call of the referee's stays up.")]
        public float callSeconds = 2.5f;

        static readonly string[] DebugKeys = { "FRAME", "PHYSICS", "BRAINS", "MEMORY", "SCALE", "LEFT" };

        UIDocument _doc;
        VisualElement _root, _safe, _result, _sheet, _debugPanel, _debugDot;
        Label _round, _winner, _method, _call, _stats, _chat;
        Label[] _debugValues = new Label[0];
        Button _replay, _fps;
        TwitchChat _twitch;
        bool _wasOver, _replaying;
        VisualElement[] _rows = new VisualElement[0];
        Label[] _states = new Label[0];
        readonly SafeArea _safeArea = new SafeArea();
        SettingsPage _settings;
        bool _leaving;
        float _nextText, _callUntil;
        int _callPriority;

        public bool SettingsOpen => _sheet != null && !_sheet.ClassListContains("hidden");
        public bool DebugOpen => _debugPanel != null && !_debugPanel.ClassListContains("hidden");
        public bool ResultOpen => _result != null && !_result.ClassListContains("hidden");
        public SettingsPage Settings => _settings;
        public VisualElement Root => _root;

        void OnEnable()
        {
            _doc = GetComponent<UIDocument>();
            _root = null;
            SimBus.Line += OnLine;
            SimBus.Knockdown += OnKnockdown;
            _twitch ??= TwitchChat.FromPrefs();
        }

        void OnDisable()
        {
            SimBus.Line -= OnLine;
            SimBus.Knockdown -= OnKnockdown;
            _twitch?.Dispose();
            _twitch = null;
        }

        /// <summary>A punch put someone down: the replay keeps the seconds around it.</summary>
        void OnKnockdown(Fighter f, HitEvent cause)
        {
            if (cause.attacker != null && MjReplay.Instance != null) MjReplay.Instance.Mark();
        }

        void OnLine(string text, int priority)
        {
            // A call does not cut off a more important one still up.
            if (Time.unscaledTime < _callUntil && priority < _callPriority) return;
            _callPriority = priority;
            _callUntil = Time.unscaledTime + callSeconds;
            if (_call != null) _call.text = text;
        }

        void Build()
        {
            VisualElement root = _doc.rootVisualElement;
            if (root == null || root == _root) return;
            _root = root;
            _safe = root.Q<VisualElement>("safe");
            _round = root.Q<Label>("title");
            _fps = root.Q<Button>("fps");
            _call = root.Q<Label>("call");
            _result = root.Q<VisualElement>("result");
            _winner = root.Q<Label>("winner");
            _method = root.Q<Label>("method");
            _stats = root.Q<Label>("stats");
            _chat = root.Q<Label>("chat");
            _sheet = root.Q<VisualElement>("menu-sheet");
            _debugPanel = root.Q<VisualElement>("debug-panel");
            _debugDot = root.Q<VisualElement>("debug-dot");
            Label version = root.Q<Label>("version");
            if (version != null) version.text = "v" + Application.version;
            root.Q<Button>("back").clicked += ToMenu;
            root.Q<Button>("leave").clicked += ToMenu;
            root.Q<Button>("again").clicked += () => { StopReplay(); rules.Restart(); };
            _replay = root.Q<Button>("replay");
            if (_replay != null) _replay.clicked += PlayReplay;
            Button share = root.Q<Button>("share");
            if (share != null) share.clicked += () => StartCoroutine(Share.Shot(rules.Winner != null ? $"{rules.Winner.displayName} is the last one standing: {rules.Method} · PoBox" : $"{rules.Method} · PoBox"));
            root.Q<Button>("menu").clicked += ToggleSettings;
            Button debug = root.Q<Button>("debug");
            if (debug != null) debug.clicked += ToggleDebug;
            DebugPanel.Hook(root, _fps, ToggleDebug, () => { ToggleDebug(); PhysicsXray.On = DebugOpen; });
            // The one overlay rule: a tap on the picture puts away whatever is open over it.
            root.Q<VisualElement>("picture").RegisterCallback<PointerDownEvent>(_ => { Open(_sheet, false); Open(_debugPanel, false); });
            _debugValues = DebugPanel.Build(_debugPanel, DebugKeys);
            SettingsPage.Load();
            _settings = new SettingsPage(root, null, ToggleDebug);

            VisualElement board = root.Q<VisualElement>("board");
            board.Clear();
            int n = rules.fighters.Length;
            _rows = new VisualElement[n];
            _states = new Label[n];
            for (int i = 0; i < n; i++)
            {
                Fighter f = rules.fighters[i];
                var row = new VisualElement { pickingMode = PickingMode.Ignore };
                row.AddToClassList("ffa-row");
                row.style.borderTopColor = f != null ? f.cornerColor : Color.grey;
                var name = new Label(f != null ? f.displayName : "?") { pickingMode = PickingMode.Ignore };
                name.AddToClassList("ffa-name");
                var state = new Label("") { pickingMode = PickingMode.Ignore };
                state.AddToClassList("ffa-state");
                row.Add(name);
                row.Add(state);
                board.Add(row);
                _rows[i] = row;
                _states[i] = state;
            }
        }

        public void ToggleSettings() => Open(_sheet, !SettingsOpen);
        /// <summary>The settings, open on one of their tabs (0 SOUND, 1 DISPLAY, 2 DEBUG).</summary>
        public void OpenSettings(int tab) { Open(_sheet, true); _settings?.ShowTab(tab); }
        public void ToggleDebug() => Open(_debugPanel, !DebugOpen);

        /// <summary>One at a time, the settings and the debug panel, and the result put away while either is up: all three are docked in the same place.</summary>
        void Open(VisualElement which, bool open)
        {
            if (which == null) return;
            if (open)
            {
                VisualElement other = which == _sheet ? _debugPanel : _sheet;
                other?.AddToClassList("hidden");
            }
            which.EnableInClassList("hidden", !open);
            if (open && which == _sheet) _settings?.Refresh();
            if (open && which == _debugPanel) RefreshDebug();
            ShowResult();
        }

        void ShowResult()
        {
            _result?.EnableInClassList("hidden", rules == null || rules.Phase != FreeForAll.Stage.Over || MjReplay.Playing || SettingsOpen || DebugOpen);
        }

        void Update()
        {
            if (_doc == null || rules == null) return;
            Build();
            if (_root == null) return;
            _safeArea.Fit(_root, _safe);
            if (Input.GetKeyDown(KeyCode.Escape)) Back();
            _call.EnableInClassList("hidden", Time.unscaledTime >= _callUntil);

            if (Time.unscaledTime < _nextText) return;
            _nextText = Time.unscaledTime + 0.25f;      // four times a second: text made every frame is garbage every frame
            // The same reading, and the same warn/bad colours, as the arena's HUD.
            PerfTelemetry perf = PerfTelemetry.Instance;
            if (perf != null)
            {
                string fps = $"{perf.Fps:0} FPS";
                if (_fps != null && _fps.text != fps) _fps.text = fps;
                _fps?.EnableInClassList("frame-fps--warn", perf.Grade == 1);
                _fps?.EnableInClassList("frame-fps--bad", perf.Grade == 2);
                _debugDot?.EnableInClassList("dot--warn", perf.Grade == 1);
                _debugDot?.EnableInClassList("dot--bad", perf.Grade == 2);
            }
            if (SettingsOpen && _settings.Armed) _settings.Refresh();
            if (DebugOpen) RefreshDebug();

            bool over = rules.Phase == FreeForAll.Stage.Over;
            // Out of a result into the same bout again: nothing recorded is of it.
            if (_wasOver && !over && MjReplay.Instance != null) MjReplay.Instance.Clear();
            if (_replaying && !MjReplay.Playing) _replaying = false;
            if (!over) StopReplay();
            _wasOver = over;
            Fighter fav = over ? null : Favourite();
            Set(_round, over ? $"{rules.Left} LEFT" : rules.Phase == FreeForAll.Stage.Break ? $"END R{rules.Round}" : $"R{rules.Round} {Bout.ClockText(rules.RoundTimeLeft)}");
            for (int i = 0; i < _rows.Length; i++)
            {
                Fighter f = rules.fighters[i];
                if (f == null) continue;
                bool isOut = rules.IsOut(f), won = over && f == rules.Winner;
                int count = rules.CountOf(f);
                // At the end the board is the result's table: each row's punches landed of thrown, beside WIN or OUT.
                string text = over ? $"{(won ? "WIN · " : isOut ? "OUT · " : "")}{f.stats.landed}/{f.stats.thrown}"
                            : isOut ? "OUT" : count >= 0 ? (count == 0 ? "DOWN" : $"COUNT {count}") : rules.Slipped(f) ? "SLIP" : f == fav ? "AI FAV" : "";
                Set(_states[i], text);
                _rows[i].EnableInClassList("ffa-row--fav", f == fav);
                _states[i].EnableInClassList("ffa-state--count", count >= 0 && !isOut && !won);
                _rows[i].EnableInClassList("ffa-row--out", isOut);
                _rows[i].EnableInClassList("ffa-row--win", won);
            }
            ShowResult();
            if (over)
            {
                Set(_winner, rules.Winner != null ? rules.Winner.displayName + " WINS" : "NO WINNER");
                Set(_method, rules.Method);
                Set(_stats, Leaders());
                // Offered once the last fall has finished and the ring has stopped (FreeForAll's settle).
                _replay?.EnableInClassList("hidden", MjReplay.Instance == null || !MjReplay.Instance.HasClip || Bout.SimRunning);
            }
            if (_twitch != null) RefreshChat();
        }

        /// <summary>The frame, what the physics and the brains cost a frame (PerfTelemetry), memory, the render scale, boxers left.</summary>
        void RefreshDebug()
        {
            PerfTelemetry perf = PerfTelemetry.Instance;
            if (perf == null || _debugValues.Length < DebugKeys.Length) return;
            Set(_debugValues[0], $"{perf.FrameMs:0.0} ms");
            Set(_debugValues[1], $"{perf.PhysicsMs:0.00} ms");
            Set(_debugValues[2], $"{perf.InferMs:0.00} ms");
            Set(_debugValues[3], $"{perf.MemoryMb:0} MB");
            Set(_debugValues[4], $"{perf.RenderScale:0.00}");
            Set(_debugValues[5], rules != null ? $"{rules.Left} of {rules.fighters.Length}" : "-");
        }

        /// <summary>The boxer still standing whose critic expects the most; none without trained critics.</summary>
        Fighter Favourite()
        {
            if (rules.Phase != FreeForAll.Stage.Fight) return null;
            Fighter best = null;
            foreach (Fighter f in rules.fighters)
            {
                if (f == null || f.boxer == null || f.boxer.Running == null || f.IsDown || rules.IsOut(f)) continue;
                if (best == null || f.boxer.Value > best.boxer.Value) best = f;
            }
            return best;
        }

        /// <summary>The fastest glove and the hardest punch (punches landed are on each boxer's row of the board).</summary>
        string Leaders()
        {
            Fighter fast = null, hard = null;
            foreach (Fighter f in rules.fighters)
            {
                if (f == null) continue;
                if (fast == null || PunchStats.FastestKmh(f) > PunchStats.FastestKmh(fast)) fast = f;
                if (hard == null || f.stats.peakImpulse > hard.stats.peakImpulse) hard = f;
            }
            if (fast == null) return "";
            return $"FASTEST {fast.displayName} {PunchStats.FastestKmh(fast):0} km/h · HARDEST {hard.displayName} {hard.stats.peakImpulse:0} N·s";
        }

        void PlayReplay()
        {
            if (MjReplay.Instance == null || Bout.SimRunning || !MjReplay.Instance.Play()) return;
            _replaying = true;
            _result.AddToClassList("hidden");
            SimBus.Say("REPLAY", 3);
        }

        static void StopReplay()
        {
            if (MjReplay.Playing) MjReplay.Instance.Stop();
        }

        /// <summary>Chat's favourite (a "!name" vote), and a cube at somebody standing when chat asks.</summary>
        void RefreshChat()
        {
            _twitch.Poll();
            if (_chat == null) return;
            _chat.RemoveFromClassList("hidden");
            string leader = _twitch.Leader(out int votes);
            Set(_chat, !_twitch.Connected ? $"CHAT · #{_twitch.Channel} ···" : leader != null ? $"CHAT FAV · {leader.ToUpperInvariant()} {votes}" : "CHAT · VOTE !NAME");
            if (rules.Phase != FreeForAll.Stage.Fight || !_twitch.TakeCube()) return;
            Fighter at = null;
            int seen = 0;
            foreach (Fighter f in rules.fighters)
                if (f != null && !f.IsDown && !rules.IsOut(f) && Random.Range(0, ++seen) == 0) at = f;
            if (TwitchChat.ThrowCube(at)) SimBus.Say($"Chat throws a cube at {at.displayName}!", 1);
        }

        /// <summary>The phone's back button (Escape): puts away what is open, then the replay, then goes back to the menu.</summary>
        public void Back()
        {
            if (SettingsOpen || DebugOpen) { Open(_sheet, false); Open(_debugPanel, false); return; }
            if (MjReplay.Playing) { StopReplay(); return; }
            ToMenu();
        }

        void ToMenu()
        {
            if (_leaving || !MatchSelection.HasMenu) return;
            _leaving = true;
            SceneManager.LoadSceneAsync(MatchSelection.MenuScene);
        }

        static void Set(Label l, string text)
        {
            if (l != null && l.text != text) l.text = text;
        }
    }
}
