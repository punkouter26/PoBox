using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace PoBox
{
    /// <summary>
    /// The on-device HUD, identical in every scene: title top-left, frame rate
    /// top-centre, MENU top-right, debug bottom-left, version bottom-right.
    ///
    /// WHY IT IS NOT IN THE SCENES. The three shipping scenes are hand-authored
    /// and committed (CLAUDE.md), so adding five labels to each by hand is five
    /// chances for them to drift apart. This attaches itself after every scene
    /// load instead, which is what makes "consistent HUD" true by construction
    /// rather than by review.
    ///
    /// It is NOT a singleton and does NOT survive a scene change (project
    /// rule): one is created per scene load and dies with its scene.
    ///
    /// It borrows the scene's existing UIDocument rather than creating a panel,
    /// because the runtime panel here carries no theme stylesheet and a
    /// hand-rolled one renders no font at all -- see Systems_UiTheme.
    ///
    /// SAFE AREA. The player is built with renderOutsideSafeArea = true so the
    /// game draws under the camera cutout; every label here is inset by
    /// Screen.safeArea so no text is under it. Turning that off instead would
    /// letterbox the render on exactly the phones this is tested on.
    /// </summary>
    [DefaultExecutionOrder(500)]   // after the screens have built their own UI
    public sealed class Systems_DeviceHud : MonoBehaviour
    {
        private const string MENU_SCENE = "SCN_MENU";
        private const float FPS_SMOOTHING = 0.1f;

        private Label _title;
        private Label _fps;
        private Label _debug;
        private Label _version;
        private Button _menu;
        private VisualElement _topBar;
        private VisualElement _overlay;
        private float _smoothedFps;
        private float _nextDebugRefresh;
        private Rect _lastSafeArea;
        private Systems_PerfTelemetry _telemetry;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            Spawn();
            // RuntimeInitializeOnLoadMethod fires ONCE per application start, not
            // once per scene load. Without this the menu's HUD was the only one
            // ever made: on device it followed the contest scene in and kept
            // reporting "SCN_MENU" and "no fighters in this scene" over a running
            // contest. The subscription is static, the objects are not.
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => Spawn();

        private static void Spawn()
        {
            // A scene that already has one (additive load, or a re-entered scene)
            // must not get a second.
            if (FindFirstObjectByType<Systems_DeviceHud>() != null) { return; }
            var go = new GameObject("Systems_DeviceHud");
            go.AddComponent<Systems_DeviceHud>();
            // Where the frame goes, in every scene that has a HUD to draw it on.
            // Unlike the MuJoCo profile below it costs nothing and toggles
            // nothing, so there is no reason to withhold it from the menu.
            go.AddComponent<Systems_PerfTelemetry>();
            // Frame-cost attribution rides along in the scenes that have a
            // creature to attribute, and ONLY when it was asked for. It is a
            // measurement that switches the creature's brain and physics off in
            // turn, so it must not run in a session nobody opted into.
            if (SceneManager.GetActiveScene().name != MENU_SCENE && Systems_MuJoCoProfile.Enabled)
            {
                go.AddComponent<Systems_MuJoCoProfile>();
            }
            // No DontDestroyOnLoad: it belongs to the scene that just loaded and
            // the next load gets its own.
        }

        private void Start()
        {
            // Android defaults targetFrameRate to 30. With frame pacing on and a
            // 120 Hz panel that is a choice, not a limit, and at a 0.02 s physics
            // step it reads as the fighters stepping in slow motion.
            if (Application.targetFrameRate != 60) { Application.targetFrameRate = 60; }

            _telemetry = GetComponent<Systems_PerfTelemetry>();

            UIDocument host = FindHost();
            if (host == null)
            {
                // A scene with no UIDocument has no panel to draw on. That is not
                // an error worth throwing -- it is a training scene, which is
                // headless by rule.
                enabled = false;
                return;
            }

            VisualElement root = host.rootVisualElement;
            if (root == null) { enabled = false; return; }

            _overlay = new VisualElement { name = "device-hud" };
            _overlay.pickingMode = PickingMode.Ignore;   // never eat game input
            _overlay.style.position = Position.Absolute;
            _overlay.style.left = 0;
            _overlay.style.right = 0;
            _overlay.style.top = 0;
            _overlay.style.bottom = 0;
            Systems_UiTheme.ApplyDefaultFont(_overlay);

            // TOP BAR. Without it the title and frame rate landed on top of the
            // menu's own PO BOX logo -- both legible on their own, unreadable
            // together. A bar is the only version of this that cannot collide
            // with whatever a screen happens to draw at the top.
            _topBar = new VisualElement();
            _topBar.pickingMode = PickingMode.Ignore;
            _topBar.style.position = Position.Absolute;
            _topBar.style.left = 0;
            _topBar.style.right = 0;
            _topBar.style.top = 0;
            _topBar.style.backgroundColor = new Color(0f, 0f, 0f, 0.78f);
            _overlay.Add(_topBar);

            _title = Corner(Align.FlexStart, Justify.FlexStart, 26f, 0.92f);
            // SCN_TEST_BALANCE_CONTEST ran straight into the centred frame rate.
            // Drop the prefix every scene shares and cap the width at a third of
            // the screen, which is the only version that cannot collide.
            _title.text = $"{Application.productName} · {ShortSceneName()}";
            _title.style.maxWidth = Length.Percent(40f);
            _title.style.whiteSpace = WhiteSpace.NoWrap;

            _fps = Corner(Align.Center, Justify.FlexStart, 26f, 0.85f);
            _version = Corner(Align.FlexEnd, Justify.FlexEnd, 30f, 0.55f);
            // A ternary cannot sit bare inside an interpolation -- the ':' ends
            // the hole -- so the build tag is resolved first.
            string guid = Application.buildGUID;
            string build = string.IsNullOrEmpty(guid) ? "dev" : guid.Substring(0, Mathf.Min(6, guid.Length));
            _version.text = $"v{Application.version} ({build})";

            _debug = Corner(Align.FlexStart, Justify.FlexEnd, 26f, 0.80f);
            _debug.style.whiteSpace = WhiteSpace.Normal;
            _debug.style.maxWidth = Length.Percent(62f);   // portrait: never span the bar

            _menu = new Button(GoToMenu) { text = "MENU" };
            _menu.pickingMode = PickingMode.Position;      // the one pickable thing
            _menu.style.position = Position.Absolute;
            _menu.style.fontSize = 24f;
            _menu.style.paddingLeft = 22f;
            _menu.style.paddingRight = 22f;
            _menu.style.paddingTop = 6f;
            _menu.style.paddingBottom = 6f;
            _menu.style.color = Systems_UiTheme.GoldBright;
            _menu.style.backgroundColor = Systems_UiTheme.PanelDark;
            _overlay.Add(_menu);

            // In the menu itself the button would reload the scene you are on,
            // and the contest scenes draw their own "< MENU" -- two buttons doing
            // the same thing, side by side, is worse than either alone.
            _pendingSelf = _menu;
            bool redundant = SceneManager.GetActiveScene().name == MENU_SCENE
                             || SceneAlreadyHasMenuButton(root);
            _pendingSelf = null;
            _menu.style.display = redundant ? DisplayStyle.None : DisplayStyle.Flex;

            root.Add(_overlay);
            _overlay.BringToFront();
            ApplySafeArea();
            RefreshDebug();
        }

        /// <summary>One anchored label. Absolute positioning is set in ApplySafeArea.</summary>
        private Label Corner(Align horizontal, Justify vertical, float fontSize, float alpha)
        {
            var label = new Label(string.Empty);
            label.pickingMode = PickingMode.Ignore;
            label.style.position = Position.Absolute;
            label.style.fontSize = fontSize;
            label.style.color = new Color(1f, 1f, 1f, alpha);
            label.userData = new Vector2((int)horizontal, (int)vertical);
            _overlay.Add(label);
            return label;
        }

        /// <summary>
        /// "SCN_TEST_BALANCE_CONTEST" -> "BALANCE". Truncating the raw name with
        /// overflow:hidden instead produced "PoBox · T", which is worse than no
        /// title: the mandate is readability, and a clipped word is not readable.
        /// </summary>
        private static string ShortSceneName()
        {
            string scene = SceneManager.GetActiveScene().name;
            foreach (string noise in new[] { "SCN_", "TEST_", "_CONTEST" })
            {
                scene = scene.Replace(noise, string.Empty);
            }
            return string.IsNullOrEmpty(scene) ? "SCENE" : scene;
        }

        /// <summary>True if the screen already draws its own way back to the menu.</summary>
        private static bool SceneAlreadyHasMenuButton(VisualElement root)
        {
            foreach (UIDocument doc in FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
            {
                VisualElement panel = doc != null ? doc.rootVisualElement : null;
                if (panel == null) { continue; }
                foreach (TextElement text in panel.Query<TextElement>().ToList())
                {
                    // Buttons AND labels: the contest draws its "< MENU" in a
                    // document this HUD did not attach to, so searching only the
                    // host root left two menu buttons side by side on device.
                    if (text == null || string.IsNullOrEmpty(text.text)) { continue; }
                    if (text == _pendingSelf) { continue; }
                    if (text.text.ToUpperInvariant().Contains("MENU")) { return true; }
                }
            }
            return false;
        }

        /// <summary>Set while probing so the HUD does not detect its own button.</summary>
        private static TextElement _pendingSelf;

        private static UIDocument FindHost()
        {
            UIDocument best = null;
            foreach (UIDocument doc in FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
            {
                if (doc == null || doc.rootVisualElement == null) { continue; }
                // Highest sorting order wins, so the HUD lands on the topmost
                // panel rather than under the scoreboard.
                if (best == null || doc.sortingOrder > best.sortingOrder) { best = doc; }
            }
            return best;
        }

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            if (dt > 0f)
            {
                float instant = 1f / dt;
                _smoothedFps = _smoothedFps <= 0f
                    ? instant
                    : Mathf.Lerp(_smoothedFps, instant, FPS_SMOOTHING);
                if (_fps != null)
                {
                    // Frame TIME beside the rate, and nothing else up here. The
                    // rest of the telemetry goes in the debug block below: the
                    // title already claims 40% of a portrait width and the MENU
                    // button claims the right corner, so a centred line long
                    // enough to carry draw calls would run into both.
                    _fps.text = _telemetry != null
                        ? $"{_smoothedFps:0} FPS · {_telemetry.FrameMillis:0.0}ms"
                        : $"{_smoothedFps:0} FPS";
                }
            }

            if (Screen.safeArea != _lastSafeArea) { ApplySafeArea(); }

            if (Time.unscaledTime >= _nextDebugRefresh)
            {
                _nextDebugRefresh = Time.unscaledTime + 0.5f;
                RefreshDebug();
            }
        }

        /// <summary>
        /// Inset every corner out of the cutout. Screen.safeArea is in pixels
        /// with y up; UI Toolkit is in panel points with y down, so the panel
        /// scale converts and top/bottom swap.
        /// </summary>
        private void ApplySafeArea()
        {
            _lastSafeArea = Screen.safeArea;
            float scale = _overlay.panel != null && Screen.width > 0
                ? _overlay.resolvedStyle.width / Screen.width
                : 1f;
            if (scale <= 0f || float.IsNaN(scale)) { scale = 1f; }

            float left = _lastSafeArea.xMin * scale + 18f;
            float right = (Screen.width - _lastSafeArea.xMax) * scale + 18f;
            float top = (Screen.height - _lastSafeArea.yMax) * scale + 18f;
            float bottom = _lastSafeArea.yMin * scale + 18f;

            Place(_title, left, right, top, bottom);
            Place(_fps, left, right, top, bottom);
            Place(_debug, left, right, top, bottom + 132f);  // clear of the scoreboard chips
            Place(_version, left, right, top, bottom);

            if (_menu != null)
            {
                _menu.style.right = right;
                _menu.style.top = top;
            }
            if (_topBar != null)
            {
                _topBar.style.height = top + 46f;
            }
        }

        private static void Place(Label label, float left, float right, float top, float bottom)
        {
            if (label == null) { return; }
            var anchor = (Vector2)label.userData;
            var horizontal = (Align)(int)anchor.x;
            var vertical = (Justify)(int)anchor.y;

            if (horizontal == Align.FlexStart) { label.style.left = left; }
            else if (horizontal == Align.FlexEnd) { label.style.right = right; }
            else
            {
                // Centred: span the safe width and centre the text inside it,
                // which survives a notch that is not symmetrical.
                label.style.left = left;
                label.style.right = right;
                label.style.unityTextAlign = TextAnchor.UpperCenter;
            }

            if (vertical == Justify.FlexStart) { label.style.top = top; }
            else { label.style.bottom = bottom; }
        }

        /// <summary>
        /// Plain English, worst cause first. The point of this panel is that
        /// someone holding the phone can say what is wrong without a cable:
        /// "3 fighters have no brain" is actionable, "nullref in Update" is not.
        /// </summary>
        private void RefreshDebug()
        {
            if (_debug == null) { return; }
            var lines = new List<string>();

            int down = 0;
            int rigsFound = 0;
            IContestFighter[] fighters = Systems_Contestants.FindAll();
            for (int fighterIndex = 0; fighterIndex < fighters.Length; fighterIndex++)
            {
                rigsFound++;
                if (fighters[fighterIndex].ReportsDown) { down++; }
            }

            // Everything that took the mat, read from the spawner when it fielded
            // a line-up and counted here otherwise.
            int total = Systems_ContestSpawner.LastFieldedCount > 0
                ? Systems_ContestSpawner.LastFieldedCount
                : rigsFound;
            if (total == 0) { lines.Add("no fighters in this scene"); }
            else if (down > 0) { lines.Add($"{down} of {total} fighters are down"); }
            else { lines.Add($"{total} fighters up"); }

            lines.Add($"{Screen.width}x{Screen.height} · {SystemInfo.graphicsDeviceType} · fixed {Time.fixedDeltaTime * 1000f:0}ms");
            if (_telemetry != null)
            {
                // Where the frame went. The share is the number the open 20 fps
                // item turns on: a humanoid's fixed step should be a few percent
                // of the frame, and this says when it is two thirds of it.
                lines.Add(_telemetry.Summarise());
            }
            if (Time.timeScale != 1f) { lines.Insert(0, $"TIME SCALE IS {Time.timeScale:0.00} — physics is not running at speed"); }

            if (!string.IsNullOrEmpty(Systems_MuJoCoProfile.Summary))
            {
                lines.Insert(0, Systems_MuJoCoProfile.Summary);
            }

            var text = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0) { text.Append('\n'); }
                text.Append(lines[i]);
            }
            _debug.text = text.ToString();
        }

        private static void GoToMenu()
        {
            if (SceneManager.GetActiveScene().name == MENU_SCENE) { return; }
            // THE ESCAPE HATCH HAS TO RESET THE CLOCK, and it is the one route
            // out of a scene that does not go through the referee. Pressed during
            // a round countdown — when a freeze is being held — loading the menu
            // without this put the player on a menu with physics stopped and no
            // countdown left alive to release it. The contest's own "‹ MENU"
            // button always did this; this button did not, which is exactly the
            // asymmetry a shared escape hatch is supposed to remove.
            Systems_GameClock.RestoreAll("device HUD MENU");
            SceneManager.LoadScene(0);   // Build_Android.SHIP_SCENES puts SCN_MENU at 0
        }
    }
}
