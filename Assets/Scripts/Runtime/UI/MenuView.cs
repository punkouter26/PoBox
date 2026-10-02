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
    /// height, so more boxers means shorter cards, not a list.
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
        public Color redColor = new Color(0.89f, 0.24f, 0.24f);
        public Color blueColor = new Color(0.20f, 0.49f, 0.94f);

        public int Red { get; private set; }
        public int Blue { get; private set; }
        public VisualElement Root => _root;

        VisualElement _root, _safe, _redHost;
        Label _versus;
        Button[][] _cards = new Button[2][];
        Rect _lastSafe;
        Vector2Int _lastScreen;
        bool _scaleKnown;

        void OnEnable()
        {
            if (Application.isPlaying) Application.targetFrameRate = 60;
            Build();
        }

        /// <summary>
        /// Fills the document in: the cards, the version, the pairing. Run again whenever the document has
        /// been rebuilt underneath this, which in the editor is every time the layout or the stylesheet is saved.
        /// </summary>
        void Build()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            if (_root == null) return;
            _safe = _root.Q<VisualElement>("safe");
            _versus = _root.Q<Label>("versus");
            Label version = _root.Q<Label>("version");
            if (version != null) version.text = "v" + Application.version;

            _redHost = _root.Q<VisualElement>("red-cards");
            BuildCards(0, _redHost);
            BuildCards(1, _root.Q<VisualElement>("blue-cards"));
            Button fight = _root.Q<Button>("fight");
            if (fight != null)
            {
                fight.clicked -= Fight;
                fight.clicked += Fight;
                fight.SetEnabled(boxers.Length > 0);
            }

            // The pair fought last time; failing that the first boxer against the second.
            Red = Mathf.Max(0, IndexOf(MatchSelection.LastRed));
            int blue = IndexOf(MatchSelection.LastBlue);
            Blue = blue >= 0 ? blue : Mathf.Min(1, boxers.Length - 1);
            if (Blue == Red && boxers.Length > 1) Blue = (Red + 1) % boxers.Length;
            Refresh();
            ApplySafeArea();
        }

        void Update()
        {
            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            if (root == null) return;
            if (root != _root || (_redHost != null && _redHost.childCount == 0 && boxers.Length > 0)) Build();
            if (_root == null) return;
            if (!_scaleKnown || _lastSafe != Screen.safeArea || _lastScreen.x != Screen.width || _lastScreen.y != Screen.height) ApplySafeArea();
        }

        int IndexOf(string name)
        {
            for (int i = 0; i < boxers.Length; i++) if (boxers[i].name == name) return i;
            return -1;
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

        /// <summary>Corner 0 is red, 1 is blue.</summary>
        public void Pick(int corner, int index)
        {
            if (index < 0 || index >= boxers.Length) return;
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
            if (_versus != null && boxers.Length > 0)
                _versus.text = $"<color=#{ColorUtility.ToHtmlStringRGB(redColor)}>{boxers[Red].displayName}</color>  v  <color=#{ColorUtility.ToHtmlStringRGB(blueColor)}>{boxers[Blue].displayName}</color>";
        }

        public void Fight()
        {
            if (boxers.Length == 0 || !Application.isPlaying) return;
            MatchSelection.Choose(boxers[Red].name, boxers[Blue].name);
            SceneManager.LoadScene(MatchSelection.ArenaScene);
        }

        /// <summary>The safe element's edges moved in by the notch and the home bar, as the HUD does it.</summary>
        void ApplySafeArea()
        {
            _lastSafe = Screen.safeArea;
            _lastScreen = new Vector2Int(Screen.width, Screen.height);
            if (_safe == null || Screen.width <= 0 || Screen.height <= 0) return;

            Rect sa = Screen.safeArea;
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
