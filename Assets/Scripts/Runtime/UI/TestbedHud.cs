using Mujoco;
using UnityEngine;
using UnityEngine.UIElements;
using PoBox.UI;

namespace PoBox.Mj
{
    /// <summary>
    /// The testbed's five anchors (Assets/UI/Testbed.uxml), the HUD's: title top left, frame rate top centre,
    /// the menu top right (the behaviour, reset, shoves and a cube), DEBUG bottom left (the readings), versions
    /// bottom right, all inside the same safe area. Everything it does to the boxer goes through
    /// <see cref="MjTestbed"/>, which writes MuJoCo's data. It lives with the interface rather than with the
    /// MuJoCo code because it shares the HUD's safe area and icons.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class TestbedHud : MonoBehaviour
    {
        public MjTestbed testbed;

        static readonly string[] Behaviours = { "STAND", "WALK", "TURN" };
        readonly SafeArea _safeArea = new SafeArea();
        VisualElement _root, _safe, _sheet, _debugPanel;
        Label _telemetry, _boxer;
        Button _behaviour, _shove, _fps;
        Glyph _menuGlyph;
        int _kind;
        float _smoothed, _nextText;

        void OnEnable()
        {
            // Hooked once a document: the same tree enabled again keeps the buttons it already has.
            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            if (root == null || root == _root) return;
            _root = root;
            _safe = root.Q<VisualElement>("safe");
            _sheet = root.Q<VisualElement>("menu-sheet");
            _debugPanel = root.Q<VisualElement>("debug-panel");
            _menuGlyph = root.Q<Glyph>("menu-glyph");
            _telemetry = root.Q<Label>("telemetry");
            _boxer = root.Q<Label>("boxer");
            _behaviour = root.Q<Button>("behaviour");
            _shove = root.Q<Button>("shove");
            _fps = root.Q<Button>("fps");
            root.Q<Label>("version").text = $"v{Application.version}  MuJoCo {MujocoLib.mj_version() / 1000000}.{MujocoLib.mj_version() / 1000 % 1000}.{MujocoLib.mj_version() % 1000}";

            root.Q<Button>("menu").clicked += () => Open(_sheet, !Showing(_sheet));
            root.Q<Button>("debug").clicked += () => Open(_debugPanel, !Showing(_debugPanel));
            _fps.clicked += () => Open(_debugPanel, !Showing(_debugPanel));
            // The one overlay rule: a tap on the picture puts away whatever is open over it.
            root.Q<VisualElement>("picture").RegisterCallback<PointerDownEvent>(_ => { Open(_sheet, false); Open(_debugPanel, false); });

            root.Q<Button>("reset").clicked += () => { if (testbed.Ready) { testbed.ResetBoxer(); testbed.SetBehaviour(_kind); } };
            root.Q<Button>("cube").clicked += () => testbed.ThrowCube();
            _shove.clicked += () => { testbed.shoves = !testbed.shoves; _shove.text = testbed.shoves ? "SHOVES ON" : "SHOVES OFF"; };
            _behaviour.clicked += () =>
            {
                _kind = (_kind + 1) % Behaviours.Length;
                _behaviour.text = Behaviours[_kind];
                if (testbed.Ready) testbed.SetBehaviour(_kind);
            };
        }

        static bool Showing(VisualElement e) => e != null && !e.ClassListContains("hidden");

        /// <summary>One at a time: opening the menu puts the readings away, and the other way round.</summary>
        void Open(VisualElement which, bool open)
        {
            if (which == null) return;
            if (open)
            {
                VisualElement other = which == _sheet ? _debugPanel : _sheet;
                if (other != null) other.AddToClassList("hidden");
            }
            which.EnableInClassList("hidden", !open);
            if (_menuGlyph != null) _menuGlyph.kind = Showing(_sheet) ? GlyphKind.Close : GlyphKind.Menu;
        }

        void Update()
        {
            _safeArea.Fit(_root, _safe);
            _smoothed = Mathf.Lerp(_smoothed, Time.unscaledDeltaTime, 0.05f);
            if (Time.unscaledTime < _nextText || _root == null) return;
            _nextText = Time.unscaledTime + 0.25f;       // four times a second: text made every frame is garbage every frame
            _fps.text = $"{1f / Mathf.Max(_smoothed, 1e-4f):0} FPS";
            if (testbed == null || !testbed.Ready) return;
            MjBoxer b = testbed.boxer;
            _boxer.text = b.Cfg.name.ToUpperInvariant() + (b.policy != null ? "  " + b.policy.name : "  no policy");
            if (Showing(_debugPanel))
                _telemetry.text = $"physics {testbed.PhysicsMs:0.00} ms   policy {testbed.PolicyMs:0.00} ms   t {testbed.Now:0.0} s\n" +
                                  $"pelvis {b.PelvisHeight:0.00} m   upright {b.Upright:0.00}   falls {testbed.Falls}   cubes {testbed.cubes.InPlay}";
        }
    }
}
