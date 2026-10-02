using Mujoco;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoBox.Mj
{
    /// <summary>
    /// The testbed's five anchors (Assets/UI/Testbed.uxml): title top left, frame rate and telemetry top
    /// centre, the behaviour selector top right, reset, shoves and a cube bottom left, versions bottom right.
    /// Everything it does to the boxer goes through <see cref="MjTestbed"/>, which writes MuJoCo's data.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class TestbedHud : MonoBehaviour
    {
        public MjTestbed testbed;

        static readonly string[] Behaviours = { "STAND", "WALK", "TURN" };
        Label _telemetry, _boxer;
        Button _behaviour, _shove;
        int _kind;
        float _smoothed, _nextText;

        void OnEnable()
        {
            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            _telemetry = root.Q<Label>("telemetry");
            _boxer = root.Q<Label>("boxer");
            _behaviour = root.Q<Button>("behaviour");
            _shove = root.Q<Button>("shove");
            root.Q<Label>("version").text = $"v{Application.version}  MuJoCo {MujocoLib.mj_version() / 1000000}.{MujocoLib.mj_version() / 1000 % 1000}.{MujocoLib.mj_version() % 1000}";
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

        void Update()
        {
            _smoothed = Mathf.Lerp(_smoothed, Time.unscaledDeltaTime, 0.05f);
            if (Time.unscaledTime < _nextText || testbed == null || !testbed.Ready) return;
            _nextText = Time.unscaledTime + 0.25f;       // four times a second: text made every frame is garbage every frame
            MjBoxer b = testbed.boxer;
            _boxer.text = b.Cfg.name.ToUpperInvariant() + (b.policy != null ? "  " + b.policy.name : "  no policy");
            _telemetry.text = $"{1f / Mathf.Max(_smoothed, 1e-4f):0} FPS   physics {testbed.PhysicsMs:0.00} ms   policy {testbed.PolicyMs:0.00} ms\n" +
                              $"t {testbed.Now:0.0} s   pelvis {b.PelvisHeight:0.00} m   upright {b.Upright:0.00}   falls {testbed.Falls}   cubes {testbed.cubes.InPlay}";
        }
    }
}
