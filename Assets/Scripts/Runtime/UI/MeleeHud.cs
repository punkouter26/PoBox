using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using PoBox.Sim;

namespace PoBox.UI
{
    /// <summary>
    /// The free-for-all's HUD (Assets/UI/Melee.uxml): the round and its time, the frame rate, the settings and the way
    /// back along the top; every boxer in two rows of four (its colour, its name, and the count over it, SLIP, OUT or
    /// WINNER); the referee's calls (SimBus.Say) across the picture; the result with AGAIN and MENU when it is over.
    /// It only reads <see cref="FreeForAll"/>.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class MeleeHud : MonoBehaviour
    {
        public FreeForAll rules;
        [Tooltip("Seconds a call of the referee's stays up.")]
        public float callSeconds = 2.5f;

        UIDocument _doc;
        VisualElement _root, _safe, _result, _sheet;
        Label _round, _fps, _winner, _method, _call;
        VisualElement[] _rows = new VisualElement[0];
        Label[] _states = new Label[0];
        readonly SafeArea _safeArea = new SafeArea();
        SettingsPage _settings;
        bool _leaving;
        float _smoothed = 1f / 60f, _nextText, _callUntil;
        int _callPriority;

        public bool SettingsOpen => _sheet != null && !_sheet.ClassListContains("hidden");
        public VisualElement Root => _root;

        void OnEnable()
        {
            _doc = GetComponent<UIDocument>();
            _root = null;
            SimBus.Line += OnLine;
        }

        void OnDisable() => SimBus.Line -= OnLine;

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
            _round = root.Q<Label>("round");
            _fps = root.Q<Label>("fps");
            _call = root.Q<Label>("call");
            _result = root.Q<VisualElement>("result");
            _winner = root.Q<Label>("winner");
            _method = root.Q<Label>("method");
            _sheet = root.Q<VisualElement>("menu-sheet");
            root.Q<Button>("back").clicked += ToMenu;
            root.Q<Button>("leave").clicked += ToMenu;
            root.Q<Button>("again").clicked += () => rules.Restart();
            root.Q<Button>("menu").clicked += ToggleSettings;
            root.Q<VisualElement>("picture").RegisterCallback<PointerDownEvent>(_ => _sheet.AddToClassList("hidden"));
            _settings = new SettingsPage(root, null);

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

        public void ToggleSettings()
        {
            if (_sheet == null) return;
            bool open = !SettingsOpen;
            _sheet.EnableInClassList("hidden", !open);
            if (open) _settings?.Refresh();
        }

        void Update()
        {
            if (_doc == null || rules == null) return;
            Build();
            if (_root == null) return;
            _safeArea.Fit(_root, _safe);
            _call.EnableInClassList("hidden", Time.unscaledTime >= _callUntil);

            _smoothed = Mathf.Lerp(_smoothed, Time.unscaledDeltaTime, 0.05f);
            if (Time.unscaledTime < _nextText) return;
            _nextText = Time.unscaledTime + 0.25f;      // four times a second: text made every frame is garbage every frame
            Set(_fps, $"{1f / Mathf.Max(_smoothed, 1e-4f):0} FPS");
            if (SettingsOpen && _settings.Armed) _settings.Refresh();

            bool over = rules.Phase == FreeForAll.Stage.Over;
            Set(_round, over ? $"{rules.Left} LEFT" : rules.Phase == FreeForAll.Stage.Break ? $"END R{rules.Round}" : $"R{rules.Round} {Bout.ClockText(rules.RoundTimeLeft)}");
            for (int i = 0; i < _rows.Length; i++)
            {
                Fighter f = rules.fighters[i];
                if (f == null) continue;
                bool isOut = rules.IsOut(f), won = over && f == rules.Winner;
                int count = rules.CountOf(f);
                string text = won ? "WINNER" : isOut ? "OUT" : count >= 0 ? (count == 0 ? "DOWN" : $"COUNT {count}") : rules.Slipped(f) ? "SLIP" : "";
                Set(_states[i], text);
                _states[i].EnableInClassList("ffa-state--count", count >= 0 && !isOut && !won);
                _rows[i].EnableInClassList("ffa-row--out", isOut);
                _rows[i].EnableInClassList("ffa-row--win", won);
            }
            _result.EnableInClassList("hidden", !over);
            if (over)
            {
                Set(_winner, rules.Winner != null ? rules.Winner.displayName : "NO WINNER");
                Set(_method, rules.Method);
            }
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
